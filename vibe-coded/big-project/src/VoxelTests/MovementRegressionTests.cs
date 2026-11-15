using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool PlacementObstructions()
    {
        using World world = CreateWorld();
        world.SetBlock(world.Map, ivec3.Zero, BlockType.Bedrock);
        Player player = world.SpawnPlayer(new dvec3(.9, .5, .9995));
        Voxel stone = new(BlockType.Stone, 0);
        bool ok = Check("feet contact slop permits adjacent block", world.CanPlaceBlock(world.Map, new(1, 0, 0), stone) ? 1 : 0, 1, 1);
        player.MoveTo(new dvec3(.9, .5, .98));
        ok &= Check("real player overlap prevents placement", world.CanPlaceBlock(world.Map, new(1, 0, 0), stone) ? 1 : 0, 0, 0);
        player.MoveTo(new dvec3(1.5, .5, .75));
        ok &= Check("empty upper half of bottom slab permits placement", world.CanPlaceBlock(world.Map, new(1, 0, 0),
            new Voxel(BlockType.StoneSlab, 0, BlockOrientation.SlabBottom)) ? 1 : 0, 1, 1);
        ok &= Check("occupied upper slab intersects player", world.CanPlaceBlock(world.Map, new(1, 0, 0),
            new Voxel(BlockType.StoneSlab, 0, BlockOrientation.SlabTop)) ? 1 : 0, 0, 0);
        player.MoveTo(new dvec3(20, 20, 20));
        VoxelObject obj = world.Spawn([(ivec3.Zero, BlockType.Bedrock)], new dvec3(3, 3, 3), dquat.FromAxisAngle(.15, dvec3.UnitZ));
        ivec3 next = ivec3.UnitX;
        dvec3 center = obj.ToWorld(new dvec3(next) + .5);
        ivec3 obstruction = (ivec3)dvec3.Floor(center);
        world.SetBlock(world.Map, obstruction, BlockType.Bedrock);
        ok &= Check("rotated object placement sees terrain", world.CanPlaceBlock(obj, next, stone) ? 1 : 0, 0, 0);
        world.SetBlock(world.Map, obstruction, BlockType.Air);
        ok &= Check("clear rotated object placement succeeds", world.CanPlaceBlock(obj, next, stone) ? 1 : 0, 1, 1);
        VoxelObject other = world.Spawn([(ivec3.Zero, BlockType.Bedrock)], center - .5, dquat.Identity);
        ok &= Check("object placement sees other objects", world.CanPlaceBlock(obj, next, stone) ? 1 : 0, 0, 0);
        ok &= Check("terrain placement sees detached object", world.CanPlaceBlock(world.Map, obstruction, stone) ? 1 : 0, 0, 0);
        return ok;
    }

    private static bool PlayerCrouchItem()
    {
        using World world = CreateWorld();
        DroppedItem item = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), new dvec3(3, 3, 3), dvec3.Zero, pickupDelay:60);
        item.Body!.Mass = 1e8;
        item.Body.GravityScale = 0;
        item.Body.FixedRotation = true;
        Player player = world.SpawnPlayer(new dvec3(3, 3, 3 + Items.BlockEdge / 2));
        player.Flying = false;
        player.Invulnerable = true;
        Run(world, .15);
        dvec3 start = player.Feet;
        player.Controls = new PlayerControls(dvec3.UnitX, false, true);
        Run(world, .15);
        bool ok = Check("crouching on an item permits walking", player.Feet.x - start.x, .15, .4);
        Run(world, .5);
        ok &= Check("crouch stops at item edge", player.Feet.x - item.Position.x, .4, .56);
        ok &= Check("crouch stays above item", player.Feet.z - item.Position.z, Items.BlockEdge / 2 - .01, Items.BlockEdge / 2 + .01);
        player.Controls = default;
        Run(world, .1);
        return ok & Check("can stand again on item", player.Crouching ? 1 : 0, 0, 0);
    }

    private static bool PlayerToolJump()
    {
        using World world = new(TestGpu.Vulkan.Vk, TestGpu.Vulkan.Device, TestGpu.Vulkan.PhysicalDevice,
            TestGpu.Vulkan.CommandPool, TestGpu.Vulkan.Queue, TestGpu.Shaders) { Sprites = TestGpu.Sprites };
        Floor(world, 6);
        DroppedItem item = world.DropItem(new ItemStack(ItemType.DiamondPickaxe, 1), new dvec3(3, 3, 2.03), dvec3.Zero,
            pickupDelay:60, orientation:dquat.FromAxisAngle(Math.PI / 2, dvec3.UnitX));
        Player player = world.SpawnPlayer(new dvec3(3, 3, 2.06));
        player.Flying = false;
        player.Invulnerable = true;
        Run(world, .3);
        bool ok = Check("tool supports player", player.Grounded ? 1 : 0, 1, 1);
        player.JumpPressed = true;
        double fastest = 0, spin = 0, slowest = 0;
        for (int step = 0; step < 20; step++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            world.StepOnce();
            if (step == 0) ok &= Check("jump from floor beneath tool takes off", player.Body.LinearVelocity.z, 9, 10.2);
            slowest = Math.Max(slowest, clock.Elapsed.TotalSeconds);
            fastest = Math.Max(fastest, item.LinearVelocity.Length);
            spin = Math.Max(spin, item.AngularVelocity.Length);
        }
        ok &= Check("tool remains at bounded speed", fastest, 0, 20);
        ok &= Check("tool remains at bounded spin", spin, 0, 200);
        ok &= Check("no multi-second jump stall", slowest, 0, 1);
        return ok & Check("player remains healthy", player.Health, 1, 1);
    }

    private static bool PlayerTiltedWall()
    {
        bool ok = true;
        foreach (double tilt in new[] { -.15, .15 })
        foreach (double across in new[] { .1, 2.5, 5.9 })
        {
            using World world = CreateWorld();
            // A massive freely simulated structure isolates contact normals from platform motion.
            var cells = TerrainGenerator.Box(new ivec3(6, 6, 1), BlockType.Bedrock)
                .Concat(TerrainGenerator.Box(new ivec3(1, 6, 1), BlockType.Bedrock).Select(v => (v.Item1 + new ivec3(3, 0, 1), v.Item2)));
            dquat orientation = dquat.FromAxisAngle(.2, dvec3.UnitZ) * dquat.FromAxisAngle(tilt, dvec3.UnitY);
            VoxelObject structure = world.Spawn(cells, new dvec3(5, 5, 5), orientation);
            world.ApplyTopologyChanges();
            structure.Body!.GravityScale = 0;
            structure.Body.FixedRotation = true;
            structure.Body.Mass = 1e9;
            dvec3 start = structure.ToWorld(new dvec3(2.5, across, 1));
            start.z += .3 * Math.Abs(Math.Tan(tilt)) + .005;
            Player player = world.SpawnPlayer(start);
            player.Flying = false;
            player.Invulnerable = true;
            Run(world, .2);
            dvec3 forward = orientation * dvec3.UnitX;
            forward.z = 0; forward = forward.Normalized;
            dvec3 side = new(-forward.y, forward.x, 0);
            player.Controls = new PlayerControls(forward, false);
            double lateral = 0, displacement = 0;
            dvec3 previous = player.Feet;
            Run(world, .5, () => {
                lateral = Math.Max(lateral, Math.Abs(dvec3.Dot(player.Body.LinearVelocity, side)));
                displacement = Math.Max(displacement, new dvec2(player.Feet - previous).Length);
                previous = player.Feet;
                return false;
            });
            ok &= Check($"wall tilt {tilt}, across {across}: no lateral launch", lateral, 0, 5);
            ok &= Check("no lateral step teleport", displacement, 0, .15);
            double bottom = player.Feet.z;
            player.JumpPressed = true;
            double highest = bottom;
            Run(world, .6, () => { highest = Math.Max(highest, player.Feet.z); return false; });
            ok &= Check($"wall tilt {tilt}, across {across}: jump is possible", highest - bottom, .7, 2);
            ok &= Check("no uncontrolled velocity", player.Body.LinearVelocity.Length, 0, 15);
        }
        return ok;
    }
}
