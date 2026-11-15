using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    // A buffered jump can find the floor through GroundBelow before the landing solver has
    // removed the downward velocity. It must still launch at full speed, including when a
    // second jump is pressed during the last few frames of the first one's descent.
    private static bool PlayerLandingJump()
    {
        bool ok = true;
        foreach ((double fallingSpeed, int landingDelay) in new[] { (0.0, -1), (3.0, 0), (9.0, 1), (15.0, 2) })
        {
            using World world = CreateWorld();
            Floor(world, 4);
            Player player = world.SpawnPlayer(new dvec3(2, 2, 2.05));
            player.Flying = false;
            player.Invulnerable = true;
            player.Body.LinearVelocity = new dvec3(0, 0, -fallingSpeed);
            player.JumpPressed = true;
            world.StepOnce();
            ok &= Check($"falling at {fallingSpeed} m/s: takeoff speed", player.Body.LinearVelocity.z, 9.0, 10.2);
            double highest = player.Feet.z;
            Run(world, 0.8, () =>
            {
                highest = Math.Max(highest, player.Feet.z);
                return false;
            });
            ok &= Check("jump rise (m)", highest - 2.05, 1.45, 1.75);

            // Repeat just before landing, on the landing step, and one or two steps later.
            Run(world, 0.3);
            player.JumpPressed = true;
            world.StepOnce();
            double nearLanding = Run(world, 1.0, () => player.Body.LinearVelocity.z < -1.0 && player.Feet.z < 2.25);
            ok &= Check("first jump reaches its landing", nearLanding, 0.4, 0.8);
            if (landingDelay >= 0)
            {
                double landed = Run(world, 0.15, () => player.Grounded);
                ok &= Check("lands before pressing jump (s)", landed, World.Step, 0.15);
                for (int step = 0; step < landingDelay; step++)
                    world.StepOnce();
            }
            player.JumpPressed = true;
            double rebound = Run(world, 0.15, () => player.Body.LinearVelocity.z > 1.0);
            ok &= Check($"jump at landing offset {landingDelay}: takes off (s)", rebound, World.Step, 0.15);
            ok &= Check("repeated takeoff speed (m/s)", player.Body.LinearVelocity.z, 9.0, 10.2);
        }
        return ok;
    }

    private static bool PlayerEdgeRest()
    {
        bool ok = true;
        foreach (double overlap in new[] { 0.02, 0.05, 0.15 })
        {
            using World world = CreateWorld();
            world.SetBlock(world.Map, ivec3.Zero, BlockType.Bedrock);
            Player player = world.SpawnPlayer(new dvec3(1 + Player.Width / 2 - overlap, 0.5, 1));
            player.Flying = false;
            player.Invulnerable = true;
            double x = player.Feet.x;
            Run(world, 0.5);
            ok &= Check($"{overlap} m of sole on ledge: height", player.Feet.z, 0.995, 1.005);
            ok &= Check("horizontal drift without input", Math.Abs(player.Feet.x - x), 0, 0.002);
        }
        return ok;
    }

    // Chunks are separate colliders, so every seam between them is an open edge of each: the
    // player must cross them, slide along walls through them and climb blocks beside them as
    // if the map were one surface. Chunks are 16 cells, so seams lie at 16 and 32.
    private static bool PlayerChunkSeams()
    {
        bool ok = true;
        double Slowest(World world, Player player, dvec3 direction, double seconds, Func<double>? along = null, double start = 0.3)
        {
            player.Controls = new PlayerControls(direction, Sprint: false);
            Run(world, start);
            double slowest = double.PositiveInfinity;
            Run(world, seconds, () =>
            {
                slowest = Math.Min(slowest, along?.Invoke() ?? dvec3.Dot(player.Body.LinearVelocity, direction));
                return false;
            });
            player.Controls = default;
            return slowest;
        }

        // Flat ground, across a seam and through the corner where four chunks meet.
        foreach (dvec2 start in (dvec2[])[new(12.5, 10.5), new(12.5, 12.5), new(12.3, 15.7)])
        {
            using World world = CreateWorld();
            Floor(world, 32);
            Player player = world.SpawnPlayer(new dvec3(start, 2.0));
            player.Flying = false;
            Run(world, 0.5);
            dvec3 direction = start.y == 10.5 ? new dvec3(1, 0, 0) : new dvec3(1, 1, 0).Normalized;
            ok &= Check($"flat from {start}: slowest walking across the seams (m/s)", Slowest(world, player, direction, 1.5), 3.5, 5.0);
            ok &= Check("its feet (m)", player.Feet.z, 1.99, 2.01);
        }

        // Pressed against a wall one block high that runs through the seam at x = 16, standing
        // on a floor at z = 2, and on one at z = 16, where the wall's foot is a seam as well.
        foreach (int level in (int[])[2, 16])
        {
            using World world = CreateWorld();
            for (int x = 0; x < 32; x++)
            for (int y = 0; y < 32; y++)
            for (int z = 0; z < level; z++)
                world.SetBlock(world.Map, new ivec3(x, y, z), z == 0 ? BlockType.Bedrock : BlockType.Stone);
            for (int x = 8; x < 26; x++)
                world.SetBlock(world.Map, new ivec3(x, 12, level), BlockType.Stone);
            Player player = world.SpawnPlayer(new dvec3(11, 11.69, level));
            player.Flying = false;
            Run(world, 0.5);
            dvec3 direction = new dvec3(1, 0.3, 0).Normalized;
            ok &= Check($"along a wall through the seam, floor at {level}: slowest (m/s)", Slowest(world, player, direction, 1.5, () => player.Body.LinearVelocity.x), 3.0, 5.0);
            ok &= Check("held by the wall (m)", player.Feet.y, 11.69, 11.71);
        }

        // A loose block dropped across the seam rests on it, and the player pushes it across.
        {
            using World world = CreateWorld();
            Floor(world, 32);
            VoxelObject block = world.Spawn([(ivec3.Zero, BlockType.OakPlanks)], new dvec3(15.5, 10.0, 2.3), dquat.Identity);
            world.ApplyTopologyChanges();
            Run(world, 1.0);
            ok &= Check("block across the seam resting at (m)", block.Position.z, 1.99, 2.01);
            ok &= Check("block across the seam at rest (m/s)", block.LinearVelocity.Length, 0, 0.02);
            Player player = world.SpawnPlayer(new dvec3(13.5, 10.5, 2));
            player.Flying = false;
            Run(world, 0.5);
            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            Run(world, 1.5);
            ok &= Check("block pushed over the seam (m)", block.Position.x, 17.0, 24.0);
            ok &= Check("its bottom (m)", block.Position.z, 1.99, 2.01);
        }

        // Cutting the inside corner of an L one block high whose corner lies on the seam, as in
        // player-corner, on top of the L.
        foreach (dvec2 start in (dvec2[])[new(16.8, 15.4), new(14.8, 15.4)])
        {
            using World world = CreateWorld();
            Floor(world, 32);
            int column = (int)Math.Floor(start.x);
            for (int y = 10; y <= 16; y++)
                world.SetBlock(world.Map, new ivec3(column, y, 2), BlockType.Stone);
            for (int x = column + 1; x <= column + 5; x++)
                world.SetBlock(world.Map, new ivec3(x, 16, 2), BlockType.Stone);
            Player player = world.SpawnPlayer(new dvec3(start, 3.0));
            player.Flying = false;
            Run(world, 0.5);
            dvec3 direction = new dvec3(1, 1, 0).Normalized;
            ok &= Check($"corner at the seam from {start}: slowest (m/s)", Slowest(world, player, direction, 0.45, () => player.Body.LinearVelocity.y, 0.05), 2.0, 3.3);
            ok &= Check("on the other arm (y, m)", player.Feet.y, 16.5, 17.2);
            ok &= Check("its feet (m)", player.Feet.z, 2.99, 3.01);
        }

        // Jumping onto a block that stands just across the seam, and walking on over it.
        foreach (int x in (int[])[16, 15])
        {
            using World world = CreateWorld();
            Floor(world, 32);
            for (int y = 8; y < 13; y++)
                world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);
            Player player = world.SpawnPlayer(new dvec3(x - 2.5, 10.5, 2.0));
            player.Flying = false;
            Run(world, 0.5);
            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            Run(world, 0.25);
            player.JumpPressed = true;
            double backward = 0.0;
            Run(world, 1.2, () =>
            {
                backward = Math.Min(backward, player.Body.LinearVelocity.x);
                return false;
            });
            ok &= Check($"onto a block at x = {x}: thrown back (m/s)", -backward, 0.0, 0.2);
            ok &= Check("past it (m)", player.Feet.x, x + 2.0, x + 6.0);
        }

        return ok;
    }

    private static bool PlayerWallHole()
    {
        using World world = CreateWorld();
        Floor(world, 12);
        for (int y = 1; y < 11; y++)
        for (int z = 2; z < 4; z++)
            if (y != 5 || z != 2)
                world.SetBlock(world.Map, new ivec3(6, y, z), BlockType.Bedrock);
        Player player = world.SpawnPlayer(new dvec3(5.7, 3, 2));
        player.Flying = false;
        Run(world, 0.2);
        player.Controls = new PlayerControls(new dvec3(0.6, 0.8, 0), false);
        Run(world, 1.5);
        return Check("slides beyond the foot-level hole", player.Feet.y, 7.8, 8.6)
            & Check("wall still blocks forward motion", player.Feet.x, 5.69, 5.71);
    }

    private static bool ImpactKinematicsChecks()
    {
        bool ok = true;
        foreach (double scale in new[] { 1e-6, 1.0, 1e6 })
        {
            ImpactMotion wallHit = new(2, dmat3.Identity, new dvec3(3, 7, 0), dvec3.Zero)
                { Impulse = new dvec3(-scale, 0, 0) };
            ok &= Check($"impulse scale {scale}: normal energy, tangential motion excluded", ImpactKinematics.CompressionEnergy([wallHit]), 9 - 1e-9, 9 + 1e-9);
            ImpactMotion offCenter = new(2, dmat3.Identity, new dvec3(3, 0, 0), dvec3.Zero)
                { Impulse = new dvec3(-scale, 0, 0), AngularImpulse = new dvec3(0, 0, scale) };
            ok &= Check("off-center hit retains free rotational motion", ImpactKinematics.CompressionEnergy([offCenter]), 3 - 1e-9, 3 + 1e-9);
        }
        foreach (double boost in new[] { 0.0, 100.0 })
        {
            ImpactMotion a = new(2, dmat3.Zero, new dvec3(3 + boost, 0, 0), dvec3.Zero) { Impulse = -dvec3.UnitX };
            ImpactMotion b = new(6, dmat3.Zero, new dvec3(-1 + boost, 0, 0), dvec3.Zero) { Impulse = dvec3.UnitX };
            ok &= Check($"two bodies, common velocity {boost}: reduced-mass energy", ImpactKinematics.CompressionEnergy([a, b]), 12 - 1e-9, 12 + 1e-9);
        }
        ImpactMotion spinning = new(2, dmat3.Identity, dvec3.Zero, new dvec3(0, 0, -3)) { AngularImpulse = dvec3.UnitZ };
        ok &= Check("angular impact energy", ImpactKinematics.CompressionEnergy([spinning]), 4.5, 4.5);
        ImpactMotion separating = new(2, dmat3.Identity, dvec3.UnitX, dvec3.Zero) { Impulse = dvec3.UnitX };
        ok &= Check("separating contact has no compression energy", ImpactKinematics.CompressionEnergy([separating]), 0, 0);
        ok &= Check("empty contact has no compression energy", ImpactKinematics.CompressionEnergy([]), 0, 0);
        return ok;
    }

    private static bool ImpactStepPhase()
    {
        bool ok = true;
        foreach (bool fixedRotation in new[] { false, true })
        foreach (double phase in new[] { 0.1, 0.5, 0.9 })
        {
            using World world = CreateWorld();
            world.Physics.Settings.Gravity = dvec3.Zero;
            for (int y = 0; y < 4; y++)
            for (int z = 0; z < 4; z++)
                world.SetBlock(world.Map, new ivec3(10, y, z), BlockType.Bedrock);
            const double speed = 8;
            VoxelObject block = world.Spawn([(ivec3.Zero, BlockType.Stone)],
                new dvec3(9 - speed * World.Step * (3 + phase), 1.3, 1.3), dquat.Identity, new dvec3(speed, 0, 0));
            world.ApplyTopologyChanges();
            block.Body!.FixedRotation = fixedRotation;
            double energy = 0;
            world.ImpactAnalysed += (_, incoming, _, _) => energy = Math.Max(energy, incoming);
            Run(world, 0.8);
            double expected = 0.5 * block.Body.Mass * speed * speed;
            ok &= Check($"hit at {phase:P0} of a step, locked {fixedRotation}: compression energy / incoming energy", energy / expected, 0.98, 1.02);
        }
        return ok;
    }

    private static bool GlassPlatformSlide()
    {
        using World world = CreateWorld();
        // Support the deck so this tests collision damage, not failure under its weight.
        for (int x = 2; x <= 6; x++)
        for (int y = 2; y <= 6; y++)
        {
            world.SetBlock(world.Map, new ivec3(x, y, 3), BlockType.Bedrock);
            world.SetBlock(world.Map, new ivec3(x, y, 4), BlockType.Glass);
        }
        VoxelObject block = world.Spawn([(ivec3.Zero, BlockType.OakPlanks)], new dvec3(4, 4, 5), dquat.Identity);
        Player player = world.SpawnPlayer(new dvec3(3, 4.5, 5));
        player.Flying = false;
        Run(world, 0.5);
        player.Controls = new PlayerControls(dvec3.UnitX, Sprint: false);
        double crossed = Run(world, 2, () => block.Position.x > 7.25);
        player.Controls = default;
        bool ok = Check("block pushed past the platform rim (s)", crossed, 0, 2);
        double damage = 0;
        for (int x = 2; x <= 6; x++)
        for (int y = 2; y <= 6; y++)
        {
            Voxel cell = world.Map.Grid[new ivec3(x, y, 4)];
            damage += cell.Type == BlockType.Glass ? cell.DamageFraction : 1;
        }
        ok &= Check("glass deck damage from sliding", damage, 0, 0);
        return ok;
    }
}
