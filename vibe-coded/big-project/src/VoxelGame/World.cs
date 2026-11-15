using System.Diagnostics;
using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

using Silk.NET.Vulkan;

namespace VoxelGame;


/// <summary>A ray's first hit on a voxel body.</summary>
internal readonly record struct VoxelHit(VoxelBody Body, ivec3 Cell, ivec3 Normal, double Distance);

/// <param name="MapStressMilliseconds">GPU time of the map's latest stress update.</param>
/// <param name="MapStressResidual">Relative residual of the map's latest stress solution.</param>
/// <param name="StressMilliseconds">Time of the latest object's stress solve on the selected backend.</param>
internal readonly record struct WorldStatistics
(
    int Objects,
    int Contacts,
    double PhysicsMilliseconds,
    double MapStressMilliseconds,
    double MapStressResidual,
    double StressMilliseconds,
    int StressIterations,
    double ImpactMilliseconds
);


/// <summary>
/// The game world: a fixed voxel map and voxel objects, simulated as fixed and rigid bodies,
/// and how they break. Damage takes health off a block (<see cref="BlockMaterial"/>), which
/// makes it weaker and makes it wear faster; it never heals.
/// <list type="bullet">
/// <item>Mining deals damage at a steady rate to the block being mined.</item>
/// <item>Impacts are analysed as a whole on the GPU (<see cref="GpuImpactWorker"/>):
/// how hard each pushes follows from its energy, so from the masses and the speed, and the
/// structure on both sides breaks where it is weakest, not only at the contact. When a
/// heavy object breaks through, it keeps the momentum breaking did not take.</item>
/// <item>Sustained loads (gravity, the smoothed contact forces, and for objects their inertia)
/// are analysed continuously: the map's on the GPU (<see cref="GpuStressLattice"/>), each
/// object's and the player's on the GPU (<see cref="GpuStressWorker"/>). A block stressed past its
/// elastic limit takes damage at a rate that grows with the excess, the most overloaded block
/// of a group first, and only by an analysis of the body as it is now.</item>
/// <item>A block whose health runs out breaks. Map blocks no longer connected to bedrock fall
/// off as new objects; an object cut in two becomes two objects.</item>
/// </list>
/// The player is a body like the objects, an upright box that never turns, whose health
/// wears as a block's does under the same impacts and loads. It walks and jumps by pushing on
/// what it stands on, which is pushed back.
/// </summary>
internal sealed partial class World : IDisposable
{
    public const double Step = 1.0 / 60.0;
    internal double DaylightSeconds { get; set; }
    public const int MinSimulationDistance = 4;
    public const int MaxSimulationDistance = 32;
    public const int DefaultSimulationDistance = 8;

    // Contacts closing faster than this (m/s) are impacts; slower ones are sustained loads.
    private const double MinimumImpactSpeed = 1.0;
    // Impacts with less kinetic energy than this (J) are not analysed.
    private const double MinimumImpactEnergy = 200.0;
    // Map cells around an impact that its analysis includes; the cells at the edge are held.
    private const int MaxImpactRegion = 1024;
    // An impact is analysed once its bodies stop closing fast, or after this many steps: the
    // step that brings two bodies together and the one that stops them often differ.
    private const int MaxImpactSteps = 3;
    // Impacts waiting for analysis beyond which new ones are dropped, their answers too late.
    private const int MaxPendingImpacts = 32;
    // Steps after an impact within which its outcome still gives an object its momentum back.
    private const int MaxRestoreDelay = 15;
    // Damage (HP/s) a block stressed to ρ times its elastic limit takes per unit of ρ² − 1,
    // the energy past the limit in units of what it stores at the limit.
    private const double StressDamageRate = 100.0;
    // Time constant (s) of the smoothing of contact loads before stress analysis.
    private const double LoadSmoothing = 0.1;
    // How far (m) the contact loads on a body must shift before they are analysed again.
    private const double LoadShift = 0.5;
    // Cells under less than this fraction of their strength are not checked for stress damage.
    private const double StressWatchRatio = 0.2;
    // Relative residuals of the map's stress analysis good enough to show, and to break blocks by.
    private const double MapShownResidual = 1e-3;
    private const double MapSettledResidual = 1e-4;
    // Conjugate gradient iterations of the map's analysis per step while it has not settled,
    // and steps between the results shown meanwhile.
    private const int MapIterationsPerStep = 2;
    private const int MapShownInterval = 4;
    private const int MaxStepsPerFrame = 3;
    // A body that moves farther than this (m) in a step jumped there, and is drawn where it is.
    private const double MaxDrawnStep = 4.0;
    private const double PlayerVoidHeight = -64.0;
    private const int PlayerStressIterations = 50;
    // Something is in the player's way when its face is turned at least this much against
    // where the player walks (73°).
    private const double PressedCosine = 0.3;
    // How far beyond the player's side (m) something is in its way.
    private const double ObstacleReach = 0.05;
    // Maximum horizontal movement (m) when stepping onto a ledge.
    private const double StepProbe = 0.15;
    // Lower than this (m) is ground, not something to step onto.
    private const double MinimumStep = 0.01;
    // How far above the player's bottom (m) the rays looking for the ground under it start, so
    // they start outside what it stands on even pressed a little into it.
    private const double GroundProbe = 0.05;
    private const double PlayerStressTolerance = 1e-6;
    // While the simulation follows the player (FollowPlayer), map chunks have colliders only
    // around the bodies that move: every chunk within this far (m) of an awake body's bounds,
    // and as much farther as the body goes in ColliderLookahead (s), has one. Those for the
    // chunks within ColliderKeep (m) more are baked ahead on a background thread, and those no
    // body comes that near any more are dropped. Adding or dropping some rebuilds the physics.
    private const double ColliderReach = 4.0;
    private const double ColliderLookahead = 0.5;
    private const double ColliderKeep = 12.0;
    // Objects farther from the player than the simulation distance are parked; parked ones come
    // back once this much (m) nearer, so that one at the edge is not parked again at once.
    private const double UnparkMargin = 4.0;
    // The box of map cells the map's stress analysis covers while it follows the player: this
    // far (cells) to either side of the player, this far below and above it. It moves on once
    // the player strays this far (m) from its middle. Cells this close to its sides take no
    // stress damage, since the box holds them in place there, which the map beyond does not.
    private const int WindowHalfWidth = 32;
    private const int WindowBelow = 24;
    private const int WindowAbove = 40;
    private const double WindowSlack = 12.0;
    private const int WindowBorder = 3;
    // Steps a move of the box waits at most for one without collider changes.
    private const int MaxWindowWaits = 10;

    /// <param name="commandPool">A pool for <paramref name="queue"/>, which the physics runs on.</param>
    /// <param name="shaderDirectory">assets/shaders: the physics and structural shaders are in its subdirectories.</param>
    /// <param name="stressQueue">
    /// A queue and pool of its own for the map's stress analysis, so that the physics never
    /// waits for its iterations; by default it shares the physics' queue.
    /// </param>
    public World
    (
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        CommandPool commandPool,
        Queue queue,
        string shaderDirectory,
        (Queue Queue, CommandPool Pool)? stressQueue = null
    )
    {
        (Queue stress, CommandPool stressPool) = stressQueue ?? (queue, commandPool);
        Physics = new PhysicsSystem(vk, device, physicalDevice, commandPool, queue, Path.Combine(shaderDirectory, "physics"));
        _createGpuBlasts = () => new(vk, device, physicalDevice, commandPool, queue, shaderDirectory);
        _mapStress = new GpuStressLattice(vk, device, physicalDevice, stressPool, stress, Path.Combine(shaderDirectory, "structural"), VoxelBody.CellSize)
        {
            Tolerance = MapSettledResidual,
            IterationsPerUpdate = MapIterationsPerStep,
            Profiling = Environment.GetEnvironmentVariable("MYT_EXP_STRESSPROF") == "1" // EXP
        };
        Physics.Settings.ReportContacts = true;
        _createGpuStress = edge => new GpuStressLattice(vk, device, physicalDevice, stressPool, stress,
            Path.Combine(shaderDirectory, "structural"), edge, sharePipelinesWith: _mapStress);
        _stress = !GpuSimulationOptions.Enabled("MYT_GPU_STRESS") ? new StressWorker()
            : new GpuStressWorker(_createGpuStress);
        _impactWorker = !GpuSimulationOptions.Enabled("MYT_GPU_IMPACTS") ? new ImpactWorker()
            // Impact damage reads the solver buffers directly. Keep them on the
            // consumer's physics queue/family; scenery stress stays independent.
            : new GpuImpactWorker(edge => new GpuStressLattice(vk, device, physicalDevice, commandPool, queue,
                Path.Combine(shaderDirectory, "structural"), edge, sharePipelinesWith: _mapStress),
                () => _gpuBlasts ??= _createGpuBlasts());
        // The renderer builds interpolated model matrices from CPU poses.
        Physics.Settings.UpdateRigidTransforms = false;
        Physics.Settings.RestitutionMixing = RestitutionMixing.GeometricMean;
        Physics.Settings.MaxContacts = 1 << 17;
        Physics.Settings.MaxContactReports = 1 << 17; // Every contact of the single world substep may load a cell.
        Map = new VoxelMap(_nextId++);
    }

    /// <summary>What an impact took from a moving body, and where it left it.</summary>
    /// <param name="Linear">The change of linear velocity the impact caused, gravity aside.</param>
    /// <param name="Angular">The change of angular velocity the impact caused.</param>
    /// <param name="Position">Where the body was when the impact ended.</param>
    /// <param name="Heading">The body's velocity before the impact.</param>
    private readonly record struct ImpactLoss(dvec3 Linear, dvec3 Angular, dvec3 Position, dvec3 Heading);

    /// <summary>An impact being analysed: its bodies and what it took from the objects among them.</summary>
    private sealed record PendingImpact
    (
        CellBody[] Bodies,
        Dictionary<CellBody, ImpactLoss> Lost,
        Dictionary<CellBody, Dictionary<ivec3, BlockDamageSite>> Sites,
        long Step,
        double Energy
    );

    /// <summary>
    /// What the contacts of an impact on one cell add up to, in the body's frame: the impulse
    /// on it, for its direction, and which way is into the body.
    /// </summary>
    private sealed class ImpactCell
    {
        public dvec3 Impulse { get; set; }
        public dvec3 Inward { get; set; }
        public dvec3 PointSum { get; set; }
        public double PointWeight { get; set; }
        public double AreaSum { get; set; }
    }

    /// <summary>
    /// An impact between two bodies in progress: their contacts over the steps they have been
    /// closing fast, gathered until they stop.
    /// </summary>
    private sealed class OpenImpact(long firstStep)
    {
        public long FirstStep { get; } = firstStep;
        public long LastStep { get; set; }
        public double Energy { get; set; }
        public Dictionary<CellBody, Dictionary<ivec3, ImpactCell>> Sides { get; } = [];
        // Each moving body's linear and angular velocity when the impact began.
        public Dictionary<CellBody, (dvec3 Linear, dvec3 Angular)> Before { get; } = [];
        public Dictionary<CellBody, ImpactMotion> Motion { get; } = [];
    }

    private readonly record struct StepStart(dvec3 Position, dquat Orientation, dvec3 LinearVelocity, dvec3 AngularVelocity);

    /// <summary>Something the player's bottom face pushed on in the last step, and where.</summary>
    /// <param name="Up">The contact normal, pointing into the player: the slope it stands on.</param>
    /// <param name="Weight">The normal impulse there, which shares out the push of the legs.</param>
    private readonly record struct Support(CellBody? Body, ivec3 Cell, dvec3 Point, dvec3 Up, double Weight, DroppedItem? Item = null)
    {
        public RigidBody? Rigid => Item?.Body ?? Body?.Rigid;
        public double Friction => Item != null ? Item.Body!.Friction : Body!.MaterialAt(Cell).Friction;
    }

    /// <summary>Something just in front of the player where it walks, found by <see cref="ObstaclesAhead"/>.</summary>
    /// <param name="Inward">The normal of the face in front, pointing back at the player.</param>
    private readonly record struct Obstacle(VoxelBody Body, ivec3 Cell, dvec3 Point, dvec3 Inward);

