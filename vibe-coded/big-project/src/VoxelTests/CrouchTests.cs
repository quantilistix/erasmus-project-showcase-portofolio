using GlmSharp;
using VoxelGame;
using static VoxelGame.BoxCollision;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool CrouchCollisionGeometry()
    {
        bool ok = true;
        CollisionBox cube = new(new dvec3(0.5), new dvec3(0.5), dquat.Identity);
        foreach (double overlap in new[] { 0.0001, 0.02, 0.3 })
        {
            CollisionBox sole = new(new dvec3(1.3 - overlap, 0.5, 0.95), new dvec3(0.3, 0.3, 0.05), dquat.Identity);
            ok &= Check($"sole overlaps ledge by {overlap}: supported", WalkableFaceIn(sole, cube) ? 1 : 0, 1, 1);
        }
        CollisionBox beyond = new(new dvec3(1.301, 0.5, 0.95), new dvec3(0.3, 0.3, 0.05), dquat.Identity);
        ok &= Check("beyond ledge is unsupported", WalkableFaceIn(beyond, cube) ? 1 : 0, 0, 0);
        foreach (double angle in new[] { -0.7, -0.18, 0.0, 0.18, 0.7, 0.9 })
        {
            dquat rotation = dquat.FromAxisAngle(angle, dvec3.UnitY);
            CollisionBox slope = new(dvec3.Zero, new dvec3(5, 5, 0.5), rotation);
            dvec3 surface = rotation * new dvec3(0, 0, 0.5);
            CollisionBox sole = new(surface - new dvec3(0, 0, 0.05), new dvec3(0.3, 0.3, 0.05), dquat.Identity);
            int expected = angle < 0.8 ? 1 : 0;
            ok &= Check($"incline {angle}: walkable face", WalkableFaceIn(sole, slope) ? 1 : 0, expected, expected);
            CollisionBox box = new(surface, new dvec3(0.3, 0.3, 0.675), dquat.Identity);
            bool overlaps = BoxPenetration(box, slope, out dvec3 push);
            ok &= Check("detects inclined penetration", overlaps ? 1 : 0, 1, 1);
            box = box with { Center = box.Center + push * 1.000001 };
            ok &= Check("translation separates inclined boxes", BoxPenetration(box, slope, out _) ? 1 : 0, 0, 0);
        }
        return ok;
    }

    private static bool PlayerCrouchShape()
    {
        Player player = new(1, new dvec3(2, 2, 4));
        player.Body.LinearVelocity = new dvec3(1, 2, 3);
        dvec3 feet = player.Feet;
        player.SetCrouching(true, grounded: true);
        bool ok = Check("ground crouch holds feet", (player.Feet - feet).Length, 0, 1e-10)
            & Check("crouched height", player.Body.Geometry.Vertices.Max(v => v.z), 1.35 - 1e-10, 1.35 + 1e-10)
            & Check("grounded eye lowers", player.Eye.z, feet.z + 1.17 - 1e-10, feet.z + 1.17 + 1e-10);
        player.SetCrouching(false, grounded: true);
        double top = player.Feet.z + player.CurrentHeight;
        double eye = player.Eye.z;
        player.SetCrouching(true, grounded: false);
        ok &= Check("air crouch holds top", player.Feet.z + player.CurrentHeight, top - 1e-10, top + 1e-10)
            & Check("air crouch holds eye", player.Eye.z, eye - 1e-10, eye + 1e-10)
            & Check("air crouch raises bottom", player.Feet.z - feet.z, 0.45 - 1e-10, 0.45 + 1e-10)
            & Check("resize preserves velocity", (player.Body.LinearVelocity - new dvec3(1, 2, 3)).Length, 0, 1e-10);
        player.SetCrouching(false, grounded: false);
        ok &= Check("air stand restores bottom", (player.Feet - feet).Length, 0, 1e-10);
        return ok;
    }

    private static bool PlayerCrouchLedges()
    {
        bool ok = true;
        foreach (dvec3 direction in (dvec3[])[dvec3.UnitX, -dvec3.UnitX, dvec3.UnitY, new dvec3(1, 1, 0).Normalized])
        {
            using World world = CreateWorld();
            world.SetBlock(world.Map, new ivec3(2, 2, 1), BlockType.Bedrock);
            Player player = world.SpawnPlayer(new dvec3(2.5, 2.5, 2));
            player.Flying = false;
            player.Invulnerable = true;
            Run(world, 0.2);
            player.Controls = new PlayerControls(direction, false, Crouch: true);
            Run(world, 1.2);
            ok &= Check($"crouch {direction}: remains above ledge", player.Feet.z, 1.99, 2.01)
                & Check("footprint still overlaps x", player.Feet.x, 1.7, 3.3)
                & Check("footprint still overlaps y", player.Feet.y, 1.7, 3.3)
                & Check("stops at edge", new dvec2(player.Body.LinearVelocity).Length, 0, 0.03);
            player.Controls = new PlayerControls(direction, false);
            Run(world, 0.5);
            ok &= Check("release allows walking off", player.Feet.z, -2, 1.9);
        }
        return ok;
    }

    private static bool PlayerCrouchClearance()
    {
        using World world = CreateWorld();
        Floor(world, 12);
        Player player = world.SpawnPlayer(new dvec3(2.5, 2.5, 2));
        player.Flying = false;
        Run(world, 0.2);
        player.Controls = new PlayerControls(dvec3.UnitX * Player.SprintFactor, true, Crouch: true);
        Run(world, 0.5);
        bool ok = Check("crouch speed ignores sprint", player.Body.LinearVelocity.x, 2.2, 2.3);
        // The ceiling begins 1.5 m above the floor: crouching fits, standing does not.
        VoxelObject ceiling = world.Spawn([(ivec3.Zero, BlockType.Bedrock)], player.Feet + new dvec3(-0.5, -0.5, 1.5), dquat.Identity);
        world.ApplyTopologyChanges();
        ceiling.Body!.GravityScale = 0;
        player.Controls = default;
        Run(world, 0.15);
        ok &= Check("cannot stand into ceiling", player.Crouching ? 1 : 0, 1, 1);
        ceiling.Body.Position += new dvec3(0, 0, 5);
        Run(world, 0.15);
        ok &= Check("stands when space clears", player.Crouching ? 1 : 0, 0, 0);
        return ok;
    }

    private static bool PlayerCrouchSlope()
    {
        using World world = CreateWorld();
        Floor(world, 24);
        for (int x = 14; x < 20; x++)
        for (int y = 4; y < 14; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);
        VoxelObject slab = world.Spawn(TerrainGenerator.Box(new ivec3(6, 4, 1), BlockType.OakPlanks),
            new dvec3(8.5, 7, 2.2), dquat.FromAxisAngle(-0.18, dvec3.UnitY));
        Run(world, 2);
        Player player = world.SpawnPlayer(slab.ToWorld(new dvec3(2.5, 2, 1.1)));
        player.Flying = false;
        player.Invulnerable = true;
        Run(world, 0.5);
        player.Controls = new PlayerControls(dvec3.Zero, false, Crouch: true);
        Run(world, 0.2);
        bool ok = true;
        foreach (dvec3 direction in (dvec3[])[dvec3.UnitX, -dvec3.UnitX, dvec3.UnitY])
        {
            player.Controls = new PlayerControls(dvec3.Zero, false, Crouch: true);
            Run(world, 0.2);
            dvec3 start = player.Feet;
            player.Controls = new PlayerControls(direction, false, Crouch: true);
            Run(world, 0.4);
            ok &= Check($"crouches along slope {direction}", dvec3.Dot(player.Feet - start, direction), 0.65, 0.95);
        }
        player.Controls = new PlayerControls(dvec3.UnitY, false, Crouch: true);
        Run(world, 2);
        ok &= Check("stays on inclined ledge", slab.ToLocal(player.Feet).y, 3.9, 4.4)
            & Check("still above slab", slab.ToLocal(player.Feet).z, 0.95, 1.15);
        double startAlongEdge = slab.ToLocal(player.Feet).x;
        player.Controls = new PlayerControls(new dvec3(1, 1, 0).Normalized, false, Crouch: true);
        Run(world, 2);
        dvec3 corner = slab.ToLocal(player.Feet);
        ok &= Check("can slide along inclined edge toward corner", corner.x - startAlongEdge, .75, 4)
            & Check("crouch retains support at inclined corner", corner.z, .95, 1.15)
            & Check("corner footprint stays over slab x", corner.x, -.4, 6.4)
            & Check("corner footprint stays over slab y", corner.y, -.4, 4.4);
        return ok;
    }

    private static bool DiscreteOverlapRecovery()
    {
        bool ok = true;
        foreach (double angle in new[] { 0.0, 0.3 })
        {
            using World world = CreateWorld();
            world.Physics.Settings.Gravity = dvec3.Zero;
            VoxelObject a = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(4, 4, 4), dquat.FromAxisAngle(angle, dvec3.UnitY));
            VoxelObject b = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(4.8, 4, 4), dquat.Identity);
            world.ApplyTopologyChanges();
            dvec3 center = (a.Body!.WorldCenterOfMass + b.Body!.WorldCenterOfMass) / 2;
            world.StepOnce();
            ok &= Check($"overlap {angle}: pushes apart", (a.Body.WorldCenterOfMass - b.Body.WorldCenterOfMass).Length, 0.99, 1.3)
                & Check("preserves center of mass", ((a.Body.WorldCenterOfMass + b.Body.WorldCenterOfMass) / 2 - center).Length, 0, 1e-5)
                & Check("does not launch objects", a.LinearVelocity.Length + b.LinearVelocity.Length, 0, 0.03);
        }
        return ok;
    }
}
