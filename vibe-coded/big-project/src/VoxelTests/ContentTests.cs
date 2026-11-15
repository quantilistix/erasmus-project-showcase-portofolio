using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

internal static class ContentTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("content-recipes-food", RecipesAndFood),
        ("door-geometry-save", DoorGeometry),
        ("door-rendering", DoorRendering),
        ("content-world-interactions", WorldInteractions)
    ];

    private static bool Expect(string label, bool success)
    {
        Console.WriteLine($"   {(success ? "ok" : "BAD")} {label}");
        return success;
    }

    private static bool RecipesAndFood()
    {
        bool ok = Expect("ruby ore drops rubies", Items.DropOf(BlockType.RubyOre) == ItemType.Ruby);
        Tool ruby = Items.Get(ItemType.RubyPickaxe).Tool!;
        ok &= Expect("ruby pickaxe mines tough ore faster than a bare hand", ruby.Damage(Blocks.Get(BlockType.RubyOre), 1, 1) > 0
            && Tool.Hand.Damage(Blocks.Get(BlockType.RubyOre), 1, 1) == 0);
        Inventory inventory = new();
        inventory.Add(new ItemStack(ItemType.Ruby, 3));
        inventory.Add(new ItemStack(ItemType.Stick, 2));
        Recipe pickaxe = Crafting.Recipes.Single(r => r.Result.Type == ItemType.RubyPickaxe);
        ok &= Expect("ruby pickaxe recipe pays for its materials", Crafting.Craft(inventory, pickaxe, false)
            && inventory.Count(ItemType.RubyPickaxe) == 1 && inventory.Count(ItemType.Ruby) == 0 && inventory.Count(ItemType.Stick) == 0);
        inventory = new();
        inventory.Add(new ItemStack(ItemType.IronIngot, 9));
        Recipe iron = Crafting.Recipes.Single(r => r.Result.Type == Items.Of(BlockType.IronBlock));
        Recipe unpack = Crafting.Recipes.Single(r => r.Result.Type == ItemType.IronIngot);
        ok &= Expect("iron blocks pack and unpack without losing ingots", Crafting.Craft(inventory, iron, false)
            && inventory.Count(Items.Of(BlockType.IronBlock)) == 1 && Crafting.Craft(inventory, unpack, false)
            && inventory.Count(ItemType.IronIngot) == 9 && inventory.Count(Items.Of(BlockType.IronBlock)) == 0);
        inventory = new();
        inventory.Add(new ItemStack(ItemType.Cocoa, 2));
        Recipe cookie = Crafting.Recipes.Single(r => r.Result.Type == ItemType.Cookie);
        ok &= Expect("cocoa makes cookies", Crafting.Craft(inventory, cookie, false) && inventory.Count(ItemType.Cookie) == 4);
        Player player = new(1, dvec3.Zero) { Food = 0.4, Health = 0.5 };
        player.Inventory.Add(new ItemStack(ItemType.Cookie, 2));
        ok &= Expect("eating restores food and health and consumes one cookie", player.EatHeld(false)
            && Math.Abs(player.Food - 0.6) < 1e-9 && Math.Abs(player.Health - 0.6) < 1e-9 && player.Inventory.Held.Count == 1);
        player.Health = player.Food = 1;
        ok &= Expect("a full player does not waste food", !player.EatHeld(false) && player.Inventory.Held.Count == 1);
        player.Health = 0.95;
        ok &= Expect("creative food heals without consuming and cannot overfill health", player.EatHeld(true) && player.Health == 1 && player.Inventory.Held.Count == 1);
        ok &= Expect("TNT and Soul have no use or recipes", Items.Get(ItemType.Soul).Tool == null
            && !Crafting.Recipes.Any(r => r.Result.Type == ItemType.Soul || r.Result.Type == Items.Of(BlockType.Tnt)));
        return ok;
    }

    private static dvec3 RenderPoint(uint a)
    {
        dvec3 p = new(a & 31u, (a >> 5) & 31u, (a >> 10) & 31u);
        for (int axis = 0; axis < 3; axis++)
        {
            bool high = (a & (1u << (25 + axis))) != 0;
            bool fine = (a & (1u << 29)) != 0 && (a & (1u << (20 + axis))) != 0;
            p[axis] += fine ? high ? 13.0 / 16 : 3.0 / 16 : high ? 0.5 : 0;
        }
        return p;
    }

    private static bool DoorRendering()
    {
        bool ok = true;
        int[,] layers = new int[Blocks.Count, 7];
        bool transmits = true;
        foreach (int orientation in Enumerable.Range(0, 4).Concat(Enumerable.Range(16, 4)))
        foreach (ivec3 cell in new[] { new ivec3(4), new ivec3(0), new ivec3(15) })
        {
            Voxel door = new(BlockType.AcaciaDoorBottom, 0, (BlockOrientation)orientation);
            Grid3D<Voxel> grid = new();
            grid[cell] = door;
            ivec3 chunk = new(cell.x >> 4, cell.y >> 4, cell.z >> 4);
            uint[] mesh = VoxelMesher.BuildRenderMesh(grid, chunk, layers);
            double volume = 0;
            for (int q = 0; q < mesh.Length; q += 8)
            {
                dvec3 a = RenderPoint(mesh[q]), b = RenderPoint(mesh[q+2]), c = RenderPoint(mesh[q+4]), d = RenderPoint(mesh[q+6]);
                volume += (dvec3.Dot(a, dvec3.Cross(b, c)) + dvec3.Dot(a, dvec3.Cross(c, d))) / 6;
            }
            ok &= Expect($"door {orientation} at {cell} has exact thin render volume", Math.Abs(volume - 3.0 / 16) < 1e-9);
            transmits &= LightingScene.Mask(door, false) == 0;
            // Test support and every side separately, including neighbors across chunk seams.
            foreach (int face in new[] { 0, 1, 2, 3, 4, 5 })
            {
                ivec3 direction = Grid3D<Voxel>.FaceDirections[face];
                ivec3 neighbor = cell + direction;
                grid[neighbor] = new(BlockType.Stone, 0);
                ivec3 neighborChunk = new(neighbor.x >> 4, neighbor.y >> 4, neighbor.z >> 4);
                mesh = VoxelMesher.BuildRenderMesh(grid, neighborChunk, layers);
                int otherFace = face ^ 1, axis = face >> 1;
                double area = 0;
                ivec3 local = neighbor - neighborChunk * 16;
                uint owner = (uint)(local.x | local.y << 4 | local.z << 8);
                for (int q = 0; q < mesh.Length; q += 8)
                {
                    if (((mesh[q] >> 15) & 7) != otherFace || ((mesh[q+1] >> 8) & 4095) != owner) continue;
                    dvec3 a = RenderPoint(mesh[q]), b = RenderPoint(mesh[q+2]), d = RenderPoint(mesh[q+6]);
                    area += dvec3.Cross(b-a, d-a).Length;
                }
                var parts = BlockShapes.Parts(door).Select(p => BlockShapes.PartBounds(door, p)).ToArray();
                double min = parts.Min(p => p.Low[axis]), max = parts.Max(p => p.High[axis]);
                bool touches = (face & 1) == 0 ? max == 1 : min == 0;
                int thin = (orientation & 1) == 0 ? 1 : 0;
                double covered = touches ? axis == thin ? 1 : 3.0 / 16 : 0;
                ok &= Expect($"door {orientation} clips neighbor face {face} at {cell} exactly", Math.Abs(area - (1-covered)) < 1e-9);
                grid[neighbor] = default;
            }
        }
        ItemVertex[] item = ItemMesher.BlockModel(BlockType.AcaciaDoorBottom, layers);
        ok &= Expect("all item edges crop to the three opaque texture columns", item.Where(v => Math.Abs(v.Normal.y) < 0.5)
            .All(v => v.UvLayer.x >= 0 && v.UvLayer.x <= 3f / 16));
        ok &= Expect("cutout doors transmit transport light in every orientation", transmits);
        return ok;
    }

    private static bool DoorGeometry()
    {
        bool ok = true;
        foreach (int facing in Enumerable.Range(0, 4))
        {
            Voxel closed = new(BlockType.AcaciaDoorBottom, 0, (BlockOrientation)facing);
            Voxel open = closed with { Orientation = (BlockOrientation)(16 + ((facing + 1) & 3)) };
            ok &= Expect($"door facing {facing} rotates its occupied volume", BlockShapes.Parts(closed).Count() == 4
                && !BlockShapes.Parts(closed).ToHashSet().SetEquals(BlockShapes.Parts(open)));
        }
        VoxelMap map = new(1);
        map.Grid[ivec3.Zero] = new(BlockType.AcaciaDoorBottom, 0);
        ok &= Expect("door ray queries pass through its empty half", !BlockShapes.Raycast(map.Grid,
            new(0.5, 0.75, 2), -dvec3.UnitZ, 4, out _));
        ok &= Expect("door ray queries hit its panel", BlockShapes.Raycast(map.Grid,
            new(0.5, 0.1, 2), -dvec3.UnitZ, 4, out _));
        int[,] layers = new int[Blocks.Count, 7];
        ok &= Expect("doors render and bake shaped collision geometry", VoxelMesher.BuildRenderMesh(map.Grid, ivec3.Zero, layers).Length > 0
            && VoxelMesher.BuildRigidCollisionMesh(map.Grid, ivec3.Zero, ivec3.Ones).Mesh.Vertices.Any(v => v.y == BlockShapes.DoorThickness));
        BoxCollision.CollisionBox passage = new(new dvec3(0.5, 0.5, 0.9), new dvec3(0.3, 0.6, 0.9), dquat.Identity);
        bool Blocked()
        {
            foreach (var panel in new VoxelCellQuery(passage, map))
                if (BoxCollision.BoxPenetration(passage, panel, out _)) return true;
            return false;
        }
        ok &= Expect("a closed door blocks walking through the doorway", Blocked());
        map.Grid[ivec3.Zero] = new(BlockType.AcaciaDoorBottom, 0, BlockOrientation.DoorOpenEast);
        ok &= Expect("an open door leaves enough room for the player's width", !Blocked());
        using (MYT.Kit.ShadercCompiler compiler = new())
        {
            string folder = Path.Combine(TestGpu.Shaders, "voxel");
            foreach (string name in new[] { "voxel.vert", "sun_shadow.vert" })
            {
                string source = File.ReadAllText(Path.Combine(folder, name))
                    .Replace("#include \"frame.glsl\"", File.ReadAllText(Path.Combine(folder, "frame.glsl")));
                ok &= Expect($"{name} compiles with thin door vertices", compiler.Compile(source, MYT.Kit.ShadercShaderKind.VertexShader, name).Length > 0);
            }
        }
        string path = Path.Combine(Path.GetTempPath(), $"myt-door-{Guid.NewGuid():N}.dat");
        try
        {
            Voxel door = new(BlockType.AcaciaDoorBottom, 123, BlockOrientation.DoorOpenEast);
            WorldSnapshot snapshot = new([], [new ObjectState([(ivec3.Zero, door)], dvec3.Zero, dquat.Identity, dvec3.Zero, dvec3.Zero)],
                default, new ItemStack[Inventory.Size], []);
            WorldFile.Write(path, snapshot);
            ok &= Expect("open door state survives saving", WorldFile.Read(path).Objects[0].Cells[0].Voxel == door);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        return ok;
    }

    private static bool WorldInteractions()
    {
        var gpu = TestGpu.Vulkan;
        using World world = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
        ivec3 bottom = new(0, 0, 1);
        bool ok = Expect("doors need support", !world.PlaceDoor(world.Map, bottom, BlockOrientation.Default));
        world.SetBlock(world.Map, ivec3.Zero, BlockType.Bedrock);
        world.SetBlock(world.Map, bottom + ivec3.UnitZ, BlockType.Stone);
        ok &= Expect("blocked upper half rejects the entire door", !world.PlaceDoor(world.Map, bottom, BlockOrientation.Default)
            && world.Map.Grid[bottom].Type == BlockType.Air);
        world.RemoveBlock(world.Map, bottom + ivec3.UnitZ);
        ok &= Expect("placing a door creates both halves", world.PlaceDoor(world.Map, bottom, BlockOrientation.Default)
            && world.Map.Grid[bottom + ivec3.UnitZ].Type == BlockType.AcaciaDoorTop);
        ok &= Expect("either half toggles both panels", world.ToggleDoor(world.Map, bottom + ivec3.UnitZ)
            && world.Map.Grid[bottom].Orientation == BlockOrientation.DoorOpenEast
            && world.Map.Grid[bottom + ivec3.UnitZ].Orientation == BlockOrientation.DoorOpenEast);
        ok &= Expect("closing restores the original facing", world.ToggleDoor(world.Map, bottom)
            && world.Map.Grid[bottom].Orientation == BlockOrientation.Default);
        world.RemoveBlock(world.Map, bottom + ivec3.UnitZ);
        ok &= Expect("removing either half removes its partner", world.Map.Grid[bottom].Type == BlockType.Air);
        world.SetBlock(world.Map, bottom, BlockType.Ice);
        world.Damage(world.Map, bottom, 1e9, "test mining");
        ok &= Expect("broken ice becomes water", world.Map.Grid[bottom].Type == BlockType.Water);
        ok &= Expect("ice is slippery and lava is non-solid", Blocks.Get(BlockType.Ice).Friction < Blocks.Get(BlockType.Glass).Friction
            && !Blocks.IsSolid(BlockType.Lava));
        Player player = world.SpawnPlayer(new(0.5, 0.5, 1));
        player.Invulnerable = false;
        world.SetBlock(world.Map, bottom, BlockType.Lava);
        ok &= Expect("placed lava emits light", world.Map.Grid[bottom].Lit);
        world.UpdateHazards(0);
        ok &= Expect("paused lava does not hurt", player.Health == 1);
        world.UpdateHazards(0.5);
        ok &= Expect("lava contact damages the player", player.Health is > 0 and < 1);
        player.Health = 1;
        player.Invulnerable = true;
        world.UpdateHazards(0.5);
        ok &= Expect("invulnerability prevents lava damage", player.Health == 1);
        return ok;
    }
}
