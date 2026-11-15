using System.Diagnostics;
using System.Threading.Channels;

using GlmSharp;

using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// Where an impact presses on one cell of a side: the force on it (only its size relative to
/// the other contacts matters), its moment about the cell center, and the unit normal pointing
/// into the body. AreaFraction is the shared contact footprint relative to a cell face.
/// </summary>
internal readonly record struct ImpactContact(int Cell, dvec3 Force, dvec3 Moment, dvec3 Inward, double AreaFraction = 1.0);

/// <summary>Finite contact patches for voxel impacts, shared by both bodies.</summary>
internal static class ImpactLoading
{
    /// <summary>
    /// A regularized footprint from the contact normal in each grid. A face uses the full
    /// cell, an edge a quarter and a corner a ninth. This is a voxel-scale approximation,
    /// not a resolved indentation: finite patches avoid singular stresses at sharp points.
    /// The sharper side sets the footprint on both bodies.
    /// </summary>
    public static double AreaFraction(dvec3 normalA, dvec3 normalB)
    {
        return Math.Min(Footprint(normalA), Footprint(normalB));

        static double Footprint(dvec3 normal)
        {
            double length = normal.Length;
            if (!(length > 0.0))
                return 1.0;
            dvec3 n = dvec3.Abs(normal / length);
            double sum = n.x + n.y + n.z;
            return Math.Clamp(1.0 / (sum * sum * sum * sum), 1.0 / 9.0, 1.0);
        }
    }

    /// <summary>
    /// Each side must carry the same total load. Contact count and closing speed determine
    /// its distribution, never an independent force multiplier for one of the bodies.
    /// </summary>
    public static ImpactContact[] Normalize(ImpactContact[] contacts)
    {
        double total = contacts.Sum(c => c.Force.Length);
        return total > 0.0
            ? [.. contacts.Select(c => c with { Force = c.Force / total, Moment = c.Moment / total })]
            : [];
    }
}

/// <summary>
/// One side of an impact: the cells of a body around the contact (a voxel body's, or the
/// player's), in the body's frame, and what the analysis needs to know about each (arrays
/// indexed alike).
/// </summary>
/// <param name="CellEdge">Edge length of the side's cells, m.</param>
/// <param name="Free">A moving object, which the impact accelerates; otherwise its <paramref name="Anchored"/> cells hold it.</param>
/// <param name="Limits">Each cell's elastic limits at its current health.</param>
/// <param name="Static">The stresses each cell already carries under the sustained loads.</param>
/// <param name="DamageToBreak">Damage (HP) that breaks each cell; infinite for unbreakable ones.</param>
/// <param name="Granular">Per cell, whether it has to be crushed to break (<see cref="BlockMaterial.Granular"/>).</param>
internal sealed record ImpactSide
(
    int BodyId,
    double CellEdge,
    ivec3[] Cells,
    CellMaterial[] Materials,
    double[] Masses,
    bool[] Anchored,
    bool Free,
    ElasticLimits[] Limits,
    CellStress[] Static,
    double[] DamageToBreak,
    bool[] Granular,
    ImpactContact[] Contacts
);

/// <summary>An impact to analyse: the energy available at peak compression (J) and its sides, one or two.</summary>
internal sealed record ImpactJob(int Id, double Energy, ImpactSide[] Sides);

/// <summary>What an impact did to one side.</summary>
/// <param name="Damage">Damage (HP) per cell.</param>
/// <param name="Opened">The struck part came loose, or the impact broke through it into open space, so the other side carries on.</param>
internal sealed record ImpactSideOutcome(int BodyId, (ivec3 Cell, double Damage)[] Damage, bool Opened);

/// <param name="RetainedEnergy">The part of the impact's energy (J) that breaking did not absorb.</param>
internal sealed record ImpactOutcome(int JobId, ImpactSideOutcome[] Sides, double RetainedEnergy, int Rounds, double Milliseconds);


