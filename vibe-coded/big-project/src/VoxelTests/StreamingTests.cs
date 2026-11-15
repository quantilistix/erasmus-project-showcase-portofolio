using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Vulkan;

using VoxelGame;

namespace VoxelTests;


/// <summary>
/// Checks of the generated world and of streaming it: that columns generated apart meet,
/// that what is generated stands up under the stress model, and that the simulation follows
/// the player across a map streamed in and out, keeping what the player changed.
/// </summary>
internal static class StreamingTests
{
    private const int Seed = 1;

    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("terrain-generation", Generation),
        ("terrain-seams", Seams),
        ("terrain-stands", Stands),
        ("collider-coverage", ColliderCoverage),
        ("streaming-cache", StreamingCache),
        ("analysis-window", AnalysisWindow),
        ("world-streaming", Streaming)
    ];

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static World CreateWorld()
    {
        return new World(TestGpu.Vulkan.Vk, TestGpu.Vulkan.Device, TestGpu.Vulkan.PhysicalDevice, TestGpu.Vulkan.CommandPool, TestGpu.Vulkan.Queue, TestGpu.Shaders);
    }

    private static bool AnalysisWindow()
    {
        using World world = CreateWorld();
        world.FollowPlayer = true;
        for (int z = 0; z < 25; z++)
        for (int y = -26; y < 47; y++)
        for (int x = -28; x < 47; x++)
            if (z < 23 || (x + y) % 3 != 0)
                world.SetBlock(world.Map, new ivec3(x, y, z), z == 0 ? BlockType.Bedrock
                    : (x + y + z) % 17 == 0 ? BlockType.Water : BlockType.Stone);
        dvec3 spawn = new(8.5, 10.5, 30.5);
        Player player = world.SpawnPlayer(spawn);
        player.Flying = true;
        world.StepOnce();
        ivec3 center = (ivec3)dvec3.Floor(spawn);
        ivec3 low = center - new ivec3(32, 32, 24), high = center + new ivec3(32, 32, 40);
        bool Inside(ivec3 p) => p.x >= low.x && p.y >= low.y && p.z >= low.z && p.x < high.x && p.y < high.y && p.z < high.z;
        IEnumerable<(ivec3 Cell, Voxel Voxel, bool Held)> Reference()
        {
            ivec3 first = Grid3D<Voxel>.ChunkOf(low), last = Grid3D<Voxel>.ChunkOf(high - 1);
            for (int z = first.z; z <= last.z; z++)
            for (int y = first.y; y <= last.y; y++)
            for (int x = first.x; x <= last.x; x++)
            {
                ivec3 chunk = new(x, y, z);
                if (!world.Map.Grid.TryGetChunk(chunk, out var data)) continue;
                for (int i = 0; i < Grid3D<Voxel>.ChunkVolume; i++)
                {
                    Voxel voxel = data.Cells[i];
                    ivec3 cell = Grid3D<Voxel>.CellOf(chunk, i);
                    if (!Blocks.IsSolid(voxel.Type) || !Inside(cell)) continue;
                    bool held = VoxelMap.IsAnchor(voxel);
                    foreach (ivec3 direction in Grid3D<Voxel>.FaceDirections)
                        held |= !Inside(cell + direction) && Blocks.IsSolid(world.Map.Grid[cell + direction].Type);
                    yield return (cell, voxel, held);
                }
            }
        }
        var expected = Reference().ToArray();
        bool ok = Check("analysis cells, voxel values, anchors and visitation order", world.AnalysedCells(world.Map).SequenceEqual(expected) ? 1 : 0, 1, 1);
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) foreach (var _ in Reference()) { }
        double original = watch.Elapsed.TotalMilliseconds / 10;
        watch.Restart();
        for (int i = 0; i < 10; i++) foreach (var _ in world.AnalysedCells(world.Map)) { }
        Console.WriteLine($"   {expected.Length} analysis cells: reference {original:F2} ms, boundary-only {watch.Elapsed.TotalMilliseconds / 10:F2} ms");
        return ok;
    }

    private static bool StreamingCache()
    {
        using World world = CreateWorld();
        using ChunkStreamer streamer = new(world, new TerrainGenerator(Seed), 4);
        dvec3 focus = new(0.5, 0.5, 80);
        bool Settle()
        {
            Stopwatch timeout = Stopwatch.StartNew();
            do
            {
                int before = streamer.LoadedColumns;
                streamer.Update(focus);
                if (streamer.LoadedColumns - before > 4)
                    throw new InvalidOperationException("Terrain completion burst exceeded the frame load budget");
                if (streamer.Generating == 0) return true;
                Thread.Sleep(1);
            } while (timeout.Elapsed.TotalSeconds < 20);
            return false;
        }
        bool ok = Settle();
        int original = streamer.LoadedColumns;
        int version = world.Map.ColumnVersion;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) streamer.Update(focus);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        ok &= Check("settled updates leave columns alone", world.Map.ColumnVersion, version, version);
        ok &= Check("settled update allocations", allocated, 0, 0);
        streamer.RenderDistance = 5;
        ok &= Settle();
        ok &= Check("radius increase loads more columns", streamer.LoadedColumns, original + 1, double.MaxValue);
        ivec2 center = new(0);
        world.UnloadColumn(center);
        ok &= Settle();
        ok &= Check("external unload refills wanted column", world.Map.IsLoaded(center) ? 1 : 0, 1, 1);
        // Move across both a negative column boundary and the old keep radius.
        focus = new dvec3(-160.5, -32.5, 80);
        ok &= Settle();
        ok &= Check("moving center loads negative column", world.Map.IsLoaded(ChunkStreamer.ColumnAt(focus)) ? 1 : 0, 1, 1);
        ok &= Check("moving center unloads old column", world.Map.IsLoaded(center) ? 1 : 0, 0, 0);
        return ok;
    }

    private static bool ColliderCoverage()
    {
        ChunkCoverage coverage = new();
        HashSet<ivec3> reference = [];
        List<(dvec3 Low, dvec3 High)> requests = [];
        int size = Grid3D<Voxel>.ChunkSize;
        void AddReference(dvec3 low, dvec3 high)
        {
            for (int z = Math.Max(0, (int)Math.Floor(low.z / size)); z <= Math.Min(VoxelMap.HeightInChunks - 1, (int)Math.Floor(high.z / size)); z++)
            for (int y = (int)Math.Floor(low.y / size); y <= (int)Math.Floor(high.y / size); y++)
            for (int x = (int)Math.Floor(low.x / size); x <= (int)Math.Floor(high.x / size); x++)
                reference.Add(new ivec3(x, y, z));
        }

        // Clustered, overlapping, repeated requests, with negative coordinates and boxes
        // crossing chunk boundaries. This is the seeded pile's streaming workload.
        Random random = new(71);
        for (int i = 0; i < 5000; i++)
        {
            dvec3 center = new(i % 24 * 2 - 24, i / 24 % 24 * 2 - 24, i / 576 * 2 + 16);
            double radius = 8 + random.NextDouble() * 4;
            requests.Add((center - radius, center + radius));
        }
        requests.Add((new dvec3(-size, -size, -size), new dvec3(0)));
        requests.Add((new dvec3(0, 0, -size * 2), new dvec3(size, size, -size)));
        requests.Add((new dvec3(0, 0, VoxelMap.HeightInChunks * size), new dvec3(size, size, (VoxelMap.HeightInChunks + 1) * size)));
        foreach ((dvec3 low, dvec3 high) in requests)
        {
            AddReference(low, high);
            coverage.Add(low, high);
        }
        bool ok = Check("coverage and insertion order match original expansion", coverage.Chunks.SequenceEqual(reference) ? 1 : 0, 1, 1);
        int count = coverage.Chunks.Count;
        foreach ((dvec3 low, dvec3 high) in requests.AsEnumerable().Reverse())
            coverage.Add(low, high);
        ok &= Check("duplicate requests preserve order", coverage.Chunks.SequenceEqual(reference) ? 1 : 0, 1, 1);

        const int repeats = 20;
        Stopwatch watch = Stopwatch.StartNew();
        for (int iteration = 0; iteration < repeats; iteration++)
        {
            reference.Clear();
            foreach ((dvec3 low, dvec3 high) in requests)
                AddReference(low, high);
        }
        double original = watch.Elapsed.TotalMilliseconds / repeats;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int iteration = 0; iteration < repeats; iteration++)
        {
            coverage.Clear();
            foreach ((dvec3 low, dvec3 high) in requests)
                coverage.Add(low, high);
        }
        double indexed = watch.Elapsed.TotalMilliseconds / repeats;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"   {requests.Count} requests, {count} chunks: original {original:F3} ms, deduplicated {indexed:F3} ms, allocated {allocated / repeats} B/step");
        ok &= Check("repeated-step coverage", coverage.Chunks.SequenceEqual(reference) ? 1 : 0, 1, 1);
        ok &= Check("steady-step allocation", allocated, 0, 0);
        coverage.Clear();
        coverage.Add(new dvec3(0), new dvec3(0));
        ok &= Check("old requests cleared", coverage.Chunks.Count, 1, 1);
        ok &= Check("fresh origin request", coverage.Chunks.Contains(new ivec3(0)) ? 1 : 0, 1, 1);
        return ok;
    }

    /// <summary>The first point of a coarse scan, from (8, 8) outward, where <paramref name="wanted"/> holds.</summary>
    private static ivec2? Find(TerrainGenerator generator, Func<int, int, bool> wanted)
    {
        for (int ring = 0; ring < 400; ring++)
        for (int k = -ring; k <= ring; k++)
        {
            foreach (ivec2 point in (ivec2[])[new(k, -ring), new(k, ring), new(-ring, k), new(ring, k)])
            {
                ivec2 at = 32 * point + 8;
                if (wanted(at.x, at.y))
                    return at;
            }
        }

        return null;
    }

    /// <summary>
    /// The generator over 8 × 8 km: heights in range, every biome present in a fair share,
    /// the same columns from the same seed, and how long a column takes.
    /// </summary>
    private static bool Generation()
    {
        TerrainGenerator generator = new(Seed);
        Dictionary<TerrainGenerator.Biome, int> biomes = [];
        int lowest = int.MaxValue;
        int highest = int.MinValue;
        double sum = 0.0;
        int samples = 0;
        for (int x = -4000; x < 4000; x += 16)
        for (int y = -4000; y < 4000; y += 16)
        {
            int height = generator.Height(x, y);
            lowest = Math.Min(lowest, height);
            highest = Math.Max(highest, height);
            sum += height;
            samples++;
            TerrainGenerator.Biome biome = generator.BiomeOf(x, y);
            biomes[biome] = biomes.GetValueOrDefault(biome) + 1;
        }

        Console.WriteLine($"   heights {lowest} .. {highest}, mean {sum / samples:F1}");
        foreach ((TerrainGenerator.Biome biome, int count) in biomes.OrderByDescending(b => b.Value))
            Console.WriteLine($"      {biome,-10} {count / (double)samples:P1}");

        bool ok = Check("lowest ground", lowest, 12, TerrainGenerator.SeaLevel - 1);
        ok &= Check("highest ground", highest, 110, 221);
        foreach (TerrainGenerator.Biome biome in Enum.GetValues<TerrainGenerator.Biome>())
            ok &= Check($"share of {biome}", biomes.GetValueOrDefault(biome) / (double)samples, 0.03, 0.6);

        // The spawn area is the hand-made one, whatever the seed.
        TerrainGenerator other = new(Seed + 1000);
        int differences = 0;
        for (int x = 0; x < TerrainGenerator.SpawnSize; x++)
        for (int y = 0; y < TerrainGenerator.SpawnSize; y++)
            differences += generator.Height(x, y) != other.Height(x, y) || generator.Height(x, y) != TerrainGenerator.GroundHeight(x, y) + 64 ? 1 : 0;
        ok &= Check("spawn area cells that differ between seeds", differences, 0, 0);

        ivec2[] columns = [.. Enumerable.Range(0, 200).Select(i => new ivec2(37 + i % 20, -55 + i / 20))];
        Stopwatch watch = Stopwatch.StartNew();
        MapColumn[] first = [.. columns.Select(generator.GenerateColumn)];
        double perColumn = watch.Elapsed.TotalMilliseconds / columns.Length;
        MapColumn[] second = [.. columns.Select(new TerrainGenerator(Seed).GenerateColumn)];
        int mismatched = 0;
        for (int i = 0; i < columns.Length; i++)
        {
            bool same = first[i].Chunks.Length == second[i].Chunks.Length
                && first[i].Chunks.Zip(second[i].Chunks).All(p => p.First.Z == p.Second.Z && p.First.Cells.AsSpan().SequenceEqual(p.Second.Cells));
            mismatched += same ? 0 : 1;
        }

        Console.WriteLine($"   {perColumn:F2} ms a column on one thread");
        ok &= Check("columns that differ when generated again", mismatched, 0, 0);
        ok &= Check("time to generate a column (ms)", perColumn, 0, 10);
        return ok;
    }

    /// <summary>
    /// Columns generated one by one meet: around a forest and a savanna, every leaf of the
    /// trees, which cross column borders, reaches wood through leaves, so no tree was grown on
    /// one side of a border and not on the other.
    /// </summary>
    private static bool Seams()
    {
        TerrainGenerator generator = new(Seed);
        bool ok = true;
        foreach (TerrainGenerator.Biome biome in (TerrainGenerator.Biome[])[TerrainGenerator.Biome.Forest, TerrainGenerator.Biome.Savanna])
        {
            ivec2 at = Find(generator, (x, y) => TerrainGenerator.SpawnSize + 200 < Math.Abs(x) + Math.Abs(y) && generator.BiomeOf(x, y) == biome && generator.BiomeOf(x + 48, y + 48) == biome)!.Value;
            ivec2 first = ChunkStreamer.ColumnAt(new dvec3(at.x, at.y, 0));
            Grid3D<Voxel> grid = new();
            for (int cx = 0; cx < 4; cx++)
            for (int cy = 0; cy < 4; cy++)
            {
                MapColumn column = generator.GenerateColumn(first + new ivec2(cx, cy));
                foreach ((int z, Voxel[] cells) in column.Chunks)
                    grid.SetChunk(new ivec3(column.Coordinate, z), cells);
            }

            // Away from the block's outer edge, where the neighbours' trees are missing.
            ivec3 low = new(first * Grid3D<Voxel>.ChunkSize + 4, 0);
            ivec3 high = new((first + 4) * Grid3D<Voxel>.ChunkSize - 4, int.MaxValue);
            bool IsWood(ivec3 cell) => grid[cell].Type is BlockType.OakLog or BlockType.AcaciaLog;
            int leaves = 0;
            int loose = 0;
            int trees = 0;
            foreach ((ivec3 cell, Voxel voxel) in grid.Occupied())
            {
                if (cell.x < low.x || cell.y < low.y || cell.x >= high.x || cell.y >= high.y)
                    continue;

                if (IsWood(cell) && grid[cell + new ivec3(0, 0, -1)].Type is BlockType.Grass)
                    trees++;
                if (voxel.Type != BlockType.Leaves)
                    continue;

                leaves++;
                HashSet<ivec3> crown = [];
                bool held = grid.FloodFill(cell, crown, include: (_, v) => v.Type == BlockType.Leaves || v.Type is BlockType.OakLog or BlockType.AcaciaLog, stop: (c, _) => IsWood(c));
                loose += held ? 0 : 1;
            }

            int checkedTrees = 0;
            int badWood = 0;
            foreach ((ivec3 cell, Voxel voxel) in grid.Occupied())
            {
                if (!IsWood(cell) || cell.x < low.x || cell.y < low.y || cell.x >= high.x || cell.y >= high.y
                    || cell.z != generator.Height(cell.x, cell.y) - 3
                    || !IsWood(cell + new ivec3(0, 0, 4)))
                    continue;

                checkedTrees++;
                ivec3 root = cell + new ivec3(0, 0, 3);
                void Expect(ivec3 position, BlockOrientation orientation)
                {
                    if (grid[position].Type != voxel.Type || grid[position].Orientation != orientation)
                        badWood++;
                }
                foreach (ivec3 side in (ivec3[])[new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)])
                {
                    BlockOrientation horizontal = side.x != 0 ? BlockOrientation.LogX : BlockOrientation.LogY;
                    Expect(root + side - new ivec3(0, 0, 1), horizontal);
                    Expect(root + side - new ivec3(0, 0, 2), BlockOrientation.Default);
                    Expect(root + 2 * side - new ivec3(0, 0, 2), horizontal);
                }

                // A flood through wood alone must reach every root, without relying on soil.
                HashSet<ivec3> connected = [];
                grid.FloodFill(cell, connected, include: (_, v) => v.Type == voxel.Type);
                foreach (ivec3 side in (ivec3[])[new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)])
                    if (!connected.Contains(root + 2 * side - new ivec3(0, 0, 2)))
                        badWood++;

                ivec3 stem = root;
                while (IsWood(stem + new ivec3(0, 0, 1))) stem.z++;
                if (voxel.Type == BlockType.OakLog)
                {
                    Expect(stem, BlockOrientation.Default);
                    foreach (ivec3 side in (ivec3[])[new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)])
                    for (int length = 1; length <= 2; length++)
                        Expect(stem - new ivec3(0, 0, 1) + length * side,
                            side.x != 0 ? BlockOrientation.LogX : BlockOrientation.LogY);
                }
                else
                {
                    foreach (ivec3 side in (ivec3[])[new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)])
                    {
                        if (!IsWood(stem + side + new ivec3(0, 0, 1))) continue;
                        Expect(stem + side, side.x != 0 ? BlockOrientation.LogX : BlockOrientation.LogY);
                        ivec3 crown = stem + side;
                        while (IsWood(crown + new ivec3(0, 0, 1))) crown.z++;
                        for (int dx = -2; dx <= 2; dx++)
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            if (Math.Abs(dx) + Math.Abs(dy) > 2) continue;
                            Expect(crown + new ivec3(dx, dy, 0), dx == 0 && dy == 0 ? BlockOrientation.Default
                                : Math.Abs(dx) >= Math.Abs(dy) ? BlockOrientation.LogX : BlockOrientation.LogY);
                        }
                    }
                }
            }
            ok &= Check($"{biome}: trees checked for connected, oriented wood", checkedTrees, 1, double.PositiveInfinity);
            ok &= Check($"{biome}: disconnected roots or incorrectly oriented logs", badWood, 0, 0);

            int crossing = 0;
            foreach ((ivec3 cell, Voxel voxel) in grid.Occupied())
            {
                bool border = (cell.x & Grid3D<Voxel>.ChunkMask) == 0 || (cell.y & Grid3D<Voxel>.ChunkMask) == 0;
                crossing += border && voxel.Type == BlockType.Leaves ? 1 : 0;
            }

            Console.WriteLine($"   {biome} at {at}: {leaves} leaves, {crossing} on column borders, {loose} loose");
            ok &= Check($"{biome}: leaves on column borders", crossing, 1, double.PositiveInfinity);
            ok &= Check($"{biome}: leaves held by nothing", loose, 0, 0);
        }

        return ok;
    }

    /// <summary>
    /// The generated terrain stands up under its own weight: a block of 4 × 4 columns of each
    /// biome, analysed in the player window with surrounding columns, has nothing near its elastic limit, and
    /// nothing breaks in the seconds after.
    /// </summary>
    private static bool Stands()
    {
        TerrainGenerator generator = new(Seed);
        bool ok = true;
        foreach (TerrainGenerator.Biome biome in Enum.GetValues<TerrainGenerator.Biome>())
        {
            ivec2 at = Find(generator, (x, y) => TerrainGenerator.SpawnSize + 200 < Math.Abs(x) + Math.Abs(y) && generator.BiomeOf(x, y) == biome && generator.BiomeOf(x + 32, y + 32) == biome)!.Value;
            ivec2 first = ChunkStreamer.ColumnAt(new dvec3(at.x, at.y, 0)) - 1;

            using World world = CreateWorld();
            world.FollowPlayer = true;
            int broken = 0;
            world.BlockBroken += (_, _, _, _) => broken++;
            for (int cx = -1; cx <= 4; cx++)
            for (int cy = -1; cy <= 4; cy++)
                world.LoadColumn(generator.GenerateColumn(first + new ivec2(cx, cy)));
            ivec2 center = (first + 2) * Grid3D<Voxel>.ChunkSize;
            Player player = world.SpawnPlayer(new dvec3(center, generator.Height(center.x, center.y) + 2));
            player.Flying = true;
            world.ApplyTopologyChanges();

            Stopwatch watch = Stopwatch.StartNew();
            while (world.Map.Stress is not { Settled: true } stress || stress.Version != world.Map.TopologyVersion)
            {
                world.StepOnce();
                if (watch.Elapsed.TotalSeconds > 60)
                    break;
            }

            // Match gameplay: neighboring terrain holds the boundary of the moving analysis window.
            ivec3 low = new(first * Grid3D<Voxel>.ChunkSize + 3, 0);
            ivec3 high = new((first + 4) * Grid3D<Voxel>.ChunkSize - 3, int.MaxValue);
            Dictionary<BlockType, double> worst = [];
            foreach ((ivec3 cell, Voxel voxel) in world.Map.Grid.Occupied())
            {
                if (cell.x < low.x || cell.y < low.y || cell.x >= high.x || cell.y >= high.y)
                    continue;

                worst[voxel.Type] = Math.Max(worst.GetValueOrDefault(voxel.Type), world.Map.StressRatio(cell, voxel));
            }

            foreach ((ivec3 cell, Voxel voxel) in world.Map.Grid.Occupied().Where(c => c.Cell.x >= low.x && c.Cell.y >= low.y && c.Cell.x < high.x && c.Cell.y < high.y).OrderByDescending(c => world.Map.StressRatio(c.Cell, c.Value)).Take(3))
            {
                if (world.Map.StressRatio(cell, voxel) < 0.6)
                    break;

                MYT.Kit.Structural.CellStress s = world.Map.StaticStress(cell);
                string around = string.Join(" ", Grid3D<Voxel>.FaceDirections.Take(4).Select(d => generator.Height(cell.x + d.x, cell.y + d.y) - generator.Height(cell.x, cell.y)));
                Console.WriteLine($"      {voxel.Material.Name} at {cell}, ground {generator.Height(cell.x, cell.y)}, neighbours {around}: {world.Map.StressRatio(cell, voxel):F2} (tension {s.Tension / 1e3:F0}, compression {s.Compression / 1e3:F0}, shear {s.Shear / 1e3:F0} kPa)");
            }

            for (int step = 0; step < 180; step++)
                world.StepOnce();

            Console.WriteLine($"   {biome} at {at}: {world.Map.Grid.Count} blocks, settled in {watch.Elapsed.TotalSeconds:F1} s; worst: {string.Join(", ", worst.Where(w => w.Value > 0.05).OrderByDescending(w => w.Value).Select(w => $"{Blocks.Get(w.Key).Name} {w.Value:F2}"))}");
            ok &= Check($"{biome}: analysis settled", world.Map.Stress is { Settled: true } ? 1 : 0, 1, 1);
            ok &= Check($"{biome}: most loaded block, of its elastic limit", worst.Count > 0 ? worst.Values.Max() : 0.0, 0.0, 0.6);
            ok &= Check($"{biome}: blocks broken", broken, 0, 0);
        }

        return ok;
    }

    /// <summary>
    /// The player flies out from the spawn area, over 600 m and back, with the map streaming
    /// in and out: it never falls through, the map holds only the columns around it and
    /// colliders only next to it, a parked object waits where it was left, and a block placed
    /// before leaving, whose column was unloaded meanwhile, is still there on coming back.
    /// Then it skims the ground for 240 m, where the stress analysis follows it too. Moving
    /// the simulation along must not stall a frame much past 33 ms.
    /// </summary>
    private static bool Streaming()
    {
        TerrainGenerator generator = new(Seed, testFixture: true);
        using World world = CreateWorld();
        world.FollowPlayer = true;
        generator.BuildSpawn(world, testing: true);
        world.ApplyTopologyChanges();
        Player player = world.SpawnPlayer(TerrainGenerator.SpawnPoint);
        using ChunkStreamer streamer = new(world, generator, 6);

        int frames = 0;
        int heldFrames = 0;
        double lowest = double.PositiveInfinity;
        int mostColumns = 0;
        int mostColliders = 0;
        List<double> times = [];
        Stopwatch frame = new();
        void Frames(int count, Func<bool>? until = null)
        {
            for (int k = 0; k < count; k++)
            {
                frame.Restart();
                streamer.Update(player.Feet);
                heldFrames += world.RegionReady ? 0 : 1;
                world.Update(World.Step);
                times.Add(frame.Elapsed.TotalMilliseconds);
                if (frame.Elapsed.TotalMilliseconds > 33.0)
                    Console.WriteLine($"      frame {frames} took {frame.Elapsed.TotalMilliseconds:F0} ms at x = {player.Feet.x:F0}, z = {player.Feet.z:F0}");
                frames++;
                mostColumns = Math.Max(mostColumns, streamer.LoadedColumns);
                mostColliders = Math.Max(mostColliders, world.Physics.FixedBodies.Count);
                if (world.RegionReady)
                    lowest = Math.Min(lowest, player.Feet.z - generator.Height((int)Math.Floor(player.Feet.x), (int)Math.Floor(player.Feet.y)));
                if (until != null && until())
                    return;
                Thread.Sleep(2);
            }
        }

        Frames(600, () => world.RegionReady && streamer.Generating == 0 && frames > 120);
        bool ok = Check("frames held at the start, waiting for the ground", heldFrames, 0, 600);

        // Something to come back to: a glass block on the ground, and the smaller loose object, parked once far.
        ivec3 marker = new(40, 8, TerrainGenerator.GroundHeight(40, 8) + 1);
        world.SetBlock(world.Map, marker, BlockType.Glass);
        VoxelObject loose = world.Objects.MinBy(o => o.Grid.Count)!;
        Frames(60);
        dvec3 looseAt = loose.Position;

        player.Flying = true;
        player.MoveTo(new dvec3(player.Feet.x, player.Feet.y, 130));
        heldFrames = 0;
        Stopwatch watch = Stopwatch.StartNew();
        player.Controls = new PlayerControls(new dvec3(3, 0, 0), false);
        Frames(4000, () => player.Feet.x > 640);
        double outbound = watch.Elapsed.TotalSeconds;
        bool unloaded = !world.Map.IsLoaded(VoxelMap.ColumnOfCell(marker));
        bool looseParked = loose.Parked;
        int columnsAway = streamer.LoadedColumns;
        int collidersAway = world.Physics.FixedBodies.Count;

        player.Controls = new PlayerControls(new dvec3(-3, 0, 0), false);
        Frames(4000, () => player.Feet.x < 40);
        player.Controls = default;
        player.MoveTo(new dvec3(36, 12, TerrainGenerator.GroundHeight(36, 12) + 1.5));
        player.Flying = false;
        Frames(600, () => world.RegionReady && streamer.Generating == 0 && !loose.Parked);
        Frames(120);

        Console.WriteLine($"   flew out to x = 640 in {outbound:F1} s, {frames} frames in all, {heldFrames} held for the ground; at most {mostColumns} columns and {mostColliders} colliders; {streamer.KeptColumns.Count()} changed columns kept");
        double[] flight = [.. times.Skip(240).Order()];
        bool markerBack = world.Map.Grid[marker].Type == BlockType.Glass;
        bool looseBack = loose.Body != null;
        double looseMoved = (loose.Position - looseAt).Length;

        // Over the ground, 15 m/s, where the map's stress analysis follows too.
        times.Clear();
        player.Flying = true;
        for (double x = 40; x < 280; x += 0.25)
        {
            player.MoveTo(new dvec3(x, 12, generator.Height((int)Math.Floor(x), 12) + 1.5));
            Frames(1);
        }

        double[] ground = [.. times.Order()];
        player.Flying = false;
        foreach ((string name, double[] sorted) in (ValueTuple<string, double[]>[])[("flying high", flight), ("over the ground", ground)])
            Console.WriteLine($"   frame {name}: median {sorted[sorted.Length / 2]:F1} ms, 99th percentile {sorted[sorted.Length * 99 / 100]:F1} ms, worst {sorted[^1]:F1} ms, {sorted.Count(t => t > 33.0)} frames over 33 ms");
        ok &= Check("marker's column unloaded while away", unloaded ? 1 : 0, 1, 1);
        ok &= Check("loose parked while away", looseParked ? 1 : 0, 1, 1);
        ok &= Check("columns loaded far away", columnsAway, 100, 260);
        // Colliders follow the bodies that move: the player 130 m up has none near.
        ok &= Check("colliders far away, flying high", collidersAway, 0, 0);
        ok &= Check("most colliders at once", mostColliders, 1, 64);
        ok &= Check("most columns loaded", mostColumns, 100, 260);
        ok &= Check("lowest the feet came, above the ground (m)", lowest, 0.5, double.PositiveInfinity);
        ok &= Check("marker block back", markerBack ? 1 : 0, 1, 1);
        ok &= Check("loose back in the physics", looseBack ? 1 : 0, 1, 1);
        ok &= Check("loose moved while away (m)", looseMoved, 0, 0.05);
        ok &= Check("player health", player.Health, 1, 1);
        ok &= Check("frames over 33 ms flying high, past the start", flight.Count(t => t > 33.0), 0, 2);
        ok &= Check("frames over 33 ms over the ground", ground.Count(t => t > 33.0), 0, 5);
        return ok;
    }
}
