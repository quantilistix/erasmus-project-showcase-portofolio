using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Structural;
using VoxelGame;

namespace VoxelTests;

internal static partial class ImpactTests
{
    private static bool GpuAnalysis()
    {
        var gpu = TestGpu.Vulkan;
        using GpuStressLattice kernels = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, Path.Combine(TestGpu.Shaders, "structural"));
        using GpuBlastSimulation simulation = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
        using GpuImpactWorker worker = new(edge => new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue,
            Path.Combine(TestGpu.Shaders, "structural"), edge, sharePipelinesWith: kernels), () => simulation);
        List<ImpactJob> jobs = [];
        foreach (double area in new[] { 1.0, .25, 1.0 / 9 })
            foreach (double energy in new[] { 1.0, 10, 10000 })
                jobs.Add(new(jobs.Count, energy, [Cell(1, dvec3.UnitX, area), Cell(2, -dvec3.UnitX, area)]));
        ImpactSide beam = new(3, 1, [new(0), new(1, 0, 0)],
            [new(1000, 400), new(1000, 400)], [1, 1], [true, false], false,
            [new(10, 100, 1000), new(10, 100, 1000)], [default, default], [1000, 1000], [false, false],
            [new(1, dvec3.UnitZ, dvec3.Zero, dvec3.UnitZ)]);
        jobs.Add(new(jobs.Count, 1, [beam]));
        jobs.Add(new(jobs.Count, 20, [beam]));
        var expected = jobs.Select(ImpactAnalysis.Run).ToDictionary(o => o.JobId);
        foreach (ImpactJob job in jobs) worker.Post(job);
        Dictionary<int, ImpactOutcome> actual = [];
        Stopwatch timer = Stopwatch.StartNew();
        while (actual.Count < jobs.Count && timer.Elapsed.TotalSeconds < 45)
        {
            worker.Update();
            while (worker.TryTake(out ImpactOutcome outcome)) actual[outcome.JobId] = outcome;
            Thread.Sleep(1);
        }
        bool ok = Check("GPU impact jobs finish without bulk pressure readback", actual.Count == jobs.Count);
        double worst = 0;
        foreach (ImpactJob job in jobs)
        {
            if (!actual.TryGetValue(job.Id, out ImpactOutcome? result)) { ok = false; continue; }
            ImpactOutcome reference = expected[job.Id];
            double error = Math.Abs(result.RetainedEnergy - reference.RetainedEnergy) / Math.Max(1, job.Energy);
            for (int i = 0; i < reference.Sides.Length; i++)
            {
                var changes = result.Sides[i].Damage.ToDictionary(c => c.Cell, c => c.Damage);
                var wanted = reference.Sides[i].Damage.ToDictionary(c => c.Cell, c => c.Damage);
                foreach (ivec3 cell in changes.Keys.Union(wanted.Keys))
                    error = Math.Max(error, Math.Abs(changes.GetValueOrDefault(cell) - wanted.GetValueOrDefault(cell)) / Math.Max(1, wanted.GetValueOrDefault(cell)));
                ok &= reference.Sides[i].Opened == result.Sides[i].Opened;
            }
            worst = Math.Max(worst, error);
            Console.WriteLine($"   job {job.Id}: GPU/CPU damage and energy error {error:G4}, rounds {result.Rounds}/{reference.Rounds}");
            ok &= error < .005;
        }
        ok &= Check("GPU failure allocation matches CPU contact, energy and propagation cases", worst < .005);
        ok &= Check("Vulkan validation errors", gpu.ValidationErrorCount == 0);
        return ok;
    }
}