/// <summary>
/// Works out what an impact breaks.
/// <para>
/// An impact is analysed as an equivalent static load (the energy method of impact loading):
/// the contact forces of the step, scaled by λ until the elastic energy both sides store
/// equals the kinetic energy the collision took. Each side's strain energy under the contact
/// forces comes from a <see cref="StressLattice"/> of its cells around the contact (a moving
/// object with inertia relief), plus the half cell between each contact face and its cell
/// center. The peak force therefore grows with the impactor's mass and speed and with how
/// stiff the struck structure is where it is hit, and every cell's peak stress follows:
/// σ_static + λ·σ_unit, where σ_unit is the stress under the contact forces alone.
/// </para>
/// <para>
/// Cells fail weakest first. As λ grows from zero, the first cells to reach their elastic
/// limit, with any that reach it under a load within <see cref="GroupSpread"/> of theirs,
/// take the energy left past that point as damage, shared by the strain energy each would
/// hold past its own limit, each up to what breaks it. Energy is counted in units of the
/// cell's elastic capacity in the mode it fails in (lim² / 2E per volume, with G for shear):
/// a cell that takes k times its capacity takes k × <see cref="ImpactDamage"/> HP. Snapping
/// a brittle block in tension takes little energy; crushing one takes a lot. Soil fails only
/// where the impact presses on it: the grass a glass wall stands on would otherwise reach its
/// small limit first and soak up, slowly crushed, the impact that should shatter the wall.
/// </para>
/// <para>
/// When cells break, the analysis repeats without them on the energy left: on a fixed
/// structure the impact moves on to the cells behind the contacts it broke, and the share of
/// the energy that went through where nothing is behind breaks nothing more. It ends when the
/// struck part comes loose or the impact has broken through everywhere. A moving object that
/// breaks flies apart, which ends it too.
/// </para>
/// </summary>
internal static class ImpactAnalysis
{
    /// <summary>Damage (HP) a cell takes per elastic capacity of energy it absorbs past its limit.</summary>
    public const double ImpactDamage = 2.0;
    // Cells reaching their limit under at most this times the load of the first fail with it,
    // such as the two ends of a beam hit in its middle. Any others are spared: once the first
    // cells break, the load that would have broken them is gone.
    private const double GroupSpread = 1.05;
    private const int MaxRounds = 4;
    private const int MaxIterations = 200;
    private const double Tolerance = 1e-3;
    // A cell that the sustained loads already overload is analysed as if just below its limit,
    // so it does not soak up an impact that barely reaches it.
    private const double StaticCap = 0.95;

    private enum Mode
    {
        Tension,
        Compression,
        Shear
    }

    /// <summary>A side's state over the rounds of one analysis.</summary>
    internal sealed class SideState
    {
        /// <summary>The neighbouring cell a direction points at most.</summary>
        private static ivec3 StepInto(dvec3 direction)
        {
            dvec3 a = dvec3.Abs(direction);
            if (a.x >= a.y && a.x >= a.z)
                return new ivec3(Math.Sign(direction.x), 0, 0);
            return a.y >= a.z ? new ivec3(0, Math.Sign(direction.y), 0) : new ivec3(0, 0, Math.Sign(direction.z));
        }

        public SideState(ImpactSide side)
        {
            Side = side;
            for (int i = 0; i < side.Cells.Length; i++)
                Index[side.Cells[i]] = i;

            Broken = new bool[side.Cells.Length];
            Damage = new double[side.Cells.Length];
            Unit = new CellStress[side.Cells.Length];
            UnitEnergy = new double[side.Cells.Length];
            Contacts = [.. side.Contacts];
        }

        public ImpactSide Side { get; }
        public Dictionary<ivec3, int> Index { get; } = [];
        public bool[] Broken { get; }
        public double[] Damage { get; }
        // Stresses and strain energy of each cell under the contact forces alone.
        public CellStress[] Unit { get; }
        public double[] UnitEnergy { get; }
        public List<ImpactContact> Contacts { get; private set; }
        // Strain energy under the contact forces alone.
        public double Energy { get; set; }
        public bool Opened { get; set; }
        public List<int> NewlyBroken { get; } = [];

