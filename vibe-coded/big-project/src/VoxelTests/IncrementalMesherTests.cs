using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

internal static class IncrementalMesherTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("incremental-meshing", Equivalence),
        ("mesh-collision-invalidation", Invalidation),
        ("incremental-render-cache", RenderCache),
        ("incremental-meshing-benchmark", Benchmark)
    ];

    private static readonly int[,] Layers = new int[Blocks.Count, 7];

    private static bool RenderCache()
    {
        bool saved = VoxelMesher.MergeRenderFaces;
        VoxelMesher.MergeRenderFaces = true;
        try
        {
            Grid3D<Voxel> grid = new();
            grid.SetChunk(ivec3.Zero, Enumerable.Repeat(new Voxel(BlockType.Stone, 0), 4096).ToArray());
            IncrementalVoxelMesh mesh = new();
            uint[] first = mesh.UpdateRenderMesh(grid, ivec3.Zero, Layers, false);
            if (first.Length != 6 * 8) return false;
            if (!ReferenceEquals(first, mesh.UpdateRenderMesh(grid, ivec3.Zero, Layers, false))) return false;
            if (mesh.RebuiltCells != 0) return false;
            grid[new ivec3(8)] = Voxel.Air;
            uint[] edited = mesh.UpdateRenderMesh(grid, ivec3.Zero, Layers, false);
            if (mesh.RebuiltCells != 27) return false;
            Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(grid, ivec3.Zero, cells);
            if (!Faces(edited).SequenceEqual(Faces(VoxelMesher.BuildRenderMeshReference(cells, Layers)))) return false;
            if (!ReferenceEquals(edited, mesh.UpdateRenderMesh(grid, ivec3.Zero, Layers, false))) return false;
            Console.WriteLine("   static chunk draws 6 compressed quads; no-op reuses it; first edit rebuilds only 27 cells and matches unit-face reference");
            return true;
        }
        finally { VoxelMesher.MergeRenderFaces = saved; }
    }

    private static bool Invalidation()
    {
        Grid3D<int> grid = new() { TrackDiagonalChanges = true };
        grid[ivec3.Zero] = 1;
        ivec3[] render = grid.TakeChangedChunks(out ivec3[] collision);
        if (render.Length != 8 || collision.Length != 4 || collision.Contains(new(-1, -1, -1))) return false;
        grid.SetChunk(ivec3.Zero, Enumerable.Repeat(1, 4096).ToArray());
        render = grid.TakeChangedChunks(out collision);
        if (render.Length != 27 || collision.Length != 7) return false;
        grid[new ivec3(8)] = 0;
        grid.TakeChangedChunks();
        if (grid.TakeChangedChunks(out collision).Length != 0 || collision.Length != 0) return false;
        Console.WriteLine("   AO edge/corner dependencies do not rebuild unrelated collision meshes; both tracking paths consume atomically");
        return true;
    }

    static IncrementalMesherTests()
    {
        for (int t = 0; t < Blocks.Count; t++)
        for (int f = 0; f < 7; f++) Layers[t, f] = (t * 7 + f) & 255;
    }

    private static string[] Faces(uint[] vertices) => Enumerable.Range(0, vertices.Length / 8)
        .Select(q => string.Join(',', vertices.AsSpan(q * 8, 8).ToArray()))
        .Order(StringComparer.Ordinal).ToArray();

    private static bool Equivalence()
    {
        Grid3D<Voxel> grid = new() { TrackDiagonalChanges = true };
        IncrementalVoxelMesh mesh = new();
        Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
        Random random = new(734);
        ivec3 chunk = new(-1, 0, -1), origin = chunk * 16;
        BlockType[] types = [BlockType.Air, BlockType.Stone, BlockType.Glass, BlockType.Water,
            BlockType.Leaves, BlockType.OakLog, BlockType.Furnace];
        bool Verify(bool leaves)
        {
            VoxelMesher.CopyPadded(grid, chunk, cells);
            uint[] actual = mesh.Update(cells, Layers, leaves);
            uint[] expected = VoxelMesher.BuildRenderMeshReference(cells, Layers, leaves);
            return Faces(actual).SequenceEqual(Faces(expected));
        }
        for (int i = 0; i < 3500; i++)
            grid[origin + new ivec3(random.Next(-1, 17), random.Next(-1, 17), random.Next(-1, 17))]
                = new Voxel(types[random.Next(types.Length)], 0);
        if (!Verify(false)) return false;
        for (int step = 0; step < 150; step++)
        {
            for (int edit = 0; edit < 1 + step % 30; edit++)
                grid[origin + new ivec3(random.Next(-1, 17), random.Next(-1, 17), random.Next(-1, 17))]
                    = new Voxel(types[random.Next(types.Length)], 0, (BlockOrientation)random.Next(6), step % 2 == 0);
            if (!Verify(step % 40 >= 20))
            {
                Console.WriteLine($"   face/AO mismatch at edit batch {step}");
                return false;
            }
        }
        // One isolated edit must touch only the 3x3x3 neighbourhood, then none.
        grid[origin + new ivec3(8)] = new Voxel(BlockType.Stone, 0);
        Verify(false);
        grid[origin + new ivec3(8)] = Voxel.Air;
        if (!Verify(false) || mesh.RebuiltCells != 27) return false;
        if (!Verify(false) || mesh.RebuiltCells != 0) return false;
        grid[origin + new ivec3(8)] = new Voxel(BlockType.OakPlanksSlab, 0);
        if (!Verify(false)) return false;
        grid[origin + new ivec3(8)] = Voxel.Air;
        if (!Verify(false)) return false;
        foreach (var item in grid.Occupied().ToArray()) grid[item.Cell] = Voxel.Air;
        if (!Verify(false)) return false;
        grid.TakeChangedChunks();
        grid[origin] = new Voxel(BlockType.Stone, 0);
        ivec3[] dirty = grid.TakeChangedChunks();
        if (dirty.Length != 8 || !dirty.Contains(chunk - new ivec3(1))) return false;
        if (!Verify(false)) return false;
        grid[origin + new ivec3(15)] = new Voxel(BlockType.Leaves, 0);
        if (!Verify(true)) return false;
        uint[] snapshot = mesh.Update(cells, Layers, true);
        uint[] saved = (uint[])snapshot.Clone();
        grid[origin] = Voxel.Air;
        if (!Verify(true) || !snapshot.AsSpan().SequenceEqual(saved)) return false;
        Console.WriteLine("   random batches, AO, negative seams, transparency, rotations, shapes, empty/refill and immutable results match reference");
        return true;
    }

    private static bool Benchmark()
    {
        Grid3D<Voxel> grid = new();
        for (int z = -1; z <= 16; z++)
        for (int y = -1; y <= 16; y++)
        for (int x = -1; x <= 16; x++)
            if (z < 9) grid[new ivec3(x, y, z)] = new Voxel(BlockType.Stone, 0);
        const int iterations = 1000;
        IncrementalVoxelMesh mesh = new();
        Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
        double Measure(bool incremental, int side = 1)
        {
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                for (int z = 0; z < side; z++)
                for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    grid[new ivec3(6 + x, 6 + y, 5 + z)] = (i & 1) == 0 ? Voxel.Air : new Voxel(BlockType.Stone, 0);
                VoxelMesher.CopyPadded(grid, ivec3.Zero, cells);
                if (incremental) mesh.Update(cells, Layers);
                else VoxelMesher.BuildMergedRenderMesh(cells, Layers);
            }
            return watch.Elapsed.TotalMilliseconds;
        }
        Measure(true); Measure(false); // JIT and warm caches before timing.
        double fast = Measure(true), full = Measure(false);
        Console.WriteLine($"   {iterations} single-block edits including padded copy: incremental {fast:F2} ms, full greedy {full:F2} ms ({full / fast:F1}x)");
        bool local = mesh.RebuiltCells == 27;
        Measure(true, 4); Measure(false, 4);
        fast = Measure(true, 4); full = Measure(false, 4);
        Console.WriteLine($"   {iterations} 64-block blast-sized batches including edits/copy: incremental {fast:F2} ms, full greedy {full:F2} ms ({full / fast:F1}x)");
        return local && mesh.RebuiltCells == 216;
    }
}
