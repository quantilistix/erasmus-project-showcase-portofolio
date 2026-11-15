using System.Diagnostics;
using GlmSharp;

namespace VoxelGame;

/// <summary>Small texture-color palette; uniform terrain chunks need only one RGB value.</summary>
internal sealed class LightSurface
{
    private readonly vec3[] _palette;
    private readonly byte[] _indices;
    public LightSurface(vec3[] values)
    {
        Dictionary<vec3,byte> palette = [];
        byte[] indices = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!palette.TryGetValue(values[i],out byte index))
            {
                if (palette.Count == 256) { _palette = values; _indices = []; return; }
                index = (byte)palette.Count; palette.Add(values[i],index);
            }
            indices[i] = index;
        }
        _palette = [.. palette.Keys]; _indices = palette.Count == 1 ? [] : indices;
    }
    public LightSurface(vec3[] palette,byte[] indices)
    {
        if (indices.AsSpan().IndexOfAnyExcept(indices[0]) < 0) { _palette = [palette[indices[0]]]; _indices = []; }
        else { _palette = palette; _indices = indices; }
    }
    public vec3 this[int index] => _palette[_indices.Length == 0 ? (_palette.Length == 1 ? 0 : index) : _indices[index]];
    public bool Same(LightSurface other) => (ReferenceEquals(_palette,other._palette) || _palette.AsSpan().SequenceEqual(other._palette)) && _indices.AsSpan().SequenceEqual(other._indices);
}

/// <summary>Persistent RGB transport. A frame visits a bounded part of the frontier, including removals.</summary>
internal sealed class RealtimeLightField
{
    internal sealed class Chunk(ivec3 key)
    {
        public readonly ivec3 Key = key;
        private vec3[]? _light;
        private byte[]? _sun, _sky;
        public vec3 UniformLight;
        public byte UniformSun, UniformSky;
        public vec3 Light(int i) => _light == null ? UniformLight : _light[i];
        public byte Sun(int i) => _sun == null ? UniformSun : _sun[i];
        public byte Sky(int i) => _sky == null ? UniformSky : _sky[i];
        public void Store(int i,vec3 light,byte sun,byte sky = 0)
        {
            if (_light == null && light != UniformLight) { _light = new vec3[4096]; Array.Fill(_light,UniformLight); }
            if (_sun == null && sun != UniformSun) { _sun = new byte[4096]; Array.Fill(_sun,UniformSun); }
            if (_sky == null && sky != UniformSky) { _sky = new byte[4096]; Array.Fill(_sky,UniformSky); }
            if (_sky != null) _sky[i] = sky;
            if (_light != null) _light[i] = light;
            if (_sun != null) _sun[i] = sun;
        }
        public readonly bool[] Queued = new bool[4096];
        public readonly bool[] Urgent = new bool[4096];
        public int Revision;
        public int Scan = -1;
        public int[]? Seeds;
        public bool Rescan;
    }
    internal sealed class PointShadow(ivec3 cell)
    {
        public const int Size = 16;
        public readonly ivec3 Cell = cell;
        public float[] Depth = new float[6*Size*Size];
        public float[] WorkingDepth = new float[6*Size*Size];
        public int Next, Revision;
        public bool Rescan;
    }
    private readonly Dictionary<ivec3,PointShadow> _pointShadows = [];
    private PointShadow[] _pointOrder = [];
    private int _pointCursor;
    public IEnumerable<PointShadow> PointLights => _pointShadows.Values;
    private readonly Dictionary<ivec3,LightChunk?> _preparedOld = [];
    private bool _batch;
    private readonly Dictionary<ivec3, LightChunk> _geometry = [];
    private readonly Dictionary<ivec3, LightSurface> _colors = [];
    private readonly Dictionary<ivec3, Chunk> _chunks = [];
    private readonly HashSet<Chunk> _dirty = [];
    public IEnumerable<Chunk> DirtyChunks => _dirty;
    public void ClearDirty() => _dirty.Clear();
    private readonly Queue<ivec3> _frontier = [], _urgent = [];
    private readonly PriorityQueue<Chunk,long> _initial = new();
    private ivec3 _focus;
    private ivec3? _batchLow, _batchHigh;
    private readonly Dictionary<ivec2,int[]> _roof = [];
    private readonly HashSet<ivec2> _dirtyRoofs = [];
    public IReadOnlyDictionary<ivec2,int[]> RoofColumns => _roof;
    public IEnumerable<ivec2> DirtyRoofs => _dirtyRoofs;
    public int RoofTopologyVersion { get; private set; }
    public void ClearDirtyRoofs() => _dirtyRoofs.Clear();
    // Orthographic depth map perpendicular to the sun, with half-cell texels.
    // Each texel caches the first terrain intersection; fragments never trace rays.
    private readonly Dictionary<ivec2, float> _shadow = [];
    private Dictionary<ivec3,byte> _objectMasks = [];
    private HashSet<ivec3> _objectEmitters = [];
    private int _objectTop;
    private ivec3 _objectLow, _objectHigh;
    private readonly HashSet<ivec3> _emitters = [];
    private static readonly (ivec3 Offset, float Attenuation)[] Directions = MakeDirections();
    private static readonly vec3 SunColor = new(1, .96f, .88f);
    private ivec3 _low, _high;
    private bool _bounds;
    private int _revision;
    public vec3 SkyColor { get; private set; } = new(.55f * .185f, .72f * .185f, .95f * .185f);
    public bool GpuSunShadows { get; init; }
    public bool BlockLighting { get; private set; } = true;
    public bool SunLighting { get; private set; } = true;
    public IEnumerable<Chunk> Chunks => _chunks.Values;
    public bool Pending => _initial.Count != 0 || _urgent.Count != 0 || _frontier.Count != 0 || (BlockLighting && _pointShadows.Values.Any(p => p.Next < p.Depth.Length));
    public double Milliseconds { get; private set; }
    public int CellCount => _chunks.Count * 4096;
    public int TopologyVersion { get; private set; }

