using System.Diagnostics;
using System.Threading.Channels;

using GlmSharp;

using MYT.Kit.Structural;

namespace VoxelGame;


internal abstract record StressMessage(int BodyId);

/// <summary>A body's cells changed: a new lattice is built, starting from the old solution where cells coincide.</summary>
/// <param name="Free">Moves freely (an object), so its loads are balanced by inertia relief; otherwise its anchored cells hold it.</param>
internal sealed record StressTopology
(
    int BodyId,
    int Version,
    ivec3[] Cells,
    CellMaterial[] Materials,
    double[] Masses,
    bool[] Anchored,
    bool Free,
    double CellEdge = VoxelBody.CellSize,
    double Tolerance = 1e-5
) : StressMessage(BodyId);

/// <summary>Cells softened by damage.</summary>
internal sealed record StressMaterials(int BodyId, int Version, (ivec3 Cell, CellMaterial Material)[] Changes) : StressMessage(BodyId);

/// <summary>
/// The loads on a body in its own frame: gravity, its angular velocity (for the centrifugal
/// and gyroscopic part of inertia relief) and the contact force and moment on each touching cell.
/// </summary>
internal sealed record StressLoads
(
    int BodyId,
    int Version,
    dvec3 Gravity,
    dvec3 AngularVelocity,
    (ivec3 Cell, dvec3 Force, dvec3 Moment)[] Contacts
) : StressMessage(BodyId);

internal sealed record StressRemove(int BodyId) : StressMessage(BodyId);

internal readonly record struct StressResult(int BodyId, StressSnapshot Snapshot, LatticeSolveResult Solve, double Milliseconds);

internal interface IStressWorker : IDisposable
{
    void Post(StressMessage message);
    void Update();
    bool TryTake(out StressResult result);
}


/// <summary>
/// CPU reference: runs the structural analysis of every body on a background thread. The game posts what
/// changed (cells, damage, loads); the worker keeps one <see cref="StressLattice"/> per body,
/// re-solves bodies whose inputs changed, longest waiting first, a limited number of
/// iterations at a time so a large map does not hold up the objects, and posts stress
/// snapshots back. Nothing is shared: all state crosses as messages.
/// </summary>
internal sealed class StressWorker : IStressWorker
{
    private const int IterationsPerRound = 30;
    // Publish occasional unfinished progress, while continuing the same CG search.
    private const int ProgressRounds = 6;

    public StressWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Stress analysis", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private readonly Channel<StressMessage> _inbox = Channel.CreateUnbounded<StressMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<StressResult> _outbox = Channel.CreateUnbounded<StressResult>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Thread _thread;
    private volatile bool _stopping;

    public void Post(StressMessage message)
    {
        _inbox.Writer.TryWrite(message);
    }

    public bool TryTake(out StressResult result)
    {
        return _outbox.Reader.TryRead(out result);
    }

    public void Update() { } // The reference backend advances on its own thread.

    /// <summary>The analysis state of one body, owned by the worker thread.</summary>
    private sealed class Body
    {
        public StressTopology? Topology;
        public StressTopology? PendingTopology;
        public StressLattice? Lattice;
        public StressLoads? Loads;
        public StressLoads? PendingLoads;
        public double Milliseconds;
        public readonly List<(ivec3 Cell, CellMaterial Material)> PendingMaterials = [];
        public RigidMass Mass;
        public bool Dirty;
        public long WaitingSince;
        public int Rounds;
        public bool Queued;
        public bool Removed;
        public long Order;

        public void MarkDirty()
        {
            if (!Dirty)
                WaitingSince = Stopwatch.GetTimestamp();

            Dirty = true;
            Rounds = 0;
        }
    }

