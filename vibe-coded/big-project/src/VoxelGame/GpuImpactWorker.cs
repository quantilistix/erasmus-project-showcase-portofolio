using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Structural;

namespace VoxelGame;

/// <summary>
/// GPU impact response and failure-energy allocation. The CPU coordinates rounds,
/// advances contacts through broken topology and assembles fragmentation commands.
/// Per-cell pressure, strain energy and damage allocation stay on the device.
/// </summary>
internal sealed class GpuImpactWorker(Func<double, GpuStressLattice> create, Func<GpuBlastSimulation> simulation) : IImpactWorker
{
    private sealed class Job(ImpactJob input)
    {
        public readonly ImpactJob Input = input;
        public readonly ImpactAnalysis.SideState[] Sides = [.. input.Sides.Select(s => new ImpactAnalysis.SideState(s))];
        public readonly GpuStressLattice[] Lattices = new GpuStressLattice[input.Sides.Length];
        public readonly bool[] Finished = new bool[input.Sides.Length];
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        public double Remaining = input.Energy, Through;
        public int Rounds;
        public bool Prepared;
    }
    private readonly Queue<ImpactJob> _pending = [];
    private readonly List<Job> _active = [];
    private readonly Queue<ImpactOutcome> _results = [];
    private readonly Dictionary<double, Stack<GpuStressLattice>> _idle = [];

    public void Post(ImpactJob job)
    {
        if (job.Sides.Length is < 1 or > 2) throw new ArgumentException("An impact must have one or two sides", nameof(job));
        _pending.Enqueue(job);
    }

    public void Update()
    {
        while (_active.Count < 4 && _pending.TryDequeue(out ImpactJob? input)) _active.Add(new(input));
        foreach (Job job in _active.ToArray())
        {
            if (!job.Prepared)
            {
                if (!Prepare(job)) Finish(job);
                continue;
            }
            bool complete = true;
            for (int i = 0; i < job.Lattices.Length; i++)
            {
                if (job.Finished[i]) continue;
                GpuStressLattice lattice = job.Lattices[i];
                if (!lattice.Update(submit: false)) { complete = false; continue; }
                if (lattice.Converged || lattice.Iterations >= 200) job.Finished[i] = true;
                else { lattice.Update(); complete = false; }
            }
            if (!complete) continue;
            if (Fracture(job)) job.Prepared = false;
            else Finish(job);
        }
    }

    private bool Prepare(Job job)
    {
        if (job.Rounds >= 4 || !(job.Remaining > 0)) return false;
        job.Rounds++;
        foreach (ImpactAnalysis.SideState state in job.Sides)
        {
            if (state.Side.Cells.Length == 0) return false;
            if (!state.Side.Free && !state.ContactsHeld()) { state.Opened = true; return false; }
        }
        for (int sideIndex = 0; sideIndex < job.Sides.Length; sideIndex++)
        {
            ImpactAnalysis.SideState state = job.Sides[sideIndex];
            ImpactSide side = state.Side;
            List<int> live = [];
            int[] position = new int[side.Cells.Length];
            for (int i = 0; i < position.Length; i++)
            {
                position[i] = state.Broken[i] ? -1 : live.Count;
                if (!state.Broken[i]) live.Add(i);
            }
            if (live.Count == 0) return false;
            if (job.Lattices[sideIndex] == null)
            {
                if (!_idle.TryGetValue(side.CellEdge, out var pool)) _idle[side.CellEdge] = pool = [];
                GpuStressLattice lattice = pool.TryPop(out var available) ? available : create(side.CellEdge);
                lattice.ReadBackStresses = false;
                lattice.Tolerance = 1e-3;
                lattice.IterationsPerUpdate = 16;
                job.Lattices[sideIndex] = lattice;
            }
            GpuStressLattice solver = job.Lattices[sideIndex];
            ivec3[] cells = [.. live.Select(i => side.Cells[i])];
            double[] masses = [.. live.Select(i => side.Masses[i])];
            solver.SetCells(cells, [.. live.Select(i => side.Materials[i])], masses, [.. live.Select(i => side.Anchored[i])]);
            dvec3[] forces = new dvec3[cells.Length], moments = new dvec3[cells.Length];
            foreach (ImpactContact contact in state.Contacts)
            {
                int i = position[contact.Cell];
                if (i < 0) continue;
                forces[i] += contact.Force; moments[i] += contact.Moment;
            }
            if (side.Free) RigidMass.Of(cells, masses, side.CellEdge).Relieve(cells, masses, forces, moments, dvec3.Zero);
            var loads = new (ivec3, dvec3, dvec3)[cells.Length];
            for (int i = 0; i < loads.Length; i++) loads[i] = (cells[i], forces[i], moments[i]);
            solver.SetLoads(dvec3.Zero, loads);
            job.Finished[sideIndex] = false;
            solver.Update();
        }
        job.Prepared = true;
        return true;
    }

    private bool Fracture(Job job)
    {
        var outcome = simulation().EvaluateImpact(job.Sides, job.Lattices, job.Remaining);
        if (!outcome.Damaged) return false;
        bool broke = false, flewApart = false;
        int index = 0;
        foreach (ImpactAnalysis.SideState state in job.Sides)
        {
            state.NewlyBroken.Clear();
            for (int i = 0; i < state.Damage.Length; i++)
            {
                double delta = outcome.Damage[index++];
                if (!(delta > 0)) continue;
                double toBreak = state.Side.DamageToBreak[i] - state.Damage[i];
                state.Damage[i] += delta;
                if (delta >= toBreak)
                {
                    state.Broken[i] = true; state.NewlyBroken.Add(i);
                    broke = true; flewApart |= state.Side.Free;
                }
            }
        }
        job.Remaining = outcome.Remaining;
        if (!broke || flewApart) return false;
        foreach (ImpactAnalysis.SideState state in job.Sides)
        {
            if (state.NewlyBroken.Count == 0) continue;
            double pressing = state.MoveContacts();
            job.Through += job.Remaining * (1 - pressing);
            job.Remaining *= pressing;
            if (state.Contacts.Count == 0) { state.Opened = true; return false; }
        }
        return true;
    }

    private void Finish(Job job)
    {
        ImpactSideOutcome[] sides = new ImpactSideOutcome[job.Sides.Length];
        for (int i = 0; i < sides.Length; i++)
        {
            ImpactAnalysis.SideState state = job.Sides[i];
            List<(ivec3, double)> damage = [];
            for (int cell = 0; cell < state.Damage.Length; cell++)
                if (state.Damage[cell] > 0) damage.Add((state.Side.Cells[cell], state.Damage[cell]));
            sides[i] = new(state.Side.BodyId, [.. damage], state.Opened);
            if (job.Lattices[i] is GpuStressLattice lattice) _idle[lattice.CellSize].Push(lattice);
        }
        _results.Enqueue(new(job.Input.Id, sides, job.Remaining + job.Through, job.Rounds, job.Watch.Elapsed.TotalMilliseconds));
        _active.Remove(job);
    }

    public bool TryTake(out ImpactOutcome outcome) => _results.TryDequeue(out outcome!);
    public void Dispose()
    {
        foreach (Job job in _active) foreach (GpuStressLattice? lattice in job.Lattices) lattice?.Dispose();
        foreach (var pool in _idle.Values) foreach (GpuStressLattice lattice in pool) lattice.Dispose();
        _pending.Clear(); _active.Clear(); _idle.Clear(); _results.Clear();
    }
}
