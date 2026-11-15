using MYT.Kit.Vulkan;

namespace PhysicsTests;

/// <summary>Preallocated measurements; recording samples allocates no memory in the step loop.</summary>
internal sealed class BenchmarkSamples(int steps)
{
    private readonly double[] _wall = new double[steps];
    private readonly double[] _gpu = new double[steps];
    private readonly double[] _cpu = new double[6];
    private int _count;

    public void Record(PhysicsSystem physics, double elapsed)
    {
        _wall[_count] = elapsed;
        _gpu[_count++] = physics.Statistics.GpuMilliseconds;
        PhysicsTimings t = physics.Timings;
        _cpu[0] += t.RebuildMilliseconds;
        _cpu[1] += t.PreparationMilliseconds;
        _cpu[2] += t.RecordingMilliseconds;
        _cpu[3] += t.SubmissionMilliseconds;
        _cpu[4] += t.WaitMilliseconds;
        _cpu[5] += t.ReadbackMilliseconds;
    }

    public void Print(string workload, PhysicsSystem physics, long allocated)
    {
        Array.Sort(_wall);
        Array.Sort(_gpu);
        double Percentile(double[] values, double p) => values[(int)Math.Ceiling(p * (_count - 1))];
        PhysicsStatistics s = physics.Statistics;
        Console.WriteLine($"   {workload}: mean {_wall.Average():F3} ms, p50 {Percentile(_wall, 0.5):F3}, p95 {Percentile(_wall, 0.95):F3}, p99 {Percentile(_wall, 0.99):F3}, max {_wall[^1]:F3}; GPU mean {_gpu.Average():F3}, p95 {Percentile(_gpu, 0.95):F3}; allocated {allocated / _count} B/step");
        Console.WriteLine($"   triangles {s.DynamicTriangleCount}, tested pairs {s.CandidatePairCount}, contacts {s.ContactCount}, reports {physics.ContactReports.Count}; CPU mean ms: rebuild {_cpu[0] / _count:F3}, prepare {_cpu[1] / _count:F3}, record {_cpu[2] / _count:F3}, submit {_cpu[3] / _count:F3}, wait {_cpu[4] / _count:F3}, readback {_cpu[5] / _count:F3}");
    }
}
