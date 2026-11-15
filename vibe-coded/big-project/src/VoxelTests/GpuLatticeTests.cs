using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Structural;

using PhysicsTests;

namespace VoxelTests;


/// <summary>
/// <see cref="GpuStressLattice"/> against statics and beam theory, as <see cref="StressLattice"/>
/// is tested, against the CPU lattice itself on a terrain-like structure, and how fast it
/// follows edits.
/// </summary>
internal static class GpuLatticeTests
{
    private const double Gravity = 9.81;
    private const double StoneDensity = 2600.0;
    private static readonly CellMaterial Stone = new(50e9, 20e9);
    private static readonly dvec3 Down = new(0, 0, -Gravity);

    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("gpu-hanging-column", HangingColumn),
        ("gpu-compressed-column", CompressedColumn),
        ("gpu-cantilever", Cantilever),
        ("gpu-fixed-beam", FixedBeam),
        ("gpu-free-bar", FreeBar),
        ("gpu-soft-on-stiff", SoftOnStiff),
        ("gpu-slender-convergence", SlenderConvergence),
        ("gpu-slab-convergence", SlabConvergence),
        ("gpu-matches-cpu", MatchesCpu),
        ("gpu-edits", Edits),
        ("gpu-topology-reuse", TopologyReuse)
    ];

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static bool Near(string what, double value, double expected, double relative)
    {
        double tolerance = Math.Abs(expected) * relative;
        return Check(what, value, expected - tolerance, expected + tolerance);
    }

    private static GpuStressLattice Create()
    {
        HeadlessVulkan vulkan = TestGpu.Vulkan;
        return new GpuStressLattice(vulkan.Vk, vulkan.Device, vulkan.PhysicalDevice, vulkan.CommandPool, vulkan.Queue, Path.Combine(TestGpu.Shaders, "structural"))
        {
            Tolerance = 1e-5
        };
    }

    private static void Build
    (
        GpuStressLattice lattice,
        IReadOnlyList<ivec3> cells,
        Func<ivec3, bool> anchored,
        Func<ivec3, CellMaterial>? material = null,
        Func<ivec3, double>? density = null
    )
    {
        lattice.SetCells
        (
            [.. cells],
            [.. cells.Select(c => material?.Invoke(c) ?? Stone)],
            [.. cells.Select(c => density?.Invoke(c) ?? StoneDensity)],
            [.. cells.Select(anchored)]
        );
        lattice.SetLoads(Down, []);
    }

    private static CellStress[] SolveAndStress(GpuStressLattice lattice, out LatticeSolveResult result, int maxIterations = 400)
    {
        Stopwatch watch = Stopwatch.StartNew();
        result = lattice.Solve(maxIterations);
        Console.WriteLine($"   solve: {result.Iterations} iterations, residual {result.RelativeResidual:G3}, {watch.Elapsed.TotalMilliseconds:F1} ms ({lattice.CellCount} cells)");
        CellStress[] stresses = new CellStress[lattice.CellCount];
        lattice.CopyStresses(stresses);
        return stresses;
    }

    private static bool HangingColumn()
    {
        const int n = 12;
        using GpuStressLattice lattice = Create();
        List<ivec3> cells = [.. Enumerable.Range(0, n + 1).Select(z => new ivec3(0, 0, -z))];
        Build(lattice, cells, c => c.z == 0);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double weightBelow = n * StoneDensity * Gravity;
        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("tension under the anchor (Pa)", stresses[lattice.IndexOf(new ivec3(0, 0, -1))].Tension, weightBelow, 1e-3);
        ok &= Near("tension above the last cell", stresses[lattice.IndexOf(new ivec3(0, 0, -n))].Tension, StoneDensity * Gravity, 1e-3);
        ok &= Check("no compression", stresses.Max(s => s.Compression), 0, 1e-3 * weightBelow);
        return ok;
    }

    private static bool CompressedColumn()
    {
        const int n = 20;
        using GpuStressLattice lattice = Create();
        Build(lattice, [.. Enumerable.Range(0, n + 1).Select(z => new ivec3(0, 0, z))], c => c.z == 0);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("compression on the base", stresses[lattice.IndexOf(new ivec3(0, 0, 1))].Compression, n * StoneDensity * Gravity, 1e-3);
        return ok;
    }

    /// <summary>A beam of 10 cells out of an anchored wall: 3·ρ·g·L² at its root.</summary>
    private static bool Cantilever()
    {
        const int length = 10;
        using GpuStressLattice lattice = Create();
        Build(lattice, [.. Enumerable.Range(0, length + 1).Select(x => new ivec3(x, 0, 0))], c => c.x == 0);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double expected = 3.0 * StoneDensity * Gravity * length * length;
        CellStress root = stresses[lattice.IndexOf(new ivec3(1, 0, 0))];
        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("root tension (Pa)", root.Tension, expected, 2e-3);
        ok &= Near("root compression (Pa)", root.Compression, expected, 2e-3);
        ok &= Near("root shear (Pa)", root.Shear, 1.5 * length * StoneDensity * Gravity, 2e-3);
        return ok;
    }

    /// <summary>A beam between two walls: Euler-Bernoulli's wL²/12 at the ends, wL²/24 at midspan.</summary>
    private static bool FixedBeam()
    {
        const int length = 21;
        using GpuStressLattice lattice = Create();
        Build(lattice, [.. Enumerable.Range(0, length + 2).Select(x => new ivec3(x, 0, 0))], c => c.x == 0 || c.x == length + 1);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double w = StoneDensity * Gravity;
        double sectionModulus = 1.0 / 6.0;
        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("end bending stress (Pa)", stresses[lattice.IndexOf(new ivec3(1, 0, 0))].Tension, w * length * length / 12.0 / sectionModulus, 0.05);
        ok &= Near("midspan bending stress (Pa)", stresses[lattice.IndexOf(new ivec3(length / 2 + 1, 0, 0))].Tension, w * length * length / 24.0 / sectionModulus, 0.1);
        return ok;
    }

    /// <summary>A free 2×2 bar pulled by balanced end loads, held by the anchor the lattice gives its middle.</summary>
    private static bool FreeBar()
    {
        const int length = 16;
        const double pull = 4e5;
        List<ivec3> cells = [];
        for (int x = 0; x < length; x++)
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
            cells.Add(new ivec3(x, y, z));

        using GpuStressLattice lattice = Create();
        lattice.SetCells([.. cells], [.. cells.Select(_ => Stone)], [.. cells.Select(_ => 0.0)], [.. cells.Select(_ => false)]);
        lattice.SetLoads
        (
            dvec3.Zero,
            [.. cells.Where(c => c.x == 0 || c.x == length - 1).Select(c => (c, new dvec3(c.x == 0 ? -pull : pull, 0, 0), dvec3.Zero))]
        );

        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);
        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("tension mid-bar (Pa)", stresses[lattice.IndexOf(new ivec3(length / 2 - 3, 1, 1))].Tension, pull, 0.02);
        return ok;
    }

    /// <summary>A 3×3×3 stone block hanging by one dirt cell: the dirt carries its whole weight.</summary>
    private static bool SoftOnStiff()
    {
        CellMaterial dirt = new(5e7, 2e7);
        List<ivec3> cells = [new(0, 0, 0), new(0, 0, -1)];
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -4; z <= -2; z++)
            cells.Add(new ivec3(x, y, z));

        using GpuStressLattice lattice = Create();
        Build(lattice, cells, c => c.z == 0, c => c.z == -1 ? dirt : Stone, c => c.z == -1 ? 1600.0 : StoneDensity);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Near("dirt link tension (Pa)", stresses[lattice.IndexOf(new ivec3(0, 0, -1))].Tension, 27 * StoneDensity * Gravity + 1600 * Gravity, 0.01);
        return ok;
    }

    /// <summary>
    /// Slender beams, the hard case for iterative solvers. The GPU's cycle coarsens to a single
    /// node with a sweep or two per level where the CPU's solves its coarsest 64 nodes exactly,
    /// so a long beam takes it a few times more iterations; it still converges, to the same stress.
    /// </summary>
    private static bool SlenderConvergence()
    {
        bool ok = true;
        foreach (int length in new[] { 16, 64, 128 })
        {
            using GpuStressLattice lattice = Create();
            Build(lattice, [.. Enumerable.Range(0, length + 1).Select(x => new ivec3(x, 0, 0))], c => c.x == 0);
            CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

            ok &= Check($"iterations for L = {length}", result.Iterations, 0, 4 * length);
            ok &= Near($"root stress for L = {length}", stresses[lattice.IndexOf(new ivec3(1, 0, 0))].Tension, 3.0 * StoneDensity * Gravity * length * length, 2e-3);
        }

        return ok;
    }

    /// <summary>A 64×64×6 slab on anchored corner pillars, map-sized, from rest.</summary>
    private static bool SlabConvergence()
    {
        List<ivec3> cells = [];
        for (int x = 0; x < 64; x++)
        for (int y = 0; y < 64; y++)
        for (int z = 0; z < 6; z++)
            cells.Add(new ivec3(x, y, z));

        using GpuStressLattice lattice = Create();
        Build(lattice, cells, c => c.z == 0 && (c.x < 2 || c.x > 61) && (c.y < 2 || c.y > 61));
        SolveAndStress(lattice, out LatticeSolveResult result);

        lattice.Profiling = true;
        lattice.IterationsPerUpdate = 1;
        lattice.SetLoads(Down * 1.01, []);
        lattice.Update();
        lattice.Wait();
        Console.WriteLine($"   one update, {lattice.IterationsPerUpdate} iterations: {lattice.GpuMilliseconds:F2} ms on the GPU ({string.Join(", ", lattice.LastProfile.Select(p => $"{p.Phase} {p.Milliseconds:F3}"))})");
        return Check("converged", result.Converged ? 1 : 0, 1, 1) & Check("iterations", result.Iterations, 0, 100);
    }

    /// <summary>
    /// Ground on bedrock with a tower and a cantilever on it, like the game's map: the GPU's
    /// stresses agree with the CPU lattice's wherever they matter.
    /// </summary>
    private static bool MatchesCpu()
    {
        List<ivec3> cells = Terrain();
        bool Anchored(ivec3 c) => c.z == 0;

        StressLattice cpu = new(1.0, [.. cells], [.. cells.Select(_ => Stone)], [.. cells.Select(Anchored)]);
        for (int i = 0; i < cpu.CellCount; i++)
            cpu.Forces[i] = StoneDensity * Down;
        cpu.Solve(400, 1e-9);
        CellStress[] expected = new CellStress[cpu.CellCount];
        cpu.ComputeStresses(expected);

        using GpuStressLattice lattice = Create();
        Build(lattice, cells, Anchored);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double largest = expected.Max(s => Math.Max(s.Tension, Math.Max(s.Compression, s.Shear)));
        double worst = 0.0;
        for (int i = 0; i < cells.Count; i++)
        {
            CellStress e = expected[cpu.IndexOf(cells[i])];
            CellStress g = stresses[i];
            worst = Math.Max(worst, Math.Abs(g.Tension - e.Tension));
            worst = Math.Max(worst, Math.Abs(g.Compression - e.Compression));
            worst = Math.Max(worst, Math.Abs(g.Shear - e.Shear));
        }

        bool ok = Check("converged", result.Converged ? 1 : 0, 1, 1);
        ok &= Check("largest difference from the CPU, of the largest stress", worst / largest, 0, 2e-3);
        return ok;
    }

    /// <summary>
    /// Following edits: after blocks are added to the tower and one is softened, the solution
    /// converges again in a few iterations, without starting over.
    /// </summary>
    private static bool Edits()
    {
        List<ivec3> cells = Terrain();
        using GpuStressLattice lattice = Create();
        lattice.Tolerance = 1e-4;
        Build(lattice, cells, c => c.z == 0);
        SolveAndStress(lattice, out LatticeSolveResult first);

        bool ok = true;
        List<int> iterations = [];
        Stopwatch watch = Stopwatch.StartNew();
        for (int k = 0; k < 5; k++)
        {
            cells.Add(new ivec3(20, 20, 16 + k));
            Build(lattice, cells, c => c.z == 0);
            LatticeSolveResult result = lattice.Solve(200);
            iterations.Add(result.Iterations);
            ok &= result.Converged;
        }

        Console.WriteLine($"   after each added block: {string.Join(", ", iterations)} iterations, {watch.Elapsed.TotalMilliseconds / 5:F1} ms each");
        ok &= Check("most iterations after adding a block", iterations.Max(), 0, 24);

        lattice.SetMaterial(lattice.IndexOf(new ivec3(20, 20, 10)), new CellMaterial(5e9, 2e9));
        LatticeSolveResult softened = lattice.Solve(200);
        ok &= Check("iterations after softening a block", softened.Iterations, 0, 24);
        ok &= Check("converged", softened.Converged ? 1 : 0, 1, 1);
        return ok;
    }

    private static bool TopologyReuse()
    {
        using GpuStressLattice retained = Create();
        List<ivec3> initial = Terrain();
        Build(retained, initial, c => c.z == 0);
        retained.Solve(200);
        bool ok = true;
        // Shrink, reorder, disconnect an unanchored group, regrow and shrink again.
        List<ivec3> small = [.. Enumerable.Range(0, 7).Select(z => new ivec3(2, 2, z)),
            .. Enumerable.Range(0, 5).Select(z => new ivec3(8, 2, z))];
        foreach (List<ivec3> cells in (List<ivec3>[])[small, [.. small.AsEnumerable().Reverse()], initial, small, [.. initial.Select(c => c + new ivec3(100, -80, 0))], small])
        {
            // Submit old-box work immediately before changing topology, including rebases.
            retained.SetLoads(Down, []);
            retained.Update();
            bool Anchor(ivec3 c) => c.z == 0 && c.x != 8;
            Build(retained, cells, Anchor);
            ok &= Check("retained cell count", retained.CellCount, cells.Count, cells.Count);
            ok &= Check("retained cell span excludes unused capacity", retained.Cells.SequenceEqual(cells.ToArray()) ? 1 : 0, 1, 1);
            ok &= Check("old topology results invalidated", retained.HasResults ? 1 : 0, 0, 0);
            retained.Solve(400);
            CellStress[] actual = new CellStress[cells.Count + 3];
            CellStress sentinel = new(123, 456, 789);
            Array.Fill(actual, sentinel);
            retained.CopyStresses(actual);
            ok &= Check("copy leaves destination tail intact", actual[^1] == sentinel && actual[^2] == sentinel && actual[^3] == sentinel ? 1 : 0, 1, 1);
            using GpuStressLattice fresh = Create();
            Build(fresh, cells, Anchor);
            fresh.Solve(400);
            CellStress[] expected = new CellStress[cells.Count];
            fresh.CopyStresses(expected);
            double difference = 0, scale = 1;
            for (int i = 0; i < cells.Count; i++)
            {
                difference = Math.Max(difference, Math.Max(Math.Abs(actual[i].Tension - expected[i].Tension), Math.Max(Math.Abs(actual[i].Compression - expected[i].Compression), Math.Abs(actual[i].Shear - expected[i].Shear))));
                scale = Math.Max(scale, Math.Max(expected[i].Tension, Math.Max(expected[i].Compression, expected[i].Shear)));
            }
            ok &= Check("reused/fresh peak stress difference", difference / scale, 0, 0.01);
        }
        Build(retained, initial, c => c.z == 0);
        // All capacity is warm; this isolates CPU preparation and its managed allocations.
        ivec3[] batch = [.. initial];
        CellMaterial[] materials = [.. initial.Select(_ => Stone)];
        double[] masses = [.. initial.Select(_ => StoneDensity)];
        bool[] anchors = [.. initial.Select(c => c.z == 0)];
        Stopwatch watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) retained.SetCells(batch, materials, masses, anchors);
        double ms = watch.Elapsed.TotalMilliseconds / 20;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"   {batch.Length} cells, warmed SetCells: {ms:F3} ms, {allocated / 20} B/rebuild");
        return ok & Check("warmed topology preparation allocation", allocated, 0, 0);
    }

    /// <summary>40×40 ground, 5 deep on bedrock, a 2×2 tower 10 high, and a beam 6 long out of it.</summary>
    /// <summary>
    /// A map-sized stress window, as the game keeps one around the player: about 100,000 cells
    /// of rolling ground on anchored bedrock, with contact loads that change a little every
    /// update, as objects resting on the map make them. Reports the GPU time of an update.
    /// </summary>
    public static bool MapBenchmark()
    {
        List<ivec3> cells = [];
        for (int x = 0; x < 64; x++)
        for (int y = 0; y < 64; y++)
        {
            int height = 22 + (int)(4.0 * Math.Sin(x * 0.17) + 3.0 * Math.Cos(y * 0.23));
            for (int z = 0; z <= height; z++)
                cells.Add(new ivec3(x, y, z));
        }

        using GpuStressLattice lattice = Create();
        lattice.Tolerance = 1e-4;
        lattice.IterationsPerUpdate = 2;
        Build(lattice, cells, c => c.z == 0);
        SolveAndStress(lattice, out LatticeSolveResult converged, 2000);
        Random random = new(1);
        List<ivec3> surface = [.. cells.Where(c => !cells.Contains(c + new ivec3(0, 0, 1))).Where((_, i) => i % 16 == 0)];
        List<double> times = [];
        lattice.Profiling = Environment.GetEnvironmentVariable("MYT_LATTICE_STAMPS") == "1";
        bool checksums = Environment.GetEnvironmentVariable("MYT_LATTICE_CHECKSUMS") == "1";
        CellStress[] stresses = new CellStress[lattice.CellCount];
        for (int update = 0; update < 40; update++)
        {
            // Loads change on most updates; the others continue the same solve.
            if (update % 3 != 2)
            {
                (ivec3, dvec3, dvec3)[] loads = [.. surface.Select(c => (c, new dvec3(0, 0, -20000 * (1 + 0.05 * random.NextDouble())), dvec3.Zero))];
                lattice.SetLoads(Down, loads);
            }
            lattice.Update();
            lattice.Wait();
            if (update >= 10)
                times.Add(lattice.GpuMilliseconds);
            if (!checksums)
                continue;

            lattice.CopyStresses(stresses);
            ulong hash = 14695981039346656037UL;
            foreach (CellStress stress in stresses)
                hash = unchecked((hash ^ (ulong)BitConverter.DoubleToInt64Bits(stress.Tension + 3 * stress.Compression + 7 * stress.Shear)) * 1099511628211UL);
            Console.WriteLine($"   checksum {update}: residual {lattice.RelativeResidual:R} iterations {lattice.Iterations} stresses {hash:X16}");
        }

        times.Sort();
        Console.WriteLine($"   {lattice.CellCount} cells: update {times.Average():F2} ms mean, {times[times.Count / 2]:F2} ms median on the GPU; residual {lattice.RelativeResidual:G3}");
        if (lattice.Profiling)
            Console.WriteLine($"   last update: {string.Join(", ", lattice.LastProfile.Select(p => $"{p.Phase} {p.Milliseconds:F3}"))}");
        return Check("converged before the loads changed", converged.Converged ? 1 : 0, 1, 1);
    }

    private static List<ivec3> Terrain()
    {
        List<ivec3> cells = [];
        for (int x = 0; x < 40; x++)
        for (int y = 0; y < 40; y++)
        for (int z = 0; z <= 5; z++)
            cells.Add(new ivec3(x, y, z));

        for (int x = 20; x < 22; x++)
        for (int y = 20; y < 22; y++)
        for (int z = 6; z < 16; z++)
            cells.Add(new ivec3(x, y, z));

        for (int x = 22; x < 28; x++)
            cells.Add(new ivec3(x, 20, 15));

        return cells;
    }
}
