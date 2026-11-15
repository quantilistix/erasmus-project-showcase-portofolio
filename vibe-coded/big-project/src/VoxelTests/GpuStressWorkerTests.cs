using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Structural;
using VoxelGame;

namespace VoxelTests;

internal static class GpuStressWorkerTests
{
    internal static bool Run()
    {
        var gpu = TestGpu.Vulkan;
        using GpuStressLattice kernels = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, Path.Combine(TestGpu.Shaders, "structural"));
        using GpuStressWorker worker = new(() => new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, Path.Combine(TestGpu.Shaders, "structural"), sharePipelinesWith: kernels));
        StressResult? Await(int id, int version, StressLoads? streaming = null)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 15)
            {
                if (streaming != null) worker.Post(streaming);
                worker.Update();
                while (worker.TryTake(out StressResult result))
                    if (result.BodyId == id && result.Snapshot.Version == version && result.Snapshot.Settled) return result;
                Thread.Sleep(1);
            }
            return null;
        }
        ivec3[] cells = Enumerable.Range(0, 8).Select(x => new ivec3(x, 0, 0)).ToArray();
        CellMaterial[] materials = Enumerable.Repeat(new CellMaterial(50e9, 20e9), cells.Length).ToArray();
        double[] masses = Enumerable.Repeat(2600.0, cells.Length).ToArray();
        bool[] anchors = cells.Select(c => c.x == 0).ToArray();
        StressTopology topology = new(40, 1, cells, materials, masses, anchors, false);
        StressLoads loads = new(40, 1, new(0, 0, -9.81), dvec3.Zero, []);
        worker.Post(topology); worker.Post(loads);
        StressResult? anchored = Await(40, 1, loads);
        StressLattice cpu = new(1, cells, materials, anchors);
        for (int i = 0; i < cells.Length; i++) cpu.Forces[i] = masses[i] * loads.Gravity;
        cpu.Solve(200, 1e-5);
        CellStress[] expected = new CellStress[cells.Length]; cpu.ComputeStresses(expected);
        double error = anchored == null ? double.PositiveInfinity : anchored.Value.Snapshot.Stresses.Zip(expected).Max(pair =>
            Math.Max(Math.Abs(pair.First.Tension - pair.Second.Tension), Math.Abs(pair.First.Compression - pair.Second.Compression)) / Math.Max(1, Math.Max(pair.Second.Tension, pair.Second.Compression)));
        bool ok = error < .005;
        Console.WriteLine($"   anchored object, continuous loads: GPU/CPU stress error {error:P3}");
        worker.Post(topology with { BodyId = 41, Version = 2, Anchored = new bool[cells.Length], Free = true });
        worker.Post(loads with { BodyId = 41, Version = 2 });
        StressResult? falling = Await(41, 2);
        ok &= falling != null && falling.Value.Snapshot.Stresses.All(s => Math.Max(s.Tension, Math.Max(s.Compression, s.Shear)) < .1);
        Console.WriteLine($"   free fall has zero internal stress: {falling != null && falling.Value.Snapshot.Stresses.All(s => s.Tension < .1 && s.Compression < .1 && s.Shear < .1)}");
        worker.Post(topology with { Version = 3, Cells = [new(0)], Materials = [materials[0]], Masses = [2600], Anchored = [false], Free = true });
        StressResult? single = Await(40, 3);
        ok &= single is { Solve.Iterations: 0 } && single.Value.Snapshot.Stresses.SequenceEqual(new CellStress[1]);
        worker.Post(new StressRemove(41));
        worker.Post(topology with { BodyId = 41, Version = 4 });
        StressResult? recreated = Await(41, 4);
        ok &= recreated is { Snapshot.Settled: true };
        Console.WriteLine($"   topology shrink, removal/recreation and versioned results: {single != null && recreated != null}");
        ok &= gpu.ValidationErrorCount == 0;
        Console.WriteLine($"   Vulkan validation errors: {gpu.ValidationErrorCount}");
        return ok;
    }
}
