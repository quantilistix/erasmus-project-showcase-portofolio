using System.Runtime.CompilerServices;
using System.Diagnostics;

using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>
/// Simulates fixed, rigid and soft bodies on the GPU with augmented vertex block descent (AVBD),
/// with discrete and continuous collision detection between all of them.
/// <para>
/// Each <see cref="Update"/> records the whole step into one command buffer, submits it on the
/// queue given here and waits for it, then copies the bodies' new state back to their CPU-side
/// properties. Rendering data (<see cref="RigidBody.TransformBuffer"/>,
/// <see cref="SoftBody.VertexBuffer"/>) is written on the GPU and is ready for draws submitted
/// afterwards on the same queue.
/// </para>
/// </summary>
public sealed class PhysicsSystem : IDisposable
{
    private const ulong ReadbackHeaderSize = 32;

    /// <param name="queue">
    /// A queue with compute and transfer support: the one rendering uses when draws read the
    /// bodies' buffers, so that they see the results; otherwise one of its own, so that a step
    /// never waits behind draws waiting for the swapchain.
    /// </param>
    /// <param name="commandPool">A pool for that queue's family, created with the reset-command-buffer flag.</param>
    /// <param name="shaderDirectory">Directory holding the physics shaders (assets/shaders/physics).</param>
    public unsafe PhysicsSystem
    (
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        CommandPool commandPool,
        Queue queue,
        string shaderDirectory
    )
    {
        _vk = vk;
        _device = device;
        _commandPool = commandPool;
        _queue = queue;
        _allocator = new GpuBufferAllocator(vk, device, physicalDevice, commandPool, queue);
        _solver = new AvbdSolver(_allocator, vk, device);
        _collision = new GPUCollisionSystem(_allocator, vk, device);

        // Every pass binds both descriptor sets, so both layouts exist before any pipeline.
        _solver.CreateLayouts();
        _collision.CreateLayout();
        _solver.LoadPipelines(shaderDirectory, _collision.Layout);
        _collision.LoadPipelines(shaderDirectory, _solver.StateLayout);

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (_vk.AllocateCommandBuffers(_device, &allocInfo, out _commandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to allocate physics command buffer");

        QueryPoolCreateInfo queryInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 256
        };
        if (_vk.CreateQueryPool(_device, &queryInfo, null, out _profilePool) != Result.Success)
            throw new InvalidOperationException("Failed to create the physics profiling query pool");

        _vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
        _timestampPeriod = properties.Limits.TimestampPeriod;
        _canTime = properties.Limits.TimestampComputeAndGraphics;

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, &fenceInfo, null, out _fence) != Result.Success)
            throw new InvalidOperationException("Failed to create physics fence");
    }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _commandPool;
    private readonly Queue _queue;
    private readonly GpuBufferAllocator _allocator;
    private readonly AvbdSolver _solver;
    private readonly GPUCollisionSystem _collision;
    private readonly CollisionGeometry _geometry = new();
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;

    private readonly List<FixedBody> _fixedBodies = [];
    private readonly List<RigidBody> _rigidBodies = [];
    private readonly List<SoftBody> _softBodies = [];
    private readonly List<(PhysicsBody Body, PhysicsEdit Type, dvec4 A, dvec4 B)> _edits = [];

    // The bodies whose state is on the GPU, which differs from the lists above between a
    // Remove and the rebuild that follows it.
    private List<RigidBody> _builtRigidBodies = [];
    private List<SoftBody> _builtSoftBodies = [];
    // The bodies' surface materials, which follow their own materials on the GPU.
    private List<SurfaceMaterial> _paletteMaterials = [];

    private bool _dirty = true;
    private bool _built;
    private uint _parity;
    private QueryPool _profilePool;
    private float _timestampPeriod;
    private bool _canTime;
    private readonly List<string> _profileLabels = [];
    private Buffer _readbackBuffer;
    private DeviceMemory _readbackMemory;
    private nint _readbackMapped;
    private ulong _readbackSize;
    private readonly List<ContactReport> _contactReports = [];
    private GpuEdit[] _resolvedEdits = [];
    private dvec4[] _materials = [];
    private bool[] _continuousAfter = [];
    private uint _scheduledIterations;
    private uint _scheduledPasses;
    private readonly ulong[] _timestamps = new ulong[256];

    public PhysicsSettings Settings { get; } = new();
    public PhysicsTimings Timings { get; private set; }
    private static readonly bool ProfOn = Environment.GetEnvironmentVariable("MYT_PROF") == "1"; // PROF
    public double ProfIdle { get; set; } // PROF

    /// <summary>When set, updates time their phases on the GPU into <see cref="LastProfile"/>, up to 256 timestamps.</summary>
    public bool Profile { get; set; }
    private readonly List<(string Phase, double Milliseconds)> _lastProfile = [];
    /// <summary>GPU phase durations from the last update recorded with <see cref="Profile"/> enabled.</summary>
    public IReadOnlyList<(string Phase, double Milliseconds)> LastProfile => _lastProfile;

    /// <summary>Counts from the last simulated step.</summary>
    public PhysicsStatistics Statistics { get; private set; }

    /// <summary>
    /// The contacts that pushed during the last update, one entry per contact per substep,
    /// while <see cref="PhysicsSettings.ReportContacts"/> is on; empty otherwise.
    /// </summary>
    public IReadOnlyList<ContactReport> ContactReports => _contactReports;

    public IReadOnlyList<FixedBody> FixedBodies => _fixedBodies;
    public IReadOnlyList<RigidBody> RigidBodies => _rigidBodies;
    public IReadOnlyList<SoftBody> SoftBodies => _softBodies;

    public void Add(FixedBody body)
    {
        Attach(body);
        _fixedBodies.Add(body);
    }

    /// <summary>Adds a rigid body. Its <see cref="RigidBody.TransformBuffer"/> exists from here on.</summary>
    public void Add(RigidBody body)
    {
        Attach(body);
        body.CreateGpuResources(_allocator);
        _rigidBodies.Add(body);
    }

    /// <summary>Adds a soft body. Its <see cref="SoftBody.VertexBuffer"/> and index buffer exist from here on.</summary>
    public void Add(SoftBody body)
    {
        Attach(body);
        body.CreateGpuResources(_allocator);
        _softBodies.Add(body);
    }

    public void Remove(FixedBody body)
    {
        Detach(_fixedBodies, body);
    }

    /// <summary>Removes a rigid body and frees its transform buffer; waits for the physics' queue to be idle first.</summary>
    public void Remove(RigidBody body)
    {
        if (Detach(_rigidBodies, body))
        {
            body.DestroyGpuResources(_allocator);
            body.SolverIndex = -1;
        }
    }

    /// <summary>Removes a soft body and frees its render buffers; waits for the physics' queue to be idle first.</summary>
    public void Remove(SoftBody body)
    {
        if (Detach(_softBodies, body))
        {
            body.DestroyGpuResources(_allocator);
            body.SolverIndex = -1;
        }
    }

    /// <summary>
    /// Advances the simulation by <paramref name="deltaTime"/> seconds, in
    /// <see cref="PhysicsSettings.Substeps"/> equal steps, and waits for the GPU to finish. A
    /// constant time step simulates most consistently. Bodies added or removed since the last
    /// update are built in first, which takes longer than a normal update.
    /// </summary>
    public unsafe void Update(double deltaTime)
    {
        if (!(deltaTime > 0.0))
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "The time step must be positive");

        long updateStart = Stopwatch.GetTimestamp();
        double rebuildMilliseconds = 0;
        if (_dirty)
        {
            long profStart = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
            Rebuild();
            rebuildMilliseconds = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
            if (ProfOn) // PROF
                Console.WriteLine($"   physics rebuild {System.Diagnostics.Stopwatch.GetElapsedTime(profStart).TotalMilliseconds:F1} ms: {_fixedBodies.Count} fixed, {_rigidBodies.Count} rigid, {_collision.TriangleCount} triangles"); // PROF
        }

        long preparationStart = Stopwatch.GetTimestamp();
        _contactReports.Clear();
        if (_solver.BlockCount == 0)
        {
            _edits.Clear();
            Statistics = default;
            _lastProfile.Clear();
            Timings = new PhysicsTimings
            (
                rebuildMilliseconds,
                Stopwatch.GetElapsedTime(preparationStart).TotalMilliseconds, 0, 0, 0, 0,
                Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds
            );
            return;
        }

        bool reporting = Settings.ReportContacts;

        uint substeps = Math.Max(Settings.Substeps, 1);
        uint iterations = Math.Max(Settings.Iterations, 1);
        ReadOnlySpan<GpuEdit> edits = ResolveEdits();

        _solver.WriteParameters(CreateParameters(deltaTime / substeps, iterations, (uint)edits.Length));
        _solver.WriteEdits(edits);
        _collision.WriteMaterials(CreateMaterials());

        if (_scheduledIterations != iterations || _scheduledPasses != Settings.ContinuousCollisionPasses)
        {
            _continuousAfter = ContinuousCollisionSchedule(iterations, Settings.ContinuousCollisionPasses);
            _scheduledIterations = iterations;
            _scheduledPasses = Settings.ContinuousCollisionPasses;
        }

        long recordingStart = Stopwatch.GetTimestamp();
        _vk.ResetCommandBuffer(_commandBuffer, 0);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo);

        // Earlier work on this queue, such as last frame's draws, may still read what this overwrites.
        ComputeCommands.Barrier
        (
            _vk, _commandBuffer,
            PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit
        );

        _profileLabels.Clear();
        if (_canTime)
            _vk.CmdResetQueryPool(_commandBuffer, _profilePool, 0, 256);
        Stamp("begin", always: true);

        if (reporting)
            _collision.RecordReportsReset(_commandBuffer);

        Span<DescriptorSet> sets = stackalloc DescriptorSet[2];
        sets[0] = _solver.StateSet;
        Action<string>? stampPasses = Profile ? label => Stamp(label) : null;

        for (uint substep = 0; substep < substeps; substep++)
        {
            uint parity = _parity;
            _parity ^= 1;
            sets[1] = _collision.Set(parity);

            if (substep == 0 && edits.Length > 0)
                _solver.RecordEdits(_commandBuffer, sets);

            _solver.RecordStepBegin(_commandBuffer, sets);
            Stamp("step begin");
            _collision.RecordDetection(_commandBuffer, sets, parity, initial: true, sweep: !_continuousAfter.Contains(true), stampPasses);
            _solver.RecordContactSetup(_commandBuffer, sets, _collision.IndirectArgsBuffer);
            Stamp("contact setup");

            for (uint iteration = 0; iteration < iterations; iteration++)
            {
                _solver.RecordIteration(_commandBuffer, sets, iteration, _collision.IndirectArgsBuffer, stampPasses);
                if (_continuousAfter[iteration])
                {
                    _collision.RecordDetection(_commandBuffer, sets, parity, initial: false, sweep: true, stampPasses);
                    _solver.RecordContactSchedule(_commandBuffer, sets);
                }
            }

            _solver.RecordStepEnd(_commandBuffer, sets);
            _collision.RecordStore(_commandBuffer, sets);
            if (reporting)
                _collision.RecordReports(_commandBuffer, sets);
            Stamp("step end");
        }

        _solver.RecordOutputs(_commandBuffer, sets, Settings.UpdateRigidTransforms);
        if (reporting)
            _collision.RecordReportReadback(_commandBuffer, sets);
        RecordReadback();
        Stamp("outputs", always: true);

        ComputeCommands.Barrier
        (
            _vk, _commandBuffer,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
            AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
            PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.HostBit,
            AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit | AccessFlags.ShaderReadBit
                | AccessFlags.UniformReadBit | AccessFlags.HostReadBit
        );

        _vk.EndCommandBuffer(_commandBuffer);

        long submissionStart = Stopwatch.GetTimestamp();
        CommandBuffer cmd = _commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        Fence fence = _fence;
        if (_vk.QueueSubmit(_queue, 1, &submitInfo, fence) != Result.Success)
            throw new InvalidOperationException("Failed to submit the physics step");

        long waitStart = Stopwatch.GetTimestamp();
        _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
        _vk.ResetFences(_device, 1, &fence);

        long readbackStart = Stopwatch.GetTimestamp();
        ReadBack(reporting);
        ReadProfile();
        _edits.Clear();
        long updateEnd = Stopwatch.GetTimestamp();
        Timings = new PhysicsTimings
        (
            rebuildMilliseconds,
            Stopwatch.GetElapsedTime(preparationStart, recordingStart).TotalMilliseconds,
            Stopwatch.GetElapsedTime(recordingStart, submissionStart).TotalMilliseconds,
            Stopwatch.GetElapsedTime(submissionStart, waitStart).TotalMilliseconds,
            Stopwatch.GetElapsedTime(waitStart, readbackStart).TotalMilliseconds,
            Stopwatch.GetElapsedTime(readbackStart, updateEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(updateStart, updateEnd).TotalMilliseconds
        );
    }

    /// <summary>Records a GPU timestamp: always for the update's start and end, between phases only when profiling.</summary>
    private void Stamp(string label, bool always = false)
    {
        if (!_canTime || !(Profile || always) || _profileLabels.Count >= 256)
            return;

        _vk.CmdWriteTimestamp(_commandBuffer, PipelineStageFlags.BottomOfPipeBit, _profilePool, (uint)_profileLabels.Count);
        _profileLabels.Add(label);
    }

    /// <summary>Reads the timestamps back: the total into <see cref="Statistics"/>, phases into <see cref="LastProfile"/>.</summary>
    private unsafe void ReadProfile()
    {
        _lastProfile.Clear();
        if (_profileLabels.Count < 2)
            return;

        int count = _profileLabels.Count;
        fixed (ulong* data = _timestamps)
        {
            _vk.GetQueryPoolResults
            (
                _device, _profilePool, 0, (uint)count, (nuint)(count * sizeof(ulong)), data, sizeof(ulong),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit
            );
        }

        Statistics = Statistics with { GpuMilliseconds = (_timestamps[count - 1] - _timestamps[0]) * _timestampPeriod / 1e6 };
        if (!Profile)
            return;

        for (int i = 1; i < count; i++)
            _lastProfile.Add((_profileLabels[i], (_timestamps[i] - _timestamps[i - 1]) * _timestampPeriod / 1e6));
    }

    /// <summary>The contacts of the last step, for tests and debugging. Blocks until the queue is idle.</summary>
    internal GpuContact[] DownloadContacts()
    {
        return _collision.DownloadContacts(Statistics.ContactCount);
    }

    /// <summary>Candidate triangle pairs of the last broad phase, for tests. Blocks until the queue is idle.</summary>
    internal uint DownloadPairCount()
    {
        return _collision.DownloadPairCount();
    }

    internal ulong TopologyUploadBytes => _collision.LastBuildUploadBytes;
    internal int TopologyBufferGrowths => _collision.LastBuildBufferGrowths;

    internal (uint[] Colors, uvec4[] Arguments, uint[] Work) DownloadRigidWorkLists() => _solver.DownloadWorkLists();

    internal void MarkDirty(PhysicsBody? changedBody = null)
    {
        changedBody?.InvalidateContactIdentity();
        _dirty = true;
    }

    internal void QueueRigidPose(RigidBody body)
    {
        dquat q = body.Orientation;
        QueueRigidEdit(body, PhysicsEdit.RigidPose, new dvec4(body.WorldCenterOfMass, 0), new dvec4(q.x, q.y, q.z, q.w));
    }

    internal void QueueRigidEdit(RigidBody body, PhysicsEdit type, dvec4 a, dvec4 b)
    {
        if (body.SolverIndex >= 0)
            _edits.Add((body, type, a, b));
    }

    internal void QueueSoftEdit(SoftBody body, PhysicsEdit type, dvec3 value)
    {
        if (body.SolverIndex >= 0)
            _edits.Add((body, type, new dvec4(value, 0), dvec4.Zero));
    }

    private void Attach(PhysicsBody body)
    {
        if (body.System != null)
            throw new InvalidOperationException("The body is already in a physics system");

        body.InvalidateContactIdentity();
        body.System = this;
        _dirty = true;
    }

    private bool Detach<T>(List<T> bodies, T body) where T : PhysicsBody
    {
        if (!bodies.Remove(body))
            return false;

        long profStart = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        _vk.QueueWaitIdle(_queue);
        ProfIdle += System.Diagnostics.Stopwatch.GetElapsedTime(profStart).TotalMilliseconds; // PROF
        body.System = null;
        _edits.RemoveAll(edit => edit.Body == body);
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Rebuilds every GPU buffer from the bodies' CPU state. The state on the GPU is copied
    /// back first, so bodies that were already simulated carry on where they were.
    /// </summary>
    private void Rebuild()
    {
        long p0 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        _vk.QueueWaitIdle(_queue);

        if (_built)
            DownloadState();
        long p1 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF

        long p2 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF

        for (int i = 0; i < _rigidBodies.Count; i++)
            _rigidBodies[i].SolverIndex = i;
        for (int i = 0; i < _softBodies.Count; i++)
            _softBodies[i].SolverIndex = i;

        // Every upload of the rebuild goes in one submission.
        _allocator.BeginBatch();
        _solver.Build(_rigidBodies, _softBodies, Settings.StartStiffness);
        long p3 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        CollisionGeometry geometry = _geometry;
        geometry.Rebuild(_fixedBodies, _rigidBodies, _softBodies, _solver.NodeCount);
        long p4 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        _collision.Build(geometry, _solver.BlockCount, Settings.MaxContacts, Settings.MaxContactReports);
        long p5 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        _allocator.EndBatch();
        _allocator.FreeRecycled();
        long p6 = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        if (ProfOn) // PROF
            Console.WriteLine($"   rebuild parts: wait+download {(p1 - p0) / 1e6:F2} release {(p2 - p1) / 1e6:F2} solver {(p3 - p2) / 1e6:F2} geometry {(p4 - p3) / 1e6:F2} collision {(p5 - p4) / 1e6:F2} upload {(p6 - p5) / 1e6:F2}"); // PROF
        _paletteMaterials = geometry.PaletteMaterials;
        if (_materials.Length < geometry.MaterialCount)
            Array.Resize(ref _materials, Math.Max((int)geometry.MaterialCount, 2 * _materials.Length));

        ulong readbackSize = ReadbackHeaderSize
            + _solver.RigidCount * (ulong)Unsafe.SizeOf<GpuRigid>()
            + _solver.NodeCount * (ulong)Unsafe.SizeOf<GpuNode>();
        if (readbackSize > _readbackSize)
        {
            _allocator.Destroy(_readbackBuffer, _readbackMemory);
            _readbackSize = readbackSize + readbackSize / 4;
            _readbackMapped = _allocator.CreateReadback(_readbackSize, BufferUsageFlags.TransferDstBit, out _readbackBuffer, out _readbackMemory);
        }

        _builtRigidBodies = [.. _rigidBodies];
        _builtSoftBodies = [.. _softBodies];
        _dirty = false;
        _built = true;
    }

    private unsafe void DownloadState()
    {
        // Every completed update already copied all rigid state into this coherent buffer.
        // Reuse that snapshot when topology changes instead of submitting and waiting for
        // an identical GPU download. Soft bodies can opt out of per-step readback.
        GpuRigid* rigids = (GpuRigid*)((byte*)_readbackMapped + ReadbackHeaderSize);
        for (int i = 0; i < _builtRigidBodies.Count; i++)
            ApplyRigidState(_builtRigidBodies[i], rigids[i]);

        GpuNode[] nodes = _solver.DownloadNodes();
        for (int i = 0; i < _builtSoftBodies.Count; i++)
        {
            SoftBody body = _builtSoftBodies[i];
            uint offset = _solver.SoftNodeOffsets[i];
            for (int n = 0; n < body.NodeCount; n++)
                body.SetSimulatedNode(n, nodes[offset + n].Position.swizzle.xyz, nodes[offset + n].Velocity.swizzle.xyz);
        }
    }

    private static void ApplyRigidState(RigidBody body, GpuRigid state)
    {
        body.SolverColorHint = (uint)state.Inertia1.w;
        dquat orientation = new(state.Orientation.x, state.Orientation.y, state.Orientation.z, state.Orientation.w);
        body.SetSimulatedState
        (
            state.Position.swizzle.xyz, orientation,
            state.LinearVelocity.swizzle.xyz, state.AngularVelocity.swizzle.xyz
        );
    }

    private ReadOnlySpan<GpuEdit> ResolveEdits()
    {
        if (_resolvedEdits.Length < _edits.Count)
            Array.Resize(ref _resolvedEdits, Math.Max(_edits.Count, 2 * _resolvedEdits.Length));

        for (int i = 0; i < _edits.Count; i++)
        {
            (PhysicsBody body, PhysicsEdit type, dvec4 a, dvec4 b) = _edits[i];
            uvec4 header = body switch
            {
                RigidBody rigid => new uvec4
                (
                    (uint)type, _solver.NodeCount + (uint)rigid.SolverIndex, 1,
                    type == PhysicsEdit.RigidPose ? (uint)rigid.SolverIndex : CollisionGeometry.NoBlock
                ),
                SoftBody soft => new uvec4
                (
                    (uint)type, _solver.SoftNodeOffsets[soft.SolverIndex], (uint)soft.NodeCount,
                    type == PhysicsEdit.NodesTranslate ? _solver.RigidCount + (uint)soft.SolverIndex : CollisionGeometry.NoBlock
                ),
                _ => throw new InvalidOperationException("Only rigid and soft bodies can be edited")
            };

            _resolvedEdits[i] = new GpuEdit { Header = header, A = a, B = b };
        }

        return _resolvedEdits.AsSpan(0, _edits.Count);
    }

    private SolverParamsUBO CreateParameters(double timeStep, uint iterations, uint editCount)
    {
        PhysicsSettings s = Settings;
        return new SolverParamsUBO
        {
            GravityAndStep = new dvec4(s.Gravity, timeStep),
            Avbd = new dvec4(s.Alpha, s.Beta, s.Gamma, s.StartStiffness),
            Limits = new dvec4(s.MaxStiffness, s.MaxForce, s.MinimumBounceSpeed, s.MaxPenetrationDepth),
            Collision = new dvec4(s.ContactMargin, s.PenetrationTolerance, (double)s.RestitutionMixing, 0),
            Counts = new uvec4(_solver.NodeCount, _solver.RigidCount, _solver.SpringCount, iterations),
            CollisionCounts = new uvec4
            (
                _collision.VertexCount, _collision.TriangleCount,
                _collision.DynamicTriangleCount, _collision.BodyCount
            ),
            BodyCounts = new uvec4(_collision.DynamicBodyCount, editCount, _solver.RigidColorCount, (uint)_softBodies.Count),
            Capacities = new uvec4
            (
                _collision.MaxContacts, _collision.CacheCapacity,
                _collision.MaxBlockReferences, _solver.BlockCount
            )
        };
    }

    /// <summary>
    /// Materials in <see cref="CollisionGeometry"/> order: each body's own (rigid, soft, fixed),
    /// then the surface materials of the bodies that have them.
    /// </summary>
    private ReadOnlySpan<dvec4> CreateMaterials()
    {
        int i = 0;
        foreach (RigidBody body in _rigidBodies)
            _materials[i++] = new dvec4(body.Friction, body.Restitution, 0, 0);
        foreach (SoftBody body in _softBodies)
            _materials[i++] = new dvec4(body.Friction, body.Restitution, 0, 0);
        foreach (FixedBody body in _fixedBodies)
            _materials[i++] = new dvec4(body.Friction, body.Restitution, 0, 0);
        foreach (SurfaceMaterial material in _paletteMaterials)
            _materials[i++] = new dvec4(material.Friction, material.Restitution, 0, 0);
        return _materials.AsSpan(0, i);
    }

    /// <summary>
    /// After which iterations a continuous collision pass runs: the first right after the first
    /// iteration, the rest spread evenly, and never after the last iteration, where its contacts
    /// could no longer act. Sweeping happens only there, along motion the solver has already
    /// constrained: the inertial prediction carries a body resting on a floor a few millimetres
    /// down into it, and a sweep of that would find the sides of blocks level with the floor in
    /// its way. The initial pass finds only what is within reach, and sweeps the prediction only
    /// when no continuous pass follows.
    /// </summary>
    private static bool[] ContinuousCollisionSchedule(uint iterations, uint passes)
    {
        bool[] after = new bool[iterations];
        for (uint k = 0; k < passes; k++)
        {
            long iteration = (long)k * iterations / passes;
            if (iteration < iterations - 1)
                after[iteration] = true;
        }

        return after;
    }

    private unsafe void RecordReadback()
    {
        ComputeCommands.ComputeToTransfer(_vk, _commandBuffer);

        BufferCopy counters = new() { SrcOffset = 0, DstOffset = 0, Size = GPUCollisionSystem.CounterCount * sizeof(uint) };
        _vk.CmdCopyBuffer(_commandBuffer, _collision.CountersBuffer, _readbackBuffer, 1, &counters);

        if (_solver.RigidCount > 0)
        {
            BufferCopy rigids = new()
            {
                SrcOffset = 0,
                DstOffset = ReadbackHeaderSize,
                Size = _solver.RigidCount * (ulong)Unsafe.SizeOf<GpuRigid>()
            };
            _vk.CmdCopyBuffer(_commandBuffer, _solver.RigidsBuffer, _readbackBuffer, 1, &rigids);
        }

        ulong nodeSize = (ulong)Unsafe.SizeOf<GpuNode>();
        ulong nodesStart = ReadbackHeaderSize + _solver.RigidCount * (ulong)Unsafe.SizeOf<GpuRigid>();
        for (int i = 0; i < _softBodies.Count; i++)
        {
            if (!_softBodies[i].SyncToCpu)
                continue;

            ulong offset = _solver.SoftNodeOffsets[i] * nodeSize;
            BufferCopy nodes = new()
            {
                SrcOffset = offset,
                DstOffset = nodesStart + offset,
                Size = (ulong)_softBodies[i].NodeCount * nodeSize
            };
            _vk.CmdCopyBuffer(_commandBuffer, _solver.NodesBuffer, _readbackBuffer, 1, &nodes);
        }
    }

    private unsafe void ReadBack(bool reporting)
    {
        byte* data = (byte*)_readbackMapped;
        uint* counters = (uint*)data;
        byte* reports = (byte*)_collision.ReportReadbackMapped;
        uint* reportCounts = (uint*)reports;
        Statistics = new PhysicsStatistics(counters[0], counters[1], counters[2], 0, reporting ? reportCounts[1] : 0)
        {
            CandidatePairCount = counters[4],
            WarmStartedContactCount = counters[5],
            DynamicTriangleCount = _collision.DynamicTriangleCount
        };

        if (reporting)
            ReadContactReports((GpuContactReport*)(reports + GPUCollisionSystem.ReportHeaderSize), Math.Min(reportCounts[0], _collision.MaxContactReports));

        GpuRigid* rigids = (GpuRigid*)(data + ReadbackHeaderSize);
        uint serialBodies = 0;
        for (int i = 0; i < _rigidBodies.Count; i++)
        {
            ApplyRigidState(_rigidBodies[i], rigids[i]);
            if (_rigidBodies[i].SolverColorHint >= AvbdSolver.MaxRigidColors)
                serialBodies++;
        }
        Statistics = Statistics with { SerialRigidBodyCount = serialBodies };

        GpuNode* nodes = (GpuNode*)(rigids + _solver.RigidCount);
        for (int i = 0; i < _softBodies.Count; i++)
        {
            SoftBody body = _softBodies[i];
            if (!body.SyncToCpu)
                continue;

            GpuNode* bodyNodes = nodes + _solver.SoftNodeOffsets[i];
            for (int n = 0; n < body.NodeCount; n++)
                body.SetSimulatedNode(n, bodyNodes[n].Position.swizzle.xyz, bodyNodes[n].Velocity.swizzle.xyz);
        }
    }

    /// <summary>Turns the GPU reports into <see cref="ContactReports"/>; body ids are material ids, in rigid, soft, fixed order.</summary>
    private unsafe void ReadContactReports(GpuContactReport* reports, uint count)
    {
        PhysicsBody Body(uint id)
        {
            if (id < _rigidBodies.Count)
                return _rigidBodies[(int)id];
            id -= (uint)_rigidBodies.Count;
            if (id < _softBodies.Count)
                return _softBodies[(int)id];
            return _fixedBodies[(int)id - _softBodies.Count];
        }

        for (uint i = 0; i < count; i++)
        {
            GpuContactReport report = reports[i];
            _contactReports.Add(new ContactReport
            (
                Body(report.Bodies.x),
                Body(report.Bodies.y),
                report.Point.swizzle.xyz,
                new dvec3(report.Normal.swizzle.xyz),
                report.Normal.w,
                new dvec3(report.Friction.swizzle.xyz),
                report.Friction.w,
                report.Point.w
            ));
        }
    }

    public unsafe void Dispose()
    {
        _vk.DeviceWaitIdle(_device);

        foreach (RigidBody body in _rigidBodies)
        {
            body.DestroyGpuResources(_allocator);
            body.SolverIndex = -1;
            body.System = null;
        }

        foreach (SoftBody body in _softBodies)
        {
            body.DestroyGpuResources(_allocator);
            body.SolverIndex = -1;
            body.System = null;
        }

        foreach (FixedBody body in _fixedBodies)
            body.System = null;

        _solver.Dispose();
        _collision.Dispose();
        _allocator.ReleaseStaging();
        _allocator.Destroy(_readbackBuffer, _readbackMemory);
        _vk.DestroyFence(_device, _fence, null);
        _vk.DestroyQueryPool(_device, _profilePool, null);

        CommandBuffer cmd = _commandBuffer;
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
    }
}
