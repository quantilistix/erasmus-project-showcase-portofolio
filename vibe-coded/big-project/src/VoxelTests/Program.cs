using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

namespace VoxelTests;


/// <summary>
/// Checks of the voxel primitives, <see cref="Grid3D{T}"/> and the structural solver
/// (<see cref="StressLattice"/>) against statics and beam theory, then of the game world on a
/// headless GPU (<see cref="WorldTests"/>). Run all with
/// <c>dotnet run --project src/VoxelTests -c Release</c>, or name some: <c>... -- cantilever</c>.
/// </summary>
internal static class Program
{
    private const double Gravity = 9.81;
    private const double StoneDensity = 2600.0;
    private static readonly CellMaterial Stone = new(50e9, 20e9);

    private static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("grid-basics", GridBasics),
        ("grid-region", GridRegion),
        ("grid-components", GridComponents),
        ("grid-raycast", GridRaycast),
        ("grid-save", GridSave),
        ("hanging-column", HangingColumn),
        ("compressed-column", CompressedColumn),
        ("cantilever", Cantilever),
        ("fixed-beam", FixedBeam),
        ("free-bar", FreeBar),
        ("slender-convergence", SlenderConvergence),
        ("slab-convergence", SlabConvergence),
        ("warm-start", WarmStart),
        ("soft-on-stiff", SoftOnStiff)
    ];

    public static int Main(string[] arguments)
    {
        if (arguments.Contains("gpu-meshing-headless"))
        {
            bool passed = HeadlessMeshTests.Run();
            TestGpu.Dispose();
            return passed ? 0 : 1;
        }
        if (arguments.Contains("gpu-meshing"))
            return RenderEquivalenceTests.Run(gpuMeshOnly: true) ? 0 : 1;
        if (arguments.Contains("async-meshing"))
            return RenderEquivalenceTests.Run(asyncOnly: true) ? 0 : 1;
        if (arguments.Contains("lighting-budget"))
            return RenderEquivalenceTests.Run(budgetOnly: true) ? 0 : 1;
        if (arguments.Contains("streaming-meshing"))
            return RenderEquivalenceTests.Run(streamingOnly: true) ? 0 : 1;
        if (arguments.Contains("particle-rendering"))
            return RenderEquivalenceTests.Run(particlesOnly: true) ? 0 : 1;
        if (arguments.Contains("terrain-lod-render"))
            return RenderEquivalenceTests.Run(lodOnly: true) ? 0 : 1;
        if (arguments.Contains("water-rendering"))
            return RenderEquivalenceTests.Run(waterOnly: true) ? 0 : 1;
        // Optional windowed pixel comparison; the normal suite stays headless.
        if (arguments.Contains("daylight-sky"))
            return RenderEquivalenceTests.Run(skyOnly:true) ? 0 : 1;

        if (arguments.Contains("render-equivalence"))
            return RenderEquivalenceTests.Run() ? 0 : 1;

        if (arguments.Contains("lighting-compute"))
            return RenderEquivalenceTests.Run(computeOnly:true) ? 0 : 1;
        if (arguments.Contains("lighting-benchmark"))
            return RenderEquivalenceTests.Run(lightingBenchmark:true) ? 0 : 1;
        if (arguments.Contains("shadow-alignment"))
            return RenderEquivalenceTests.Run(shadowOnly:true) ? 0 : 1;
        if (arguments.Contains("enclosed-room"))
            return RenderEquivalenceTests.Run(roomOnly:true) ? 0 : 1;
        if (arguments.Contains("gpu-map-benchmark"))
        {
            bool passed = GpuLatticeTests.MapBenchmark();
            TestGpu.Dispose();
            return passed ? 0 : 1;
        }

        int failures = 0;
        foreach ((string name, Func<bool> run) in Scenarios.Concat(ImpactTests.Scenarios).Concat(DaylightTests.Scenarios).Concat(RealtimeLightingTests.Scenarios).Concat(SettingsTests.Scenarios).Concat(CollisionPerformanceTests.Scenarios).Concat(MesherTests.Scenarios).Concat(RenderMesherTests.Scenarios).Concat(IncrementalMesherTests.Scenarios).Concat(TerrainCoverTests.Scenarios).Concat(ParticleStreamTests.Scenarios).Concat(GpuLatticeTests.Scenarios).Concat(StressWorkerTests.Scenarios).Concat(WorldTests.Scenarios).Concat(StreamingTests.Scenarios).Concat(WorldLodTests.Scenarios).Concat(GuiTests.Scenarios).Concat(SaveTests.Scenarios).Concat(InventoryTests.Scenarios).Concat(GameplayTests.Scenarios).Concat(ContentTests.Scenarios).Concat(BlockTextureTests.Scenarios).Concat(BlockShapeTests.Scenarios))
        {
            if (arguments.Length > 0 && !arguments.Contains(name))
                continue;

            Console.WriteLine($"\n== {name}");
            bool passed = run();
            Console.WriteLine(passed ? "   PASS" : "   FAIL");
            if (!passed)
                failures++;
        }

        TestGpu.Dispose();
        Console.WriteLine($"\n{failures} failed");
        return failures == 0 ? 0 : 1;
    }

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

    // -------------------------------------------------------------------------------------
    // Grid3D
    // -------------------------------------------------------------------------------------

    private static bool GridBasics()
    {
        Grid3D<int> grid = new();
        bool ok = true;

        grid[new ivec3(-1, -17, 40)] = 5;
        grid[new ivec3(15, 0, 0)] = 7;
        ok &= Check("value at a negative cell", grid[-1, -17, 40], 5, 5);
        ok &= Check("count", grid.Count, 2, 2);
        ok &= Check("chunks", grid.Chunks.Count, 2, 2);

        ivec3[] changed = grid.TakeChangedChunks();
        // (15, 0, 0) touches three chunk faces and (−1, −17, 40) two, so the chunks across them count as changed too.
        ok &= Check("changed chunks", changed.Length, 7, 7);
        ok &= Check("changed after taking", grid.TakeChangedChunks().Length, 0, 0);

        ok &= Check("unchanged set reports false", grid.Set(new ivec3(15, 0, 0), 7) ? 1 : 0, 0, 0);
        grid[new ivec3(15, 0, 0)] = 0;
        ok &= Check("count after clearing", grid.Count, 1, 1);
        ok &= Check("empty chunk freed", grid.Chunks.Count, 1, 1);

        (ivec3 min, ivec3 max) = grid.Bounds();
        ok &= Check("bounds min y", min.y, -17, -17);
        ok &= Check("bounds max z", max.z, 41, 41);

        grid.Update(new ivec3(-1, -17, 40), 9);
        ok &= Check("update leaves change tracking alone", grid.TakeChangedChunks().Length, 4, 4);
        ok &= Check("updated value", grid[-1, -17, 40], 9, 9);
        return ok;
    }

    private static bool GridRegion()
    {
        Grid3D<int> grid = new();
        Random random = new(1);
        for (int k = 0; k < 2000; k++)
            grid[new ivec3(random.Next(-20, 40), random.Next(-20, 40), random.Next(-20, 40))] = random.Next(1, 100);

        ivec3 min = new(-5, -18, 3);
        ivec3 size = new(37, 18, 21);
        int[] copy = new int[size.x * size.y * size.z];
        grid.CopyRegion(min, size, copy);

        int mismatches = 0;
        for (int z = 0; z < size.z; z++)
        for (int y = 0; y < size.y; y++)
        for (int x = 0; x < size.x; x++)
        {
            if (copy[x + size.x * (y + size.y * z)] != grid[min + new ivec3(x, y, z)])
                mismatches++;
        }

        return Check("region cells differing from Get", mismatches, 0, 0);
    }

    private static bool GridComponents()
    {
        Grid3D<byte> grid = new();
        for (int x = 0; x < 10; x++)
            grid[new ivec3(x, 0, 0)] = 1;
        for (int z = 1; z < 4; z++)
            grid[new ivec3(9, 0, z)] = 1;

        bool ok = Check("one component", grid.ConnectedComponents().Count, 1, 1);

        grid[new ivec3(4, 0, 0)] = 0;
        List<HashSet<ivec3>> parts = grid.ComponentsOf(Grid3D<byte>.FaceDirections.Select(d => new ivec3(4, 0, 0) + d));
        ok &= Check("components after the cut", parts.Count, 2, 2);
        ok &= Check("largest first", parts[0].Count, 8, 8);

        HashSet<ivec3> visited = [];
        bool found = grid.FloodFill(new ivec3(9, 0, 3), visited, stop: (cell, _) => cell.x == 5);
        ok &= Check("flood stops at the target", found ? 1 : 0, 1, 1);
        return ok;
    }

    private static bool GridRaycast()
    {
        Grid3D<byte> grid = new();
        grid[new ivec3(5, 2, -3)] = 1;
        bool ok = true;

        bool hit = grid.Raycast(new dvec3(0.5, 2.5, -2.5), new dvec3(1, 0, 0), 20, out Grid3DHit result);
        ok &= Check("hit", hit ? 1 : 0, 1, 1);
        ok &= Check("hit cell x", result.Cell.x, 5, 5);
        ok &= Check("entered through −x", result.Normal.x, -1, -1);
        ok &= Check("distance", result.Distance, 4.5, 4.5);

        hit = grid.Raycast(new dvec3(5.5, 2.5, 3.0), new dvec3(0, 0, -1), 20, out result);
        ok &= Check("downward hit through +z", hit && result.Normal.z == 1 ? 1 : 0, 1, 1);
        ok &= Check("miss when short", grid.Raycast(new dvec3(0.5, 2.5, -2.5), new dvec3(1, 0, 0), 4, out _) ? 1 : 0, 0, 0);
        return ok;
    }

    private static bool GridSave()
    {
        Grid3D<long> grid = new(-1);
        grid[new ivec3(3, -40, 7)] = 12;
        grid[new ivec3(100, 0, 0)] = 0;

        MemoryStream stream = new();
        grid.Save(stream);
        stream.Position = 0;
        Grid3D<long> loaded = Grid3D<long>.Load(stream);

        bool ok = Check("count", loaded.Count, 2, 2);
        ok &= Check("value", loaded[3, -40, 7], 12, 12);
        ok &= Check("zero is a value when empty is −1", loaded[100, 0, 0], 0, 0);
        ok &= Check("empty", loaded[1, 1, 1], -1, -1);
        return ok;
    }

    // -------------------------------------------------------------------------------------
    // Structural solver
    // -------------------------------------------------------------------------------------

    private static StressLattice Build(IReadOnlyList<ivec3> cells, Func<ivec3, bool> anchored, Func<ivec3, CellMaterial>? material = null)
    {
        return new StressLattice
        (
            1.0,
            [.. cells],
            [.. cells.Select(c => material?.Invoke(c) ?? Stone)],
            [.. cells.Select(anchored)]
        );
    }

    private static void ApplyGravity(StressLattice lattice, Func<ivec3, double>? density = null)
    {
        for (int i = 0; i < lattice.CellCount; i++)
        {
            lattice.Forces[i] = new dvec3(0, 0, -(density?.Invoke(lattice.Cells[i]) ?? StoneDensity) * Gravity);
            lattice.Moments[i] = dvec3.Zero;
        }
    }

    private static CellStress[] SolveAndStress(StressLattice lattice, out LatticeSolveResult result, int maxIterations = 200, double tolerance = 1e-8)
    {
        Stopwatch watch = Stopwatch.StartNew();
        result = lattice.Solve(maxIterations, tolerance);
        Console.WriteLine($"   solve: {result.Iterations} iterations, residual {result.RelativeResidual:G3}, {watch.Elapsed.TotalMilliseconds:F1} ms ({lattice.CellCount} cells)");
        CellStress[] stresses = new CellStress[lattice.CellCount];
        lattice.ComputeStresses(stresses);
        return stresses;
    }

    /// <summary>A column of n cells hanging below an anchored cell: the top face carries the whole weight in tension.</summary>
    private static bool HangingColumn()
    {
        const int n = 12;
        List<ivec3> cells = [.. Enumerable.Range(0, n + 1).Select(z => new ivec3(0, 0, -z))];
        StressLattice lattice = Build(cells, c => c.z == 0);
        ApplyGravity(lattice);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double weightBelow = n * StoneDensity * Gravity;
        bool ok = result.Converged;
        ok &= Near("tension under the anchor (Pa)", stresses[lattice.IndexOf(new ivec3(0, 0, -1))].Tension, weightBelow, 1e-4);
        ok &= Near("tension above the last cell", stresses[lattice.IndexOf(new ivec3(0, 0, -n))].Tension, StoneDensity * Gravity, 1e-4);
        ok &= Check("no compression", stresses.Max(s => s.Compression), 0, 1e-3 * weightBelow);
        return ok;
    }

    private static bool CompressedColumn()
    {
        const int n = 20;
        List<ivec3> cells = [.. Enumerable.Range(0, n + 1).Select(z => new ivec3(0, 0, z))];
        StressLattice lattice = Build(cells, c => c.z == 0);
        ApplyGravity(lattice);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        bool ok = result.Converged;
        ok &= Near("compression on the base", stresses[lattice.IndexOf(new ivec3(0, 0, 1))].Compression, n * StoneDensity * Gravity, 1e-4);
        return ok;
    }

    /// <summary>
    /// A horizontal 1×1 beam of L cells from an anchored wall cell. The root face carries
    /// M = m·g·h·L²/2, so its peak bending stress is M / (h³/6) = 3·ρ·g·L² for h = 1.
    /// </summary>
    private static bool Cantilever()
    {
        const int length = 10;
        List<ivec3> cells = [.. Enumerable.Range(0, length + 1).Select(x => new ivec3(x, 0, 0))];
        StressLattice lattice = Build(cells, c => c.x == 0);
        ApplyGravity(lattice);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double expected = 3.0 * StoneDensity * Gravity * length * length;
        CellStress root = stresses[lattice.IndexOf(new ivec3(1, 0, 0))];
        bool ok = result.Converged;
        ok &= Near("root tension (Pa)", root.Tension, expected, 1e-3);
        ok &= Near("root compression (Pa)", root.Compression, expected, 1e-3);
        ok &= Near("root shear (Pa)", root.Shear, 1.5 * length * StoneDensity * Gravity, 1e-3);
        return ok;
    }

    /// <summary>
    /// A beam of L cells between two anchored walls under its own weight, statically
    /// indeterminate: Euler-Bernoulli gives end moments wL²/12 and wL²/24 at midspan. The
    /// lattice beam also shears (Timoshenko), which shifts this a little.
    /// </summary>
    private static bool FixedBeam()
    {
        const int length = 21;
        List<ivec3> cells = [.. Enumerable.Range(0, length + 2).Select(x => new ivec3(x, 0, 0))];
        StressLattice lattice = Build(cells, c => c.x == 0 || c.x == length + 1);
        ApplyGravity(lattice);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double w = StoneDensity * Gravity;
        double sectionModulus = 1.0 / 6.0;
        double endStress = w * length * length / 12.0 / sectionModulus;
        double midStress = w * length * length / 24.0 / sectionModulus;

        bool ok = result.Converged;
        ok &= Near("end bending stress (Pa)", stresses[lattice.IndexOf(new ivec3(1, 0, 0))].Tension, endStress, 0.05);
        ok &= Near("midspan bending stress (Pa)", stresses[lattice.IndexOf(new ivec3(length / 2 + 1, 0, 0))].Tension, midStress, 0.1);
        return ok;
    }

    /// <summary>A free 2×2 bar pulled apart by balanced end loads, held only by the rigid-motion anchor in its middle.</summary>
    private static bool FreeBar()
    {
        const int length = 16;
        const double pull = 4e5;
        List<ivec3> cells = [];
        for (int x = 0; x < length; x++)
        for (int y = 0; y < 2; y++)
        for (int z = 0; z < 2; z++)
            cells.Add(new ivec3(x, y, z));

        StressLattice lattice = Build(cells, c => c == new ivec3(length / 2, 0, 0));
        for (int i = 0; i < lattice.CellCount; i++)
        {
            ivec3 c = lattice.Cells[i];
            lattice.Forces[i] = c.x == 0 ? new dvec3(-pull, 0, 0) : c.x == length - 1 ? new dvec3(pull, 0, 0) : dvec3.Zero;
        }

        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);
        bool ok = result.Converged;
        ok &= Near("tension mid-bar (Pa)", stresses[lattice.IndexOf(new ivec3(length / 2 - 3, 1, 1))].Tension, pull, 0.02);
        ok &= Check("anchor displacement (m)", lattice.Displacement(lattice.IndexOf(new ivec3(length / 2, 0, 0))).Length, 0, 1e-9);
        return ok;
    }

    /// <summary>
    /// Slender beams are the hard case for plain iterative solvers (≈ L² iterations); the
    /// multigrid keeps them cheap. Their condition number grows as L⁴, so double precision
    /// caps the attainable residual near 1e-16 · 12 · L⁴: about 1e-6 at L = 128.
    /// </summary>
    private static bool SlenderConvergence()
    {
        bool ok = true;
        foreach (int length in new[] { 16, 64, 128 })
        {
            List<ivec3> cells = [.. Enumerable.Range(0, length + 1).Select(x => new ivec3(x, 0, 0))];
            StressLattice lattice = Build(cells, c => c.x == 0);
            ApplyGravity(lattice);
            CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result, 400, 1e-6);

            double expected = 3.0 * StoneDensity * Gravity * length * length;
            ok &= Check($"iterations for L = {length}", result.Iterations, 0, 60);
            ok &= Near($"root stress for L = {length}", stresses[1].Tension, expected, 1e-3);
        }

        return ok;
    }

    /// <summary>A 64×64×6 slab resting on anchored corner pillars: map-sized, solved from rest.</summary>
    private static bool SlabConvergence()
    {
        List<ivec3> cells = [];
        for (int x = 0; x < 64; x++)
        for (int y = 0; y < 64; y++)
        for (int z = 0; z < 6; z++)
            cells.Add(new ivec3(x, y, z));

        bool Pillar(ivec3 c) => c.z == 0 && (c.x < 2 || c.x > 61) && (c.y < 2 || c.y > 61);
        StressLattice lattice = Build(cells, Pillar);
        ApplyGravity(lattice);

        Stopwatch watch = Stopwatch.StartNew();
        lattice.RebuildPreconditioner();
        Console.WriteLine($"   hierarchy: {watch.Elapsed.TotalMilliseconds:F0} ms");
        SolveAndStress(lattice, out LatticeSolveResult result, 400);
        return Check("iterations", result.Iterations, 0, 80) & (result.Converged ? 1 : 0) == 1;
    }

    /// <summary>After a small change in load, the previous solution is a good start.</summary>
    private static bool WarmStart()
    {
        const int length = 40;
        List<ivec3> cells = [.. Enumerable.Range(0, length + 1).Select(x => new ivec3(x, 0, 0))];
        StressLattice lattice = Build(cells, c => c.x == 0);
        ApplyGravity(lattice);
        lattice.Solve(400, 1e-8);

        lattice.Forces[length] += new dvec3(0, 0, -1000);
        LatticeSolveResult result = lattice.Solve(400, 1e-4);
        Console.WriteLine($"   re-solve: {result.Iterations} iterations");
        return Check("iterations after a 4% load change", result.Iterations, 0, 8);
    }

    /// <summary>
    /// A stone block hanging from a dirt cell under a stone ceiling: interfaces in series, so
    /// both the dirt-stone interfaces carry the block's whole weight.
    /// </summary>
    private static bool SoftOnStiff()
    {
        CellMaterial dirt = new(5e7, 2e7);
        List<ivec3> cells = [new(0, 0, 0), new(0, 0, -1)];
        for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
        for (int z = -4; z <= -2; z++)
            cells.Add(new ivec3(x, y, z));

        StressLattice lattice = Build(cells, c => c.z == 0, c => c.z == -1 ? dirt : Stone);
        ApplyGravity(lattice, c => c.z == -1 ? 1600.0 : StoneDensity);
        CellStress[] stresses = SolveAndStress(lattice, out LatticeSolveResult result);

        double hanging = 27 * StoneDensity * Gravity;
        bool ok = result.Converged;
        ok &= Near("dirt link tension (Pa)", stresses[lattice.IndexOf(new ivec3(0, 0, -1))].Tension, hanging + 1600 * Gravity, 0.01);
        return ok;
    }
}
