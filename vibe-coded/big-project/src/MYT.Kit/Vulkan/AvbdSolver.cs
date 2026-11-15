using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


// GPU structs below must match solver_common.glsl (std430/std140, dvec4-aligned).

[StructLayout(LayoutKind.Sequential)]
internal struct GpuNode
{
    public dvec4 Position;          // xyz = current iterate x, w = mass (infinite = pinned)
    public dvec4 Inertial;          // xyz = inertial target y
    public dvec4 Previous;          // xyz = x^t, position at the start of the step
    public dvec4 Velocity;
    public dvec4 PreviousVelocity;  // velocity one step earlier, for adaptive initialization
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuRigid
{
    public dvec4 Position;              // xyz = center of mass, w = mass
    public dvec4 Orientation;           // quaternion (x, y, z, w)
    public dvec4 LinearVelocity;
    public dvec4 AngularVelocity;       // world frame
    public dvec4 InertialPosition;
    public dvec4 InertialOrientation;
    public dvec4 PreviousPosition;
    public dvec4 PreviousOrientation;
    public dvec4 PreviousLinearVelocity;
    public dvec4 Inertia0;              // columns of the body-frame inertia tensor about the center of mass; w = gravity scale
    public dvec4 Inertia1;              // xyz = inertia column, w = previous solver color hint
    public dvec4 Inertia2;
    public dvec4 CenterOfMass;          // xyz = center of mass in mesh space, w = 1 if the rotation is fixed
}

/// <summary>A <see cref="Spring"/> as the solver stores it: relative quantities only, so float.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSpring
{
    public uint Node1;
    public uint Node2;
    public float RestLength;
    public float Stiffness;
    public float Damping;
    public float Durability;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuEdit
{
    public uvec4 Header;                // x = type, y = first block, z = block count, w = collision body to reset
    public uvec4 Padding;
    public dvec4 A;
    public dvec4 B;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SolverParamsUBO
{
    public dvec4 GravityAndStep;        // xyz = gravity, w = time step
    public dvec4 Avbd;                  // x = alpha, y = beta, z = gamma, w = starting stiffness
    public dvec4 Limits;                // x = max stiffness, y = max force, z = minimum bounce speed, w = max persistent depth
    public dvec4 Collision;             // x = contact margin, y = penetration tolerance, z = restitution mixing
    public uvec4 Counts;                // x = nodes, y = rigid bodies, z = springs, w = iterations
    public uvec4 CollisionCounts;       // x = collision vertices, y = triangles, z = dynamic triangles, w = bodies
    public uvec4 BodyCounts;            // x = dynamic bodies, y = edits, z = rigid colors
    public uvec4 Capacities;            // x = max contacts, y = cache slots, z = max block contact refs, w = blocks
}

/// <summary>Edit types; values match the EDIT_* constants in solver_common.glsl.</summary>
internal enum PhysicsEdit : uint
{
    RigidPose = 0,
    RigidLinearVelocity = 1,
    RigidAngularVelocity = 2,
    RigidImpulse = 3,
    NodesVelocity = 4,
    NodesTranslate = 5,
    NodesAddVelocity = 6
}


/// <summary>
/// Augmented vertex block descent over every dynamic degree of freedom: soft body nodes are
/// 3-DOF blocks, rigid bodies 6-DOF blocks. Owns the simulation state on the GPU (descriptor
/// set 0) and records the solver passes of a time step, so the CPU never reads state back
/// during a step.
/// </summary>
internal sealed class AvbdSolver : IDisposable
{
    public const uint MaxRigidColors = 8;

    private const uint RigidColorRounds = 4;
    private uint _rigidColorHalf;
    // Threads sharing one node in node_solve.comp.
    private const uint NodeSolveCluster = 64;
    private const uint ModeDual = 1;
    private const uint ModeFind = 1;
    private const uint ModeApply = 2;
    private const int InitialEditCapacity = 64;

    private const int ParamsBinding = 0;
    private const int NodesBinding = 1;
    private const int RigidsBinding = 2;
    private const int SpringsBinding = 3;
    private const int SpringStatesBinding = 4;
    private const int NodeSpringOffsetsBinding = 5;
    private const int NodeSpringIndicesBinding = 6;
    private const int ColorNodesBinding = 7;
    private const int RigidTransformsBinding = 8;
    private const int EditsBinding = 9;
    private const int RigidColorsBinding = 10;
    private const int NodeBodiesBinding = 11;
    private const int GuessFractionsBinding = 12;
    private const int RigidWorkBinding = 13;
    private const int RigidArgumentsBinding = 14;
    private const int StateBindingCount = 15;

    private const int SoftRenderBindingCount = 4;

    public AvbdSolver(GpuBufferAllocator allocator, Vk vk, Device device)
    {
        _allocator = allocator;
        _vk = vk;
        _device = device;
    }

    private readonly GpuBufferAllocator _allocator;
    private readonly Vk _vk;
    private readonly Device _device;

    private ComputePipelineLoader? _pipelineLoader;
    private ComputePipeline _applyEdits;
    private ComputePipeline _stepBegin;
    private ComputePipeline _springUpdate;
    private ComputePipeline _nodeSolve;
    private ComputePipeline _guessLimit;
    private ComputePipeline _rigidColor;
    private ComputePipeline _rigidSolve;
    private ComputePipeline _rigidSolveDense;
    private ComputePipeline _rigidSolveSerial;
    private ComputePipeline _rigidWork;
    private ComputePipeline _dualUpdate;
    private ComputePipeline _stepEnd;
    private ComputePipeline _restitution;
    private ComputePipeline _rigidTransforms;
    private ComputePipeline _softRender;

    private DescriptorSetLayout _softRenderLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _stateSet;
    private DescriptorSet[] _softRenderSets = [];

    private readonly Buffer[] _stateBuffers = new Buffer[StateBindingCount];
    private readonly DeviceMemory[] _stateMemory = new DeviceMemory[StateBindingCount];
    private nint _paramsMapped;
    private nint _editsMapped;
    private int _editCapacity;

    private List<RigidBody> _rigidBodies = [];
    private GpuRigid[] _rigidBuildState = [];
    private List<SoftBody> _softBodies = [];
    private (uint Offset, uint Count)[] _colorRanges = [];

    public DescriptorSetLayout StateLayout { get; private set; }
    public DescriptorSet StateSet => _stateSet;

    public uint NodeCount { get; private set; }
    public uint RigidCount { get; private set; }
    public uint SpringCount { get; private set; }
    public uint BlockCount => NodeCount + RigidCount;
    public uint RigidColorCount => Math.Clamp(RigidCount, 1, MaxRigidColors);

    /// <summary>First node of each soft body, in the order given to <see cref="Build"/>.</summary>
    public uint[] SoftNodeOffsets { get; private set; } = [];

    public Buffer NodesBuffer => _stateBuffers[NodesBinding];
    public Buffer RigidsBuffer => _stateBuffers[RigidsBinding];

    public void CreateLayouts()
    {
        StateLayout = CreateLayout
        (
            [
                DescriptorType.UniformBuffer,
                .. Enumerable.Repeat(DescriptorType.StorageBuffer, StateBindingCount - 1)
            ]
        );
        _softRenderLayout = CreateLayout([.. Enumerable.Repeat(DescriptorType.StorageBuffer, SoftRenderBindingCount)]);
    }

    /// <summary>Solver passes bind the state as set 0 and the collision data as set 1.</summary>
    public void LoadPipelines(string shaderDirectory, DescriptorSetLayout collisionLayout)
    {
        _pipelineLoader = new ComputePipelineLoader(_vk, _device);
        uint pushSize = (uint)Unsafe.SizeOf<DispatchRange>();
        DescriptorSetLayout[] stateAndCollision = [StateLayout, collisionLayout];

        ComputePipeline Load(string name, DescriptorSetLayout[] layouts) =>
            _pipelineLoader.Load(Path.Combine(shaderDirectory, name), layouts, pushSize);

        _applyEdits = Load("apply_edits.comp", stateAndCollision);
        _stepBegin = Load("step_begin.comp", stateAndCollision);
        _springUpdate = Load("spring_update.comp", stateAndCollision);
        _nodeSolve = Load("node_solve.comp", stateAndCollision);
        _guessLimit = Load("guess_limit.comp", stateAndCollision);
        _rigidColor = Load("rigid_color.comp", stateAndCollision);
        _rigidSolve = Load("rigid_solve.comp", stateAndCollision);
        _rigidSolveDense = Load("rigid_solve_dense.comp", stateAndCollision);
        _rigidSolveSerial = Load("rigid_solve_serial.comp", stateAndCollision);
        _rigidWork = Load("rigid_work.comp", stateAndCollision);
        _dualUpdate = Load("dual_update.comp", stateAndCollision);
        _stepEnd = Load("step_end.comp", stateAndCollision);
        _restitution = Load("restitution.comp", stateAndCollision);
        _rigidTransforms = Load("rigid_transforms.comp", stateAndCollision);
        _softRender = Load("soft_render_vertices.comp", [StateLayout, _softRenderLayout]);
    }

    /// <summary>
    /// Concatenates every body's state into the solver's buffers. The bodies' GPU resources must
    /// exist already; their CPU-side state is only read here.
    /// </summary>
    public void Build(IReadOnlyList<RigidBody> rigidBodies, IReadOnlyList<SoftBody> softBodies, double startStiffness)
    {
        _rigidBodies = [.. rigidBodies];
        _softBodies = [.. softBodies];

        BuildNodes
        (
            out GpuNode[] nodes, out uint[] nodeBodies, out Spring[] springs, out uint[] colorNodes,
            out uint[] nodeSpringOffsets, out uint[] nodeSpringIndices
        );

        if (_rigidBuildState.Length < _rigidBodies.Count)
            Array.Resize(ref _rigidBuildState, Math.Max(_rigidBodies.Count, 2 * _rigidBuildState.Length));
        for (int r = 0; r < _rigidBodies.Count; r++)
            _rigidBuildState[r] = ToGpuRigid(_rigidBodies[r]);
        ReadOnlySpan<GpuRigid> rigids = _rigidBuildState.AsSpan(0, _rigidBodies.Count);
        GpuSpring[] gpuSprings =
        [
            .. springs.Select
            (
                s => new GpuSpring
                {
                    Node1 = s.Node1,
                    Node2 = s.Node2,
                    RestLength = (float)s.RestLength,
                    Stiffness = (float)s.Stiffness,
                    Damping = (float)s.Damping,
                    Durability = (float)s.Durability
                }
            )
        ];
        vec4[] springStates =
        [
            .. springs.Select(s => new vec4(double.IsInfinity(s.Stiffness) ? (float)startStiffness : (float)s.Stiffness, 0, 0, 0))
        ];

        NodeCount = (uint)nodes.Length;
        RigidCount = (uint)rigids.Length;
        SpringCount = (uint)springs.Length;

        const BufferUsageFlags storage = BufferUsageFlags.StorageBufferBit;
        const BufferUsageFlags readable = storage | BufferUsageFlags.TransferSrcBit;

        if (_stateBuffers[ParamsBinding].Handle == 0)
            _paramsMapped = _allocator.CreateMapped
            (
                (ulong)Unsafe.SizeOf<SolverParamsUBO>(), BufferUsageFlags.UniformBufferBit,
                out _stateBuffers[ParamsBinding], out _stateMemory[ParamsBinding]
            );

        CreateFilled<GpuNode>(NodesBinding, nodes, readable);
        CreateFilled<GpuRigid>(RigidsBinding, rigids, readable);
        CreateFilled<GpuSpring>(SpringsBinding, gpuSprings, storage);
        CreateFilled<vec4>(SpringStatesBinding, springStates, storage);
        CreateFilled<uint>(NodeSpringOffsetsBinding, nodeSpringOffsets, storage);
        CreateFilled<uint>(NodeSpringIndicesBinding, nodeSpringIndices, storage);
        CreateFilled<uint>(ColorNodesBinding, colorNodes, storage);
        CreateEmpty(RigidTransformsBinding, RigidCount * (ulong)Unsafe.SizeOf<mat4>(), readable);
        CreateEmpty(RigidColorsBinding, 2 * RigidCount * (ulong)sizeof(uint), storage | BufferUsageFlags.TransferSrcBit);
        CreateFilled<uint>(NodeBodiesBinding, nodeBodies, storage);
        CreateEmpty(GuessFractionsBinding, ((ulong)_softBodies.Count + RigidCount) * sizeof(uint), storage);
        CreateEmpty(RigidWorkBinding, (2 * MaxRigidColors + 1) * (ulong)RigidCount * sizeof(uint), storage | BufferUsageFlags.TransferSrcBit);
        CreateEmpty(RigidArgumentsBinding, (2 * MaxRigidColors + 1) * (ulong)Unsafe.SizeOf<uvec4>(), storage | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferSrcBit);
        if (_stateBuffers[EditsBinding].Handle == 0)
            CreateEditsBuffer(InitialEditCapacity);

        CreateDescriptorSets();
    }

    public unsafe void WriteParameters(in SolverParamsUBO parameters)
    {
        *(SolverParamsUBO*)_paramsMapped = parameters;
    }

    /// <summary>Uploads the edits for the next <see cref="RecordEdits"/>, growing the buffer if needed.</summary>
    public unsafe void WriteEdits(ReadOnlySpan<GpuEdit> edits)
    {
        if (edits.Length > _editCapacity)
        {
            _allocator.Destroy(_stateBuffers[EditsBinding], _stateMemory[EditsBinding]);
            CreateEditsBuffer(Math.Max(edits.Length, 2 * _editCapacity));
            WriteSet(_stateSet, [_stateBuffers[EditsBinding]], [DescriptorType.StorageBuffer], EditsBinding);
        }

        edits.CopyTo(new Span<GpuEdit>((void*)_editsMapped, edits.Length));
    }

    public void RecordEdits(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.Dispatch(_vk, cmd, _applyEdits, sets, new DispatchRange { Count = BlockCount });
    }

    /// <summary>Records x^t, the inertial targets, the initial guess and the warm-started springs.</summary>
    public void RecordStepBegin(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.Dispatch(_vk, cmd, _stepBegin, sets, new DispatchRange { Count = BlockCount });
        ComputeCommands.Dispatch(_vk, cmd, _springUpdate, sets, new DispatchRange { Count = SpringCount });
    }

    /// <summary>
    /// Prepares the iterations once the step's contacts exist: pulls the initial guess back to
    /// where impacts begin, and colors the rigid bodies over their contacts.
    /// </summary>
    /// <param name="contactArguments">Indirect dispatch arguments with one thread per contact at offset 0.</param>
    public void RecordContactSetup(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets, Buffer contactArguments)
    {
        ComputeCommands.Dispatch(_vk, cmd, _guessLimit, sets, new DispatchRange { Count = (uint)_softBodies.Count + RigidCount });
        ComputeCommands.DispatchIndirect(_vk, cmd, _guessLimit, sets, new DispatchRange { Flags = ModeFind }, contactArguments, 0);
        ComputeCommands.Dispatch(_vk, cmd, _guessLimit, sets, new DispatchRange { Count = BlockCount, Flags = ModeApply });

        // One workgroup per rigid body here and in the other per-body passes, whose threads
        // share out the body's contacts.
        if (RigidCount > 0)
        {
            for (uint round = 0; round < RigidColorRounds; round++)
                ComputeCommands.DispatchGroups(_vk, cmd, _rigidColor, sets, new DispatchRange { Count = RigidCount, Flags = round }, RigidCount);
        }
        if (RigidCount > 0)
            ComputeCommands.DispatchGroups(_vk, cmd, _rigidColor, sets, new DispatchRange { Count = RigidCount, Flags = 4 }, RigidCount);
        _rigidColorHalf = 1;
        RecordRigidWork(cmd, sets);
    }

    /// <summary>Prunes conflicts introduced by continuous detection before another solve.</summary>
    public void RecordContactSchedule(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        if (RigidCount > 0)
            ComputeCommands.DispatchGroups(_vk, cmd, _rigidColor, sets,
                new DispatchRange { Count = RigidCount, Flags = 4 + _rigidColorHalf }, RigidCount);
        _rigidColorHalf = 1 - _rigidColorHalf;
        RecordRigidWork(cmd, sets);
    }

    private void RecordRigidWork(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        if (RigidCount > 0)
            ComputeCommands.DispatchGroups(_vk, cmd, _rigidWork, sets,
                new DispatchRange { Count = 2 * MaxRigidColors + 1, Flags = _rigidColorHalf }, 2 * MaxRigidColors + 1);
    }

    /// <summary>One primal pass over every block color, then the dual updates.</summary>
    public void RecordIteration(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets, uint iteration, Buffer contactArguments, Action<string>? stamp = null)
    {
        foreach ((uint offset, uint count) in _colorRanges)
        {
            DispatchRange range = new() { Offset = offset, Count = count, Iteration = iteration };
            ComputeCommands.DispatchGroups(_vk, cmd, _nodeSolve, sets, range, (count * NodeSolveCluster + ComputeCommands.WorkgroupSize - 1) / ComputeCommands.WorkgroupSize);
            stamp?.Invoke("node color");
        }

        for (uint color = 0; color < RigidColorCount && RigidCount > 0; color++)
        {
            DispatchRange range = new() { Count = RigidCount, Iteration = iteration, Flags = color };
            ulong argumentSize = (ulong)Unsafe.SizeOf<uvec4>();
            ComputeCommands.DispatchIndirect(_vk, cmd, _rigidSolve, sets, range, _stateBuffers[RigidArgumentsBinding], color * argumentSize);
            range.Flags = color + MaxRigidColors;
            ComputeCommands.DispatchIndirect(_vk, cmd, _rigidSolveDense, sets, range, _stateBuffers[RigidArgumentsBinding], range.Flags * argumentSize);
            stamp?.Invoke("rigid color");
        }

        if (RigidCount > 0)
        {
            DispatchRange overflow = new() { Count = RigidCount, Iteration = iteration, Flags = 2 * MaxRigidColors };
            ComputeCommands.DispatchIndirect(_vk, cmd, _rigidSolveSerial, sets, overflow, _stateBuffers[RigidArgumentsBinding], overflow.Flags * (ulong)Unsafe.SizeOf<uvec4>());
        }

        ComputeCommands.DispatchIndirect(_vk, cmd, _dualUpdate, sets, default, contactArguments, 0);
        stamp?.Invoke("contact dual");
        ComputeCommands.Dispatch(_vk, cmd, _springUpdate, sets, new DispatchRange { Count = SpringCount, Flags = ModeDual });
        stamp?.Invoke("spring dual");
    }

    /// <summary>Velocities from the solved positions, then rigid body bounces.</summary>
    public void RecordStepEnd(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets)
    {
        ComputeCommands.Dispatch(_vk, cmd, _stepEnd, sets, new DispatchRange { Count = BlockCount, Flags = _rigidColorHalf });
        if (RigidCount > 0)
            ComputeCommands.DispatchGroups(_vk, cmd, _restitution, sets, new DispatchRange { Count = RigidCount }, RigidCount);
    }

    /// <summary>Writes every body's render data: rigid model matrices and soft body vertices.</summary>
    public unsafe void RecordOutputs(CommandBuffer cmd, ReadOnlySpan<DescriptorSet> sets, bool updateRigidTransforms)
    {
        if (updateRigidTransforms && RigidCount > 0)
        {
            ComputeCommands.Dispatch(_vk, cmd, _rigidTransforms, sets, new DispatchRange { Count = RigidCount });
            ComputeCommands.ComputeToTransfer(_vk, cmd);

            ulong transformSize = (ulong)Unsafe.SizeOf<mat4>();
            for (int i = 0; i < _rigidBodies.Count; i++)
            {
                BufferCopy region = new() { SrcOffset = (ulong)i * transformSize, DstOffset = 0, Size = transformSize };
                _vk.CmdCopyBuffer(cmd, _stateBuffers[RigidTransformsBinding], _rigidBodies[i].TransformBuffer, 1, &region);
            }
        }

        for (int i = 0; i < _softBodies.Count; i++)
        {
            DispatchRange range = new() { Offset = SoftNodeOffsets[i], Count = (uint)_softBodies[i].NodeCount };
            ComputeCommands.Dispatch(_vk, cmd, _softRender, [_stateSet, _softRenderSets[i]], range);
        }
    }

    public GpuNode[] DownloadNodes() => _allocator.Download<GpuNode>(_stateBuffers[NodesBinding], (int)NodeCount);
    public GpuRigid[] DownloadRigids() => _allocator.Download<GpuRigid>(_stateBuffers[RigidsBinding], (int)RigidCount);

    internal (uint[] Colors, uvec4[] Arguments, uint[] Work) DownloadWorkLists() =>
    (
        _allocator.Download<uint>(_stateBuffers[RigidColorsBinding], (int)(2 * RigidCount))[(int)(_rigidColorHalf * RigidCount)..(int)((_rigidColorHalf + 1) * RigidCount)],
        _allocator.Download<uvec4>(_stateBuffers[RigidArgumentsBinding], (int)(2 * MaxRigidColors + 1)),
        _allocator.Download<uint>(_stateBuffers[RigidWorkBinding], (int)((2 * MaxRigidColors + 1) * RigidCount))
    );

    /// <summary>
    /// Lays out every soft body's nodes and springs in one global index space. Springs never
    /// cross bodies, so each body is colored on its own and same-numbered colors are merged.
    /// </summary>
    private void BuildNodes
    (
        out GpuNode[] nodes,
        out uint[] nodeBodies,
        out Spring[] springs,
        out uint[] colorNodes,
        out uint[] nodeSpringOffsets,
        out uint[] nodeSpringIndices
    )
    {
        int nodeCount = _softBodies.Sum(b => b.NodeCount);
        nodes = new GpuNode[nodeCount];
        nodeBodies = new uint[nodeCount];
        uint[] nodeColors = new uint[nodeCount];
        List<Spring> globalSprings = [];
        SoftNodeOffsets = new uint[_softBodies.Count];

        uint offset = 0;
        for (int b = 0; b < _softBodies.Count; b++)
        {
            SoftBody body = _softBodies[b];
            SoftNodeOffsets[b] = offset;

            for (int i = 0; i < body.NodeCount; i++)
            {
                dvec4 position = new(body.Positions[i], body.Masses[i]);
                dvec4 velocity = new(body.Velocities[i], 0);
                nodes[offset + i] = new GpuNode
                {
                    Position = position,
                    Inertial = position,
                    Previous = position,
                    Velocity = velocity,
                    PreviousVelocity = velocity
                };
                nodeBodies[offset + i] = (uint)b;
            }

            Spring[] bodySprings = body.Springs.ToArray();
            uint[] localColors = SpringUtility.ColorGraph(body.NodeCount, bodySprings);
            Array.Copy(localColors, 0, nodeColors, offset, localColors.Length);

            foreach (Spring spring in bodySprings)
            {
                Spring global = spring;
                global.Node1 += offset;
                global.Node2 += offset;
                globalSprings.Add(global);
            }

            offset += (uint)body.NodeCount;
        }

        springs = [.. globalSprings];

        uint colorCount = nodeCount == 0 ? 0 : nodeColors.Max() + 1;
        List<uint>[] byColor = new List<uint>[colorCount];
        for (int c = 0; c < colorCount; c++)
            byColor[c] = [];
        for (uint n = 0; n < nodeCount; n++)
            byColor[nodeColors[n]].Add(n);

        colorNodes = [.. byColor.SelectMany(c => c)];
        _colorRanges = new (uint, uint)[colorCount];
        uint rangeStart = 0;
        for (int c = 0; c < colorCount; c++)
        {
            _colorRanges[c] = (rangeStart, (uint)byColor[c].Count);
            rangeStart += (uint)byColor[c].Count;
        }

        nodeSpringOffsets = new uint[nodeCount + 1];
        foreach (Spring spring in springs)
        {
            nodeSpringOffsets[spring.Node1 + 1]++;
            nodeSpringOffsets[spring.Node2 + 1]++;
        }
        for (int n = 0; n < nodeCount; n++)
            nodeSpringOffsets[n + 1] += nodeSpringOffsets[n];

        nodeSpringIndices = new uint[nodeSpringOffsets[nodeCount]];
        uint[] cursor = (uint[])nodeSpringOffsets.Clone();
        for (uint s = 0; s < springs.Length; s++)
        {
            nodeSpringIndices[cursor[springs[s].Node1]++] = s;
            nodeSpringIndices[cursor[springs[s].Node2]++] = s;
        }
    }

    private static GpuRigid ToGpuRigid(RigidBody body)
    {
        dquat q = body.Orientation.Normalized;
        dvec4 position = new(body.WorldCenterOfMass, body.Mass);
        dvec4 orientation = new(q.x, q.y, q.z, q.w);
        dvec4 linearVelocity = new(body.LinearVelocity, 0);
        dmat3 inertia = body.LocalInertia;

        return new GpuRigid
        {
            Position = position,
            Orientation = orientation,
            LinearVelocity = linearVelocity,
            AngularVelocity = new dvec4(body.AngularVelocity, 0),
            InertialPosition = position,
            InertialOrientation = orientation,
            PreviousPosition = position,
            PreviousOrientation = orientation,
            PreviousLinearVelocity = linearVelocity,
            Inertia0 = new dvec4(inertia.m00, inertia.m01, inertia.m02, body.GravityScale),
            Inertia1 = new dvec4(inertia.m10, inertia.m11, inertia.m12, body.SolverColorHint),
            Inertia2 = new dvec4(inertia.m20, inertia.m21, inertia.m22, 0),
            CenterOfMass = new dvec4(body.CenterOfMass, body.FixedRotation ? 1 : 0)
        };
    }

    private void CreateEditsBuffer(int capacity)
    {
        _editCapacity = capacity;
        _editsMapped = _allocator.CreateMapped
        (
            (ulong)(capacity * Unsafe.SizeOf<GpuEdit>()), BufferUsageFlags.StorageBufferBit,
            out _stateBuffers[EditsBinding], out _stateMemory[EditsBinding]
        );
    }

    // Buffer residency survives topology changes. Logical descriptor ranges still
    // follow live counts; allocations grow only when capacity is exhausted.
    private void CreateFilled<T>(int binding, ReadOnlySpan<T> data, BufferUsageFlags usage) where T : unmanaged
    {
        CreateEmpty(binding, (ulong)(data.Length * Unsafe.SizeOf<T>()), usage | BufferUsageFlags.TransferDstBit);
        if (!data.IsEmpty)
            _allocator.Upload(data, _stateBuffers[binding]);
    }

    private void CreateEmpty(int binding, ulong size, BufferUsageFlags usage)
    {
        if (_stateBuffers[binding].Handle != 0 && _allocator.TryResizeRange(_stateBuffers[binding], size))
            return;
        _allocator.Recycle(_stateBuffers[binding], _stateMemory[binding]);
        _allocator.Create(size, usage, MemoryPropertyFlags.DeviceLocalBit,
            out _stateBuffers[binding], out _stateMemory[binding], headroom: true);
    }

    private unsafe DescriptorSetLayout CreateLayout(DescriptorType[] types)
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[types.Length];
        for (int i = 0; i < types.Length; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = types[i],
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = (uint)types.Length,
            PBindings = bindings
        };

        if (_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out DescriptorSetLayout layout) != Result.Success)
            throw new InvalidOperationException("Failed to create solver descriptor set layout");

        return layout;
    }

    private unsafe void CreateDescriptorSets()
    {
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        uint softCount = (uint)_softBodies.Count;

        DescriptorPoolSize* poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 },
            new()
            {
                Type = DescriptorType.StorageBuffer,
                DescriptorCount = StateBindingCount - 1 + softCount * SoftRenderBindingCount
            }
        };

        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1 + softCount,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes
        };

        if (_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("Failed to create solver descriptor pool");

        _stateSet = AllocateSet(StateLayout);
        DescriptorType[] stateTypes =
        [
            DescriptorType.UniformBuffer,
            .. Enumerable.Repeat(DescriptorType.StorageBuffer, StateBindingCount - 1)
        ];
        WriteSet(_stateSet, _stateBuffers, stateTypes, 0);

        _softRenderSets = new DescriptorSet[softCount];
        DescriptorType[] softTypes = [.. Enumerable.Repeat(DescriptorType.StorageBuffer, SoftRenderBindingCount)];
        for (int i = 0; i < softCount; i++)
        {
            SoftBody body = _softBodies[i];
            _softRenderSets[i] = AllocateSet(_softRenderLayout);
            WriteSet
            (
                _softRenderSets[i],
                [
                    body.VertexBuffer,
                    body.IndexBuffer,
                    body.VertexTriangleOffsetsBuffer,
                    body.VertexTriangleIndicesBuffer
                ],
                softTypes,
                0
            );
        }
    }

    private unsafe DescriptorSet AllocateSet(DescriptorSetLayout layout)
    {
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        if (_vk.AllocateDescriptorSets(_device, &allocInfo, out DescriptorSet set) != Result.Success)
            throw new InvalidOperationException("Failed to allocate solver descriptor set");

        return set;
    }

    private unsafe void WriteSet(DescriptorSet set, Buffer[] buffers, DescriptorType[] types, int firstBinding)
    {
        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[buffers.Length];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[buffers.Length];

        for (int i = 0; i < buffers.Length; i++)
        {
            infos[i] = new DescriptorBufferInfo { Buffer = buffers[i], Offset = 0, Range = _allocator.RangeOf(buffers[i]) };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)(firstBinding + i),
                DescriptorCount = 1,
                DescriptorType = types[i],
                PBufferInfo = &infos[i]
            };
        }

        _vk.UpdateDescriptorSets(_device, (uint)buffers.Length, writes, 0, null);
    }

    /// <summary>
    /// Recycles everything <see cref="Build"/> created, for the next build to use again (see
    /// <see cref="GpuBufferAllocator.Recycle"/>); the layouts and pipelines stay.
    /// </summary>
    public unsafe void Release()
    {
        for (int i = 0; i < StateBindingCount; i++)
        {
            _allocator.Recycle(_stateBuffers[i], _stateMemory[i]);
            _stateBuffers[i] = default;
            _stateMemory[i] = default;
        }

        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);

        _descriptorPool = default;
        _stateSet = default;
        _softRenderSets = [];
        _paramsMapped = 0;
        _editsMapped = 0;
        _editCapacity = 0;
        NodeCount = 0;
        RigidCount = 0;
        SpringCount = 0;
    }

    public unsafe void Dispose()
    {
        Release();
        _pipelineLoader?.Dispose();

        if (StateLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, StateLayout, null);
        if (_softRenderLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, _softRenderLayout, null);
    }
}
