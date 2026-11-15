using System.Diagnostics;
using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class WorldLodTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("world-periodic", Periodic),
        ("terrain-lod", Lod),
        ("terrain-lod-edits", LodEdits),
        ("world-wrapped-edits", WrappedEdits)
    ];

    private static bool Expect(string label, bool condition)
    {
        Console.WriteLine($"   {(condition ? "ok" : "BAD")} {label}");
        return condition;
    }

    private static bool Periodic()
    {
        bool ok = Expect("half-open bounds [-6000,6000)", WorldTopology.Wrap(-6000) == -6000
            && WorldTopology.Wrap(6000) == -6000 && WorldTopology.Wrap(6001) == -5999
            && WorldTopology.Wrap(-6001) == 5999 && WorldTopology.Wrap(int.MinValue) >= -6000);
        foreach (int seed in new[] { 1, 713 })
        {
            TerrainGenerator generator = new(seed);
            foreach (ivec2 c in new ivec2[] { new(0), new(-375, 374), new(374, -375), new(-376, 375), new(29, -94) })
            {
                MapColumn a = generator.GenerateColumn(c);
                MapColumn b = generator.GenerateColumn(c + new ivec2(750, -750));
                ok &= Expect($"seed {seed}, column {c} repeats by translation including caves, ores, trees",
                    a.Chunks.Length == b.Chunks.Length && a.Chunks.Zip(b.Chunks).All(p => p.First.Z == p.Second.Z && p.First.Cells.SequenceEqual(p.Second.Cells)));
            }
            foreach (int y in new[] { -6000, -3901, 0, 2337, 5999 })
            {
                double a = Noise.Fractal(6000 - .0001, y, 220, 4, seed);
                double b = Noise.Fractal(-6000 + .0001, y, 220, 4, seed);
                ok &= Expect("noise remains continuous across the join", Math.Abs(a - b) < .0001);
                ok &= Expect("surface has no seam cliff", Math.Abs(generator.Height(5999, y) - generator.Height(6000, y)) < 5);
            }
        }
        return ok;
    }

    private static bool Lod()
    {
        using TerrainLod lod = new(new TerrainGenerator(1));
        dvec3 eye = new(-6001, 5999, 200);
        lod.Select(eye, 224);
        var tiles = lod.Wanted.ToArray();
        dvec3 selectionEye = new dvec3((ivec3)dvec3.Floor(eye / 16) * 16) + 8;
        static double DistanceSquared(TerrainLod.Tile tile, dvec3 eye)
        {
            double x = Math.Max(Math.Max(tile.X - eye.x, eye.x - tile.X - tile.Size), 0);
            double y = Math.Max(Math.Max(tile.Y - eye.y, eye.y - tile.Y - tile.Size), 0);
            double z = Math.Max(Math.Max(tile.Z - eye.z, eye.z - tile.Z - tile.Height), 0);
            return x * x + y * y + z * z;
        }
        bool ok = Expect("bounded tile count for an infinite horizon", tiles.Length < 65536 && tiles.Length > 100);
        ok &= Expect("all LOD generation requests are nearest first", tiles.Zip(tiles.Skip(1)).All(p => DistanceSquared(p.First, selectionEye) <= DistanceSquared(p.Second, selectionEye)));
        long area = tiles.Sum(t => (long)t.Size * t.Size * t.Height);
        ok &= Expect("volume hierarchy covers all nine roots exactly", area == 9L * TerrainLod.RootSize * TerrainLod.RootSize * TerrainLod.WorldHeight);
        for (int y = -256; y <= 256; y += 16)
        for (int x = -256; x <= 256; x += 16)
        {
            double px = eye.x + x + .5, py = eye.y + y + .5;
            if (tiles.Count(t => px >= t.X && px < t.X + t.Size && py >= t.Y && py < t.Y + t.Size && eye.z >= t.Z && eye.z < t.Z + t.Height) != 1) return Expect("near LOD has neither gaps nor overlaps", false);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) lod.Select(eye, 224);
        ok &= Expect("stationary selection reuses cached volume hierarchy without allocations", GC.GetAllocatedBytesForCurrentThread() == before);
        var a = lod.Build(new(-6000, 5984, 16));
        var b = lod.Build(new(6000, -6016, 16));
        ok &= Expect("LOD geometry and materials repeat in the same orientation", a.Vertices.Select(v => (v.Position, v.Material, v.TextureLayer, v.Face)).SequenceEqual(b.Vertices.Select(v => (v.Position, v.Material, v.TextureLayer, v.Face))));
        ok &= Expect("LOD cube faces are finite", a.Vertices.All(v => float.IsFinite(v.Position.x) && float.IsFinite(v.Position.y) && float.IsFinite(v.Position.z)));
        ok &= Expect("detail radius is identical on X, Y and Z", TerrainLod.UsesFullDetail(new(0), new(8), 64)
            && !TerrainLod.UsesFullDetail(new(5, 0, 0), new(8), 64)
            && !TerrainLod.UsesFullDetail(new(0, 5, 0), new(8), 64)
            && !TerrainLod.UsesFullDetail(new(0, 0, 5), new(8), 64));
        lod.Select(new(8), 64);
        foreach (int resolution in new[] { 2, 4, 8, 16, 32 })
        {
            double distance = 48 * resolution;
            foreach (dvec3 point in new[] { new dvec3(8 + distance, 8, 8), new dvec3(8, 8 + distance, 8) })
            {
                var selected = lod.Wanted.Single(t => point.x >= t.X && point.x < t.X + t.Size && point.y >= t.Y && point.y < t.Y + t.Size && point.z >= t.Z && point.z < t.Z + t.Height);
                ok &= Expect($"distance {distance} uses {resolution}-block voxels in {16 * resolution}-block chunks", selected.Resolution == resolution && selected.Size == 16 * resolution && selected.VerticalResolution == resolution);
            }
        }
        lod.Select(new(eye.x, eye.y, 1200), 224);
        ok &= Expect("flying upwards reduces ground voxel detail", lod.Wanted.Min(t => t.Size) > 16);
        Console.WriteLine($"   {tiles.Length} patches, {tiles.Length * a.Vertices.Length / 2:N0} maximum triangles before culling");
        return ok;
    }

    private static bool LodEdits()
    {
        int[,] layers = new int[Blocks.Count, 6];
        for (int type = 0; type < Blocks.Count; type++)
        for (int face = 0; face < 6; face++) layers[type, face] = (type * 6 + face) % 256;
        using TerrainLod lod = new(new TerrainGenerator(1), layers);
        static MapColumn Ground(ivec2 column, bool crater)
        {
            List<(int, Voxel[])> chunks = [];
            for (int z = 0; z < 4; z++)
            {
                Voxel[] cells = new Voxel[4096];
                for (int i = 0; i < cells.Length; i++)
                {
                    int x = column.x * 16 + (i & 15), y = column.y * 16 + ((i >> 4) & 15), height = z * 16 + (i >> 8);
                    bool removed = crater && x >= 8 && x < 24 && y >= 8 && y < 24 && height >= 32;
                    cells[i] = new(removed ? BlockType.Air : height == 63 ? BlockType.Grass : BlockType.Stone, 0);
                }
                chunks.Add((z, cells));
            }
            return new(column, chunks.ToArray());
        }
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++) lod.Publish(Ground(new(x, y), false));
        var before = lod.Build(new(0, 0, 64));
        long version = lod.Version;
        bool ok = Expect("coarse terrain initially contains ground", lod.CellAt(8, 8, 48, 8, 8) == BlockType.Stone);
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++) lod.Publish(Ground(new(x, y), true));
        var after = lod.Build(new(0, 0, 64));
        ok &= Expect("crater replaces cached coarse voxels", lod.CellAt(8, 8, 48, 8, 8) == BlockType.Air && lod.CellAt(8, 8, 24, 8, 8) == BlockType.Stone);
        ok &= Expect("translated crater remains visible", lod.CellAt(12008, -11992, 48, 8, 8) == BlockType.Air);
        ok &= Expect("an edit invalidates affected meshes, including translated copies", !lod.IsCurrent(new(0, 0, 64), version) && !lod.IsCurrent(new(12000, -12000, 64), version));
        ok &= Expect("distant unrelated meshes keep their cached data", lod.IsCurrent(new(1000, 1000, 64), version));
        ok &= Expect("crater changes geometry and reveals stone rather than grass", !before.Vertices.Select(v => (v.Position, v.Material, v.TextureLayer, v.Face)).SequenceEqual(after.Vertices.Select(v => (v.Position, v.Material, v.TextureLayer, v.Face)))
            && after.Vertices.Any(v => v.Position.z == 32 && v.Face == 4 && v.Material == BlockType.Stone));
        ok &= Expect("coarse faces retain the block texture for their material and face", after.Vertices.All(v => v.TextureLayer == layers[(int)v.Material, v.Face]));
        ok &= Expect("packed GPU vertex matches the 8-byte full-detail format", System.Runtime.InteropServices.Marshal.SizeOf<TerrainVertex>() == 8);
        bool normals = true;
        for (int i = 0; i < after.Vertices.Length; i += 4)
        {
            var a = after.Vertices[i]; var b = after.Vertices[i + 1]; var c = after.Vertices[i + 2];
            vec3 cross = vec3.Cross(b.Position - a.Position, c.Position - a.Position).Normalized;
            ivec3 direction = MYT.Kit.Vulkan.Grid3D<Voxel>.FaceDirections[(int)a.Face];
            normals &= vec3.Dot(cross, new vec3(direction)) > .999f;
        }
        ok &= Expect("every triangle has its exact outward axis normal", normals);
        Voxel[] seabed = new Voxel[4096];
        for (int i = 0; i < seabed.Length; i++) seabed[i] = new(i >= 3840 ? BlockType.Water : BlockType.Stone, 0);
        lod.Publish(new(new(0), [(3, seabed)]));
        var coast = lod.Build(new(0, 0, 32, 32));
        ok &= Expect("mixed water/stone voxels keep an opaque seabed", lod.CellAt(0, 0, 62, 2, 2) == BlockType.Stone
            && coast.Vertices.Any(v => v.Face == 4 && v.Position.z == 32 && v.Position.x < 16 && v.Position.y < 16 && v.Material == BlockType.Stone));
        Voxel[] roof = new Voxel[4096];
        for (int z = 8; z < 16; z++)
        for (int y = 8; y < 16; y++)
        for (int x = 8; x < 16; x++) roof[x + 16 * (y + 16 * z)] = new(BlockType.Stone, 0);
        lod.Publish(new(new(0), [(5, roof)]));
        var overhang = lod.Build(new(0, 0, 32, 64));
        ok &= Expect("volume LOD keeps air below suspended structures and downward faces", lod.CellAt(8, 8, 80, 2, 2) == BlockType.Air
            && lod.CellAt(8, 8, 88, 2, 2) == BlockType.Stone && overhang.Vertices.Any(v => v.Face == 5 && v.Position.z == 24));
        return ok;
    }

    private static bool WrappedEdits()
    {
        using World world = new(TestGpu.Vulkan.Vk, TestGpu.Vulkan.Device, TestGpu.Vulkan.PhysicalDevice,
            TestGpu.Vulkan.CommandPool, TestGpu.Vulkan.Queue, TestGpu.Shaders);
        using ChunkStreamer streamer = new(world, new TerrainGenerator(1), 4);
        bool Settle(dvec3 focus)
        {
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                streamer.Update(focus);
                if (streamer.Generating == 0 && world.Map.IsLoaded(ChunkStreamer.ColumnAt(focus))) return true;
                Thread.Sleep(1);
            } while (timer.Elapsed.TotalSeconds < 30);
            return false;
        }
        if (!Settle(new(0, 0, 80))) return false;
        ivec3 cell = new(1, 1, 180);
        world.SetBlock(world.Map, cell, BlockType.DiamondOre);
        VoxelObject marker = world.Spawn(TerrainGenerator.Box(new ivec3(1), BlockType.Stone), new(2, 2, 190), dquat.Identity);
        ivec3 station = new(2, 2, 180);
        world.SetBlock(world.Map, station, BlockType.Furnace);
        Furnace furnace = world.FurnaceAt(new VoxelHit(world.Map, station, new(0, 0, 1), 0));
        if (!Settle(new(12000, 0, 80))) return false;
        bool ok = Expect("edited terrain returns after one positive loop", world.Map.Grid[cell + new ivec3(12000, 0, 0)].Type == BlockType.DiamondOre);
        ok &= Expect("loose objects follow the same translated world", marker.Position == new dvec3(12002, 2, 190));
        ok &= Expect("furnace contents keep their identity across the border", world.Furnaces.TryGetValue((world.Map, station + new ivec3(12000, 0, 0)), out var moved) && ReferenceEquals(moved, furnace));
        world.SetBlock(world.Map, cell + new ivec3(12000, 0, 0), BlockType.GoldOre);
        world.SpawnPlayer(new(12000, 0, 200));
        WorldSnapshot snapshot = WorldFile.Capture(world, streamer);
        ok &= Expect("save has one current version of the canonical edited column", snapshot.Columns.Count(c => c.Coordinate == new ivec2(0)) == 1);
        if (!Settle(new(-12000, 12000, 80))) return false;
        ok &= Expect("latest edit survives negative and diagonal loops", world.Map.Grid[cell + new ivec3(-12000, 12000, 0)].Type == BlockType.GoldOre);
        return ok;
    }
}
