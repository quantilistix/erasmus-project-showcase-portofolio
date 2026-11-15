using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class StreamingMeshingTests
{
    public static bool Run(Renderer renderer, World world)
    {
        const int chunks = 5;
        const int columns = 2;
        List<(int, Voxel[])> column = [];
        for (int z = 0; z < chunks; z++)
        {
            Voxel[] cells = new Voxel[4096];
            for (int i = 0; i < cells.Length; i++)
                if ((i & 3) != 0) cells[i] = new(BlockType.Leaves, 0);
            column.Add((z, cells));
        }
        world.LoadColumn(new(ivec2.Zero, [.. column]));
        world.LoadColumn(new(new(1, 0), [.. column]));
        void Sync()
        {
            world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
            renderer.Sync(world, new dvec3(8, 8, 40));
        }
        Sync();
        bool waits = renderer.ImmediateMeshesBuilt == 0;
        // All center chunks are dirty AND in the waiting set when their final
        // neighbouring columns arrive. Previously they were enqueued twice and
        // Parallel.For raced two writers over each persistent face array.
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 2; x++)
            if (y != 0 || x < 0 || x >= columns) world.LoadColumn(new(new(x, y), []));
        Sync();
        bool once = renderer.ImmediateMeshesBuilt == chunks * columns;
        Console.WriteLine($"   waits for neighbours: {waits}; built {renderer.ImmediateMeshesBuilt} jobs for {chunks * columns} newly ready chunks; {(waits && once ? "PASS" : "FAIL")}");
        // Repeated local edits exercise the same cached meshes after ownership passes
        // back from the worker batch, including insertion/removal and face compaction.
        for (int iteration = 0; iteration < 20; iteration++)
        {
            for (int z = 0; z < chunks; z++)
            for (int x = 0; x < columns; x++)
                world.Map.Grid[new ivec3(x * 16 + 8, 8, z * 16 + 8)] = (iteration & 1) == 0
                    ? new(BlockType.Stone, 0) : Voxel.Air;
            Sync();
            once &= renderer.ImmediateMeshesBuilt == chunks * columns;
        }
        Console.WriteLine($"   parallel cached mesh updates retain one writer per chunk: {once}; {(once ? "PASS" : "FAIL")}");
        return waits && once;
    }
}