        /// <summary>
        /// Contacts on cells that broke press on the cell behind them instead, along the way
        /// the impact pushes; where there is none, the impact broke through into open space.
        /// Returns the share of the contact force still pressing on the side.
        /// </summary>
        public double MoveContacts()
        {
            dvec3 push = dvec3.Zero;
            double total = 0.0;
            foreach (ImpactContact contact in Contacts)
            {
                push += contact.Force;
                total += contact.Force.Length;
            }

            ivec3 behind = StepInto(push);
            double through = 0.0;
            Dictionary<int, ImpactContact> moved = [];
            foreach (ImpactContact contact in Contacts)
            {
                int cell = contact.Cell;
                if (Broken[cell])
                {
                    if (!Index.TryGetValue(Side.Cells[cell] + behind, out cell) || Broken[cell])
                    {
                        through += contact.Force.Length;
                        continue;
                    }
                }

                moved[cell] = moved.TryGetValue(cell, out ImpactContact other)
                    ? other with { Force = other.Force + contact.Force, Moment = other.Moment + contact.Moment }
                    : contact with { Cell = cell };
            }

            Contacts = [.. moved.Values];
            return total > 0.0 ? 1.0 - through / total : 0.0;
        }

        /// <summary>
        /// Whether a cell can fail under the impact: any but a granular one the impact does not
        /// press on directly. Soil is broken only by being crushed, and an impact is over long
        /// before the load that would crush the footing of what it hits gets there; the struck
        /// part gives first.
        /// </summary>
        public bool CanFail(int i)
        {
            return !Broken[i] && (!Side.Granular[i] || Contacts.Any(c => c.Cell == i));
        }

        /// <summary>Whether any contact cell still reaches an anchored cell through unbroken cells.</summary>
        public bool ContactsHeld()
        {
            Queue<int> open = new();
            bool[] seen = new bool[Broken.Length];
            foreach (ImpactContact contact in Contacts)
            {
                if (!seen[contact.Cell])
                {
                    seen[contact.Cell] = true;
                    open.Enqueue(contact.Cell);
                }
            }

            while (open.Count > 0)
            {
                int i = open.Dequeue();
                if (Side.Anchored[i])
                    return true;

                foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
                {
                    if (Index.TryGetValue(Side.Cells[i] + direction, out int j) && !Broken[j] && !seen[j])
                    {
                        seen[j] = true;
                        open.Enqueue(j);
                    }
                }
            }

            return false;
        }
    }

