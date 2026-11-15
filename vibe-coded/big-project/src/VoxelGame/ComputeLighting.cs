using System.Numerics;
using System.Runtime.InteropServices;
using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VoxelGame;

/// <summary>
/// Jacobi RGB diffusion over every loaded block, including explicit air cells.
/// <para>
/// The kernels and the voxel shader read one metadata buffer: chunk hash, slot records,
/// reflectance, column roofs, packed materials, point sources and object occupancy. Its CPU
/// mirror is edited in place as terrain, columns and objects change, and only changed 4 KB
/// pages are copied to the single device-local copy, after the previous frame's reads. A region
/// that outgrows its capacity lays the whole mirror out again, with room to spare.
/// </para>
/// </summary>
internal sealed unsafe class ComputeLighting : IDisposable
{
    private const int BindingCount = 6;
    private const int TilesPerChunk = 8;
    private const int HeaderWords = 16;
    private const int RecordWords = 16;
    private const int ReflectanceWords = 256*4;
    private const int RoofWords = 256;
    private const int PageShift = 10;
    private const int PointShadowSamples = 1536;
    private const int ChunkBytes = 4096*4;
    private const uint Emitting = 512;
    // A point shadow ray stops at cube-face depth 256, so farther cells (Chebyshev) never occlude it.
    private const int PointReach = 258;
    // The largest packing of each palette width (header, palette, indices), and raw values.
    private static readonly int[] MaterialBlockWords = [1+2+128, 1+4+256, 1+16+512, 1+256+1024, 1+4096];
    private static readonly uint AirSignature = Signature(new uint[4096]);

