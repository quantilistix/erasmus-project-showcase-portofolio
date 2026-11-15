using System.Diagnostics;

namespace VoxelGame;

// Opt-in frame profiling, enabled with MYT_PROF=1.
internal static class Prof
{
    public static readonly bool On = Environment.GetEnvironmentVariable("MYT_PROF") == "1";
    private static readonly bool EveryFrame = Environment.GetEnvironmentVariable("MYT_PROF_EVERY_FRAME") == "1";
    private static readonly Dictionary<string, double> Totals = [];
    private static readonly List<string> Order = [];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<double> Frames = [];
    private static readonly List<double> Intervals = [];
    private static readonly List<double> RenderTimes = [];
    private static readonly List<double> PhysicsTimes = [];
    private static double _frameStart;
    private static int _gcCount;
    private static TimeSpan _gcPause;

    public static void Add(string name, double ms)
    {
        if (!On)
            return;
        if (!Totals.ContainsKey(name))
            Order.Add(name);
        Totals[name] = Totals.GetValueOrDefault(name) + ms;
    }

    public static T Time<T>(string name, Func<T> f)
    {
        if (!On)
            return f();
        long start = Stopwatch.GetTimestamp();
        T r = f();
        Add(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return r;
    }

    public static void Time(string name, Action f)
    {
        if (!On)
        {
            f();
            return;
        }
        long start = Stopwatch.GetTimestamp();
        f();
        Add(name, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    // Observed at the next loop boundary: includes polling, input and diagnostic output
    // as well as Update/Draw/present. The screenshot frame never enters this series.
    public static void ObserveInterval(int frame, double seconds)
    {
        if (On && frame > 1)
            Intervals.Add(seconds * 1000);
    }

    public static void BeginFrame()
    {
        if (!On)
            return;
        foreach (string k in Order)
            Totals[k] = 0;
        _frameStart = Clock.Elapsed.TotalMilliseconds;
        _gcCount = GC.CollectionCount(0) + GC.CollectionCount(1) * 100 + GC.CollectionCount(2) * 10000;
        _gcPause = GC.GetTotalPauseDuration();
    }

    public static void EndFrame(int frame, string extra)
    {
        if (!On)
            return;
        double total = Clock.Elapsed.TotalMilliseconds - _frameStart;
        Frames.Add(total);
        RenderTimes.Add(Totals.GetValueOrDefault("gpu.render"));
        PhysicsTimes.Add(Totals.GetValueOrDefault("s.physics"));
        int gc = GC.CollectionCount(0) + GC.CollectionCount(1) * 100 + GC.CollectionCount(2) * 10000;
        double pause = (GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds;
        if (EveryFrame || total > 20 || pause > 2 || Totals.GetValueOrDefault("s.topo") > 0 || Totals.GetValueOrDefault("w.topo") > 0)
        {
            string parts = string.Join(" ", Order.Where(k => Totals[k] > 0.3).Select(k => $"{k}={Totals[k]:F1}"));
            Console.WriteLine($"frame {frame} {total:F1} ms gc+{gc - _gcCount} pause {pause:F1} | {parts} | {extra}");
        }
    }

    public static void Summary()
    {
        if (!On || Frames.Count == 0)
            return;
        double[] sorted = [.. Frames.Skip(60).OrderBy(f => f)];
        if (sorted.Length == 0)
            return;
        double[] intervals = [.. Intervals.Skip(60).OrderBy(t => t)];
        if (intervals.Length > 0)
            Console.WriteLine($"frame-loop intervals {intervals.Length}: mean {intervals.Average():F2} p50 {intervals[intervals.Length / 2]:F2} p99 {intervals[(int)(intervals.Length * 0.99)]:F2} max {intervals[^1]:F2} ms; over33 {intervals.Count(t => t > 33)}");
        double[] render = [.. RenderTimes.Skip(60).Where(t => t > 0).OrderBy(t => t)];
        if (render.Length > 0)
            Console.WriteLine($"render GPU (completed frame slots): mean {render.Average():F3} p50 {render[render.Length / 2]:F3} p99 {render[(int)(render.Length * 0.99)]:F3} ms; mean physics CPU per frame {PhysicsTimes.Skip(60).Average():F3} ms");
        Console.WriteLine($"frames {sorted.Length}: mean {sorted.Average():F2} p50 {sorted[sorted.Length / 2]:F2} p99 {sorted[(int)(sorted.Length * 0.99)]:F2} max {sorted[^1]:F2} over20 {sorted.Count(f => f > 20)} over33 {sorted.Count(f => f > 33)}; gc0 {GC.CollectionCount(0)} gc1 {GC.CollectionCount(1)} gc2 {GC.CollectionCount(2)} pause {GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms");
    }
}
