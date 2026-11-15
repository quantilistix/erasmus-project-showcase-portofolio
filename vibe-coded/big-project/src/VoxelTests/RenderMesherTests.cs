using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

internal static class RenderMesherTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("render-meshing", Equivalence),
        ("render-meshing-parallel", ParallelEquivalence),
        ("render-meshing-benchmark", Benchmark)
    ];

    private static readonly int[,] Layers = MakeLayers();

    private static bool ParallelEquivalence()
    {
        Grid3D<Voxel>[] grids = [.. Enumerable.Range(0, 32).Select(i =>
        {
            Grid3D<Voxel> grid = new();
            // Alternate shaped blocks, sparse chunks and negative-coordinate seams.
            for (int z = 0; z < 3; z++)
            for (int x = -2; x < 18; x++)
                grid[new ivec3(x, i % 4, z)] = new Voxel(i % 2 == 0 ? BlockType.Stone : BlockType.OakPlanksSlab, 0);
            return grid;
        })];
        ivec3[] chunks = [new(-1, 0, 0), ivec3.Zero, new(1, 0, 0)];
        uint[][] expected = [.. grids.SelectMany(grid => chunks.Select(chunk => VoxelMesher.BuildRenderMesh(grid, chunk, Layers)))];
        int errors = 0;
        Parallel.For(0, expected.Length, index =>
        {
            int i = index / chunks.Length, c = index % chunks.Length;
            for (int repeat = 0; repeat < 4; repeat++)
                if (!expected[index].AsSpan().SequenceEqual(VoxelMesher.BuildRenderMesh(grids[i], chunks[c], Layers)))
                    Interlocked.Increment(ref errors);
        });
        Console.WriteLine($"   parallel and repeated meshing: {errors} mismatches; prior results remain independent of thread scratch");
        return errors == 0;
    }
    private static int[,] MakeLayers()
    {
        int[,] layers = new int[Blocks.Count, 7];
        for (int type = 0; type < Blocks.Count; type++)
        for (int face = 0; face < 7; face++)
            layers[type, face] = type * 6 + face;
        return layers;
    }

    private static ivec3 Point(uint a) => new((int)(a & 31), (int)((a >> 5) & 31), (int)((a >> 10) & 31));

    // Expand rectangles back into unit faces and compare with the independent original
    // mesher. Nonconstant AO must retain all four vertices AND its original diagonal.
    private static bool Verify(Grid3D<Voxel> grid, ivec3 chunk)
    {
        Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
        VoxelMesher.CopyPadded(grid, chunk, cells);
        uint[] reference = VoxelMesher.BuildRenderMeshReference(cells, Layers);
        uint[] merged = VoxelMesher.BuildMergedRenderMesh(cells, Layers);
        Dictionary<(ivec3 Cell, int Face), (uint Layer, uint[] Corners)> expected = [];
        for (int q = 0; q < reference.Length; q += 8)
        {
            uint index = reference[q + 1] >> 8;
            ivec3 cell = new((int)(index & 15), (int)((index >> 4) & 15), (int)(index >> 8));
            expected.Add((cell, (int)((reference[q] >> 15) & 7)), (reference[q + 1] & 255, reference[q..(q + 8)]));
        }
        int errors = 0;
        for (int q = 0; q < merged.Length; q += 8)
        {
            int face = (int)((merged[q] >> 15) & 7);
            int axis = face >> 1, u = (axis + 1) % 3, v = (axis + 2) % 3;
            ivec3 low = new(16), high = new(0);
            for (int k = 0; k < 4; k++)
            {
                ivec3 p = Point(merged[q + k * 2]);
                low = ivec3.Min(low, p); high = ivec3.Max(high, p);
            }
            dvec3 cross = dvec3.Cross(new dvec3(Point(merged[q + 2]) - Point(merged[q])),
                new dvec3(Point(merged[q + 4]) - Point(merged[q])));
            if (dvec3.Dot(cross, new dvec3(Grid3D<Voxel>.FaceDirections[face])) <= 0)
                errors++;
            if ((face & 1) == 0) low[axis]--;
            bool rectangle = (merged[q] & (1u << 22)) != 0;
            for (int y = low[v]; y < high[v]; y++)
            for (int x = low[u]; x < high[u]; x++)
            {
                ivec3 cell = low; cell[u] = x; cell[v] = y;
                if (!expected.Remove((cell, face), out var original)) { errors++; continue; }
                if (original.Layer != (merged[q + 1] & 255)) errors++;
                if (((original.Corners[0] >> 23) & 3) != ((merged[q] >> 23) & 3)) errors++;
                if (!rectangle)
                {
                    if (!original.Corners.AsSpan().SequenceEqual(merged.AsSpan(q, 8))) errors++;
                }
                else
                {
                    uint ao = (merged[q] >> 18) & 3;
                    for (int k = 0; k < 4; k++)
                        if (((original.Corners[k * 2] >> 18) & 3) != ao || ((merged[q + k * 2] >> 18) & 3) != ao) errors++;
                    // Sample within every covered cell; the fragment's half-normal offset
                    // must address that cell, including positive/negative chunk faces.
                    dvec3 sample = new dvec3(cell) + new dvec3(0.371, 0.613, 0.427);
                    sample[axis] = cell[axis] + ((face & 1) == 0 ? 1 : 0);
                    sample -= 0.5 * new dvec3(Grid3D<Voxel>.FaceDirections[face]);
                    ivec3 located = new((int)Math.Floor(sample.x), (int)Math.Floor(sample.y), (int)Math.Floor(sample.z));
                    if (located != cell) errors++;
                }
            }
        }
        errors += expected.Count;
        if (errors > 0) Console.WriteLine($"   chunk {chunk}: {errors} coverage/shading/address errors");
        return errors == 0 && merged.Length <= reference.Length;
    }

    private static bool Equivalence()
    {
        foreach (BlockOrientation orientation in Enum.GetValues<BlockOrientation>())
        {
            Grid3D<Voxel> rotated = new();
            for (int x = 0; x < 5; x++)
            for (int y = 0; y < 4; y++)
                rotated[new ivec3(x, y, 0)] = new Voxel(BlockType.OakLog, 0, orientation);
            if (!Verify(rotated, ivec3.Zero)) return false;
        }

        bool ok = true;
        Grid3D<Voxel> box = new();
        foreach (var (cell, type) in TerrainGenerator.Box(new ivec3(16), BlockType.Stone))
            box[cell] = new Voxel(type, Voxel.ToDamage(cell.x / 20.0));
        ok &= Verify(box, ivec3.Zero);
        Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
        VoxelMesher.CopyPadded(box, ivec3.Zero, cells);
        int quads = VoxelMesher.BuildMergedRenderMesh(cells, Layers).Length / 8;
        ok &= quads == 6;
        Console.WriteLine($"   solid chunk: 1536 -> {quads} quads; independent cell damage retained");

        foreach (ivec3 cell in (ivec3[])[new(0), new(15), new(-1, 16, -17), new(37, -25, 19)])
        foreach (BlockType type in Enum.GetValues<BlockType>().Where(t => t != BlockType.Air))
        {
            Grid3D<Voxel> singleton = new();
            singleton[cell] = new Voxel(type, Voxel.ToDamage(0.5));
            ivec3 chunk = Grid3D<Voxel>.ChunkOf(cell);
            VoxelMesher.CopyPadded(singleton, chunk, cells);
            ok &= VoxelMesher.BuildRenderMesh(singleton, chunk, Layers).SequenceEqual(VoxelMesher.BuildRenderMeshReference(cells, Layers));
        }
        Random random = new(847);
        for (int iteration = 0; iteration < 24; iteration++)
        {
            Grid3D<Voxel> grid = new();
            for (int z = -2; z < 19; z++)
            for (int y = -2; y < 19; y++)
            for (int x = -2; x < 19; x++)
            {
                bool occupied = iteration < 12 ? z < 4 + 3 * Math.Sin(x * 0.6) + 2 * Math.Cos(y) : random.NextDouble() < 0.45;
                if (occupied)
                    grid[new ivec3(x, y, z)] = new Voxel(random.Next(5) == 0 ? BlockType.Glass : BlockType.Stone, 0);
            }
            ok &= Verify(grid, ivec3.Zero);
            ok &= Verify(grid, new ivec3(-1, 0, 0));
            ok &= Verify(grid, new ivec3(1, 1, 1));
        }
        Console.WriteLine($"   randomized transparency, concavities, AO gradients, winding and chunk seams: {(ok ? "ok" : "FAIL")}");
        return ok;
    }

    private static bool Benchmark()
    {
        TerrainGenerator generator = new(1);
        Grid3D<Voxel> grid = new();
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            MapColumn column = generator.GenerateColumn(new ivec2(x, y));
            foreach (var (z, data) in column.Chunks)
            for (int i = 0; i < data.Length; i++)
                if (data[i].Type != BlockType.Air)
                    grid[Grid3D<Voxel>.CellOf(new ivec3(x, y, z), i)] = data[i];
        }
        Voxel[][] chunks = [.. grid.Chunks.Where(c => c.Coordinate.x == 0 && c.Coordinate.y == 0).Select(c =>
        {
            Voxel[] copy = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(grid, c.Coordinate, copy);
            return copy;
        })];
        (long Quads, double Ms) Measure(Func<Voxel[], int[,], uint[]> build)
        {
            foreach (Voxel[] c in chunks) build(c, Layers);
            long quads = 0;
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
                foreach (Voxel[] c in chunks) quads += build(c, Layers).Length / 8;
            return (quads / 20, clock.Elapsed.TotalMilliseconds / 20);
        }
        var original = Measure(VoxelMesher.BuildRenderMeshReference);
        var merged = Measure(VoxelMesher.BuildMergedRenderMesh);
        Console.WriteLine($"   seeded column: {original.Quads} -> {merged.Quads} quads; original {original.Ms:F3} ms, merged {merged.Ms:F3} ms");
        Grid3D<Voxel> single = new();
        single[ivec3.Zero] = new Voxel(BlockType.Stone, 0);
        Stopwatch singleClock = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++)
        {
            Voxel[] copy = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(single, ivec3.Zero, copy);
            VoxelMesher.BuildRenderMeshReference(copy, Layers);
        }
        double before = singleClock.Elapsed.TotalMilliseconds;
        singleClock.Restart();
        for (int i = 0; i < 5000; i++) VoxelMesher.BuildRenderMesh(single, ivec3.Zero, Layers);
        Console.WriteLine($"   5000 single-block objects: original {before:F2} ms, specialized {singleClock.Elapsed.TotalMilliseconds:F2} ms");
        single[new ivec3(1, 0, 0)] = new Voxel(BlockType.Stone, 0);
        // Compare the same mesher with fresh versus reusable padded input. Warm both
        // paths first so retained scratch and JIT costs do not skew allocations.
        uint[] Fresh()
        {
            Voxel[] copy = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(single, ivec3.Zero, copy);
            return VoxelMesher.BuildRenderMesh(copy, Layers);
        }
        Fresh(); VoxelMesher.BuildRenderMesh(single, ivec3.Zero, Layers);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Fresh();
        long freshBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) VoxelMesher.BuildRenderMesh(single, ivec3.Zero, Layers);
        long reusedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"   1000 two-block rebuilds: fresh input {freshBytes:N0} bytes, reused input {reusedBytes:N0} bytes allocated");
        return merged.Quads < original.Quads && reusedBytes < freshBytes;
    }
}