    public static ImpactOutcome Run(ImpactJob job)
    {
        Stopwatch watch = Stopwatch.StartNew();
        SideState[] sides = [.. job.Sides.Select(s => new SideState(s))];
        double remaining = job.Energy;
        double through = 0.0;
        int round = 0;

        while (round < MaxRounds && remaining > 0.0)
        {
            round++;
            double energy = 0.0;
            bool loose = false;
            foreach (SideState side in sides)
            {
                if (!side.Side.Free && !side.ContactsHeld())
                {
                    side.Opened = true;
                    loose = true;
                    break;
                }

                Solve(side);
                energy += side.Energy;
            }

            if (loose || !(energy > 0.0))
                break;

            if (!Fracture(sides, energy, ref remaining, ref through))
                break;
        }

        ImpactSideOutcome[] outcomes = new ImpactSideOutcome[sides.Length];
        for (int s = 0; s < sides.Length; s++)
        {
            SideState side = sides[s];
            List<(ivec3, double)> damage = [];
            for (int i = 0; i < side.Damage.Length; i++)
            {
                if (side.Damage[i] > 0.0)
                    damage.Add((side.Side.Cells[i], side.Damage[i]));
            }

            outcomes[s] = new ImpactSideOutcome(side.Side.BodyId, [.. damage], side.Opened);
        }

        return new ImpactOutcome(job.Id, outcomes, remaining + through, round, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// One round of failure at the impact's full load: damages the weakest cells and reports
    /// whether the analysis should go on without the ones that broke. The energy past the
    /// first failure is shared by the energy each failing cell would hold past its own limit,
    /// so of two cells on either side of an overloaded face, the one with more such faces
    /// takes more: a punched cell shears out, its neighbours crack.
    /// </summary>
    /// <param name="energy">Strain energy of all sides under the contact forces alone.</param>
    /// <param name="remaining">The impact's energy still to absorb.</param>
    /// <param name="through">Energy that went on through where the impact broke through.</param>
    private static bool Fracture(SideState[] sides, double energy, ref double remaining, ref double through)
    {
        double scale = Math.Sqrt(remaining / energy);

        double first = double.PositiveInfinity;
        foreach (SideState side in sides)
        {
            for (int i = 0; i < side.Unit.Length; i++)
            {
                if (side.CanFail(i))
                    first = Math.Min(first, FailureScale(side, i, out _));
            }
        }

        if (!(first < scale))
            return false;

        double reach = Math.Min(scale, first * GroupSpread);
        List<(SideState Side, int Cell, Mode Mode, double Weight)> group = [];
        double totalWeight = 0.0;
        foreach (SideState side in sides)
        {
            for (int i = 0; i < side.Unit.Length; i++)
            {
                if (!side.CanFail(i))
                    continue;

                double reached = FailureScale(side, i, out Mode mode);
                if (reached > reach)
                    continue;

                double weight = side.UnitEnergy[i] * (scale * scale - reached * reached);
                group.Add((side, i, mode, weight));
                totalWeight += weight;
            }
        }

        // Up to the first failure the impact's energy is stored elastically and given back; only
        // what is left past it can break anything.
        double budget = remaining - first * first * energy;
        double absorbed = 0.0;
        bool broke = false;
        bool flewApart = false;
        foreach (SideState side in sides)
            side.NewlyBroken.Clear();

        foreach ((SideState side, int i, Mode mode, double weight) in group)
        {
            double capacity = Capacity(side.Side, i, mode);
            double toBreak = side.Side.DamageToBreak[i] - side.Damage[i];
            double share = totalWeight > 0.0 ? weight / totalWeight : 1.0 / group.Count;
            double damage = Math.Min(ImpactDamage * budget * share / capacity, toBreak);
            side.Damage[i] += damage;
            absorbed += damage / ImpactDamage * capacity;

            if (damage >= toBreak)
            {
                side.Broken[i] = true;
                side.NewlyBroken.Add(i);
                broke = true;
                flewApart |= side.Side.Free;
            }
        }

        remaining = Math.Max(0.0, remaining - absorbed);
        if (!broke || flewApart)
            return false;

        foreach (SideState side in sides)
        {
            if (side.NewlyBroken.Count == 0)
                continue;

            double pressing = side.MoveContacts();
            through += remaining * (1.0 - pressing);
            remaining *= pressing;
            if (side.Contacts.Count == 0)
            {
                side.Opened = true;
                return false;
            }
        }

        return true;
    }

    /// <summary>The multiple of the contact forces that brings a cell to its elastic limit, and in which mode.</summary>
    private static double FailureScale(SideState side, int i, out Mode mode)
    {
        CellStress unit = side.Unit[i];
        ElasticLimits limits = side.Side.Limits[i];
        CellStress already = side.Side.Static[i];
        mode = Mode.Tension;
        double scale = double.PositiveInfinity;

        Consider(unit.Tension, already.Tension, limits.Tension, Mode.Tension, ref scale, ref mode);
        Consider(unit.Compression, already.Compression, limits.Compression, Mode.Compression, ref scale, ref mode);
        Consider(unit.Shear, already.Shear, limits.Shear, Mode.Shear, ref scale, ref mode);
        return scale;

        static void Consider(double unit, double already, double limit, Mode candidate, ref double scale, ref Mode mode)
        {
            if (!(unit > 0.0) || double.IsPositiveInfinity(limit))
                return;

            double reach = (limit - Math.Min(already, StaticCap * limit)) / unit;
            if (reach < scale)
            {
                scale = reach;
                mode = candidate;
            }
        }
    }

    /// <summary>
    /// Elastic energy a cell stores at its limit in a mode, J: lim² / 2E (G for shear) times
    /// its volume. A granular cell's is always the one in compression: whichever way it starts
    /// to give, it is broken only by being crushed.
    /// </summary>
    private static double Capacity(ImpactSide side, int i, Mode mode)
    {
        CellMaterial material = side.Materials[i];
        ElasticLimits limits = side.Limits[i];
        double volume = side.CellEdge * side.CellEdge * side.CellEdge;
        if (side.Granular[i])
            mode = Mode.Compression;

        (double limit, double modulus) = mode switch
        {
            Mode.Tension => (limits.Tension, material.YoungsModulus),
            Mode.Compression => (limits.Compression, material.YoungsModulus),
            _ => (limits.Shear, material.ShearModulus)
        };

        return limit * limit / (2.0 * modulus) * volume;
    }

    /// <summary>
    /// The side's response to its contact forces alone: the stress of each cell (the lattice's,
    /// with each contact face's own pressure and shear) and the strain energy, which includes
    /// the half cell between each contact face and its cell center the lattice leaves out.
    /// </summary>
    private static void Solve(SideState state)
    {
        ImpactSide side = state.Side;
        List<int> live = [];
        int[] position = new int[side.Cells.Length];
        for (int i = 0; i < side.Cells.Length; i++)
        {
            position[i] = state.Broken[i] ? -1 : live.Count;
            if (!state.Broken[i])
                live.Add(i);
        }

        ivec3[] cells = [.. live.Select(i => side.Cells[i])];
        double[] masses = [.. live.Select(i => side.Masses[i])];
        StressLattice lattice = new
        (
            side.CellEdge,
            cells,
            [.. live.Select(i => side.Materials[i])],
            [.. live.Select(i => side.Anchored[i])]
        );

        Span<dvec3> forces = lattice.Forces;
        Span<dvec3> moments = lattice.Moments;
        foreach (ImpactContact contact in state.Contacts)
        {
            forces[position[contact.Cell]] += contact.Force;
            moments[position[contact.Cell]] += contact.Moment;
        }

        if (side.Free)
            RigidMass.Of(cells, masses, side.CellEdge).Relieve(cells, masses, forces, moments, dvec3.Zero);

        lattice.Solve(MaxIterations, Tolerance);

        double energy = 0.0;
        for (int k = 0; k < cells.Length; k++)
            energy += dvec3.Dot(forces[k], lattice.Displacement(k)) + dvec3.Dot(moments[k], lattice.Rotation(k));
        energy *= 0.5;

        CellStress[] stresses = new CellStress[cells.Length];
        double[] energies = new double[cells.Length];
        lattice.ComputeStresses(stresses);
        lattice.ComputeStrainEnergies(energies);
        Array.Clear(state.Unit);
        Array.Clear(state.UnitEnergy);
        for (int k = 0; k < cells.Length; k++)
        {
            state.Unit[live[k]] = stresses[k];
            state.UnitEnergy[live[k]] = energies[k];
        }

        double half = 0.5 * side.CellEdge;
        foreach (ImpactContact contact in state.Contacts)
        {
            double area = side.CellEdge * side.CellEdge * Math.Clamp(contact.AreaFraction, 1.0 / 9.0, 1.0);
            CellMaterial material = side.Materials[contact.Cell];
            double normal = Math.Max(0.0, dvec3.Dot(contact.Force, contact.Inward));
            double tangential = (contact.Force - dvec3.Dot(contact.Force, contact.Inward) * contact.Inward).Length;
            double face = 0.5 * normal * normal * half / (material.YoungsModulus * area) + 0.5 * tangential * tangential * half / (material.ShearModulus * area);
            energy += face;
            state.UnitEnergy[contact.Cell] += face;

            CellStress stress = state.Unit[contact.Cell];
            state.Unit[contact.Cell] = stress with
            {
                Compression = Math.Max(stress.Compression, normal / area),
                Shear = Math.Max(stress.Shear, 1.5 * tangential / area)
            };
        }

        state.Energy = energy;
    }
}


/// <summary>
/// CPU reference: runs <see cref="ImpactAnalysis"/> on background threads of its own, so an impact is
/// answered within a few steps however long the sustained stress analysis is busy, and a
/// shower of debris landing at once is analysed side by side. Jobs and outcomes cross as
/// messages; nothing is shared.
/// </summary>
internal interface IImpactWorker : IDisposable
{
    void Post(ImpactJob job);
    void Update();
    bool TryTake(out ImpactOutcome outcome);
}

internal sealed class ImpactWorker : IImpactWorker
{
    // Whether this process has compiled the analysis yet; see WarmUp.
    private static int _warmedUp;

    /// <summary>
    /// Runs an impact like a real one several times: a stone cube hitting a stone tower on a
    /// slab hard enough to break it. The solver's code runs slowly until the runtime has
    /// compiled and profiled it, which would make the first impacts of a game late.
    /// </summary>
    private static void WarmUp()
    {
        BlockMaterial stone = Blocks.Get(BlockType.Stone);
        ImpactSide Side(ivec3[] cells, bool free, ivec3 contact, dvec3 inward)
        {
            return new ImpactSide
            (
                -1,
                VoxelBody.CellSize,
                cells,
                [.. cells.Select(_ => stone.Elastic(1.0))],
                [.. cells.Select(_ => stone.Density)],
                [.. cells.Select(c => !free && c.z == 0)],
                free,
                [.. cells.Select(_ => stone.ElasticLimit(1.0))],
                new CellStress[cells.Length],
                [.. cells.Select(_ => stone.DamageToBreak(1.0))],
                new bool[cells.Length],
                [new ImpactContact(Array.IndexOf(cells, contact), 1e6 * inward, dvec3.Zero, inward)]
            );
        }

        IEnumerable<ivec3> slab = TerrainGenerator.Box(new ivec3(12, 12, 4), BlockType.Stone).Select(b => b.Cell);
        IEnumerable<ivec3> tower = TerrainGenerator.Box(new ivec3(2, 2, 8), BlockType.Stone).Select(b => b.Cell + new ivec3(5, 5, 4));
        ivec3[] structure = [.. slab.Concat(tower)];
        ivec3[] cube = [.. TerrainGenerator.Box(new ivec3(2, 2, 2), BlockType.Stone).Select(b => b.Cell)];
        ImpactJob job = new
        (
            -1,
            5e5,
            [Side(structure, false, new ivec3(5, 5, 11), new dvec3(1, 0, 0)), Side(cube, true, new ivec3(1, 0, 0), new dvec3(-1, 0, 0))]
        );

        for (int i = 0; i < 8; i++)
            ImpactAnalysis.Run(job);
    }

    public ImpactWorker()
    {
        int count = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        _threads = [.. Enumerable.Range(0, count).Select(i => new Thread(Run) { IsBackground = true, Name = $"Impact analysis {i}" })];
        foreach (Thread thread in _threads)
            thread.Start();
    }

    private readonly Channel<ImpactJob> _inbox = Channel.CreateUnbounded<ImpactJob>();
    private readonly Channel<ImpactOutcome> _outbox = Channel.CreateUnbounded<ImpactOutcome>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread[] _threads;

    public void Post(ImpactJob job)
    {
        _inbox.Writer.TryWrite(job);
    }

    public void Update() { }

    public bool TryTake(out ImpactOutcome outcome)
    {
        return _outbox.Reader.TryRead(out outcome!);
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _warmedUp, 1) == 0)
            WarmUp();

        while (_inbox.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_inbox.Reader.TryRead(out ImpactJob? job))
                _outbox.Writer.TryWrite(ImpactAnalysis.Run(job));
        }
    }

    public void Dispose()
    {
        _inbox.Writer.TryComplete();
        foreach (Thread thread in _threads)
            thread.Join();
    }
}
