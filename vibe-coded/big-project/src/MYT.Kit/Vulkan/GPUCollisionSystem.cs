using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>
/// Matches the GLSL Contact in contact_common.glsl. Only its size matters on the CPU: contacts
/// are produced and consumed on the GPU.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuContact
{
    public uvec4 Header;
    public uvec4 NodesA;
    public uvec4 NodesB;
    public uvec4 Key;
    public vec4 WeightsA;
    public vec4 WeightsB;
    public vec4 Normal;
    public vec4 OffsetA;
    public vec4 OffsetB;
    public vec4 Lambda;
    public vec4 Penalty;
}

/// <summary>Matches the GLSL ContactReport in contact_common.glsl; its array stride is padded to the dvec4 alignment.</summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
internal struct GpuContactReport
{
    public dvec4 Point;     // xyz = world point, w = substep duration
    public uvec4 Bodies;    // x = body of side A, y = body of side B (material ids), z = contact type
    public vec4 Normal;     // xyz = n from B to A, w = normal impulse on A
    public vec4 Friction;   // xyz = friction impulse on A, w = approach speed
}

/// <summary>Matches the GLSL CacheEntry; only its size matters on the CPU.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuCacheEntry
{
    public uvec4 Header;
    public uvec4 Identity;
    public vec4 Lambda;
    public vec4 Penalty;
    public vec4 Normal;
}


/// <summary>
/// Contact generation for every body (descriptor set 1). Owns the concatenated collision
/// surfaces and the contacts; each detection pass places the surfaces, bounds them, lists the
/// triangle pairs whose bounds overlap (contact_pairs.comp), finds vertex-triangle and edge-edge
/// contacts between each pair (contact_detect.comp) and files each contact under the blocks it
/// touches. Contacts are remembered between steps in two hash tables that swap roles every
/// step, so there are two descriptor sets, one per step parity.
/// </summary>
internal sealed class GPUCollisionSystem : IDisposable
{
    public const int CounterCount = 6;

    /// <summary>Bytes before the first report in the reports buffer: the written and dropped counts, padded to 32.</summary>
    public const ulong ReportHeaderSize = 32;

    private const int VertexLocalBinding = 0;
    private const int VertexInfoBinding = 1;
    private const int VertexStartBinding = 2;
    private const int VertexEndBinding = 3;
    private const int VertexLastBinding = 4;
    private const int TrianglesBinding = 5;
    private const int EdgesBinding = 6;
    private const int BodiesBinding = 7;
    private const int BvhNodesBinding = 8;
    private const int BvhPrimitivesBinding = 9;
    private const int ContactsBinding = 10;
    private const int CountersBinding = 11;
    private const int BlockContactOffsetsBinding = 12;
    private const int BlockContactCursorBinding = 13;
    private const int BlockContactRefsBinding = 14;
    private const int CacheCurrentBinding = 15;
    private const int CachePreviousBinding = 16;
    private const int MaterialsBinding = 17;
    private const int IndirectArgsBinding = 18;
    private const int TriangleBoundsBinding = 19;
    private const int PairsBinding = 20;
    private const int BvhRefitBinding = 21;
    private const int ContactReportsBinding = 22;
    private const int ReportReadbackBinding = 23;
    private const int BodyBvhNodesBinding = 24;
    private const int BodyBvhPrimitivesBinding = 25;
    private const int BodyBvhRefitBinding = 26;
    private const int BodyCandidatesBinding = 27;
    private const int BodyCandidateCountsBinding = 28;
    private const int BindingCount = 29;
    private const uint BodyBvhThreshold = 32;
    private const uint MaxBodyCandidates = 64; // Must match contact_common.glsl; overflow falls back to traversal.

    private const uint ModeContinuous = 1;
    private const uint ModeSweep = 1;
    private const uint ModeFreeFlight = 2;
    private const uint ModeFill = 1;
    private const uint ModePairs = 1;
    private const uint ModeReports = 2;

    // Candidate triangle pairs per contact the broad phase may list in one pass. Most pairs make
    // no contact; a large triangle lying on a dense mesh makes thousands.
    private const uint PairsPerContact = 16;

