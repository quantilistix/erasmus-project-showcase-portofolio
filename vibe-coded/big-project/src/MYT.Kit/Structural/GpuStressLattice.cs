using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Vulkan;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Structural;


/// <summary>
/// The analysis of <see cref="StressLattice"/> on the GPU, for structures too large to solve on
/// the CPU as fast as they change: the same rigid-body-spring lattice, conjugate gradients and
/// multigrid preconditioner, on a dense box of cells around the structure. Mostly in float,
/// with double where float loses too much: the conjugate gradient's vectors and operator, and
/// the sums that build the coarser levels of the multigrid (see lattice_common.glsl).
/// <para>
/// It runs continuously alongside a game rather than solving on demand. Changes (cells,
/// moduli, loads) are recorded on the CPU; each <see cref="Update"/> uploads them, rebuilds
/// what they invalidate on the GPU (the multigrid hierarchy takes a handful of dispatches, so
/// an edit costs about as much as an iteration) and runs some iterations from the current
/// solution, then reads back the stresses and the residual of the previous submission. The
/// GPU never waits for the CPU, nor the CPU for the GPU: one submission is in flight at a time
/// and is polled for, and a converged lattice with nothing changed submits nothing.
/// </para>
/// <para>
/// Cells live at fixed nodes of a box with a margin, so the solution of the cells that stay
/// carries over when cells come and go; only a cell outside the box makes it grow, which
/// restarts the solution from rest. Every face-connected group of cells needs an anchored
/// cell, or one is anchored at its middle as <see cref="StressLattice"/> does; its loads
/// should then balance. Air nodes cost a thread each, so the box suits structures that fill
/// a good part of it, such as terrain. Not thread safe: use it from the thread that owns the queue.
/// </para>
/// </summary>
public sealed class GpuStressLattice : IDisposable
{
    private const int BlockFloats = 36;
    // A coarse node's diagonal block and its coupling blocks towards +x, +y and +z; those
    // towards −x, −y and −z are the transposes of its neighbours'.
    private const int BlocksPerNode = 4;
    private const uint WorkgroupSize = 64;
    private const uint ReductionSize = 256;
    private const int ResidualReplacementInterval = 20;
    private const int StateBytes = 32;
    // Levels with at most this many nodes go through the rest of the cycle in one workgroup.
    private const int SmallLevel = 512;
    // Gauss-Seidel sweeps before and after each coarse correction, on the large levels and on
    // the small ones. A second sweep on the large levels about halves the iterations an edit
    // takes; more on the small ones, run by one workgroup, cost more than they save.
    private const int LargeLevelSweeps = 2;
    private const int SmallLevelSweeps = 1;
    private const uint MaxStamps = 64;

    // Room left around the cells when the box is made, more above, where structures grow.
    private static readonly ivec3 MarginBelow = new(2, 2, 1);
    private static readonly ivec3 MarginAbove = new(2, 2, 8);

    private enum Binding
    {
        State,
        Cells,
        Solution,
        Direction,
        Product,
        Loads,
        Right,
        Correction,
        Residual,
        Inverses,
        Blocks,
        Positions,
        Partials,
        CellLoads,
        Stresses,
        CoarseInverses,
        Bonds,
        Levels,
        CgResidual,
        CellNodes,
        Count
    }