    private static (ivec3, float)[] MakeDirections()
    {
        List<(ivec3, float)> result = [];
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0 || z != 0)
                result.Add((new(x,y,z), MathF.Pow(.85f, MathF.Sqrt(x*x+y*y+z*z))));
        return [.. result];
    }

    public void Configure(vec3 sky, bool blocks, bool sun)
    {
        if (SkyColor == sky && BlockLighting == blocks && SunLighting == sun) return;
        SkyColor = sky; BlockLighting = blocks; SunLighting = sun;
        foreach (Chunk chunk in _chunks.Values) Schedule(chunk);
    }

    public void PrepareBatch(IEnumerable<LightChunk> chunks)
    {
        _batch = true; _preparedOld.Clear(); _batchLow = _batchHigh = null;
        foreach (LightChunk chunk in chunks)
        {
            _preparedOld[chunk.Coordinate] = _geometry.GetValueOrDefault(chunk.Coordinate);
            _geometry[chunk.Coordinate] = chunk;
            if (!_bounds) { _low = chunk.Coordinate; _high = chunk.Coordinate; _bounds = true; }
            else { _low = ivec3.Min(_low,chunk.Coordinate); _high = ivec3.Max(_high,chunk.Coordinate); }
        }
    }
    public void EndBatch()
    {
        _batch = false; _preparedOld.Clear();
        if (_batchLow is ivec3 low && _batchHigh is ivec3 high) ScheduleShadowRegion(low,high);
    }
    public void SetFocus(ivec3 cell)
    {
        ivec3 focus = LightGrid.ChunkOf(cell);
        if (_focus == focus) return;
        _focus = focus;
        Chunk[] chunks = [.. _initial.UnorderedItems.Select(p => p.Element)];
        _initial.Clear(); foreach (Chunk chunk in chunks) EnqueueInitial(chunk);
    }
    private void EnqueueInitial(Chunk chunk)
    {
        ivec3 d = chunk.Key-_focus;
        _initial.Enqueue(chunk,(long)d.x*d.x+(long)d.y*d.y+(long)d.z*d.z);
    }
    public void Set(LightChunk geometry, vec3[] reflectance) => Set(geometry,new LightSurface(reflectance));
    public void Set(LightChunk geometry, LightSurface reflectance)
    {
        ivec3 key = geometry.Coordinate;
        LightChunk? old = _batch ? _preparedOld.GetValueOrDefault(key) : _geometry.GetValueOrDefault(key);
        _geometry[key] = geometry;
        _colors.TryGetValue(key,out LightSurface? oldColors);
        _colors[key] = reflectance;
        foreach (ivec3 emitter in old?.Emitters ?? []) _emitters.Remove(emitter);
        foreach (ivec3 emitter in geometry.Emitters) _emitters.Add(emitter);
        if (!_bounds) { _low = key; _high = key; _bounds = true; }
        else { _low = ivec3.Min(_low,key); _high = ivec3.Max(_high,key); }
        // Refresh only columns touched by geometry, including lower cells exposed by mining.
        HashSet<ivec2> columns = [];
        List<ivec3> changed = [];
        for (int i = 0; i < 4096; i++)
        {
            if (old == null ? geometry.Masks[i] == 0 : old.Masks[i] == geometry.Masks[i] && oldColors != null && oldColors[i] == reflectance[i]) continue;
            ivec3 p = key*16 + Local(i);
            columns.Add(p.xy); changed.Add(p);
        }
        foreach (ivec2 column in columns)
        {
            int top = int.MinValue;
            for (int z = _high.z; z >= _low.z && top == int.MinValue; z--)
                if (_geometry.TryGetValue(new(column.x>>4,column.y>>4,z),out LightChunk? c))
                    for (int dz = 15; dz >= 0; dz--)
                        if (c.Masks[(column.x&15)+16*(column.y&15)+256*dz] != 0) { top = z*16+dz; break; }
            int previous = Roof(column);
            ivec2 roofKey = new(column.x>>4,column.y>>4);
            if (!_roof.TryGetValue(roofKey,out int[]? heights))
            {
                heights = new int[256]; Array.Fill(heights,int.MinValue); _roof[roofKey] = heights; RoofTopologyVersion++;
            }
            heights[(column.x&15)+16*(column.y&15)] = top;
            if (previous != top) _dirtyRoofs.Add(roofKey);
            if (previous != top) RefreshSkyColumn(column,top,old != null);
        }
        ivec2 columnKey = key.xy;
        if (!_roof.ContainsKey(columnKey))
        {
            int[] heights = new int[256]; Array.Fill(heights,int.MinValue); _roof[columnKey] = heights;
            _dirtyRoofs.Add(columnKey); RoofTopologyVersion++;
        }
        bool full = geometry.Masks.AsSpan().IndexOfAnyExcept((byte)255) < 0;
        if (full && _chunks.Remove(key,out Chunk? retired)) { retired.Scan = -1; _dirty.Remove(retired); TopologyVersion++; }
        if (!full && !_chunks.ContainsKey(key)) AddChunk(key);
        // An air halo lets sources propagate over chunk boundaries and above the terrain.
        foreach (ivec3 d in LightGrid.FaceDirections)
        {
            ivec3 adjacent = key+d;
            if (_geometry.TryGetValue(adjacent,out LightChunk? solid) && solid.Masks.AsSpan().IndexOfAnyExcept((byte)255) < 0) continue;
            if (adjacent.z >= Math.Min(0,_low.z) && adjacent.z <= _high.z+1) AddChunk(adjacent);
        }
        foreach (ivec3 p in old == null ? Enumerable.Empty<ivec3>() : changed)
        {
            Queue(p,true);
            foreach (var (d,_) in Directions) Queue(p+d,true);
        }
        if (columns.Count > 0)
            for (int z = Math.Min(0,_low.z); z <= _high.z+1; z++)
                if (_chunks.TryGetValue(new(key.x,key.y,z),out Chunk? chunk)) Schedule(chunk);
        if (changed.Count > 0)
        {
            ivec3 lo = changed.Aggregate(ivec3.Min), hi = changed.Aggregate(ivec3.Max)+1;
            if (_batch)
            {
                _batchLow = _batchLow is ivec3 a ? ivec3.Min(a,lo) : lo;
                _batchHigh = _batchHigh is ivec3 b ? ivec3.Max(b,hi) : hi;
            }
            else ScheduleShadowRegion(lo,hi);
        }
        SyncPoints(changed.Count > 0);
        foreach (ivec3 p in (old?.Emitters ?? []).Concat(geometry.Emitters))
            foreach (ivec3 d in LightGrid.FaceDirections) Queue(p+d,true);
    }

    private void ScheduleShadowRegion(ivec3 lo,ivec3 hi)
    {
        InvalidateShadow(lo,hi);
        double minU = lo.x-hi.z*(.45/.84), maxU = hi.x-lo.z*(.45/.84);
        double minV = lo.y-hi.z*(.3/.84), maxV = hi.y-lo.z*(.3/.84);
        foreach (Chunk c in _chunks.Values)
        {
            ivec3 a = c.Key*16, b = a+16;
            if (a.x-b.z*(.45/.84) <= maxU && b.x-a.z*(.45/.84) >= minU &&
                a.y-b.z*(.3/.84) <= maxV && b.y-a.z*(.3/.84) >= minV) Schedule(c);
        }
    }
    private void SyncPoints(bool geometryChanged)
    {
        foreach (ivec3 source in _pointShadows.Keys.Where(p => !_emitters.Contains(p) && !_objectEmitters.Contains(p)).ToArray()) _pointShadows.Remove(source);
        foreach (ivec3 source in _emitters.Concat(_objectEmitters))
        {
            if (!_pointShadows.TryGetValue(source,out PointShadow? point)) _pointShadows[source] = point = new(source);
            if (geometryChanged) { if (point.Next >= point.Depth.Length) point.Next = 0; else if (point.Next > 0) point.Rescan = true; }
        }
        _pointOrder = [.. _pointShadows.Values];
    }
    public void SetShadowObjects(LightObject[] objects)
    {
        Dictionary<ivec3,byte> masks = [];
        HashSet<ivec3> emitters = [];
        foreach (LightObject body in objects)
        foreach (LightChunk chunk in body.Chunks)
            foreach (ivec3 emitter in chunk.Emitters)
                emitters.Add((ivec3)dvec3.Floor(body.Position+body.Orientation*(new dvec3(emitter)+.5)));
        bool needsObjectGeometry = !GpuSunShadows || _emitters.Count != 0 || emitters.Count != 0;
        if (needsObjectGeometry)
        foreach (LightObject body in objects)
        foreach (LightChunk chunk in body.Chunks)
        {
            foreach (int i in chunk.Occupied)
            {
                ivec3 p = (ivec3)dvec3.Floor(body.Position+body.Orientation*(new dvec3(chunk.Coordinate*16+Local(i))+.5));
                masks[p] = (byte)(masks.GetValueOrDefault(p)|chunk.Masks[i]);
            }
        }
        List<ivec3> changed = [.. _objectMasks.Keys.Concat(masks.Keys).Distinct().Where(p => _objectMasks.GetValueOrDefault(p) != masks.GetValueOrDefault(p))];
        _objectMasks = masks; _objectTop = masks.Count == 0 ? 0 : masks.Keys.Max(p => p.z)+2;
        _objectLow = masks.Count == 0 ? _low*16 : masks.Keys.Aggregate(ivec3.Min);
        _objectHigh = masks.Count == 0 ? (_high+1)*16 : masks.Keys.Aggregate(ivec3.Max)+1;
        foreach (ivec3 source in _objectEmitters.Concat(emitters))
            foreach (ivec3 d in LightGrid.FaceDirections) Queue(source+d,true);
        _objectEmitters = emitters;
        SyncPoints(changed.Count > 0);
        if (GpuSunShadows) return;
        foreach (ivec3 p in changed)
        {
            InvalidateShadow(p,p+1);
            // Refresh the narrow column of grid cells covered by this moving shadow.
            double minU = p.x-(p.z+1)*(.45/.84), maxU = p.x+1-p.z*(.45/.84);
            double minV = p.y-(p.z+1)*(.3/.84), maxV = p.y+1-p.z*(.3/.84);
            for (int z = Math.Min(0,_low.z)*16; z <= (_high.z+1)*16; z++)
            for (int y = (int)Math.Floor(minV+z*(.3/.84))-1; y <= (int)Math.Ceiling(maxV+(z+1)*(.3/.84)); y++)
            for (int x = (int)Math.Floor(minU+z*(.45/.84))-1; x <= (int)Math.Ceiling(maxU+(z+1)*(.45/.84)); x++)
                Queue(new(x,y,z),true);
        }
    }
    private byte ShadowMask(ivec3 p) => (byte)(Mask(p)|_objectMasks.GetValueOrDefault(p));

    public void Remove(ivec3 key)
    {
        if (!_geometry.ContainsKey(key)) return;
        Set(new(key,new byte[4096],[]),new vec3[4096]);
        _geometry.Remove(key); _colors.Remove(key);
        foreach (ivec3 candidate in LightGrid.FaceDirections.Select(d => key+d).Append(key))
        {
            if (_geometry.ContainsKey(candidate) || LightGrid.FaceDirections.Any(d => _geometry.ContainsKey(candidate+d))) continue;
            if (_chunks.Remove(candidate,out Chunk? removed)) { removed.Scan = -1; _dirty.Remove(removed); TopologyVersion++; }
        }
        if (!_geometry.Keys.Any(k => k.x == key.x && k.y == key.y) && _roof.Remove(key.xy))
        { _dirtyRoofs.Remove(key.xy); RoofTopologyVersion++; }
        // Cached rays must not retain geometry that has streamed out of the world.
        InvalidateShadow(key*16,key*16+16);
        if (_geometry.Count != 0)
        {
            _low = _geometry.Keys.Aggregate(ivec3.Min); _high = _geometry.Keys.Aggregate(ivec3.Max);
        }
        else { _bounds = false; _roof.Clear(); _dirtyRoofs.Clear(); RoofTopologyVersion++; _shadow.Clear(); }
    }

    private void RefreshSkyColumn(ivec2 column,int roof,bool urgent)
    {
        for (int z = Math.Min(0,_low.z); z <= _high.z+1; z++)
        {
            if (!_chunks.TryGetValue(new(column.x>>4,column.y>>4,z),out Chunk? chunk)) continue;
            for (int localZ = 0; localZ < 16; localZ++)
            {
                ivec3 p = new(column,z*16+localZ); int i = LightGrid.IndexInChunk(p);
                byte sky = Mask(p) != 255 && p.z > roof ? (byte)255 : (byte)0;
                if (chunk.Sky(i) == sky) continue;
                chunk.Store(i,vec3.Zero,sky,sky); chunk.Revision = ++_revision; _dirty.Add(chunk);
                Queue(p,urgent);
                foreach (ivec3 d in LightGrid.FaceDirections) Queue(p+d,urgent);
            }
        }
    }
    private static readonly int[] AllCells = [.. Enumerable.Range(0,4096)];
    private void Schedule(Chunk chunk)
    {
        if (chunk.Scan >= 0) { chunk.Rescan = true; return; }
        chunk.Scan = 0; chunk.Seeds = null; EnqueueInitial(chunk);
    }
    private int[] InitialSeeds(Chunk chunk)
    {
        if (!GpuSunShadows) return AllCells;
        List<int> seeds = [];
        for (int i = 0; i < 4096; i++)
        {
            ivec3 p = chunk.Key*16+Local(i);
            if (Mask(p) == 255) continue;
            if (chunk.Light(i) != vec3.Zero) { seeds.Add(i); continue; }
            foreach (ivec3 d in LightGrid.FaceDirections)
            {
                ivec3 neighbor = p+d;
                bool skyBoundary = SkyColor != vec3.Zero && p.z <= Roof(p.xy) && neighbor.z > Roof(neighbor.xy) && Mask(neighbor) != 255;
                if (skyBoundary || (_colors.TryGetValue(LightGrid.ChunkOf(neighbor),out LightSurface? colors) && colors[LightGrid.IndexInChunk(neighbor)] != vec3.Zero)
                    || _emitters.Contains(neighbor) || _objectEmitters.Contains(neighbor))
                { seeds.Add(i); break; }
            }
        }
        return [.. seeds];
    }
    private bool Initial(out ivec3 p)
    {
        p = default; Chunk chunk = _initial.Peek();
        if (chunk.Scan < 0) { _initial.Dequeue(); return false; }
        chunk.Seeds ??= InitialSeeds(chunk);
        if (chunk.Seeds.Length == 0) { _initial.Dequeue(); chunk.Scan = -1; chunk.Rescan = false; return false; }
        p = chunk.Key*16+Local(chunk.Seeds[chunk.Scan++]);
        if (chunk.Scan == chunk.Seeds.Length)
        {
            _initial.Dequeue(); chunk.Scan = -1; chunk.Seeds = null;
            if (chunk.Rescan) { chunk.Rescan = false; Schedule(chunk); }
        }
        else if ((chunk.Scan&63) == 0) { _initial.Dequeue(); EnqueueInitial(chunk); }
        return true;
    }
    private Chunk AddChunk(ivec3 key)
    {
        if (_chunks.TryGetValue(key,out Chunk? chunk)) return chunk;
        chunk = new(key);
        for (int i = 0; i < 4096; i++)
        {
            ivec3 p = key*16+Local(i);
            if (Mask(p) != 255 && p.z > Roof(p.xy))
            {
                if (i == 0) { chunk.UniformLight = vec3.Zero; chunk.UniformSun = 255; chunk.UniformSky = 255; }
                else chunk.Store(i,vec3.Zero,255,255);
            }
            else chunk.Store(i,vec3.Zero,0);
        }
        chunk.Revision = ++_revision; _dirty.Add(chunk);
        _chunks.Add(key,chunk); Schedule(chunk); TopologyVersion++;
        return chunk;
    }
    private static ivec3 Local(int i) => new(i&15,(i>>4)&15,i>>8);
    private byte Mask(ivec3 p) => _geometry.TryGetValue(LightGrid.ChunkOf(p),out LightChunk? c) ? c.Masks[LightGrid.IndexInChunk(p)] : (byte)0;
    private int Roof(ivec2 p) => _roof.TryGetValue(new(p.x>>4,p.y>>4),out int[]? column) ? column[(p.x&15)+16*(p.y&15)] : int.MinValue;
    public vec3 Sample(ivec3 p) => _chunks.TryGetValue(LightGrid.ChunkOf(p),out Chunk? c) ? c.Light(LightGrid.IndexInChunk(p)) + SkyColor*(c.Sky(LightGrid.IndexInChunk(p))/255f) : _bounds && Mask(p) != 255 && p.z > Roof(p.xy) ? SkyColor : vec3.Zero;
    public float Sun(ivec3 p) => _chunks.TryGetValue(LightGrid.ChunkOf(p),out Chunk? c) ? c.Sun(LightGrid.IndexInChunk(p)) / 255f : 0;
    private void Queue(ivec3 p,bool urgent = false)
    {
        if (!_chunks.TryGetValue(LightGrid.ChunkOf(p),out Chunk? c)) return;
        int i = LightGrid.IndexInChunk(p);
        if (urgent ? c.Urgent[i] : c.Queued[i]) return;
        if (urgent) c.Urgent[i] = true;
        c.Queued[i] = true;
        (urgent ? _urgent : _frontier).Enqueue(p);
    }
    private bool Open(ivec3 p, ivec3 d)
    {
        int from = ~Mask(p), to = ~Mask(p+d);
        if (to == ~255) return false;
        if (d.y == 0 && d.z == 0) return d.x > 0 ? ((from>>1)&to&0x55) != 0 : (from&(to>>1)&0x55) != 0;
        if (d.x == 0 && d.z == 0) return d.y > 0 ? ((from>>2)&to&0x33) != 0 : (from&(to>>2)&0x33) != 0;
        if (d.x == 0 && d.y == 0) return d.z > 0 ? ((from>>4)&to&0x0f) != 0 : (from&(to>>4)&0x0f) != 0;
        if (d.x != 0 && !Open(p,new(d.x,0,0))) return false;
        if (d.y != 0 && !Open(p,new(0,d.y,0))) return false;
        if (d.z != 0 && !Open(p,new(0,0,d.z))) return false;
        if (d.x != 0 && d.y != 0 && d.z != 0 && !Open(p,new(d.x,d.y,0))) return false;
        if (d.x != 0 && d.y != 0 && d.z != 0 && !Open(p,new(d.x,0,d.z))) return false;
        if (d.x != 0 && d.y != 0 && d.z != 0 && !Open(p,new(0,d.y,d.z))) return false;
        return true;
    }

    /// <summary>Advance the frontier within the frame's CPU and visit budgets.</summary>
    public int Step(double milliseconds = 2, int maxCells = 4096)
    {
        long start = Stopwatch.GetTimestamp();
        int count = 0, wave = _urgent.Count+_frontier.Count;
        while (count < maxCells && Pending)
        {
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= milliseconds) break;
            if (BlockLighting && ((count&3) == 0 || (_initial.Count == 0 && _urgent.Count == 0 && _frontier.Count == 0)))
            {
                PointShadow? point = null;
                for (int visit = 0; visit < _pointOrder.Length; visit++)
                {
                    _pointCursor %= _pointOrder.Length;
                    var candidate = _pointOrder[_pointCursor++];
                    if (candidate.Next < candidate.Depth.Length) { point = candidate; break; }
                }
                if (point != null) { UpdatePointShadow(point); count++; continue; }
            }
            ivec3 p; bool urgent = false;
            if (_initial.Count != 0 && (count&3) == 3)
            {
                if (!Initial(out p)) { count++; continue; }
            }
            else if (_urgent.Count != 0) { p = _urgent.Dequeue(); wave--; urgent = true; }
            else if (wave > 0 && _frontier.Count != 0) { p = _frontier.Dequeue(); wave--; }
            else if (_initial.Count != 0)
            {
                if (!Initial(out p)) { count++; continue; }
            }
            else break;
            if (!_chunks.TryGetValue(LightGrid.ChunkOf(p),out Chunk? c)) { count++; continue; }
            int i = LightGrid.IndexInChunk(p);
            c.Queued[i] = false; c.Urgent[i] = false;
            Update(c,i,p,urgent); count++;
        }
        Milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return count;
    }

    private void Update(Chunk c,int i,ivec3 p,bool urgent)
    {
        vec3 value = vec3.Zero; byte sun = 0, sky = 0;
        if (Mask(p) != 255)
        {
            bool testedSun = !GpuSunShadows;
            sun = testedSun ? (Visible(p) ? (byte)255 : (byte)0) : (p.z > Roof(p.xy) ? (byte)255 : (byte)0);
            if (p.z > Roof(p.xy)) { value = SkyColor; sky = 255; }
            vec3 bounce = vec3.Zero;
            foreach (ivec3 d in LightGrid.FaceDirections)
            {
                ivec3 wall = p+d;
                if (BlockLighting && (_emitters.Contains(wall) || _objectEmitters.Contains(wall))) value = vec3.Max(value,new vec3(1.8f,.99f,.36f));
                if (!_colors.TryGetValue(LightGrid.ChunkOf(wall),out LightSurface? colors) || colors[LightGrid.IndexInChunk(wall)] == vec3.Zero) continue;
                // Incoming irradiance times average linear texture RGB times average alpha.
                // A face returns a sixth of its diffuse energy into the spherical grid.
                if (SunLighting && !testedSun) { sun = Visible(p) ? (byte)255 : (byte)0; testedSun = true; }
                float cosine = Math.Max(0,vec3.Dot(-(vec3)d,new vec3(.45f,.3f,.84f).Normalized));
                vec3 direct = SunLighting && sun != 0 ? SunColor*(cosine*.75f) : vec3.Zero;
                bounce += (Sample(p)+direct)*colors[LightGrid.IndexInChunk(wall)]/6f;
            }
            vec3 transported = vec3.Zero;
            foreach (var (d,attenuation) in Directions)
                if (Open(p,d)) transported = vec3.Max(transported,Sample(p+d)*attenuation);
            // The convex diffuse mix makes repeated bounces energy losing and removals converge.
            value = vec3.Max(value,transported)+bounce*.05f;
            if (sky != 0) value = vec3.Max(vec3.Zero,value-SkyColor);
            if (value.x < .0001f) value.x = 0;
            if (value.y < .0001f) value.y = 0;
            if (value.z < .0001f) value.z = 0;
        }
        vec3 delta = vec3.Abs(value-c.Light(i));
        float tolerance = Math.Max(.0000001f,Math.Max(value.x,Math.Max(value.y,value.z))*.001f);
        bool radianceChanged = Math.Max(delta.x,Math.Max(delta.y,delta.z)) >= tolerance || c.Sky(i) != sky;
        if (!radianceChanged && c.Sun(i) == sun) return;
        c.Store(i,value,sun,sky);
        if (radianceChanged || !GpuSunShadows) { c.Revision = ++_revision; _dirty.Add(c); }
        if (radianceChanged) foreach (var (d,_) in Directions) Queue(p+d,urgent);
    }

    private void UpdatePointShadow(PointShadow point)
    {
        int index = point.Next++, face = index/(PointShadow.Size*PointShadow.Size);
        int pixel = index%(PointShadow.Size*PointShadow.Size);
        double u = 2.0*((pixel%PointShadow.Size)+.5)/PointShadow.Size-1;
        double v = 2.0*((pixel/PointShadow.Size)+.5)/PointShadow.Size-1;
        dvec3 ray = (face switch
        {
            0 => new dvec3(1,v,u), 1 => new dvec3(-1,v,-u),
            2 => new dvec3(u,1,v), 3 => new dvec3(u,-1,-v),
            4 => new dvec3(u,v,1), _ => new dvec3(-u,v,-1)
        }).Normalized;
        dvec3 origin = new dvec3(point.Cell)+.5;
        ivec3 cell = point.Cell, step = new(Math.Sign(ray.x),Math.Sign(ray.y),Math.Sign(ray.z));
        dvec3 delta = dvec3.Abs(1.0/ray), next = delta*.5;
        float depth = float.PositiveInfinity;
        ivec3 low = ivec3.Min(_low*16,point.Cell), high = ivec3.Max((_high+1)*16,point.Cell+1);
        if (_objectMasks.Count != 0) { low = ivec3.Min(low,_objectLow); high = ivec3.Max(high,_objectHigh); }
        while (cell.x >= low.x && cell.y >= low.y && cell.z >= low.z && cell.x < high.x && cell.y < high.y && cell.z < high.z)
        {
            byte mask = cell == point.Cell ? (byte)0 : ShadowMask(cell);
            for (int part = 0; part < 8; part++)
            {
                if ((mask&(1<<part)) == 0) continue;
                dvec3 a = new dvec3(cell)+new dvec3(part&1,(part>>1)&1,part>>2)*.5;
                dvec3 t0 = (a-origin)/ray, t1 = (a+.5-origin)/ray;
                dvec3 enter = dvec3.Min(t0,t1), exit = dvec3.Max(t0,t1);
                double t = Math.Max(0,Math.Max(enter.x,Math.Max(enter.y,enter.z)));
                if (Math.Min(exit.x,Math.Min(exit.y,exit.z)) >= t) depth = Math.Min(depth,(float)t);
            }
            if (float.IsFinite(depth)) break;
            if (next.x < next.y && next.x < next.z) { cell.x += step.x; next.x += delta.x; }
            else if (next.y < next.z) { cell.y += step.y; next.y += delta.y; }
            else { cell.z += step.z; next.z += delta.z; }
        }
        point.WorkingDepth[index] = depth*(float)Math.Max(Math.Abs(ray.x),Math.Max(Math.Abs(ray.y),Math.Abs(ray.z)));
        if (point.Next == point.Depth.Length)
        {
            (point.Depth,point.WorkingDepth) = (point.WorkingDepth,point.Depth);
            point.Revision = ++_revision;
            if (point.Rescan) { point.Next = 0; point.Rescan = false; }
        }
    }

    private static ivec2 ShadowKey(ivec3 p) => new((int)Math.Floor(2*(p.x+.5-(p.z+.5)*(.45/.84))), (int)Math.Floor(2*(p.y+.5-(p.z+.5)*(.3/.84))));
    private void InvalidateShadow(ivec3 low,ivec3 high)
    {
        if (_shadow.Count == 0) return;
        int x0 = (int)Math.Floor(2*(low.x-high.z*(.45/.84))), x1 = (int)Math.Floor(2*(high.x-low.z*(.45/.84)));
        int y0 = (int)Math.Floor(2*(low.y-high.z*(.3/.84))), y1 = (int)Math.Floor(2*(high.y-low.z*(.3/.84)));
        long area = (long)(x1-x0+1)*(y1-y0+1);
        if (area > _shadow.Count)
        {
            ivec2[] keys = [.. _shadow.Keys.Where(k => k.x >= x0 && k.x <= x1 && k.y >= y0 && k.y <= y1)];
            foreach (ivec2 key in keys) _shadow.Remove(key);
        }
        else for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) _shadow.Remove(new(x,y));
    }
    private bool Visible(ivec3 p)
    {
        ivec2 key = ShadowKey(p);
        if (!_shadow.TryGetValue(key,out float depth))
        {
            // Build this shadow-map texel on demand with a terrain DDA. Partial blocks
            // intersect their occupied half-cell boxes, matching transport masks.
            double top = Math.Max((_high.z+2)*16,_objectTop);
            double ox = (key.x+.5)*.5+top*(.45/.84), oy = (key.y+.5)*.5+top*(.3/.84);
            int x = (int)Math.Floor(ox), y = (int)Math.Floor(oy), z = (int)top-1;
            double tx = (ox-x)/(.45/.84), ty = (oy-y)/(.3/.84), tz = 1;
            double t = 0; depth = float.NegativeInfinity;
            while (z >= Math.Min(0,_low.z)*16)
            {
                byte mask = GpuSunShadows ? Mask(new(x,y,z)) : ShadowMask(new(x,y,z));
                if (mask != 0)
                {
                    double hit = double.PositiveInfinity;
                    for (int part = 0; part < 8; part++)
                    {
                        if ((mask&(1<<part)) == 0) continue;
                        double bx = x+(part&1)*.5, by = y+((part>>1)&1)*.5, bz = z+(part>>2)*.5;
                        double enter = Math.Max(t,Math.Max((ox-bx-.5)/(.45/.84),Math.Max((oy-by-.5)/(.3/.84),top-bz-.5)));
                        double exit = Math.Min((ox-bx)/(.45/.84),Math.Min((oy-by)/(.3/.84),top-bz));
                        if (exit >= enter) hit = Math.Min(hit,enter);
                    }
                    if (double.IsFinite(hit)) { depth = (float)(top-hit); break; }
                }
                if (tx < ty && tx < tz) { t = tx; x--; tx += .84/.45; }
                else if (ty < tz) { t = ty; y--; ty += .84/.3; }
                else { t = tz; z--; tz++; }
            }
            _shadow[key] = depth;
        }
        return p.z+.5f > depth+.05f;
    }
}