    // A contact touches at most four blocks: a vertex or rigid body against a triangle's three
    // nodes, or two edges' two nodes each.
    private const uint MaxBlocksPerContact = 4;

    public GPUCollisionSystem(GpuBufferAllocator allocator, Vk vk, Device device)
    {
        _allocator = allocator;
        _vk = vk;
        _device = device;
    }

    private readonly GpuBufferAllocator _allocator;
    private readonly Vk _vk;
    private readonly Device _device;

    private ComputePipelineLoader? _pipelineLoader;
    private ComputePipeline _surfaceUpdate;
    private ComputePipeline _triangleBounds;
    private ComputePipeline _bodyBounds;
    private ComputePipeline _bodyBvhRefit;
    private ComputePipeline _bodyCandidates;
    private ComputePipeline _bvhRefit;
    private ComputePipeline _contactPairs;
    private ComputePipeline _detect;
    private ComputePipeline _contactArgs;
    private ComputePipeline _contactLists;
    private ComputePipeline _contactOrder;
    private ComputePipeline _contactScan;
    private ComputePipeline _contactStore;
    private ComputePipeline _contactReport;
    private ComputePipeline _reportReadback;

    private DescriptorPool _descriptorPool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[2];

    private readonly Buffer[] _buffers = new Buffer[BindingCount];
    private readonly DeviceMemory[] _memory = new DeviceMemory[BindingCount];
    private static readonly int[] CapacityBindings =
    [
        PairsBinding,
        ContactsBinding,
        CountersBinding,
        BlockContactRefsBinding,
        IndirectArgsBinding,
        ContactReportsBinding,
        ReportReadbackBinding
    ];

    private (uint Contacts, uint Reports) _capacity;
    private readonly Buffer[] _cacheTables = new Buffer[2];
    private readonly DeviceMemory[] _cacheMemory = new DeviceMemory[2];
    private nint _materialsMapped;

    public DescriptorSetLayout Layout { get; private set; }

    public uint VertexCount { get; private set; }
    public uint DynamicVertexCount { get; private set; }
    public uint TriangleCount { get; private set; }
    public uint DynamicTriangleCount { get; private set; }
    public uint BodyCount { get; private set; }
    public uint DynamicBodyCount { get; private set; }
    public uint SoftBvhNodeStart { get; private set; }
    public uint SoftBvhNodeCount { get; private set; }
    public uint MaterialCount { get; private set; }
    private uint _bodyBvhNodeCount;
    public uint MaxContacts { get; private set; }
    public uint CacheCapacity { get; private set; }
    public uint MaxBlockReferences { get; private set; }
    public uint MaxContactReports { get; private set; }

    /// <summary>Cached host memory containing the live reports after the update's fence signals.</summary>
    public nint ReportReadbackMapped { get; private set; }

    /// <summary>uint counters: contacts, dropped contacts, dropped block references, and the broad phase's pairs while it runs.</summary>
    public Buffer CountersBuffer => _buffers[CountersBinding];

    /// <summary>Workgroup counts: [0] per-contact passes, [1] narrow phase, [2] live report readback.</summary>
    public Buffer IndirectArgsBuffer => _buffers[IndirectArgsBinding];

    public DescriptorSet Set(uint parity) => _sets[parity & 1];

    public unsafe void CreateLayout()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[BindingCount];
        for (int i = 0; i < BindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = BindingCount,
            PBindings = bindings
        };

