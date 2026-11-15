using GlmSharp;
using MYT.Kit.Structural;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool LargeObjectPlayer()
    {
        using World world = CreateWorld();
        const int size = 24, height = 24;
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
        VoxelObject platform = world.Spawn(TerrainGenerator.Box(new ivec3(size, size, height), BlockType.Stone),
            new dvec3(0, 0, 1), dquat.Identity);
        Player player = world.SpawnPlayer(new dvec3(size / 2 + .5, size / 2 + .5, height + 1));
        player.Flying = false;
        world.PlayerHurt += (amount, cause) => Console.WriteLine($"   hurt {amount}: {cause}");
        double largestStep = 0;
        Run(world, 1.2, () => { largestStep = Math.Max(largestStep, player.Body.LinearVelocity.Length); return false; });
        bool ok = Check("standing on large object retains health", player.Health, .999, 1);
        player.Controls = new PlayerControls(dvec3.UnitX, false);
        Run(world, .4);
        player.Controls = default;
        Run(world, .2);
        ok &= Check("walking on large object retains health", player.Health, .999, 1);
        ok &= Check("large object resting player speed", largestStep, 0, .1);
        return ok;
    }

    private static bool LandedLargeObjectPlayer()
    {
        using World world = CreateWorld();
        const int size = 24;
        for (int x = -4; x < size + 4; x++)
        for (int y = -4; y < size + 4; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
        // Unbreakable material isolates collision safety from genuine crushing by
        // falling rubble. The object still falls, rotates and has its full mass.
        VoxelObject cube = world.Spawn(TerrainGenerator.Box(new ivec3(size), BlockType.Bedrock),
            new dvec3(0, 0, 1.5), dquat.Identity);
        Player player = world.SpawnPlayer(new dvec3(-1, size / 2, 1));
        player.Flying = false;
        world.PlayerHurt += (amount, cause) => Console.WriteLine($"   landed-object hurt {amount}: {cause}");
        Run(world, 1.5);
        bool ok = Check("large falling object lands beside player safely", player.Health, .999, 1);
        ok &= Check("large object has landed", cube.Position.z, .99, 1.01);
        player.Controls = new PlayerControls(dvec3.UnitX, false);
        Run(world, .6);
        player.JumpPressed = true;
        Run(world, .8);
        player.Controls = default;
        Run(world, .5);
        return ok & Check("walking and jumping beside landed object retains health", player.Health, .999, 1);
    }

    private static bool StressFractureBatch()
    {
        using World world = CreateWorld();
        Dictionary<ivec3, double> overloaded = [];
        for (int x = 0; x < 32; x++)
        for (int y = 0; y < 32; y++) overloaded[new(x, y, 0)] = 2;
        var selected = world.MostOverloaded(overloaded).ToArray();
        HashSet<ivec3> cells = selected.Select(c => c.Cell).ToHashSet();
        bool ok = Check("wide overloaded slab has simultaneous failure sites", cells.Count, 256, 512);
        ok &= Check("one face of a crack survives this snapshot", cells.Count(c => MYT.Kit.Vulkan.Grid3D<Voxel>.FaceDirections.Any(d => cells.Contains(c + d))), 0, 0);
        overloaded.Clear();
        for (int z = 0; z < 40; z++) overloaded[new ivec3(0, 0, z)] = 2;
        ok &= Check("long narrow support localizes to one break", world.MostOverloaded(overloaded).Count, 1, 1);
        overloaded.Clear();
        overloaded[ivec3.Zero] = 3;
        overloaded[ivec3.UnitZ] = 1.5;
        selected = world.MostOverloaded(overloaded).ToArray();
        return ok & Check("strongly localized failure still chooses its peak", selected.Length == 1 && selected[0].Cell == ivec3.Zero ? 1 : 0, 1, 1);
    }

    private static bool StressedMining()
    {
        using World world = CreateWorld();
        ivec3 cell = new(2, 2, 2);
        world.SetBlock(world.Map, cell - ivec3.UnitZ, BlockType.Bedrock);
        world.SetBlock(world.Map, cell, BlockType.Stone);
        world.ApplyTopologyChanges();
        Player player = world.SpawnPlayer(new dvec3(1, 2.5, 2));
        VoxelHit hit = new(world.Map, cell, -ivec3.UnitX, 1);
        double before = world.Map.Grid[cell].Health;
        world.Mine(hit, new dvec3(1, 2.5, 2.5), dvec3.UnitX, Tool.Hand);
        bool ok = Check("bare hand does not wear unloaded stone", world.Map.Grid[cell].Health, before, before);
        double compression = Blocks.Get(BlockType.Stone).ElasticLimit(1).Compression * .95;
        world.Map.SetStress(new StressSnapshot(world.Map.TopologyVersion, [cell], [new CellStress(0, compression, 0)], true));
        world.Mine(hit, new dvec3(1, 2.5, 2.5), dvec3.UnitX, Tool.Hand);
        ok &= Check("sustained compression allows hand to damage stone", world.Map.Grid[cell].Health < before ? 1 : 0, 1, 1);
        world.Map.SetStress(new StressSnapshot(world.Map.TopologyVersion - 1, [cell], [new CellStress(0, compression, 0)], true));
        double worn = world.Map.Grid[cell].Health;
        world.Mine(hit, new dvec3(1, 2.5, 2.5), dvec3.UnitX, Tool.Hand);
        return ok & Check("stale stress cannot assist a strike", world.Map.Grid[cell].Health, worn, worn);
    }
}