    // Matches the push constant block of lattice_common.glsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct Pass
    {
        public ivec4 Size;
        public ivec4 CoarseSize;
        public uvec4 Offsets;
        public vec4 Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuCellLoad
    {
        public uvec4 Node;
        public vec4 Force;
        public vec4 Moment;
    }

    // Matches LevelEntry in lattice_common.glsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuLevel
    {
        public ivec4 Size;
        public uvec4 Offsets;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuState
    {
        public float Rz;
        public float Pq;
        public float Alpha;
        public float Beta;
        public float Rr;
        public float Bb;
        public uint Iterations;
        public uint Padding;
    }

    /// <summary>One level of the hierarchy: its box, and where its nodes start in the per-level arrays.</summary>
    private readonly record struct Level(ivec3 Size, int Count, uint NodeOffset, uint BlockOffset);

    private static int Count(ivec3 size)
    {
        return size.x * size.y * size.z;
    }

    private static uint Groups(int threads, uint size)
    {
        return (uint)((threads + size - 1) / size);
    }

    // Instances retain their own simulation buffers and descriptors, while all
    // bodies in a world can share the same immutable compiled kernels.
    private sealed class Programs : IDisposable
    {
        public readonly DescriptorSetLayout Layout;
        public readonly ComputePipelineLoader Loader;
        public readonly Dictionary<string, ComputePipeline> Kernels = [];
        public int References = 1;
        private readonly Vk _vk;
        private readonly Device _device;
        public unsafe Programs(Vk vk, Device device, string directory)
        {
            _vk = vk; _device = device;
            Loader = new(vk, device);
            DescriptorSetLayoutBinding[] bindings = new DescriptorSetLayoutBinding[(int)Binding.Count];
            for (uint i = 0; i < bindings.Length; i++) bindings[i] = new(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            fixed (DescriptorSetLayoutBinding* pointer = bindings)
            {
                DescriptorSetLayoutCreateInfo create = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)bindings.Length, PBindings = pointer };
                if (vk.CreateDescriptorSetLayout(device, &create, null, out Layout) != Result.Success)
                    throw new InvalidOperationException("Failed to create the lattice descriptor set layout");
            }
            try
            {
                foreach (string name in new[] { "loads", "bonds", "setup_fine", "positions", "galerkin", "apply", "smooth", "residual", "restrict", "prolong", "coarse_cycle", "dot", "scalars", "update", "stress" })
                    Kernels[name] = Loader.Load(Path.Combine(directory, "lattice_" + name + ".comp"), [Layout], (uint)Unsafe.SizeOf<Pass>());
            }
            catch { Dispose(); throw; }
        }
        public unsafe void Dispose()
        {
            Loader.Dispose();
            _vk.DestroyDescriptorSetLayout(_device, Layout, null);
        }
    }

    /// <param name="shaderDirectory">The directory with the lattice_*.comp shaders.</param>
    /// <param name="cellSize">Edge length of a cell in m.</param>
    /// <param name="sharePipelinesWith">Optional live lattice on the same device/owning thread whose immutable kernels are retained.</param>
    public unsafe GpuStressLattice(Vk vk, Device device, PhysicalDevice physicalDevice, CommandPool commandPool, Queue queue, string shaderDirectory, double cellSize = 1.0, GpuStressLattice? sharePipelinesWith = null)
    {
        if (!(cellSize > 0.0))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "The cell size must be positive");

        _vk = vk;
        _device = device;
        _commandPool = commandPool;
        _queue = queue;
        _allocator = new GpuBufferAllocator(vk, device, physicalDevice, commandPool, queue);
        CellSize = cellSize;

        if (sharePipelinesWith != null)
        {
            if (sharePipelinesWith._disposed || sharePipelinesWith._device.Handle != device.Handle)
                throw new ArgumentException("Shared stress kernels must belong to the same live device", nameof(sharePipelinesWith));
            _programs = sharePipelinesWith._programs;
            _programs.References++;
        }
        else _programs = new(vk, device, shaderDirectory);
        _setLayout = _programs.Layout;
        _loadsPass = _programs.Kernels["loads"];
        _bonds = _programs.Kernels["bonds"];
        _setupFine = _programs.Kernels["setup_fine"];
        _positions = _programs.Kernels["positions"];
        _galerkin = _programs.Kernels["galerkin"];
        _apply = _programs.Kernels["apply"];
        _smooth = _programs.Kernels["smooth"];
        _residual = _programs.Kernels["residual"];
        _restrict = _programs.Kernels["restrict"];
        _prolong = _programs.Kernels["prolong"];
        _coarseCycle = _programs.Kernels["coarse_cycle"];
        _dot = _programs.Kernels["dot"];
        _scalars = _programs.Kernels["scalars"];
        _update = _programs.Kernels["update"];
        _stress = _programs.Kernels["stress"];

        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (_vk.AllocateCommandBuffers(_device, &allocInfo, out _commandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to allocate the lattice command buffer");

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        if (_vk.CreateFence(_device, &fenceInfo, null, out _fence) != Result.Success)
            throw new InvalidOperationException("Failed to create the lattice fence");

        QueryPoolCreateInfo queryInfo = new()
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = MaxStamps
        };
        if (_vk.CreateQueryPool(_device, &queryInfo, null, out _stampPool) != Result.Success)
            throw new InvalidOperationException("Failed to create the lattice timestamp pool");

        _vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
        _timestampPeriod = properties.Limits.TimestampPeriod;
    }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _commandPool;
    private readonly Queue _queue;
    private readonly GpuBufferAllocator _allocator;
    private readonly DescriptorSetLayout _setLayout;
    private readonly Programs _programs;
    private bool _disposed;
    private readonly ComputePipeline _loadsPass;
    private readonly ComputePipeline _bonds;
    private readonly ComputePipeline _setupFine;
    private readonly ComputePipeline _positions;
    private readonly ComputePipeline _galerkin;
    private readonly ComputePipeline _apply;
    private readonly ComputePipeline _smooth;
    private readonly ComputePipeline _residual;
    private readonly ComputePipeline _restrict;
    private readonly ComputePipeline _prolong;
    private readonly ComputePipeline _coarseCycle;
    private readonly ComputePipeline _dot;
    private readonly ComputePipeline _scalars;
    private readonly ComputePipeline _update;
    private readonly ComputePipeline _stress;
    private readonly CommandBuffer _commandBuffer;
    private readonly Fence _fence;
    private readonly QueryPool _stampPool;
    private readonly float _timestampPeriod;
    private readonly List<string> _stampLabels = [];

    private readonly Buffer[] _buffers = new Buffer[(int)Binding.Count];
    private readonly DeviceMemory[] _memory = new DeviceMemory[(int)Binding.Count];
    private DescriptorPool _descriptorPool;
    private DescriptorSet _set;
    private Buffer _staging;
    private DeviceMemory _stagingMemory;
    private nint _stagingMapped;
    private ulong _stagingSize;
    // Two readback buffers, a submission's results in one while the next fills the other, so
    // its stresses are converted only when someone copies them.
    private readonly Buffer[] _readback = new Buffer[2];
    private readonly DeviceMemory[] _readbackMemory = new DeviceMemory[2];
    private readonly nint[] _readbackMapped = new nint[2];
    private int _submissions;
    private int _resultReadback;
    private int _loadCapacity;

    // The box: its lowest cell, and every level from the cells themselves to a single node.
    private ivec3 _origin;
    private Level[] _levels = [];

    // The cells as given, the node of each, the cell at each node (−1 for air), and what the
    // GPU should know of each node: Young's modulus, shear modulus, mass, 0 air / 1 cell / 2 anchored.
    private ivec3[] _cells = [];
    private int _cellCount;
    private readonly List<int> _componentNodes = [];
    private int[] _cellNodes = [];
    private int[] _nodeCells = [];
    private vec4[] _nodeData = [];
    // Which nodes a search over the box has reached, kept for the next.
    private bool[] _seen = [];
    private readonly HashSet<int> _changedNodes = [];
    private bool _uploadAllNodes;
    private bool _allocateBuffers;
    private bool _resetBuffers;

    private dvec3 _gravity;
    private (ivec3 Cell, dvec3 Force, dvec3 Moment)[] _loads = [];

    private int _uploadedLoads;
    private bool _loadsChanged;
    private bool _operatorChanged;
    private bool _restart;
    private int _iterationsSinceRestart;
    // The last iteration stopped once it had updated the solution: its next direction, a
    // preconditioner cycle, waits for the next submission, which a restart makes needless.
    private bool _directionPending;

    // Every change bumps the generation; a submission remembers the one it was recorded at.
    private long _generation;
    private long _topologyGeneration;
    private bool _inFlight;
    private long _submittedGeneration;
    private long _submittedTopology;
    private long _resultGeneration = -1;
    private long _resultTopology = -1;

    public double CellSize { get; }
    public int CellCount => _cellCount;
    /// <summary>Disable bulk stress readback when subsequent GPU passes consume it directly. Set before the first update.</summary>
    public bool ReadBackStresses { get; set; } = true;
    /// <summary>Device-local (tension, compression, shear, energy). Consume after Update(submit: false) completes; retain the owner until the consumer finishes.</summary>
    public (Buffer Buffer, ulong Bytes) StressStorage => (_buffers[(int)Binding.Stresses], (ulong)_cellCount * 16);
    public ReadOnlySpan<ivec3> Cells => _cells.AsSpan(0, _cellCount);

    /// <summary>Relative residual below which the solution counts as converged and the lattice stops iterating.</summary>
    public double Tolerance { get; set; } = 1e-4;

    /// <summary>Iterations each <see cref="Update"/> submits while the solution has not converged.</summary>
    public int IterationsPerUpdate { get; set; } = 8;

    /// <summary>GPU time of the latest results' submission, ms.</summary>
    public double GpuMilliseconds { get; private set; }

    /// <summary>When set, submissions time their phases into <see cref="LastProfile"/>.</summary>
    public bool Profiling { get; set; }

    /// <summary>GPU time of each phase of the latest profiled submission, ms.</summary>
    public List<(string Phase, double Milliseconds)> LastProfile { get; } = [];

    /// <summary>The relative residual of the latest results, ‖b − K·x‖ / ‖b‖.</summary>
    public double RelativeResidual { get; private set; } = double.PositiveInfinity;

    /// <summary>Iterations behind the latest results since the last change.</summary>
    public int Iterations { get; private set; }

    /// <summary>Whether the latest results are for the current cells (not necessarily the current loads and moduli).</summary>
    public bool HasResults => _resultTopology == _topologyGeneration && _resultTopology >= 0;

    /// <summary>Whether the latest results are for everything as it is now, converged.</summary>
    public bool Converged => _resultGeneration == _generation && RelativeResidual <= Tolerance;

    public int IndexOf(ivec3 cell)
    {
        int node = NodeOf(cell);
        return node >= 0 ? _nodeCells[node] : -1;
    }

    /// <summary>
    /// Replaces the structure. Cells that keep their place in the box keep their solution;
    /// the loads given before stay, on the cells that are still there.
    /// </summary>
    /// <param name="masses">Per cell, kg, loaded by the gravity of <see cref="SetLoads"/>.</param>
    public void SetCells(ReadOnlySpan<ivec3> cells, ReadOnlySpan<CellMaterial> materials, ReadOnlySpan<double> masses, ReadOnlySpan<bool> anchored)
    {
        if (materials.Length != cells.Length || masses.Length != cells.Length || anchored.Length != cells.Length)
            throw new ArgumentException("One material, mass and anchor flag per cell are required");
        if (cells.Length == 0)
            throw new ArgumentException("A lattice needs at least one cell", nameof(cells));

        ivec3 low = new(int.MaxValue);
        ivec3 high = new(int.MinValue);
        foreach (ivec3 cell in cells)
        {
            low = ivec3.Min(low, cell);
            high = ivec3.Max(high, cell);
        }

        if (NodeOf(low) < 0 || NodeOf(high) < 0)
            PrepareBox(low - MarginBelow, high - low + 1 + MarginBelow + MarginAbove);

        if (_cells.Length < cells.Length)
        {
            int capacity = Math.Max(cells.Length, _cells.Length + _cells.Length / 2);
            Array.Resize(ref _cells, capacity);
            Array.Resize(ref _cellNodes, capacity);
        }
        cells.CopyTo(_cells);
        _cellCount = cells.Length;
        Array.Fill(_nodeCells, -1);
        Array.Clear(_nodeData);
        for (int i = 0; i < cells.Length; i++)
        {
            int node = NodeOf(cells[i]);
            if (node < 0 || _nodeCells[node] >= 0)
                throw new ArgumentException($"Cell {cells[i]} is listed twice", nameof(cells));

            _cellNodes[i] = node;
            _nodeCells[node] = i;
            _nodeData[node] = new vec4((float)materials[i].YoungsModulus, (float)materials[i].ShearModulus, (float)masses[i], anchored[i] ? 2f : 1f);
        }

        AnchorFreeGroups();
        _uploadAllNodes = true;
        _changedNodes.Clear();
        _topologyGeneration = ++_generation;
        _loadsChanged = true;
        _operatorChanged = true;
        _restart = true;
    }

    /// <summary>Changes the moduli of a cell, for instance as damage softens it.</summary>
    public void SetMaterial(int cell, CellMaterial material)
    {
        if ((uint)cell >= (uint)_cellCount)
            throw new ArgumentOutOfRangeException(nameof(cell));
        int node = _cellNodes[cell];
        vec4 data = _nodeData[node];
        _nodeData[node] = new vec4((float)material.YoungsModulus, (float)material.ShearModulus, data.z, data.w);
        _changedNodes.Add(node);
        _generation++;
        _operatorChanged = true;
        _restart = true;
    }

    /// <summary>
    /// Sets the loads: gravity on every cell's mass, plus a force (N) and a moment about the
    /// center (N·m) on some cells; each cell at most once, and cells not in the lattice ignored.
    /// </summary>
    public void SetLoads(dvec3 gravity, ReadOnlySpan<(ivec3 Cell, dvec3 Force, dvec3 Moment)> loads)
    {
        _gravity = gravity;
        _loads = loads.ToArray();
        _generation++;
        _loadsChanged = true;
        _restart = true;
    }

    /// <summary>
    /// Reads back what the GPU finished since the last call, then, unless the solution has
    /// converged with nothing changed, submits the next round of work. Never waits. Returns
    /// whether new results arrived.
    /// </summary>
    public unsafe bool Update(bool submit = true)
    {
        bool arrived = false;
        if (_inFlight)
        {
            if (_vk.GetFenceStatus(_device, _fence) != Result.Success)
                return false;

            Fence fence = _fence;
            _vk.ResetFences(_device, 1, &fence);
            _inFlight = false;
            ReadResults();
            arrived = true;
        }

        if (!submit || _cellCount == 0)
            return arrived;

        bool changed = _resultGeneration != _generation || _restart || _loadsChanged || _operatorChanged;
        if (!changed && RelativeResidual <= Tolerance)
            return arrived;

        Submit(IterationsPerUpdate);
        return arrived;
    }

    /// <summary>Blocks until the GPU is done with the work submitted, and reads its results.</summary>
    public unsafe void Wait()
    {
        if (!_inFlight)
            return;

        Fence fence = _fence;
        _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
        Update();
    }

    /// <summary>Iterates until converged or <paramref name="maxIterations"/> are spent, blocking; for tests and tools.</summary>
    public LatticeSolveResult Solve(int maxIterations)
    {
        int spent = 0;
        while (true)
        {
            Wait();
            if (Converged || spent >= maxIterations)
                return new LatticeSolveResult(Iterations, RelativeResidual, Converged);

            Update();
            spent += IterationsPerUpdate;
        }
    }

    /// <summary>The stresses of the latest results, per cell in the order of <see cref="SetCells"/>.</summary>
    public unsafe void CopyStresses(Span<CellStress> stresses)
    {
        if (!ReadBackStresses) throw new InvalidOperationException("Stress readback is disabled for this lattice");
        if (!HasResults)
            throw new InvalidOperationException("There are no results for the current cells yet");
        if (stresses.Length < _cellCount)
            throw new ArgumentException("One entry per cell is required", nameof(stresses));

        ReadOnlySpan<vec4> results = new((void*)(_readbackMapped[_resultReadback] + StateBytes), _cellCount);
        for (int i = 0; i < results.Length; i++)
            stresses[i] = new CellStress(results[i].x, results[i].y, results[i].z);
    }

    private int NodeOf(ivec3 cell)
    {
        if (_levels.Length == 0)
            return -1;

        ivec3 local = cell - _origin;
        ivec3 size = _levels[0].Size;
        if (local.x < 0 || local.y < 0 || local.z < 0 || local.x >= size.x || local.y >= size.y || local.z >= size.z)
            return -1;

        return local.x + size.x * (local.y + size.y * local.z);
    }

    /// <summary>
    /// Anchors the node nearest the middle of every face-connected group of cells without an
    /// anchor, which would otherwise be free to move as a rigid body and leave the system singular.
    /// </summary>
    private void AnchorFreeGroups()
    {
        ivec3 size = _levels[0].Size;
        bool[] seen = _seen;
        Array.Clear(seen);
        List<int> members = _componentNodes;
        members.EnsureCapacity(_cellCount);

        foreach (int seed in _cellNodes.AsSpan(0, _cellCount))
        {
            if (seen[seed])
                continue;

            members.Clear();
            members.Add(seed);
            seen[seed] = true;
            bool anchored = false;
            for (int k = 0; k < members.Count; k++)
            {
                int node = members[k];
                anchored |= _nodeData[node].w >= 2f;
                int row = node / size.x;
                int x = node - row * size.x;
                int z = row / size.y;
                int y = row - z * size.y;
                // Same +x,-x,+y,-y,+z,-z visitation and tie order as the generic walk.
                if (x + 1 < size.x) VisitComponentNode(node + 1);
                if (x > 0) VisitComponentNode(node - 1);
                if (y + 1 < size.y) VisitComponentNode(node + size.x);
                if (y > 0) VisitComponentNode(node - size.x);
                if (z + 1 < size.z) VisitComponentNode(node + size.x * size.y);
                if (z > 0) VisitComponentNode(node - size.x * size.y);
            }

            if (anchored)
                continue;

            dvec3 middle = dvec3.Zero;
            foreach (int node in members)
                middle += new dvec3(node % size.x, node / size.x % size.y, node / (size.x * size.y));
            middle /= members.Count;

            // Keep the first minimum, as MinBy did. A lambda capturing this loop's
            // middle allocated a closure for every seed, even already-visited nodes.
            int nearest = members[0];
            double best = double.PositiveInfinity;
            foreach (int node in members)
            {
                double distance = (new dvec3(node % size.x, node / size.x % size.y, node / (size.x * size.y)) - middle).LengthSqr;
                if (distance < best)
                {
                    best = distance;
                    nearest = node;
                }
            }
            _nodeData[nearest].w = 2f;
        }
    }

    private void VisitComponentNode(int node)
    {
        if (!_seen[node] && _nodeData[node].w > 0f)
        {
            _seen[node] = true;
            _componentNodes.Add(node);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Buffers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Prepares a new box whose GPU solution will start from rest. A box
    /// of the same size as the last, as one that moves with a player is, takes over its
    /// arrays. GPU buffers are recycled on the next submission, after the old solve finishes.
    /// </summary>
    private void PrepareBox(ivec3 origin, ivec3 size)
    {
        _origin = origin;
        List<Level> levels = [];
        uint nodeOffset = 0;
        uint blockOffset = 0;
        ivec3 levelSize = size;
        while (true)
        {
            int count = Count(levelSize);
            levels.Add(new Level(levelSize, count, nodeOffset, levels.Count == 0 ? 0 : blockOffset));
            nodeOffset += (uint)count;
            if (levels.Count > 1)
                blockOffset += (uint)count;
            if (levelSize == new ivec3(1))
                break;

            levelSize = (levelSize + 1) / 2;
        }

        _levels = [.. levels];
        int nodes = _levels[0].Count;
        if (_nodeCells.Length != nodes)
        {
            _nodeCells = new int[nodes];
            _nodeData = new vec4[nodes];
            _seen = new bool[nodes];
        }

        // CPU topology can change while the previous GPU solve finishes. Keep its
        // resources alive; Update polls its fence before allocating for the new box.
        _allocateBuffers = true;
    }

    private unsafe void AllocateBuffers()
    {
        ReleaseBuffers(recycle: true);
        int nodes = _levels[0].Count;
        ulong allNodes = (ulong)_levels.Sum(level => (long)level.Count);
        ulong coarseNodes = allNodes - (ulong)nodes;
        _loadCapacity = 0;

        BufferUsageFlags storage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;
        ulong six = 32;
        ulong[] sizes = new ulong[(int)Binding.Count];
        sizes[(int)Binding.State] = StateBytes;
        sizes[(int)Binding.Cells] = (ulong)nodes * 16;
        sizes[(int)Binding.Solution] = (ulong)nodes * 2 * six;
        sizes[(int)Binding.Direction] = (ulong)nodes * 2 * six;
        sizes[(int)Binding.Product] = (ulong)nodes * 2 * six;
        sizes[(int)Binding.CgResidual] = (ulong)nodes * 2 * six;
        sizes[(int)Binding.Loads] = (ulong)nodes * six;
        sizes[(int)Binding.Right] = allNodes * six;
        sizes[(int)Binding.Correction] = allNodes * six;
        sizes[(int)Binding.Residual] = allNodes * six;
        sizes[(int)Binding.Inverses] = (ulong)nodes * BlockFloats * 4;
        sizes[(int)Binding.Blocks] = Math.Max(coarseNodes, 1) * BlocksPerNode * BlockFloats * 4;
        sizes[(int)Binding.CoarseInverses] = Math.Max(coarseNodes, 1) * BlockFloats * 4;
        sizes[(int)Binding.Bonds] = (ulong)nodes * 4 * 16;
        sizes[(int)Binding.Levels] = (ulong)(_levels.Length * Unsafe.SizeOf<GpuLevel>());
        sizes[(int)Binding.Positions] = Math.Max(coarseNodes, 1) * 16;
        sizes[(int)Binding.Partials] = Groups(nodes, ReductionSize) * 16UL;
        sizes[(int)Binding.CellLoads] = 48;
        sizes[(int)Binding.Stresses] = (ulong)nodes * 16;
        sizes[(int)Binding.CellNodes] = (ulong)nodes * 4;

        for (int i = 0; i < (int)Binding.Count; i++)
        {
            _allocator.Create(sizes[i], storage, MemoryPropertyFlags.DeviceLocalBit, out _buffers[i], out _memory[i]);
        }

        // Initialize in the solver command buffer, without a separate blocking submit.
        _resetBuffers = true;

        for (int i = 0; i < _readback.Length; i++)
            _readbackMapped[i] = _allocator.CreateReadback(StateBytes + sizes[(int)Binding.Stresses], BufferUsageFlags.TransferDstBit, out _readback[i], out _readbackMemory[i]);
        _allocator.FreeRecycled();
        EnsureStaging((ulong)nodes * 20 + 48);
        CreateDescriptorSet();
    }

    /// <summary>Makes the sparse load buffer hold at least <paramref name="count"/> loads.</summary>
    private void EnsureLoadCapacity(int count)
    {
        if (count <= _loadCapacity)
            return;

        _loadCapacity = Math.Max(count, Math.Max(64, _loadCapacity * 2));
        int binding = (int)Binding.CellLoads;
        _allocator.Destroy(_buffers[binding], _memory[binding]);
        BufferUsageFlags storage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit;
        _allocator.Create((ulong)(_loadCapacity * Unsafe.SizeOf<GpuCellLoad>()), storage, MemoryPropertyFlags.DeviceLocalBit, out _buffers[binding], out _memory[binding]);
        CreateDescriptorSet();
    }

    private void EnsureStaging(ulong size)
    {
        if (size <= _stagingSize)
            return;

        _allocator.Destroy(_staging, _stagingMemory);
        _stagingSize = size + size / 4;
        _stagingMapped = _allocator.CreateMapped(_stagingSize, BufferUsageFlags.TransferSrcBit, out _staging, out _stagingMemory);
    }

    private unsafe void CreateDescriptorSet()
    {
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);

        DescriptorPoolSize poolSize = new() { Type = DescriptorType.StorageBuffer, DescriptorCount = (uint)Binding.Count };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };

        if (_vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool) != Result.Success)
            throw new InvalidOperationException("Failed to create the lattice descriptor pool");

        DescriptorSetLayout layout = _setLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };

        if (_vk.AllocateDescriptorSets(_device, &allocInfo, out _set) != Result.Success)
            throw new InvalidOperationException("Failed to allocate the lattice descriptor set");

        DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[(int)Binding.Count];
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[(int)Binding.Count];
        for (int i = 0; i < (int)Binding.Count; i++)
        {
            infos[i] = new DescriptorBufferInfo { Buffer = _buffers[i], Offset = 0, Range = Vk.WholeSize };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &infos[i]
            };
        }

        _vk.UpdateDescriptorSets(_device, (uint)Binding.Count, writes, 0, null);
    }

    /// <param name="recycle">Keep them for the next <see cref="AllocateBuffers"/> to take again (<see cref="GpuBufferAllocator.Recycle"/>).</param>
    private unsafe void ReleaseBuffers(bool recycle)
    {
        for (int i = 0; i < (int)Binding.Count; i++)
        {
            if (recycle)
                _allocator.Recycle(_buffers[i], _memory[i]);
            else
                _allocator.Destroy(_buffers[i], _memory[i]);
            _buffers[i] = default;
            _memory[i] = default;
        }

        for (int i = 0; i < _readback.Length; i++)
        {
            if (recycle)
                _allocator.Recycle(_readback[i], _readbackMemory[i]);
            else
                _allocator.Destroy(_readback[i], _readbackMemory[i]);
            _readback[i] = default;
            _readbackMemory[i] = default;
        }
        if (_descriptorPool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _descriptorPool = default;
    }

    // ---------------------------------------------------------------------------------------
    // Recording
    // ---------------------------------------------------------------------------------------

    private unsafe void Submit(int iterations)
    {
        if (_allocateBuffers)
        {
            AllocateBuffers();
            _allocateBuffers = false;
        }
        _vk.ResetCommandBuffer(_commandBuffer, 0);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo);
        _vk.CmdResetQueryPool(_commandBuffer, _stampPool, 0, MaxStamps);
        _stampLabels.Clear();
        Stamp("start", always: true);

        RecordUploads();
        if (_loadsChanged)
            RecordLoads();
        Stamp("uploads and loads");
        if (_operatorChanged)
            RecordSetup();
        Stamp("hierarchy");
        if (_restart)
            RecordRestart();
        else if (_directionPending)
            RecordDirection(counted: false);
        Stamp("restart");
        for (int k = 0; k < iterations; k++)
        {
            if (k > 0)
                RecordDirection(counted: true);
            RecordStep();
        }
        _directionPending = iterations > 0;
        if (_directionPending)
            RecordResidualNorm();
        Stamp("iterations");
        RecordReadback();
        Stamp("stresses and readback", always: true);

        _vk.EndCommandBuffer(_commandBuffer);

        CommandBuffer cmd = _commandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        if (_vk.QueueSubmit(_queue, 1, &submitInfo, _fence) != Result.Success)
            throw new InvalidOperationException("Failed to submit the lattice's work");

        _inFlight = true;
        _submissions++;
        _submittedGeneration = _generation;
        _submittedTopology = _topologyGeneration;
        _loadsChanged = false;
        _operatorChanged = false;
        _restart = false;
    }

    /// <summary>Copies changed cells and, when the loads changed, the sparse loads, through the staging buffer.</summary>
    private unsafe void RecordUploads()
    {
        int nodes = _levels[0].Count;
        int loadCount = _loadsChanged ? _loads.Length : 0;
        if (loadCount > 0)
            EnsureLoadCapacity(loadCount);
        ulong levelStart = (ulong)nodes * 16 + (ulong)Math.Max(loadCount, 1) * 48;
        ulong cellNodesStart = levelStart + (ulong)(_levels.Length * Unsafe.SizeOf<GpuLevel>());
        EnsureStaging(cellNodesStart + (ulong)_cellCount * 4);
        if (_resetBuffers)
        {
            for (int i = 0; i < (int)Binding.Count; i++)
                if (i != (int)Binding.Levels)
                    _vk.CmdFillBuffer(_commandBuffer, _buffers[i], 0, Vk.WholeSize, 0);

            // Cell/load copies below overwrite parts of these cleared buffers.
            MemoryBarrier cleared = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.TransferWriteBit
            };
            _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.TransferBit, 0, 1, &cleared, 0, null, 0, null);
            GpuLevel* table = (GpuLevel*)(_stagingMapped + (nint)levelStart);
            for (int i = 0; i < _levels.Length; i++)
            {
                Level level = _levels[i];
                table[i] = new GpuLevel { Size = new ivec4(level.Size, i), Offsets = new uvec4(level.NodeOffset, level.BlockOffset, 0, 0) };
            }
            BufferCopy levels = new() { SrcOffset = levelStart, Size = (ulong)(_levels.Length * Unsafe.SizeOf<GpuLevel>()) };
            _vk.CmdCopyBuffer(_commandBuffer, _staging, _buffers[(int)Binding.Levels], 1, &levels);
            _resetBuffers = false;
        }

        List<BufferCopy> cellCopies = [];
        vec4* data = (vec4*)_stagingMapped;
        if (_uploadAllNodes)
        {
            _nodeData.AsSpan().CopyTo(new Span<vec4>(data, nodes));
            cellCopies.Add(new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = (ulong)nodes * 16 });
            // The stress pass runs over the cells, writing each at its index in SetCells' order.
            _cellNodes.AsSpan(0, _cellCount).CopyTo(new Span<int>((void*)(_stagingMapped + (nint)cellNodesStart), _cellCount));
            BufferCopy order = new() { SrcOffset = cellNodesStart, Size = (ulong)_cellCount * 4 };
            _vk.CmdCopyBuffer(_commandBuffer, _staging, _buffers[(int)Binding.CellNodes], 1, &order);
        }
        else
        {
            int slot = 0;
            foreach (int node in _changedNodes)
            {
                data[slot] = _nodeData[node];
                cellCopies.Add(new BufferCopy { SrcOffset = (ulong)slot * 16, DstOffset = (ulong)node * 16, Size = 16 });
                slot++;
            }
        }

        _uploadAllNodes = false;
        _changedNodes.Clear();
        if (cellCopies.Count > 0)
        {
            BufferCopy[] regions = [.. cellCopies];
            fixed (BufferCopy* regionsPtr = regions)
            {
                _vk.CmdCopyBuffer(_commandBuffer, _staging, _buffers[(int)Binding.Cells], (uint)regions.Length, regionsPtr);
            }
        }

        _uploadedLoads = 0;
        if (_loadsChanged)
        {
            ulong start = (ulong)nodes * 16;
            GpuCellLoad* loads = (GpuCellLoad*)(_stagingMapped + (nint)start);
            foreach ((ivec3 cell, dvec3 force, dvec3 moment) in _loads)
            {
                int node = NodeOf(cell);
                if (node < 0 || _nodeCells[node] < 0)
                    continue;

                loads[_uploadedLoads++] = new GpuCellLoad { Node = new uvec4((uint)node, 0, 0, 0), Force = new vec4((vec3)force, 0), Moment = new vec4((vec3)moment, 0) };
            }

            if (_uploadedLoads > 0)
            {
                BufferCopy region = new() { SrcOffset = start, DstOffset = 0, Size = (ulong)(_uploadedLoads * Unsafe.SizeOf<GpuCellLoad>()) };
                _vk.CmdCopyBuffer(_commandBuffer, _staging, _buffers[(int)Binding.CellLoads], 1, &region);
            }
        }

        ComputeCommands.TransferToCompute(_vk, _commandBuffer);
    }

    private void RecordLoads()
    {
        Dispatch(_loadsPass, LevelPass(0, 0), _levels[0].Count);
        if (_uploadedLoads == 0)
            return;

        Pass sparse = LevelPass(0, 1);
        sparse.Offsets.y = (uint)_uploadedLoads;
        Dispatch(_loadsPass, sparse, _uploadedLoads);
    }

    /// <summary>
    /// Level 0's interfaces and diagonal inverses, then each coarser level's reference points,
    /// Galerkin blocks and inverses.
    /// </summary>
    private void RecordSetup()
    {
        Dispatch(_bonds, LevelPass(0, 0), _levels[0].Count);
        Dispatch(_setupFine, LevelPass(0, 0), _levels[0].Count);
        for (int l = 0; l + 1 < _levels.Length; l++)
        {
            Dispatch(_positions, LevelPass(l, 0), _levels[l + 1].Count);
            Dispatch(_galerkin, LevelPass(l, 0), _levels[l + 1].Count);
        }
    }

    /// <summary>Restarts the conjugate gradient from the current solution: r = b − K·x, z = M⁻¹·r, p = z.</summary>
    private void RecordRestart()
    {
        int nodes = _levels[0].Count;
        Dispatch(_apply, LevelPass(0, 1), nodes);
        RecordCycle();
        DispatchGroups(_dot, LevelPass(0, 2), Groups(nodes, ReductionSize));
        DispatchGroups(_scalars, ScalarPass(2), 1);
        Dispatch(_update, LevelPass(0, 2), nodes);
        _iterationsSinceRestart = 0;
        _directionPending = false;
    }

    /// <summary>
    /// The first half of a preconditioned conjugate gradient iteration: x += α·p, r −= α·q, with
    /// the true residual now and then. <see cref="RecordDirection"/> is its second half.
    /// </summary>
    private void RecordStep()
    {
        int nodes = _levels[0].Count;
        Dispatch(_apply, LevelPass(0, 0), nodes);
        DispatchGroups(_dot, LevelPass(0, 0), Groups(nodes, ReductionSize));
        DispatchGroups(_scalars, ScalarPass(0), 1);
        Dispatch(_update, LevelPass(0, 0), nodes);

        // The updated residual drifts from b − K·x, all the more in float, so it is replaced
        // by the true one now and then (as StressLattice does).
        if (++_iterationsSinceRestart % ResidualReplacementInterval == 0)
            Dispatch(_apply, LevelPass(0, 1), nodes);
    }

    /// <summary>
    /// The second half of an iteration: z = M⁻¹·r, β, p = z + β·p. Counted, it ends the
    /// iteration; deferred to the next submission, <see cref="RecordResidualNorm"/> counted it.
    /// </summary>
    private void RecordDirection(bool counted)
    {
        int nodes = _levels[0].Count;
        RecordCycle();
        DispatchGroups(_dot, LevelPass(0, 1), Groups(nodes, ReductionSize));
        DispatchGroups(_scalars, ScalarPass(counted ? 1 : 4), 1);
        Dispatch(_update, LevelPass(0, 1), nodes);
    }

    /// <summary>r·r for the results of a submission that ends after a step, and the iteration counted.</summary>
    private void RecordResidualNorm()
    {
        int nodes = _levels[0].Count;
        DispatchGroups(_dot, LevelPass(0, 3), Groups(nodes, ReductionSize));
        DispatchGroups(_scalars, ScalarPass(3), 1);
    }

    /// <summary>
    /// z = M⁻¹·r: a symmetric V-cycle from a zero guess, red-black block Gauss-Seidel sweeps
    /// before (red, black) and after (black, red) each coarse correction, and the single node at
    /// the top solved exactly by its own block. From the first small level up, the cycle runs in
    /// one workgroup (lattice_coarse_cycle.comp).
    /// </summary>
    private void RecordCycle()
    {
        int small = Array.FindIndex(_levels, level => level.Count <= SmallLevel);
        for (int l = 0; l < small; l++)
        {
            for (int k = 0; k < LargeLevelSweeps; k++)
            {
                Dispatch(_smooth, LevelPass(l, 0), _levels[l].Count);
                Dispatch(_smooth, LevelPass(l, 1), _levels[l].Count);
            }

            Dispatch(_residual, LevelPass(l, 0), _levels[l].Count);
            Dispatch(_restrict, LevelPass(l, 0), _levels[l + 1].Count);
        }

        Pass rest = LevelPass(small, 0);
        rest.CoarseSize.x = _levels.Length;
        rest.CoarseSize.y = SmallLevelSweeps;
        DispatchGroups(_coarseCycle, rest, 1);

        for (int l = small - 1; l >= 0; l--)
        {
            Dispatch(_prolong, LevelPass(l, 0), _levels[l].Count);
            for (int k = 0; k < LargeLevelSweeps; k++)
            {
                Dispatch(_smooth, LevelPass(l, 1), _levels[l].Count);
                Dispatch(_smooth, LevelPass(l, 0), _levels[l].Count);
            }
        }
    }

    /// <summary>The stresses of the solution's cells, copied with the scalars to this submission's readback buffer.</summary>
    private unsafe void RecordReadback()
    {
        Pass cells = LevelPass(0, 0);
        cells.Offsets.y = (uint)_cellCount;
        Dispatch(_stress, cells, _cellCount);
        ComputeCommands.ComputeToTransfer(_vk, _commandBuffer);

        Buffer readback = _readback[_submissions & 1];
        BufferCopy state = new() { SrcOffset = 0, DstOffset = 0, Size = StateBytes };
        _vk.CmdCopyBuffer(_commandBuffer, _buffers[(int)Binding.State], readback, 1, &state);
        if (ReadBackStresses)
        {
            BufferCopy stresses = new() { SrcOffset = 0, DstOffset = StateBytes, Size = (ulong)_cellCount * 16 };
            _vk.CmdCopyBuffer(_commandBuffer, _buffers[(int)Binding.Stresses], readback, 1, &stresses);
        }

        ComputeCommands.Barrier
        (
            _vk, _commandBuffer,
            PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit,
            PipelineStageFlags.HostBit, AccessFlags.HostReadBit
        );
    }

    /// <summary>Writes a timestamp after everything recorded so far, when profiling (or always).</summary>
    private void Stamp(string label, bool always = false)
    {
        if ((!Profiling && !always) || _stampLabels.Count >= MaxStamps)
            return;

        _vk.CmdWriteTimestamp(_commandBuffer, PipelineStageFlags.BottomOfPipeBit, _stampPool, (uint)_stampLabels.Count);
        _stampLabels.Add(label);
    }

    private unsafe void ReadStamps()
    {
        ulong[] stamps = new ulong[_stampLabels.Count];
        fixed (ulong* stampsPtr = stamps)
        {
            _vk.GetQueryPoolResults
            (
                _device, _stampPool, 0, (uint)stamps.Length,
                (nuint)(stamps.Length * sizeof(ulong)), stampsPtr, sizeof(ulong),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit
            );
        }

        GpuMilliseconds = (stamps[^1] - stamps[0]) * _timestampPeriod / 1e6;
        if (!Profiling)
            return;

        LastProfile.Clear();
        for (int i = 1; i < stamps.Length; i++)
            LastProfile.Add((_stampLabels[i], (stamps[i] - stamps[i - 1]) * _timestampPeriod / 1e6));
    }

    private unsafe void ReadResults()
    {
        ReadStamps();
        // Submit has counted the submission it recorded into this buffer.
        _resultReadback = (_submissions - 1) & 1;
        GpuState state = *(GpuState*)_readbackMapped[_resultReadback];
        Iterations = (int)state.Iterations;
        RelativeResidual = state.Bb > 0f ? Math.Sqrt(Math.Max(state.Rr, 0f) / state.Bb) : 0.0;
        if (!double.IsFinite(RelativeResidual))
            RelativeResidual = double.PositiveInfinity;

        _resultGeneration = _submittedGeneration;
        _resultTopology = _submittedTopology;
    }

    /// <param name="mode">The pass's color or mode.</param>
    private Pass LevelPass(int level, int mode)
    {
        Level fine = _levels[level];
        Level coarse = level + 1 < _levels.Length ? _levels[level + 1] : default;
        return new Pass
        {
            Size = new ivec4(fine.Size, level),
            CoarseSize = new ivec4(coarse.Size, mode),
            Offsets = new uvec4(fine.NodeOffset, coarse.NodeOffset, fine.BlockOffset, coarse.BlockOffset),
            Parameters = new vec4((vec3)_gravity, (float)CellSize)
        };
    }

    private Pass ScalarPass(int mode)
    {
        Pass pass = LevelPass(0, mode);
        pass.Offsets.y = Groups(_levels[0].Count, ReductionSize);
        return pass;
    }

    /// <summary>Dispatches one thread per node of a pass (64 to a group), then makes its writes visible.</summary>
    private void Dispatch(ComputePipeline pipeline, Pass pass, int threads)
    {
        DispatchGroups(pipeline, pass, Groups(threads, WorkgroupSize));
    }

    private unsafe void DispatchGroups(ComputePipeline pipeline, Pass pass, uint groups)
    {
        if (groups == 0)
            return;

        DescriptorSet set = _set;
        _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Compute, pipeline.Pipeline);
        _vk.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Compute, pipeline.Layout, 0, 1, &set, 0, null);
        _vk.CmdPushConstants(_commandBuffer, pipeline.Layout, ShaderStageFlags.ComputeBit, 0, (uint)Unsafe.SizeOf<Pass>(), &pass);
        _vk.CmdDispatch(_commandBuffer, groups, 1, 1);
        ComputeCommands.Barrier
        (
            _vk, _commandBuffer,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        );
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_inFlight)
        {
            Fence pending = _fence;
            _vk.WaitForFences(_device, 1, &pending, true, ulong.MaxValue);
            _inFlight = false;
        }
        ReleaseBuffers(recycle: false);
        _allocator.Destroy(_staging, _stagingMemory);
        _allocator.ReleaseStaging();
        if (--_programs.References == 0) _programs.Dispose();
        _vk.DestroyFence(_device, _fence, null);
        _vk.DestroyQueryPool(_device, _stampPool, null);
        CommandBuffer cmd = _commandBuffer;
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
    }
}