        if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out DescriptorSetLayout layout) != Result.Success)
            throw new InvalidOperationException("Failed to create collision descriptor set layout");

        Layout = layout;
    }

    public void LoadPipelines(string shaderDirectory, DescriptorSetLayout stateLayout)
    {
        _pipelineLoader = new ComputePipelineLoader(_vk, _device);
        DescriptorSetLayout[] layouts = [stateLayout, Layout];
        uint pushSize = (uint)Unsafe.SizeOf<DispatchRange>();

        ComputePipeline Load(string name) =>
            _pipelineLoader.Load(Path.Combine(shaderDirectory, name), layouts, pushSize);

        _surfaceUpdate = Load("surface_update.comp");
        _triangleBounds = Load("triangle_bounds.comp");
        _bodyBounds = Load("body_bounds.comp");
        _bodyBvhRefit = Load("body_bvh_refit.comp");
        _bodyCandidates = Load("body_candidates.comp");
        _bvhRefit = Load("bvh_refit.comp");
        _contactPairs = Load("contact_pairs.comp");
        _detect = Load("contact_detect.comp");
        _contactArgs = Load("contact_args.comp");
        _contactLists = Load("contact_lists.comp");
        _contactOrder = Load("contact_order.comp");
        _contactScan = Load("contact_scan.comp");
        _contactStore = Load("contact_store.comp");
        _contactReport = Load("contact_report.comp");
        _reportReadback = Load("report_readback.comp");
    }

    /// <summary>Uploads the collision surfaces and sizes the contact buffers.</summary>
    /// <param name="blockCount">Solver blocks: soft body nodes, then rigid bodies.</param>
    /// <param name="maxReports">Capacity of the contact reports of one update.</param>
    public void Build(CollisionGeometry geometry, uint blockCount, uint maxContacts, uint maxReports)
    {
        LastBuildUploadBytes = 0;
        LastBuildBufferGrowths = 0;
        VertexCount = (uint)geometry.VertexInfo.Count;
        DynamicVertexCount = geometry.DynamicVertexCount;
        TriangleCount = (uint)geometry.Triangles.Count;
        DynamicTriangleCount = geometry.DynamicTriangleCount;
        BodyCount = (uint)geometry.Bodies.Count;
        DynamicBodyCount = geometry.DynamicBodyCount;
        SoftBvhNodeStart = geometry.SoftBvhNodeStart;
        SoftBvhNodeCount = geometry.SoftBvhNodeCount;
        MaterialCount = geometry.MaterialCount;
        MaxContacts = Math.Max(maxContacts, 1);
        CacheCapacity = BitOperations.RoundUpToPowerOf2(2 * MaxContacts);
        MaxBlockReferences = MaxBlocksPerContact * MaxContacts;
        MaxContactReports = Math.Max(maxReports, 1);

        const BufferUsageFlags storage = BufferUsageFlags.StorageBufferBit;
        const BufferUsageFlags transfer = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;
        ReadOnlySpan<dvec4> world = CollectionsMarshal.AsSpan(geometry.VertexWorld);

        CreateFilled<dvec4>(VertexLocalBinding, CollectionsMarshal.AsSpan(geometry.VertexLocal), storage);
        CreateFilled<uvec4>(VertexInfoBinding, CollectionsMarshal.AsSpan(geometry.VertexInfo), storage);
        CreateFilled(VertexStartBinding, world, storage | transfer);
        CreateFilled(VertexEndBinding, world, storage);
        CreateFilled(VertexLastBinding, world, storage | transfer);
        CreateFilled<GpuTriangle>(TrianglesBinding, CollectionsMarshal.AsSpan(geometry.Triangles), storage);
        CreateFilled<uvec4>(EdgesBinding, CollectionsMarshal.AsSpan(geometry.Edges), storage);
        CreateFilled<GpuCollisionBody>(BodiesBinding, CollectionsMarshal.AsSpan(geometry.Bodies), storage);
        CreateFilled<GpuBvhNode>(BvhNodesBinding, CollectionsMarshal.AsSpan(geometry.BvhNodes), storage);
        CreateFilled<uint>(BvhPrimitivesBinding, CollectionsMarshal.AsSpan(geometry.BvhPrimitives), storage);
        CreateFilled<vec4>(TriangleBoundsBinding, CollectionsMarshal.AsSpan(geometry.TriangleBounds), storage);
        CreateFilled<uvec4>(BvhRefitBinding, CollectionsMarshal.AsSpan(geometry.BvhRefit), storage);
        (GpuBvhNode[] bodyNodes, uint[] bodyOrder) = BvhBuilder.BuildBodies(CollectionsMarshal.AsSpan(geometry.Bodies));
        _bodyBvhNodeCount = (uint)bodyNodes.Length;
        uvec4[] links = new uvec4[bodyNodes.Length];
        for (int i = bodyNodes.Length - 1; i >= 0; i--)
        {
            uint count = BitConverter.SingleToUInt32Bits(bodyNodes[i].Max.w);
            uint right = BitConverter.SingleToUInt32Bits(bodyNodes[i].Min.w);
            uint escape = count == 0 ? links[right].z : (uint)i + 1;
            links[i] = new uvec4(CollisionGeometry.NoBlock, 0, escape, 0);
        }
        for (int i = 0; i < bodyNodes.Length; i++)
        {
            if (BitConverter.SingleToUInt32Bits(bodyNodes[i].Max.w) != 0)
                continue;
            uint right = BitConverter.SingleToUInt32Bits(bodyNodes[i].Min.w);
            links[i + 1].x = links[right].x = (uint)i;
        }
        CreateFilled<GpuBvhNode>(BodyBvhNodesBinding, bodyNodes, storage);
        CreateFilled<uint>(BodyBvhPrimitivesBinding, bodyOrder, storage);
        CreateFilled<uvec4>(BodyBvhRefitBinding, links, storage);
        CreateEmpty(BodyCandidatesBinding, DynamicBodyCount * (ulong)MaxBodyCandidates * sizeof(uint), storage, headroom: true);
        CreateEmpty(BodyCandidateCountsBinding, DynamicBodyCount * (ulong)sizeof(uint), storage, headroom: true);
        CreateEmpty(BlockContactOffsetsBinding, (blockCount + 1) * (ulong)sizeof(uint), storage, headroom: true);
        CreateEmpty(BlockContactCursorBinding, blockCount * (ulong)sizeof(uint), storage | transfer, headroom: true);

        ulong materialBytes = Math.Max(MaterialCount, 1) * (ulong)Unsafe.SizeOf<dvec4>();
        if (_buffers[MaterialsBinding].Handle == 0 || !_allocator.TryResizeRange(_buffers[MaterialsBinding], materialBytes))
        {
            _allocator.Recycle(_buffers[MaterialsBinding], _memory[MaterialsBinding]);
            _materialsMapped = _allocator.CreateMapped(materialBytes, storage, out _buffers[MaterialsBinding], out _memory[MaterialsBinding], headroom: true);
            LastBuildBufferGrowths++;
        }

        // The contact buffers depend only on the capacities, and are large, so they survive
        // rebuilds that keep them. Warm-start tables use lifetime handles and local feature
        // indices, so unrelated topology edits preserve their contents.
        if (_capacity != (MaxContacts, MaxContactReports) || _buffers[ContactsBinding].Handle == 0)
        {
            ReleaseCapacityBuffers();
            _capacity = (MaxContacts, MaxContactReports);

            CreateEmpty(PairsBinding, PairsPerContact * MaxContacts * (ulong)Unsafe.SizeOf<uvec2>(), storage);
            CreateEmpty(ContactsBinding, MaxContacts * (ulong)Unsafe.SizeOf<GpuContact>(), storage | BufferUsageFlags.TransferSrcBit);
            CreateEmpty(CountersBinding, CounterCount * sizeof(uint), storage | transfer);
            CreateEmpty(BlockContactRefsBinding, MaxBlockReferences * (ulong)sizeof(uint), storage);
            CreateEmpty(IndirectArgsBinding, 3 * (ulong)Unsafe.SizeOf<uvec4>(), storage | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferSrcBit);
            CreateEmpty(ContactReportsBinding, ReportHeaderSize + MaxContactReports * (ulong)Unsafe.SizeOf<GpuContactReport>(), storage | transfer);
            ReportReadbackMapped = _allocator.CreateReadback
            (
                ReportHeaderSize + MaxContactReports * (ulong)Unsafe.SizeOf<GpuContactReport>(), storage,
                out _buffers[ReportReadbackBinding], out _memory[ReportReadbackBinding]
            );

            ulong cacheSize = CacheCapacity * (ulong)Unsafe.SizeOf<GpuCacheEntry>();
            for (int i = 0; i < 2; i++)
            {
                _allocator.Create(cacheSize, storage | transfer, MemoryPropertyFlags.DeviceLocalBit, out _cacheTables[i], out _cacheMemory[i]);
                _allocator.Fill(_cacheTables[i], 0);
            }
        }

        CreateDescriptorSets();
    }

    /// <summary>Material per id, in <see cref="CollisionGeometry"/> order: x = friction, y = restitution.</summary>
    public unsafe void WriteMaterials(ReadOnlySpan<dvec4> materials)
    {
        if (materials.Length != MaterialCount)
            throw new ArgumentException("One material per body is required", nameof(materials));

        materials.CopyTo(new Span<dvec4>((void*)_materialsMapped, materials.Length));
    }

    /// <summary>
    /// Records a detection pass and the contact lists it produces. The initial pass of a step
    /// must run right after the solver's step_begin; continuous passes run between iterations.
    /// With <paramref name="sweep"/>, the narrow phase also sweeps every feature pair along its
    /// motion to the pass's end positions. Without, it finds the pairs within reach, then
    /// sweeps only the pairs whose moving bodies are in free flight, touching nothing: the
    /// inertial prediction is the motion of those alone.
    /// </summary>
    public unsafe void RecordDetection(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets, uint parity, bool initial, bool sweep, Action<string>? stamp = null)
    {
        uint mode = initial ? 0 : ModeContinuous;

        if (initial)
        {
            ComputeCommands.ComputeToTransfer(_vk, cmd);

            if (DynamicVertexCount > 0)
            {
                BufferCopy copy = new() { Size = DynamicVertexCount * (ulong)Unsafe.SizeOf<dvec4>() };
                _vk.CmdCopyBuffer(cmd, _buffers[VertexStartBinding], _buffers[VertexLastBinding], 1, &copy);
            }

            _vk.CmdFillBuffer(cmd, _buffers[CountersBinding], 0, Vk.WholeSize, 0);
            _vk.CmdFillBuffer(cmd, _cacheTables[parity & 1], 0, Vk.WholeSize, 0);
            ComputeCommands.TransferToCompute(_vk, cmd);
        }

        ComputeCommands.Dispatch(_vk, cmd, _surfaceUpdate, sets, new DispatchRange { Count = DynamicVertexCount, Flags = mode });
        ComputeCommands.Dispatch(_vk, cmd, _triangleBounds, sets, new DispatchRange { Count = DynamicTriangleCount, Flags = mode });
        ComputeCommands.Dispatch(_vk, cmd, _bodyBounds, sets, new DispatchRange { Count = DynamicBodyCount, Flags = mode });
        if (BodyCount >= BodyBvhThreshold)
            ComputeCommands.Dispatch(_vk, cmd, _bodyBvhRefit, sets, new DispatchRange { Count = _bodyBvhNodeCount });
        ComputeCommands.Dispatch(_vk, cmd, _bvhRefit, sets, new DispatchRange { Offset = SoftBvhNodeStart, Count = SoftBvhNodeCount });
        stamp?.Invoke("bounds");
        if (BodyCount >= BodyBvhThreshold)
            ComputeCommands.Dispatch(_vk, cmd, _bodyCandidates, sets, new DispatchRange { Count = DynamicBodyCount });
        stamp?.Invoke("body candidates");
        ComputeCommands.Dispatch(_vk, cmd, _contactPairs, sets, new DispatchRange { Count = DynamicTriangleCount, Flags = mode });
        stamp?.Invoke("broad phase");
        ComputeCommands.DispatchGroups(_vk, cmd, _contactArgs, sets, new DispatchRange { Flags = ModePairs }, 1);
        ComputeCommands.DispatchIndirect(_vk, cmd, _detect, sets, new DispatchRange { Flags = sweep ? ModeSweep : 0 }, _buffers[IndirectArgsBinding], (ulong)Unsafe.SizeOf<uvec4>());
        if (!sweep)
            ComputeCommands.DispatchIndirect(_vk, cmd, _detect, sets, new DispatchRange { Flags = ModeFreeFlight }, _buffers[IndirectArgsBinding], (ulong)Unsafe.SizeOf<uvec4>());
        stamp?.Invoke("narrow phase");

        RecordContactLists(cmd, sets);
        stamp?.Invoke("contact lists");
    }

    public GpuContact[] DownloadContacts(uint count)
    {
        return _allocator.Download<GpuContact>(_buffers[ContactsBinding], (int)Math.Min(count, MaxContacts));
    }

    /// <summary>Candidate pairs the last broad phase listed.</summary>
    public uint DownloadPairCount()
    {
        return _allocator.Download<uvec4>(_buffers[IndirectArgsBinding], 2)[1].w;
    }

    /// <summary>Records saving the step's contacts for the next step to warm start from.</summary>
    public void RecordStore(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.DispatchIndirect(_vk, cmd, _contactStore, sets, default, _buffers[IndirectArgsBinding], 0);
    }

    /// <summary>Records the reset of the contact reports, before the first substep of an update.</summary>
    public void RecordReportsReset(CommandBuffer cmd)
    {
        ComputeCommands.ComputeToTransfer(_vk, cmd);
        _vk.CmdFillBuffer(cmd, _buffers[ContactReportsBinding], 0, ReportHeaderSize, 0);
        ComputeCommands.TransferToCompute(_vk, cmd);
    }

    /// <summary>Records listing the substep's contacts that pushed, after its last dual update.</summary>
    public void RecordReports(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.DispatchIndirect(_vk, cmd, _contactReport, sets, default, _buffers[IndirectArgsBinding], 0);
    }

    /// <summary>Copies only the reports written this update to host memory, using a GPU dispatch count.</summary>
    public void RecordReportReadback(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.DispatchGroups(_vk, cmd, _contactArgs, sets, new DispatchRange { Flags = ModeReports }, 1);
        ComputeCommands.DispatchIndirect(_vk, cmd, _reportReadback, sets, default, _buffers[IndirectArgsBinding], 2 * (ulong)Unsafe.SizeOf<uvec4>());
    }

    private void RecordContactLists(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        Buffer arguments = _buffers[IndirectArgsBinding];

        ComputeCommands.DispatchGroups(_vk, cmd, _contactArgs, sets, default, 1);

        ComputeCommands.ComputeToTransfer(_vk, cmd);
        _vk.CmdFillBuffer(cmd, _buffers[BlockContactCursorBinding], 0, Vk.WholeSize, 0);
        ComputeCommands.TransferToCompute(_vk, cmd);

        ComputeCommands.DispatchIndirect(_vk, cmd, _contactLists, sets, default, arguments, 0);
        ComputeCommands.DispatchGroups(_vk, cmd, _contactScan, sets, default, 1);
        ComputeCommands.DispatchIndirect(_vk, cmd, _contactLists, sets, new DispatchRange { Flags = ModeFill }, arguments, 0);
        ComputeCommands.DispatchGroups(_vk, cmd, _contactOrder, sets, default, DynamicBodyCount);
    }

    private readonly byte[][] _uploadShadows = new byte[BindingCount][];
    private readonly int[] _uploadLengths = new int[BindingCount];
    public ulong LastBuildUploadBytes { get; private set; }
    public int LastBuildBufferGrowths { get; private set; }

    // Only buffers the shaders never modify may use their previous CPU upload as a
    // shadow. Simulation positions, bounds and refitted BVHs always upload fresh state.
    private static bool ImmutableBinding(int binding) => binding is
        VertexLocalBinding or VertexInfoBinding or TrianglesBinding or EdgesBinding
        or BvhPrimitivesBinding or BodyBvhPrimitivesBinding;

    private void CreateFilled<T>(int binding, ReadOnlySpan<T> data, BufferUsageFlags usage) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        bool retained = EnsureBuffer(binding, (ulong)bytes.Length, usage | BufferUsageFlags.TransferDstBit, headroom: true);
        int begin = 0, end = bytes.Length;
        if (ImmutableBinding(binding))
        {
            byte[] shadow = _uploadShadows[binding] ?? [];
            int oldLength = _uploadLengths[binding];
            if (retained)
            {
                int shared = Math.Min(oldLength, bytes.Length);
                // Compare cache-line sized spans using the runtime's vectorized equality.
                while (begin + 64 <= shared && bytes.Slice(begin, 64).SequenceEqual(shadow.AsSpan(begin, 64)))
                    begin += 64;
                if (oldLength == bytes.Length)
                    while (end - 64 >= begin && bytes.Slice(end - 64, 64).SequenceEqual(shadow.AsSpan(end - 64, 64)))
                        end -= 64;
                if (end > begin && end <= shared && bytes[begin..end].SequenceEqual(shadow.AsSpan(begin, end - begin)))
                    begin = end;
            }
            if (shadow.Length < bytes.Length)
                Array.Resize(ref shadow, Math.Max(bytes.Length, shadow.Length * 2));
            bytes[begin..end].CopyTo(shadow.AsSpan(begin));
            _uploadShadows[binding] = shadow;
            _uploadLengths[binding] = bytes.Length;
        }
        if (end > begin)
        {
            _allocator.Upload(bytes[begin..end], _buffers[binding], (ulong)begin);
            LastBuildUploadBytes += (ulong)(end - begin);
        }
    }

    private bool EnsureBuffer(int binding, ulong size, BufferUsageFlags usage, bool headroom)
    {
        if (_buffers[binding].Handle != 0 && _allocator.TryResizeRange(_buffers[binding], size))
            return true;
        _allocator.Recycle(_buffers[binding], _memory[binding]);
        _allocator.Create(size, usage, MemoryPropertyFlags.DeviceLocalBit, out _buffers[binding], out _memory[binding], headroom);
        LastBuildBufferGrowths++;
        return false;
    }

    private void CreateEmpty(int binding, ulong size, BufferUsageFlags usage, bool headroom = false)
    {
        EnsureBuffer(binding, size, usage, headroom);
    }

    private unsafe void CreateDescriptorSets()
    {
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        DescriptorPoolSize poolSize = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 2 * BindingCount };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 2,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };

        if (_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("Failed to create collision descriptor pool");

        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[BindingCount];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[BindingCount];

        for (int parity = 0; parity < 2; parity++)
        {
            DescriptorSetLayout layout = Layout;
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };

            if (_vk.AllocateDescriptorSets(_device, &allocInfo, out _sets[parity]) != Result.Success)
                throw new InvalidOperationException("Failed to allocate collision descriptor set");

            _buffers[CacheCurrentBinding] = _cacheTables[parity];
            _buffers[CachePreviousBinding] = _cacheTables[1 - parity];

            for (int i = 0; i < BindingCount; i++)
            {
                infos[i] = new DescriptorBufferInfo { Buffer = _buffers[i], Offset = 0, Range = _allocator.RangeOf(_buffers[i]) };
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[parity],
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &infos[i]
                };
            }

            _vk.UpdateDescriptorSets(_device, BindingCount, writes, 0, null);
        }

        _buffers[CacheCurrentBinding] = default;
        _buffers[CachePreviousBinding] = default;
    }

    /// <summary>
    /// Recycles what <see cref="Build"/> made from the geometry, for the next build to use
    /// again (see <see cref="GpuBufferAllocator.Recycle"/>); the contact buffers, which depend
    /// only on the capacities, the layout and the pipelines stay.
    /// </summary>
    public unsafe void Release()
    {
        for (int i = 0; i < BindingCount; i++)
        {
            if (CapacityBindings.Contains(i))
                continue;

            _allocator.Recycle(_buffers[i], _memory[i]);
            _buffers[i] = default;
            _memory[i] = default;
        }

        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _descriptorPool = default;
        _materialsMapped = 0;
    }

    private void ReleaseCapacityBuffers()
    {
        ReportReadbackMapped = 0;
        foreach (int i in CapacityBindings)
        {
            _allocator.Destroy(_buffers[i], _memory[i]);
            _buffers[i] = default;
            _memory[i] = default;
        }

        for (int i = 0; i < 2; i++)
        {
            _allocator.Destroy(_cacheTables[i], _cacheMemory[i]);
            _cacheTables[i] = default;
            _cacheMemory[i] = default;
        }
    }

    public unsafe void Dispose()
    {
        Release();
        ReleaseCapacityBuffers();
        _pipelineLoader?.Dispose();

        if (Layout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, Layout, null);
    }
}
