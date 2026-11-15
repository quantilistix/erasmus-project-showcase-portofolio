using GlmSharp;
using MYT.Kit;
using MYT.Kit.Vulkan;
using VoxelGame;
using static VoxelGame.BoxCollision;

namespace VoxelTests;

internal static class BlockShapeTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("block-shapes", Geometry),
        ("shape-placement", Placement),
        ("shape-ray-collision", Queries),
        ("shape-save", Save),
        ("shape-shader", Shader),
        ("shape-player", PlayerOnShapes),
        ("shape-climbing", Climbing),
        ("shape-dropped-items", DroppedShapes)
    ];

    private static bool Expect(string name, bool passed)
    {
        Console.WriteLine($"   {(passed ? "ok " : "BAD")} {name}");
        return passed;
    }

    private static dvec3 Point(uint packed) => new dvec3(packed & 31, (packed >> 5) & 31, (packed >> 10) & 31)
        + new dvec3((packed >> 25) & 1, (packed >> 26) & 1, (packed >> 27) & 1) / 2;

    private static bool Geometry()
    {
        bool ok = true;
        int[,] layers = new int[Blocks.Count, 7];
        foreach (BlockType type in Enum.GetValues<BlockType>().Where(BlockShapes.IsPartial))
        {
            var orientations = BlockShapes.Kind(type) == 0
                ? Enumerable.Range(32, 6) : Enumerable.Range(64, 8);
            foreach (int orientation in orientations)
            {
                Voxel voxel = new(type, 0, (BlockOrientation)orientation);
                Grid3D<Voxel> grid = new();
                grid[ivec3.Zero] = voxel;
                uint[] render = VoxelMesher.BuildRenderMesh(grid, ivec3.Zero, layers);
                double volume = 0;
                for (int q = 0; q < render.Length; q += 8)
                {
                    dvec3 a = Point(render[q]), b = Point(render[q + 2]), c = Point(render[q + 4]), d = Point(render[q + 6]);
                    volume += (dvec3.Dot(a, dvec3.Cross(b, c)) + dvec3.Dot(a, dvec3.Cross(c, d))) / 6;
                    ok &= ((render[q] >> 23) & 3) == 0; // no texture rotation
                    ok &= (render[q] & (1u << 28)) != 0;
                }
                double expected = new[] { 0.5, 0.75, 0.625, 0.875 }[BlockShapes.Kind(type)];
                ok &= Math.Abs(volume - expected) < 1e-10;
                var collision = VoxelMesher.BuildRigidCollisionMesh(grid, ivec3.Zero, ivec3.Ones);
                ok &= collision.Mesh.Vertices.Any(p => p.x == 0.5 || p.y == 0.5 || p.z == 0.5);
                ItemVertex[] item = ItemMesher.BlockModel(type, layers);
                ok &= item.Length > 0 && item.All(v => v.UvLayer.x >= 0 && v.UvLayer.x <= 1 && v.UvLayer.y >= 0 && v.UvLayer.y <= 1);
                ok &= voxel.TextureRotation(4) == 0;
            }
            BlockMaterial material = Blocks.Get(BlockShapes.BaseOf(type));
            ok &= Enumerable.Range(0, 6).All(f => Blocks.Get(type).Texture(f) == material.Texture(f));
        }
        ok &= Expect("all materials/orientations have the expected volume and grid UVs", ok);
        // A full block against a half block retains the exposed half of its face.
        Grid3D<Voxel> joined = new();
        joined[0, 0, 0] = new(BlockType.Stone, 0);
        joined[1, 0, 0] = new(BlockType.StoneSlab, 0);
        uint[] mesh = VoxelMesher.BuildRenderMesh(joined, ivec3.Zero, layers);
        int exposed = 0;
        for (int q = 0; q < mesh.Length; q += 8)
            if (((mesh[q] >> 15) & 7) == 0 && Point(mesh[q]).x == 1) exposed++;
        ok &= Expect("cube beside a slab exposes exactly half its adjoining face", exposed == 2);
        VoxelObject body = new(1);
        body.Grid[ivec3.Zero] = new(BlockType.StoneSlab, 0);
        RigidMass mass = body.MassProperties();
        ok &= Expect("half slab mass and center", Math.Abs(mass.Mass - 1300) < 1e-8 && mass.Center == new dvec3(0.5, 0.5, 0.25));
        return ok;
    }

    private static bool Placement()
    {
        bool ok = true;
        ok &= Expect("top face places a bottom slab", Voxel.Placement(BlockType.StoneSlab, ivec3.UnitZ, dvec3.UnitX) == BlockOrientation.SlabBottom);
        ok &= Expect("underside places a top slab", Voxel.Placement(BlockType.StoneSlab, -ivec3.UnitZ, dvec3.UnitX) == BlockOrientation.SlabTop);
        ok &= Expect("side center has a deterministic horizontal tie", Voxel.Placement(BlockType.StoneSlab, ivec3.UnitX, dvec3.UnitX, new(1, 0.5, 0.5)) == BlockOrientation.SlabBottom);
        foreach (ivec3 normal in Grid3D<Voxel>.FaceDirections.Take(4))
        foreach (var (u, z) in new[] { (0.5, 0.9), (0.5, 0.1), (0.1, 0.5), (0.9, 0.5), (0.85, 0.7), (0.65, 0.7) })
        {
            dvec3 point = new(0.5);
            int lateral = normal.x != 0 ? 1 : 0;
            point[lateral] = u; point.z = z;
            point[normal.x != 0 ? 0 : 1] = normal.x + normal.y > 0 ? 1 : 0;
            BlockOrientation expected = Math.Abs(z - 0.5) >= Math.Abs(u - 0.5)
                ? z > 0.5 ? BlockOrientation.SlabTop : BlockOrientation.SlabBottom
                : lateral == 0 ? u > 0.5 ? BlockOrientation.SlabEast : BlockOrientation.SlabWest
                    : u > 0.5 ? BlockOrientation.SlabNorth : BlockOrientation.SlabSouth;
            ok &= Voxel.Placement(BlockType.StoneSlab, normal, new dvec3(normal), point) == expected;
        }
        ok &= Expect("all side faces use four triangles bounded by the diagonal cross", ok);
        ok &= Expect("side upper edge places a top slab", Voxel.Placement(BlockType.StoneSlab, ivec3.UnitX, dvec3.UnitX, new(1, 0.5, 0.9)) == BlockOrientation.SlabTop);
        for (int i = 0; i < 4; i++)
        {
            dvec3 toward = new[] { -dvec3.UnitY, -dvec3.UnitX, dvec3.UnitY, dvec3.UnitX }[i];
            ok &= Voxel.Placement(BlockType.StoneStairs, ivec3.UnitZ, toward) == (BlockOrientation)(64 + i);
            ok &= Voxel.Placement(BlockType.StoneStairs, -ivec3.UnitZ, toward) == (BlockOrientation)(68 + i);
        }
        ok &= Expect("stairs follow all four view directions, upright and inverted", ok);
        bool corners = true;
        foreach (BlockType type in new[] { BlockType.StoneCornerStairs, BlockType.StoneMissingCornerStairs })
        foreach (ivec3 normal in Grid3D<Voxel>.FaceDirections)
        foreach (double sideHeight in new[] { 0.25, 0.75 })
        foreach (int x in new[] { 0, 1 })
        foreach (int y in new[] { 0, 1 })
        foreach (dvec3 toward in new[] { new dvec3(1, 0.99, 0), new dvec3(0.99, 1, 0), new dvec3(-1, 0.99, 0), new dvec3(0.99, -1, 0) })
        {
            bool missing = BlockShapes.Kind(type) == 3;
            dvec3 point = new(0.25 + x * 0.5, 0.25 + y * 0.5, normal.z < 0 ? 0 : normal.z > 0 ? 1 : sideHeight);
            int aimedX = x, aimedY = y;
            if (normal.x != 0) { point.x = normal.x > 0 ? 1 : 0; aimedX = (missing ? normal.x : -normal.x) > 0 ? 1 : 0; }
            if (normal.y != 0) { point.y = normal.y > 0 ? 1 : 0; aimedY = (missing ? normal.y : -normal.y) > 0 ? 1 : 0; }
            BlockOrientation orientation = Voxel.Placement(type, normal, toward, point);
            Voxel voxel = new(type, 0, orientation);
            int layer = normal.z < 0 || (normal.z == 0 && sideHeight > 0.5) ? 0 : 1;
            for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                bool aimed = a == aimedX && b == aimedY;
                corners &= BlockShapes.Occupied(voxel, new(a, b, layer)) == (missing ? !aimed : aimed);
            }
        }
        ok &= Expect("outer corner and inner notch follow the aimed quadrant on every face across view diagonals", corners);
        return ok;
    }

    private static bool Queries()
    {
        VoxelMap map = new(1);
        map.Grid[-1, 0, 0] = new(BlockType.StoneSlab, 0);
        map.Grid[1, 0, 0] = new(BlockType.Stone, 0);
        bool ok = Expect("ray passes through empty upper slab to next cube", BlockShapes.Raycast(map.Grid,
            new(-2, 0.5, 0.75), dvec3.UnitX, 5, out var ray) && ray.Cell.x == 1 && ray.Distance == 3);
        ok &= Expect("ray hits occupied lower slab at negative coordinates", BlockShapes.Raycast(map.Grid,
            new(-2, 0.5, 0.25), dvec3.UnitX, 5, out ray) && ray.Cell.x == -1 && ray.Distance == 1);
        ok &= Expect("ray hits slab internal top", BlockShapes.Raycast(map.Grid,
            new(-0.5, 0.5, 2), -dvec3.UnitZ, 5, out ray) && ray.Normal == ivec3.UnitZ && ray.Distance == 1.5);
        CollisionBox empty = new(new(-0.5, 0.5, 0.8), new dvec3(0.1), dquat.Identity);
        CollisionBox solid = empty with { Center = new(-0.5, 0.5, 0.2) };
        bool Penetrates(CollisionBox box)
        {
            foreach (CollisionBox part in new VoxelCellQuery(box, map))
                if (BoxPenetration(box, part, out _)) return true;
            return false;
        }
        ok &= Expect("collision leaves empty half open", !Penetrates(empty) && Penetrates(solid));
        map.Grid[-1, 0, 0] = new(BlockType.StoneStairs, 0);
        ok &= Expect("stairs expose the lower tread to rays", BlockShapes.Raycast(map.Grid,
            new(-0.5, 0.25, 2), -dvec3.UnitZ, 5, out ray) && ray.Distance == 1.5);
        ok &= Expect("stairs expose the high tread to rays", BlockShapes.Raycast(map.Grid,
            new(-0.5, 0.75, 2), -dvec3.UnitZ, 5, out ray) && ray.Distance == 1);
        return ok;
    }

    private static bool Save()
    {
        string path = Path.Combine(Path.GetTempPath(), $"myt-shapes-{Guid.NewGuid():N}.dat");
        try
        {
            Voxel[] voxels = [new(BlockType.GlassSlab, 123, BlockOrientation.SlabNorth),
                new(BlockType.StoneMissingCornerStairs, 321, BlockOrientation.StairsWestTop)];
            ObjectState obj = new([.. voxels.Select((v, i) => (new ivec3(i, 0, 0), v))], dvec3.Zero,
                dquat.Identity, dvec3.Zero, dvec3.Zero);
            WorldSnapshot state = new([], [obj], default, new ItemStack[Inventory.Size], []);
            WorldFile.Write(path, state);
            return Expect("shape types, orientation and damage survive a world save", WorldFile.Read(path).Objects[0].Cells.Select(c => c.Voxel).SequenceEqual(voxels));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static bool PlayerOnShapes()
    {
        var gpu = TestGpu.Vulkan;
        bool ok = true;
        foreach (var (type, expected) in new[] { (BlockType.BedrockSlab, 0.5), (BlockType.BedrockStairs, 1.0) })
        {
            using World world = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) world.SetBlock(world.Map, new(x, y, 0), type);
            Player player = world.SpawnPlayer(new(1.5, 1.75, 2));
            player.Flying = false;
            for (int step = 0; step < 120; step++) world.StepOnce();
            ok &= Expect($"player stands on {type} at its actual height ({player.Feet.z:F3})",
                Math.Abs(player.Feet.z - expected) < 0.02 && player.Grounded);
        }
        return ok;
    }

    private static bool Climbing()
    {
        var gpu = TestGpu.Vulkan;
        bool ok = true;
        foreach (double tangent in new[] { 0.0, 2.0, 5.0, 20.0 })
        {
            using World world = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
            for (int y = 0; y < 25; y++)
            for (int x = 0; x < 12; x++)
            {
                world.SetBlock(world.Map, new(x, y, 0), BlockType.Bedrock);
                if (x >= 4) world.SetBlock(world.Map, new(x, y, 1), BlockType.BedrockSlab);
            }
            Player player = world.SpawnPlayer(new(tangent > 5 ? 3.65 : 2.8, 3.5, 1));
            player.Flying = false;
            for (int i = 0; i < 20; i++) world.StepOnce();
            player.Controls = new PlayerControls(new dvec3(1, tangent, 0).Normalized, false);
            double drop = 0, previous = player.Feet.z;
            int lifts = 0;
            for (int i = 0; i < (int)Math.Ceiling((tangent > 5 ? 0.8 : 2.5) * Math.Sqrt(1 + tangent * tangent) / (Player.WalkSpeed * World.Step)) + 30; i++)
            {
                world.StepOnce();
                double z = player.Feet.z;
                if (z - previous > 0.2) lifts++;
                drop = Math.Max(drop, previous - z);
                previous = z;
            }
            ok &= Expect($"slab approach (1,{tangent}): feet {player.Feet:F3}, lifts {lifts}, largest drop {drop:F3}",
                player.Feet.x > 4.3 && Math.Abs(player.Feet.z - 1.5) < 0.025 && lifts == 1 && drop < 0.04);
        }
        return ok;
    }

    private static bool DroppedShapes()
    {
        var gpu = TestGpu.Vulkan;
        using World world = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
        bool ok = true;
        int[,] layers = new int[Blocks.Count, 7];
        foreach (BlockType type in new[] { BlockType.StoneSlab, BlockType.StoneStairs, BlockType.StoneCornerStairs, BlockType.StoneMissingCornerStairs })
        {
            DroppedItem item = world.DropItem(new(Items.Of(type), 1), new(3, 3, 4), dvec3.Zero, pickupDelay: 100);
            Mesh3D mesh = item.Body!.Geometry;
            double volume = 0;
            for (int i = 0; i < mesh.Faces.Length; i += 3)
                volume += dvec3.Dot(mesh.Vertices[mesh.Faces[i]], dvec3.Cross(mesh.Vertices[mesh.Faces[i + 1]], mesh.Vertices[mesh.Faces[i + 2]])) / 6;
            double fraction = new[] { 0.5, 0.75, 0.625, 0.875 }[BlockShapes.Kind(type)];
            double edge = Items.BlockEdge;
            ok &= Expect($"{type} drop has the model's volume, mesh coordinates and mass",
                Math.Abs(volume - fraction * edge * edge * edge) < 1e-10
                && Math.Abs(item.Body.Mass - 2600 * volume) < 1e-8
                && ItemMesher.BlockModel(type, layers).All(v => mesh.Vertices.Any(p => (p - new dvec3(v.Position) * edge).Length < 1e-7)));
            DroppedItem copy = world.DropItem(item.Stack, new(6, 3, 4), dvec3.Zero, pickupDelay: 100);
            ok &= Expect("repeated drops share the cooked shape mesh", ReferenceEquals(mesh, copy.Body!.Geometry));
        }
        return ok;
    }

    private static bool Shader()
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "assets/shaders/voxel");
        string source = File.ReadAllText(Path.Combine(folder, "voxel.vert"))
            .Replace("#include \"frame.glsl\"", File.ReadAllText(Path.Combine(folder, "frame.glsl")));
        using ShadercCompiler compiler = new();
        return Expect("voxel vertex shader compiles", compiler.Compile(source, ShadercShaderKind.VertexShader, "voxel.vert").Length > 0);
    }
}
