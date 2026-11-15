using System.Runtime.InteropServices;
using GlmSharp;

namespace VoxelGame;

/// <summary>Terrain-only incremental lighting, with per-frame-slot dirty GPU uploads.</summary>
internal sealed class LightingScene : IDisposable
{
    private readonly Dictionary<(int Body,ivec3 Chunk),LightChunk> _objectChunks = [];
    private readonly Dictionary<int,(ivec3 Position,ivec4 Rotation)> _objectPoses = [];
    private readonly HashSet<int> _liveObjects = [];
    private bool _objectsDirty;
    private readonly Dictionary<ivec3,LightChunk> _snapshots = [];
    private readonly Dictionary<ivec3,LightSurface> _colors = [];
    private readonly vec3[] _reflectance = DefaultReflectance();
    private static vec3[] DefaultReflectance()
    {
        vec3[] result = Enumerable.Repeat(new vec3(.5f),256).ToArray(); result[0] = result[1] = vec3.Zero; return result;
    }
    private readonly LightVolumeCache _cache = new();
    private readonly Dictionary<ivec3,(LightChunk Chunk,LightSurface Surface)> _pendingChanges = [];
    private RealtimeLightField _field = new() { GpuSunShadows = true };
    private int _stateVersion, _columnVersion = -1;
    public int Version => _stateVersion + _cache.Version;
    public bool Ready => _cache.Ready;
    public bool Pending => _pendingChanges.Count != 0 || _field.Pending;
    public int Bytes => _cache.Bytes;
    public double StepMilliseconds => _field.Milliseconds;
    public int AirCells => _field.CellCount;
    public vec3 SampleRgb(ivec3 cell) => _field.Sample(cell);
    internal static uint Mask(Voxel voxel,bool transparentLeaves)
    {
        // Cutout doors transmit light; the half-cell transport grid cannot represent their thin frame.
        // The sun shadow pass still tests their actual geometry and texture alpha.
        if (voxel.Type == BlockType.Air || BlockShapes.IsDoor(voxel.Type) || voxel.Material.Transparent || (voxel.Type == BlockType.Leaves && transparentLeaves)) return 0;
        if (!BlockShapes.HasShape(voxel.Type)) return 255;
        uint result = 0;
        foreach (ivec3 part in BlockShapes.Parts(voxel)) result |= 1u << (part.x+2*part.y+4*part.z);
        return result;
    }
    internal static vec3 AverageReflectance(ReadOnlySpan<byte> pixels)
    {
        vec3 sum = vec3.Zero; float alpha = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            float Linear(byte b) { float c = b/255f; return c <= .04045f ? c/12.92f : MathF.Pow((c+.055f)/1.055f,2.4f); }
            sum += new vec3(Linear(pixels[i]),Linear(pixels[i+1]),Linear(pixels[i+2]));
            alpha += pixels[i+3]/255f;
        }
        int count = pixels.Length/4;
        return count == 0 ? vec3.Zero : sum/count*(alpha/count);
    }
    public void SetReflectance(BlockType type,int variant,vec3 value) => _reflectance[(int)type*2+variant] = value;
    public void SetFocus(dvec3 eye) => _field.SetFocus((ivec3)dvec3.Floor(eye));
    public void Configure(vec3 sky,bool blocks,bool sun) => _field.Configure(sky,blocks,sun);
    public void Invalidate(VoxelBody body,ivec3 key,bool transparentLeaves)
    {
        if (body is not VoxelMap)
        {
            if (!body.Grid.TryGetChunk(key,out var objectChunk))
            { _objectsDirty |= _objectChunks.Remove((body.Id,key)); return; }
            byte[] occupied = new byte[4096]; List<ivec3> sources = [];
            for (int i = 0; i < 4096; i++)
            {
                Voxel voxel = objectChunk.Cells[i]; occupied[i] = (byte)Mask(voxel,transparentLeaves);
                if (voxel.Type == BlockType.Furnace && voxel.Lit) sources.Add(key*16+new ivec3(i&15,(i>>4)&15,i>>8));
            }
            if (_objectChunks.TryGetValue((body.Id,key),out LightChunk? old) && old.Masks.AsSpan().SequenceEqual(occupied)
                && old.Emitters.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(sources))) return;
            _objectChunks[(body.Id,key)] = new(key,occupied,[.. sources]); _objectsDirty = true;
            return;
        }
        if (!body.Grid.TryGetChunk(key,out var chunk))
        {
            _pendingChanges.Remove(key);
            if (_snapshots.Remove(key)) { _colors.Remove(key); _field.Remove(key); _stateVersion++; }
            return;
        }
        byte[] masks = new byte[4096], materials = new byte[4096];
        List<ivec3> emitters = [];
        for (int i = 0; i < 4096; i++)
        {
            Voxel voxel = chunk.Cells[i]; masks[i] = (byte)Mask(voxel,transparentLeaves);
            int variant = voxel.Type == BlockType.Leaves && transparentLeaves ? 1 : 0;
            materials[i] = checked((byte)((int)voxel.Type*2+variant));
            if (voxel.Type == BlockType.Furnace && voxel.Lit) emitters.Add(key*16+new ivec3(i&15,(i>>4)&15,i>>8));
        }
        LightSurface surface = new(_reflectance,materials);
        if (_snapshots.TryGetValue(key,out LightChunk? previous) && previous.Masks.AsSpan().SequenceEqual(masks)
            && previous.Emitters.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(emitters)) && _colors[key].Same(surface)) return;
        if (previous == null ? emitters.Count != 0 : !previous.Emitters.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(emitters))) _objectsDirty = true;
        LightChunk snapshot = new(key,masks,[.. emitters]);
        _snapshots[key] = snapshot; _colors[key] = surface;
        _pendingChanges[key] = (snapshot,surface);
        _stateVersion++;
    }
    public void Build(IEnumerable<VoxelBody> bodies,Func<VoxelBody,(dvec3 Position,dquat Orientation)> poseOf,bool force = false)
    {
        foreach (VoxelBody body in bodies)
        {
            if (body is not VoxelMap map || map.ColumnVersion == _columnVersion) continue;
            _columnVersion = map.ColumnVersion;
            foreach (ivec3 key in _snapshots.Keys.Where(k => !map.Grid.TryGetChunk(k,out _)).ToArray())
            {
                _snapshots.Remove(key); _colors.Remove(key); _pendingChanges.Remove(key); _field.Remove(key); _stateVersion++;
            }
        }
        if (_pendingChanges.Count != 0)
        {
            _field.PrepareBatch(_pendingChanges.Values.Select(p => p.Chunk));
            foreach (var change in _pendingChanges.Values) _field.Set(change.Chunk,change.Surface);
            _field.EndBatch(); _pendingChanges.Clear();
        }
        _liveObjects.Clear();
        foreach (VoxelBody body in bodies)
        {
            if (body is VoxelMap) continue;
            _liveObjects.Add(body.Id);
            var pose = poseOf(body);
            var state = (new ivec3((int)Math.Round(pose.Position.x*4),(int)Math.Round(pose.Position.y*4),(int)Math.Round(pose.Position.z*4)),
                new ivec4((int)Math.Round(pose.Orientation.x*32),(int)Math.Round(pose.Orientation.y*32),(int)Math.Round(pose.Orientation.z*32),(int)Math.Round(pose.Orientation.w*32)));
            if (!_objectPoses.TryGetValue(body.Id,out var old) || state != old) { _objectPoses[body.Id] = state; _objectsDirty = true; }
        }
        foreach (int id in _objectPoses.Keys.Where(id => !_liveObjects.Contains(id)).ToArray()) { _objectPoses.Remove(id); _objectsDirty = true; }
        foreach (var key in _objectChunks.Keys.Where(k => !_liveObjects.Contains(k.Body)).ToArray()) _objectChunks.Remove(key);
        if (_objectsDirty)
        {
            List<LightObject> objects = [];
            foreach (VoxelBody body in bodies)
            {
                if (body is VoxelMap) continue;
                var pose = poseOf(body);
                LightChunk[] chunks = [.. body.Grid.Chunks.Select(c => _objectChunks.GetValueOrDefault((body.Id,c.Coordinate))).OfType<LightChunk>()];
                objects.Add(new(chunks,pose.Position,pose.Orientation));
            }
            _field.SetShadowObjects([.. objects]); _objectsDirty = false;
        }
        _field.Step();
        _cache.Update(_field);
    }
    // Offline captures explicitly drain all waves; normal rendering only takes one budgeted step.
    public void Wait()
    {
        while (_field.Pending) _field.Step(double.PositiveInfinity,65536);
        _cache.Update(_field);
    }
    public void InvalidateUpload(int frame) => _cache.InvalidateUpload(frame);
    public void Write(Span<byte> destination,int frame = 0) => _cache.Write(destination,frame);
    public void Clear(bool keepVolume = false)
    {
        _objectChunks.Clear(); _objectPoses.Clear(); _liveObjects.Clear(); _objectsDirty = false;
        _snapshots.Clear(); _colors.Clear(); _pendingChanges.Clear(); _cache.Clear();
        _field = new() { GpuSunShadows = true }; _columnVersion = -1; _stateVersion++;

    }
    public void Dispose() => Clear();
}