    private readonly IStressWorker _stress;
    private readonly ColliderBaker _baker = new();
    private readonly GpuStressLattice _mapStress;
    // The map's cells as the lattice has them, where each is among them, and the topology
    // version they are of; the two arrays its results are published in by turns, and the step
    // of the last.
    private ivec3[] _mapCells = [];
    private readonly List<ivec3> _mapTopologyCells = [];
    private readonly List<CellMaterial> _mapTopologyMaterials = [];
    private readonly List<double> _mapTopologyMasses = [];
    private readonly List<bool> _mapTopologyAnchors = [];
    private BoxIndex? _mapCellIndex;
    private int _mapCellsVersion = -1;
    private CellStress[][] _mapStresses = [[], []];
    private long _mapPublishedStep;
    private bool _deferMapStress;
    private readonly IImpactWorker _impactWorker;
    private readonly Dictionary<(int, int), OpenImpact> _openImpacts = [];
    private readonly List<(int, int)> _endedImpacts = [];
    private readonly List<ivec3> _loadCells = [];
    private readonly Dictionary<int, PendingImpact> _pendingImpacts = [];
    // What impacts that broke through took from their bodies, and the share of it they give back.
    private readonly List<(CellBody Body, ImpactLoss Loss, double Kept)> _restores = [];
    private readonly List<VoxelObject> _objects = [];
    private readonly Dictionary<int, VoxelObject> _objectsById = [];
    private readonly Dictionary<PhysicsBody, CellBody> _owners = [];
    private readonly HashSet<VoxelBody> _topologyChanged = [];
    private readonly Dictionary<VoxelBody, List<(ivec3 Cell, CellMaterial Material)>> _softened = [];
    private readonly Dictionary<VoxelBody, List<ivec3>> _watched = [];
    // Per cell of a settled snapshot: loaded enough to watch. Above this many cells, ranges of
    // them are checked in parallel.
    private bool[] _watchFlags = [];
    private const int ParallelWatchCells = 8192;
    private readonly Dictionary<ivec3, double> _overloadedCells = [];
    private readonly HashSet<ivec3> _overloadSeen = [];
    private readonly List<ivec3> _overloadGroup = [];
    private readonly List<(ivec3 Cell, double Ratio)> _overloadFailures = [];
    private readonly Dictionary<CellBody, StepStart> _stepStart = [];
    private readonly record struct ContactPose(dvec3 Position, dquat Orientation, dquat Inverse, int Index,
        bool SingleCell = false, ivec3 Cell = default);
    private readonly Dictionary<CellBody, ContactPose> _contactPoses = [];
    private readonly List<CellBody> _contactBodies = [];
    private readonly Dictionary<PhysicsBody, int> _contactOwnerIndices = [];
    private ContactPose[] _contactPosesByIndex = [];
    private readonly record struct LocatedContact(int IndexA, int IndexB,
        bool HitA, bool HitB, ivec3 CellA, ivec3 CellB, dvec3 WorldForce, dvec3 ForceA, dvec3 MomentA, dvec3 ForceB, dvec3 MomentB);
    private LocatedContact[] _locatedContacts = [];
    private int[] _loadStarts = [], _loadCursors = [], _loadRefs = [];
    private readonly ParallelOptions _contactParallelOptions = new() { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)) };
    internal static bool ParallelContactMapping { get; set; } = Environment.GetEnvironmentVariable("MYT_PARALLEL_CONTACTS") != "0";
    internal static bool ParallelContactReduction { get; set; } = Environment.GetEnvironmentVariable("MYT_PARALLEL_LOADS") != "0";


    private readonly Dictionary<VoxelBody, LoadSignature> _sentLoads = [];
    private readonly List<Support> _supports = [];
    // The push of the player's legs on what it stands on, as loads: force (N) on a cell, inward normal.
    private readonly List<(CellBody Body, ivec3 Cell, dvec3 Force, dvec3 Inward)> _pushes = [];
    // Steps a pressed jump still waits for the feet to touch down.
    private int _jumpWaiting;
    // Steps the player is still rising from its own jump, while the ground under it is left alone.
    private int _jumpRising;
    // While following the player: the map chunks the awake bodies need colliders in, those
    // whose colliders are kept or baked ahead, and those known to have no faces, so no collider;
    // and the box [low, high) of map cells the stress analysis covers.
    private readonly ChunkCoverage _neededCoverage = new();
    private readonly ChunkCoverage _keptCoverage = new();
    private readonly HashSet<ivec3> _faceless = [];
    private readonly List<ivec3> _droppedColliders = [];
    private (ivec3 Low, ivec3 High)? _window;
    // Steps the box has waited to move, for a step without collider changes.
    private int _windowWaits;
    private int _nextId;
    private int _nextImpactId;
    private long _steps;
    private double _accumulator;
    private double _lastStressMilliseconds;
    private int _lastStressIterations;
    private double _lastImpactMilliseconds;

    public PhysicsSystem Physics { get; }
    public VoxelMap Map { get; }
    public BlockParticles Particles { get; } = new();
    public IReadOnlyList<VoxelObject> Objects => _objects;
    public IEnumerable<VoxelBody> Bodies => _objects.Prepend<VoxelBody>(Map);

    /// <summary>The player, once <see cref="SpawnPlayer"/> has made one.</summary>
    public Player? Player { get; private set; }

    /// <summary>
    /// Whether the simulation follows the player around a map too big to simulate whole, as
    /// one streamed in is (<see cref="ChunkStreamer"/>): objects farther from the player than
    /// <see cref="SimulationDistance"/> are parked until it comes back, map chunks have
    /// colliders only around the player and the objects that are not, and the map's stress
    /// analysis covers a box around the player. Off, the whole map is simulated, as tests
    /// build it.
    /// </summary>
    public bool FollowPlayer { get; set; }

    /// <summary>
    /// How far (chunk columns, 16 m) from the player objects move, collide and break while the
    /// simulation follows it. An object also needs the ground around it loaded, so the map's
    /// streaming distance (<see cref="ChunkStreamer.RenderDistance"/>) limits it too.
    /// </summary>
    public int SimulationDistance
    {
        get;
        set => field = Math.Clamp(value, MinSimulationDistance, MaxSimulationDistance);
    } = DefaultSimulationDistance;

    /// <summary>
    /// Whether the map around the player is loaded, which it needs to move. Until it is,
    /// <see cref="Update"/> holds the world still, so that nothing falls through ground that
    /// is not there yet.
    /// </summary>
    public bool RegionReady => !FollowPlayer || Player == null || GroundLoaded(Player, ColliderReach);

    /// <summary>The map chunks with colliders, and the objects in the physics and parked.</summary>
    public (int Colliders, int Simulated, int Parked) SimulationCounts =>
        (Map.Colliders.Count, _objects.Count(o => o.Body != null), _objects.Count(o => o.Parked));

    /// <summary>Every body contacts act on: the map, the objects and the player.</summary>
    private IEnumerable<CellBody> CellBodies => Player == null ? Bodies : Bodies.Append<CellBody>(Player);

    /// <summary>Raised when an object leaves the world, so its render data can go.</summary>
    public event Action<VoxelBody>? BodyRemoved;

    /// <summary>Raised when a block breaks: the body, the cell, the block and what broke it.</summary>
    public event Action<VoxelBody, ivec3, Voxel, string>? BlockBroken;

    /// <summary>
    /// Raised when an impact has been analysed, before its damage is dealt: its bodies, its
    /// energy (J), what it did and how many steps the answer took.
    /// </summary>
    public event Action<IReadOnlyList<CellBody>, double, ImpactOutcome, long>? ImpactAnalysed;

    /// <summary>Raised when the player takes damage: how much (HP), and what did it.</summary>
    public event Action<double, string>? PlayerHurt;

    public WorldStatistics Statistics => new
    (
        _objects.Count,
        (int)Physics.Statistics.ContactCount,
        Physics.Statistics.GpuMilliseconds,
        _mapStress.GpuMilliseconds,
        _mapStress.RelativeResidual,
        _lastStressMilliseconds,
        _lastStressIterations,
        _lastImpactMilliseconds
    );

    // ---------------------------------------------------------------------------------------
    // Editing
    // ---------------------------------------------------------------------------------------

    /// <summary>Places a block, or removes one with <see cref="BlockType.Air"/>.</summary>
    public void SetBlock(VoxelBody body, ivec3 cell, BlockType type, BlockOrientation orientation = BlockOrientation.Default)
    {
        if (type == BlockType.Air)
        {
            RemoveBlock(body, cell);
            return;
        }

        if (BlockShapes.IsDoor(body.Grid[cell].Type) && body.Grid[cell].Type != type)
            RemoveBlock(body, cell);
        if (type != BlockType.Furnace && body.Grid[cell].Type == BlockType.Furnace)
            BreakFurnace(body, cell);

        if (body.Grid.Set(cell, new Voxel(type, 0, orientation, type == BlockType.Lava)))
        {
            NoteEdit(body, cell);
            _topologyChanged.Add(body);
        }
    }

    public void RemoveBlock(VoxelBody body, ivec3 cell)
    {
        Voxel removed = body.Grid[cell];
        BreakFurnace(body, cell);
        if (!body.Grid.Set(cell, Voxel.Air))
            return;

        NoteEdit(body, cell);
        body.RemovedCells.Add(cell);
        body.ContactLoads.Remove(cell);
        _topologyChanged.Add(body);
        if (BlockShapes.IsDoor(removed.Type))
        {
            ivec3 other = cell + (removed.Type == BlockType.AcaciaDoorBottom ? ivec3.UnitZ : -ivec3.UnitZ);
            BlockType partner = removed.Type == BlockType.AcaciaDoorBottom ? BlockType.AcaciaDoorTop : BlockType.AcaciaDoorBottom;
            if (body.Grid[other].Type == partner) RemoveBlock(body, other);
        }
    }

    /// <summary>
    /// Puts a chunk column into the map, as generated or as it was when it was unloaded. The
    /// map's surface there gets render meshes, and colliders if the player is near.
    /// </summary>
    /// <param name="edited">It holds changes the generator would not make again, so unloading it must keep it.</param>
    public void LoadColumn(MapColumn column, bool edited = false)
    {
        Map.LoadedColumns ??= [];
        if (!Map.LoadedColumns.Add(column.Coordinate))
            throw new InvalidOperationException($"Column {column.Coordinate} is loaded already");

        foreach ((int z, Voxel[] cells) in column.Chunks)
            Map.Grid.SetChunk(new ivec3(column.Coordinate, z), cells);

        if (edited)
            Map.EditedColumns.Add(column.Coordinate);
        Map.ColumnVersion++;
        _topologyChanged.Add(Map);
    }

    /// <summary>
    /// Takes a chunk column out of the map. Returns what it holds if it was changed since it
    /// was generated, to load again when the player comes back; null if generating it again
    /// gives it back as it was.
    /// </summary>
    public MapColumn? UnloadColumn(ivec2 column)
    {
        if (Map.LoadedColumns == null || !Map.LoadedColumns.Remove(column))
            return null;

        bool edited = Map.EditedColumns.Remove(column);
        MapColumn? kept = edited ? CopyColumn(column) : null;
        for (int z = 0; z < VoxelMap.HeightInChunks; z++)
            Map.Grid.RemoveChunk(new ivec3(column, z));

        Map.ColumnVersion++;
        _topologyChanged.Add(Map);
        return kept;
    }

    /// <summary>A copy of what a chunk column of the map holds, as <see cref="UnloadColumn"/> keeps it and a save stores it.</summary>
    public MapColumn CopyColumn(ivec2 column)
    {
        List<(int, Voxel[])> chunks = [];
        for (int z = 0; z < VoxelMap.HeightInChunks; z++)
        {
            if (Map.Grid.TryGetChunk(new ivec3(column, z), out Grid3DChunk<Voxel> cells))
                chunks.Add((z, cells.Cells.ToArray()));
        }

        return new MapColumn(column, [.. chunks]);
    }

    /// <summary>Remembers that a map column holds a change, which unloading it must keep.</summary>
    private void NoteEdit(VoxelBody body, ivec3 cell)
    {
        if (body is VoxelMap map)
            map.EditedColumns.Add(VoxelMap.ColumnOfCell(cell));
    }

    /// <summary>Deals damage (HP) to a block, which breaks once its health runs out. Returns whether it broke.</summary>
    /// <param name="cause">What did it, for <see cref="BlockBroken"/>.</param>
    public bool Damage(VoxelBody body, ivec3 cell, double amount, string cause, BlockDamageSite? site = null)
    {
        Voxel voxel = body.Grid[cell];
        if (voxel.Type == BlockType.Air || voxel.Material.Unbreakable || !(amount > 0.0))
            return false;

        double health;
        ushort damage;
        if (GpuDamageEnabled)
        {
            dvec4 result = (_gpuBlasts ??= _createGpuBlasts()).EvaluateDamage([GpuDamageRequest.Raw(voxel.Material, voxel.Health, amount)])[0];
            health = result.x; damage = (ushort)result.y;
        }
        else { health = voxel.Material.HealthAfter(voxel.Health, amount); damage = Voxel.ToDamage(1.0 - health); }
        return ApplyDamageState(body, cell, voxel, damage, health, cause, site);
    }

    // GPU damage arrives already evaluated and quantized. CPU work here updates
    // its mirror, effects and connectivity; it does not evaluate the damage again.
    private bool ApplyDamageState(VoxelBody body, ivec3 cell, Voxel voxel, ushort damage, double health, string cause, BlockDamageSite? site)
    {
        bool broken = damage == ushort.MaxValue;
        double wear = broken ? 1.0 : (voxel with { Damage = damage }).Wear;
        // Cumulative wear is the share of breaking damage already absorbed. Its integer
        // thresholds carry fractional chips between hits and follow cells through splitting
        // and streaming, without keeping a separate particle counter per block.
        if (broken)
        {
            Break(body, cell, cause, site);
            return true;
        }

        int count = (int)Math.Round(wear * BlockParticles.ParticlesPerBlock) - (int)Math.Round(voxel.Wear * BlockParticles.ParticlesPerBlock);
        Particles.Emit(body, cell, voxel.Type, count, false, site);
        body.SetDamage(cell, 1.0 - health);
        NoteEdit(body, cell);

        int level = (int)((1.0 - health) * 16);
        if (body.SentDamage.GetValueOrDefault(cell) != level)
        {
            body.SentDamage[cell] = level;
            if (!_softened.TryGetValue(body, out List<(ivec3, CellMaterial)>? changes))
                _softened[body] = changes = [];
            changes.Add((cell, voxel.Material.Elastic(health)));
        }

        return false;
    }

    /// <summary>Breaks a block outright, whatever it is made of, with the particles of what wear it had left.</summary>
    /// <param name="cause">What did it, for <see cref="BlockBroken"/>.</param>
    private void Break(VoxelBody body, ivec3 cell, string cause, BlockDamageSite? site)
    {
        Voxel voxel = body.Grid[cell];
        int count = BlockParticles.ParticlesPerBlock - (int)Math.Round(voxel.Wear * BlockParticles.ParticlesPerBlock);
        Particles.Emit(body, cell, voxel.Type, count, true, site);
        RemoveBlock(body, cell);
        if (voxel.Type == BlockType.Ice && body is VoxelMap)
            SetBlock(body, cell, BlockType.Water);
        BlockBroken?.Invoke(body, cell, voxel, cause);
    }

    /// <summary>Mines a block by hand for <paramref name="seconds"/>. Returns whether it broke.</summary>
    public bool Mine(VoxelBody body, ivec3 cell, double seconds, BlockDamageSite? site = null)
    {
        Voxel voxel = body.Grid[cell];
        return voxel.Type != BlockType.Air && Damage(body, cell, voxel.Material.MiningRate * seconds, "mining", site);
    }

    /// <summary>
    /// One strike at the ray's hit point with a tool, or a bare hand (<see cref="Tool.Hand"/>):
    /// the damage a strike of it deals (<see cref="Tool.Damage"/>), none if it does not get past
    /// the block's elastic limit, and its thrust, with equal recoil. Returns whether the block broke.
    /// </summary>
    /// <param name="instant">Break the block with this strike whatever it is made of, bedrock too, as creative mode can.</param>
    public bool Mine(VoxelHit hit, dvec3 origin, dvec3 direction, Tool tool, bool instant = false)
    {
        if (Player is not Player player || player.Dead || !hit.Body.Grid.IsOccupied(hit.Cell) || !(direction.LengthSqr > 0.0))
            return false;

        if (hit.Body.Grid[hit.Cell].Type == BlockType.Tnt)
        {
            Detonate(hit.Body, hit.Cell);
            return false; // The consumed explosive does not drop a mined item.
        }

        dvec3 inward = direction.Normalized;
        dvec3 point = origin + hit.Distance * inward;
        dvec3 impulse = (Player.MiningForce / Player.MiningHitsPerSecond) * inward;
        if (hit.Body is VoxelObject obj)
            obj.ApplyMiningImpulse(impulse, point);
        player.Body.ApplyImpulse(-impulse, point);

        BlockDamageSite site = new(hit.Body.ToLocal(point), new dvec3(hit.Normal));
        if (instant)
        {
            Break(hit.Body, hit.Cell, "mining", site);
            return true;
        }

        Voxel voxel = hit.Body.Grid[hit.Cell];
        if (GpuDamageEnabled && !voxel.Material.Unbreakable)
        {
            CellStress sustained = hit.Body.Stress is { Settled: true } current && current.Version == hit.Body.TopologyVersion
                ? hit.Body.StaticStress(hit.Cell) : default;
            dvec4 result = (_gpuBlasts ??= _createGpuBlasts()).EvaluateDamage([
                GpuDamageRequest.Tool(voxel, tool, 1.0 / Player.MiningHitsPerSecond, sustained)])[0];
            return result.z > 0 && ApplyDamageState(hit.Body, hit.Cell, voxel, (ushort)result.y, result.x, "mining", site);
        }
        return Damage(hit.Body, hit.Cell, tool.Damage(voxel.Material, voxel.Health, 1.0 / Player.MiningHitsPerSecond,
            hit.Body.Stress is { Settled: true } stress && stress.Version == hit.Body.TopologyVersion
                ? hit.Body.StaticStress(hit.Cell) : default), "mining", site);
    }

    /// <summary>Adds a new object made of the given blocks (in its own frame) at a pose and velocity.</summary>
    public VoxelObject Spawn
    (
        IEnumerable<(ivec3 Cell, BlockType Type)> blocks,
        dvec3 position,
        dquat orientation,
        dvec3 velocity = default,
        dvec3 angularVelocity = default
    )
    {
        return Spawn(blocks.Select(b => (b.Cell, new Voxel(b.Type, 0))), position, orientation, velocity, angularVelocity);
    }

    /// <summary>Adds a new object made of the given cells (in its own frame), worn as they are, at a pose and velocity.</summary>
    public VoxelObject Spawn
    (
        IEnumerable<(ivec3 Cell, Voxel Voxel)> cells,
        dvec3 position,
        dquat orientation,
        dvec3 velocity = default,
        dvec3 angularVelocity = default
    )
    {
        VoxelObject spawned = new(_nextId++)
        {
            PendingPosition = position,
            PendingOrientation = orientation,
            PendingLinearVelocity = velocity,
            PendingAngularVelocity = angularVelocity
        };

        foreach ((ivec3 cell, Voxel voxel) in cells)
            spawned.Grid.Set(cell, voxel);

        _objects.Add(spawned);
        _objectsById.Add(spawned.Id, spawned);
        _topologyChanged.Add(spawned);
        return spawned;
    }

    internal bool HasPendingTopology(VoxelObject body) => _topologyChanged.Contains(body);

    /// <summary>The nearest block a ray hits on the map or any object, within <paramref name="maxDistance"/> m.</summary>
    public VoxelHit? Raycast(dvec3 origin, dvec3 direction, double maxDistance)
    {
        double length = direction.Length;
        if (!(length > 0.0) || maxDistance < 0.0)
            return null;
        dvec3 unit = direction / length;
        VoxelHit? nearest = null;
        foreach (VoxelBody body in Bodies)
        {
            double distanceLimit = nearest?.Distance ?? maxDistance;
            if (body is VoxelObject obj && !_topologyChanged.Contains(obj))
            {
                // Reject the whole object before walking its voxel grid. Walking and pushing
                // cast many short rays each step, and most objects are nowhere near them.
                dvec3 center = obj.Position + obj.Orientation * obj.Bounds.Center - origin;
                double along = Math.Clamp(dvec3.Dot(center, unit), 0.0, distanceLimit);
                double radius = obj.Bounds.Radius + 1e-7;
                if ((center - along * unit).LengthSqr > radius * radius)
                    continue;
            }
            dvec3 localOrigin = body.ToLocal(origin) / VoxelBody.CellSize;
            dvec3 localDirection = body.DirectionToLocal(direction);
            double reach = distanceLimit / VoxelBody.CellSize;
            if (BlockShapes.Raycast(body.Grid, localOrigin, localDirection, reach, out Grid3DHit hit))
            {
                double distance = hit.Distance * VoxelBody.CellSize;
                if (nearest == null || distance < nearest.Value.Distance)
                    nearest = new VoxelHit(body, hit.Cell, hit.Normal, distance);
            }
        }

        return nearest;
    }

    // ---------------------------------------------------------------------------------------
    // The player
    // ---------------------------------------------------------------------------------------

    /// <summary>Puts the player in the world standing at <paramref name="feet"/>, which is also where it respawns.</summary>
    public Player SpawnPlayer(dvec3 feet)
    {
        if (Player != null)
            throw new InvalidOperationException("The world has a player already");

        Player = new Player(_nextId++, feet);
        Physics.Add(Player.Body);
        _owners[Player.Body] = Player;
        return Player;
    }

    /// <summary>Brings the player back where it was spawned, at rest and unhurt.</summary>
    public void RespawnPlayer()
    {
        if (Player == null)
            return;

        foreach (int id in _pendingImpacts.Where(p => p.Value.Bodies.Contains(Player)).Select(p => p.Key).ToArray())
            _pendingImpacts.Remove(id);
        foreach (var pair in _openImpacts.Where(p => p.Value.Sides.ContainsKey(Player)).Select(p => p.Key).ToArray())
            _openImpacts.Remove(pair);
        _restores.RemoveAll(r => r.Body == Player);
        _supports.Clear();
        _jumpRising = _jumpWaiting = 0;
        Player.Grounded = false;
        Player.Health = 1.0;
        Player.SetCrouching(false, grounded: true);
        Player.MoveTo(Player.Spawn);
        Player.ContactLoads.Clear();
        Array.Clear(Player.Stresses);
    }

    /// <summary>
    /// Deals damage (HP) to the player as to a block: the less health it has left, the more
    /// each HP costs, and the less it endures. Nothing while it is invulnerable or dead.
    /// </summary>
    public void DamagePlayer(double amount, string cause)
    {
        if (Player == null || Player.Dead || Player.Invulnerable || !(amount > 0.0))
            return;

        Player.Health = GpuDamageEnabled
            ? (_gpuBlasts ??= _createGpuBlasts()).EvaluateDamage([GpuDamageRequest.Raw(Player.Material, Player.Health, amount)])[0].x
            : Player.Material.HealthAfter(Player.Health, amount);
        PlayerHurt?.Invoke(amount, cause);
    }

    /// <summary>
    /// Moves the player as its controls ask, before a step. Its legs push it the way it asks,
    /// along what it stands on, up and down slopes too, and the ground's friction drags on its
    /// speed over the ground; both grow with that friction (<see cref="Player.WalkAcceleration"/>),
    /// so grippy ground gets the player going and stops it faster than slippery ground, at the
    /// same walking speed. A pressed jump launches at <see cref="Player.JumpSpeed"/> relative
    /// to the ground. Whatever the legs give the player they take from what it stands on
    /// (Newton's third law), shared by how hard
    /// each support pushes back. Its hands push what it walks into (<see cref="PushObstacles"/>),
    /// and it steps up onto ledges low enough (<see cref="StepUp"/>). In the air it can steer a
    /// little, pushing on nothing, and the air drags on it (<see cref="Player.AirDrag"/>).
    /// Flight steers too, and also holds the player up against its gravity, which its body
    /// feels <see cref="Player.GravityScale"/> times as hard as the rest of the world. A dead
    /// player neither flies nor walks, but still comes to rest where it lands.
    /// </summary>
    private void DrivePlayer()
    {
        _pushes.Clear();
        if (Player is not Player player)
            return;

        player.ViewLag *= Math.Exp(-Step / Player.StepSmoothing);
        if (player.JumpPressed)
        {
            _jumpWaiting = (int)Math.Ceiling(Player.JumpBuffer / Step);
            player.JumpPressed = false;
        }

        PlayerControls controls = player.Dead ? default : player.Controls;
        bool crouch = controls.Crouch && !player.Flying;
        if (crouch != player.Crouching)
        {
            bool grounded = !player.Flying && _jumpRising == 0 && (player.Grounded || GroundBelow(player).Length > 0);
            UpdateCrouch(player, crouch, grounded);
        }
        dvec3 velocity = player.Body.LinearVelocity;
        if (player.Flying && !player.Dead)
        {
            dvec3 steer = Limit(controls.Direction * Player.FlySpeed - velocity, Player.FlyAcceleration * Step);
            player.Body.ApplyImpulse(Player.Mass * (steer - Step * PlayerGravity));
            _jumpWaiting = 0;
            return;
        }

        dvec3 wish = new(controls.Direction.x, controls.Direction.y, 0.0);
        if (player.Crouching)
        {
            if (wish.LengthSqr > 1.0)
                wish = wish.Normalized;
            wish *= Player.CrouchSpeedFactor;
        }
        dvec3 asked = Player.WalkSpeed * wish;
        Support[] supports = [.. _supports.Where(s => s.Item is { Body: not null } item
            ? _itemBodies.GetValueOrDefault(item.Body) == item
            : s.Body != null && IsPresent(s.Body) && (s.Body is not VoxelBody voxels || voxels.Grid.IsOccupied(s.Cell)))];
        if (_jumpRising == 0 && (supports.Length == 0 || supports.Any(s => s.Item is { } item && !item.Stack.Info.IsBlock)))
        {
            // A thin tool on the floor is not the only footing: the reachable floor
            // takes the leg load, rather than an instantaneous kick through the tool.
            Support[] below = GroundBelow(player);
            if (below.Length > 0) supports = below;
        }
        _jumpRising = Math.Max(0, _jumpRising - 1);
        if (supports.Length == 0)
        {
            _jumpWaiting = Math.Max(0, _jumpWaiting - 1);
            dvec3 steer = dvec3.Zero;
            if (wish.LengthSqr > 0.0)
            {
                steer = asked - velocity;
                steer.z = 0.0;
                steer = Limit(steer, Player.AirAcceleration * Step);
            }

            // Drag against the velocity, k·v² (dv/dt = −k·|v|·v), integrated exactly over the step.
            dvec3 drag = velocity * (1.0 / (1.0 + Player.AirDrag * velocity.Length * Step) - 1.0);
            player.Body.ApplyImpulse(Player.Mass * (steer + drag));
            return;
        }

        double total = supports.Sum(s => s.Weight);
        double Share(Support support) => total > 0.0 ? support.Weight / total : 1.0 / supports.Length;

        dvec3 ground = dvec3.Zero;
        dvec3 up = dvec3.Zero;
        double friction = 0.0;
        foreach (Support support in supports)
        {
            ground += Share(support) * SurfaceVelocity(support.Rigid, support.Point);
            up += Share(support) * support.Up;
            friction += Share(support) * support.Friction;
        }

        // Along the ground, so that a slope is walked up and down rather than into, and never
        // against the ground, which landing and jumping are about.
        up = up.LengthSqr > 0.0 ? up.Normalized : dvec3.UnitZ;
        dvec3 Along(dvec3 v) => v - dvec3.Dot(v, up) * up;
        dvec3 wanted = Along(asked);
        if (wanted.LengthSqr > 0.0)
            wanted *= asked.Length / wanted.Length;

        // The legs push the way asked, at a = μ·WalkAcceleration·(asked / WalkSpeed), and the
        // ground drags on the speed over it, at μ·WalkAcceleration·(v / WalkSpeed): together
        // the speed relaxes toward the speed asked for at the rate below, integrated exactly
        // over the step. The legs also hold the player against the pull of gravity down the
        // slope over the coming step, or it would creep down a slope it stands on, and up one
        // walk slowly.
        double rate = Player.WalkAcceleration * friction / Player.WalkSpeed;
        dvec3 change = (wanted - Along(velocity - ground)) * (1.0 - Math.Exp(-rate * Step)) - Step * Along(PlayerGravity);
        if (_jumpWaiting > 0)
        {
            // A ground probe can catch a buffered jump just before landing, while the body
            // still has its falling velocity. Launch relative to the support, so that fall
            // cannot cancel the jump and a moving platform carries its speed into takeoff.
            change.z = ground.z + Player.JumpSpeed - velocity.z;
            _jumpWaiting = 0;
            _jumpRising = (int)Math.Ceiling(Player.JumpSpeed / PlayerGravity.Length / Step);
        }

        dvec3 stepMotion = asked * Step;
        if (player.Crouching && _jumpRising == 0)
        {
            dvec3 next = velocity + change;
            dvec3 safe = CrouchMotion(player, next * Step);
            change.x = safe.x / Step - velocity.x;
            change.y = safe.y / Step - velocity.y;
            stepMotion = safe;
        }
        PushOff(player, supports, Share, Player.Mass * change);
        if (wish.LengthSqr == 0.0)
            return;

        dvec3 forward = wish.Normalized;
        List<Obstacle> obstacles = ObstaclesAhead(player, forward);
        PushObstacles(player, obstacles, asked, supports, Share);
        StepUp(player, stepMotion);
    }

    /// <summary>The gravity the player feels, <see cref="Player.GravityScale"/> times the world's.</summary>
    private dvec3 PlayerGravity => Player.GravityScale * Physics.Settings.Gravity;

    /// <summary>Gives the player an impulse its feet take from what it stands on, which gets the opposite.</summary>
    private void PushOff(Player player, Support[] supports, Func<Support, double> share, dvec3 push)
    {
        if (push.LengthSqr == 0.0)
            return;

        // Block platforms use the prescribed walking/takeoff speed. A loose hand
        // tool cannot act as that platform: include its linear and angular response
        // when changing relative speed, or the kick gives it enormous kinetic energy
        // and an equally enormous swept collision workload.
        dvec3 direction = push.Normalized;
        double inverseMass = 1.0 / Player.Mass;
        foreach (Support support in supports)
            if (support.Item is { } item && !item.Stack.Info.IsBlock)
                inverseMass += Math.Pow(share(support), 2) * ContactInverseMass(support.Rigid, support.Point, direction);
        push /= Player.Mass * inverseMass;
        player.Body.ApplyImpulse(push);
        _pushes.Add((player, Player.Cells[0], push / Step, dvec3.UnitZ));
        PushSupports(supports, share, -push);
    }

    private static double ContactInverseMass(RigidBody? rigid, dvec3 point, dvec3 direction)
    {
        if (rigid == null) return 0;
        double inverse = 1 / rigid.Mass;
        if (!rigid.FixedRotation)
        {
            dvec3 torque = dvec3.Cross(point - rigid.WorldCenterOfMass, direction);
            dmat3 rotation = rigid.Orientation.ToMat3;
            inverse += dvec3.Dot(torque, rotation * rigid.LocalInertia.Inverse * rotation.Transposed * torque);
        }
        return inverse;
    }

    /// <summary>Hands an impulse to what the player stands on, shared by how hard each support pushes back.</summary>
    private void PushSupports(Support[] supports, Func<Support, double> share, dvec3 impulse)
    {
        foreach (Support support in supports)
        {
            dvec3 part = share(support) * impulse;
            support.Rigid?.ApplyImpulse(part, support.Point);
            if (support.Body != null)
                _pushes.Add((support.Body, support.Cell, part / Step, -dvec3.UnitZ));
        }
    }

    /// <summary>
    /// The player's hands push the objects it walks into toward the velocity it walks at, along
    /// their contacts' normals, with up to <see cref="Player.PushForce"/> shared among them; its
    /// feet take the push back into what it stands on (Newton's third law), so its own momentum
    /// does not change. The push goes straight to the object: passed on through the contact by
    /// the legs alone, it would be too weak to slide a block, and the solver passes on only part
    /// of what a light body pressing on a heavier one gives it.
    /// </summary>
    /// <param name="asked">The velocity the player walks at, which the hands push toward.</param>
    private void PushObstacles(Player player, List<Obstacle> obstacles, dvec3 asked, Support[] supports, Func<Support, double> share)
    {
        List<IGrouping<VoxelBody, Obstacle>> pushed = [.. obstacles.Where(o => o.Body is VoxelObject && o.Body.Rigid != null && IsPresent(o.Body)).GroupBy(o => o.Body)];
        foreach (IGrouping<VoxelBody, Obstacle> faces in pushed)
        {
            RigidBody rigid = faces.Key.Rigid!;
            dvec3 inward = dvec3.Zero;
            foreach (Obstacle face in faces)
                inward += face.Inward;

            dvec3 along = new(-inward.x, -inward.y, 0.0);
            if (along.LengthSqr == 0.0)
                continue;

            // The hands spread the push over the face, in front of the player and as high as
            // the object's center of mass if they reach it, rather than at one corner: pushed
            // there, a block would spin and bounce off.
            along = along.Normalized;
            double reach = 0.5 * Player.Width * (Math.Abs(along.x) + Math.Abs(along.y));
            double height = Math.Clamp(rigid.WorldCenterOfMass.z - player.Feet.z, 0.1, player.CurrentHeight - 0.1);
            dvec3 point = player.Feet + reach * along + new dvec3(0, 0, height);

            double wanted = dvec3.Dot(asked, along) - dvec3.Dot(SurfaceVelocity(faces.Key, point), along);
            double impulse = Math.Min(Player.PushForce * Step / pushed.Count, rigid.Mass * wanted);
            if (!(impulse > 0.0))
                continue;

            // Pushing harder with one hand than the other, the hands also keep the object from
            // turning away about the vertical, which would let it slip past to one side.
            dvec3 push = impulse * along;
            dmat3 rotation = rigid.Orientation.ToMat3;
            double spinInertia = (rotation * rigid.LocalInertia * rotation.Transposed).m22;
            double turning = dvec3.Cross(point - rigid.WorldCenterOfMass, push).z;
            dvec3 side = new(-along.y, along.x, 0.0);
            point += Math.Clamp((turning + spinInertia * rigid.AngularVelocity.z) / impulse, -0.5 * Player.Width, 0.5 * Player.Width) * side;
            rigid.ApplyImpulse(push, point);
            _pushes.Add((faces.Key, faces.First().Cell, push / Step, along));
            PushSupports(supports, share, -push);
        }
    }

    /// <summary>
    /// Steps the player up onto what it walks into when that is no higher than
    /// <see cref="Player.StepHeight"/>, like a stair or a slab, or a tilted object's low edge:
    /// lifts it onto the top at once, keeping its motion, while the view follows over
    /// <see cref="Player.StepSmoothing"/>. Only when what is in the way reaches no higher than
    /// that and there is room on top.
    /// </summary>
    private void StepUp(Player player, dvec3 motion)
    {
        if (_jumpRising > 0 || motion.LengthSqr < 1e-12) return;
        // Raise only when the next footprint reaches the ledge, and put the feet over it
        // in the same operation. Probing beyond the footprint lifted the player into empty
        // air repeatedly. A box test also catches shallow approaches that forward rays miss.
        motion = Limit(motion, StepProbe);
        double inset = 0.005;
        BoxCollision.CollisionBox BoxAt(dvec3 feet) => new(feet + new dvec3(0, 0, player.CurrentHeight / 2 + inset),
            new dvec3(Player.Width / 2 - inset, Player.Width / 2 - inset, player.CurrentHeight / 2 - inset), dquat.Identity);
        BoxCollision.CollisionBox probe = BoxAt(player.Feet + motion);
        probe = probe with { Half = probe.Half + new dvec3(ObstacleReach, ObstacleReach, 0) };
        // A shallow lip may have an upward SAT normal; ankle rays still see its
        // side. Require a steep face so ordinary slope walking never adds a step.
        var sides = _collisionQueries.StepSides(probe, motion);
        if (sides.Count == 0 && ObstaclesAhead(player, motion.Normalized).Count == 0) return;
        if (!player.Crouching)
        {
            dvec3 requested = motion;
            foreach (var (point, normal) in sides)
            {
                dvec3 into = new dvec3(-normal.x, -normal.y, 0).Normalized;
                double extent = Player.Width / 2 * (Math.Abs(into.x) + Math.Abs(into.y));
                double gap = dvec3.Dot(point - player.Feet, into) - extent;
                double extra = gap + .02 - dvec3.Dot(motion, into);
                // A glancing approach needs a small sole patch over the tread. A
                // distant SAT plane or several corner faces cannot accumulate travel.
                if (extra > 0 && extra <= ObstacleReach)
                    motion = requested + Limit(motion + into * extra - requested, .025);
            }
            motion = Limit(motion, StepProbe);
        }
        dvec3 nextFeet = player.Feet + motion;
        if (!PlayerBoxBlocked(BoxAt(nextFeet))) return;
        if (LedgeHeight(player, nextFeet, Player.StepHeight) is not double height) return;
        dvec3 lift = new(0, 0, height + 0.002);
        if (PlayerBoxBlocked(BoxAt(nextFeet + lift)) || PlayerBoxBlocked(BoxAt(player.Feet + lift))) return;

        player.Body.Position += motion + lift;
        dvec3 velocity = player.Body.LinearVelocity;
        if (velocity.z < 0) player.Body.LinearVelocity = new(velocity.x, velocity.y, 0);
        player.ViewLag += lift.z;
    }

    /// <summary>The highest walkable surface under the proposed full footprint, within step height.</summary>
    private double? LedgeHeight(Player player, dvec3 nextFeet, double highest)
    {
        double half = Player.Width / 2 - 0.005;
        double top = highest + 0.05;
        double found = double.NegativeInfinity;
        foreach (double x in (double[])[-half, 0, half])
        foreach (double y in (double[])[-half, 0, half])
        {
            dvec3 above = nextFeet + new dvec3(x, y, top);
            if (Raycast(above, -dvec3.UnitZ, top) is not VoxelHit hit) continue;
            double height = top - hit.Distance;
            if (height > highest) return null;
            dvec3 up = hit.Body.Orientation * new dvec3(hit.Normal);
            if (up.z >= Player.SupportSlope) found = Math.Max(found, height);
        }
        return found >= MinimumStep ? found : null;
    }

    private static dvec3 Limit(dvec3 vector, double length)
    {
        double current = vector.Length;
        return current > length ? vector * (length / current) : vector;
    }

    /// <summary>How fast a body's surface moves at a world point.</summary>
    private static dvec3 SurfaceVelocity(CellBody body, dvec3 point)
    {
        return SurfaceVelocity(body.Rigid, point);
    }

    private static dvec3 SurfaceVelocity(RigidBody? rigid, dvec3 point)
    {
        if (rigid == null)
            return dvec3.Zero;

        return rigid.LinearVelocity + dvec3.Cross(rigid.AngularVelocity, point - rigid.WorldCenterOfMass);
    }

    /// <summary>
    /// What the player stands on, found by rays cast down from just above its bottom, at its
    /// middle and near its corners, for when no contact reported it: the solver's final
    /// contact forces can leave out one that held the player up during the step, as on a slope
    /// whose box rides on one edge. Within <see cref="Player.SupportReach"/> of the bottom and
    /// no steeper than <see cref="Player.SupportSlope"/>, shared alike.
    /// </summary>
    private Support[] GroundBelow(Player player)
    {
        List<Support> found = [];
        double inset = 0.5 * Player.Width - (player.Crouching ? 0.0001 : 0.05);
        foreach (dvec2 offset in (dvec2[])[dvec2.Zero, new(-inset, -inset), new(inset, -inset), new(-inset, inset), new(inset, inset)])
        {
            dvec3 origin = player.Feet + new dvec3(offset, GroundProbe);
            if (Raycast(origin, -dvec3.UnitZ, GroundProbe + Player.SupportReach) is not VoxelHit hit)
                continue;

            dvec3 up = hit.Body.Orientation * new dvec3(hit.Normal);
            if (up.z >= Player.SupportSlope)
                found.Add(new Support(hit.Body, hit.Cell, origin - hit.Distance * dvec3.UnitZ, up, 1.0));
        }

        return [.. found];
    }

    /// <summary>Keeps a contact as something the player stands on if it pushes the bottom face up.</summary>
    /// <param name="up">The contact normal, pointing into the player.</param>
    private void NoteSupport(CellBody ground, ivec3 cell, ContactReport contact, dvec3 up)
    {
        double bottom = StepStartPosition(Player!).z;
        if (up.z >= Player.SupportSlope && contact.Point.z <= bottom + Player.SupportReach)
            _supports.Add(new Support(ground, cell, contact.Point, up, contact.NormalImpulse));
    }

    /// <summary>
    /// What is in the player's way, within <see cref="ObstacleReach"/> of its side where it
    /// walks: the faces that rays cast from inside it that way meet, from its ankles to its
    /// head and across its width, facing back at it steeply or slightly tilted. Rays rather
    /// than contacts, which are only reported while they push.
    /// </summary>
    private List<Obstacle> ObstaclesAhead(Player player, dvec3 forward)
    {
        List<Obstacle> found = [];
        dvec3 side = new(-forward.y, forward.x, 0.0);
        double inside = 0.5 * Player.Width * (Math.Abs(forward.x) + Math.Abs(forward.y));
        double across = 0.5 * Player.Width * (Math.Abs(side.x) + Math.Abs(side.y)) - 0.05;
        // Include every ledge high enough for StepUp, including shallow lips.
        foreach (double height in (double[])[MinimumStep, 0.5, 1.0, player.CurrentHeight - 0.1])
        foreach (double offset in (double[])[-across, 0.0, across])
        {
            dvec3 origin = player.Feet + offset * side + new dvec3(0, 0, height);
            if (Raycast(origin, forward, inside + ObstacleReach) is not VoxelHit hit)
                continue;

            dvec3 inward = hit.Body.Orientation * new dvec3(hit.Normal);
            if (Math.Abs(inward.z) < Player.SupportSlope && dvec3.Dot(inward, forward) < -PressedCosine)
                found.Add(new Obstacle(hit.Body, hit.Cell, origin + hit.Distance * forward, inward));
        }

        return found;
    }

    /// <summary>
    /// The player's sustained loads, analysed as a body's are: gravity and the smoothed contact
    /// loads on its three cells, with inertia relief. Past its elastic limit it takes damage as
    /// an overloaded block does, at the pace of its worst cell, since they share one health.
    /// The GPU backend schedules the player alongside the other bodies; the CPU reference
    /// solves its three cells directly each step.
    /// </summary>
    private void ApplyPlayerStress()
    {
        if (Player is not Player player || player.Dead)
            return;

        if (_stress is GpuStressWorker)
        {
            ApplyGpuPlayerStress(player);
            return;
        }

        if (player.ContactLoads.Count == 0)
        {
            Array.Clear(player.Stresses);
            return;
        }

        ivec3[] cells = Player.Cells;
        CellMaterial material = Player.Material.Elastic(player.Health);
        double cellMass = Player.Mass / cells.Length;
        double[] masses = [.. cells.Select(_ => cellMass)];
        StressLattice lattice = new(Player.Width, cells, [.. cells.Select(_ => material)], new bool[cells.Length]);

        dvec3 gravity = player.DirectionToLocal(PlayerGravity);
        for (int i = 0; i < cells.Length; i++)
        {
            (dvec3 force, dvec3 moment) = player.ContactLoads.GetValueOrDefault(cells[i]);
            lattice.Forces[i] = cellMass * gravity + force;
            lattice.Moments[i] = moment;
        }

        RigidMass.Of(cells, masses, Player.Width).Relieve(cells, masses, lattice.Forces, lattice.Moments, dvec3.Zero);
        lattice.Solve(PlayerStressIterations, PlayerStressTolerance);
        lattice.ComputeStresses(player.Stresses);

        ElasticLimits limits = Player.Material.ElasticLimit(player.Health);
        double ratio = player.Stresses.Max(limits.Ratio);
        if (ratio > 1.0)
            DamagePlayer(Step * StressDamageRate * (ratio * ratio - 1.0), $"a load at {ratio:F2}x its elastic limit");
    }

    // ---------------------------------------------------------------------------------------
    // Simulation
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Advances the world by a frame's worth of fixed steps and applies every change they
    /// caused. While the map around the player is not all loaded (<see cref="RegionReady"/>),
    /// it holds still instead.
    /// </summary>
    public void Update(double frameTime)
    {
        _accumulator = RegionReady ? Math.Min(_accumulator + frameTime, MaxStepsPerFrame * Step) : 0.0;
        ProfSteps = 0; // PROF
        while (_accumulator >= Step)
        {
            StepOnce();
            ProfSteps++; // PROF
            _accumulator -= Step;
        }

        Prof.Time("w.recvStress", ReceiveStress);
        Prof.Time("w.topo", ApplyTopologyChanges);
    }

    /// <summary>
    /// Where a body is drawn: between where the last step found it and where it left it, as far
    /// as the time since has gone into the next step. Steps come at 60 Hz whatever the frame
    /// rate, so drawn where the last step left them, bodies would move in uneven jumps: at 75
    /// frames a second, one frame in five would show them where the one before did. One that
    /// jumped farther in the step than anything moves, as one put back where it spawns does,
    /// is drawn where it is.
    /// </summary>
    public (dvec3 Position, dquat Orientation) DrawnPose(CellBody body)
    {
        if (!_stepStart.TryGetValue(body, out StepStart start))
            return (body.Position, body.Orientation);

        return Interpolate(start.Position, start.Orientation, body.Position, body.Orientation);
    }

    /// <summary>A pose as far from where the last step began to where it ended as the time since has gone into the next; the end, for a jump farther than anything moves.</summary>
    private (dvec3 Position, dquat Orientation) Interpolate(dvec3 startPosition, dquat startOrientation, dvec3 position, dquat orientation)
    {
        if ((position - startPosition).LengthSqr > MaxDrawnStep * MaxDrawnStep)
            return (position, orientation);

        double t = _accumulator / Step;
        dquat end = orientation;
        if (dquat.Dot(startOrientation, end) < 0.0)
            end = new dquat(-end.x, -end.y, -end.z, -end.w);

        dquat q = startOrientation;
        dquat between = new(q.x + t * (end.x - q.x), q.y + t * (end.y - q.y), q.z + t * (end.z - q.z), q.w + t * (end.w - q.w));
        return (startPosition + t * (position - startPosition), between.Normalized);
    }

    /// <summary>Where the player's eyes are drawn, as its body is (<see cref="DrawnPose"/>).</summary>
    public dvec3 DrawnEye => Player!.Eye + (DrawnPose(Player).Position - Player.Position);

    /// <summary>One fixed step, also for tests that drive the world directly.</summary>
    public int ProfSteps { get; private set; } // PROF
    public int ExpLoadSends, ExpResults, ExpShown, ExpPublished; // EXP
    public string ExpStress => $"steps {_steps} results {ExpResults} shown {ExpShown} published {ExpPublished} sends {ExpLoadSends} nodes {_mapStress.CellCount} it {_mapStress.Iterations} res {_mapStress.RelativeResidual:E1} " + string.Join(",", _mapStress.LastProfile.Select(p => $"{p.Phase}={p.Milliseconds:F2}")); // EXP

    public void StepOnce()
    {
        Prof.Time("s.topo", ApplyTopologyChanges);
        Prof.Time("s.blasts", () => AdvanceExplosions(Step));
        if (FollowPlayer)
            Prof.Time("s.follow", FollowThePlayer);
        RestoreMomentum();
        Prof.Time("s.separate", SeparateOverlaps);
        Prof.Time("s.drive", DrivePlayer);

        _stepStart.Clear();
        foreach (CellBody body in CellBodies)
        {
            if (body.Rigid != null)
                _stepStart[body] = new StepStart(body.Position, body.Orientation, body.LinearVelocity, body.AngularVelocity);
        }

        RecordItemStarts();

        Physics.Profile = Prof.On && _steps % 120 == 0 && Environment.GetEnvironmentVariable("MYT_PROFILE") == "1";
        Prof.Time("s.physics", () => Physics.Update(Step));
        Prof.Add("s.physRebuild", Physics.Timings.RebuildMilliseconds);
        Prof.Add("s.physRecord", Physics.Timings.RecordingMilliseconds);
        Prof.Add("s.physWait", Physics.Timings.WaitMilliseconds);
        Prof.Add("s.physReadback", Physics.Timings.ReadbackMilliseconds);
        if (Physics.Profile)
        {
            string phases = string.Join(" ", Physics.LastProfile.GroupBy(p => p.Phase)
                .Select(g => $"{g.Key}={g.Sum(p => p.Milliseconds):F2}"));
            Console.WriteLine($"   physics GPU phases (ms): {phases}");
        }
        Prof.Add("s.idle", Physics.ProfIdle); // PROF
        Physics.ProfIdle = 0; // PROF
        _steps++;

        Prof.Time("s.gather", GatherContacts);
        Prof.Time("s.impacts", ReceiveImpacts);
        Prof.Time("s.recvStress", ReceiveStress);
        Prof.Time("s.stressDmg", ApplyStressDamage);
        Prof.Time("s.playerStress", ApplyPlayerStress);
        Prof.Time("s.sendStress", SendStressInputs);
        Prof.Time("s.mapStress", UpdateMapStress);
        UpdateHazards(Step);
        RespawnFallenPlayer();
        Prof.Time("s.items", UpdateItems);
        Prof.Time("s.particles", () => Particles.Update(this, Step));
    }

    /// <summary>A player that falls out of the world dies and returns to its spawn point.</summary>
    private void RespawnFallenPlayer()
    {
        if (Player != null && Player.Feet.z < PlayerVoidHeight)
        {
            DamagePlayer(double.PositiveInfinity, "falling out of the world");
            Player.MoveTo(Player.Spawn);
        }
    }

    /// <summary>Whether a body is still in the world.</summary>
    private bool IsPresent(CellBody body)
    {
        return body switch
        {
            VoxelMap => true,
            VoxelObject moving => HasObject(moving),
            _ => body == Player
        };
    }

    private bool HasObject(VoxelObject body) => _objectsById.GetValueOrDefault(body.Id) == body;

    // ---------------------------------------------------------------------------------------
    // Following the player around a streamed map
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Keeps the simulation around the player: parks the objects farther from it than the
    /// simulation distance and brings back those nearer (<see cref="ParkObjects"/>), gives the
    /// map colliders around the player and the objects that move (<see cref="UpdateColliders"/>),
    /// and moves the map's stress analysis on once the player strays far enough from its middle.
    /// </summary>
    private void FollowThePlayer()
    {
        if (Player == null)
            return;

        ParkObjects();
        ParkItems();
        bool changed = UpdateColliders();
        if (!WindowMustMove())
        {
            _windowWaits = 0;
            return;
        }

        // A move and the physics rebuild that changed colliders bring cost a few milliseconds
        // each; on the same step, they would add up. The move waits a few steps at most.
        if (changed && _window != null && _windowWaits < MaxWindowWaits)
        {
            _windowWaits++;
            return;
        }

        _windowWaits = 0;
        // Spread window preparation and GPU resource replacement over successive steps.
        // Stress solves already run asynchronously; preserve the topology generation gates.
        _deferMapStress = _window != null;
        dvec3 feet = Player.Feet;
        ivec3 center = new((int)Math.Floor(feet.x), (int)Math.Floor(feet.y), (int)Math.Floor(feet.z));
        _window = (center - new ivec3(WindowHalfWidth, WindowHalfWidth, WindowBelow), center + new ivec3(WindowHalfWidth, WindowHalfWidth, WindowAbove));
        Map.TopologyVersion++;
        PostTopology(Map);
    }

    /// <summary>Whether the player has strayed far enough from the middle of the stress analysis' box for it to move, or there is none yet.</summary>
    private bool WindowMustMove()
    {
        if (_window is not (ivec3 low, ivec3 high))
            return true;

        dvec3 feet = Player!.Feet;
        dvec3 middle = 0.5 * new dvec3(low + high) + new dvec3(0, 0, 0.5 * (WindowBelow - WindowAbove));
        return Math.Max(Math.Abs(feet.x - middle.x), Math.Abs(feet.y - middle.y)) > WindowSlack || Math.Abs(feet.z - middle.z) > WindowSlack;
    }

    /// <summary>
    /// Gives colliders to the map chunks around the bodies that move, the player and the
    /// objects not parked: those baked ahead where they are ready, the others built now. Drops
    /// the colliders no body comes near any more, keeping them for a while in case one comes
    /// back, and has those of the chunks a little farther out baked ahead. Returns whether any
    /// collider came or went, which rebuilds the physics.
    /// </summary>
    private bool UpdateColliders()
    {
        _neededCoverage.Clear();
        _keptCoverage.Clear();
        foreach (CellBody body in AwakeBodies)
            Cover(BoundsOf(body), body.LinearVelocity);
        foreach (DroppedItem item in _items)
        {
            if (item.Body != null)
                Cover(item.Bounds, item.Body.LinearVelocity);
        }

        bool changed = false;
        _droppedColliders.Clear();
        foreach (ivec3 chunk in Map.Colliders.Keys)
        {
            if (!_keptCoverage.Chunks.Contains(chunk))
                _droppedColliders.Add(chunk);
        }

        foreach (ivec3 chunk in _droppedColliders)
        {
            _baker.Keep(chunk, Map.Colliders[chunk]);
            RemoveCollider(Map, chunk);
            changed = true;
        }

        foreach (ivec3 chunk in _neededCoverage.Chunks)
        {
            if (Map.Colliders.ContainsKey(chunk) || _faceless.Contains(chunk) || !Map.IsLoaded(VoxelMap.ColumnOfChunk(chunk)) || !Map.Grid.TryGetChunk(chunk, out _))
                continue;

            if (!_baker.TryTake(chunk, out FixedBody? baked))
                changed |= RebuildCollider(Map, chunk);
            else if (baked != null)
                changed |= AddCollider(Map, chunk, baked);
            else
                _faceless.Add(chunk);
        }

        _faceless.RemoveWhere(chunk => !_keptCoverage.Chunks.Contains(chunk));
        _baker.KeepOnly(_keptCoverage.Chunks);
        _baker.Prepare(Map, _keptCoverage.Chunks, _faceless);
        return changed;
    }

    /// <summary>Has the map chunks around a body that moves given colliders, and those a little farther out kept or baked ahead.</summary>
    private void Cover((dvec3 Low, dvec3 High) bounds, dvec3 velocity)
    {
        double reach = ColliderReach + velocity.Length * ColliderLookahead;
        _neededCoverage.Add(bounds.Low - reach, bounds.High + reach);
        _keptCoverage.Add(bounds.Low - (reach + ColliderKeep), bounds.High + (reach + ColliderKeep));
    }

    /// <summary>The bodies that move: the player and the objects in the physics.</summary>
    private IEnumerable<CellBody> AwakeBodies
    {
        get
        {
            if (Player != null)
                yield return Player;
            foreach (VoxelObject body in _objects)
            {
                if (body.Body != null)
                    yield return body;
            }
        }
    }

    /// <summary>A box around a body in the world, m.</summary>
    private static (dvec3 Low, dvec3 High) BoundsOf(CellBody body)
    {
        if (body is VoxelObject voxels)
        {
            dvec3 center = voxels.ToWorld(voxels.Bounds.Center);
            return (center - voxels.Bounds.Radius, center + voxels.Bounds.Radius);
        }

        return (body.Position, body.Position + new dvec3(Player.Width, Player.Width, ((Player)body).CurrentHeight));
    }

    /// <summary>Whether the map columns a body's bounds reach into, widened by <paramref name="margin"/> (m), are all loaded.</summary>
    private bool GroundLoaded(CellBody body, double margin)
    {
        return GroundLoaded(BoundsOf(body), margin);
    }

    /// <summary>Whether the map columns a box (m) reaches into, widened by <paramref name="margin"/> (m), are all loaded.</summary>
    private bool GroundLoaded((dvec3 Low, dvec3 High) bounds, double margin)
    {
        (dvec3 low, dvec3 high) = bounds;
        ivec2 first = ChunkStreamer.ColumnAt(low - margin);
        ivec2 last = ChunkStreamer.ColumnAt(high + margin);
        for (int x = first.x; x <= last.x; x++)
        for (int y = first.y; y <= last.y; y++)
        {
            if (!Map.IsLoaded(new ivec2(x, y)))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether an object is to be simulated: always, unless the simulation follows the player;
    /// then while it is within the simulation distance of the player, less
    /// <paramref name="margin"/> (m), and the ground around it is loaded.
    /// </summary>
    private bool MaySimulate(VoxelObject body, double margin)
    {
        if (!FollowPlayer)
            return true;
        if (Player == null)
            return false;

        dvec3 away = body.ToWorld(body.Bounds.Center) - Player.Feet;
        double reach = SimulationDistance * Grid3D<Voxel>.ChunkSize * VoxelBody.CellSize - margin;
        return away.x * away.x + away.y * away.y <= reach * reach && GroundLoaded(body, ColliderReach);
    }

    /// <summary>Parks the objects that go beyond the simulation distance, and brings back the parked ones well within it.</summary>
    private void ParkObjects()
    {
        foreach (VoxelObject body in _objects)
        {
            if (!body.Parked && body.Body != null && !MaySimulate(body, 0.0))
                Park(body);
            else if (body.Parked && MaySimulate(body, UnparkMargin))
                Unpark(body);
        }
    }

    /// <summary>Takes an object out of the physics where it is, keeping its motion for when it comes back.</summary>
    private void Park(VoxelObject body)
    {
        RigidBody rigid = body.Body!;
        body.PendingPosition = rigid.Position;
        body.PendingOrientation = rigid.Orientation;
        body.PendingLinearVelocity = rigid.LinearVelocity;
        body.PendingAngularVelocity = rigid.AngularVelocity;
        Physics.Remove(rigid);
        _owners.Remove(rigid);
        body.Body = null;
        body.Parked = true;
    }

    private void Unpark(VoxelObject body)
    {
        body.Parked = false;
        RebuildRigidBody(body);
    }

    /// <summary>
    /// Whether a map chunk overlaps the box the map's stress analysis covers: the whole map,
    /// unless the simulation follows the player.
    /// </summary>
    private bool TouchesWindow(ivec3 chunk)
    {
        if (!FollowPlayer)
            return true;
        if (_window is not (ivec3 low, ivec3 high))
            return false;

        ivec3 first = chunk * Grid3D<Voxel>.ChunkSize;
        ivec3 last = first + Grid3D<Voxel>.ChunkSize;
        return first.x < high.x && first.y < high.y && first.z < high.z && last.x > low.x && last.y > low.y && last.z > low.z;
    }

    /// <summary>
    /// Whether a map cell lies near a side of the stress analysis' box, where the box holds it
    /// in place though the map beyond may not. Never under the bedrock, which holds still.
    /// </summary>
    private bool NearWindowSide(ivec3 cell)
    {
        if (!FollowPlayer || _window is not (ivec3 low, ivec3 high))
            return false;

        ivec3 below = cell - low;
        ivec3 above = high - 1 - cell;
        int nearest = Math.Min(Math.Min(below.x, below.y), Math.Min(Math.Min(above.x, above.y), above.z));
        if (low.z > 0)
            nearest = Math.Min(nearest, below.z);

        return nearest < WindowBorder;
    }

    // ---------------------------------------------------------------------------------------
    // Contacts: impact damage and loads
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sorts the step's contacts: those closing slowly are sustained loads for the stress
    /// analysis; those closing fast are impacts, gathered per pair of bodies and sent to the
    /// impact analysis once the pair stops. The normal impulse distribution determines their
    /// compression energy, independently of where in the timestep they hit.
    /// </summary>
    private void GatherContacts()
    {
        // Resolve each body's report-space transform once; contacts refer to the start
        // of the step, even when its final pose has already been read back.
        _contactPoses.Clear();
        _contactBodies.Clear();
        int bodyCount = _objects.Count + (Player == null ? 1 : 2);
        if (_contactPosesByIndex.Length < bodyCount)
            Array.Resize(ref _contactPosesByIndex, Math.Max(bodyCount, _contactPosesByIndex.Length * 2));
        foreach (CellBody body in CellBodies)
        {
            body.StepLoads.Clear();
            (dvec3 position, dquat orientation) = StepStartPose(body);
            // Bounds can be stale between edits and cooking. Verify the candidate against
            // live occupancy before using the exact singleton surface lookup.
            ivec3 single = body is VoxelObject obj ? (ivec3)dvec3.Floor(obj.Bounds.Center / body.CellEdge) : default;
            bool singleton = body is VoxelObject voxel && voxel.Grid.Count == 1 && voxel.Grid.IsOccupied(single);
            ContactPose pose = new(position, orientation, orientation.Conjugate, _contactBodies.Count, singleton, single);
            _contactPoses[body] = pose;
            _contactPosesByIndex[pose.Index] = pose;
            _contactBodies.Add(body);
        }
        _contactOwnerIndices.Clear();
        foreach ((PhysicsBody physicsBody, CellBody body) in _owners)
            _contactOwnerIndices[physicsBody] = _contactPoses[body].Index;

        foreach ((CellBody body, ivec3 cell, dvec3 force, dvec3 inward) in _pushes)
            AddLoad(body, _contactPoses[body], cell, force, inward);

        long mapStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        int count = Physics.ContactReports.Count;
        if (_locatedContacts.Length < count)
            Array.Resize(ref _locatedContacts, Math.Max(count, Math.Max(256, _locatedContacts.Length * 2)));
        // Grid occupancy and start poses remain immutable until mapping finishes. Workers
        // write disjoint slots; all effects below retain the original report order.
        if (ParallelContactMapping && count >= 512 && Environment.ProcessorCount > 1)
            Parallel.For(0, (count + 255) / 256, _contactParallelOptions, batch =>
            {
                int end = Math.Min(count, (batch + 1) * 256);
                for (int i = batch * 256; i < end; i++)
                    _locatedContacts[i] = LocateContact(Physics.ContactReports[i]);
            });
        else
            for (int i = 0; i < count; i++)
                _locatedContacts[i] = LocateContact(Physics.ContactReports[i]);

        if (Prof.On)
            Prof.Add("g.map", Stopwatch.GetElapsedTime(mapStart).TotalMilliseconds);
        long reduceStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        bool parallelLoads = ParallelContactReduction && count >= 512 && _contactBodies.Count >= 32 && Environment.ProcessorCount > 1;
        if (parallelLoads)
            ReduceContactLoads(count);
        _supports.Clear();
        for (int i = 0; i < count; i++)
        {
            ContactReport contact = Physics.ContactReports[i];
            NoteItemSupport(contact);
            LocatedContact located = _locatedContacts[i];
            CellBody? a = located.IndexA < 0 ? null : _contactBodies[located.IndexA];
            CellBody? b = located.IndexB < 0 ? null : _contactBodies[located.IndexB];
            bool hitA = located.HitA, hitB = located.HitB;
            ivec3 cellA = located.CellA, cellB = located.CellB;
            if (a == null && b == null)
                continue;
            if (hitA && hitB)
            {
                if (a == Player)
                    NoteSupport(b!, cellB, contact, contact.Normal);
                else if (b == Player)
                    NoteSupport(a!, cellA, contact, -contact.Normal);
            }

            dvec3 forceOnA = located.WorldForce;

            // The force of an impact lasts the step only; the impact analysis deals with it.
            if (contact.ApproachSpeed < MinimumImpactSpeed)
            {
                if (!parallelLoads && hitA)
                    AccumulateLoad(a!, cellA, located.ForceA, located.MomentA);
                if (!parallelLoads && hitB)
                    AccumulateLoad(b!, cellB, located.ForceB, located.MomentB);
                continue;
            }

            if (!(hitA || hitB))
                continue;

            int idA = a?.Id ?? -1;
            int idB = b?.Id ?? -1;
            (int, int) pair = (Math.Min(idA, idB), Math.Max(idA, idB));
            if (!_openImpacts.TryGetValue(pair, out OpenImpact? impact))
                _openImpacts[pair] = impact = new OpenImpact(_steps);

            impact.LastStep = _steps;
            dvec3 normalImpulse = contact.NormalImpulse * contact.Normal;
            AddImpactMotion(impact, a, normalImpulse, contact.Point);
            AddImpactMotion(impact, b, -normalImpulse, contact.Point);
            double area = ImpactLoading.AreaFraction(
                a == null ? dvec3.UnitX : _contactPoses[a].Inverse * contact.Normal,
                b == null ? dvec3.UnitX : _contactPoses[b].Inverse * -contact.Normal);
            if (hitA)
                AddImpactContact(impact, a!, _contactPoses[a!], cellA, forceOnA, contact.Normal, contact.Point, area);
            if (hitB)
                AddImpactContact(impact, b!, _contactPoses[b!], cellB, -forceOnA, -contact.Normal, contact.Point, area);
        }

        if (Prof.On)
            Prof.Add("g.reduce", Stopwatch.GetElapsedTime(reduceStart).TotalMilliseconds);
        _endedImpacts.Clear();
        foreach (((int, int) pair, OpenImpact impact) in _openImpacts)
        {
            if (impact.LastStep == _steps && _steps - impact.FirstStep + 1 < MaxImpactSteps)
                continue;
            _endedImpacts.Add(pair);
        }
        foreach ((int, int) pair in _endedImpacts)
        {
            _openImpacts.Remove(pair, out OpenImpact? impact);
            Prof.Time("g.postImpact", () => PostImpact(impact!)); // PROF
        }

        if (Player != null)
            Player.Grounded = _supports.Count > 0;
        Prof.Time("g.smooth", SmoothLoads); // PROF
    }

    // Stable counting scatter gives each body an ordered slice of sustained contact sides.
    // Each dictionary has exactly one writer; cells see the same additions as the serial fold.
    private void ReduceContactLoads(int count)
    {
        long scatterStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        int bodies = _contactBodies.Count;
        if (_loadStarts.Length < bodies + 1)
        {
            int capacity = Math.Max(bodies + 1, _loadStarts.Length * 2);
            _loadStarts = new int[capacity];
            _loadCursors = new int[capacity];
        }
        if (_loadRefs.Length < 2 * count)
            _loadRefs = new int[Math.Max(2 * count, _loadRefs.Length * 2)];
        Array.Clear(_loadStarts, 0, bodies + 1);
        for (int i = 0; i < count; i++)
        {
            if (!(Physics.ContactReports[i].ApproachSpeed < MinimumImpactSpeed))
                continue;
            ref readonly LocatedContact located = ref _locatedContacts[i];
            if (located.HitA)
                _loadStarts[located.IndexA + 1]++;
            if (located.HitB)
                _loadStarts[located.IndexB + 1]++;
        }
        for (int i = 0; i < bodies; i++)
        {
            _loadStarts[i + 1] += _loadStarts[i];
            _loadCursors[i] = _loadStarts[i];
        }
        for (int i = 0; i < count; i++)
        {
            if (!(Physics.ContactReports[i].ApproachSpeed < MinimumImpactSpeed))
                continue;
            ref readonly LocatedContact located = ref _locatedContacts[i];
            if (located.HitA)
                _loadRefs[_loadCursors[located.IndexA]++] = i * 2;
            if (located.HitB)
                _loadRefs[_loadCursors[located.IndexB]++] = i * 2 + 1;
        }
        if (Prof.On)
            Prof.Add("g.scatter", Stopwatch.GetElapsedTime(scatterStart).TotalMilliseconds);
        long foldStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        Parallel.For(0, (bodies + 15) / 16, _contactParallelOptions, batch =>
        {
            int end = Math.Min(bodies, (batch + 1) * 16);
            for (int body = batch * 16; body < end; body++)
                for (int i = _loadStarts[body]; i < _loadStarts[body + 1]; i++)
                {
                    int side = _loadRefs[i];
                    ref readonly LocatedContact located = ref _locatedContacts[side / 2];
                    if ((side & 1) == 0)
                        AccumulateLoad(_contactBodies[body], located.CellA, located.ForceA, located.MomentA);
                    else
                        AccumulateLoad(_contactBodies[body], located.CellB, located.ForceB, located.MomentB);
                }
        });
        if (Prof.On)
            Prof.Add("g.fold", Stopwatch.GetElapsedTime(foldStart).TotalMilliseconds);
    }

    /// <summary>Checks mapping, owner indices and ordered loads against the serial reference.</summary>
    internal int CheckContactMapping()
    {
        int count = Physics.ContactReports.Count;
        LocatedContact[] serial = new LocatedContact[count];
        int mismatches = 0;
        // The reference uses the original occupancy search even for singleton bodies.
        for (int i = 0; i < _contactBodies.Count; i++)
            _contactPosesByIndex[i] = _contactPosesByIndex[i] with { SingleCell = false };
        for (int i = 0; i < count; i++)
        {
            serial[i] = LocateContact(Physics.ContactReports[i]);
            CellBody? a = serial[i].IndexA < 0 ? null : _contactBodies[serial[i].IndexA];
            CellBody? b = serial[i].IndexB < 0 ? null : _contactBodies[serial[i].IndexB];
            if (a != _owners.GetValueOrDefault(Physics.ContactReports[i].BodyA)
                || b != _owners.GetValueOrDefault(Physics.ContactReports[i].BodyB))
                mismatches++;
        }
        for (int i = 0; i < _contactBodies.Count; i++)
            _contactPosesByIndex[i] = _contactPoses[_contactBodies[i]];
        Parallel.For(0, count, _contactParallelOptions, i =>
        {
            if (serial[i] != LocateContact(Physics.ContactReports[i]))
                Interlocked.Increment(ref mismatches);
        });
        Dictionary<ivec3, (dvec3 Force, dvec3 Moment)>[] reference =
            [.. _contactBodies.Select(_ => new Dictionary<ivec3, (dvec3 Force, dvec3 Moment)>())];
        foreach (var push in _pushes)
        {
            ContactPose pose = _contactPoses[push.Body];
            dvec3 force = pose.Inverse * push.Force;
            Add(pose.Index, push.Cell, force, FaceMoment(force, pose.Inverse * push.Inward, push.Body.CellEdge));
        }
        for (int i = 0; i < count; i++)
        {
            if (!(Physics.ContactReports[i].ApproachSpeed < MinimumImpactSpeed))
                continue;
            ref readonly LocatedContact located = ref serial[i];
            if (located.HitA)
                Add(located.IndexA, located.CellA, located.ForceA, located.MomentA);
            if (located.HitB)
                Add(located.IndexB, located.CellB, located.ForceB, located.MomentB);
        }
        for (int i = 0; i < reference.Length; i++)
            if (!reference[i].SequenceEqual(_contactBodies[i].StepLoads))
                mismatches++;
        return mismatches;

        void Add(int body, ivec3 cell, dvec3 force, dvec3 moment)
        {
            ref (dvec3 Force, dvec3 Moment) load = ref CollectionsMarshal.GetValueRefOrAddDefault(reference[body], cell, out _);
            load = (load.Force + force, load.Moment + moment);
        }
    }

    private LocatedContact LocateContact(in ContactReport contact)
    {
        int indexA = _contactOwnerIndices.GetValueOrDefault(contact.BodyA, -1);
        int indexB = _contactOwnerIndices.GetValueOrDefault(contact.BodyB, -1);
        CellBody? a = indexA < 0 ? null : _contactBodies[indexA];
        CellBody? b = indexB < 0 ? null : _contactBodies[indexB];
        ContactPose poseA = indexA < 0 ? default : _contactPosesByIndex[indexA];
        ContactPose poseB = indexB < 0 ? default : _contactPosesByIndex[indexB];
        // Dropped items cannot break and break nothing: their contacts press on no cell.
        bool item = IsItem(contact.BodyA) || IsItem(contact.BodyB);
        ivec3 cellA = default, cellB = default;
        dvec3 normalA = poseA.Inverse * contact.Normal;
        dvec3 normalB = poseB.Inverse * -contact.Normal;
        bool hitA = !item && TryLocate(a, poseA, contact.Point, normalA, out cellA);
        bool hitB = !item && TryLocate(b, poseB, contact.Point, normalB, out cellB);
        if (hitA && hitB)
        {
            cellA = Facing(a!, poseA, cellA, contact.Point, contact.Normal, normalA, poseB.Position + poseB.Orientation * b!.CenterOf(cellB));
            cellB = Facing(b!, poseB, cellB, contact.Point, -contact.Normal, normalB, poseA.Position + poseA.Orientation * a!.CenterOf(cellA));
        }
        dvec3 worldForce = (contact.NormalImpulse * contact.Normal + contact.FrictionImpulse) / Step;
        dvec3 forceA = default, momentA = default, forceB = default, momentB = default;
        if (contact.ApproachSpeed < MinimumImpactSpeed)
        {
            if (hitA)
            {
                forceA = poseA.Inverse * worldForce;
                momentA = FaceMoment(forceA, normalA, a!.CellEdge);
            }
            if (hitB)
            {
                forceB = poseB.Inverse * -worldForce;
                momentB = FaceMoment(forceB, normalB, b!.CellEdge);
            }
        }
        return new LocatedContact(indexA, indexB, hitA, hitB, cellA, cellB, worldForce, forceA, momentA, forceB, momentB);
    }

    /// <summary>The cell a contact point presses on, with the body at its pose when the step began.</summary>
    private static bool TryLocate(CellBody? body, in ContactPose pose, dvec3 worldPoint, dvec3 localInward, out ivec3 cell)
    {
        cell = default;
        if (body == null)
            return false;

        dvec3 localPoint = pose.Inverse * (worldPoint - pose.Position) / body.CellEdge;
        return pose.SingleCell
            ? VoxelBody.TryFindSingleSurfaceCell(pose.Cell, localPoint, localInward, out cell)
            : body.TryFindSurfaceCell(localPoint, localInward, out cell);
    }

    /// <summary>
    /// Of the cells around a contact point, the one facing <paramref name="otherCenter"/>, the
    /// center of the cell the other side touches with: looked for a little toward it across the
    /// contact plane. <paramref name="found"/> if there is none there.
    /// </summary>
    private static ivec3 Facing(CellBody body, in ContactPose pose, ivec3 found, dvec3 worldPoint, dvec3 inward, dvec3 localInward, dvec3 otherCenter)
    {
        dvec3 across = otherCenter - worldPoint;
        across -= dvec3.Dot(across, inward) * inward;
        double length = across.Length;
        if (length < 1e-6)
            return found;

        dvec3 nudged = worldPoint + Math.Min(0.5 * length, 0.25 * body.CellEdge) / length * across;
        return TryLocate(body, pose, nudged, localInward, out ivec3 cell) ? cell : found;
    }

    private (dvec3 Position, dquat Orientation) StepStartPose(CellBody body)
    {
        return _stepStart.TryGetValue(body, out StepStart start)
            ? (start.Position, start.Orientation) : (body.Position, body.Orientation);
    }

    private dvec3 StepStartPosition(CellBody body)
    {
        return _stepStart.TryGetValue(body, out StepStart start) ? start.Position : body.Position;
    }

    /// <summary>
    /// The moment about a cell's center of a force on the face it is pressed through. Where on
    /// the face is lost: the contact points are mesh corners, each shared by several cells
    /// and given to one of them, so only the face's center is meaningful.
    /// </summary>
    private static dvec3 FaceMoment(dvec3 force, dvec3 inward, double cellEdge)
    {
        return dvec3.Cross(-0.5 * cellEdge * inward, force);
    }

    private static void AddLoad(CellBody body, in ContactPose pose, ivec3 cell, dvec3 worldForce, dvec3 worldInward)
    {
        dquat toBody = pose.Inverse;
        dvec3 force = toBody * worldForce;
        dvec3 moment = FaceMoment(force, toBody * worldInward, body.CellEdge);
        AccumulateLoad(body, cell, force, moment);
    }

    private static void AccumulateLoad(CellBody body, ivec3 cell, dvec3 force, dvec3 moment)
    {
        ref (dvec3 Force, dvec3 Moment) load = ref CollectionsMarshal.GetValueRefOrAddDefault(body.StepLoads, cell, out _);
        load = (load.Force + force, load.Moment + moment);
    }

    private void AddImpactContact(OpenImpact impact, CellBody body, in ContactPose pose, ivec3 cell, dvec3 worldForce, dvec3 worldInward, dvec3 worldPoint, double areaFraction)
    {
        if (!impact.Sides.TryGetValue(body, out Dictionary<ivec3, ImpactCell>? cells))
            impact.Sides[body] = cells = [];
        if (!cells.TryGetValue(cell, out ImpactCell? pressed))
            cells[cell] = pressed = new ImpactCell();

        dquat toBody = pose.Inverse;
        pressed.Impulse += toBody * worldForce;
        pressed.Inward += toBody * worldInward;
        double weight = worldForce.Length;
        pressed.PointSum += weight * (pose.Inverse * (worldPoint - pose.Position));
        pressed.PointWeight += weight;
        pressed.AreaSum += weight * areaFraction;
    }

    private void AddImpactMotion(OpenImpact impact, CellBody? body, dvec3 impulse, dvec3 point)
    {
        if (body?.Rigid is not RigidBody rigid || !_stepStart.TryGetValue(body, out StepStart start))
            return;
        if (!impact.Motion.TryGetValue(body, out ImpactMotion? motion))
        {
            dmat3 rotation = start.Orientation.ToMat3;
            dmat3 inverseInertia = rigid.FixedRotation ? dmat3.Zero : rotation * rigid.LocalInertia.Inverse * rotation.Transposed;
            impact.Motion[body] = motion = new ImpactMotion(rigid.Mass, inverseInertia, start.LinearVelocity, start.AngularVelocity);
            impact.Before[body] = (start.LinearVelocity, start.AngularVelocity);
        }
        motion.Impulse += impulse;
        dvec3 center = start.Position + start.Orientation * rigid.CenterOfMass;
        motion.AngularImpulse += dvec3.Cross(point - center, impulse);
    }

    /// <summary>
    /// Low-pass filters each cell's contact load, so the stress analysis sees what a body
    /// sustains rather than the spikes of single impacts, whose damage the impact model
    /// already accounts for at the surface.
    /// </summary>
    private void SmoothLoads()
    {
        double alpha = Step / (LoadSmoothing + Step);
        foreach (CellBody body in CellBodies)
        {
            _loadCells.Clear();
            _loadCells.AddRange(body.ContactLoads.Keys);
            foreach (ivec3 cell in body.StepLoads.Keys)
            {
                if (!body.ContactLoads.ContainsKey(cell))
                    _loadCells.Add(cell);
            }
            foreach (ivec3 cell in _loadCells)
            {
                (dvec3 force, dvec3 moment) = body.ContactLoads.GetValueOrDefault(cell);
                (dvec3 stepForce, dvec3 stepMoment) = body.StepLoads.GetValueOrDefault(cell);
                force += alpha * (stepForce - force);
                moment += alpha * (stepMoment - moment);

                if (force.LengthSqr < 1.0 && !body.StepLoads.ContainsKey(cell))
                    body.ContactLoads.Remove(cell);
                else
                    body.ContactLoads[cell] = (force, moment);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Impacts
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sends an impact that has ended to the impact analysis, unless it is too light to matter
    /// or too many are waiting already, with what it took from each moving body's motion: the
    /// change of velocity the contacts made, gravity aside. Its energy is capped at what a
    /// collision of the bodies could take (<see cref="CollisionEnergy"/>).
    /// </summary>
    private void PostImpact(OpenImpact impact)
    {
        impact.Energy = Prof.Time("pi.energy", () => Math.Min(ImpactKinematics.CompressionEnergy(impact.Motion.Values), CollisionEnergy(impact))); // PROF
        if (impact.Energy < MinimumImpactEnergy || _pendingImpacts.Count >= MaxPendingImpacts)
            return;

        CellBody[] bodies = [.. impact.Sides.Keys.Where(IsPresent)];
        ImpactSide[] sides = Prof.Time("pi.describe", () => // PROF
        (ImpactSide[])[
            .. bodies
                .Select(body => DescribeImpactSide(body, impact.Sides[body], (cell, inward) => ClosingSpeed(impact, body, cell, inward)))
                .Where(side => side.Contacts.Length > 0)
        ]);
        if (sides.Length == 0)
            return;

        double elapsed = (_steps - impact.FirstStep + 1) * Step;
        Dictionary<CellBody, ImpactLoss> lost = [];
        foreach ((CellBody moving, (dvec3 linear, dvec3 angular)) in impact.Before)
        {
            dvec3 gravity = moving is Player ? PlayerGravity : Physics.Settings.Gravity;
            if (IsPresent(moving))
                lost[moving] = new ImpactLoss(linear + elapsed * gravity - moving.LinearVelocity, angular - moving.AngularVelocity, moving.Position, linear);
        }

        int id = _nextImpactId++;
        Dictionary<CellBody, Dictionary<ivec3, BlockDamageSite>> sites = [];
        foreach (CellBody body in bodies)
        {
            sites[body] = impact.Sides[body].Where(c => c.Value.PointWeight > 0.0).ToDictionary(
                c => c.Key, c => new BlockDamageSite(c.Value.PointSum / c.Value.PointWeight, -c.Value.Inward.Normalized));
        }
        _pendingImpacts[id] = new PendingImpact(bodies, lost, sites, _steps, impact.Energy);
        _impactWorker.Post(new ImpactJob(id, impact.Energy, sides));
    }

    /// <summary>
    /// The most kinetic energy (J) a collision of an impact's bodies could take, from their
    /// motion when it began: all of it against the map, which holds still, but between two
    /// moving bodies only what they have in their center-of-mass frame, since their total
    /// momentum carries on. This remains a final energy bound when topology changes during
    /// the impact or contact data are incomplete.
    /// </summary>
    private static double CollisionEnergy(OpenImpact impact)
    {
        double total = impact.Before.Sum(b => b.Key.KineticEnergy(b.Value.Linear, b.Value.Angular));
        if (impact.Before.Count < 2 || impact.Sides.Keys.Any(b => b.Rigid == null))
            return total;

        double mass = 0.0;
        dvec3 momentum = dvec3.Zero;
        foreach ((CellBody body, (dvec3 linear, _)) in impact.Before)
        {
            mass += body.Rigid!.Mass;
            momentum += body.Rigid.Mass * linear;
        }

        return Math.Max(0.0, total - 0.5 * momentum.LengthSqr / mass);
    }

    /// <summary>
    /// How fast the other body of an impact was closing on a pressed cell's face when the
    /// impact began, from both bodies' rigid motion then.
    /// </summary>
    private static double ClosingSpeed(OpenImpact impact, CellBody body, ivec3 cell, dvec3 inward)
    {
        CellBody? other = impact.Sides.Keys.FirstOrDefault(b => b != body);
        dvec3 face = body.ToWorld(body.CenterOf(cell) - 0.5 * body.CellEdge * inward);
        return dvec3.Dot(VelocityBefore(impact, other, face) - VelocityBefore(impact, body, face), body.Orientation * inward);
    }

    private static dvec3 VelocityBefore(OpenImpact impact, CellBody? body, dvec3 point)
    {
        if (body?.Rigid is not RigidBody rigid || !impact.Before.TryGetValue(body, out (dvec3 Linear, dvec3 Angular) before))
            return dvec3.Zero;

        return before.Linear + dvec3.Cross(before.Angular, point - rigid.WorldCenterOfMass);
    }

    /// <summary>
    /// The cells of a body an impact analysis looks at: all of an object's or the player's, or
    /// the map's around the contacts, with each cell's moduli, limits, sustained stress and
    /// remaining health.
    /// </summary>
    /// <param name="closing">How fast the impact closed on a pressed cell, given its inward normal.</param>
    private static ImpactSide DescribeImpactSide(CellBody body, Dictionary<ivec3, ImpactCell> contacts, Func<ivec3, dvec3, double> closing)
    {
        List<ivec3> cells = [];
        List<bool> anchored = [];
        bool localRegion = body is VoxelBody voxels && (body is VoxelMap || voxels.Grid.Count > MaxImpactRegion);
        if (localRegion)
        {
            CollectRegion((VoxelBody)body, contacts.Keys, cells, anchored);
        }
        else
        {
            foreach (ivec3 cell in body.OccupiedCells())
            {
                cells.Add(cell);
                anchored.Add(false);
            }
        }

        long profA = Stopwatch.GetTimestamp(); // PROF
        int count = cells.Count;
        CellMaterial[] materials = new CellMaterial[count];
        double[] masses = new double[count];
        ElasticLimits[] limits = new ElasticLimits[count];
        CellStress[] sustained = new CellStress[count];
        double[] toBreak = new double[count];
        bool[] granular = new bool[count];
        Dictionary<ivec3, int> index = new(count);
        double volume = body.CellEdge * body.CellEdge * body.CellEdge;

        for (int i = 0; i < count; i++)
        {
            BlockMaterial material = body.MaterialAt(cells[i]);
            double health = body.HealthAt(cells[i]);
            materials[i] = material.Elastic(health);
            masses[i] = material.Density * volume;
            limits[i] = material.ElasticLimit(health);
            sustained[i] = body.StaticStress(cells[i]);
            toBreak[i] = material.DamageToBreak(health);
            granular[i] = material.Granular;
            index[cells[i]] = i;
        }

        Prof.Add($"d.cells{count}{body.GetType().Name}", Stopwatch.GetElapsedTime(profA).TotalMilliseconds); // PROF
        profA = Stopwatch.GetTimestamp(); // PROF
        // How the solver shares an impulse between contacts is arbitrary: any split with the right
        // total stops the bodies. Elastic bodies press hardest where they close fastest, so each
        // cell's force follows its closing speed, along the impulse it took. A cell only touched
        // as the bodies turned during the impact keeps a little of the fastest one's.
        List<(int Cell, dvec3 Direction, dvec3 Inward, double Closing, double Area)> pressed = [];
        foreach ((ivec3 cell, ImpactCell contact) in contacts)
        {
            if (index.TryGetValue(cell, out int i) && contact.Impulse.LengthSqr > 0.0)
            {
                dvec3 inward = contact.Inward.Normalized;
                pressed.Add((i, contact.Impulse.Normalized, inward, closing(cell, inward),
                    contact.PointWeight > 0.0 ? contact.AreaSum / contact.PointWeight : 1.0));
            }
        }

        double fastest = pressed.Count > 0 ? pressed.Max(p => p.Closing) : 0.0;
        ImpactContact[] pressing =
        [
            .. pressed.Select(p =>
            {
                dvec3 force = Math.Max(p.Closing, 0.1 * fastest) * p.Direction;
                return new ImpactContact(p.Cell, force, FaceMoment(force, p.Inward, body.CellEdge), p.Inward, p.Area);
            })
        ];
        pressing = ImpactLoading.Normalize(pressing);

        Prof.Add($"d.pressed{pressed.Count}", Stopwatch.GetElapsedTime(profA).TotalMilliseconds); // PROF
        return new ImpactSide(body.Id, body.CellEdge, [.. cells], materials, masses, [.. anchored], body.Rigid != null && !localRegion, limits, sustained, toBreak, granular, pressing);
    }

    /// <summary>
    /// A bounded neighbourhood around impact contacts. Map bedrock and the cut boundary
    /// are held by the surrounding body; a short contact need not solve an entire mountain.
    /// </summary>
    private static void CollectRegion(VoxelBody map, IEnumerable<ivec3> seeds, List<ivec3> cells, List<bool> anchored)
    {
        long profStart = Stopwatch.GetTimestamp(); // PROF
        HashSet<ivec3> included = [];
        Queue<ivec3> open = new();
        foreach (ivec3 seed in seeds)
        {
            if (cells.Count == MaxImpactRegion) break;
            if (Blocks.IsSolid(map.Grid[seed].Type) && included.Add(seed))
            {
                cells.Add(seed);
                open.Enqueue(seed);
            }
        }

        while (open.Count > 0 && cells.Count < MaxImpactRegion)
        {
            ivec3 cell = open.Dequeue();
            if (map is VoxelMap && VoxelMap.IsAnchor(map.Grid[cell]))
                continue;

            foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
            {
                ivec3 next = cell + direction;
                if (cells.Count < MaxImpactRegion && Blocks.IsSolid(map.Grid[next].Type) && included.Add(next))
                {
                    cells.Add(next);
                    open.Enqueue(next);
                }
            }
        }

        foreach (ivec3 cell in cells)
        {
            bool held = map is VoxelMap && VoxelMap.IsAnchor(map.Grid[cell]);
            foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
                held |= Blocks.IsSolid(map.Grid[cell + direction].Type) && !included.Contains(cell + direction);
            anchored.Add(held);
        }
        Prof.Add($"pi.region{cells.Count}", Stopwatch.GetElapsedTime(profStart).TotalMilliseconds); // PROF
    }

    /// <summary>
    /// Deals the damage of every analysed impact. Where an object broke through the map, it
    /// gets back the share of the velocity the impact took that breaking did not use up, and
    /// the way it bounced back meanwhile (<see cref="RestoreMomentum"/>).
    /// </summary>
    private void ReceiveImpacts()
    {
        _impactWorker.Update();
        while (_impactWorker.TryTake(out ImpactOutcome outcome))
        {
            if (!_pendingImpacts.Remove(outcome.JobId, out PendingImpact? impact))
                continue;

            _lastImpactMilliseconds = outcome.Milliseconds;
            ImpactAnalysed?.Invoke(impact.Bodies, impact.Energy, outcome, _steps - impact.Step);

            bool brokeThrough = false;
            string cause = $"an impact of {impact.Energy / 1e3:F1} kJ";
            _gpuImpactDamage.Clear(); _gpuImpactRequests.Clear();
            foreach (ImpactSideOutcome side in outcome.Sides)
            {
                CellBody? body = impact.Bodies.FirstOrDefault(b => b.Id == side.BodyId);
                if (body == null || !IsPresent(body))
                    continue;

                if (body is VoxelBody voxels)
                {
                    foreach ((ivec3 cell, double damage) in side.Damage)
                    {
                        BlockDamageSite? site = impact.Sites.TryGetValue(body, out var sites) && sites.TryGetValue(cell, out var contact)
                            ? contact : null;
                        Voxel voxel = voxels.Grid[cell];
                        if (GpuDamageEnabled && voxel.Type != BlockType.Air && !voxel.Material.Unbreakable && damage > 0)
                        {
                            _gpuImpactDamage.Add((voxels, cell, voxel, site));
                            _gpuImpactRequests.Add(GpuDamageRequest.Raw(voxel.Material, voxel.Health, damage));
                        }
                        else if (!GpuDamageEnabled) Damage(voxels, cell, damage, cause, site);
                    }
                }
                else
                {
                    // The player's cells share one health.
                    DamagePlayer(side.Damage.Sum(d => d.Damage), cause);
                }

                brokeThrough |= side.Opened && body is VoxelMap;
            }

            if (_gpuImpactRequests.Count > 0)
            {
                dvec4[] results = (_gpuBlasts ??= _createGpuBlasts()).EvaluateDamage(CollectionsMarshal.AsSpan(_gpuImpactRequests));
                for (int i = 0; i < results.Length; i++)
                {
                    var (body, cell, voxel, site) = _gpuImpactDamage[i];
                    if (body.Grid[cell] != voxel || !(results[i].z > 0)) continue;
                    ApplyDamageState(body, cell, voxel, (ushort)results[i].y, results[i].x, cause, site);
                }
            }
            if (!brokeThrough || _steps - impact.Step > MaxRestoreDelay)
                continue;

            double kept = Math.Sqrt(outcome.RetainedEnergy / impact.Energy);
            foreach ((CellBody body, ImpactLoss loss) in impact.Lost)
                _restores.Add((body, loss, kept));
        }
    }

    /// <summary>
    /// Gives bodies back the momentum impacts they broke through took, once the broken blocks
    /// are gone. A body that bounced off what it broke has been going back the way it came
    /// while the impact was analysed, and goes on from where the impact left it instead: back
    /// along its heading, never past where it was then.
    /// </summary>
    private void RestoreMomentum()
    {
        foreach ((CellBody body, ImpactLoss loss, double kept) in _restores)
        {
            if (body.Rigid is not RigidBody rigid || !IsPresent(body))
                continue;

            if (loss.Heading.LengthSqr > 0.0)
            {
                dvec3 heading = loss.Heading.Normalized;
                double retreat = dvec3.Dot(body.Position - loss.Position, heading);
                if (retreat < 0.0)
                    rigid.Position -= retreat * heading;
            }

            rigid.LinearVelocity += kept * loss.Linear;
            rigid.AngularVelocity += kept * loss.Angular;
        }

        _restores.Clear();
    }

    // ---------------------------------------------------------------------------------------
    // Stress
    // ---------------------------------------------------------------------------------------

    /// <param name="Moment">Moment of the contact loads about the body's origin, which changes as they move.</param>
    private readonly record struct LoadSignature(int Version, double TotalForce, dvec3 Moment, dvec3 Gravity, dvec3 AngularVelocity);

    /// <summary>
    /// Sends softened materials, and loads that changed or moved enough to matter, to the
    /// map's lattice and the stress worker.
    /// </summary>
    private void SendStressInputs()
    {
        foreach ((VoxelBody body, List<(ivec3 Cell, CellMaterial Material)> changes) in _softened)
        {
            if (body is VoxelMap)
            {
                foreach ((ivec3 cell, CellMaterial material) in changes)
                {
                    int index = _mapStress.IndexOf(cell);
                    if (index >= 0)
                        _mapStress.SetMaterial(index, material);
                }
            }
            else
            {
                _stress.Post(new StressMaterials(body.Id, body.TopologyVersion, [.. changes]));
            }
        }

        _softened.Clear();

        dvec3 gravity = Physics.Settings.Gravity;
        foreach (VoxelBody body in Bodies)
        {
            // No cell-to-cell interfaces means identically zero structural stress.
            // PostTopology still publishes the matching zero snapshot; impacts keep
            // processing this object's contact forces and damage independently.
            if (body is VoxelObject && body.Grid.Count == 1)
                continue;
            dvec3 localGravity = body.DirectionToLocal(gravity);
            dvec3 omega = body is VoxelObject o ? body.DirectionToLocal(o.AngularVelocity) : dvec3.Zero;
            double total = 0.0;
            dvec3 moment = dvec3.Zero;
            foreach ((ivec3 cell, (dvec3 force, dvec3 cellMoment)) in body.ContactLoads)
            {
                total += force.Length;
                moment += dvec3.Cross(VoxelBody.CellCenter(cell), force) + cellMoment;
            }

            LoadSignature signature = new(body.TopologyVersion, total, moment, localGravity, omega);

            if (_sentLoads.TryGetValue(body, out LoadSignature sent)
                && sent.Version == signature.Version
                && Math.Abs(sent.TotalForce - total) <= 0.02 * sent.TotalForce + 100.0
                && (sent.Moment - moment).Length <= LoadShift * total + 100.0
                && (sent.Gravity - localGravity).Length <= 0.02 * gravity.Length
                && (sent.AngularVelocity - omega).Length <= 0.05)
            {
                continue;
            }

            _sentLoads[body] = signature;
            (ivec3, dvec3, dvec3)[] contacts = [.. body.ContactLoads.Select(l => (l.Key, l.Value.Force, l.Value.Moment))];
            if (body is VoxelMap)
            {
                ExpLoadSends++; // EXP
                if (Environment.GetEnvironmentVariable("MYT_EXP_LOADLOG") == "1" && _sentLoads.TryGetValue(body, out LoadSignature prev)) Console.WriteLine($"EXPLOAD total {total:F0} prev {prev.TotalForce:F0} dTotal {Math.Abs(prev.TotalForce - total):F0} tolT {0.02 * prev.TotalForce + 100:F0} dMoment {(prev.Moment - moment).Length:F0} tolM {LoadShift * total + 100:F0} n {body.ContactLoads.Count}"); // EXP
                _mapStress.SetLoads(localGravity, contacts);
            }
            else
                _stress.Post(new StressLoads(body.Id, body.TopologyVersion, localGravity, omega, contacts));
        }
    }

    /// <summary>
    /// Runs the map's analysis on: reads what the GPU finished and submits the next iterations.
    /// Results for the map as it is now, solved closely enough to show, are published: settled
    /// ones at once, as blocks break by them, and the first for new cells, but while it
    /// converges, only every few steps, which is plenty to watch, and costs a fraction of
    /// copying and comparing a few hundred thousand cells' stresses every step.
    /// </summary>
    private void UpdateMapStress()
    {
        if (_deferMapStress)
        {
            _deferMapStress = false;
            return;
        }
        if (Environment.GetEnvironmentVariable("MYT_EXP_NOSTRESS") == "1") return; // EXP
        if (Environment.GetEnvironmentVariable("MYT_EXP_STRESSFRAME") == "1" && ProfSteps > 0) return; // EXP
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); // EXP
        bool arrived = _mapCells.Length != 0 && _mapStress.Update(); // EXP
        Prof.Add("ms.update", System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds); // EXP
        if (!arrived) return; // EXP
        ExpResults++; // EXP
        if (!_mapStress.HasResults || _mapStress.RelativeResidual > MapShownResidual)
            return;
        ExpShown++; // EXP

        bool settled = _mapStress.RelativeResidual <= MapSettledResidual;
        bool fresh = Map.Stress == null || Map.Stress.Cells != _mapCells;
        if (!settled && !fresh && _steps - _mapPublishedStep < MapShownInterval)
            return;

        // The snapshot shown last still holds the other array, which comparing against it needs.
        if (_mapStresses[0].Length != _mapCells.Length)
            _mapStresses = [new CellStress[_mapCells.Length], new CellStress[_mapCells.Length]];
        CellStress[] stresses = Map.Stress?.Stresses == _mapStresses[0] ? _mapStresses[1] : _mapStresses[0];

        t0 = System.Diagnostics.Stopwatch.GetTimestamp(); // EXP
        _mapStress.CopyStresses(stresses);
        Prof.Add("ms.copy", System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds); // EXP
        ExpPublished++; // EXP
        _mapPublishedStep = _steps;
        PublishStress(Map, new StressSnapshot(_mapCellsVersion, _mapCells, stresses, settled, _mapCellIndex));
    }

    private void ReceiveStress()
    {
        _stress.Update();
        while (_stress.TryTake(out StressResult result))
        {
            VoxelObject? body = _objectsById.GetValueOrDefault(result.BodyId);
            if (body == null || result.Snapshot.Version != body.TopologyVersion)
                continue;

            PublishStress(body, result.Snapshot);
            _lastStressMilliseconds = result.Milliseconds;
            _lastStressIterations = result.Solve.Iterations;
        }
    }

    /// <summary>
    /// Gives a body a new analysis, and if it is settled, watches the cells it loads enough to
    /// matter; an unsettled one breaks nothing (<see cref="ApplyStressDamage"/>).
    /// </summary>
    private void PublishStress(VoxelBody body, StressSnapshot snapshot)
    {
        body.SetStress(snapshot);
        if (!snapshot.Settled)
            return;

        int count = snapshot.Cells.Length;
        if (_watchFlags.Length < count)
            _watchFlags = new bool[Math.Max(count, _watchFlags.Length * 2)];
        bool[] flags = _watchFlags;
        if (count < ParallelWatchCells)
        {
            FlagWatched(body.Grid, snapshot, flags, 0, count);
        }
        else
        {
            // Cells come chunk by chunk; each range keeps its own chunk at hand.
            int ranges = (count + ParallelWatchCells - 1) / ParallelWatchCells;
            Parallel.For(0, ranges, r => FlagWatched(body.Grid, snapshot, flags, r * ParallelWatchCells, Math.Min(count, (r + 1) * ParallelWatchCells)));
        }

        List<ivec3> watched = [];
        for (int i = 0; i < count; i++)
        {
            if (flags[i])
                watched.Add(snapshot.Cells[i]);
        }

        _watched[body] = watched;
    }

    /// <summary>Which cells of [start, end) are loaded enough to matter, looking each run of cells' chunk up once.</summary>
    private static void FlagWatched(Grid3D<Voxel> grid, StressSnapshot snapshot, bool[] flags, int start, int end)
    {
        ivec3 lastChunk = new(int.MinValue);
        ReadOnlySpan<Voxel> chunkCells = default;
        for (int i = start; i < end; i++)
        {
            ivec3 cell = snapshot.Cells[i];
            ivec3 chunk = Grid3D<Voxel>.ChunkOf(cell);
            if (chunk != lastChunk)
            {
                lastChunk = chunk;
                chunkCells = grid.TryGetChunk(chunk, out Grid3DChunk<Voxel> found) ? found.Cells : default;
            }

            Voxel voxel = chunkCells.IsEmpty ? grid.Empty : chunkCells[Grid3D<Voxel>.IndexInChunk(cell)];
            flags[i] = voxel.Type != BlockType.Air && voxel.Material.ElasticLimit(voxel.Health).Ratio(snapshot.Stresses[i]) >= StressWatchRatio;
        }
    }

    /// <summary>
    /// Damages blocks stressed past their elastic limit, faster the further past it they are.
    /// Failure localizes around a group's highest stress. Similar, nonadjacent peaks
    /// fail together across broad regions; adjacent cells of a crack are spared until
    /// the loads redistribute. Only a settled analysis of the body as it is now counts: once a block breaks, the
    /// loads take new paths, and the old analysis says nothing about them.
    /// </summary>
    private void ApplyStressDamage()
    {
        _gpuStressDamage.Clear();
        _gpuStressRequests.Clear();
        foreach ((VoxelBody body, List<ivec3> cells) in _watched)
        {
            if (cells.Count == 0)
                continue;
            StressSnapshot? stress = body.Stress;
            if (stress == null || !stress.Settled || stress.Version != body.TopologyVersion)
                continue;

            Dictionary<ivec3, double> overloaded = _overloadedCells;
            overloaded.Clear();
            foreach (ivec3 cell in cells)
            {
                Voxel voxel = body.Grid[cell];
                if (voxel.Type == BlockType.Air || voxel.Material.Unbreakable || (body is VoxelMap && NearWindowSide(cell)))
                    continue;

                double ratio = body.StressRatio(cell, voxel);
                if (ratio > 1.0)
                    overloaded[cell] = ratio;
            }

            if (overloaded.Count == 0)
                continue;
            foreach ((ivec3 cell, double ratio) in MostOverloaded(overloaded))
            {
                // Mining durability must not turn gross structural overload into
                // minutes of chipping. Consume at least one intact damage budget
                // per second per unit of excess elastic energy, preserving the
                // existing faster response of fragile materials.
                double rate = Math.Max(StressDamageRate, body.Grid[cell].Material.DamageToBreak(1));
                if (GpuDamageEnabled)
                {
                    Voxel voxel = body.Grid[cell];
                    _gpuStressDamage.Add((body, cell, voxel, ratio));
                    _gpuStressRequests.Add(GpuDamageRequest.Sustained(voxel, body.StaticStress(cell), Step, StressDamageRate));
                }
                else Damage(body, cell, Step * rate * (ratio * ratio - 1.0), $"stress at {ratio:F2}x its elastic limit");
            }
        }
        if (_gpuStressRequests.Count > 0)
        {
            dvec4[] results = (_gpuBlasts ??= _createGpuBlasts()).EvaluateDamage(CollectionsMarshal.AsSpan(_gpuStressRequests));
            for (int i = 0; i < results.Length; i++)
            {
                var (body, cell, voxel, ratio) = _gpuStressDamage[i];
                if (body.Grid[cell] != voxel || !(results[i].z > 0)) continue;
                ApplyDamageState(body, cell, voxel, (ushort)results[i].y, results[i].x, $"stress at {ratio:F2}x its elastic limit", null);
            }
        }
    }

    private readonly List<(VoxelBody Body, ivec3 Cell, Voxel Voxel, double Ratio)> _gpuStressDamage = [];
    private readonly List<GpuDamageRequest> _gpuStressRequests = [];
    private readonly List<(VoxelBody Body, ivec3 Cell, Voxel Voxel, BlockDamageSite? Site)> _gpuImpactDamage = [];
    private readonly List<GpuDamageRequest> _gpuImpactRequests = [];

    /// <summary>The most overloaded cell of each face-connected group of overloaded cells.</summary>
    internal List<(ivec3 Cell, double Ratio)> MostOverloaded(Dictionary<ivec3, double> overloaded)
    {
        List<(ivec3 Cell, double Ratio)> failing = _overloadFailures;
        HashSet<ivec3> seen = _overloadSeen;
        List<ivec3> group = _overloadGroup;
        failing.Clear();
        seen.Clear();
        foreach (ivec3 seed in overloaded.Keys)
        {
            if (!seen.Add(seed))
                continue;

            group.Clear();
            group.Add(seed);
            for (int k = 0; k < group.Count; k++)
            {
                foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
                {
                    ivec3 next = group[k] + direction;
                    if (overloaded.ContainsKey(next) && seen.Add(next))
                        group.Add(next);
                }
            }

            ivec3 worst = group[0];
            for (int k = 1; k < group.Count; k++)
            {
                if (overloaded[group[k]] > overloaded[worst])
                    worst = group[k];
            }
            ivec3 low = group[0], high = group[0];
            foreach (ivec3 cell in group) { low = ivec3.Min(low, cell); high = ivec3.Max(high, cell); }
            ivec3 extent = high - low;
            int dimensions = (extent.x > 0 ? 1 : 0) + (extent.y > 0 ? 1 : 0) + (extent.z > 0 ? 1 : 0);
            // A thin leg/chain still localizes at one break; broad failure regions
            // have independent transverse sites that may fail simultaneously.
            if (dimensions <= 1)
            {
                failing.Add((worst, overloaded[worst]));
                continue;
            }
            // Similar peaks can fail together across a wide slab. Spare adjacent
            // cells of a crack, but do not serialize an entire overloaded structure
            // behind a single block and another full topology rebuild.
            HashSet<ivec3> selected = [];
            foreach (ivec3 cell in group.OrderByDescending(c => overloaded[c]).ThenBy(c => c.z).ThenBy(c => c.y).ThenBy(c => c.x))
            {
                if (overloaded[cell] < overloaded[worst] / 1.05) break;
                if (Grid3D<Voxel>.FaceDirections.Any(d => selected.Contains(cell + d))) continue;
                selected.Add(cell);
                failing.Add((cell, overloaded[cell]));
            }
        }

        return failing;
    }

    private void PostTopology(VoxelBody body)
    {
        // The map changes by a few cells most of the time. Retain preparation capacity;
        // the arrays published to snapshots/workers below remain independent.
        bool isMap = body is VoxelMap;
        List<ivec3> cells = isMap ? _mapTopologyCells : [];
        List<CellMaterial> materials = isMap ? _mapTopologyMaterials : [];
        List<double> masses = isMap ? _mapTopologyMasses : [];
        List<bool> anchored = isMap ? _mapTopologyAnchors : [];
        cells.Clear(); materials.Clear(); masses.Clear(); anchored.Clear();
        double volume = VoxelBody.CellSize * VoxelBody.CellSize * VoxelBody.CellSize;

        long profStart = Stopwatch.GetTimestamp(); // PROF
        body.SentDamage.Clear();
        foreach ((ivec3 cell, Voxel voxel, bool held) in AnalysedCells(body))
        {
            cells.Add(cell);
            materials.Add(voxel.Material.Elastic(voxel.Health));
            masses.Add(voxel.Material.Density * volume);
            anchored.Add(held);
            // Undamaged cells, nearly all of the map's, are left out: the level they are at is 0.
            int level = (int)(voxel.DamageFraction * 16);
            if (level != 0)
                body.SentDamage[cell] = level;
        }

        _softened.Remove(body);
        _sentLoads.Remove(body);
        if (body is VoxelMap map)
        {
            Prof.Add("pt.cells", Stopwatch.GetElapsedTime(profStart).TotalMilliseconds); // PROF
            profStart = Stopwatch.GetTimestamp(); // PROF
            _mapCells = [.. cells];
            _mapCellIndex = IndexOf(_mapCells);
            _mapCellsVersion = body.TopologyVersion;
            Prof.Add("pt.index", Stopwatch.GetElapsedTime(profStart).TotalMilliseconds); // PROF
            profStart = Stopwatch.GetTimestamp(); // PROF
            if (cells.Count > 0)
                _mapStress.SetCells(_mapCells, CollectionsMarshal.AsSpan(materials), CollectionsMarshal.AsSpan(masses), CollectionsMarshal.AsSpan(anchored));
            else
                map.ClearStress();
            Prof.Add("pt.setCells", Stopwatch.GetElapsedTime(profStart).TotalMilliseconds); // PROF
            return;
        }

        _stress.Post(new StressTopology(body.Id, body.TopologyVersion, [.. cells], [.. materials], [.. masses], [.. anchored], body is VoxelObject));
    }

    /// <summary>
    /// Where each of the map's analysed cells is among them, over the box they fill: the
    /// stress analysis' box, or the whole map's. None for a map so spread out that the box
    /// would take far more memory than the cells, which the map then indexes itself.
    /// </summary>
    private static BoxIndex? IndexOf(ivec3[] cells)
    {
        if (cells.Length == 0)
            return null;

        ivec3 low = new(int.MaxValue);
        ivec3 high = new(int.MinValue);
        foreach (ivec3 cell in cells)
        {
            low = ivec3.Min(low, cell);
            high = ivec3.Max(high, cell + 1);
        }

        ivec3 size = high - low;
        if ((long)size.x * size.y * size.z > 16L * cells.Length + (1 << 20))
            return null;

        BoxIndex index = new(low, high);
        for (int i = 0; i < cells.Length; i++)
            index[cells[i]] = i;
        return index;
    }

    /// <summary>
    /// The cells a body's stress analysis covers, and which of them are held in place: all of an
    /// object's, none held; all of the map's, the bedrock held, or while the simulation follows
    /// the player, those in the box around it, the bedrock held and every cell the map goes on
    /// from beyond the box, which holds it there.
    /// </summary>
    internal IEnumerable<(ivec3 Cell, Voxel Voxel, bool Held)> AnalysedCells(VoxelBody body)
    {
        if (body is not VoxelMap map)
        {
            foreach ((ivec3 cell, Voxel voxel) in body.Grid.Occupied())
                if (Blocks.IsSolid(voxel.Type))
                    yield return (cell, voxel, false);
            yield break;
        }

        if (!FollowPlayer)
        {
            foreach ((ivec3 cell, Voxel voxel) in map.Grid.Occupied())
                if (Blocks.IsSolid(voxel.Type))
                    yield return (cell, voxel, VoxelMap.IsAnchor(voxel));
            yield break;
        }

        if (_window is not (ivec3 low, ivec3 high))
            yield break;

        ivec3 firstChunk = Grid3D<Voxel>.ChunkOf(low);
        ivec3 lastChunk = Grid3D<Voxel>.ChunkOf(high - 1);
        for (int cz = firstChunk.z; cz <= lastChunk.z; cz++)
        for (int cy = firstChunk.y; cy <= lastChunk.y; cy++)
        for (int cx = firstChunk.x; cx <= lastChunk.x; cx++)
        {
            ivec3 chunk = new(cx, cy, cz);
            if (!map.Grid.TryGetChunk(chunk, out Grid3DChunk<Voxel> cells))
                continue;

            ivec3 origin = chunk * Grid3D<Voxel>.ChunkSize;
            ivec3 first = ivec3.Max(low - origin, ivec3.Zero);
            ivec3 last = ivec3.Min(high - origin, new ivec3(Grid3D<Voxel>.ChunkSize));
            // Retain chunk/index ordering, but never visit the portion outside the window.
            for (int z = first.z; z < last.z; z++)
            for (int y = first.y; y < last.y; y++)
            for (int x = first.x; x < last.x; x++)
            {
                Voxel voxel = cells.Cells[x + 16 * (y + 16 * z)];
                if (!Blocks.IsSolid(voxel.Type))
                    continue;
                ivec3 cell = origin + new ivec3(x, y, z);
                // Only a boundary cell can have a neighbour outside the analysis box.
                // Interior cells need no six-neighbour search; anchored cells already win.
                bool held = VoxelMap.IsAnchor(voxel)
                    || (cell.x == high.x - 1 && Blocks.IsSolid(map.Grid[cell + new ivec3(1, 0, 0)].Type))
                    || (cell.x == low.x && Blocks.IsSolid(map.Grid[cell + new ivec3(-1, 0, 0)].Type))
                    || (cell.y == high.y - 1 && Blocks.IsSolid(map.Grid[cell + new ivec3(0, 1, 0)].Type))
                    || (cell.y == low.y && Blocks.IsSolid(map.Grid[cell + new ivec3(0, -1, 0)].Type))
                    || (cell.z == high.z - 1 && Blocks.IsSolid(map.Grid[cell + new ivec3(0, 0, 1)].Type))
                    || (cell.z == low.z && Blocks.IsSolid(map.Grid[cell + new ivec3(0, 0, -1)].Type));
                yield return (cell, voxel, held);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Topology: connectivity, splitting and rebuilding bodies
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Handles every add and remove since the last call: map blocks cut off from bedrock fall
    /// away as objects, objects cut apart split, and every changed body gets new collision
    /// geometry, a new topology version and a new stress lattice.
    /// </summary>
    public void ApplyTopologyChanges()
    {
        while (_topologyChanged.Count > 0)
        {
            VoxelBody[] changed = [.. _topologyChanged];
            _topologyChanged.Clear();

            foreach (VoxelBody body in changed)
            {
                if (body is VoxelMap map)
                    Prof.Time("t.detach", () => DetachUnsupported(map)); // PROF
                else if (body is VoxelObject voxelObject && HasObject(voxelObject))
                    SplitApart(voxelObject);
            }

            foreach (VoxelBody body in changed)
            {
                if (body is VoxelObject voxelObject && !HasObject(voxelObject))
                    continue;

                Rebuild(body);
            }
        }
    }

    /// <summary>
    /// After map blocks were removed, every group of map blocks around them that can no
    /// longer reach bedrock through other blocks becomes an object.
    /// </summary>
    private void DetachUnsupported(VoxelMap map)
    {
        HashSet<ivec3> supported = [];
        HashSet<ivec3> falling = [];

        foreach (ivec3 removed in map.RemovedCells)
        {
            foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
            {
                ivec3 start = removed + direction;
                if (!Blocks.IsSolid(map.Grid[start].Type) || supported.Contains(start) || falling.Contains(start))
                    continue;

                HashSet<ivec3> group = [];
                bool anchored = map.Grid.FloodFill(start, group, include: (_, voxel) => Blocks.IsSolid(voxel.Type), stop: (cell, voxel) => VoxelMap.IsAnchor(voxel) || supported.Contains(cell) || StandsOnBedrock(map, cell));
                if (anchored)
                {
                    supported.UnionWith(group);
                    continue;
                }

                falling.UnionWith(group);
                Detach(map, group, dvec3.Zero, dvec3.Zero);
            }
        }

        map.RemovedCells.Clear();
    }

    /// <summary>
    /// Whether an unbroken column of blocks carries a cell down to bedrock, which settles that it
    /// is anchored at once: searching outward for bedrock instead would cover the whole of a
    /// mountain before reaching its foot.
    /// </summary>
    private static bool StandsOnBedrock(VoxelMap map, ivec3 cell)
    {
        for (ivec3 below = cell - new ivec3(0, 0, 1); ; below.z--)
        {
            Voxel voxel = map.Grid[below];
            if (!Blocks.IsSolid(voxel.Type))
                return false;
            if (VoxelMap.IsAnchor(voxel))
                return true;
        }
    }

    /// <summary>After blocks were removed from an object, its pieces that no longer touch become separate objects.</summary>
    private void SplitApart(VoxelObject body)
    {
        if (body.Grid.Count == 0)
        {
            RemoveObject(body);
            return;
        }

        if (body.RemovedCells.Count == 0)
            return;

        List<HashSet<ivec3>> pieces = body.Grid.ComponentsOf(body.RemovedCells.SelectMany(c => Grid3D<Voxel>.FaceDirections.Select(d => c + d)));
        body.RemovedCells.Clear();
        if (pieces.Count < 2)
            return;

        dvec3 velocity = body.LinearVelocity;
        dvec3 omega = body.AngularVelocity;
        dvec3 center = body.Body?.WorldCenterOfMass ?? body.Position;

        foreach (HashSet<ivec3> piece in pieces.Skip(1))
            Detach(body, piece, velocity, omega, center);
    }

    /// <summary>
    /// Moves cells out of a body into a new object at the same place, moving as the old body
    /// did at that point: v + ω × (its center − the old center).
    /// </summary>
    private void Detach(VoxelBody source, HashSet<ivec3> cells, dvec3 velocity, dvec3 omega, dvec3? sourceCenter = null)
    {
        ivec3 origin = new(int.MaxValue);
        foreach (ivec3 cell in cells)
            origin = ivec3.Min(origin, cell);

        VoxelObject piece = new(_nextId++)
        {
            PendingPosition = source.ToWorld(new dvec3(origin) * VoxelBody.CellSize),
            PendingOrientation = source.Orientation,
            PendingAngularVelocity = omega
        };

        foreach (ivec3 cell in cells)
        {
            if (Furnaces.Remove((source, cell), out Furnace? furnace))
                Furnaces[(piece, cell - origin)] = furnace;
            piece.Grid.Set(cell - origin, source.Grid[cell]);
            source.Grid.Set(cell, Voxel.Air);
            source.ContactLoads.Remove(cell);
            NoteEdit(source, cell);
        }

        (_, dvec3 localCenter, _) = piece.MassProperties();
        dvec3 worldCenter = piece.ToWorld(localCenter);
        piece.PendingLinearVelocity = velocity + dvec3.Cross(omega, worldCenter - (sourceCenter ?? worldCenter));

        // The source is being processed already; only the new piece still needs building.
        _objects.Add(piece);
        _objectsById.Add(piece.Id, piece);
        _topologyChanged.Add(piece);
    }

    private void Rebuild(VoxelBody body)
    {
        ivec3[] renderChunks = body.Grid.TakeChangedChunks(out ivec3[] chunks);
        body.MeshDirty.UnionWith(renderChunks);
        body.DataDirty.UnionWith(chunks);

        if (body is VoxelMap map)
        {
            // While the simulation follows the player, only the chunks bodies are near have
            // colliders; the others get one when a body comes near.
            foreach (ivec3 chunk in chunks)
            {
                _baker.Invalidate(chunk);
                _faceless.Remove(chunk);
                if (!FollowPlayer || map.Colliders.ContainsKey(chunk) || _neededCoverage.Chunks.Contains(chunk))
                    Prof.Time("t.collider", () => RebuildCollider(map, chunk)); // PROF
            }

            // Columns streaming in and out far away leave the analysis as it is.
            if (FollowPlayer && !chunks.Any(TouchesWindow))
                return;
        }
        else
        {
            RebuildRigidBody((VoxelObject)body);
        }

        body.TopologyVersion++;
        PostTopology(body);
    }

    /// <summary>Takes a chunk's collider out of the physics. Returns whether it had one.</summary>
    private bool RemoveCollider(VoxelMap map, ivec3 chunk)
    {
        if (!map.Colliders.Remove(chunk, out FixedBody? old))
            return false;

        Physics.Remove(old);
        _owners.Remove(old);
        return true;
    }

    /// <summary>Gives a chunk a collider for its blocks as they are now, or none if it has no faces. Returns whether its colliders changed.</summary>
    private bool RebuildCollider(VoxelMap map, ivec3 chunk)
    {
        bool removed = RemoveCollider(map, chunk);
        long profM = Stopwatch.GetTimestamp(); // PROF
        FixedBody? profBody = ColliderBaker.Make(VoxelMesher.BuildCollisionMesh(map.Grid, [chunk], openBelowBedrock: true, mergeFaces: VoxelMesher.MergeCollisionFaces)); // PROF
        Prof.Add("c.mesh", Stopwatch.GetElapsedTime(profM).TotalMilliseconds); // PROF
        profM = Stopwatch.GetTimestamp(); // PROF
        profBody?.Cook(); // PROF
        Prof.Add("c.cook", Stopwatch.GetElapsedTime(profM).TotalMilliseconds); // PROF
        if (profBody is FixedBody collider)
            return AddCollider(map, chunk, collider);

        if (FollowPlayer && map.Grid.TryGetChunk(chunk, out _))
            _faceless.Add(chunk);
        return removed;
    }

    private bool AddCollider(VoxelMap map, ivec3 chunk, FixedBody collider)
    {
        Physics.Add(collider);
        map.Colliders[chunk] = collider;
        _owners[collider] = map;
        return true;
    }

    /// <summary>
    /// Replaces an object's rigid body after its cells changed, keeping its pose and motion:
    /// the new center of mass moves with the velocity the old rigid motion gives that point.
    /// An object outside the simulated region is parked instead, with that pose and motion.
    /// </summary>
    private void RebuildRigidBody(VoxelObject body)
    {
        dvec3 position = body.Position;
        dquat orientation = body.Orientation;
        dvec3 velocity = body.LinearVelocity;
        dvec3 omega = body.AngularVelocity;
        dvec3? oldCenter = body.Body?.WorldCenterOfMass;

        if (body.Body != null)
        {
            Physics.Remove(body.Body);
            _owners.Remove(body.Body);
            body.Body = null;
        }

        (ivec3 lowest, ivec3 highest) = body.Grid.Bounds();
        dvec3 middle = 0.5 * new dvec3(lowest + highest) * VoxelBody.CellSize;
        body.Bounds = (middle, 0.5 * (new dvec3(highest - lowest) * VoxelBody.CellSize).Length);

        // Its pose and motion while it has no rigid body, which the check below reads.
        body.PendingPosition = position;
        body.PendingOrientation = orientation;
        body.PendingLinearVelocity = velocity;
        body.PendingAngularVelocity = omega;
        if (body.Parked || !MaySimulate(body, 0.0))
        {
            body.Parked = true;
            return;
        }

        (double mass, dvec3 center, dmat3 inertia) = body.MassProperties();
        // Keep moving bodies' contact samples at cell resolution: their reports drive local
        // impact damage. Terrain can merge while these samples still locate cell loads.
        (Mesh3D mesh, SurfaceMaterial[] surfaces) = VoxelMesher.BuildRigidCollisionMesh(body.Grid, lowest, highest);
        dvec3 newCenter = position + orientation * center;

        RigidBody rigid = new(mesh, mass, center, inertia)
        {
            Position = position,
            Orientation = orientation,
            LinearVelocity = velocity + (oldCenter.HasValue ? dvec3.Cross(omega, newCenter - oldCenter.Value) : dvec3.Zero),
            AngularVelocity = omega,
            SurfaceMaterials = surfaces
        };

        Physics.Add(rigid);
        body.Body = rigid;
        _owners[rigid] = body;
    }

    private void RemoveObject(VoxelObject body)
    {
        if (body.Body != null)
        {
            Physics.Remove(body.Body);
            _owners.Remove(body.Body);
            body.Body = null;
        }

        _objects.Remove(body);
        _objectsById.Remove(body.Id);
        _topologyChanged.Remove(body);
        _watched.Remove(body);
        _softened.Remove(body);
        _sentLoads.Remove(body);
        _stress.Post(new StressRemove(body.Id));
        BodyRemoved?.Invoke(body);
    }

    public void Dispose()
    {
        _gpuBlasts?.Dispose();
        _baker.Dispose();
        _impactWorker.Dispose();
        _stress.Dispose();
        _playerGpuStress?.Dispose();
        _mapStress.Dispose();
        Physics.Dispose();
    }
}
