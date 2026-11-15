using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Structural;

using VoxelGame;

namespace VoxelTests;

internal static class StressWorkerTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("stress-scheduling", Scheduling),
        ("stress-single-cell", SingleCell),
        ("stress-sliced-solve", SlicedSolve),
        ("stress-streaming-loads", StreamingLoads)
        ,("gpu-object-stress", GpuStressWorkerTests.Run)
    ];

    private static bool Scheduling()
    {
        const int count = 1000;
        using StressWorker worker = new();
        StressTopology[] topologies = new StressTopology[count];
        CellStress[][] expected = new CellStress[4][];
        for (int length = 1; length <= 4; length++)
        {
            ivec3[] cells = Enumerable.Range(0, length).Select(z => new ivec3(0, 0, z)).ToArray();
            CellMaterial[] materials = Enumerable.Repeat(new CellMaterial(50e9, 20e9), length).ToArray();
            bool[] anchors = Enumerable.Range(0, length).Select(z => z == 0).ToArray();
            StressLattice lattice = new(VoxelBody.CellSize, cells, materials, anchors);
            for (int c = 0; c < length; c++)
                lattice.Forces[c] = new dvec3(0, 0, -9.81);
            for (int round = 0; round < 6; round++)
                if (lattice.Solve(30, 1e-5, refreshPreconditioner: lattice.PreconditionerStale).Converged)
                    break;
            expected[length - 1] = new CellStress[length];
            lattice.ComputeStresses(expected[length - 1]);
            for (int i = length - 1; i < count; i += 4)
                topologies[i] = new StressTopology(i, 1, cells, materials,
                    Enumerable.Repeat(1.0, length).ToArray(), anchors, Free: false);
        }

        bool Await(int version)
        {
            HashSet<int> received = [];
            int mismatches = 0;
            Stopwatch watch = Stopwatch.StartNew();
            while (received.Count < count && watch.Elapsed.TotalSeconds < 30)
            {
                while (worker.TryTake(out StressResult result))
                {
                    if (result.Snapshot.Version != version)
                        continue;
                    received.Add(result.BodyId);
                    if (!result.Snapshot.Settled
                        || !result.Snapshot.Stresses.SequenceEqual(expected[result.BodyId % 4]))
                        mismatches++;
                }
                if (received.Count < count)
                    Thread.Sleep(1);
            }
            Console.WriteLine($"   version {version}: {received.Count}/{count} bodies completed in {watch.Elapsed.TotalMilliseconds:F1} ms, {mismatches} differing stress results");
            return received.Count == count && mismatches == 0;
        }

        foreach (StressTopology topology in topologies)
            worker.Post(topology);
        bool ok = Await(1);
        // Dirty old bodies can remain queued when removed. A new lifetime with the same
        // ID must receive its own solve, and the removed work must never overwrite it.
        foreach (StressTopology topology in topologies.Reverse())
        {
            worker.Post(new StressLoads(topology.BodyId, 1, new dvec3(0, 0, -19.62),
                dvec3.Zero, []));
            worker.Post(new StressRemove(topology.BodyId));
            worker.Post(topology with { Version = 2 });
        }
        return Await(2) && ok;
    }

    private static bool SlicedSolve()
    {
        ivec3[] cells = Enumerable.Range(0, 256).Select(x => new ivec3(x, 0, 0)).ToArray();
        var materials = Enumerable.Repeat(new CellMaterial(50e9, 20e9), cells.Length).ToArray();
        bool[] anchors = cells.Select(c => c.x == 0).ToArray();
        StressLattice whole = new(1, cells, materials, anchors), sliced = new(1, cells, materials, anchors);
        for (int i = 0; i < cells.Length; i++) whole.Forces[i] = sliced.Forces[i] = new dvec3(0, 0, -2600 * 9.81);
        LatticeSolveResult expected = whole.Solve(300, 1e-5), result = default;
        int total = 0;
        while (total < 300)
        {
            result = sliced.Solve(1, 1e-5, continuePrevious: true);
            total += result.Iterations;
            if (result.Converged) break;
        }
        CellStress[] first = new CellStress[cells.Length], second = new CellStress[cells.Length];
        whole.ComputeStresses(first); sliced.ComputeStresses(second);
        double error = first.Zip(second).Max(p => Math.Abs(p.First.Tension - p.Second.Tension) / Math.Max(1, p.First.Tension));
        bool ok = expected.Converged && result.Converged && total > 1 && total == expected.Iterations && error < 1e-10;
        // A changed load/material invalidates the old search direction even when a
        // caller requests continuation; compare against a fresh full solve.
        sliced.Forces[^1] *= 2; whole.Forces[^1] *= 2;
        result = sliced.Solve(300, 1e-5, continuePrevious:true);
        expected = whole.Solve(300, 1e-5);
        sliced.ComputeStresses(second); whole.ComputeStresses(first);
        ok &= expected.Converged && result.Converged && first.Zip(second).All(p => Math.Abs(p.First.Tension - p.Second.Tension) < 1e-5 * Math.Max(1, p.First.Tension));
        Console.WriteLine($"   whole/sliced solve: {total} iterations, stress error {error:E2}; changed loads: {(ok ? "ok" : "BAD")}");
        return ok;
    }

    private static bool StreamingLoads()
    {
        using StressWorker worker = new();
        ivec3[] cells = (from x in Enumerable.Range(0, 48) from y in Enumerable.Range(0, 12)
                        from z in Enumerable.Range(0, 2) select new ivec3(x, y, z)).ToArray();
        worker.Post(new StressTopology(75, 1, cells,
            Enumerable.Repeat(new CellMaterial(50e9, 20e9), cells.Length).ToArray(),
            Enumerable.Repeat(2600.0, cells.Length).ToArray(), cells.Select(c => c.x == 0).ToArray(), false));
        Stopwatch clock = Stopwatch.StartNew();
        bool settled = false, falseSettled = false;
        while (!settled && clock.Elapsed.TotalSeconds < 15)
        {
            worker.Post(new StressLoads(75, 1, new dvec3(0, 0, -9.81), dvec3.Zero, []));
            while (worker.TryTake(out StressResult result))
            {
                falseSettled |= result.Snapshot.Settled && !result.Solve.Converged;
                settled |= result.Snapshot.Settled && result.Snapshot.Stresses.Max(s => s.Tension) > 1e6;
            }
            Thread.Sleep(1);
        }
        Console.WriteLine($"   continuous contact messages: settled={settled}, falsely settled={falseSettled}, {clock.Elapsed.TotalMilliseconds:F0} ms");
        return settled && !falseSettled;
    }

    private static bool SingleCell()
    {
        using StressWorker worker = new();
        StressResult? Await(int version)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 5)
            {
                while (worker.TryTake(out StressResult result))
                    if (result.Snapshot.Version == version)
                        return result;
                Thread.Sleep(1);
            }
            return null;
        }
        StressTopology single = new(71, 1, [new ivec3(5, -2, 3)],
            [new CellMaterial(50e9, 20e9)], [2600.0], [false], Free: true);
        worker.Post(single);
        worker.Post(new StressLoads(71, 1, new dvec3(3, -7, -100), new dvec3(20, -30, 40),
            [(single.Cells[0], new dvec3(1e8, -2e8, 3e8), new dvec3(-4e8, 5e8, 6e8))]));
        StressResult? first = Await(1);
        bool ok = first is { Snapshot.Settled: true, Solve.Iterations: 0 }
            && first.Value.Snapshot.Stresses.SequenceEqual(new CellStress[1]);

        // Building onto a single cell must resume a real multi-cell solve.
        StressTopology pair = new(71, 2, [new ivec3(0), new ivec3(0, 0, 1)],
            [new CellMaterial(50e9, 20e9), new CellMaterial(50e9, 20e9)],
            [1.0, 1.0], [true, false], Free: false);
        worker.Post(pair);
        StressResult? second = Await(2);
        ok &= second is { Snapshot.Settled: true }
            && second.Value.Snapshot.Stresses.Any(s => s.Compression > 9.0);

        // Removing that neighbour must discard the old lattice and publish zero again.
        worker.Post(single with { Version = 3, Anchored = [true], Free = false });
        worker.Post(new StressMaterials(71, 3, [(single.Cells[0], new CellMaterial(1e6, 4e5))]));
        StressResult? third = Await(3);
        ok &= third is { Snapshot.Settled: true, Solve.Iterations: 0 }
            && third.Value.Snapshot.Stresses.SequenceEqual(new CellStress[1]);
        Console.WriteLine($"   single-cell arbitrary loads, growth to a stressed pair and shrink: {(ok ? "ok" : "BAD")}");
        return ok;
    }
}