    private void Run()
    {
        Dictionary<int, Body> bodies = [];
        PriorityQueue<Body, (long WaitingSince, long Order)> waiting = new();
        long nextOrder = 0;

        void Schedule(Body body)
        {
            if (body.Dirty && !body.Queued && !body.Removed)
            {
                body.Queued = true;
                waiting.Enqueue(body, (body.WaitingSince, body.Order));
            }
        }

        while (!_stopping)
        {
            if (waiting.Count == 0)
            {
                if (!_inbox.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    return;
            }

            while (_inbox.Reader.TryRead(out StressMessage? message))
            {
                if (message is StressRemove)
                {
                    // An old queued entry must never address a body reusing this ID.
                    if (bodies.Remove(message.BodyId, out Body? removed))
                        removed.Removed = true;
                    continue;
                }

                if (!bodies.TryGetValue(message.BodyId, out Body? body))
                {
                    body = new Body { Order = nextOrder++ };
                    bodies.Add(message.BodyId, body);
                }
                Receive(body, message);
                Schedule(body);
            }

            if (waiting.TryDequeue(out Body? next, out _))
            {
                next.Queued = false;
                if (next.Removed)
                    continue;
                Solve(next);
                Schedule(next); // An unfinished solve rejoins at its new waiting time.
            }
        }
    }

    private static void Receive(Body body, StressMessage message)
    {
        switch (message)
        {
            case StressTopology topology:
                body.PendingTopology = topology;
                body.PendingMaterials.Clear();
                body.MarkDirty();
                break;

            case StressMaterials materials when materials.Version == (body.PendingTopology ?? body.Topology)?.Version:
                body.PendingMaterials.AddRange(materials.Changes);
                body.MarkDirty();
                break;

            case StressLoads loads when loads.Version == (body.PendingTopology ?? body.Topology)?.Version:
                body.PendingLoads = loads;
                // Keep solving one immutable load snapshot. Streaming contacts must
                // not restart every slice before a large body can converge.
                if (!body.Dirty) body.MarkDirty();
                break;
        }
    }

    private void Solve(Body body)
    {
        Stopwatch watch = Stopwatch.StartNew();
        // CellStress is computed only across cell-to-cell interfaces. A singleton has
        // none, regardless of external loads, anchors, material or inertia relief.
        // Keep the topology/version lifecycle, but avoid constructing an empty graph.
        StressTopology? singleton = body.PendingTopology ?? body.Topology;
        if (singleton?.Cells.Length == 1)
        {
            body.Topology = singleton;
            body.PendingTopology = null;
            body.Lattice = null;
            body.PendingMaterials.Clear();
            body.Dirty = false;
            body.Rounds = 0;
            _outbox.Writer.TryWrite(new StressResult(singleton.BodyId,
                new StressSnapshot(singleton.Version, singleton.Cells, [default], Settled: true),
                new LatticeSolveResult(0, 0, true), watch.Elapsed.TotalMilliseconds));
            return;
        }
        if (body.PendingTopology != null)
            Rebuild(body);

        StressTopology? topology = body.Topology;
        StressLattice? lattice = body.Lattice;
        if (topology == null || lattice == null)
        {
            body.Dirty = false;
            return;
        }

        foreach ((ivec3 cell, CellMaterial material) in body.PendingMaterials)
        {
            int index = lattice.IndexOf(cell);
            if (index >= 0)
                lattice.SetMaterial(index, material);
        }

        body.PendingMaterials.Clear();
        if (body.Rounds == 0)
        {
            body.Milliseconds = 0;
            if (body.PendingLoads != null) { body.Loads = body.PendingLoads; body.PendingLoads = null; }
            ApplyLoads(body, topology, lattice);
        }
        int iterations = Math.Clamp(30000 / Math.Max(1, lattice.CellCount), 1, IterationsPerRound);
        LatticeSolveResult result = lattice.Solve(iterations, topology.Tolerance,
            refreshPreconditioner: lattice.PreconditionerStale, continuePrevious: body.Rounds > 0);

        body.Milliseconds += watch.Elapsed.TotalMilliseconds;
        body.Rounds++;
        body.Dirty = !result.Converged;
        body.WaitingSince = Stopwatch.GetTimestamp();
        if (!result.Converged && body.Rounds % ProgressRounds != 0) return;

        CellStress[] stresses = new CellStress[lattice.CellCount];
        lattice.ComputeStresses(stresses);
        // A visible approximation is never permission to fracture a structure.
        _outbox.Writer.TryWrite(new StressResult(topology.BodyId,
            new StressSnapshot(topology.Version, topology.Cells, stresses, Settled: result.Converged), result, body.Milliseconds));
        if (result.Converged && body.PendingLoads != null)
            body.MarkDirty();
    }

    private static void Rebuild(Body body)
    {
        StressTopology topology = body.PendingTopology!;
        body.PendingTopology = null;
        body.Topology = topology;

        StressLattice lattice = new(topology.CellEdge, topology.Cells, topology.Materials, topology.Anchored);
        if (body.Lattice != null)
            lattice.CopySolution(body.Lattice);
        body.Lattice = lattice;

        body.Mass = RigidMass.Of(topology.Cells, topology.Masses, topology.CellEdge);
    }

    /// <summary>
    /// Gravity on every cell plus the contact loads. A free body is also loaded by its own
    /// inertia (<see cref="RigidMass.Relieve"/>), so its loads balance exactly, as they do on a
    /// real body accelerating freely: a falling object feels no stress.
    /// </summary>
    private static void ApplyLoads(Body body, StressTopology topology, StressLattice lattice)
    {
        StressLoads? loads = body.Loads?.Version == topology.Version ? body.Loads : null;
        dvec3 gravity = loads?.Gravity ?? new dvec3(0, 0, -9.81);
        Span<dvec3> forces = lattice.Forces;
        Span<dvec3> moments = lattice.Moments;

        for (int i = 0; i < lattice.CellCount; i++)
        {
            forces[i] = topology.Masses[i] * gravity;
            moments[i] = dvec3.Zero;
        }

        if (loads != null)
        {
            foreach ((ivec3 cell, dvec3 force, dvec3 moment) in loads.Contacts)
            {
                int index = lattice.IndexOf(cell);
                if (index < 0)
                    continue;

                forces[index] += force;
                moments[index] += moment;
            }
        }

        if (topology.Free)
            body.Mass.Relieve(topology.Cells, topology.Masses, forces, moments, loads?.AngularVelocity ?? dvec3.Zero);
    }

    public void Dispose()
    {
        _stopping = true;
        _inbox.Writer.TryComplete();
        _thread.Join();
    }
}
