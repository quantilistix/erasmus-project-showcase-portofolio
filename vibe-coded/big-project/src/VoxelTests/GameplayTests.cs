using GlmSharp;
using MYT.Kit.Vulkan;
using MYT.Kit.Gui;
using VoxelGame;

namespace VoxelTests;

internal static class GameplayTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("asset-catalog", AssetCatalog),
        ("player-health-capacity", PlayerHealthCapacity),
        ("frame-timing", FrameTimes),
        ("furnace-timing", FurnaceTiming),
        ("inventory-side-by-side", InventoryPanels),
        ("terrain-water-caves", TerrainFeatures),
        ("water-collision", WaterCollision)
    ];

    private static bool Expect(string name, bool value)
    {
        Console.WriteLine($"   {(value ? "ok" : "BAD")} {name}");
        return value;
    }

    private static bool AssetCatalog()
    {
        bool ok = Expect("saved block and item numbers remain stable",
            (int)BlockType.Water == 25 && (int)BlockType.BedrockSlab == 26
            && (int)BlockType.GlassMissingCornerStairs == 57 && (int)ItemType.Emerald == 1035);
        ok &= Expect("every block number has one registered material", Blocks.Count == Enum.GetValues<BlockType>().Length);
        foreach (BlockType type in Enum.GetValues<BlockType>().Skip(1).Where(t => t != BlockType.AcaciaDoorTop))
            ok &= Expect($"{type} is available in creative", Items.Creative.Contains(Items.Of(type)) && Items.Get(Items.Of(type)).Name == Blocks.Get(type).Name);
        foreach (string texture in Blocks.TextureNames())
        {
            using FileStream stream = File.OpenRead(Path.Combine(TestGpu.Shaders, "..", "images", texture + ".png"));
            var image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            ok &= Expect($"{texture} loads as a block texture", image.Width == 16 && image.Height == 16);
        }
        foreach (ItemType type in Items.NonBlockTypes)
            ok &= Expect($"{type} sprite loads", TestGpu.Sprites.Pixels(type).Length == 16 * 16 * 4 && Items.Creative.Contains(type));
        BlockMaterial tnt = Blocks.Get(BlockType.Tnt);
        ok &= Expect("TNT uses its side, top and bottom textures", tnt.East == "tnt_side" && tnt.Top == "tnt_top" && tnt.Bottom == "tnt_bottom");
        return ok;
    }

    private static bool PlayerHealthCapacity()
    {
        Player player = new(1, dvec3.Zero);
        player.Health = Player.Material.HealthAfter(player.Health, 9);
        bool ok = Expect("player survives the former lethal damage", !player.Dead && Math.Abs(player.Health - Math.Sqrt(0.5)) < 1e-9);
        player.Health = Player.Material.HealthAfter(player.Health, 10);
        return ok & Expect("sufficient damage still kills the player", player.Dead);
    }

    private static bool FrameTimes()
    {
        FrameTiming timing = new();
        for (int i = 0; i < 75; i++) timing.Add(1.0 / 75);
        bool ok = Expect("75 Hz presentation rate", Math.Abs(timing.FramesPerSecond - 75) < 1e-9);
        timing.Add(0.25);
        ok &= Expect("250 ms stall is shown immediately, without a 100 ms simulation cap", timing.SlowestSeconds == 0.25);
        for (int i = 0; i < 150; i++) timing.Add(1.0 / 75);
        ok &= Expect("old stall ages out", Math.Abs(timing.SlowestSeconds - 1.0 / 75) < 1e-9);
        timing.Add(0); timing.Add(double.NaN); timing.Add(double.PositiveInfinity);
        ok &= Expect("invalid samples do not corrupt timing", double.IsFinite(timing.FramesPerSecond));
        FrameTiming slow = new();
        slow.Add(2);
        return ok & Expect("long stalls remain uncapped", slow.SlowestSeconds == 2 && slow.FramesPerSecond == 0.5);
    }

    private static bool InventoryPanels()
    {
        InterfaceArt art = new(Path.Combine(TestGpu.Shaders, "..", "images"), TestGpu.Sprites);
        GuiContext gui = new(art.Atlas, art.Style);
        InventoryScreen screen = new(art, null);
        Inventory inventory = new();
        Furnace? station = null;
        void Draw(vec2 cursor, bool down)
        {
            gui.Begin(new GuiInput { Cursor = cursor, MouseDown = down }, new vec2(1000, 500), 1);
            screen.Draw(gui, inventory, true, false, _ => throw new Exception("Unexpected drop"), station);
            gui.End();
        }
        Draw(new vec2(314, 165), true);
        bool ok = Expect("left catalog supplies infinite blocks", inventory.Cursor == new ItemStack(Items.Of(BlockType.Bedrock), 64));
        Draw(new vec2(512, 270), false);
        Draw(new vec2(512, 270), true);
        ok &= Expect("right inventory accepts the stack without switching tabs", inventory.Cursor.IsEmpty && inventory.Slots[9].Count == 64);
        Draw(new vec2(512, 270), false);
        Draw(new vec2(512, 270), true);
        ok &= Expect("right slots retain normal pickup", inventory.Cursor.Count == 64 && inventory.Slots[9].IsEmpty);
        inventory.Cursor = ItemStack.Empty;
        station = new Furnace();
        inventory.Add(new ItemStack(Items.Of(BlockType.IronOre), 1));
        inventory.Add(new ItemStack(ItemType.Coal, 1));
        Draw(new vec2(512, 165), false);
        Draw(new vec2(512, 165), true);
        ok &= Expect("furnace recipe click pays and starts timed work", station.Queued == 1 && inventory.Count(Items.Of(BlockType.IronOre)) == 0);
        station.Update(10);
        Draw(new vec2(656, 232), false);
        Draw(new vec2(656, 232), true);
        ok &= Expect("furnace output click collects the completed ingot", inventory.Cursor == new ItemStack(ItemType.IronIngot, 1) && station.Output.Slots[0].IsEmpty);
        return ok;
    }

    private static bool FurnaceTiming()
    {
        Inventory inventory = new();
        Furnace furnace = new();
        SmeltingRecipe iron = Furnace.Recipes[0];
        inventory.Add(new ItemStack(iron.Input, 10));
        bool ok = Expect("missing fuel leaves input unchanged", !furnace.Start(inventory, iron) && inventory.Count(iron.Input) == 10);
        inventory.Add(new ItemStack(ItemType.Coal, 1));
        for (int i = 0; i < 8; i++) ok &= Expect($"queue job {i}", furnace.Start(inventory, iron));
        ok &= Expect("one coal pays for eight jobs", inventory.Count(ItemType.Coal) == 0 && inventory.Count(iron.Input) == 2 && !furnace.Start(inventory, iron));
        furnace.Update(9.9);
        ok &= Expect("no immediate output", furnace.Output.Slots[0].IsEmpty);
        furnace.Update(.2);
        ok &= Expect("first job completes after ten seconds", furnace.Output.Slots[0] == new ItemStack(ItemType.IronIngot, 1) && furnace.Queued == 7);
        furnace.Update(69.9);
        ok &= Expect("large frames complete all elapsed jobs", furnace.Output.Slots[0].Count == 8 && furnace.Queued == 0);
        inventory.Add(new ItemStack(Items.Of(BlockType.OakPlanks), 1));
        ok &= Expect("wood can fuel a job", furnace.Start(inventory, iron));
        furnace.Output.Slots[0] = new ItemStack(ItemType.IronIngot, 64);
        furnace.Update(20);
        ok &= Expect("full output blocks completion without losing the job", furnace.Queued == 1 && furnace.Remaining == 0);
        furnace.Output.Slots[0] = ItemStack.Empty;
        furnace.Update(.1);
        ok &= Expect("collecting output unblocks completion", furnace.Queued == 0 && furnace.Output.Slots[0].Count == 1);
        return ok;
    }

    private static bool TerrainFeatures()
    {
        TerrainGenerator generator = new(5);
        int[] bedrock = new int[5];
        int water = 0, caves = 0;
        HashSet<BlockType> ores = [];
        bool ok = true;
        for (int cx = -8; cx < 8; cx += 2)
        for (int cy = -8; cy < 8; cy += 2)
        {
            MapColumn column = generator.GenerateColumn(new ivec2(cx, cy));
            foreach ((int cz, Voxel[] cells) in column.Chunks)
            for (int i = 0; i < cells.Length; i++)
            {
                ivec3 at = Grid3D<Voxel>.CellOf(new ivec3(cx, cy, cz), i);
                BlockType type = cells[i].Type;
                if (type == BlockType.Bedrock)
                {
                    if (at.z > 4) ok = false;
                    else bedrock[at.z]++;
                }
                if (type == BlockType.Water)
                {
                    water++;
                    if (at.z > 63 || at.z <= generator.Height(at.x, at.y)) ok = false;
                }
                if (type == BlockType.Air && at.z > 4 && at.z < generator.Height(at.x, at.y) - 3) caves++;
                if (type.ToString().EndsWith("Ore")) ores.Add(type);
            }
        }
        ok &= Expect("bedrock probability falls with height", bedrock[0] == 64 * 256 && bedrock.Zip(bedrock.Skip(1)).All(p => p.First > p.Second));
        ok &= Expect("water reaches sea level and caves carve underground", water > 0 && caves > 1000);
        ok &= Expect("every registered ore generates", Enum.GetValues<BlockType>().Where(t => t.ToString().EndsWith("Ore")).All(ores.Contains));
        Console.WriteLine($"   water={water}, cave air={caves}, ores={string.Join(", ", ores)}");
        return ok;
    }

    private static bool WaterCollision()
    {
        Grid3D<Voxel> grid = new();
        grid[new ivec3(0, 0, 0)] = new(BlockType.Stone, 0);
        var dry = VoxelMesher.BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate));
        grid[new ivec3(0, 0, 1)] = new(BlockType.Water, 0);
        var wet = VoxelMesher.BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate));
        bool ok = Expect("water preserves solid collision surfaces", dry!.Value.Mesh.Vertices.SequenceEqual(wet!.Value.Mesh.Vertices) && dry.Value.Mesh.Faces.SequenceEqual(wet.Value.Mesh.Faces));
        ok &= Expect("filtered ray reaches the riverbed", grid.Raycast(new dvec3(.5, .5, 3), -dvec3.UnitZ, 5, out Grid3DHit hit, v => Blocks.IsSolid(v.Type)) && hit.Cell.z == 0);
        grid[new ivec3(0, 0, 0)] = Voxel.Air;
        ok &= Expect("water alone produces no collider", VoxelMesher.BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate)) == null);
        return ok;
    }
}
