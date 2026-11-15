using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.Vulkan;

namespace VoxelGame;

internal sealed unsafe partial class Renderer
{
    private const int GpuMeshBatch = 32;
    internal bool GpuCulledMeshing { get; set; } = GpuSimulationOptions.Enabled("MYT_GPU_MESH");
    internal bool GpuMeshInitialChunks { get; set; } = true;
    // Test-only full vertex readback. Production reads eight bytes per completed chunk.
    internal bool CaptureGpuMeshes { get; set; }
    internal int GpuMeshesDispatched { get; private set; }
    internal long GpuMeshesCompleted { get; private set; }
    internal double GpuMeshMilliseconds { get; private set; }
    private QueryPool _gpuMeshTiming;
    private float _gpuMeshTimestampPeriod;
    private readonly record struct GpuMeshWork(MeshResult Result, uint? Quads);
    private readonly LinkedList<GpuMeshWork> _gpuMeshEdits = [], _gpuMeshChunks = [], _gpuMeshTerrain = [];
    private long _gpuMeshTurn;
    private ArenaRange _gpuMeshScratch;
    private readonly List<GpuMeshFlight>[] _gpuMeshFlights = Enumerable.Range(0, (int)GraphicsContext.FramesInFlight).Select(_ => new List<GpuMeshFlight>()).ToArray();
    private readonly Dictionary<int, DescriptorSet>[] _gpuMeshSets = Enumerable.Range(0, (int)GraphicsContext.FramesInFlight).Select(_ => new Dictionary<int, DescriptorSet>()).ToArray();
    private readonly HostBuffer[] _gpuMeshInputs = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _gpuMeshCounts = new HostBuffer[GraphicsContext.FramesInFlight];
    private HostBuffer _gpuMeshMaterials;
    private DescriptorSetLayout _gpuMeshLayout;
    private DescriptorPool _gpuMeshPool;
    private ComputePipelineLoader _gpuMeshLoader = null!;
    private ComputePipeline _gpuMeshPipeline;
    private readonly record struct GpuMeshFlight(MeshResult Result, ArenaRange Range, HostBuffer Readback, bool CountOnly);
    private int GpuMeshesPending => _gpuMeshEdits.Count + _gpuMeshChunks.Count + _gpuMeshTerrain.Count + _gpuMeshFlights.Sum(f => f.Count);

    private void QueueGpuMesh(MeshResult result, uint? quads = null)
    {
        ChunkRecord? record = result.Record;
        bool urgent = record != null && UrgentGpuMesh(record);
        var queue = result.TerrainMesh != null ? _gpuMeshTerrain : urgent ? _gpuMeshEdits : _gpuMeshChunks;
        GpuMeshWork work = new(result, quads);
        // Finish emission before starting more snapshots at this priority.
        var node = quads.HasValue ? queue.AddFirst(work) : queue.AddLast(work);
        if (record != null) record.GpuMeshWork = node;
    }

    private bool UrgentGpuMesh(ChunkRecord record) => record.HasMesh || record.PendingEdit
        || (record.Owner!.ToWorld(new dvec3(record.Coordinate * 16) + 8) - _asyncEye).LengthSqr
            <= Math.Pow(ImmediateDistance * 16 + ChunkRadius, 2);

    private void PromoteGpuMesh(ChunkRecord record)
    {
        if (record.GpuMeshWork is not { List: not null } node) return;
        node.List.Remove(node);
        _gpuMeshEdits.AddFirst(node);
    }

    private bool TakeGpuMesh(out GpuMeshWork work)
    {
        // Edits get six of eight slots under load. Reserve the others for initial
        // chunks and LOD so building or explosions cannot starve world streaming.
        long turn = _gpuMeshTurn++ % 8;
        var queue = turn == 6 && _gpuMeshChunks.Count > 0 ? _gpuMeshChunks
            : turn == 7 && _gpuMeshTerrain.Count > 0 ? _gpuMeshTerrain
            : _gpuMeshEdits.Count > 0 ? _gpuMeshEdits
            : _gpuMeshChunks.Count > 0 ? _gpuMeshChunks : _gpuMeshTerrain;
        if (queue.First is not { } node) { work = default; return false; }
        work = node.Value;
        queue.RemoveFirst();
        if (work.Result.Record is { } record) record.GpuMeshWork = null;
        return true;
    }

