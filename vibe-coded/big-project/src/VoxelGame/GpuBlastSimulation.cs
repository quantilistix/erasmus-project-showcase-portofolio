using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GlmSharp;
using MYT.Kit.Vulkan;
using MYT.Kit.Structural;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VoxelGame;

/// <summary>
/// Shared, double-buffered acoustic pressure and voxel damage on the GPU. CPU voxel
/// chunks have independent revisions; only changed chunks are uploaded. The field and
/// its intermediate state never leave device memory. Readback consists of changed
/// voxels (for the CPU mirror and fragmentation) and explicitly requested receiver loads.
/// </summary>
internal sealed unsafe class GpuBlastSimulation : IDisposable
{
    private const int BindingCount = 16;
    private const int ChunkVolume = 4096;
    private sealed class Storage
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        public nint Mapped;
        public ulong Capacity;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HashEntry { public ivec4 Key; public uvec4 Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BodyData { public mat4 Model, Inverse; public vec4 Bounds; public ivec4 Identity; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TileData { public ivec4 Coordinate, NearXY, NearZ; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters { public uvec4 Counts; public vec4 Physics; public uvec4 Batch; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Change { public uvec4 Data; public vec4 Load; }
    internal readonly record struct VoxelChange(VoxelBody Body, ivec3 Cell, Voxel Before, ushort Damage, bool Ignite, BlastPressure.Load Load);
    private sealed record Resident(VoxelBody Body, ivec3 Coordinate, int Slot)
    {
        public Grid3DChunk<Voxel>? Source;
        public long Revision = -1;
    }
    private readonly record struct Source(ivec3 Cell, double Remaining);

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly GpuBufferAllocator _allocator;
    private readonly ComputePipelineLoader _loader;
    private readonly Storage[] _storage = Enumerable.Range(0, BindingCount).Select(_ => new Storage()).ToArray();
    private readonly Dictionary<string, ComputePipeline> _pipelines = [];
    private readonly Dictionary<ivec3, int> _tiles = [];
    private readonly List<ivec3> _origins = [];
    private readonly List<Source> _sources = [];
    private readonly Dictionary<(int Body, ivec3 Coordinate), Resident> _residents = [];
    private readonly List<Resident?> _slots = [];
    private readonly Stack<int> _freeSlots = [];
    private readonly List<VoxelChange> _changes = [];
    private DescriptorSetLayout _layout;
    private DescriptorPool _descriptors;
    private DescriptorSet _set;
    private CommandBuffer _commands;
    private Fence _fence;
    private QueryPool _timing;
    private float _timestampPeriod;
    private readonly ulong _maximumRange;
    private Parameters _parameters;
    private double _remaining;
    private bool _clearField = true, _disposed;
    private int _builtTiles = -1;
    private TileData[] _tileData = [];
    private double _support;

    public bool Active => _remaining > 0;
    public int PressureCells => _tiles.Count * ChunkVolume;
    public int ResidentChunks => _residents.Count;
    public long UploadedBytes { get; private set; }
    public double AdvanceMilliseconds { get; private set; }
    public double WaitMilliseconds { get; private set; }
    public double GpuMilliseconds { get; private set; }

    public GpuBlastSimulation(Vk vk, Device device, PhysicalDevice physicalDevice, CommandPool pool, Queue queue, string shaders)
    {
        _vk = vk; _device = device; _pool = pool; _queue = queue;
        _allocator = new(vk, device, physicalDevice, pool, queue);
        _loader = new(vk, device);
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        _maximumRange = properties.Limits.MaxStorageBufferRange;
        try
        {
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[BindingCount];
            for (uint i = 0; i < BindingCount; i++) bindings[i] = new(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            DescriptorSetLayoutCreateInfo layout = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = BindingCount, PBindings = bindings };
            Check(vk.CreateDescriptorSetLayout(device, &layout, null, out _layout));
            DescriptorPoolSize size = new(DescriptorType.StorageBuffer, BindingCount);
            DescriptorPoolCreateInfo descriptors = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
            Check(vk.CreateDescriptorPool(device, &descriptors, null, out _descriptors));
            DescriptorSetLayout setLayout = _layout;
            DescriptorSetAllocateInfo allocate = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptors, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Check(vk.AllocateDescriptorSets(device, &allocate, out _set));
            foreach (string name in new[] { "occupancy", "inject", "velocity", "pressure", "damage", "sample", "health", "impact" })
                _pipelines[name] = _loader.Load(Path.Combine(shaders, "voxel", "simulation", name + ".comp"), [_layout], (uint)sizeof(Parameters), optimize: true);
            CommandBufferAllocateInfo command = new() { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            Check(vk.AllocateCommandBuffers(device, &command, out _commands));
            FenceCreateInfo fence = new() { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(device, &fence, null, out _fence));
            if (properties.Limits.TimestampComputeAndGraphics)
            {
                _timestampPeriod = properties.Limits.TimestampPeriod;
                QueryPoolCreateInfo timing = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = 2 };
                Check(vk.CreateQueryPool(device, &timing, null, out _timing));
            }
            for (int i = 0; i < BindingCount; i++) Ensure(i, 16, host: i is not (0 or 4 or 5 or 13));
            var materials = new vec4[Blocks.Count * 2];
            for (int i = 0; i < Blocks.Count; i++)
            {
                BlockMaterial material = Blocks.Get((BlockType)i);
                materials[i * 2] = new((float)material.Elasticity.Compression, (float)material.Elasticity.Shear, (float)material.Stiffness, (float)material.Durability);
                materials[i * 2 + 1] = new((float)material.MiningRate, Blocks.IsSolid((BlockType)i) ? 1 : 0, (BlockType)i == BlockType.Tnt ? 1 : 0, material.Unbreakable ? 1 : 0);
            }
            Write<vec4>(7, materials);
        }
        catch { Dispose(); throw; }
    }

    public void Emit(dvec3 center)
    {
        if (!Active)
        {
            _tiles.Clear(); _origins.Clear(); _sources.Clear();
            _builtTiles = -1; _clearField = true;
        }
        ivec3 cell = (ivec3)dvec3.Floor(center);
        _origins.Add(cell);
        _sources.Add(new(cell, BlastPressure.PulseDuration));
        _support = 4 * (Math.Cbrt(_origins.Count / BlastPressure.Threshold) - 1);
        // Include a sponge beyond the useful blast radius. Tiles are shared even
        // between overlapping sources, and distant sources never allocate their gap.
        int radius = (int)Math.Ceiling(_support) + 8;
        foreach (ivec3 origin in _origins)
        {
            ivec3 low = (origin - new ivec3(radius)) >> 4, high = (origin + new ivec3(radius)) >> 4;
            for (int z = low.z; z <= high.z; z++)
            for (int y = low.y; y <= high.y; y++)
            for (int x = low.x; x <= high.x; x++)
            {
                ivec3 coordinate = new(x, y, z);
                dvec3 closest = dvec3.Clamp(new dvec3(origin) + .5, new dvec3(coordinate * 16), new dvec3(coordinate * 16 + 16));
                if ((closest - (new dvec3(origin) + .5)).LengthSqr > radius * radius) continue;
                _tiles.TryAdd(coordinate, _tiles.Count);
            }
        }
        _remaining = Math.Max(_remaining, radius / BlastPressure.WaveSpeed + .3);
    }

    internal bool MayReach(dvec3 center, double radius)
    {
        double reach = _support + 12 + radius;
        foreach (ivec3 source in _origins)
            if ((center - (new dvec3(source) + .5)).LengthSqr <= reach * reach) return true;
        return false;
    }

    /// <summary>Updates the independent GPU voxel mirror and its body transforms.</summary>
    private void Synchronize(World world)
    {
        UploadedBytes = 0;
        HashSet<(int, ivec3)> live = [];
        List<BodyData> bodies = [];
        List<ivec4> slotData = [];
        List<uint> work = [];
        List<Resident> uploads = [];
        foreach (VoxelBody body in world.Bodies)
        {
            dvec3 center = dvec3.Zero;
            double radius = double.MaxValue;
            if (body is VoxelObject obj)
            {
                center = body.ToWorld(obj.Bounds.Center); radius = obj.Bounds.Radius;
                if (world.HasPendingTopology(obj) && body.Grid.Chunks.Count > 0)
                {
                    // Physics bounds may not exist yet for spawned/edited objects.
                    ivec3 low = new(int.MaxValue), high = new(int.MinValue);
                    foreach (var chunk in body.Grid.Chunks) { low = ivec3.Min(low, chunk.Origin); high = ivec3.Max(high, chunk.Origin + 16); }
                    center = body.ToWorld((new dvec3(low) + new dvec3(high)) * .5);
                    radius = .5 * new dvec3(high - low).Length;
                }
                if (!MayReach(center, radius)) continue;
            }
            int bodyIndex = bodies.Count;
            mat4 model = Model(body.Position, body.Orientation);
            bodies.Add(new() { Model = model, Inverse = model.Inverse, Bounds = new((vec3)center, (float)Math.Min(radius, float.MaxValue)), Identity = new(body.Id, 0, 0, 0) });
            foreach (Grid3DChunk<Voxel> chunk in body.Grid.Chunks)
            {
                if (!MayReach(body.ToWorld(new dvec3(chunk.Origin) + 8), 14)) continue;
                var key = (body.Id, chunk.Coordinate);
                live.Add(key);
                if (!_residents.TryGetValue(key, out Resident? resident))
                {
                    int slot = _freeSlots.TryPop(out int free) ? free : _slots.Count;
                    resident = new(body, chunk.Coordinate, slot);
                    _residents.Add(key, resident);
                    if (slot == _slots.Count) _slots.Add(resident); else _slots[slot] = resident;
                }
                while (slotData.Count <= resident.Slot) slotData.Add(default);
                slotData[resident.Slot] = new(chunk.Coordinate, bodyIndex);
                work.Add((uint)resident.Slot);
                if (!ReferenceEquals(resident.Source, chunk) || resident.Revision != chunk.Revision)
                {
                    resident.Source = chunk; resident.Revision = chunk.Revision;
                    uploads.Add(resident);
                }
            }
        }
        foreach (var key in _residents.Keys.Where(k => !live.Contains(k)).ToArray())
        {
            Resident removed = _residents[key];
            _slots[removed.Slot] = null;
            _freeSlots.Push(removed.Slot);
            _residents.Remove(key);
        }
        Ensure(0, (ulong)Math.Max(_slots.Count, 1) * ChunkVolume * 4, preserve: true);
        // One contiguous staging upload for all dirty chunks, rather than one
        // staging allocation and submission per chunk during a large detonation.
        if (uploads.Count > 0)
        {
            uint[] packed = new uint[uploads.Count * ChunkVolume];
            for (int i = 0; i < uploads.Count; i++)
            {
                ReadOnlySpan<Voxel> cells = uploads[i].Source!.Cells;
                for (int j = 0; j < ChunkVolume; j++) packed[i * ChunkVolume + j] = Pack(cells[j]);
            }
            EnsureStaging((ulong)packed.Length * 4);
            packed.AsSpan().CopyTo(new Span<uint>((void*)_stagingMapped, packed.Length));
            Begin();
            for (int i = 0; i < uploads.Count; i++)
            {
                BufferCopy copy = new((ulong)i * ChunkVolume * 4, (ulong)uploads[i].Slot * ChunkVolume * 4, ChunkVolume * 4);
                _vk.CmdCopyBuffer(_commands, _staging, _storage[0].Buffer, 1, &copy);
            }
            Barrier(PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
            Submit();
            UploadedBytes += (long)packed.Length * 4;
        }
        Write<BodyData>(2, CollectionsMarshal.AsSpan(bodies));
        Write<ivec4>(11, CollectionsMarshal.AsSpan(slotData));
        Write<uint>(12, CollectionsMarshal.AsSpan(work));
        int hashSize = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, (_tiles.Count + _residents.Count) * 2));
        HashEntry[] hash = new HashEntry[hashSize];
        void Add(ivec4 key, int slot)
        {
            uint h = Hash(key) & (uint)(hashSize - 1);
            while (hash[h].Value.x != 0) h = (h + 1) & (uint)(hashSize - 1);
            hash[h] = new() { Key = key, Value = new((uint)slot + 1, 0, 0, 0) };
        }
        foreach (var (coordinate, slot) in _tiles) Add(new(coordinate, -1), slot);
        foreach (Resident resident in _residents.Values) Add(new(resident.Coordinate, resident.Body.Id), resident.Slot);
        Write<HashEntry>(1, hash);
        _parameters.Counts = new((uint)_tiles.Count, (uint)hashSize - 1, (uint)bodies.Count, (uint)work.Count);
        _parameters.Batch.w = (uint)world.Map.Id;
        BuildBodyCandidates(bodies, world.Map.Id);
        uint eventCapacity = checked((uint)work.Count * ChunkVolume);
        Ensure(8, 16 + (ulong)eventCapacity * (uint)sizeof(Change), host: true, readback: true);
        _parameters.Batch.z = eventCapacity;
    }

    private void BuildBodyCandidates(List<BodyData> bodies, int mapId)
    {
        List<uint>?[] candidates = new List<uint>?[_tiles.Count];
        for (int bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
        {
            BodyData body = bodies[bodyIndex];
            if (body.Identity.x == mapId) continue;
            dvec3 center = new(body.Bounds.x, body.Bounds.y, body.Bounds.z);
            double radius = body.Bounds.w + .001;
            ivec3 low = (ivec3)dvec3.Floor((center - radius) / 16), high = (ivec3)dvec3.Floor((center + radius) / 16);
            long volume = (long)(high.x - low.x + 1) * (high.y - low.y + 1) * (high.z - low.z + 1);
            void Add(ivec3 coordinate, int tile)
            {
                dvec3 closest = dvec3.Clamp(center, new dvec3(coordinate * 16), new dvec3(coordinate * 16 + 16));
                if ((center - closest).LengthSqr <= radius * radius) (candidates[tile] ??= []).Add((uint)bodyIndex);
            }
            if (volume > _tiles.Count)
                foreach (var (coordinate, tile) in _tiles) Add(coordinate, tile);
            else
                for (int z = low.z; z <= high.z; z++) for (int y = low.y; y <= high.y; y++) for (int x = low.x; x <= high.x; x++)
                {
                    ivec3 coordinate = new(x, y, z);
                    if (_tiles.TryGetValue(coordinate, out int tile)) Add(coordinate, tile);
                }
        }
        List<uint> indices = [];
        for (int i = 0; i < _tileData.Length; i++)
        {
            _tileData[i].Coordinate.w = indices.Count;
            _tileData[i].NearZ.z = candidates[i]?.Count ?? 0;
            if (candidates[i] != null) indices.AddRange(candidates[i]!);
        }
        Write<TileData>(3, _tileData);
        Write<uint>(14, CollectionsMarshal.AsSpan(indices));
    }

    public IReadOnlyList<VoxelChange> Advance(World world, double dt)
    {
        if (!(dt > 0) || !double.IsFinite(dt)) throw new ArgumentOutOfRangeException(nameof(dt));
        _changes.Clear();
        if (!Active) return _changes;
        long start = Stopwatch.GetTimestamp();
        WaitMilliseconds = 0;
        int oldTiles = _builtTiles < 0 ? 0 : _builtTiles;
        Ensure(4, (ulong)PressureCells * 16, preserve: !_clearField);
        Ensure(5, (ulong)PressureCells * 16);
        Ensure(13, (ulong)PressureCells * 4);
        if (_builtTiles != _tiles.Count)
        {
            TileData[] tiles = new TileData[_tiles.Count];
            foreach (var (coordinate, slot) in _tiles)
            {
                int Neighbor(ivec3 offset) => _tiles.TryGetValue(coordinate + offset, out int n) ? n : -1;
                tiles[slot] = new() { Coordinate = new(coordinate, 0), NearXY = new(Neighbor(-ivec3.UnitX), Neighbor(ivec3.UnitX), Neighbor(-ivec3.UnitY), Neighbor(ivec3.UnitY)), NearZ = new(Neighbor(-ivec3.UnitZ), Neighbor(ivec3.UnitZ), -1, -1) };
            }
            Write<TileData>(3, tiles);
            _tileData = tiles;
            Begin();
            _vk.CmdFillBuffer(_commands, _storage[4].Buffer, (ulong)oldTiles * ChunkVolume * 16, (ulong)(_tiles.Count - oldTiles) * ChunkVolume * 16, 0);
            Barrier(PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
            Submit();
            _builtTiles = _tiles.Count; _clearField = false;
        }
        Synchronize(world);
        int steps = Math.Max(1, (int)Math.Ceiling(dt * BlastPressure.WaveSpeed / .55));
        double subDt = dt / steps;
        List<uvec4[]> injections = [];
        for (int step = 0; step < steps; step++)
        {
            Dictionary<int, float> combined = [];
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                double duration = Math.Min(subDt, source.Remaining);
                if (!(duration > 0)) continue;
                int index = _tiles[source.Cell >> 4] * ChunkVolume + Grid3D<Voxel>.IndexInChunk(source.Cell);
                combined[index] = combined.GetValueOrDefault(index) + (float)(8 * duration / BlastPressure.PulseDuration);
                _sources[i] = source with { Remaining = source.Remaining - duration };
            }
            injections.Add([.. combined.Select(pair => new uvec4((uint)pair.Key, BitConverter.SingleToUInt32Bits(pair.Value), 0, 0))]);
        }
        _sources.RemoveAll(s => s.Remaining <= 1e-12);
        // Each substep's source range occupies its own immutable part of the
        // mapped input. Updating one mapping between recorded dispatches is unsafe.
        var allSources = injections.SelectMany(s => s).ToArray();
        Write<uvec4>(6, allSources);
        new Span<uint>((void*)_storage[8].Mapped, 4).Clear();
        UpdateDescriptors();
        _parameters.Physics = new((float)subDt, (float)BlastPressure.WaveSpeed, (float)BlastPressure.PulseDuration, (float)BlastPressure.Threshold);
        Begin();
        Barrier(PipelineStageFlags.HostBit, AccessFlags.HostWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        if (_timing.Handle != 0)
        {
            _vk.CmdResetQueryPool(_commands, _timing, 0, 2);
            _vk.CmdWriteTimestamp(_commands, PipelineStageFlags.TopOfPipeBit, _timing, 0);
        }
        Dispatch("occupancy", (uint)PressureCells);
        // Each substep selects its own immutable range of injection records.
        int offset = 0;
        foreach (uvec4[] injection in injections)
        {
            _parameters.Batch.x = (uint)injection.Length;
            // Reuse batch.y, which only the sample kernel interprets as a count.
            _parameters.Batch.y = (uint)offset;
            Dispatch("inject", (uint)injection.Length);
            Dispatch("velocity", (uint)PressureCells);
            Dispatch("pressure", (uint)PressureCells);
            offset += injection.Length;
        }
        _parameters.Physics.x = (float)dt;
        Dispatch("damage", _parameters.Counts.w * ChunkVolume);
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.HostBit, AccessFlags.HostReadBit);
        if (_timing.Handle != 0) _vk.CmdWriteTimestamp(_commands, PipelineStageFlags.BottomOfPipeBit, _timing, 1);
        Submit();
        if (_timing.Handle != 0)
        {
            ulong* timestamps = stackalloc ulong[2];
            Check(_vk.GetQueryPoolResults(_device, _timing, 0, 2, 16, timestamps, 8, QueryResultFlags.Result64Bit));
            GpuMilliseconds = (timestamps[1] - timestamps[0]) * _timestampPeriod / 1e6;
        }
        uint count = *(uint*)_storage[8].Mapped;
        if (count > _parameters.Batch.z) throw new InvalidOperationException("GPU voxel change queue overflow");
        ReadOnlySpan<Change> changes = new((void*)(_storage[8].Mapped + 16), (int)count);
        foreach (Change change in changes)
        {
            Resident resident = _slots[(int)(change.Data.x / ChunkVolume)]!;
            ivec3 cell = resident.Coordinate * 16 + CellOf((int)(change.Data.x % ChunkVolume));
            Voxel before = resident.Body.Grid[cell];
            if (Pack(before) != change.Data.y) throw new InvalidOperationException("GPU voxel mirror changed during simulation");
            ushort damage = change.Data.z == 0 ? ushort.MaxValue : (ushort)(change.Data.z >> 16);
            _changes.Add(new(resident.Body, cell, before, damage, change.Data.w != 0, ToLoad(change.Load)));
        }
        _changes.Sort((a, b) => { int body = a.Body.Id.CompareTo(b.Body.Id); if (body != 0) return body; int z = a.Cell.z.CompareTo(b.Cell.z); if (z != 0) return z; int y = a.Cell.y.CompareTo(b.Cell.y); return y != 0 ? y : a.Cell.x.CompareTo(b.Cell.x); });
        _remaining = Math.Max(0, _remaining - dt);
        AdvanceMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return _changes;
    }

    /// <summary>Samples explicit receivers; never downloads the pressure grid.</summary>
    public BlastPressure.Load[] Sample(ReadOnlySpan<dvec3> positions)
    {
        if (positions.Length == 0) return [];
        vec4[] requests = new vec4[positions.Length];
        for (int i = 0; i < positions.Length; i++) requests[i] = new((vec3)positions[i], 0);
        Write<vec4>(9, requests);
        Ensure(10, (ulong)positions.Length * 16, host: true, readback: true);
        UpdateDescriptors();
        _parameters.Batch.y = (uint)positions.Length;
        Begin();
        Barrier(PipelineStageFlags.HostBit, AccessFlags.HostWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
        Dispatch("sample", (uint)positions.Length);
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.HostBit, AccessFlags.HostReadBit);
        Submit();
        ReadOnlySpan<vec4> samples = new((void*)_storage[10].Mapped, positions.Length);
        BlastPressure.Load[] result = new BlastPressure.Load[positions.Length];
        for (int i = 0; i < result.Length; i++) result[i] = ToLoad(samples[i]);
        return result;
    }

    private static BlastPressure.Load ToLoad(vec4 value) => new(value.x, new dvec3(value.y, value.z, value.w));
    internal dvec4[] EvaluateDamage(ReadOnlySpan<GpuDamageRequest> requests)
    {
        if (requests.Length == 0) return [];
        Write<GpuDamageRequest>(9, requests);
        Ensure(10, (ulong)requests.Length * (uint)sizeof(dvec4), host: true, readback: true);
        UpdateDescriptors();
        _parameters.Batch.y = (uint)requests.Length;
        Begin();
        Barrier(PipelineStageFlags.HostBit, AccessFlags.HostWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
        Dispatch("health", (uint)requests.Length);
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.HostBit, AccessFlags.HostReadBit);
        Submit();
        return new ReadOnlySpan<dvec4>((void*)_storage[10].Mapped, requests.Length).ToArray();
    }

    internal (bool Damaged, double Remaining, double[] Damage) EvaluateImpact(ImpactAnalysis.SideState[] sides, GpuStressLattice[] lattices, double remaining)
    {
        int count = sides.Sum(s => s.Side.Cells.Length);
        if (count == 0) return (false, remaining, []);
        List<dvec4> requests = [new(remaining, count, 1 + count * 4, 0)];
        List<dvec4> contacts = [];
        for (int sideIndex = 0; sideIndex < sides.Length; sideIndex++)
        {
            ImpactAnalysis.SideState state = sides[sideIndex];
            ImpactSide side = state.Side;
            var byCell = state.Contacts.ToLookup(c => c.Cell);
            for (int i = 0; i < side.Cells.Length; i++)
            {
                CellMaterial material = side.Materials[i];
                ElasticLimits limits = side.Limits[i];
                CellStress sustained = side.Static[i];
                int start = contacts.Count / 2;
                foreach (ImpactContact contact in byCell[i])
                {
                    contacts.Add(new(contact.Force, contact.AreaFraction));
                    contacts.Add(new(contact.Inward, side.CellEdge));
                }
                requests.Add(new(material.YoungsModulus, material.ShearModulus, Math.Pow(side.CellEdge, 3), side.Granular[i] ? 1 : 0));
                requests.Add(new(limits.Tension, limits.Compression, limits.Shear, side.DamageToBreak[i]));
                requests.Add(new(sustained.Tension, sustained.Compression, sustained.Shear, state.Damage[i]));
                requests.Add(new(state.Broken[i] ? -1 : lattices[sideIndex].IndexOf(side.Cells[i]),
                    sideIndex | (state.CanFail(i) ? 2 : 0), start, contacts.Count / 2 - start));
            }
        }
        requests.AddRange(contacts);
        Write<dvec4>(9, CollectionsMarshal.AsSpan(requests));
        Ensure(10, (ulong)(3 + count * 2) * (uint)sizeof(dvec4), host: true, readback: true);
        var a = lattices[0].StressStorage;
        var b = lattices.Length > 1 ? lattices[1].StressStorage : a;
        UpdateDescriptors(a, b);
        Parameters saved = _parameters;
        _parameters.Batch.y = (uint)count;
        Begin();
        Barrier(PipelineStageFlags.HostBit | PipelineStageFlags.ComputeShaderBit, AccessFlags.HostWriteBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        for (uint phase = 0; phase < 6; phase++)
        {
            _parameters.Counts.x = phase;
            // The reduction phases use one 256-lane workgroup. Other phases
            // dispatch one lane per material cell, reading the solver buffers directly.
            Dispatch("impact", phase is 1 or 3 or 5 ? 256u : (uint)count, workgroupSize: 256);
        }
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.HostBit, AccessFlags.HostReadBit);
        Submit();
        _parameters = saved;
        ReadOnlySpan<dvec4> work = new((void*)_storage[10].Mapped, 3 + count * 2);
        double[] damage = new double[count];
        for (int i = 0; i < count; i++) damage[i] = work[4 + i * 2].w;
        return (work[2].x > .5, work[2].y, damage);
    }
    private static uint Pack(Voxel voxel) => (uint)voxel.Type | ((uint)voxel.Damage << 16);
    private static ivec3 CellOf(int i) => new(i & 15, (i >> 4) & 15, i >> 8);
    private static uint Hash(ivec4 key)
    {
        unchecked { uint h = (uint)key.x * 73856093 ^ (uint)key.y * 19349663 ^ (uint)key.z * 83492791 ^ (uint)key.w * 2654435761; h ^= h >> 16; h *= 2246822519; return h ^ (h >> 13); }
    }
    private static mat4 Model(dvec3 position, dquat orientation)
    {
        dmat3 r = orientation.ToMat3;
        return new((float)r.m00, (float)r.m01, (float)r.m02, 0, (float)r.m10, (float)r.m11, (float)r.m12, 0, (float)r.m20, (float)r.m21, (float)r.m22, 0, (float)position.x, (float)position.y, (float)position.z, 1);
    }
    private void Write<T>(int binding, ReadOnlySpan<T> data) where T : unmanaged
    {
        Ensure(binding, (ulong)data.Length * (uint)sizeof(T), host: true);
        data.CopyTo(new Span<T>((void*)_storage[binding].Mapped, data.Length));
    }
    private void Ensure(int binding, ulong bytes, bool host = false, bool readback = false, bool preserve = false)
    {
        Storage storage = _storage[binding];
        bytes = Math.Max(16, bytes);
        if (bytes <= storage.Capacity) return;
        if (bytes > _maximumRange) throw new InvalidOperationException($"GPU simulation buffer {binding} needs {bytes / (1 << 20)} MiB, exceeding the device storage range {_maximumRange / (1 << 20)} MiB");
        ulong capacity = Math.Min(_maximumRange, System.Numerics.BitOperations.RoundUpToPowerOf2(bytes));
        BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
        Buffer previous = storage.Buffer; DeviceMemory previousMemory = storage.Memory; ulong previousSize = storage.Capacity;
        if (host)
            storage.Mapped = readback ? _allocator.CreateReadback(capacity, usage, out storage.Buffer, out storage.Memory) : _allocator.CreateMapped(capacity, usage, out storage.Buffer, out storage.Memory);
        else
            _allocator.Create(capacity, usage, MemoryPropertyFlags.DeviceLocalBit, out storage.Buffer, out storage.Memory);
        storage.Capacity = capacity;
        if (preserve && previousSize > 0)
        {
            Begin();
            Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit);
            BufferCopy copy = new(0, 0, previousSize);
            _vk.CmdCopyBuffer(_commands, previous, storage.Buffer, 1, &copy);
            Barrier(PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
            Submit();
        }
        _allocator.Destroy(previous, previousMemory);
    }
    private Buffer _staging;
    private DeviceMemory _stagingMemory;
    private nint _stagingMapped;
    private ulong _stagingSize;
    private void EnsureStaging(ulong bytes)
    {
        if (bytes <= _stagingSize) return;
        _allocator.Destroy(_staging, _stagingMemory);
        _stagingSize = System.Numerics.BitOperations.RoundUpToPowerOf2(bytes);
        _stagingMapped = _allocator.CreateMapped(_stagingSize, BufferUsageFlags.TransferSrcBit, out _staging, out _stagingMemory);
    }
    private void UpdateDescriptors((Buffer Buffer, ulong Bytes)? sideA = null, (Buffer Buffer, ulong Bytes)? sideB = null)
    {
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[BindingCount];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[BindingCount];
        for (uint i = 0; i < BindingCount; i++)
        {
            buffers[i] = new(_storage[i].Buffer, 0, _storage[i].Capacity);
            if (i == 14 && sideA is { } first) buffers[i] = new(first.Buffer, 0, first.Bytes);
            if (i == 15 && sideB is { } second) buffers[i] = new(second.Buffer, 0, second.Bytes);
            writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &buffers[i] };
        }
        _vk.UpdateDescriptorSets(_device, BindingCount, writes, 0, null);
    }
    private void Begin()
    {
        Check(_vk.ResetCommandBuffer(_commands, 0));
        CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(_commands, &begin));
    }
    private void Dispatch(string name, uint count, uint workgroupSize = 64)
    {
        if (count == 0) return;
        ComputePipeline pipeline = _pipelines[name];
        DescriptorSet set = _set;
        _vk.CmdBindPipeline(_commands, PipelineBindPoint.Compute, pipeline.Pipeline);
        _vk.CmdBindDescriptorSets(_commands, PipelineBindPoint.Compute, pipeline.Layout, 0, 1, &set, 0, null);
        Parameters parameters = _parameters;
        _vk.CmdPushConstants(_commands, pipeline.Layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(Parameters), &parameters);
        uint groups = (count + workgroupSize - 1) / workgroupSize;
        uint x = Math.Min(groups, 65535u);
        _vk.CmdDispatch(_commands, x, (groups + x - 1) / x, 1);
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    }
    private void Barrier(PipelineStageFlags source, AccessFlags sourceAccess, PipelineStageFlags target, AccessFlags targetAccess)
    {
        MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = sourceAccess, DstAccessMask = targetAccess };
        _vk.CmdPipelineBarrier(_commands, source, target, 0, 1, &barrier, 0, null, 0, null);
    }
    private void Submit()
    {
        Check(_vk.EndCommandBuffer(_commands));
        Fence fence = _fence;
        Check(_vk.ResetFences(_device, 1, &fence));
        CommandBuffer commands = _commands;
        SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commands };
        Check(_vk.QueueSubmit(_queue, 1, &submit, fence));
        long start = Stopwatch.GetTimestamp();
        Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue));
        WaitMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    private static void Check(Result result) { if (result != Result.Success) throw new InvalidOperationException($"GPU voxel simulation: {result}"); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Storage storage in _storage) _allocator.Destroy(storage.Buffer, storage.Memory);
        _allocator.Destroy(_staging, _stagingMemory);
        _allocator.ReleaseStaging();
        _loader.Dispose();
        if (_fence.Handle != 0) _vk.DestroyFence(_device, _fence, null);
        if (_timing.Handle != 0) _vk.DestroyQueryPool(_device, _timing, null);
        if (_commands.Handle != 0) { CommandBuffer cmd = _commands; _vk.FreeCommandBuffers(_device, _pool, 1, &cmd); }
        if (_descriptors.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptors, null);
        if (_layout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _layout, null);
    }
}
