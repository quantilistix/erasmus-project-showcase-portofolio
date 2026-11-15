using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

internal static class BlockTextureTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("block-face-textures", Faces),
        ("furnace-appearance", FurnaceAppearance)
    ];

    private static bool Faces()
    {
        int[,] layers = new int[Blocks.Count, 7];
        for (int type = 0; type < Blocks.Count; type++)
        for (int face = 0; face < 7; face++) layers[type, face] = face;
        bool ok = true;
        foreach (BlockType type in new[] { BlockType.OakLog, BlockType.AcaciaLog })
        foreach (ivec3 normal in Grid3D<Voxel>.FaceDirections)
        {
            Voxel log = new(type, 0, Voxel.Placement(type, normal, dvec3.Zero));
            for (int face = 0; face < 6; face++)
                ok &= (log.TextureFace(face) >= 4) == (face / 2 == (normal.x != 0 ? 0 : normal.y != 0 ? 1 : 2));
            // Bark's vertical texture direction must follow the log axis on every side.
            for (int face = 0; face < 6; face++)
            {
                if (log.TextureFace(face) >= 4) continue;
                int rotation = log.TextureRotation(face);
                ivec3 bottom = VoxelMesher.FaceCorners[face][(4 - rotation) % 4];
                ivec3 top = VoxelMesher.FaceCorners[face][(7 - rotation) % 4];
                ivec3 grain = top - bottom;
                ok &= Math.Abs(ivec3.Dot(grain, normal)) == 1;
            }
        }
        for (int front = 0; front < 4; front++)
        {
            ivec3 normal = Grid3D<Voxel>.FaceDirections[front];
            Voxel furnace = new(BlockType.Furnace, 0, Voxel.Placement(BlockType.Furnace, ivec3.UnitZ, new dvec3(normal)), true);
            Grid3D<Voxel> grid = new();
            grid[ivec3.Zero] = furnace;
            uint[] mesh = VoxelMesher.BuildRenderMesh(grid, ivec3.Zero, layers);
            Voxel[] padded = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(grid, ivec3.Zero, padded);
            ok &= mesh.SequenceEqual(VoxelMesher.BuildRenderMeshReference(padded, layers));
            for (int q = 0; q < mesh.Length; q += 8)
            {
                int face = (int)((mesh[q] >> 15) & 7);
                ok &= (mesh[q + 1] & 255) == (face == front ? 6u : face >= 4 ? (uint)face : (uint)furnace.TextureFace(face));
            }
        }
        ok &= Blocks.Get(BlockType.Workbench).East != Blocks.Get(BlockType.Workbench).North;
        ok &= Blocks.TextureNames().Contains("furnace_front_lit");
        Console.WriteLine($"   face selection, placement axes, bark UVs and all furnace facings: {ok}");
        return ok;
    }

    private static bool FurnaceAppearance()
    {
        var gpu = TestGpu.Vulkan;
        using World world = new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
        ivec3 cell = new(3, 4, 5);
        world.SetBlock(world.Map, cell, BlockType.Furnace, BlockOrientation.East);
        Furnace furnace = world.FurnaceAt(new VoxelHit(world.Map, cell, ivec3.UnitZ, 0));
        Inventory inventory = new();
        inventory.Add(new ItemStack(Furnace.Recipes[0].Input, 2));
        inventory.Add(new ItemStack(ItemType.Coal, 1));
        bool ok = !world.Map.Grid[cell].Lit;
        furnace.Start(inventory, Furnace.Recipes[0]);
        world.Map.MeshDirty.Clear();
        world.UpdateFurnaces(0);
        ok &= world.Map.Grid[cell].Lit && world.Map.Grid[cell].Orientation == BlockOrientation.East;
        ok &= world.Map.MeshDirty.Contains(Grid3D<Voxel>.ChunkOf(cell));
        world.UpdateFurnaces(10);
        ok &= !world.Map.Grid[cell].Lit && furnace.Output.Slots[0].Count == 1;
        furnace.Start(inventory, Furnace.Recipes[0]);
        world.UpdateFurnaces(0);
        ok &= world.Map.Grid[cell].Lit;
        furnace.Output.Slots[0] = new ItemStack(ItemType.IronIngot, 64);
        world.UpdateFurnaces(10);
        ok &= !world.Map.Grid[cell].Lit && furnace.Queued == 1;
        furnace.Output.Slots[0] = ItemStack.Empty;
        world.UpdateFurnaces(.1);
        ok &= !world.Map.Grid[cell].Lit && furnace.Queued == 0;
        Console.WriteLine($"   furnace lights, finishes, stalls and retains facing: {ok}");
        return ok;
    }
}
