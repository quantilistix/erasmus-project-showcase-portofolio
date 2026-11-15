using GlmSharp;

using MYT.Kit.Vulkan;

using VoxelGame;

namespace VoxelTests;


/// <summary>
/// Checks of what the game keeps on disk: the saved worlds' folders and records, the settings,
/// and a world saved and played again, its edits, its objects and its player as they were,
/// whether the columns changed were loaded or not when it was saved.
/// </summary>
internal static class SaveTests
{
    private const int Seed = 1;

    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("save-world-store", Store),
        ("save-settings", Settings),
        ("save-damaged-file", DamagedFile),
        ("save-round-trip", RoundTrip),
        ("save-no-despawn", NoDespawn),
        ("save-particles-daytime", ParticlesAndDaytime)
    ];

    private static bool ParticlesAndDaytime()
    {
        string directory = ScratchDirectory();
        try
        {
            using World world = CreateWorld();
            using ChunkStreamer streamer = new(world, new TerrainGenerator(Seed), ChunkStreamer.MinRenderDistance);
            world.SpawnPlayer(new(0, 0, 10));
            world.DaylightSeconds = DaylightCycle.DurationSeconds * 12.7;
            var source = world.Spawn([(ivec3.Zero, BlockType.OakLog)], new(30, 20, 50), dquat.Identity);
            world.Particles.Emit(source, ivec3.Zero, BlockType.OakLog, 32, true);
            world.Particles.EmitExplosion(new(20, 20, 50));
            world.Particles.Update(world, .07);
            WorldSnapshot snapshot = WorldFile.Capture(world, streamer);
            string path = Path.Combine(directory, "world.dat");
            WorldFile.Write(path, snapshot);
            WorldSnapshot read = WorldFile.Read(path);
            bool ok = Expect("chips, dust and puffs retain every saved property", read.Particles.SequenceEqual(snapshot.Particles)
                && read.Particles.Select(p => p.Effect).Distinct().Count() == 3);
            ok &= Expect("day and moon clock and flashes round-trip exactly", read.DaylightSeconds == snapshot.DaylightSeconds
                && DaylightCycle.Evaluate(read.DaylightSeconds) == DaylightCycle.Evaluate(snapshot.DaylightSeconds)
                && read.Flashes.SequenceEqual(snapshot.Flashes));
            world.Particles.Update(world, .1);
            ok &= Expect("particle capture owns its snapshot during background saving", !world.Particles.Capture().SequenceEqual(snapshot.Particles));
            using World restored = CreateWorld();
            using ChunkStreamer restoredStreamer = new(restored, new TerrainGenerator(Seed), ChunkStreamer.MinRenderDistance);
            WorldFile.Restore(read, restored, restoredStreamer);
            ok &= Expect("restore resumes particles and the same daytime", restored.Particles.Capture().SequenceEqual(snapshot.Particles)
                && restored.DaylightSeconds == snapshot.DaylightSeconds);
            restored.Particles.Enabled = false;
            restored.Particles.Restore(read.Particles, read.Flashes);
            ok &= Expect("particles disabled in settings remain disabled on load", restored.Particles.Count == 0);

            // Version 5 ends immediately before the appended clock/particle section.
            WorldFile.Write(path, snapshot with { Particles = [], Flashes = [], DaylightSeconds = 0 });
            byte[] binary;
            using (var file = File.OpenRead(path))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var data = new MemoryStream()) { gzip.CopyTo(data); binary = data.ToArray(); }
            BitConverter.GetBytes(5).CopyTo(binary, 4);
            using (var file = File.Create(path))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
                gzip.Write(binary, 0, binary.Length - 16);
            var legacy = WorldFile.Read(path);
            ok &= Expect("existing version 5 saves load at noon without particles", legacy.DaylightSeconds == 0 && legacy.Particles.Length == 0 && legacy.Flashes.Length == 0);
            bool Reject(WorldSnapshot invalid)
            {
                WorldFile.Write(path, invalid);
                try { WorldFile.Read(path); return false; } catch (InvalidDataException) { return true; }
            }
            ok &= Expect("invalid clock and particle physics are rejected", Reject(snapshot with { DaylightSeconds = double.NaN })
                && Reject(snapshot with { Particles = [snapshot.Particles[0] with { Mass = 0 }] }));
            return ok;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static bool Expect(string what, bool condition)
    {
        Console.WriteLine($"   {(condition ? "ok  " : "BAD ")} {what}");
        return condition;
    }

    /// <summary>A new empty directory of its own under the system's temporary one.</summary>
    private static string ScratchDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"myt-save-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static World CreateWorld()
    {
        return new World(TestGpu.Vulkan.Vk, TestGpu.Vulkan.Device, TestGpu.Vulkan.PhysicalDevice, TestGpu.Vulkan.CommandPool, TestGpu.Vulkan.Queue, TestGpu.Shaders)
        {
            FollowPlayer = true,
            Sprites = TestGpu.Sprites
        };
    }

    /// <summary>More than the former drop cap, including old and below-world entities, survive saving/restoring.</summary>
    private static bool NoDespawn()
    {
        string directory = ScratchDirectory();
        try
        {
            using World world = CreateWorld();
            using ChunkStreamer streamer = new(world, new TerrainGenerator(Seed), ChunkStreamer.MinRenderDistance);
            world.SpawnPlayer(new dvec3(0, 0, 10));
            VoxelObject body = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(500, 0, -80), dquat.Identity, new dvec3(1, 2, 3));
            List<DroppedItem> drops = [];
            for (int i = 0; i < 600; i++)
            {
                DroppedItem item = world.DropItem(new ItemStack(Items.Of(BlockType.Dirt), 1 + i % 64),
                    new dvec3(500 + i, 0, -80), new dvec3(1, 0, -2));
                item.Age = 600 + i;
                drops.Add(item);
            }
            world.ApplyTopologyChanges();
            bool ok = Expect("every drop is retained beyond the former cap", world.DroppedItems.SequenceEqual(drops));
            ok &= Expect("distant entities are parked and retained", body.Parked && drops.All(i => i.Parked));
            WorldSnapshot snapshot = WorldFile.Capture(world, streamer);
            string path = Path.Combine(directory, "world.dat");
            WorldFile.Write(path, snapshot);
            WorldSnapshot read = WorldFile.Read(path);
            ok &= Check("all drops are written and read", read.Items.Length, 600, 600);
            using World restored = CreateWorld();
            using ChunkStreamer restoredStreamer = new(restored, new TerrainGenerator(Seed), ChunkStreamer.MinRenderDistance);
            WorldFile.Restore(read, restored, restoredStreamer);
            ok &= Check("restore retains every drop", restored.DroppedItems.Count, drops.Count, drops.Count);
            ok &= Expect("restored stacks, poses, velocities and ages are intact", restored.DroppedItems.Select(i =>
                (i.Stack, i.Position, i.LinearVelocity, i.Age)).SequenceEqual(drops.Select(i => (i.Stack, i.Position, i.LinearVelocity, i.Age))));
            ok &= Expect("below-world voxel object is retained with its motion", restored.Objects.Count == 1
                && restored.Objects[0].Position == body.Position && restored.Objects[0].LinearVelocity == body.LinearVelocity);
            return ok;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Worlds get folders of their own, keep them when renamed, list by when they were played and go when deleted; seeds come from what was typed.</summary>
    private static bool Store()
    {
        string directory = ScratchDirectory();
        try
        {
            WorldStore store = new(Path.Combine(directory, "saves"));
            bool ok = Check("worlds before any is made", store.List().Count, 0, 0);

            WorldInfo first = store.Create("My World", 5, GameMode.Survival);
            WorldInfo second = store.Create("My World", 6, GameMode.Creative);
            WorldInfo odd = store.Create("a/b:c*", 7, GameMode.Survival);
            ok &= Expect($"folders \"{first.Folder}\", \"{second.Folder}\", \"{odd.Folder}\"", first.Folder == "My World" && second.Folder == "My World (2)" && odd.Folder == "a_b_c_");

            Thread.Sleep(20);
            store.Touch(first);
            WorldInfo renamed = store.Rename(second, "Other");
            List<WorldInfo> worlds = store.List();
            ok &= Check("worlds", worlds.Count, 3, 3);
            ok &= Expect("the one played last comes first", worlds[0].Folder == "My World" || worlds[0].Folder == "My World (2)");
            WorldInfo listed = worlds.Single(w => w.Folder == "My World (2)");
            ok &= Expect("a renamed world keeps its folder, seed and game mode", listed.Name == "Other" && listed.Seed == 6 && listed.Mode == GameMode.Creative && renamed.Folder == listed.Folder);
            ok &= Expect("a survival world lists as survival", worlds.Single(w => w.Folder == "My World").Mode == GameMode.Survival);

            string old = Path.Combine(directory, "saves", "Old");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "world.json"), "{ \"Name\": \"Old\", \"Seed\": 3, \"Created\": \"2026-10-01T10:00:00\", \"LastPlayed\": \"2026-10-01T10:00:00\" }");
            ok &= Expect("a world from before game modes plays in creative", store.List().Single(w => w.Folder == "Old").Mode == GameMode.Creative);
            store.Delete(store.List().Single(w => w.Folder == "Old"));

            store.Delete(odd);
            ok &= Check("worlds after one is deleted", store.List().Count, 2, 2);
            ok &= Expect("its folder is gone", !Directory.Exists(Path.Combine(directory, "saves", odd.Folder)));

            ok &= Check("seed from a number", WorldStore.SeedFrom(" 123 "), 123, 123);
            ok &= Check("seed from text, as Java hashes it", WorldStore.SeedFrom("abc"), 96354, 96354);
            return ok;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Settings come back as saved, out-of-range ones are brought in range, and a damaged file gives the defaults.</summary>
    private static bool Settings()
    {
        string directory = ScratchDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            bool ok = Check("render distance with no file", GameSettings.Load(path).RenderDistance, ChunkStreamer.DefaultRenderDistance, ChunkStreamer.DefaultRenderDistance);

            new GameSettings { RenderDistance = 20, FieldOfView = 90, MouseSensitivity = 150, GuiScale = 3 }.Save(path);
            GameSettings loaded = GameSettings.Load(path);
            ok &= Expect("saved settings come back", loaded.RenderDistance == 20 && loaded.FieldOfView == 90 && loaded.MouseSensitivity == 150 && loaded.GuiScale == 3);

            File.WriteAllText(path, "{ \"RenderDistance\": 500, \"FieldOfView\": 5 }");
            loaded = GameSettings.Load(path);
            ok &= Expect("out-of-range settings are brought in range", loaded.RenderDistance == ChunkStreamer.MaxRenderDistance && loaded.FieldOfView == GameSettings.MinFieldOfView);

            File.WriteAllText(path, "{ not json");
            ok &= Check("render distance from a damaged file", GameSettings.Load(path).RenderDistance, ChunkStreamer.DefaultRenderDistance, ChunkStreamer.DefaultRenderDistance);

            string servers = Path.Combine(directory, "servers.json");
            ServerList.Save(servers, [new ServerInfo("Home", "localhost:25565")]);
            ok &= Expect("the server list comes back", ServerList.Load(servers).SequenceEqual([new ServerInfo("Home", "localhost:25565")]));
            return ok;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A file that is not a save, or a save cut short, is refused as invalid data, not read as a broken world.</summary>
    private static bool DamagedFile()
    {
        string directory = ScratchDirectory();
        try
        {
            string path = Path.Combine(directory, WorldStore.DataFile);
            PlayerState player = new(new dvec3(1, 2, 3), dvec3.Zero, 0.5, -0.2, 1.0, false, false, new dvec3(1, 2, 3), 2, true);
            MapColumn column = new(new ivec2(3, -4), [(2, Enumerable.Repeat(new Voxel(BlockType.Stone, 7), Grid3D<Voxel>.ChunkVolume).ToArray())]);
            ItemStack[] inventory = new ItemStack[Inventory.Size];
            inventory[4] = new ItemStack(ItemType.DiamondPickaxe, 1);
            inventory[20] = new ItemStack(Items.Of(BlockType.GoldOre), 17);
            ItemState item = new(new ItemStack(ItemType.Stick, 3), new dvec3(4, 5, 6), dquat.FromAxisAngle(0.3, dvec3.UnitZ), new dvec3(0, 1, 0), new dvec3(0, 0, 2), 12.5, 0.5);
            FurnaceState furnace = new(-1, new ivec3(48, -64, 33), Items.Of(BlockType.IronOre), ItemType.IronIngot, 3, 4.5, 30, new ItemStack(ItemType.IronIngot, 2));
            Voxel log = new(BlockType.OakLog, 23, BlockOrientation.LogX);
            Voxel lit = new(BlockType.Furnace, 4, BlockOrientation.West, true);
            column.Chunks[0].Cells[0] = log;
            ObjectState body = new([(ivec3.Zero, lit)], dvec3.Zero, dquat.Identity, dvec3.Zero, dvec3.Zero);
            WorldFile.Write(path, new WorldSnapshot([column], [body], player, inventory, [item]) { Furnaces = [furnace] });
            WorldSnapshot read = WorldFile.Read(path);
            bool ok = Expect("a small save reads back", read.Player == player && read.Columns.Single().Chunks.Single().Cells.SequenceEqual(column.Chunks[0].Cells));
            ok &= Expect("moving furnace orientation and lit state read back", read.Objects.Single().Cells.Single().Voxel == lit);
            ok &= Expect("its inventory and dropped items read back", read.Inventory.SequenceEqual(inventory) && read.Items.SequenceEqual([item]));
            ok &= Expect("furnace queue, output and fuel read back", read.Furnaces.SequenceEqual([furnace]));
            ok &= Expect("no partial file is left behind", !File.Exists(path + ".partial"));

            byte[] whole = File.ReadAllBytes(path);
            File.WriteAllBytes(path, whole[..(whole.Length / 2)]);
            ok &= Expect("a save cut short is refused", Refused(path));

            File.WriteAllText(path, "not a save at all");
            ok &= Expect("a file that is not a save is refused", Refused(path));

            WriteVersion1(path, player, column);
            WorldSnapshot old = WorldFile.Read(path);
            ok &= Expect("a version 1 save reads, breaking in one hit, with an empty inventory and no items", old.Player == player with { InstantBreak = true } && old.Inventory.All(s => s.IsEmpty) && old.Items.Length == 0 && old.Columns.Length == 1 && old.Columns[0].Chunks[0].Cells[0] == new Voxel(BlockType.OakLog, 23));
            return ok;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A save as version 1 wrote it: the player without instant breaking, no inventory or items, then the columns and the objects.</summary>
    private static void WriteVersion1(string path, PlayerState player, MapColumn column)
    {
        using FileStream file = File.Create(path);
        using System.IO.Compression.GZipStream compressed = new(file, System.IO.Compression.CompressionLevel.Fastest);
        using BinaryWriter writer = new(compressed);
        writer.Write(0x5754594Du);
        writer.Write(1);
        writer.Write(Grid3D<Voxel>.ChunkSize);
        foreach (dvec3 vector in (dvec3[])[player.Feet, player.Velocity])
        {
            writer.Write(vector.x);
            writer.Write(vector.y);
            writer.Write(vector.z);
        }

        writer.Write(player.Yaw);
        writer.Write(player.Pitch);
        writer.Write(player.Health);
        writer.Write(player.Flying);
        writer.Write(player.Invulnerable);
        writer.Write(player.Spawn.x);
        writer.Write(player.Spawn.y);
        writer.Write(player.Spawn.z);
        writer.Write(player.Selected);
        writer.Write(1);
        writer.Write(column.Coordinate.x);
        writer.Write(column.Coordinate.y);
        writer.Write(column.Chunks.Length);
        foreach ((int z, Voxel[] cells) in column.Chunks)
        {
            writer.Write(z);
            foreach (Voxel voxel in cells)
            {
                writer.Write((ushort)voxel.Type);
                writer.Write(voxel.Damage);
            }
        }

        writer.Write(0);
    }

    private static bool Refused(string path)
    {
        try
        {
            WorldFile.Read(path);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    /// <summary>
    /// A world played, changed and saved twice: once with its changes loaded, once from far
    /// away, where they are unloaded and its objects parked. Both saves hold the same; the file
    /// reads back as written; and a new world restored from it has the player as it was, and
    /// once the player is back, the changes and the objects where they were.
    /// </summary>
    private static bool RoundTrip()
    {
        string directory = ScratchDirectory();
        try
        {
            TerrainGenerator generator = new(Seed, testFixture: true);
            using World world = CreateWorld();
            generator.BuildSpawn(world, testing: true);
            world.ApplyTopologyChanges();
            Player player = world.SpawnPlayer(TerrainGenerator.SpawnPoint);
            using ChunkStreamer streamer = new(world, generator, ChunkStreamer.MinRenderDistance);

            void Frames(World w, ChunkStreamer s, int count, Func<bool>? until = null)
            {
                for (int k = 0; k < count && !(until?.Invoke() ?? false); k++)
                {
                    s.Update(w.Player!.Feet);
                    w.Update(World.Step);
                    Thread.Sleep(1);
                }
            }

            Frames(world, streamer, 600, () => world.RegionReady && streamer.Generating == 0);
            Frames(world, streamer, 120);

            // Changes: a glass block on the ground, a dent in the grass, a worn object.
            ivec3 marker = new(40, 8, TerrainGenerator.GroundHeight(40, 8) + 1);
            ivec3 dented = new(30, 30, TerrainGenerator.GroundHeight(30, 30));
            world.SetBlock(world.Map, marker, BlockType.Glass);
            world.Mine(world.Map, dented, 0.3);
            VoxelObject worn = world.Objects.MaxBy(o => o.Grid.Count)!;
            ivec3 wornCell = worn.Grid.Occupied().First().Cell;
            world.Damage(worn, wornCell, 1.0, "test");
            Frames(world, streamer, 60);
            Voxel dentedVoxel = world.Map.Grid[dented];
            Voxel wornVoxel = worn.Grid[wornCell];
            bool ok = Expect($"the grass is dented ({dentedVoxel.Health:P0}) and the object worn ({wornVoxel.Health:P0})", dentedVoxel.Damage > 0 && wornVoxel.Damage > 0);

            player.Yaw = 1.25;
            player.Pitch = -0.5;
            player.Health = 0.7;
            player.Invulnerable = true;
            player.InstantBreak = true;
            player.Inventory.Selected = 3;
            player.Inventory.Slots[3] = new ItemStack(ItemType.StonePickaxe, 1);
            player.Inventory.Slots[12] = new ItemStack(Items.Of(BlockType.Cobblestone), 40);
            // Carried on the inventory screen when saved: it goes back into the inventory.
            player.Inventory.Cursor = new ItemStack(Items.Of(BlockType.Cobblestone), 30);
            // A dropped item out of the player's reach, by the spawn area's objects.
            ivec2 itemColumn = new(36, 16);
            world.DropItem(new ItemStack(ItemType.Stick, 2), new dvec3(itemColumn.x + 0.5, itemColumn.y + 0.5, TerrainGenerator.GroundHeight(itemColumn.x, itemColumn.y) + 1.5), dvec3.Zero);
            Frames(world, streamer, 120);
            WorldSnapshot near = WorldFile.Capture(world, streamer);
            ItemStack[] expected = player.Inventory.Copy().Slots;
            expected[12] = expected[12].WithCount(64);
            expected[0] = new ItemStack(Items.Of(BlockType.Cobblestone), 6);
            ok &= Expect("the carried stack is saved in the inventory", near.Inventory.SequenceEqual(expected));
            ok &= Check("dropped items saved", near.Items.Length, 1, 1);

            // Far enough that the spawn area unloads and its objects are parked.
            player.Flying = true;
            player.MoveTo(new dvec3(400, 30, 120));
            Frames(world, streamer, 900, () => world.RegionReady && streamer.Generating == 0 && !world.Map.IsLoaded(VoxelMap.ColumnOfCell(marker)));
            Frames(world, streamer, 30);
            ok &= Expect("the spawn area unloaded", !world.Map.IsLoaded(VoxelMap.ColumnOfCell(marker)));
            ok &= Expect("its objects are parked", world.Objects.All(o => o.Parked));

            WorldSnapshot far = WorldFile.Capture(world, streamer);
            ok &= Expect("saved near and far, the changed columns are the same", SameColumns(near, far));
            // The objects settle a little more on the way, until they are parked.
            ok &= Expect("and the objects, but for settling", SameObjects(near, far, 0.02));
            ok &= Check("changed columns saved", far.Columns.Length, 1, 400);

            string path = Path.Combine(directory, WorldStore.DataFile);
            WorldFile.Write(path, far);
            WorldSnapshot read = WorldFile.Read(path);
            ok &= Expect("the file reads back as written", SameColumns(far, read) && SameObjects(far, read, 0.0) && read.Player == far.Player && read.Inventory.SequenceEqual(far.Inventory) && read.Items.SequenceEqual(far.Items));
            ok &= Expect("the parked item is saved where it lay", far.Items.Length == 1 && (far.Items[0].Position - near.Items[0].Position).Length < 0.02);
            Console.WriteLine($"   {far.Columns.Length} columns and {far.Objects.Length} objects in {new FileInfo(path).Length / 1024} KB");

            using World restored = CreateWorld();
            using ChunkStreamer restoredStreamer = new(restored, new TerrainGenerator(Seed, testFixture: true), ChunkStreamer.MinRenderDistance);
            WorldFile.Restore(read, restored, restoredStreamer);
            Player again = restored.Player!;
            ok &= Check("player's distance from where it was saved (m)", (again.Feet - player.Feet).Length, 0, 1e-9);
            ok &= Expect("its view, health, flying, invulnerability and instant breaking are as saved", again.Yaw == 1.25 && again.Pitch == -0.5 && again.Health == 0.7 && again.Flying && again.Invulnerable && again.InstantBreak);
            ok &= Expect("its inventory and the slot it holds are as saved", again.Inventory.Slots.SequenceEqual(expected) && again.Inventory.Selected == 3);
            ok &= Expect("its spawn point is as saved", again.Spawn == player.Spawn);

            Frames(restored, restoredStreamer, 600, () => restored.RegionReady && restoredStreamer.Generating == 0);
            again.MoveTo(new dvec3(36, 12, TerrainGenerator.GroundHeight(36, 12) + 1.5));
            Frames(restored, restoredStreamer, 900, () => restored.RegionReady && restoredStreamer.Generating == 0 && restored.Objects.All(o => !o.Parked));
            Frames(restored, restoredStreamer, 120);

            ok &= Expect("the glass block is back", restored.Map.Grid[marker].Type == BlockType.Glass);
            ok &= Expect("the dent is back as deep", restored.Map.Grid[dented] == dentedVoxel);
            ok &= Check("objects", restored.Objects.Count, far.Objects.Length, far.Objects.Length);
            ok &= Expect("every object is back in the physics", restored.Objects.All(o => o.Body != null));
            ok &= Expect("the dropped item is back in the physics, resting where it lay", restored.DroppedItems.Count == 1 && restored.DroppedItems[0].Body != null
                && restored.DroppedItems[0].Stack == new ItemStack(ItemType.Stick, 2) && (restored.DroppedItems[0].Position - far.Items[0].Position).Length < 0.05);
            double moved = 0.0;
            for (int i = 0; i < Math.Min(restored.Objects.Count, far.Objects.Length); i++)
                moved = Math.Max(moved, (restored.Objects[i].Position - far.Objects[i].Position).Length);
            ok &= Check("farthest an object moved from where it was saved, resting (m)", moved, 0, 0.05);
            VoxelObject wornAgain = restored.Objects.Single(o => o.Grid.Count == worn.Grid.Count && o.Grid[wornCell] == wornVoxel);
            ok &= Expect("the worn block is as worn", wornAgain.Grid[wornCell].Damage == wornVoxel.Damage);
            return ok;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool SameColumns(WorldSnapshot a, WorldSnapshot b)
    {
        Dictionary<ivec2, MapColumn> other = b.Columns.ToDictionary(c => c.Coordinate);
        return a.Columns.Length == b.Columns.Length && a.Columns.All(column =>
            other.TryGetValue(column.Coordinate, out MapColumn? match)
            && column.Chunks.Length == match.Chunks.Length
            && column.Chunks.Zip(match.Chunks).All(p => p.First.Z == p.Second.Z && p.First.Cells.AsSpan().SequenceEqual(p.Second.Cells)));
    }

    private static bool SameObjects(WorldSnapshot a, WorldSnapshot b, double tolerance)
    {
        return a.Objects.Length == b.Objects.Length && a.Objects.Zip(b.Objects).All(p =>
            p.First.Cells.SequenceEqual(p.Second.Cells)
            && (p.First.Position - p.Second.Position).Length <= tolerance
            && (p.First.LinearVelocity - p.Second.LinearVelocity).Length <= tolerance);
    }
}