    private void CreateGpuMesher(string shaders)
    {
        _gpuMeshLoader = new(_vk, _device);
        _vk.GetPhysicalDeviceProperties(_graphics.PhysicalDevice, out var properties);
        if (properties.Limits.TimestampComputeAndGraphics)
        {
            _gpuMeshTimestampPeriod = properties.Limits.TimestampPeriod;
            QueryPoolCreateInfo timing = new() { SType = StructureType.QueryPoolCreateInfo, QueryType = QueryType.Timestamp, QueryCount = GraphicsContext.FramesInFlight * 2 };
            if (_vk.CreateQueryPool(_device, &timing, null, out _gpuMeshTiming) != Result.Success) throw new InvalidOperationException("GPU mesh timing pool");
        }
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[4];
        for (uint i = 0; i < 4; i++) bindings[i] = new()
        { Binding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.ComputeBit };
        DescriptorSetLayoutCreateInfo layout = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings };
        if (_vk.CreateDescriptorSetLayout(_device, &layout, null, out _gpuMeshLayout) != Result.Success) throw new InvalidOperationException("GPU mesh descriptor layout");
        _gpuMeshPipeline = _gpuMeshLoader.Load(Path.Combine(shaders, "voxel/culled_mesh.comp"), [_gpuMeshLayout], 16, optimize: true);
        DescriptorPoolSize size = new(DescriptorType.StorageBuffer, 8192);
        DescriptorPoolCreateInfo pool = new() { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &size, MaxSets = 2048 };
        if (_vk.CreateDescriptorPool(_device, &pool, null, out _gpuMeshPool) != Result.Success) throw new InvalidOperationException("GPU mesh descriptor pool");
        uint[] materials = GpuMeshInputs.Materials(FaceLayers);
        _gpuMeshMaterials = _graphics.CreateHostBuffer((ulong)materials.Length * 4, BufferUsageFlags.StorageBufferBit);
        materials.AsSpan().CopyTo(_gpuMeshMaterials.As<uint>());
        _gpuMeshScratch = _arena.Allocate(256);
        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            _gpuMeshInputs[f] = _graphics.CreateHostBuffer(GpuMeshBatch * GpuMeshInputs.InputWords * 4UL, BufferUsageFlags.StorageBufferBit);
            _gpuMeshCounts[f] = _graphics.CreateHostBuffer(GpuMeshBatch * 8, BufferUsageFlags.StorageBufferBit);
        }
    }

    private DescriptorSet GpuMeshSet(uint frame, int page)
    {
        if (_gpuMeshSets[frame].TryGetValue(page, out var set)) return set;
        DescriptorSetLayout layout = _gpuMeshLayout;
        DescriptorSetAllocateInfo allocate = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _gpuMeshPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        if (_vk.AllocateDescriptorSets(_device, &allocate, out set) != Result.Success) throw new InvalidOperationException("GPU mesh descriptor allocation");
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[4];
        buffers[0] = new(_gpuMeshInputs[frame].Buffer, 0, _gpuMeshInputs[frame].Size);
        buffers[1] = new(_gpuMeshMaterials.Buffer, 0, _gpuMeshMaterials.Size);
        buffers[2] = new(_arena.BufferOf(page), 0, PageBytes);
        buffers[3] = new(_gpuMeshCounts[frame].Buffer, 0, _gpuMeshCounts[frame].Size);
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
        for (uint i = 0; i < 4; i++) writes[i] = new()
        { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers + i };
        _vk.UpdateDescriptorSets(_device, 4, writes, 0, null);
        _gpuMeshSets[frame].Add(page, set);
        return set;
    }

    private void RecordGpuMeshes(CommandBuffer cmd, uint frame)
    {
        GpuMeshesDispatched = 0;
        if (_gpuMeshEdits.Count == 0 && _gpuMeshChunks.Count == 0 && _gpuMeshTerrain.Count == 0) return;
        _gpuMeshCounts[frame].As<uint>().Clear();
        var flights = _gpuMeshFlights[frame];
        if (_gpuMeshTimestampPeriod > 0)
        {
            _vk.CmdResetQueryPool(cmd, _gpuMeshTiming, frame * 2, 2);
            _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.ComputeShaderBit, _gpuMeshTiming, frame * 2);
        }
        while (flights.Count < GpuMeshBatch && (MeshBytesUploaded == 0
            || MeshBytesUploaded + GpuMeshInputs.InputWords * 4 <= MeshUploadBudgetBytes)
            && TakeGpuMesh(out var work))
        {
            MeshResult result = work.Result;
            if (!LiveGpuMesh(result)) { AbandonGpuTerrain(result); continue; }
            uint? knownQuads = work.Quads;
            if (result.Record is { PendingEdit: true } changed)
            {
                // A queued snapshot has not touched device memory yet. Coalesce
                // intervening edits instead of displaying it and starting over.
                result = CaptureGpuMesh(changed);
                knownQuads = null;
            }
            bool direct = !knownQuads.HasValue && result.Record is { } chunk && UrgentGpuMesh(chunk);
            bool countOnly = !knownQuads.HasValue && !direct;
            int index = flights.Count;
            uint[] input = result.GpuCells!;
            input.AsSpan().CopyTo(_gpuMeshInputs[frame].As<uint>().Slice(index * GpuMeshInputs.InputWords, input.Length));
            // Interactive chunks get a modest speculative range and emit in one
            // dispatch. Overflow reports the exact size without writing vertices;
            // the previous mesh stays visible until a correctly sized retry.
            uint capacity = knownQuads ?? (uint)Math.Clamp((long)(result.Record?.QuadCount ?? 0) * 2 + 128, 512, MaxQuads);
            ulong vertexBytes = countOnly ? 0 : capacity * 8UL * sizeof(uint);
            ArenaRange range = countOnly ? _gpuMeshScratch : _arena.Allocate(vertexBytes);
            if (!countOnly)
            {
                vertexBytes = range.Size;
                _gpuMeshCounts[frame].As<uint>()[index * 2] = (uint)(range.Size / (8 * sizeof(uint)));
            }
            DescriptorSet set = GpuMeshSet(frame, range.Page);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _gpuMeshPipeline.Pipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _gpuMeshPipeline.Layout, 0, 1, &set, 0, null);
            uint flags = (result.TransparentLeaves ? 1u : 0u) | (result.GpuShapes ? 2u : 0u) | (countOnly ? 4u : 32u);
            if (result.TerrainMesh is TerrainLod.Mesh terrain)
                flags |= 9u | (terrain.Tile.Resolution >= TerrainLod.WorldHeight ? 16u : 0u)
                    | (uint)System.Numerics.BitOperations.Log2((uint)terrain.Tile.Resolution) << 8;
            uvec4 parameters = new((uint)(index * GpuMeshInputs.InputWords), (uint)(range.Offset / 4), (uint)(index * 2), flags);
            _vk.CmdPushConstants(cmd, _gpuMeshPipeline.Layout, ShaderStageFlags.ComputeBit, 0, 16, &parameters);
            _vk.CmdDispatch(cmd, 1, 1, 1);
            HostBuffer readback = CaptureGpuMeshes && !countOnly ? _graphics.CreateHostBuffer(vertexBytes, BufferUsageFlags.TransferDstBit) : default;
            flights.Add(new(result with { Cells = [] }, range, readback, countOnly));
            MeshBytesUploaded += (ulong)input.Length * 4;
            GpuMeshesDispatched++;
        }
        if (flights.Count == 0) return;
        if (_gpuMeshTimestampPeriod > 0)
            _vk.CmdWriteTimestamp(cmd, PipelineStageFlags.ComputeShaderBit, _gpuMeshTiming, frame * 2 + 1);
        MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit | AccessFlags.TransferReadBit | AccessFlags.HostReadBit };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.VertexInputBit | PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        foreach (var flight in flights)
            if (flight.Readback.Size != 0)
            {
                BufferCopy copy = new(flight.Range.Offset, 0, flight.Readback.Size);
                _vk.CmdCopyBuffer(cmd, _arena.BufferOf(flight.Range.Page), flight.Readback.Buffer, 1, &copy);
            }
    }

    // BeginFrame has already waited for this frame slot's fence. Only tiny counts are
    // read; the generated vertices stay in device memory. Keep the old mesh until here.
    private void CompleteGpuMeshes(uint frame)
    {
        var flights = _gpuMeshFlights[frame];
        if (flights.Count != 0 && _gpuMeshTimestampPeriod > 0)
        {
            ulong* ticks = stackalloc ulong[2];
            if (_vk.GetQueryPoolResults(_device, _gpuMeshTiming, frame * 2, 2, 16, ticks, 8, QueryResultFlags.Result64Bit) == Result.Success)
                GpuMeshMilliseconds = (ticks[1] - ticks[0]) * _gpuMeshTimestampPeriod / 1e6;
        }
        for (int i = 0; i < flights.Count; i++)
        {
            var flight = flights[i];
            var result = flight.Result;
            uint count = _gpuMeshCounts[frame].As<uint>()[i * 2];
            bool overflow = !flight.CountOnly && count * 8UL * sizeof(uint) > flight.Range.Size;
            if ((flight.CountOnly && count > 0 || overflow) && LiveGpuMesh(result))
            {
                if (overflow)
                {
                    Bury(flight.Range);
                    if (flight.Readback.Size != 0) _graphics.Destroy(flight.Readback);
                }
                QueueGpuMesh(result, count);
                continue;
            }
            if (overflow)
            {
                Bury(flight.Range);
                if (flight.Readback.Size != 0) _graphics.Destroy(flight.Readback);
                AbandonGpuTerrain(result);
                continue;
            }
            uint[] vertices = flight.Readback.Size != 0 ? flight.Readback.As<uint>().Slice(0, checked((int)count * 8)).ToArray() : [];
            if (flight.Readback.Size != 0) _graphics.Destroy(flight.Readback);
            if (!LiveGpuMesh(result)) { if (!flight.CountOnly) Bury(flight.Range); AbandonGpuTerrain(result); continue; }
            if (result.TerrainMesh != null)
            {
                CompleteGpuTerrain(result, count == 0 ? default : flight.Range, count * 4,
                    _gpuMeshCounts[frame].As<uint>()[i * 2 + 1] != 0);
                GpuMeshesCompleted++;
                continue;
            }
            ChunkRecord record = result.Record!;
            record.MeshInFlight = false;
            record.Mesh = null;
            if (record.PendingEdit)
            {
                record.MeshInFlight = true;
                QueueGpuMesh(CaptureGpuMesh(record));
            }
            if (record.UploadVersion > result.Version || record.DisplayedVersion > result.Version)
            {
                if (!flight.CountOnly) Bury(flight.Range);
                continue;
            }
            if (count == 0)
            {
                if (!flight.CountOnly) Bury(flight.Range);
                if (record.Version > result.Version && record.Owner!.Grid.TryGetChunk(result.Chunk, out _)) continue;
                record.PendingEmptyVersion = result.Version;
                record.EmptyQueuedAt = result.QueuedAt;
                record.EmptyDependencies = result.Dependencies ?? [];
                _asyncEmpty.Add(record);
            }
            else
            {
                Bury(record.Vertices);
                record.Vertices = _arena.Trim(flight.Range, count * 8UL * sizeof(uint));
                record.QuadCount = count;
                record.HasWater = _gpuMeshCounts[frame].As<uint>()[i * 2 + 1] != 0;
                record.UploadedVertices = vertices;
                record.UploadVersion = record.DisplayedVersion = result.Version;
                record.PendingEmptyVersion = 0;
                _asyncEmpty.Remove(record);
                if (record.HasMesh) RecordMeshLatency(result.QueuedAt);
                if (!record.HasMesh) _meshCoverageGeneration++;
                record.HasMesh = true;
                WriteData(record.Owner!, result.Chunk, record);
            }
            GpuMeshesCompleted++;
            MeshResultsAccepted++;
        }
        flights.Clear();
    }

    private bool LiveGpuMesh(MeshResult result) => result.TerrainMesh is TerrainLod.Mesh terrain
        ? ReferenceEquals(_terrainLod, result.TerrainOwner) && _terrainLod!.IsCurrent(terrain.Tile, terrain.Version)
        : result.Record!.Live;

    private void DestroyGpuMesher()
    {
        foreach (var flights in _gpuMeshFlights)
            foreach (var flight in flights)
                if (flight.Readback.Size != 0) _graphics.Destroy(flight.Readback);
        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            _graphics.Destroy(_gpuMeshInputs[f]);
            _graphics.Destroy(_gpuMeshCounts[f]);
        }
        _graphics.Destroy(_gpuMeshMaterials);
        _gpuMeshLoader.Dispose();
        if (_gpuMeshTiming.Handle != 0) _vk.DestroyQueryPool(_device, _gpuMeshTiming, null);
        _vk.DestroyDescriptorPool(_device, _gpuMeshPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _gpuMeshLayout, null);
    }
}