    public ComputeLighting(GraphicsContext graphics,string shaders)
    {
        _graphics = graphics; _loader = new(graphics.Vk,graphics.Device);
        Vk.GetPhysicalDeviceProperties(graphics.PhysicalDevice,out PhysicalDeviceProperties deviceProperties);
        _timestampPeriod = deviceProperties.Limits.TimestampPeriod;
        _maxStorageRange = deviceProperties.Limits.MaxStorageBufferRange;
        QueryPoolCreateInfo timing = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 3*GraphicsContext.FramesInFlight };
        Check(Vk.CreateQueryPool(Device,&timing,null,out _timing));
        DescriptorSetLayoutBinding[] bindings = Enumerable.Range(0,BindingCount).Select(i => new DescriptorSetLayoutBinding
        { Binding = (uint)i, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit }).ToArray();
        DescriptorSetLayoutCreateInfo info = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = BindingCount };
        fixed (DescriptorSetLayoutBinding* b = bindings) { info.PBindings = b; Check(Vk.CreateDescriptorSetLayout(Device,&info,null,out _layout)); }
        _diffuse = _loader.Load(Path.Combine(shaders,"voxel/light_diffuse.comp"),[_layout],(uint)sizeof(Parameters),optimize:true);
        _selectTiles = _loader.Load(Path.Combine(shaders,"voxel/light_active.comp"),[_layout],(uint)sizeof(Parameters),optimize:true);
        _dispatchTiles = _loader.Load(Path.Combine(shaders,"voxel/light_dispatch.comp"),[_layout],(uint)sizeof(Parameters),optimize:true);
        _shadows = _loader.Load(Path.Combine(shaders,"voxel/light_points.comp"),[_layout],(uint)sizeof(Parameters),optimize:true);
        DescriptorPoolSize size = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = BindingCount*GraphicsContext.FramesInFlight };
        DescriptorPoolCreateInfo pool = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = GraphicsContext.FramesInFlight, PoolSizeCount = 1, PPoolSizes = &size };
        Check(Vk.CreateDescriptorPool(Device,&pool,null,out _pool));
        for (int i = 0; i < _sets.Length; i++)
        {
            DescriptorSetLayout layout = _layout;
            DescriptorSetAllocateInfo allocation = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
            Check(Vk.AllocateDescriptorSets(Device,&allocation,out _sets[i]));
        }
    }

    private readonly GraphicsContext _graphics;
    private Vk Vk => _graphics.Vk;
    private Device Device => _graphics.Device;
    private readonly ComputePipelineLoader _loader;
    private readonly ComputePipeline _diffuse, _shadows, _selectTiles, _dispatchTiles;
    private DescriptorSetLayout _layout;
    private DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[GraphicsContext.FramesInFlight];
    // Buffers are replaced, never resized, and a destroyed handle's value may come back:
    // descriptor sets are rewritten when this count of allocations moves on.
    private int _allocations;
    private readonly int[] _bound = [-1,-1];
    private QueryPool _timing;
    private readonly bool[] _timed = new bool[GraphicsContext.FramesInFlight];
    private readonly float _timestampPeriod;
    private readonly ulong _maxStorageRange;
    public double DiffuseMilliseconds { get; private set; }
    public double ShadowMilliseconds { get; private set; }
    internal int PointShadowDispatches { get; private set; }
    /// <summary>Commits that laid the metadata out whole, and commits that edited it in place.</summary>
    internal int Relayouts { get; private set; }
    internal int Edits { get; private set; }
    /// <summary>Metadata bytes copied to the GPU by the latest <see cref="Record"/>.</summary>
    public int UploadedBytes { get; private set; }
    internal bool ReuseUnchanged { get; set; } = Environment.GetEnvironmentVariable("MYT_LIGHT_REUSE") != "0";
    internal bool CompactDispatch { get; set; } = Environment.GetEnvironmentVariable("MYT_LIGHT_COMPACT") != "0";
    /// <summary>When off, every change lays the whole metadata out again, and every point source is traced again each frame, as a reference.</summary>
    internal bool IncrementalMetadata { get; set; } = true;
    internal bool BudgetedDiffusion { get; set; }
    internal bool DeferredDaylight { get; set; }
    internal bool AdaptiveTileBudget { get; set; } = true;
    internal int TileBudget { get; set; } = 2048;
    internal int ActiveTiles { get; private set; }
    internal int ConfigurationChanges => _configurationVersion;
    internal vec3 FocusPosition { get; set; }
    private double _configuredAt = double.NegativeInfinity;
    private uint _budgetCursor;
    private bool _lastBudgeted;
    private readonly HostBuffer[] _workReadback = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly int[] _recordedBudget = new int[GraphicsContext.FramesInFlight];
    private int _version;
    public int Version => _version;
    public int MetadataBytes => _words.Length*4;
    public int PointCount => _pointSlots.Count;
    public ulong GridBytes => (ulong)_capacity*4096*8;
    public int CellCount => _slots.Count*4096;
    public bool Ready => _slots.Count != 0;
    public Buffer LightBuffer => _light[0].Buffer;
    public Buffer PointBuffer => _pointDepth.Buffer;
    public Buffer MetadataBuffer => _metadata.Buffer;
    public int Allocations => _allocations;

    // ---------------------------------------------------------------------------------------
    // Terrain, by chunk, and the columns it stands in
    // ---------------------------------------------------------------------------------------

    private readonly Dictionary<ivec3,TerrainChunk> _terrain = [];
    private readonly HashSet<ivec3> _emitterTerrain = [];
    private readonly Dictionary<ivec2,Column> _columns = [];
    private readonly Dictionary<int,int> _terrainLevels = [];
    private HashSet<ivec2> _loadedColumns = [], _nextLoaded = [];
    private int _columnVersion = -1;
    private long _chunkRevision = -1;
    private int _airTop = 1;
    private readonly uint[] _values = new uint[4096];
    private readonly uint[] _packed = new uint[4097];
    private readonly vec3[] _reflectance = Enumerable.Repeat(new vec3(.5f),256).ToArray();
    private vec3 _sky;
    private vec4 _sunDirection, _sunColor;
    private bool _blocks = true, _sun = true;
    private int _configurationVersion, _activityConfiguration = -1;
    // Terrain or columns changed since the last Build: objects are voxelized again, as before.
    private bool _dirty = true;

    // ---------------------------------------------------------------------------------------
    // Slots: one light brick each, at a fixed place in the light buffers
    // ---------------------------------------------------------------------------------------

    private readonly Dictionary<ivec3,int> _slots = [];
    private readonly List<ivec3?> _keys = [];
    // The geometry each slot's light was computed for, as saves record it.
    private readonly List<uint> _signatures = [];
    private readonly Stack<int> _free = [];
    private readonly HashSet<int> _reset = [];
    private readonly Dictionary<ivec3,LightChunkState> _saved = [];
    // Removed slots whose light is on its way back from the GPU. Their slots stay reserved, so a
    // key that returns first takes its slot back with the light it left.
    private readonly Dictionary<ivec3,(int Slot,long Token)> _archiving = [];
    private readonly List<ArchiveEntry> _archiveRequests = [];
    private readonly List<PendingArchive> _pendingArchives = [];
    private long _archiveTokens;
    private int _epoch;
    private bool _wakeAll = true;
    private readonly HashSet<ivec2> _wakeColumns = [];
    private readonly List<int> _wakeSlots = [], _zeroSlots = [];
    private readonly List<(int Slot,LightChunkState State)> _restores = [];

    // ---------------------------------------------------------------------------------------
    // Objects: occupancy near point sources, for their shadows, and lit furnaces
    // ---------------------------------------------------------------------------------------

    private readonly HashSet<(int Body,ivec3 Chunk)> _objectSources = [];
    private readonly List<VoxelBody> _bodies = [];
    private readonly List<(dvec3 Position,dquat Orientation)> _bodyPoses = [];
    private readonly HashSet<int> _liveBodies = [];
    private readonly Dictionary<int,BodyCells> _bodyCells = [];
    private readonly Dictionary<int,(dvec3 Position,dquat Rotation)> _poses = [];
    private Dictionary<ivec3,uint> _objects = [], _nextObjects = [];
    private bool _objectDirty;
    private HashSet<ivec3> _objectEmitterCells = [], _nextObjectEmitters = [];
    private HashSet<ivec3> _emitterWanted = [], _nextEmitterWanted = [];
    private HashSet<int> _emitterSlots = [], _nextEmitterSlots = [];
    private readonly List<ivec3> _reachPoints = [];

    // ---------------------------------------------------------------------------------------
    // Point sources: stable slots, each with its own depth maps
    // ---------------------------------------------------------------------------------------

    private readonly Dictionary<ivec3,int> _pointSlots = [];
    private readonly List<ivec3?> _pointCells = [];
    private readonly List<bool> _pointDirty = [];
    private readonly Stack<int> _freePoints = new();
    private readonly HashSet<ivec3> _nextPoints = [];
    private bool _pointsDirty;
    // Chunks whose occluders changed since the last commit.
    private readonly HashSet<ivec3> _shadowChunks = [];

    // ---------------------------------------------------------------------------------------
    // The metadata mirror and its pending edits
    // ---------------------------------------------------------------------------------------

    private uint[] _words = [];
    private ulong[] _dirtyPages = [];
    private bool _anyDirty, _relayout = true;
    private int _hashStart, _hashCapacity, _recordStart, _recordCapacity, _reflectanceStart, _roofHashStart, _roofHashCapacity;
    private int _roofStart, _roofCapacity, _materialStart, _materialCapacity, _pointStart, _pointCapacity, _objectStart, _objectRegion;
    private int _roofTop, _materialTop;
    private readonly Stack<int> _freeRoofs = new();
    private readonly Stack<int>[] _freeMaterials = [new(),new(),new(),new(),new()];
    private readonly HashSet<ivec2> _dirtyColumns = [], _dirtyRoofs = [], _touchedColumns = [];
    private readonly HashSet<ivec3> _dirtyKeys = [], _dirtyHashKeys = [], _dirtyMaterials = [];
    private readonly HashSet<int> _dirtyRecords = [];
    private bool _dynamicDirty;

    // ---------------------------------------------------------------------------------------
    // GPU resources
    // ---------------------------------------------------------------------------------------

    private DeviceBuffer _metadata;
    private DeviceBuffer[] _light = new DeviceBuffer[2];
    private DeviceBuffer _pointDepth;
    // Ping-pong change flags mirror the two light buffers. An 8^3 tile sleeps only when
    // it and all six input neighbors were unchanged in the preceding Jacobi iteration.
    private DeviceBuffer _activity;
    private DeviceBuffer _work;
    private int _capacity, _pointDepthCapacity;
    private readonly HostBuffer[] _uploads = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly List<BufferCopy> _copies = [];
    private readonly List<(DeviceBuffer Buffer,ulong Frame)> _retired = [];
    private readonly List<(DeviceBuffer Source, DeviceBuffer Target)> _lightGrowth = [];
    private ulong _frame;

    private readonly record struct DeviceBuffer(Buffer Buffer, DeviceMemory Memory, ulong Size);
    private readonly record struct ArchiveEntry(ivec3 Key, int Slot, uint Signature, long Token);
    private sealed record PendingArchive(HostBuffer Buffer, uint FrameIndex, int Epoch, ArchiveEntry[] Entries);
    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters { public vec4 Sky; public uint Iteration, Blocks, Sun, Count; public vec4 SunDirection, SunColor, Focus; public uvec4 Schedule; }

    /// <summary>A terrain chunk as the kernels see it.</summary>
    private sealed class TerrainChunk
    {
        // Non-uniform materials, packed as gridMaterial unpacks them; null for a uniform chunk.
        public uint[]? Packed;
        public uint Uniform;
        public uint Hash;
        // 2 all opaque, 1 all empty, 0 mixed.
        public uint Occupancy;
        // The highest local z of an occupied cell per (x, y), or -1.
        public sbyte[] Tops = [];
        public ivec3[] Emitters = [];
        // Offset of Packed in the material region, once placed.
        public int Block = -1;
    }

    private sealed class Column
    {
        public readonly List<int> Terrain = [];
        public readonly List<int> Slots = [];
        public int Roof = -1;
    }

    /// <summary>An object's cells with a light value, cached until its grid changes.</summary>
    private sealed class BodyCells
    {
        public Grid3D<Voxel>? Grid;
        public long Revision = -1;
        public bool TransparentLeaves;
        public (ivec3 Cell,uint Value)[] Cells = [];
        public dvec3 Center;
        public double Radius;
        public bool Emits;
    }

    private static void Check(Result result) { if (result != Result.Success) throw new InvalidOperationException($"Compute lighting: {result}"); }
    private static uint Hash(ivec3 key) => unchecked((uint)key.x*73856093u^(uint)key.y*19349663u^(uint)key.z*83492791u);
    private static uint Signature(ReadOnlySpan<uint> values)
    {
        uint hash = 2166136261; foreach (uint value in values) hash = unchecked((hash^value)*16777619); return hash;
    }
    private static uint Float(float x) => BitConverter.SingleToUInt32Bits(x);
    private static int PowerOfTwo(int x) => (int)BitOperations.RoundUpToPowerOf2((uint)x);
    private static int ObjectHashCapacity(int count) => PowerOfTwo(Math.Max(2,count*2));
    private static int MaterialClass(uint header) => (header>>16) switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };

    private DeviceBuffer Allocate(ulong bytes,BufferUsageFlags extraUsage = 0)
    {
        bytes = Math.Max(16,bytes);
        if (bytes > _maxStorageRange) throw new InvalidOperationException($"Light grid needs a {bytes} byte storage buffer; GPU limit is {_maxStorageRange}");
        BufferCreateInfo info = new() { SType = StructureType.BufferCreateInfo, Size = bytes,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | extraUsage };
        Check(Vk.CreateBuffer(Device,&info,null,out Buffer buffer));
        Vk.GetBufferMemoryRequirements(Device,buffer,out MemoryRequirements requirements);
        MemoryAllocateInfo allocation = new() { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size,
            MemoryTypeIndex = _graphics.FindMemoryType(requirements.MemoryTypeBits,MemoryPropertyFlags.DeviceLocalBit) };
        Check(Vk.AllocateMemory(Device,&allocation,null,out DeviceMemory memory)); Check(Vk.BindBufferMemory(Device,buffer,memory,0));
        _allocations++;
        return new(buffer,memory,bytes);
    }
    private void Destroy(DeviceBuffer buffer) { if (buffer.Buffer.Handle != 0) { Vk.DestroyBuffer(Device,buffer.Buffer,null); Vk.FreeMemory(Device,buffer.Memory,null); } }
    // Frames in flight may still read a replaced buffer; it goes once they have finished.
    private void Retire(DeviceBuffer buffer) { if (buffer.Buffer.Handle != 0) _retired.Add((buffer,_frame)); }

    public void SetReflectance(BlockType type,int variant,vec3 value) { _reflectance[(int)type*2+variant] = value; _relayout = true; }
    public void Configure(vec3 sky,bool blocks,bool sun,vec3? sunDirection = null,vec3? sunColor = null,float sunIntensity = 1, double? timeSeconds = null, bool force = false)
    {
        vec4 direction = new(sunDirection ?? new vec3(.45f,.3f,.84f).Normalized,sunIntensity);
        vec4 color = new(sunColor ?? new vec3(1,.96f,.88f),0);
        bool changed = _sky != sky || _blocks != blocks || _sun != sun
            || (sun && (_sunDirection != direction || _sunColor != color));
        if (!changed) return;
        double now = timeSeconds ?? System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        float skyDelta = (sky - _sky).Length;
        float solarDelta = sun ? Math.Max((direction - _sunDirection).Length, (color - _sunColor).Length) : 0;
        bool major = _blocks != blocks || _sun != sun || skyDelta >= .05f || solarDelta >= .05f;
        if (DeferredDaylight && !force && !major)
        {
            double elapsed = now - _configuredAt;
            if (elapsed < .2 || (Math.Max(skyDelta, solarDelta) < 1f / 512 && elapsed < 1)) return;
        }
        _configuredAt = now;
        _configurationVersion++;
        _sky = sky; _blocks = blocks; _sun = sun;
        _sunDirection = direction; _sunColor = color;
    }

    // ---------------------------------------------------------------------------------------
    // Changes
    // ---------------------------------------------------------------------------------------

    public void Invalidate(VoxelBody body,ivec3 key,bool transparentLeaves)
    {
        if (body is not VoxelMap)
        {
            bool source = body.Grid.TryGetChunk(key,out var objectChunk) && HasEmitter(objectChunk.Cells);
            if (source) _objectSources.Add((body.Id,key)); else _objectSources.Remove((body.Id,key));
            _objectDirty = true; return;
        }
        if (!body.Grid.TryGetChunk(key,out var chunk)) { RemoveTerrain(key); return; }
        Span<uint> values = _values;
        ReadOnlySpan<Voxel> cells = chunk.Cells;
        for (int i = 0; i < values.Length; i++)
        {
            Voxel v = cells[i]; int variant = v.Type == BlockType.Leaves && transparentLeaves ? 1 : 0;
            values[i] = LightingScene.Mask(v,transparentLeaves) | ((v.Lit && v.Type == BlockType.Furnace ? 1u : 0u)<<9)
                | (uint)((int)v.Type*2+variant)<<16;
        }
        bool uniform = values.IndexOfAnyExcept(values[0]) < 0;
        ReadOnlySpan<uint> packed = uniform ? [] : _packed.AsSpan(0,Pack(values,_packed));
        _terrain.TryGetValue(key,out TerrainChunk? old);
        // A packing holds every value, so equal packings are equal chunks.
        if (old != null && (uniform ? old.Packed == null && old.Uniform == values[0] : old.Packed != null && old.Packed.AsSpan().SequenceEqual(packed))) return;
        TerrainChunk next = Describe(key,values,uniform,packed);
        if (old == null)
        {
            ColumnAt(key.xy).Terrain.Add(key.z);
            _terrainLevels[key.z] = _terrainLevels.GetValueOrDefault(key.z)+1;
        }
        else ReleaseMaterial(old);
        if ((old?.Emitters.Length ?? 0) != 0 || next.Emitters.Length != 0) _pointsDirty = true;
        if (next.Emitters.Length != 0) _emitterTerrain.Add(key); else _emitterTerrain.Remove(key);
        _terrain[key] = next;
        TerrainChanged(key);
    }
    private void RemoveTerrain(ivec3 key)
    {
        if (!_terrain.Remove(key,out TerrainChunk? old)) return;
        ReleaseMaterial(old);
        _columns[key.xy].Terrain.Remove(key.z);
        if (--_terrainLevels[key.z] == 0) _terrainLevels.Remove(key.z);
        if (_emitterTerrain.Remove(key)) _pointsDirty = true;
        TerrainChanged(key);
    }
    private void TerrainChanged(ivec3 key)
    {
        _dirtyColumns.Add(key.xy); _dirtyMaterials.Add(key); _shadowChunks.Add(key);
        _wakeColumns.Add(key.xy); _dirty = true;
    }
    private static TerrainChunk Describe(ivec3 key,ReadOnlySpan<uint> values,bool uniform,ReadOnlySpan<uint> packed)
    {
        TerrainChunk chunk = new() { Hash = Signature(values), Uniform = uniform ? values[0] : 0u, Packed = uniform ? null : packed.ToArray(), Tops = new sbyte[256] };
        bool opaque = true, empty = true;
        List<ivec3>? emitters = null;
        Array.Fill(chunk.Tops,(sbyte)-1);
        for (int i = 0; i < values.Length; i++)
        {
            uint v = values[i], shape = v&767u;
            opaque &= shape == 255u; empty &= shape == 0u;
            if ((v&255u) != 0) chunk.Tops[i&255] = (sbyte)(i>>8);
            if ((v&Emitting) != 0) (emitters ??= []).Add(key*16+new ivec3(i&15,(i>>4)&15,i>>8));
        }
        chunk.Occupancy = opaque ? 2u : empty ? 1u : 0u;
        chunk.Emitters = emitters == null ? [] : [.. emitters];
        return chunk;
    }
    private void ReleaseMaterial(TerrainChunk chunk)
    {
        if (chunk.Block < 0) return;
        _freeMaterials[MaterialClass(chunk.Packed![0])].Push(chunk.Block);
        chunk.Block = -1;
    }

    /// <summary>The materials as gridMaterial unpacks them: palette in first-occurrence order, then indices.</summary>
    private static int Pack(ReadOnlySpan<uint> values,Span<uint> result)
    {
        Span<uint> palette = stackalloc uint[256];
        Span<byte> indices = stackalloc byte[4096];
        Span<int> table = stackalloc int[1024];
        table.Fill(-1);
        int count = 0, lastIndex = 0; uint last = ~values[0];
        for (int i = 0; i < values.Length; i++)
        {
            uint v = values[i];
            if (v != last)
            {
                int h = (int)((v*2654435761u)>>22);
                while (table[h] >= 0 && palette[table[h]] != v) h = (h+1)&1023;
                if (table[h] < 0)
                {
                    if (count == 256)
                    {
                        result[0] = 32u<<16; values.CopyTo(result[1..]);
                        return 4097;
                    }
                    table[h] = count; palette[count++] = v;
                }
                last = v; lastIndex = table[h];
            }
            indices[i] = (byte)lastIndex;
        }
        int bits = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8, perWord = 32/bits, length = 1+count+4096/perWord;
        result[..length].Clear();
        result[0] = (uint)(count|(bits<<16));
        palette[..count].CopyTo(result[1..]);
        for (int i = 0; i < 4096; i++) result[1+count+i/perWord] |= (uint)indices[i]<<((i%perWord)*bits);
        return length;
    }
    private static bool HasEmitter(ReadOnlySpan<Voxel> cells)
    { foreach (Voxel v in cells) if (v.Type == BlockType.Furnace && v.Lit) return true; return false; }
    private Column ColumnAt(ivec2 key)
    {
        if (!_columns.TryGetValue(key,out Column? column)) _columns[key] = column = new();
        return column;
    }

    // ---------------------------------------------------------------------------------------
    // Build: bring the CPU state and the mirror up to date for this frame
    // ---------------------------------------------------------------------------------------

    public void Build(IEnumerable<VoxelBody> bodies,Func<VoxelBody,(dvec3 Position,dquat Orientation)> poseOf,bool transparentLeaves)
    {
        _bodies.Clear(); VoxelMap? map = null;
        foreach (VoxelBody body in bodies) { if (body is VoxelMap m) map ??= m; else _bodies.Add(body); }
        if (map != null) SyncTerrain(map,transparentLeaves);
        if (_objectSources.Count != 0 || _bodyCells.Count > _bodies.Count)
        {
            _liveBodies.Clear(); foreach (VoxelBody body in _bodies) _liveBodies.Add(body.Id);
            _objectSources.RemoveWhere(p => !_liveBodies.Contains(p.Body));
            foreach (int id in _bodyCells.Keys) if (!_liveBodies.Contains(id)) _bodyCells.Remove(id);
        }
        UpdateObjects(poseOf,transparentLeaves);
        Commit();
        _dirty = false;
    }
    private void SyncTerrain(VoxelMap map,bool transparentLeaves)
    {
        if (map.ColumnVersion != _columnVersion)
        {
            _columnVersion = map.ColumnVersion;
            _nextLoaded.Clear();
            if (map.LoadedColumns != null) _nextLoaded.UnionWith(map.LoadedColumns);
            foreach (ivec2 column in _loadedColumns) if (!_nextLoaded.Contains(column)) _dirtyColumns.Add(column);
            foreach (ivec2 column in _nextLoaded) if (!_loadedColumns.Contains(column)) _dirtyColumns.Add(column);
            (_loadedColumns,_nextLoaded) = (_nextLoaded,_loadedColumns);
            _dirty = true;
        }
        // Chunks that came or went without passing through Invalidate. Only a created or freed
        // chunk can make the terrain and the map disagree.
        if (map.Grid.ChunkRevision == _chunkRevision) return;
        _chunkRevision = map.Grid.ChunkRevision;
        foreach (var chunk in map.Grid.Chunks)
            if (!_terrain.ContainsKey(chunk.Coordinate)) Invalidate(map,chunk.Coordinate,transparentLeaves);
        List<ivec3>? gone = null;
        foreach (ivec3 key in _terrain.Keys) if (!map.Grid.TryGetChunk(key,out _)) (gone ??= []).Add(key);
        if (gone != null) foreach (ivec3 key in gone) RemoveTerrain(key);
    }
    private void UpdateObjects(Func<VoxelBody,(dvec3 Position,dquat Orientation)> poseOf,bool transparentLeaves)
    {
        bool terrainPoints = _emitterTerrain.Count != 0, objectPoints = _objectSources.Count != 0;
        if (!terrainPoints && !objectPoints && _objects.Count == 0) { _objectDirty = false; return; }
        // Object poses affect point emitters/occluders, never ambient reflection or transport.
        bool moved = _objectDirty || _bodies.Count != _poses.Count;
        _bodyPoses.Clear();
        foreach (VoxelBody body in _bodies)
        {
            var pose = poseOf(body); _bodyPoses.Add(pose);
            if (!_poses.TryGetValue(body.Id,out var previous) || (previous.Position-pose.Position).LengthSqr > .0625 || Math.Abs(dquat.Dot(previous.Rotation,pose.Orientation)) < .999) moved = true;
        }
        if (!moved && !_dirty) return;
        _objectDirty = false; _poses.Clear();
        for (int i = 0; i < _bodies.Count; i++) _poses[_bodies[i].Id] = _bodyPoses[i];
        _nextObjects.Clear();
        if (terrainPoints || objectPoints)
        {
            // Lit furnaces on objects are point sources wherever they are; other objects only
            // matter where some source's shadow rays reach.
            for (int i = 0; i < _bodies.Count; i++)
            {
                BodyCells cells = CellsOf(_bodies[i],transparentLeaves);
                if (cells.Emits) Voxelize(cells,_bodyPoses[i]);
            }
            _reachPoints.Clear();
            foreach (ivec3 key in _emitterTerrain) _reachPoints.AddRange(_terrain[key].Emitters);
            foreach (var (cell,value) in _nextObjects) if ((value&Emitting) != 0) _reachPoints.Add(cell);
            ivec3 low = new(int.MaxValue), high = new(int.MinValue);
            foreach (ivec3 p in _reachPoints) { low = ivec3.Min(low,p); high = ivec3.Max(high,p); }
            for (int i = 0; i < _bodies.Count; i++)
            {
                BodyCells cells = CellsOf(_bodies[i],transparentLeaves);
                if (!cells.Emits && cells.Cells.Length != 0 && InReach(cells,_bodyPoses[i],low,high)) Voxelize(cells,_bodyPoses[i]);
            }
        }
        bool changed = _nextObjects.Count != _objects.Count;
        foreach (var (cell,value) in _nextObjects)
            if (!_objects.TryGetValue(cell,out uint old) || old != value) { changed = true; _shadowChunks.Add(cell>>4); }
        foreach (ivec3 cell in _objects.Keys)
            if (!_nextObjects.ContainsKey(cell)) { changed = true; _shadowChunks.Add(cell>>4); }
        (_objects,_nextObjects) = (_nextObjects,_objects);
        if (!changed) return;
        _dynamicDirty = true;
        // Objects occlude direct point light, but do not obstruct ambient transport.
        // Only changes to their emitter cells can invalidate a sleeping ambient tile.
        _nextObjectEmitters.Clear();
        foreach (var (cell,value) in _objects) if ((value&Emitting) != 0) _nextObjectEmitters.Add(cell);
        if (_nextObjectEmitters.SetEquals(_objectEmitterCells)) return;
        foreach (ivec3 cell in _objectEmitterCells)
            if (!_nextObjectEmitters.Contains(cell)) _wakeColumns.Add((cell>>4).xy);
        foreach (ivec3 cell in _nextObjectEmitters)
            if (!_objectEmitterCells.Contains(cell)) _wakeColumns.Add((cell>>4).xy);
        (_objectEmitterCells,_nextObjectEmitters) = (_nextObjectEmitters,_objectEmitterCells);
        _pointsDirty = true;
        // Emitter bricks and their face neighbors hold light grid slots wherever they are.
        _nextEmitterWanted.Clear();
        foreach (ivec3 cell in _objectEmitterCells)
        {
            ivec3 key = cell>>4; _nextEmitterWanted.Add(key);
            foreach (ivec3 d in LightGrid.FaceDirections) _nextEmitterWanted.Add(key+d);
        }
        foreach (ivec3 key in _emitterWanted) if (!_nextEmitterWanted.Contains(key)) _dirtyKeys.Add(key);
        foreach (ivec3 key in _nextEmitterWanted) if (!_emitterWanted.Contains(key)) _dirtyKeys.Add(key);
        (_emitterWanted,_nextEmitterWanted) = (_nextEmitterWanted,_emitterWanted);
    }
    private BodyCells CellsOf(VoxelBody body,bool transparentLeaves)
    {
        if (!_bodyCells.TryGetValue(body.Id,out BodyCells? cached)) _bodyCells[body.Id] = cached = new();
        if (cached.Grid == body.Grid && cached.Revision == body.Grid.Revision && cached.TransparentLeaves == transparentLeaves) return cached;
        List<(ivec3 Cell,uint Value)> cells = [];
        dvec3 low = new(double.MaxValue), high = new(double.MinValue);
        bool emits = false;
        foreach (var (cell,v) in body.Grid.Occupied())
        {
            uint value = LightingScene.Mask(v,transparentLeaves) | (v.Type == BlockType.Furnace && v.Lit ? Emitting : 0u);
            if (value == 0) continue;
            cells.Add((cell,value)); emits |= (value&Emitting) != 0;
            dvec3 center = new dvec3(cell)+.5; low = dvec3.Min(low,center); high = dvec3.Max(high,center);
        }
        cached.Grid = body.Grid; cached.Revision = body.Grid.Revision; cached.TransparentLeaves = transparentLeaves;
        cached.Cells = [.. cells]; cached.Emits = emits;
        cached.Center = cells.Count == 0 ? dvec3.Zero : (low+high)*.5;
        cached.Radius = 0;
        foreach (var (cell,_) in cached.Cells) cached.Radius = Math.Max(cached.Radius,(new dvec3(cell)+.5-cached.Center).Length);
        return cached;
    }
    private void Voxelize(BodyCells cells,(dvec3 Position,dquat Orientation) pose)
    {
        foreach (var (cell,value) in cells.Cells)
        {
            ivec3 p = (ivec3)dvec3.Floor(pose.Position+pose.Orientation*(new dvec3(cell)+.5));
            _nextObjects[p] = _nextObjects.GetValueOrDefault(p)|value;
        }
    }
    private bool InReach(BodyCells cells,(dvec3 Position,dquat Orientation) pose,ivec3 low,ivec3 high)
    {
        if (_reachPoints.Count == 0) return false;
        dvec3 center = pose.Position+pose.Orientation*cells.Center;
        // Floored cell indices lie within a cell of the rotated cell centers.
        double reach = PointReach+cells.Radius+2;
        dvec3 outside = dvec3.Max(new dvec3(low)+.5-center,center-(new dvec3(high)+.5));
        if (Math.Max(outside.x,Math.Max(outside.y,outside.z)) > reach) return false;
        foreach (ivec3 p in _reachPoints)
        {
            dvec3 d = center-(new dvec3(p)+.5);
            if (Math.Max(Math.Abs(d.x),Math.Max(Math.Abs(d.y),Math.Abs(d.z))) <= reach) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Commit: slots, roofs, materials and points, then the mirror
    // ---------------------------------------------------------------------------------------

    private void Commit()
    {
        int airTop = 1;
        foreach (int z in _terrainLevels.Keys) airTop = Math.Max(airTop,z+1);
        if (airTop != _airTop)
        {
            // A completely mined-out column still contains air blocks with persistent light.
            _airTop = airTop;
            foreach (ivec2 column in _loadedColumns) if (!_columns.TryGetValue(column,out Column? c) || c.Terrain.Count == 0) _dirtyColumns.Add(column);
        }
        foreach (ivec2 key in _dirtyColumns) UpdateColumn(key);
        foreach (ivec3 key in _dirtyKeys)
        {
            bool wanted = Wanted(key), present = _slots.ContainsKey(key);
            if (wanted && !present) AddSlot(key); else if (!wanted && present) RemoveSlot(key);
            _touchedColumns.Add(key.xy);
        }
        foreach (ivec3 key in _dirtyMaterials)
        {
            _terrain.TryGetValue(key,out TerrainChunk? chunk);
            if (chunk?.Packed != null && chunk.Block < 0)
            {
                int type = MaterialClass(chunk.Packed[0]);
                if (_freeMaterials[type].Count != 0) chunk.Block = _freeMaterials[type].Pop();
                else { chunk.Block = _materialTop; _materialTop += MaterialBlockWords[type]; }
            }
            if (!_slots.TryGetValue(key,out int slot)) continue;
            _signatures[slot] = chunk?.Hash ?? AirSignature;
            _dirtyRecords.Add(slot);
        }
        foreach (ivec2 key in _touchedColumns)
            if (_columns.TryGetValue(key,out Column? column) && column.Terrain.Count == 0 && column.Slots.Count == 0 && column.Roof < 0) _columns.Remove(key);
        if (_pointsDirty) UpdatePoints();
        UpdateEmitterSlots();
        MarkShadows();
        bool changed = _dirtyColumns.Count+_dirtyKeys.Count+_dirtyMaterials.Count+_dirtyRecords.Count+_dirtyHashKeys.Count+_dirtyRoofs.Count != 0 || _dynamicDirty;
        if (_relayout || changed && (!IncrementalMetadata || Outgrown())) { Relayout(); Relayouts++; }
        else if (changed) { ApplyEdits(); Edits++; }
        if (changed || _relayout) _version++;
        _relayout = false;
        _dirtyColumns.Clear(); _dirtyKeys.Clear(); _dirtyMaterials.Clear(); _dirtyRecords.Clear();
        _dirtyHashKeys.Clear(); _dirtyRoofs.Clear(); _touchedColumns.Clear(); _dynamicDirty = false;
    }
    private (int Low,int High) WantedRange(ivec2 key)
    {
        if (_columns.TryGetValue(key,out Column? column) && column.Terrain.Count != 0)
        {
            int low = 0, high = int.MinValue;
            foreach (int z in column.Terrain) { low = Math.Min(low,z); high = Math.Max(high,z); }
            return (low,high+1);
        }
        return _loadedColumns.Contains(key) ? (0,_airTop) : (1,0);
    }
    private bool Wanted(ivec3 key)
    {
        var (low,high) = WantedRange(key.xy);
        return key.z >= low && key.z <= high || _emitterWanted.Contains(key);
    }
    private void UpdateColumn(ivec2 key)
    {
        _touchedColumns.Add(key); _dirtyRoofs.Add(key);
        if (_columns.TryGetValue(key,out Column? column) && (column.Terrain.Count != 0) != (column.Roof >= 0))
        {
            if (column.Roof < 0) column.Roof = _freeRoofs.Count != 0 ? _freeRoofs.Pop() : _roofTop++;
            else { _freeRoofs.Push(column.Roof); column.Roof = -1; }
            // Every brick of the column points at its roof, or at none.
            foreach (int z in column.Slots) _dirtyRecords.Add(_slots[new ivec3(key,z)]);
        }
        var (low,high) = WantedRange(key);
        if (column != null)
            for (int i = column.Slots.Count-1; i >= 0; i--)
            {
                ivec3 slotKey = new(key,column.Slots[i]);
                if ((slotKey.z < low || slotKey.z > high) && !_emitterWanted.Contains(slotKey)) RemoveSlot(slotKey);
            }
        for (int z = low; z <= high; z++) if (!_slots.ContainsKey(new ivec3(key,z))) AddSlot(new ivec3(key,z));
    }
    private void AddSlot(ivec3 key)
    {
        uint signature = _terrain.TryGetValue(key,out TerrainChunk? terrain) ? terrain.Hash : AirSignature;
        int slot;
        if (_archiving.Remove(key,out var archiving))
        {
            // Its light never left the GPU: take the slot back, unless the geometry changed meanwhile.
            slot = archiving.Slot;
            _archiveRequests.RemoveAll(r => r.Token == archiving.Token);
            _saved.Remove(key);
            if (_signatures[slot] != signature) _reset.Add(slot);
            _keys[slot] = key;
        }
        else
        {
            slot = _free.Count == 0 ? _keys.Count : _free.Pop();
            if (slot == _keys.Count) { _keys.Add(key); _signatures.Add(0); } else _keys[slot] = key;
            _reset.Add(slot);
        }
        _signatures[slot] = signature;
        _slots[key] = slot; ColumnAt(key.xy).Slots.Add(key.z);
        _wakeColumns.Add(key.xy);
        SlotChanged(key,slot);
    }
    private void RemoveSlot(ivec3 key)
    {
        int slot = _slots[key];
        _slots.Remove(key); _columns[key.xy].Slots.Remove(key.z); _keys[slot] = null;
        SlotChanged(key,slot);
        if (_reset.Remove(slot) || _capacity == 0) { _free.Push(slot); return; }
        long token = ++_archiveTokens;
        _archiving[key] = (slot,token);
        _archiveRequests.Add(new(key,slot,_signatures[slot],token));
    }
    private void SlotChanged(ivec3 key,int slot)
    {
        _dirtyRecords.Add(slot); _dirtyHashKeys.Add(key);
        foreach (ivec3 d in LightGrid.FaceDirections) if (_slots.TryGetValue(key+d,out int neighbor)) _dirtyRecords.Add(neighbor);
    }
    private void UpdatePoints()
    {
        _pointsDirty = false;
        _nextPoints.Clear();
        foreach (ivec3 key in _emitterTerrain) _nextPoints.UnionWith(_terrain[key].Emitters);
        _nextPoints.UnionWith(_objectEmitterCells);
        for (int i = 0; i < _pointCells.Count; i++)
            if (_pointCells[i] is ivec3 cell && !_nextPoints.Contains(cell))
            { _pointSlots.Remove(cell); _pointCells[i] = null; _pointDirty[i] = false; _freePoints.Push(i); _dynamicDirty = true; }
        foreach (ivec3 cell in _nextPoints)
        {
            if (_pointSlots.ContainsKey(cell)) continue;
            int slot = _freePoints.Count != 0 ? _freePoints.Pop() : _pointCells.Count;
            if (slot == _pointCells.Count) { _pointCells.Add(cell); _pointDirty.Add(true); }
            else { _pointCells[slot] = cell; _pointDirty[slot] = true; }
            _pointSlots[cell] = slot; _dynamicDirty = true;
        }
    }
    private void UpdateEmitterSlots()
    {
        _nextEmitterSlots.Clear();
        foreach (ivec3 cell in _objectEmitterCells) if (_slots.TryGetValue(cell>>4,out int slot)) _nextEmitterSlots.Add(slot);
        if (_nextEmitterSlots.SetEquals(_emitterSlots)) return;
        foreach (int slot in _emitterSlots) if (!_nextEmitterSlots.Contains(slot)) _dirtyRecords.Add(slot);
        foreach (int slot in _nextEmitterSlots) if (!_emitterSlots.Contains(slot)) _dirtyRecords.Add(slot);
        (_emitterSlots,_nextEmitterSlots) = (_nextEmitterSlots,_emitterSlots);
    }
    /// <summary>Marks the point sources whose shadow rays reach a chunk whose occluders changed.</summary>
    private void MarkShadows()
    {
        if (!IncrementalMetadata) for (int i = 0; i < _pointDirty.Count; i++) _pointDirty[i] = true;
        if (_shadowChunks.Count == 0) return;
        for (int i = 0; i < _pointCells.Count; i++)
        {
            if (_pointDirty[i] || _pointCells[i] is not ivec3 p) continue;
            foreach (ivec3 chunk in _shadowChunks)
            {
                ivec3 low = chunk*16, gap = ivec3.Max(ivec3.Max(low-p,p-(low+15)),ivec3.Zero);
                if (Math.Max(gap.x,Math.Max(gap.y,gap.z)) <= PointReach) { _pointDirty[i] = true; break; }
            }
        }
        _shadowChunks.Clear();
    }

    // ---------------------------------------------------------------------------------------
    // The mirror
    // ---------------------------------------------------------------------------------------

    private bool Outgrown() => _slots.Count*2 > _hashCapacity || _keys.Count > _recordCapacity || _roofTop > _roofCapacity
        || _roofTop*2 > _roofHashCapacity || _materialTop > _materialCapacity || _pointCells.Count > _pointCapacity
        || ObjectHashCapacity(_objects.Count)*4 > _objectRegion;
    private void Relayout()
    {
        // Pack the roofs and materials of live chunks together, leaving no holes behind.
        _roofTop = 0; _freeRoofs.Clear();
        foreach (Column column in _columns.Values) column.Roof = column.Terrain.Count != 0 ? _roofTop++ : -1;
        _materialTop = 0; foreach (Stack<int> free in _freeMaterials) free.Clear();
        foreach (TerrainChunk chunk in _terrain.Values)
            if (chunk.Packed != null) { chunk.Block = _materialTop; _materialTop += MaterialBlockWords[MaterialClass(chunk.Packed[0])]; }
        _hashCapacity = PowerOfTwo(Math.Max(16,_slots.Count*4));
        _recordCapacity = PowerOfTwo(Math.Max(64,_keys.Count+_keys.Count/2));
        _roofHashCapacity = PowerOfTwo(Math.Max(16,_roofTop*4));
        _roofCapacity = Math.Max(16,_roofTop*2);
        _materialCapacity = Math.Max(1<<14,_materialTop*2);
        _pointCapacity = PowerOfTwo(Math.Max(4,_pointCells.Count*2));
        _objectRegion = ObjectHashCapacity(_objects.Count)*8;
        _hashStart = HeaderWords;
        _recordStart = _hashStart+_hashCapacity*4;
        _reflectanceStart = _recordStart+_recordCapacity*RecordWords;
        _roofHashStart = _reflectanceStart+ReflectanceWords;
        _roofStart = _roofHashStart+_roofHashCapacity*4;
        _materialStart = _roofStart+_roofCapacity*RoofWords;
        _pointStart = _materialStart+_materialCapacity;
        _objectStart = _pointStart+_pointCapacity*4;
        int total = _objectStart+_objectRegion;
        if (_words.Length != total) _words = new uint[total]; else Array.Clear(_words);
        _dirtyPages = new ulong[((total>>PageShift)+64)/64];
        for (int i = 0; i < 256; i++)
        {
            int e = _reflectanceStart+i*4; vec3 color = _reflectance[i];
            _words[e] = Float(color.x); _words[e+1] = Float(color.y); _words[e+2] = Float(color.z);
        }
        foreach (var (key,slot) in _slots) HashPut(key,(uint)slot+1);
        for (int slot = 0; slot < _keys.Count; slot++) WriteRecord(slot);
        foreach (var (key,column) in _columns) if (column.Roof >= 0) { RoofPut(key,(uint)(_roofStart+column.Roof*RoofWords)); WriteRoof(key,column); }
        foreach (TerrainChunk chunk in _terrain.Values) chunk.Packed?.CopyTo(_words,_materialStart+chunk.Block);
        WriteDynamic();
        WriteHeader();
        MarkDirty(0,total);
    }
    private void ApplyEdits()
    {
        foreach (ivec3 key in _dirtyHashKeys) HashPut(key,_slots.TryGetValue(key,out int slot) ? (uint)slot+1 : 0u);
        foreach (ivec2 key in _dirtyRoofs)
        {
            Column? column = _columns.GetValueOrDefault(key);
            if (column == null || column.Roof < 0) { RoofPut(key,0); continue; }
            RoofPut(key,(uint)(_roofStart+column.Roof*RoofWords)); WriteRoof(key,column);
        }
        foreach (ivec3 key in _dirtyMaterials)
            if (_terrain.TryGetValue(key,out TerrainChunk? chunk) && chunk.Packed != null)
            { chunk.Packed.CopyTo(_words,_materialStart+chunk.Block); MarkDirty(_materialStart+chunk.Block,chunk.Packed.Length); }
        foreach (int slot in _dirtyRecords) WriteRecord(slot);
        if (_dynamicDirty) WriteDynamic();
        WriteHeader();
    }
    private void MarkDirty(int start,int count)
    {
        if (count <= 0) return;
        for (int page = start>>PageShift, last = (start+count-1)>>PageShift; page <= last; page++) _dirtyPages[page>>6] |= 1UL<<(page&63);
        _anyDirty = true;
    }
    private void Set(int index,uint value) { if (_words[index] != value) { _words[index] = value; MarkDirty(index,1); } }
    private void WriteHeader()
    {
        Set(0,(uint)_hashCapacity-1); Set(1,(uint)_recordStart); Set(2,(uint)_keys.Count); Set(3,(uint)_reflectanceStart);
        Set(4,(uint)_pointCells.Count); Set(5,(uint)_pointStart);
        Set(6,(uint)ObjectHashCapacity(_objects.Count)-1); Set(7,(uint)_objectStart);
        Set(8,(uint)_roofHashCapacity-1); Set(9,(uint)_roofHashStart);
    }
    private void WriteRecord(int slot)
    {
        int r = _recordStart+slot*RecordWords;
        if (_keys[slot] is not ivec3 key)
        {
            for (int i = 0; i < RecordWords; i++) Set(r+i,0);
            return;
        }
        TerrainChunk? material = _terrain.GetValueOrDefault(key);
        Column? column = _columns.GetValueOrDefault(key.xy);
        Set(r,(uint)key.x); Set(r+1,(uint)key.y); Set(r+2,(uint)key.z);
        Set(r+3,material?.Packed != null ? (uint)(_materialStart+material.Block) : 0u);
        Set(r+4,material != null && material.Packed == null ? material.Uniform : 0u);
        for (int d = 0; d < 6; d++) Set(r+5+d,_slots.TryGetValue(key+LightGrid.FaceDirections[d],out int neighbor) ? (uint)neighbor+1 : 0u);
        Set(r+11,1); Set(r+12,column != null && column.Roof >= 0 ? (uint)(_roofStart+column.Roof*RoofWords) : 0u);
        Set(r+13,_emitterSlots.Contains(slot) ? 1u : 0u); Set(r+14,material?.Occupancy ?? 1u); Set(r+15,0);
    }
    private void WriteRoof(ivec2 key,Column column)
    {
        // Roof fallback also covers air outside explicitly allocated vertical chunks.
        int start = _roofStart+column.Roof*RoofWords;
        Span<int> roof = stackalloc int[RoofWords];
        roof.Fill(int.MinValue);
        foreach (int z in column.Terrain)
        {
            sbyte[] tops = _terrain[new ivec3(key,z)].Tops;
            for (int i = 0; i < RoofWords; i++) if (tops[i] >= 0) roof[i] = Math.Max(roof[i],z*16+tops[i]);
        }
        for (int i = 0; i < RoofWords; i++) Set(start+i,unchecked((uint)roof[i]));
    }
    private void WriteDynamic()
    {
        // A removed source leaves a hole, flagged in its fourth word, so the others keep their depth maps.
        for (int i = 0; i < _pointCells.Count; i++)
        {
            int e = _pointStart+i*4; ivec3 p = _pointCells[i] ?? ivec3.Zero;
            Set(e,(uint)p.x); Set(e+1,(uint)p.y); Set(e+2,(uint)p.z); Set(e+3,_pointCells[i] == null ? 1u : 0u);
        }
        int capacity = ObjectHashCapacity(_objects.Count);
        Span<uint> table = _words.AsSpan(_objectStart,capacity*4);
        table.Clear();
        foreach (var (p,value) in _objects)
        {
            uint h = Hash(p)&(uint)(capacity-1); while (table[(int)h*4+3] != 0) h = (h+1)&(uint)(capacity-1);
            int e = (int)h*4; table[e] = (uint)p.x; table[e+1] = (uint)p.y; table[e+2] = (uint)p.z; table[e+3] = value;
        }
        MarkDirty(_objectStart,capacity*4);
    }
    private void HashPut(ivec3 key,uint slot) => TablePut(_hashStart,_hashCapacity,key,3,slot);
    private void RoofPut(ivec2 key,uint offset) => TablePut(_roofHashStart,_roofHashCapacity,new ivec3(key,0),2,offset);
    /// <summary>
    /// Sets a key's value in a linear-probing table of four-word entries, or removes the key for 0.
    /// <paramref name="valueWord"/> 3 keys by xyz, 2 by xy. Removal shifts the rest of the probe
    /// run back, so lookups still stop at the first gap.
    /// </summary>
    private void TablePut(int start,int capacity,ivec3 key,int valueWord,uint value)
    {
        int mask = capacity-1, h = (int)(Hash(key)&(uint)mask);
        while (true)
        {
            int e = start+h*4;
            if (_words[e+valueWord] == 0)
            {
                if (value == 0) return;
                Set(e,(uint)key.x); Set(e+1,(uint)key.y); if (valueWord == 3) Set(e+2,(uint)key.z); Set(e+valueWord,value);
                return;
            }
            if (_words[e] == (uint)key.x && _words[e+1] == (uint)key.y && (valueWord == 2 || _words[e+2] == (uint)key.z))
            {
                if (value != 0) { Set(e+valueWord,value); return; }
                for (int hole = h, next = h; ; hole = next)
                {
                    for (int w = 0; w < 4; w++) Set(start+hole*4+w,0);
                    while (true)
                    {
                        next = (next+1)&mask;
                        int n = start+next*4;
                        if (_words[n+valueWord] == 0) return;
                        int home = (int)(Hash(new ivec3((int)_words[n],(int)_words[n+1],valueWord == 3 ? (int)_words[n+2] : 0))&(uint)mask);
                        bool stays = hole <= next ? hole < home && home <= next : hole < home || home <= next;
                        if (!stays) break;
                    }
                    for (int w = 0; w < 4; w++) Set(start+hole*4+w,_words[start+next*4+w]);
                }
            }
            h = (h+1)&mask;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Recording
    // ---------------------------------------------------------------------------------------

    private static void Barrier(Vk vk,CommandBuffer cmd,PipelineStageFlags source,AccessFlags sourceAccess,PipelineStageFlags dest,AccessFlags destAccess)
    {
        MemoryBarrier b = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = sourceAccess, DstAccessMask = destAccess };
        vk.CmdPipelineBarrier(cmd,source,dest,0,1,&b,0,null,0,null);
    }
    /// <summary>After this frame slot's fence: timings, finished archives and buffer sizes.</summary>
    public void Prepare(uint frame = 0)
    {
        if (_timed[frame])
        {
            ulong* ticks = stackalloc ulong[3];
            if (Vk.GetQueryPoolResults(Device,_timing,frame*3,3,24,ticks,8,QueryResultFlags.Result64Bit) == Result.Success)
            {
                DiffuseMilliseconds = (ticks[1]-ticks[0])*_timestampPeriod/1e6;
                ShadowMilliseconds = (ticks[2]-ticks[1])*_timestampPeriod/1e6;
                if (_workReadback[frame].Buffer.Handle != 0)
                {
                    uint count = _workReadback[frame].As<uint>()[3];
                    ActiveTiles = count == uint.MaxValue ? _keys.Count * TilesPerChunk : (int)count;
                    int budget = _recordedBudget[frame];
                    if (budget > 0 && AdaptiveTileBudget && ActiveTiles >= budget * .8)
                    {
                        double ratio = Math.Clamp(2.0 / Math.Max(.1, DiffuseMilliseconds), .8, 1.2);
                        TileBudget = Math.Clamp((int)(budget * ratio) / 128 * 128, 128, 4096);
                    }
                }
            }
        }
        for (int i = _pendingArchives.Count-1; i >= 0; i--)
            if (_pendingArchives[i].FrameIndex == frame) { Complete(_pendingArchives[i]); _pendingArchives.RemoveAt(i); }
        for (int i = _retired.Count-1; i >= 0; i--)
            if (_retired[i].Frame+GraphicsContext.FramesInFlight <= _frame) { Destroy(_retired[i].Buffer); _retired.RemoveAt(i); }
        if (_metadata.Size < (ulong)Math.Max(64,_words.Length*4))
        {
            Retire(_metadata);
            _metadata = Allocate(BitOperations.RoundUpToPowerOf2((ulong)Math.Max(256,_words.Length*4)));
            MarkDirty(0,_words.Length);
        }
        if (_workReadback[frame].Buffer.Handle == 0) _workReadback[frame] = _graphics.CreateReadbackBuffer(16);
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(16,_keys.Count));
        if (capacity > _capacity)
        {
            // Growth preserves every existing light value. No reconstruction from a CPU frontier.
            DeviceBuffer[] next = [Allocate((ulong)capacity*ChunkBytes),Allocate((ulong)capacity*ChunkBytes)];
            for (int i = 0; i < 2; i++) _lightGrowth.Add((_light[i], next[i]));
            _light = next; _capacity = capacity;
            Retire(_activity); _activity = Allocate((ulong)capacity*TilesPerChunk*8); _wakeAll = true;
            Retire(_work); _work = Allocate(16+(ulong)capacity*TilesPerChunk*8,BufferUsageFlags.IndirectBufferBit);
        }
        if (_pointDepthCapacity < Math.Max(1,_pointCells.Count))
        {
            Retire(_pointDepth);
            _pointDepthCapacity = PowerOfTwo(Math.Max(1,_pointCells.Count));
            _pointDepth = Allocate((ulong)_pointDepthCapacity*PointShadowSamples*4);
            for (int i = 0; i < _pointDirty.Count; i++) _pointDirty[i] = _pointCells[i] != null;
        }
    }
    public void Record(Frame frame,int iterations = 2)
    {
        if ((iterations&1) != 0 || iterations < 2) throw new ArgumentOutOfRangeException(nameof(iterations));
        _frame++; CommandBuffer cmd = frame.Commands; uint f = frame.FrameIndex;
        Bind(f);
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.FragmentShaderBit|PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit|AccessFlags.TransferWriteBit,
            PipelineStageFlags.TransferBit|PipelineStageFlags.ComputeShaderBit,AccessFlags.TransferReadBit|AccessFlags.TransferWriteBit|AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        RecordLightGrowth(cmd);
        RecordArchives(cmd,f);
        _restores.Clear(); _zeroSlots.Clear();
        foreach (int slot in _reset)
        {
            if (_keys[slot] is ivec3 key && _saved.Remove(key,out LightChunkState? saved) && saved.GeometryHash == _signatures[slot])
                _restores.Add((slot,saved));
            else _zeroSlots.Add(slot);
        }
        _reset.Clear();
        RecordUploads(cmd,f);
        _zeroSlots.Sort();
        for (int first = 0, end; first < _zeroSlots.Count; first = end)
        {
            for (end = first+1; end < _zeroSlots.Count && _zeroSlots[end] == _zeroSlots[end-1]+1; end++) { }
            foreach (DeviceBuffer buffer in _light) Vk.CmdFillBuffer(cmd,buffer.Buffer,(ulong)_zeroSlots[first]*ChunkBytes,(ulong)(end-first)*ChunkBytes,0);
        }
        // A changed roof affects its whole column; the adjoining columns may read its
        // cells as their halo. New/restored slots and moved emitters use the same path;
        // global lighting settings and buffer growth wake everything.
        bool budgeted = BudgetedDiffusion && CompactDispatch && ReuseUnchanged && iterations == 2;
        bool wake = _wakeAll || _activityConfiguration != _configurationVersion || budgeted != _lastBudgeted;
        _lastBudgeted = budgeted;
        _wakeAll = false; _activityConfiguration = _configurationVersion;
        if (!budgeted)
        {
            if (!wake && _wakeColumns.Count != 0) RecordWake(cmd);
            _wakeColumns.Clear();
        }
        Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.FragmentShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        DescriptorSet set = _sets[f];
        Parameters parameters = new() { Sky = new vec4(_sky,0), Blocks = _blocks ? 1u : 0u, Sun = _sun ? 1u : 0u, Count = (uint)_keys.Count, SunDirection = _sunDirection, SunColor = _sunColor };
        Vk.CmdResetQueryPool(cmd,_timing,f*3,3);
        Vk.CmdWriteTimestamp(cmd,PipelineStageFlags.ComputeShaderBit,_timing,f*3);
        if (budgeted)
        {
            parameters.Focus = new vec4(FocusPosition, 128);
            parameters.Schedule = new uvec4((uint)Math.Max(1, TileBudget), _budgetCursor, 0, (uint)(4 + _capacity * TilesPerChunk));
            RecordBudgetedTiles(cmd, set, parameters, wake);
            _budgetCursor = (_budgetCursor + (uint)Math.Max(1, TileBudget / 4)) % (uint)Math.Max(1, _keys.Count * TilesPerChunk);
        }
        _recordedBudget[f] = budgeted ? Math.Max(1, TileBudget) : 0;
        for (int i = 0; i < iterations; i++)
        {
            if (!budgeted) PrepareActivity(cmd,i,i == 0 && wake);
            parameters.Iteration = (uint)i;
            if (!budgeted && CompactDispatch) RecordActiveTiles(cmd,set,parameters);
            Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_diffuse.Pipeline);
            Vk.CmdBindDescriptorSets(cmd,PipelineBindPoint.Compute,_diffuse.Layout,0,1,&set,0,null);
            Vk.CmdPushConstants(cmd,_diffuse.Layout,ShaderStageFlags.ComputeBit,0,(uint)sizeof(Parameters),&parameters);
            if (CompactDispatch) Vk.CmdDispatchIndirect(cmd,_work.Buffer,0);
            else if (_keys.Count != 0) Vk.CmdDispatch(cmd,8,(uint)Math.Min(_keys.Count,65535),(uint)((_keys.Count+65534)/65535));
            Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        }
        Vk.CmdWriteTimestamp(cmd,PipelineStageFlags.ComputeShaderBit,_timing,f*3+1);
        if (_blocks) RecordPointShadows(cmd,set,parameters);
        Vk.CmdWriteTimestamp(cmd,PipelineStageFlags.ComputeShaderBit,_timing,f*3+2);
        _timed[f] = true;
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.DrawIndirectBit,
            AccessFlags.ShaderWriteBit|AccessFlags.IndirectCommandReadBit,PipelineStageFlags.TransferBit,AccessFlags.TransferReadBit);
        BufferCopy dispatchReadback = new(0,0,16);
        Vk.CmdCopyBuffer(cmd,_work.Buffer,_workReadback[f].Buffer,1,&dispatchReadback);
        Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.FragmentShaderBit,AccessFlags.ShaderReadBit);
    }
    // Preserve both Jacobi buffers on the graphics queue, ahead of this frame's work.
    // Old descriptors and copy sources remain alive until their frame fences have passed.
    private void RecordLightGrowth(CommandBuffer cmd)
    {
        foreach (var (source, target) in _lightGrowth)
        {
            Vk.CmdFillBuffer(cmd,target.Buffer,0,target.Size,0);
            Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,
                PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit);
            if (source.Buffer.Handle != 0)
            {
                BufferCopy copy = new(0,0,source.Size);
                Vk.CmdCopyBuffer(cmd,source.Buffer,target.Buffer,1,&copy);
                Retire(source);
            }
            Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,
                PipelineStageFlags.TransferBit|PipelineStageFlags.ComputeShaderBit,
                AccessFlags.TransferReadBit|AccessFlags.TransferWriteBit|AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        }
        _lightGrowth.Clear();
    }
    private void Bind(uint frame)
    {
        if (_bound[frame] == _allocations) return;
        _bound[frame] = _allocations;
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[BindingCount];
        buffers[0] = new() { Buffer = _metadata.Buffer, Range = _metadata.Size };
        buffers[1] = new() { Buffer = _light[0].Buffer, Range = _light[0].Size };
        buffers[2] = new() { Buffer = _light[1].Buffer, Range = _light[1].Size };
        buffers[3] = new() { Buffer = _pointDepth.Buffer, Range = _pointDepth.Size };
        buffers[4] = new() { Buffer = _activity.Buffer, Range = _activity.Size };
        buffers[5] = new() { Buffer = _work.Buffer, Range = _work.Size };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[BindingCount];
        for (int i = 0; i < BindingCount; i++) writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _sets[frame], DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers+i };
        Vk.UpdateDescriptorSets(Device,BindingCount,writes,0,null);
    }
    /// <summary>Changed metadata pages and restored bricks, through this frame slot's staging buffer.</summary>
    private void RecordUploads(CommandBuffer cmd,uint frame)
    {
        _copies.Clear(); ulong bytes = 0;
        if (_anyDirty)
            for (int page = 0, pages = (_words.Length+(1<<PageShift)-1)>>PageShift; page < pages; page++)
            {
                if ((_dirtyPages[page>>6]&(1UL<<(page&63))) == 0) continue;
                int start = page<<PageShift, end = page+1;
                while (end < pages && (_dirtyPages[end>>6]&(1UL<<(end&63))) != 0) end++;
                ulong size = (ulong)(Math.Min(end<<PageShift,_words.Length)-start)*4;
                _copies.Add(new(bytes,(ulong)start*4,size)); bytes += size; page = end-1;
            }
        int metadataCopies = _copies.Count;
        UploadedBytes = (int)bytes;
        ulong total = bytes+(ulong)_restores.Count*ChunkBytes;
        if (total == 0) return;
        HostBuffer staging = _uploads[frame];
        // This slot's fence has passed: its staging buffer is free to reuse, grow or shrink.
        if (staging.Size < total || staging.Size > 4*total && staging.Size > 16UL<<20)
        {
            _graphics.Destroy(staging);
            _uploads[frame] = staging = _graphics.CreateHostBuffer(BitOperations.RoundUpToPowerOf2(Math.Max(total,1UL<<16)),BufferUsageFlags.TransferSrcBit);
        }
        Span<byte> target = staging.As<byte>();
        foreach (BufferCopy copy in _copies) MemoryMarshal.AsBytes(_words.AsSpan((int)(copy.DstOffset/4),(int)(copy.Size/4))).CopyTo(target[(int)copy.SrcOffset..]);
        if (metadataCopies != 0)
        {
            fixed (BufferCopy* regions = CollectionsMarshal.AsSpan(_copies)) Vk.CmdCopyBuffer(cmd,staging.Buffer,_metadata.Buffer,(uint)metadataCopies,regions);
            Array.Clear(_dirtyPages); _anyDirty = false;
        }
        Span<uint> values = MemoryMarshal.Cast<byte,uint>(target[(int)bytes..]);
        for (int i = 0; i < _restores.Count; i++)
        {
            var (slot,saved) = _restores[i]; Span<uint> chunk = values.Slice(i*4096,4096);
            if (saved.Rgb.Length == 1) chunk.Fill(saved.Rgb[0]); else saved.Rgb.CopyTo(chunk);
            BufferCopy copy = new(bytes+(ulong)i*ChunkBytes,(ulong)slot*ChunkBytes,ChunkBytes);
            foreach (DeviceBuffer buffer in _light) Vk.CmdCopyBuffer(cmd,staging.Buffer,buffer.Buffer,1,&copy);
        }
    }
    private void RecordWake(CommandBuffer cmd, bool urgent = false)
    {
        _wakeSlots.Clear();
        foreach (ivec2 column in _wakeColumns)
            for (int n = 0; n < 5; n++)
            {
                ivec2 key = n switch { 0 => column, 1 => column+new ivec2(1,0), 2 => column-new ivec2(1,0), 3 => column+new ivec2(0,1), _ => column-new ivec2(0,1) };
                if (_columns.TryGetValue(key,out Column? c)) foreach (int z in c.Slots) _wakeSlots.Add(_slots[new ivec3(key,z)]);
            }
        _wakeSlots.Sort();
        for (int first = 0, end; first < _wakeSlots.Count; first = end)
        {
            for (end = first+1; end < _wakeSlots.Count && _wakeSlots[end] <= _wakeSlots[end-1]+1; end++) { }
            Vk.CmdFillBuffer(cmd,_activity.Buffer,(ulong)_wakeSlots[first]*TilesPerChunk*4,(ulong)(_wakeSlots[end-1]-_wakeSlots[first]+1)*TilesPerChunk*4,urgent ? 3u : 1u);
        }
    }
    private void RecordPointShadows(CommandBuffer cmd,DescriptorSet set,Parameters parameters)
    {
        bool bound = false;
        for (int first = 0, end; first < _pointCells.Count; first = end)
        {
            end = first+1;
            if (!_pointDirty[first] || _pointCells[first] == null) continue;
            while (end < _pointCells.Count && _pointDirty[end] && _pointCells[end] != null) end++;
            if (!bound)
            {
                Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_shadows.Pipeline);
                Vk.CmdBindDescriptorSets(cmd,PipelineBindPoint.Compute,_shadows.Layout,0,1,&set,0,null);
                bound = true;
            }
            // The shadow kernel traces sources from its iteration on.
            parameters.Iteration = (uint)first;
            Vk.CmdPushConstants(cmd,_shadows.Layout,ShaderStageFlags.ComputeBit,0,(uint)sizeof(Parameters),&parameters);
            Vk.CmdDispatch(cmd,24,(uint)(end-first),1);
            for (int i = first; i < end; i++) _pointDirty[i] = false;
        }
        if (bound) PointShadowDispatches++;
    }
    private void PrepareActivity(CommandBuffer cmd,int iteration,bool wake)
    {
        // Fixed bank offsets preserve activity when slots are added within capacity.
        ulong flagBytes = _activity.Size/2;
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.DrawIndirectBit|PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit|AccessFlags.IndirectCommandReadBit|AccessFlags.TransferWriteBit,
            PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit);
        Vk.CmdFillBuffer(cmd,_work.Buffer,12,4,CompactDispatch ? 0u : uint.MaxValue);
        if (wake || !ReuseUnchanged)
            Vk.CmdFillBuffer(cmd,_activity.Buffer,(ulong)(iteration&1)*flagBytes,flagBytes,1);
        Vk.CmdFillBuffer(cmd,_activity.Buffer,(ulong)(1-(iteration&1))*flagBytes,flagBytes,0);
        Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,
            PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
    }
    private void RecordBudgetedTiles(CommandBuffer cmd, DescriptorSet set, Parameters parameters, bool wake)
    {
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.DrawIndirectBit|PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit|AccessFlags.IndirectCommandReadBit|AccessFlags.TransferReadBit|AccessFlags.TransferWriteBit,
            PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit);
        Vk.CmdFillBuffer(cmd,_work.Buffer,12,4,0);
        Vk.CmdFillBuffer(cmd,_work.Buffer,(ulong)parameters.Schedule.w*4,(ulong)_capacity*TilesPerChunk*4,0);
        if (wake) Vk.CmdFillBuffer(cmd,_activity.Buffer,0,_activity.Size/2,1);
        // Urgent column ranges overwrite the global wake fill in the same bank.
        if (wake && _wakeColumns.Count != 0)
            Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit);
        if (_wakeColumns.Count != 0) RecordWake(cmd,urgent:true);
        _wakeColumns.Clear();
        Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,
            PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        uint totalBudget = parameters.Schedule.x;
        // A reserved contiguous quarter-window guarantees progress for every
        // pending tile even under continuous urgent edits. The final pass fills
        // spare capacity opportunistically without stealing that fair quota.
        for (uint pass = 0; pass < 3; pass++)
        {
            parameters.Schedule.x = pass == 0 ? Math.Max(1u,totalBudget*3/4) : totalBudget;
            parameters.Schedule.z = pass;
            Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_selectTiles.Pipeline);
            Vk.CmdBindDescriptorSets(cmd,PipelineBindPoint.Compute,_selectTiles.Layout,0,1,&set,0,null);
            Vk.CmdPushConstants(cmd,_selectTiles.Layout,ShaderStageFlags.ComputeBit,0,(uint)sizeof(Parameters),&parameters);
            if (_keys.Count != 0) Vk.CmdDispatch(cmd,(uint)((_keys.Count*TilesPerChunk+63)/64),1,1);
            Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,
                PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        }
        Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_dispatchTiles.Pipeline);
        Vk.CmdDispatch(cmd,1,1,1);
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,
            PipelineStageFlags.DrawIndirectBit|PipelineStageFlags.ComputeShaderBit,
            AccessFlags.IndirectCommandReadBit|AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
    }

    private void RecordActiveTiles(CommandBuffer cmd,DescriptorSet set,Parameters parameters)
    {
        // One small invocation per tile builds a dense list. Sleeping tiles never launch
        // the 512-invocation diffusion workgroup and need no CPU readback or scheduling.
        Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_selectTiles.Pipeline);
        Vk.CmdBindDescriptorSets(cmd,PipelineBindPoint.Compute,_selectTiles.Layout,0,1,&set,0,null);
        Vk.CmdPushConstants(cmd,_selectTiles.Layout,ShaderStageFlags.ComputeBit,0,(uint)sizeof(Parameters),&parameters);
        if (_keys.Count != 0) Vk.CmdDispatch(cmd,(uint)((_keys.Count*TilesPerChunk+63)/64),1,1);
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,
            PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        Vk.CmdBindPipeline(cmd,PipelineBindPoint.Compute,_dispatchTiles.Pipeline);
        Vk.CmdDispatch(cmd,1,1,1);
        Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,
            PipelineStageFlags.DrawIndirectBit|PipelineStageFlags.ComputeShaderBit,
            AccessFlags.IndirectCommandReadBit|AccessFlags.ShaderReadBit);
    }

    // ---------------------------------------------------------------------------------------
    // Archives: light of bricks leaving the grid, for when they come back and for saves
    // ---------------------------------------------------------------------------------------

    /// <summary>Copies out the bricks removed since the last frame; their slots stay reserved until it lands.</summary>
    private void RecordArchives(CommandBuffer cmd,uint frame)
    {
        if (_archiveRequests.Count == 0) return;
        ArchiveEntry[] entries = [.. _archiveRequests]; _archiveRequests.Clear();
        HostBuffer readback = _graphics.CreateReadbackBuffer((ulong)entries.Length*ChunkBytes);
        BufferCopy* regions = stackalloc BufferCopy[256];
        for (int first = 0; first < entries.Length; first += 256)
        {
            int count = Math.Min(256,entries.Length-first);
            for (int i = 0; i < count; i++) regions[i] = new((ulong)entries[first+i].Slot*ChunkBytes,(ulong)(first+i)*ChunkBytes,ChunkBytes);
            Vk.CmdCopyBuffer(cmd,_light[0].Buffer,readback.Buffer,(uint)count,regions);
        }
        Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
        _pendingArchives.Add(new(readback,frame,_epoch,entries));
    }
    private void Complete(PendingArchive archive)
    {
        if (archive.Epoch == _epoch)
        {
            Span<uint> data = archive.Buffer.As<uint>();
            for (int i = 0; i < archive.Entries.Length; i++) Store(archive.Entries[i],data.Slice(i*4096,4096));
        }
        _graphics.Destroy(archive.Buffer);
    }
    private void Store(ArchiveEntry entry,ReadOnlySpan<uint> values)
    {
        // A key that came back meanwhile kept its slot, and its light with it.
        if (entry.Token != 0)
        {
            if (!_archiving.TryGetValue(entry.Key,out var archiving) || archiving.Token != entry.Token) return;
            _archiving.Remove(entry.Key);
            _free.Push(entry.Slot);
        }
        _saved[entry.Key] = new(entry.Key,entry.Signature,values.IndexOfAnyExcept(values[0]) < 0 ? [values[0]] : values.ToArray());
    }
    /// <summary>Copies bricks to the CPU in batches and waits for each; for saves.</summary>
    private void ArchiveNow(List<ArchiveEntry> entries)
    {
        const int Batch = 1024;
        if (entries.Count == 0) return;
        HostBuffer readback = _graphics.CreateReadbackBuffer((ulong)Math.Min(entries.Count,Batch)*ChunkBytes);
        try
        {
            for (int first = 0; first < entries.Count; first += Batch)
            {
                int count = Math.Min(Batch,entries.Count-first);
                _graphics.Submit(cmd =>
                {
                    Barrier(Vk,cmd,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.TransferBit,AccessFlags.TransferReadBit);
                    for (int i = 0; i < count; i++) { BufferCopy copy = new((ulong)entries[first+i].Slot*ChunkBytes,(ulong)i*ChunkBytes,ChunkBytes); Vk.CmdCopyBuffer(cmd,_light[0].Buffer,readback.Buffer,1,&copy); }
                    Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
                });
                Span<uint> data = readback.As<uint>();
                for (int i = 0; i < count; i++) Store(entries[first+i],data.Slice(i*4096,4096));
            }
        }
        finally { _graphics.Destroy(readback); }
    }
    /// <summary>
    /// For tests, after a frame has completed: whether the mirror agrees with the CPU state it
    /// projects (hashes, records, roofs, materials) and the device copy agrees with the mirror.
    /// Returns what disagrees, or null.
    /// </summary>
    internal string? CheckMetadata()
    {
        int entries = 0;
        for (int h = 0; h < _hashCapacity; h++)
        {
            int e = _hashStart+h*4; if (_words[e+3] == 0) continue; entries++;
            ivec3 key = new((int)_words[e],(int)_words[e+1],(int)_words[e+2]);
            if (!_slots.TryGetValue(key,out int slot) || _words[e+3] != slot+1) return $"stale hash entry {key}";
        }
        if (entries != _slots.Count) return $"hash holds {entries} of {_slots.Count} slots";
        foreach (var (key,slot) in _slots)
        {
            uint h = Hash(key)&(uint)(_hashCapacity-1);
            while (_words[_hashStart+(int)h*4+3] != 0 && (_words[_hashStart+(int)h*4] != (uint)key.x || _words[_hashStart+(int)h*4+1] != (uint)key.y || _words[_hashStart+(int)h*4+2] != (uint)key.z)) h = (h+1)&(uint)(_hashCapacity-1);
            if (_words[_hashStart+(int)h*4+3] != slot+1) return $"slot of {key} not found";
        }
        uint[] before = (uint[])_words.Clone();
        for (int slot = 0; slot < _keys.Count; slot++) WriteRecord(slot);
        foreach (var (key,column) in _columns)
        {
            if (column.Roof < 0) continue;
            uint h = Hash(new ivec3(key,0))&(uint)(_roofHashCapacity-1);
            while (_words[_roofHashStart+(int)h*4+2] != 0 && (_words[_roofHashStart+(int)h*4] != (uint)key.x || _words[_roofHashStart+(int)h*4+1] != (uint)key.y)) h = (h+1)&(uint)(_roofHashCapacity-1);
            if (_words[_roofHashStart+(int)h*4+2] != (uint)(_roofStart+column.Roof*RoofWords)) return $"roof of {key} not found";
            WriteRoof(key,column);
        }
        foreach (var (key,chunk) in _terrain)
            if (chunk.Packed != null && !_words.AsSpan(_materialStart+chunk.Block,chunk.Packed.Length).SequenceEqual(chunk.Packed)) return $"materials of {key} differ";
        int changed = before.AsSpan().CommonPrefixLength(_words);
        if (changed != _words.Length) return $"word {changed} differs from a fresh record or roof";
        HostBuffer readback = _graphics.CreateReadbackBuffer((ulong)_words.Length*4);
        try
        {
            _graphics.Submit(cmd =>
            {
                BufferCopy copy = new(0,0,(ulong)_words.Length*4); Vk.CmdCopyBuffer(cmd,_metadata.Buffer,readback.Buffer,1,&copy);
                Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
            });
            int uploaded = readback.As<uint>()[.._words.Length].CommonPrefixLength(_words);
            return uploaded == _words.Length ? null : $"device word {uploaded} of {_words.Length} differs from the mirror";
        }
        finally { _graphics.Destroy(readback); }
    }
    /// <summary>For tests: each point source's depth maps, by its cell.</summary>
    internal Dictionary<ivec3,float[]> CapturePointShadows()
    {
        Dictionary<ivec3,float[]> result = [];
        if (_pointCells.Count == 0 || !_blocks) return result;
        HostBuffer readback = _graphics.CreateReadbackBuffer((ulong)_pointCells.Count*PointShadowSamples*4);
        try
        {
            _graphics.Submit(cmd =>
            {
                BufferCopy copy = new(0,0,(ulong)_pointCells.Count*PointShadowSamples*4); Vk.CmdCopyBuffer(cmd,_pointDepth.Buffer,readback.Buffer,1,&copy);
                Barrier(Vk,cmd,PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
            });
            Span<float> depths = readback.As<float>();
            for (int i = 0; i < _pointCells.Count; i++)
                if (_pointCells[i] is ivec3 cell) result[cell] = depths.Slice(i*PointShadowSamples,PointShadowSamples).ToArray();
            return result;
        }
        finally { _graphics.Destroy(readback); }
    }
    public LightChunkState[] Capture()
    {
        // Called between frames for manual save/autosave; the GPU snapshot is then immutable.
        Vk.DeviceWaitIdle(Device);
        foreach (PendingArchive archive in _pendingArchives) Complete(archive);
        _pendingArchives.Clear();
        if (_capacity != 0)
        {
            List<ArchiveEntry> entries = [.. _archiveRequests]; _archiveRequests.Clear();
            foreach (var (key,slot) in _slots) if (!_reset.Contains(slot)) entries.Add(new(key,slot,_signatures[slot],0));
            ArchiveNow(entries);
        }
        return [.. _saved.Values];
    }
    public void Restore(IEnumerable<LightChunkState> chunks) { foreach (LightChunkState chunk in chunks) _saved[chunk.Coordinate] = chunk; }
    public void Clear()
    {
        _terrain.Clear(); _emitterTerrain.Clear(); _columns.Clear(); _terrainLevels.Clear(); _loadedColumns.Clear();
        _columnVersion = -1; _chunkRevision = -1; _airTop = 1; _dirty = true;
        _slots.Clear(); _keys.Clear(); _signatures.Clear(); _free.Clear(); _reset.Clear(); _saved.Clear();
        _archiving.Clear(); _archiveRequests.Clear(); _epoch++;
        _objectSources.Clear(); _bodyCells.Clear(); _poses.Clear(); _objects.Clear(); _objectDirty = false;
        _objectEmitterCells.Clear(); _emitterWanted.Clear(); _emitterSlots.Clear();
        _pointSlots.Clear(); _pointCells.Clear(); _pointDirty.Clear(); _freePoints.Clear(); _pointsDirty = false; _shadowChunks.Clear();
        _dirtyColumns.Clear(); _dirtyKeys.Clear(); _dirtyMaterials.Clear(); _dirtyRecords.Clear(); _dirtyHashKeys.Clear(); _dirtyRoofs.Clear();
        _touchedColumns.Clear(); _dynamicDirty = false; _relayout = true;
        _version++; _wakeAll = true; _wakeColumns.Clear();
        _budgetCursor = 0; _configuredAt = double.NegativeInfinity;
    }
    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        foreach (PendingArchive archive in _pendingArchives) _graphics.Destroy(archive.Buffer);
        foreach (DeviceBuffer b in _light) Destroy(b); Destroy(_metadata); Destroy(_pointDepth); Destroy(_activity); Destroy(_work);
        foreach (var (buffer,_) in _retired) Destroy(buffer);
        foreach (var (source,_) in _lightGrowth) Destroy(source);
        foreach (HostBuffer upload in _uploads) _graphics.Destroy(upload);
        foreach (HostBuffer readback in _workReadback) if (readback.Buffer.Handle != 0) _graphics.Destroy(readback);
        Vk.DestroyQueryPool(Device,_timing,null); Vk.DestroyDescriptorPool(Device,_pool,null); _loader.Dispose(); Vk.DestroyDescriptorSetLayout(Device,_layout,null);
    }
}
