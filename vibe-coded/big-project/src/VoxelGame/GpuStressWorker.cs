using GlmSharp;
using MYT.Kit.Structural;

namespace VoxelGame;

/// <summary>
/// Object material stresses use the same GPU RBSM/CG solver as the terrain.
/// Each body's state remains on the device; kernels are shared across bodies.
/// Update polls fences and schedules bounded iteration slices on the owning thread.
/// The CPU prepares external loads and receives inspection/fracture snapshots.
/// </summary>
internal sealed class GpuStressWorker(Func<double, GpuStressLattice> create) : IStressWorker
{
    public GpuStressWorker(Func<GpuStressLattice> create) : this(_ => create()) { }
    private sealed class Body
    {
        public StressTopology? Topology, PendingTopology;
        public StressLoads? Loads, PendingLoads;
        public readonly List<(ivec3 Cell, CellMaterial Material)> Materials = [];
        public GpuStressLattice? Lattice;
        public RigidMass Mass;
        public bool Solving, Queued, Removed;
        public int Rounds;
        public double Milliseconds;
    }
    private readonly Dictionary<int, Body> _bodies = [];
    private readonly Queue<Body> _waiting = [];
    private readonly Queue<StressResult> _results = [];

    public void Post(StressMessage message)
    {
        if (message is StressRemove)
        {
            if (_bodies.Remove(message.BodyId, out Body? removed))
            {
                removed.Removed = true;
                removed.Lattice?.Dispose();
            }
            return;
        }
        if (!_bodies.TryGetValue(message.BodyId, out Body? body)) _bodies[message.BodyId] = body = new();
        switch (message)
        {
            case StressTopology topology:
                body.PendingTopology = topology;
                body.Materials.Clear();
                break;
            case StressMaterials materials when materials.Version == (body.PendingTopology ?? body.Topology)?.Version:
                body.Materials.AddRange(materials.Changes);
                break;
            case StressLoads loads when loads.Version == (body.PendingTopology ?? body.Topology)?.Version:
                body.PendingLoads = loads;
                break;
            default: return;
        }
        // An isolated cell has no internal interfaces and identically zero stress.
        if ((body.PendingTopology ?? body.Topology) is { Cells.Length: <= 1 } single)
        {
            bool changed = body.PendingTopology != null;
            body.Topology = single; body.PendingTopology = null; body.PendingLoads = null;
            body.Materials.Clear(); body.Solving = false;
            body.Lattice?.Dispose(); body.Lattice = null;
            if (changed) _results.Enqueue(new(single.BodyId, new(single.Version, single.Cells, new CellStress[single.Cells.Length], true), new(0, 0, true), 0));
            return;
        }
        Schedule(body);
    }

    private void Schedule(Body body)
    {
        if (body.Queued || body.Removed) return;
        body.Queued = true; _waiting.Enqueue(body);
    }

    public void Update()
    {
        int turns = Math.Min(8, _waiting.Count);
        while (turns-- > 0)
        {
            Body body = _waiting.Dequeue(); body.Queued = false;
            if (body.Removed) continue;
            Advance(body);
            if (body.Solving || body.PendingTopology != null || body.PendingLoads != null || body.Materials.Count > 0) Schedule(body);
        }
    }

    private void Advance(Body body)
    {
        GpuStressLattice lattice = body.Lattice ??= create((body.PendingTopology ?? body.Topology)!.CellEdge);
        bool changed = false;
        if (body.PendingTopology is StressTopology topology)
        {
            body.Topology = topology; body.PendingTopology = null;
            lattice.SetCells(topology.Cells, topology.Materials, topology.Masses, topology.Anchored);
            lattice.IterationsPerUpdate = Math.Clamp(30000 / Math.Max(1, topology.Cells.Length), 1, 8);
            lattice.Tolerance = topology.Tolerance;
            body.Mass = RigidMass.Of(topology.Cells, topology.Masses, topology.CellEdge);
            body.Solving = false;
            changed = true;
        }
        if (body.Topology == null) return;
        foreach (var (cell, material) in body.Materials)
        {
            int i = lattice.IndexOf(cell);
            if (i >= 0) { lattice.SetMaterial(i, material); changed = true; }
        }
        body.Materials.Clear();
        if (!body.Solving && (changed || body.PendingLoads != null))
        {
            if (body.PendingLoads != null) { body.Loads = body.PendingLoads; body.PendingLoads = null; }
            ApplyLoads(body, lattice);
            body.Milliseconds = 0; body.Rounds = 0; body.Solving = true;
        }
        else if (changed) { body.Rounds = 0; body.Milliseconds = 0; body.Solving = true; }
        if (!body.Solving) return;
        bool arrived = lattice.Update();
        if (!arrived || !lattice.HasResults) return;
        body.Rounds++;
        body.Milliseconds += lattice.GpuMilliseconds;
        bool settled = lattice.Converged;
        if (settled || body.Rounds % 6 == 0)
        {
            var stresses = new CellStress[body.Topology.Cells.Length];
            lattice.CopyStresses(stresses);
            _results.Enqueue(new(body.Topology.BodyId,
                new(body.Topology.Version, body.Topology.Cells, stresses, settled),
                new(lattice.Iterations, lattice.RelativeResidual, settled), body.Milliseconds));
        }
        if (settled) body.Solving = false;
    }

    private static void ApplyLoads(Body body, GpuStressLattice lattice)
    {
        StressTopology topology = body.Topology!;
        StressLoads? loads = body.Loads?.Version == topology.Version ? body.Loads : null;
        dvec3 gravity = loads?.Gravity ?? new dvec3(0, 0, -9.81);
        if (!topology.Free) { lattice.SetLoads(gravity, loads?.Contacts ?? []); return; }
        dvec3[] forces = new dvec3[topology.Cells.Length], moments = new dvec3[forces.Length];
        Dictionary<ivec3, int> index = [];
        for (int i = 0; i < forces.Length; i++) { forces[i] = topology.Masses[i] * gravity; index[topology.Cells[i]] = i; }
        if (loads != null)
            foreach (var (cell, force, moment) in loads.Contacts)
                if (index.TryGetValue(cell, out int i)) { forces[i] += force; moments[i] += moment; }
        body.Mass.Relieve(topology.Cells, topology.Masses, forces, moments, loads?.AngularVelocity ?? dvec3.Zero);
        var balanced = new (ivec3, dvec3, dvec3)[forces.Length];
        for (int i = 0; i < balanced.Length; i++) balanced[i] = (topology.Cells[i], forces[i], moments[i]);
        lattice.SetLoads(dvec3.Zero, balanced);
    }

    public bool TryTake(out StressResult result) => _results.TryDequeue(out result);
    public void Dispose()
    {
        foreach (Body body in _bodies.Values) body.Lattice?.Dispose();
        _bodies.Clear(); _waiting.Clear(); _results.Clear();
    }
}
