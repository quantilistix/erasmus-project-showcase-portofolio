using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Vulkan;

using PhysicsTests;

using VoxelGame;

namespace VoxelTests;


/// <summary>
/// Headless checks of the game world: physics, stress analysis and breaking together. The
/// stress analysis runs on its own thread, so these step in real time (twice as fast as the
/// game) to give it the time it gets in play.
/// </summary>
internal static partial class WorldTests
{
    private const double PacedStepMilliseconds = 8.0;

    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("placement-obstructions", PlacementObstructions),
        ("player-crouch-item", PlayerCrouchItem),
        ("player-tool-jump", PlayerToolJump),
        ("player-tilted-wall", PlayerTiltedWall),
        ("large-object-player", LargeObjectPlayer),
        ("landed-large-object-player", LandedLargeObjectPlayer),
        ("stress-fracture-batch", StressFractureBatch),
        ("stressed-mining", StressedMining),
        ("health-model", HealthModel),
        ("world-mining", Mining),
        ("mining-strikes", MiningStrikes),
        ("mining-recoil", MiningRecoil),
        ("mining-split-recoil", MiningSplitRecoil),
        ("block-particles", BlockParticleEffects),
        ("tnt-explosion", TntExplosion),
        ("tnt-chain", TntChain),
        ("tnt-flashes", TntFlashes),
        ("tnt-particles", TntParticles),
        ("particle-air", ParticleAir),
        ("pressure-waves", PressureWaves),
        ("pressure-support", PressureSupport),
        ("pressure-grid-benchmark", PressureGridBenchmark),
        ("gpu-pressure-stencil", GpuPressureStencil),
        ("gpu-pressure-sources", GpuPressureSources),
        ("gpu-pressure-damage", GpuPressureDamage),
        ("gpu-pressure-mirror", GpuPressureMirror),
        ("gpu-pressure-objects", GpuPressureObjects),
        ("gpu-pressure-world", GpuPressureWorld),
        ("gpu-damage-laws", GpuDamageLaws),
        ("tnt-global-grid", TntGlobalGrid),
        ("tnt-spatial-benchmark", TntSpatialBenchmark),
        ("tnt-superposition", TntSuperposition),
        ("tnt-world-step", TntWorldStep),
        ("tnt-stone", TntStone),
        ("tnt-punch", TntPunch),
        ("tnt-player-damage", TntPlayerDamage),
        ("damage-particle-budget", DamageParticleBudget),
        ("world-raycast", RaycastBounds),
        ("particle-raycast", ParticleRaycastBatch),
        ("particle-containment", ParticleContainment),
        ("particle-cannon", ParticleCannon),
        ("world-many", ManyObjects),
        ("contact-mapping", ContactMapping),
        ("contact-single-cell", SingleCellContacts),
        ("world-hanging", Hanging),
        ("world-cantilever", Cantilever),
        ("world-table-legs", TableLegs),
        ("world-split", Split),
        ("world-detach", Detach),
        ("world-impact", Impact),
        ("world-smash-bridge", SmashBridge),
        ("world-mass-matters", MassMatters),
        ("world-through-glass", ThroughGlass),
        ("world-soft-ground", SoftGround),
        ("world-build-on-object", BuildOnObject),
        ("world-big-object", BigObject),
        ("world-terrain", Terrain),
        ("crouch-collision-geometry", CrouchCollisionGeometry),
        ("player-crouch-shape", PlayerCrouchShape),
        ("player-crouch-ledges", PlayerCrouchLedges),
        ("player-crouch-clearance", PlayerCrouchClearance),
        ("player-crouch-slope", PlayerCrouchSlope),
        ("discrete-overlap-recovery", DiscreteOverlapRecovery),
        ("player-stand", PlayerStands),
        ("player-walk", PlayerWalks),
        ("player-landing-jump", PlayerLandingJump),
        ("player-grip", PlayerGrip),
        ("player-push-back", PlayerPushesBack),
        ("player-fall", PlayerFalls),
        ("player-crushed", PlayerCrushed),
        ("player-hit", PlayerHit),
        ("player-smash", PlayerSmash),
        ("player-ledges", PlayerLedges),
        ("player-slope", PlayerSlope),
        ("player-step", PlayerSteps),
        ("player-push", PlayerPushes),
        ("glass-platform-slide", GlassPlatformSlide),
        ("impact-step-phase", ImpactStepPhase),
        ("impact-kinematics", ImpactKinematicsChecks),
        ("world-drops", Drops),
        ("player-terrain", PlayerTerrain),
        ("world-glass-wall", GlassWallOnGrass),
        ("player-corner", PlayerCutsCorner),
        ("player-lip", PlayerOverLips),
        ("player-jump-onto", PlayerJumpsOnto)
        ,("player-edge-rest", PlayerEdgeRest)
        ,("player-wall-hole", PlayerWallHole)
        ,("player-chunk-seams", PlayerChunkSeams)
    ];

    /// <summary>A strike that gets through any block, at a hand's speed: mining at the full rate, as before there were tools.</summary>
    private static readonly Tool Unstoppable = new(double.PositiveInfinity, 1.0);

    private static World CreateWorld(bool gpuBlasts = false)
    {
        HeadlessVulkan vulkan = TestGpu.Vulkan;
        World world = new(vulkan.Vk, vulkan.Device, vulkan.PhysicalDevice, vulkan.CommandPool, vulkan.Queue, TestGpu.Shaders);
        // Existing blast scenarios specify the wavefront reference model. The
        // acoustic GPU model has separate checks and can be selected explicitly.
        world.GpuBlastsEnabled = gpuBlasts;
        if (Environment.GetEnvironmentVariable("MYT_LOG") == "1")
        {
            world.BlockBroken += (body, cell, voxel, cause) =>
                Console.WriteLine($"      {voxel.Material.Name} at {cell} of {(body is VoxelMap ? "the map" : $"object {body.Id}")} broke: {cause}");
            world.ImpactAnalysed += (bodies, energy, outcome, steps) =>
                Console.WriteLine($"      impact of {energy / 1e3:F1} kJ: {outcome.Sides.Sum(s => s.Damage.Length)} damaged, opened {string.Join("/", outcome.Sides.Select(s => s.Opened))}, {outcome.RetainedEnergy / energy:P0} left, {outcome.Rounds} rounds, {outcome.Milliseconds:F0} ms, {steps} steps after");
            world.PlayerHurt += (damage, cause) =>
                Console.WriteLine($"      the player took {damage:F2} HP from {cause}: health {world.Player!.Health:P0}");
        }

        return world;
    }

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    /// <summary>A bedrock slab with a stone floor on it, size × size.</summary>
    private static void Floor(World world, int size)
    {
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
            world.SetBlock(world.Map, new ivec3(x, y, 1), BlockType.Stone);
        }
    }

    /// <summary>
    /// Steps for a while of simulated time, paced so the background analyses keep up, and stops
    /// early once <paramref name="until"/> holds. Returns the simulated time that took, or
    /// infinity if it never held.
    /// </summary>
    private static double Run(World world, double seconds, Func<bool>? until = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int steps = (int)(seconds / World.Step);
        for (int step = 0; step < steps; step++)
        {
            world.StepOnce();
            world.ApplyTopologyChanges();
            if (until != null && until())
                return (step + 1) * World.Step;

            double wait = (step + 1) * PacedStepMilliseconds - watch.Elapsed.TotalMilliseconds;
            if (wait > 0)
                Thread.Sleep((int)wait);
        }

        return double.PositiveInfinity;
    }

    private static bool ParticleRaycastBatch()
    {
        using World world = CreateWorld();
        Random random = new(71);
        for (int i = 0; i < 1000; i++)
            world.Spawn(TerrainGenerator.Box(new ivec3(i % 6 == 0 ? 3 : 1), BlockType.Stone),
                new dvec3(i % 20 * 5, i / 20 % 20 * 5, i / 400 * 5 + 10),
                dquat.FromAxisAngle(random.NextDouble() * Math.Tau, new dvec3(1, 2, 3).Normalized));
        world.ApplyTopologyChanges();
        ParticleRaycaster batch = new();
        batch.Prepare(world);
        Stopwatch watch = Stopwatch.StartNew();
        batch.Prepare(world);
        double build = watch.Elapsed.TotalMilliseconds;
        (dvec3 Origin, dvec3 Direction)[] rays = new (dvec3, dvec3)[2048];
        VoxelHit?[] reference = new VoxelHit?[rays.Length];
        for (int i = 0; i < rays.Length; i++)
        {
            VoxelObject body = world.Objects[i % world.Objects.Count];
            dvec3 direction = new(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            rays[i] = (body.ToWorld(new dvec3(0.5)) - direction * 1.5, direction);
            batch.Raycast(rays[i].Origin, direction, 2); // Warm the query path before measuring.
        }
        watch.Restart();
        for (int i = 0; i < rays.Length; i++)
            reference[i] = world.Raycast(rays[i].Origin, rays[i].Direction, 2);
        double scan = watch.Elapsed.TotalMilliseconds;
        int mismatches = batch.Raycast(rays[0].Origin, rays[0].Direction, 2) == reference[0] ? 0 : 1;
        long before = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < rays.Length; i++)
            if (batch.Raycast(rays[i].Origin, rays[i].Direction, 2) != reference[i])
                mismatches++;
        double query = watch.Elapsed.TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"   1000 objects / 2048 debris rays: linear scan {scan:F3} ms, hierarchy refit {build:F3} ms, indexed queries {query:F3} ms; allocated {allocated} B/query batch");
        bool ok = Check("indexed debris rays differ from the reference", mismatches, 0, 0);
        ok &= Check("indexed queries allocate no managed memory", allocated, 0, 0);
        return ok;
    }

    /// <summary>Object culling must agree with a full grid walk, including rotated and dirty objects.</summary>
    private static bool RaycastBounds()
    {
        using World world = CreateWorld();
        Floor(world, 8);
        Random random = new(42);
        for (int i = 0; i < 40; i++)
        {
            world.Spawn(TerrainGenerator.Box(new ivec3(1 + i % 4, 1, 1), BlockType.Stone),
                new dvec3(random.NextDouble() * 30 - 15, random.NextDouble() * 30 - 15, random.NextDouble() * 10),
                dquat.FromAxisAngle(random.NextDouble() * Math.PI, new dvec3(1, 2, 3).Normalized));
        }
        ParticleRaycaster batch = new();
        bool ok = CompareRays(); // Bounds are not built yet.
        world.ApplyTopologyChanges();
        ok &= CompareRays();
        world.Objects[0].Body!.Position += new dvec3(7, 3, 2);
        world.Objects[0].Body!.Orientation = dquat.FromAxisAngle(1.2, dvec3.UnitY);
        ok &= CompareRays(); // Refit after motion, with the same body membership.
        world.SetBlock(world.Objects[0], new ivec3(20, 0, 0), BlockType.Stone);
        ok &= CompareRays(); // A cell added outside the old bounds is still targetable.
        return ok;

        bool CompareRays()
        {
            batch.Prepare(world);
            int mismatches = 0;
            foreach (VoxelObject target in world.Objects)
            {
                dvec3 center = target.ToWorld(VoxelBody.CellCenter(new ivec3(20, 0, 0)));
                Compare(center + new dvec3(0, 0, 3), new dvec3(0, 0, -2), 5);
            }
            for (int i = 0; i < 400; i++)
            {
                dvec3 origin = new(random.NextDouble() * 40 - 20, random.NextDouble() * 40 - 20, random.NextDouble() * 14);
                VoxelObject target = world.Objects[i % world.Objects.Count];
                Compare(origin, target.ToWorld(new dvec3(0.5)) - origin, i % 3 == 0 ? 2 : 40);
            }
            Compare(dvec3.Zero, dvec3.Zero, 10);
            return Check("culled raycasts differing from the full grid walk", mismatches, 0, 0);

            void Compare(dvec3 origin, dvec3 direction, double reach)
            {
                VoxelHit? actual = world.Raycast(origin, direction, reach);
                VoxelHit? expected = null;
                foreach (VoxelBody body in world.Bodies)
                {
                    if (body.Grid.Raycast(body.ToLocal(origin) / VoxelBody.CellSize, body.DirectionToLocal(direction),
                        (expected?.Distance ?? reach) / VoxelBody.CellSize, out Grid3DHit hit))
                    {
                        double distance = hit.Distance * VoxelBody.CellSize;
                        if (expected == null || distance < expected.Value.Distance)
                            expected = new VoxelHit(body, hit.Cell, hit.Normal, distance);
                    }
                }
                if (actual != expected)
                    mismatches++;
                if (batch.Raycast(origin, direction, reach) != expected)
                    mismatches++;
            }
        }
    }

    /// <summary>Exercise physics, player rays, contact loads and stress with more than 140 objects.</summary>
    private static bool SingleCellContacts()
    {
        Random random = new(41);
        int differences = 0;
        long referenceTicks = 0, fastTicks = 0;
        foreach (ivec3 occupied in (ivec3[])[new(0), new(-17, 16, -1), new(31, -32, 8)])
        {
            VoxelObject body = new(1);
            body.Grid[occupied] = new Voxel(BlockType.Stone, 0);
            List<(dvec3 Point, dvec3 Normal)> samples = [];
            for (int i = 0; i < 10000; i++)
            {
                dvec3 point = new dvec3(occupied) + new dvec3(random.NextDouble() * 4 - 1.5,
                    random.NextDouble() * 4 - 1.5, random.NextDouble() * 4 - 1.5);
                dvec3 normal = new dvec3(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1).Normalized;
                samples.Add((point, normal));
            }
            // Exact half-cell and face boundaries, corners, zero directions and ties.
            for (int z = -2; z <= 4; z++)
            for (int y = -2; y <= 4; y++)
            for (int x = -2; x <= 4; x++)
            foreach (ivec3 normal in Grid3D<Voxel>.FaceDirections.Append(ivec3.Zero))
                samples.Add((new dvec3(occupied) + new dvec3(x, y, z) * 0.5, new dvec3(normal)));
            foreach (var sample in samples)
            {
                bool hit = body.TryFindSurfaceCell(sample.Point, sample.Normal, out ivec3 expected);
                bool fast = VoxelBody.TryFindSingleSurfaceCell(occupied, sample.Point, sample.Normal, out ivec3 actual);
                if (hit != fast || expected != actual) differences++;
            }
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++)
                foreach (var sample in samples) body.TryFindSurfaceCell(sample.Point, sample.Normal, out _);
            referenceTicks += Stopwatch.GetTimestamp() - start;
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++)
                foreach (var sample in samples) VoxelBody.TryFindSingleSurfaceCell(occupied, sample.Point, sample.Normal, out _);
            fastTicks += Stopwatch.GetTimestamp() - start;
        }
        Console.WriteLine($"   surface lookup reference {referenceTicks * 1000.0 / Stopwatch.Frequency:F2} ms, singleton {fastTicks * 1000.0 / Stopwatch.Frequency:F2} ms");
        return Check("singleton hit, miss and selected-cell differences", differences, 0, 0);
    }

    private static bool ContactMapping()
    {
        using World world = CreateWorld();
        Floor(world, 32);
        // Offset, rotated voxel surfaces exercise corner-cell selection on both sides.
        for (int i = 0; i < 144; i++)
        {
            world.Spawn([(ivec3.Zero, BlockType.Bedrock)], new dvec3(2 + i % 12 * 2, 2 + i / 12 * 2, 2.01),
                dquat.FromAxisAngle((i % 3 - 1) * 0.1, dvec3.UnitZ));
            if (i % 4 == 0)
                world.Spawn([(ivec3.Zero, BlockType.Bedrock)], new dvec3(2 + i % 12 * 2, 2 + i / 12 * 2, 3.05),
                    dquat.FromAxisAngle(0.03, dvec3.UnitY));
        }
        Player player = world.SpawnPlayer(new dvec3(4.2, 2.2, 3.03));
        player.Invulnerable = true;
        player.Controls = new PlayerControls(new dvec3(1, 0, 0), false);
        int reports = 0, mismatches = 0;
        for (int step = 0; step < 20; step++)
        {
            world.StepOnce();
            reports = Math.Max(reports, world.Physics.ContactReports.Count);
            mismatches += world.CheckContactMapping();
        }
        return Check("reports exercise the parallel batch threshold", reports, 512, double.MaxValue)
            & Check("serial/parallel mapping and ordered cell-load mismatches", mismatches, 0, 0);
    }

    private static bool ManyObjects()
    {
        using World world = CreateWorld();
        int count = int.TryParse(Environment.GetEnvironmentVariable("MYT_WORLD_COUNT"), out int n) ? Math.Max(n, 1) : 144;
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        Floor(world, Math.Max(28, side * 2 + 4));
        for (int i = 0; i < count; i++)
            world.Spawn([(ivec3.Zero, BlockType.Bedrock)], new dvec3(2 + i % side * 2, 2 + i / side * 2, 2), dquat.Identity);
        Player player = world.SpawnPlayer(new dvec3(0.5, 0.5, 2));
        player.Invulnerable = true;
        for (int i = 0; i < 60; i++)
            world.StepOnce();

        double[] times = new double[120];
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            world.StepOnce();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(times);
        Console.WriteLine($"   {count} voxel objects + player: mean {times.Average():F3} ms/step, p50 {times[times.Length / 2]:F3}, p95 {times[(int)(times.Length * 0.95)]:F3}, allocated {allocated / times.Length} B/step");
        return Check("objects retained", world.Objects.Count, count, count)
            & Check("lowest object bottom", world.Objects.Min(o => o.Position.z), 1.995, 2.005)
            & Check("fastest resting object", world.Objects.Max(o => o.LinearVelocity.Length + o.AngularVelocity.Length), 0, 0.05)
            & Check("player standing", player.Feet.z, 1.995, 2.01)
            & Check("dropped contacts", world.Physics.Statistics.DroppedContacts, 0, 0)
            & Check("dropped reports", world.Physics.Statistics.DroppedContactReports, 0, 0);
    }

    private static void PrintMostLoaded(VoxelBody body, int count)
    {
        foreach ((ivec3 cell, Voxel voxel) in body.Grid.Occupied().OrderByDescending(c => body.StressRatio(c.Cell, c.Value)).Take(count))
        {
            MYT.Kit.Structural.CellStress s = body.StaticStress(cell);
            Console.WriteLine($"      {voxel.Material.Name,-12} at {cell}: {body.StressRatio(cell, voxel):F2} of strength (tension {s.Tension / 1e3:F0} kPa, compression {s.Compression / 1e3:F0} kPa, shear {s.Shear / 1e3:F0} kPa)");
        }
    }

    private static double MaxRatio(VoxelBody body)
    {
        double max = 0.0;
        foreach ((ivec3 cell, Voxel voxel) in body.Grid.Occupied())
            max = Math.Max(max, body.StressRatio(cell, voxel));
        return max;
    }

    /// <summary>
    /// A block's effective stiffness is its stiffness times its health: its elastic limit
    /// falls with its health, and damage costs a worn block more health.
    /// </summary>
    private static bool HealthModel()
    {
        // Analytical wear-law fixture, independent of gameplay durability tuning.
        BlockMaterial stone = Blocks.Get(BlockType.Stone) with { Durability = 100 };
        bool ok = Check("breaking damage of the 100-HP reference material", stone.DamageToBreak(1.0), 50.0, 50.0);
        ok &= Check("health after half of it", stone.HealthAfter(1.0, 25.0), 0.7071, 0.7072);
        ok &= Check("health after all of it", stone.HealthAfter(1.0, 50.0), 0.0, 0.0);
        ok &= Check("health 10 HP takes from a fresh block", 1.0 - stone.HealthAfter(1.0, 10.0), 0.105, 0.106);
        ok &= Check("health 10 HP takes from a block at half health", 0.5 - stone.HealthAfter(0.5, 10.0), 0.276, 0.277);
        ok &= Check("tensile elastic limit at half health (Pa)", stone.ElasticLimit(0.5).Tension, 3e6, 3e6);
        ok &= Check("Young's modulus left at no health (Pa)", stone.Elastic(0.0).YoungsModulus, 10e9, 10e9);
        ok &= Check("mining time from the mining rate (s)", stone.DamageToBreak(1.0) / stone.MiningRate, 2.0, 2.0);

        BlockMaterial leaves = Blocks.Get(BlockType.Leaves);
        ok &= Check("leaves' tensile elastic limit, a stiffness of 0.5 (Pa)", leaves.ElasticLimit(1.0).Tension, 20e3, 20e3);
        ok &= Check("bedrock's damage to break", Blocks.Get(BlockType.Bedrock).DamageToBreak(1.0), double.PositiveInfinity, double.PositiveInfinity);
        return ok;
    }

    /// <summary>
    /// Stone takes 2 s to mine. After 1 s its health is √½, not ½: damage costs a worn block
    /// more health, so the second second does the rest. The damage stays when mining stops.
    /// </summary>
    private static bool Mining()
    {
        using World world = CreateWorld();
        Floor(world, 4);
        ivec3 cell = new(1, 1, 1);
        world.Mine(world.Map, cell, 1.0);
        Run(world, 1.0);

        bool ok = Check("health a second after mining stopped", world.Map.Grid[cell].Health, 0.706, 0.708);
        world.Mine(world.Map, cell, 1.01);
        world.ApplyTopologyChanges();
        ok &= Check("block gone after 2 s of mining in all", world.Map.Grid.IsOccupied(cell) ? 1 : 0, 0, 0);
        ok &= Check("bedrock does not break", world.Damage(world.Map, new ivec3(0, 0, 0), 1e9, "test") ? 1 : 0, 0, 0);
        return ok;
    }

    private static bool MiningStrikes()
    {
        using World world = CreateWorld();
        Player player = world.SpawnPlayer(new dvec3(0.5, 0.5, 10));
        ivec3 cell = new(3, 0, 11);
        world.SetBlock(world.Map, cell, BlockType.Stone);
        VoxelHit hit = world.Raycast(player.Eye, dvec3.UnitX, 8)!.Value;
        for (int i = 0; i < 6; i++)
            world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);

        bool ok = Check("six hits deal one second of stone damage", world.Map.Grid[cell].Wear, 0.4999, 0.5001);
        ok &= Check("half of the block's particle budget", world.Particles.Count, BlockParticles.ParticlesPerBlock / 2, BlockParticles.ParticlesPerBlock / 2);
        int hits = 6;
        while (world.Map.Grid.IsOccupied(cell) && hits < 14)
        {
            world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);
            hits++;
        }
        ok &= Check("hits to break stone (health rounding may add one)", hits, 12, 13);
        ok &= Check("broken stone is gone", world.Map.Grid.IsOccupied(cell) ? 1 : 0, 0, 0);
        ok &= Check("one total particle budget through destruction", world.Particles.Count, BlockParticles.ParticlesPerBlock, BlockParticles.ParticlesPerBlock);
        int count = world.Particles.Count;
        world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);
        ok &= Check("mining air emits nothing", world.Particles.Count - count, 0, 0);
        world.SetBlock(world.Map, cell, BlockType.Bedrock);
        world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);
        ok &= Check("unbreakable block emits no damage chips", world.Particles.Count - count, 0, 0);
        return ok;
    }

    private static bool MiningRecoil()
    {
        using World world = CreateWorld();
        world.Physics.Settings.Gravity = dvec3.Zero;
        Player player = world.SpawnPlayer(new dvec3(0.5, 0.5, 10));
        VoxelObject obj = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(3, 0, 11), dquat.Identity);
        world.ApplyTopologyChanges();
        VoxelHit hit = world.Raycast(player.Eye, dvec3.UnitX, 8)!.Value;
        world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);
        world.Physics.Update(World.Step);

        dvec3 momentum = obj.Body!.Mass * obj.LinearVelocity + Player.Mass * player.LinearVelocity;
        bool ok = Check("equal and opposite mining momentum (N s)", momentum.Length, 0, 1e-6);
        ok &= Check("player recoil (m/s)", player.LinearVelocity.x, -0.25001, -0.24999);
        ok &= Check("target thrust (N s)", obj.Body.Mass * obj.LinearVelocity.x, 19.999, 20.001);
        ok &= Check("off-center hit spins target", Math.Abs(obj.AngularVelocity.y), 1e-4, 1);
        ok &= Check("player stays upright", player.AngularVelocity.Length, 0, 0);
        return ok;
    }

    private static bool MiningSplitRecoil()
    {
        using World world = CreateWorld();
        world.Physics.Settings.Gravity = dvec3.Zero;
        Player player = world.SpawnPlayer(new dvec3(0.5, 1.5, 10));
        VoxelObject obj = world.Spawn(TerrainGenerator.Box(new ivec3(1, 3, 1), BlockType.Stone), new dvec3(3, 0, 11), dquat.Identity);
        world.ApplyTopologyChanges();
        ivec3 middle = new(0, 1, 0);
        obj.SetDamage(middle, 0.99);
        VoxelHit hit = world.Raycast(player.Eye, dvec3.UnitX, 8)!.Value;
        bool broke = world.Mine(hit, player.Eye, dvec3.UnitX, Unstoppable);
        dvec3 linear = obj.LinearVelocity;
        dvec3 angular = obj.AngularVelocity;
        dvec3 oldCenter = obj.Body!.WorldCenterOfMass;
        world.ApplyTopologyChanges();
        bool ok = Check("breaking hit splits the object", broke && world.Objects.Count == 2 ? 1 : 0, 1, 1);
        foreach (VoxelObject piece in world.Objects)
        {
            dvec3 expected = linear + dvec3.Cross(angular, piece.Body!.WorldCenterOfMass - oldCenter);
            ok &= Check("fragment keeps breaking-hit thrust", (piece.LinearVelocity - expected).Length, 0, 1e-8);
        }
        world.Physics.Update(World.Step);
        ok &= Check("final hit still recoils player", player.LinearVelocity.x, -0.25001, -0.24999);
        return ok;
    }

    private static bool BlockParticleEffects()
    {
        using World world = CreateWorld();
        ivec3 cell = new(0, 0, 0);
        world.SetBlock(world.Map, cell, BlockType.Grass);
        BlockDamageSite site = new(new dvec3(0.2, 0.7, 1), dvec3.UnitZ);
        world.Damage(world.Map, cell, Blocks.Get(BlockType.Grass).DamageToBreak(1.0) / 4.0, "test impact", site);
        int damagedCount = world.Particles.Count;
        world.Damage(world.Map, cell, 1e9, "test breaking", site);
        bool ok = Check("quarter damage emits a quarter of the particle budget", damagedCount, 8, 8);
        ok &= Check("overkill only emits the remaining particle budget", world.Particles.Count - damagedCount, 24, 24);

        int[,] layers = new int[Blocks.Count, 6];
        for (int type = 0; type < Blocks.Count; type++)
        for (int face = 0; face < 6; face++)
            layers[type, face] = type * 6 + face;
        Camera camera = new(new dvec3(0.2, -3, 1.5), dvec3.UnitY, Math.PI / 2, 1.0);
        ReadOnlySpan<ParticleVertex> vertices = world.Particles.BuildVertices(camera, layers, 20);
        ok &= Check("six vertices per billboard", vertices.Length, world.Particles.Count * 6, world.Particles.Count * 6);
        for (int i = 0; i < vertices.Length; i += 6)
        {
            ParticleVertex a = vertices[i], b = vertices[i + 1], c = vertices[i + 2];
            dvec3 center = (new dvec3(a.Position) + new dvec3(c.Position)) / 2.0;
            if (a.UvLayer.z != layers[(int)BlockType.Grass, 4]
                || Math.Abs(b.UvLayer.x - a.UvLayer.x - 2f / 16f) > 1e-6
                || Math.Abs(c.UvLayer.y - a.UvLayer.y - 2f / 16f) > 1e-6
                || a.UvLayer.x < 0 || a.UvLayer.y < 0 || c.UvLayer.x > 1 || c.UvLayer.y > 1
                || Math.Abs((b.Position - a.Position).Length - 0.125f) > 1e-6
                || Math.Abs(dvec3.Dot((dvec3)(b.Position - a.Position), camera.Forward)) > 1e-6
                || center.z < 1 || (new dvec3(center.x, center.y, 1) - site.Point).Length > 0.5)
                return false;
        }

        world.Particles.Update(world, 0);
        ok &= Check("paused particles stay alive", world.Particles.Count, 32, 32);
        for (int i = 0; i < 59; i++)
            world.Particles.Update(world, World.Step);
        ok &= Check("particles survive almost one second", world.Particles.Count, 32, 32);
        for (int i = 0; i < 31; i++)
            world.Particles.Update(world, World.Step);
        ok &= Check("random lifetimes expire at different times", world.Particles.Count, 1, 31);
        for (int i = 0; i < 31; i++)
            world.Particles.Update(world, World.Step);
        ok &= Check("all particles expire after two seconds", world.Particles.Count, 0, 0);
        for (int i = 0; i <= BlockParticles.MaxParticles / BlockParticles.ParticlesPerBlock; i++)
            world.Particles.Emit(world.Map, cell, BlockType.Grass, BlockParticles.ParticlesPerBlock, broken: true, site);
        ok &= Check("particle count stays bounded", world.Particles.Count, BlockParticles.MaxParticles, BlockParticles.MaxParticles);
        world.Particles.Enabled = false;
        ok &= Check("disabling particles immediately clears existing chips", world.Particles.Count, 0, 0);
        world.SetBlock(world.Map, cell, BlockType.Grass);
        world.Damage(world.Map, cell, 1e9, "disabled particles", site);
        ok &= Check("blocks still break while particles are disabled", (int)world.Map.Grid[cell].Type, (int)BlockType.Air, (int)BlockType.Air);
        ok &= Check("disabled damage emits no chips", world.Particles.Count, 0, 0);
        ok &= Check("disabled particles have no draw vertices", world.Particles.BuildVertices(camera, layers, 20).Length, 0, 0);
        world.Particles.Enabled = true;
        ok &= Check("re-enabling does not restore old chips", world.Particles.Count, 0, 0);
        world.SetBlock(world.Map, cell, BlockType.Grass);
        world.Damage(world.Map, cell, 1e9, "re-enabled particles", site);
        ok &= Check("re-enabled particles emit on breaking", world.Particles.Count, 32, 32);
        return ok;
    }

    /// <summary>Each material sheds the same total, even with thousands of tiny hits or one oversized hit.</summary>
    private static bool DamageParticleBudget()
    {
        using World world = CreateWorld();
        ivec3 cell = ivec3.Zero;
        bool ok = true;
        foreach (BlockType type in Enum.GetValues<BlockType>())
        {
            BlockMaterial material = Blocks.Get(type);
            if (type == BlockType.Air || material.Unbreakable)
                continue;

            // The last type's chips fade first: every type's together would pass the cap on particles.
            world.Particles.Update(world, 3.0);
            world.SetBlock(world.Map, cell, type);
            int before = world.Particles.Count;
            int hits = 0;
            while (world.Map.Grid.IsOccupied(cell) && hits++ < 30)
                world.Mine(world.Map, cell, 1.0 / Player.MiningHitsPerSecond);
            ok &= Check($"{material.Name}: total particles from mining", world.Particles.Count - before, 32, 32);
            ok &= Check($"{material.Name}: mined block is gone", world.Map.Grid.IsOccupied(cell) ? 1 : 0, 0, 0);

            world.SetBlock(world.Map, cell, type);
            before = world.Particles.Count;
            double damage = material.DamageToBreak(1.0) / 2000.0;
            world.Damage(world.Map, cell, damage, "tiny hit");
            ok &= Check($"{material.Name}: sub-particle hit does not emit a whole chip", world.Particles.Count - before, 0, 0);
            hits = 1;
            while (world.Map.Grid.IsOccupied(cell) && hits++ < 2500)
                world.Damage(world.Map, cell, damage, "tiny hit");
            ok &= Check($"{material.Name}: total particles from tiny hits", world.Particles.Count - before, 32, 32);
            ok &= Check($"{material.Name}: tiny hits eventually destroy block", world.Map.Grid.IsOccupied(cell) ? 1 : 0, 0, 0);

            world.SetBlock(world.Map, cell, type);
            before = world.Particles.Count;
            world.Damage(world.Map, cell, material.DamageToBreak(1.0) * 100, "overkill");
            ok &= Check($"{material.Name}: overkill particles", world.Particles.Count - before, 32, 32);

            world.SetBlock(world.Map, cell, type);
            world.Damage(world.Map, cell, material.DamageToBreak(1.0) * 0.6, "partial damage");
            world.SetBlock(world.Map, cell, type);
            before = world.Particles.Count;
            world.Damage(world.Map, cell, material.DamageToBreak(1.0) * 100, "replacement block");
            ok &= Check($"{material.Name}: replacement has a fresh budget", world.Particles.Count - before, 32, 32);
        }
        return ok;
    }

    /// <summary>
    /// Two stone blocks hang from a stone frame by one dirt block (67 kPa of its 80); nothing
    /// happens. A third stone block overloads the dirt by a quarter, which tears it within a
    /// second, and the stones fall as one object onto the stone floor.
    /// </summary>
    private static bool Hanging()
    {
        using World world = CreateWorld();
        Floor(world, 10);
        for (int z = 2; z <= 9; z++)
        {
            world.SetBlock(world.Map, new ivec3(2, 5, z), BlockType.Stone);
            world.SetBlock(world.Map, new ivec3(8, 5, z), BlockType.Stone);
        }

        for (int x = 3; x < 8; x++)
            world.SetBlock(world.Map, new ivec3(x, 5, 9), BlockType.Stone);

        world.SetBlock(world.Map, new ivec3(5, 5, 8), BlockType.Dirt);
        world.SetBlock(world.Map, new ivec3(5, 5, 7), BlockType.Stone);
        world.SetBlock(world.Map, new ivec3(5, 5, 6), BlockType.Stone);

        Run(world, 3.0);
        double ratio = world.Map.StressRatio(new ivec3(5, 5, 8), world.Map.Grid[new ivec3(5, 5, 8)]);
        bool ok = Check("dirt link load over strength", ratio, 0.78, 0.9);
        ok &= Check("objects while it holds", world.Objects.Count, 0, 0);

        world.SetBlock(world.Map, new ivec3(5, 5, 5), BlockType.Stone);
        double tear = Run(world, 8.0, () => world.Objects.Count > 0);
        ok &= Check("time for the dirt to tear (s)", tear, 0.0, 1.5);
        if (double.IsInfinity(tear))
            return false;

        VoxelObject fallen = world.Objects[0];
        ok &= Check("blocks in the fallen object", fallen.Grid.Count, 3, 3);
        Run(world, 1.5);
        // 7.8 t landing at 7.7 m/s presses the floor block under it at about 45 MPa in
        // compression, and shears it out at its sides: it may crack or break, but no more.
        int floorLost = 0;
        for (int x = 3; x <= 7; x++)
        for (int y = 3; y <= 7; y++)
            floorLost += world.Map.Grid.IsOccupied(new ivec3(x, y, 1)) ? 0 : 1;
        ok &= Check("floor blocks broken by the fall", floorLost, 0, 1);
        ok &= Check("fallen stones' height (m)", world.Objects.Max(o => o.Position.z), 0.9, 2.2);
        return ok;
    }

    /// <summary>
    /// A stone beam out of a stone tower: 3.75 MPa at its root at 7 blocks long, of the 6 MPa
    /// stone takes, holds; at 10 blocks (7.7 MPa) its root breaks within a couple of seconds
    /// and the beam drops.
    /// </summary>
    private static bool Cantilever()
    {
        using World world = CreateWorld();
        Floor(world, 16);
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
        for (int z = 2; z <= 9; z++)
            world.SetBlock(world.Map, new ivec3(x, y, z), BlockType.Stone);

        for (int x = 2; x < 9; x++)
            world.SetBlock(world.Map, new ivec3(x, 0, 9), BlockType.Stone);

        Run(world, 3.0);
        double ratio = world.Map.StressRatio(new ivec3(2, 0, 9), world.Map.Grid[new ivec3(2, 0, 9)]);
        bool ok = Check("root load over strength at 7 blocks", ratio, 0.55, 0.75);
        ok &= Check("objects while it holds", world.Objects.Count, 0, 0);

        for (int x = 9; x < 12; x++)
            world.SetBlock(world.Map, new ivec3(x, 0, 9), BlockType.Stone);

        double broke = Run(world, 15.0, () => world.Objects.Count > 0);
        ok &= Check("time for the beam to break off (s)", broke, 0.0, 2.5);
        if (!double.IsInfinity(broke))
            ok &= Check("blocks that fell", world.Objects.Sum(o => o.Grid.Count), 8, 10);
        return ok;
    }

    /// <summary>
    /// A table: a stone top on three legs of leaves, three high, each block of a leg as loaded
    /// as the next. Stones put over one end crush that leg; the table then tips over the
    /// middle leg, levering the far one in tension, and falls. Each leg gives at one block,
    /// where the load localizes, rather than vanishing whole: once a block breaks the loads
    /// take new paths, and only an analysis of the table as it now is counts.
    /// </summary>
    private static bool TableLegs()
    {
        using World world = CreateWorld();
        Floor(world, 12);
        int[] legs = [3, 6, 9];
        foreach (int x in legs)
        {
            for (int z = 2; z <= 4; z++)
                world.SetBlock(world.Map, new ivec3(x, 5, z), BlockType.Leaves);
        }

        for (int x = 3; x <= 9; x++)
            world.SetBlock(world.Map, new ivec3(x, 5, 5), BlockType.Stone);

        Run(world, 1.5);
        bool ok = Check("objects while it stands", world.Objects.Count, 0, 0);

        // Until the table falls off: what is left of a leg can be crushed by the table landing on it.
        Dictionary<int, int> crushed = legs.ToDictionary(x => x, _ => 0);
        world.BlockBroken += (body, cell, voxel, cause) =>
        {
            if (body is VoxelMap && voxel.Type == BlockType.Leaves && cause.StartsWith("stress") && world.Objects.Count == 0)
                crushed[cell.x]++;
        };

        for (int z = 6; z <= 8; z++)
            world.SetBlock(world.Map, new ivec3(9, 5, z), BlockType.Stone);

        double failed = Run(world, 4.0, () => crushed[9] > 0);
        Run(world, 2.0);
        ok &= Check("time for the loaded leg to give (s)", failed, 0.0, 2.0);
        ok &= Check("blocks the load broke in the loaded leg", crushed[9], 1, 1);
        ok &= Check("most blocks the load broke in a leg", crushed.Values.Max(), 1, 1);
        return ok;
    }

    /// <summary>Two cubes joined by one block; breaking it leaves two objects that keep the motion they had.</summary>
    private static bool Split()
    {
        using World world = CreateWorld();
        Floor(world, 4);
        List<(ivec3, BlockType)> blocks = [.. TerrainGenerator.Box(new ivec3(2, 2, 2), BlockType.Stone)];
        blocks.AddRange(TerrainGenerator.Box(new ivec3(2, 2, 2), BlockType.Stone).Select(b => (b.Cell + new ivec3(3, 0, 0), b.Type)));
        blocks.Add((new ivec3(2, 0, 0), BlockType.Stone));

        VoxelObject dumbbell = world.Spawn(blocks, new dvec3(0, 0, 40), dquat.Identity, new dvec3(1, 0, 0), new dvec3(0, 0, 2));
        world.StepOnce();
        world.RemoveBlock(dumbbell, new ivec3(2, 0, 0));
        world.ApplyTopologyChanges();

        bool ok = Check("objects after the cut", world.Objects.Count, 2, 2);
        ok &= Check("blocks in each", world.Objects.Min(o => o.Grid.Count), 8, 8);

        // Rigid motion: each half's center moves at v + ω × r, apart in opposite directions.
        dvec3 v0 = world.Objects[0].LinearVelocity;
        dvec3 v1 = world.Objects[1].LinearVelocity;
        ok &= Check("halves' relative speed (m/s)", (v0 - v1).swizzle.xy.Length, 2.0 * 2.0 * 1.5 * 0.9, 2.0 * 2.0 * 1.5 * 1.1);
        ok &= Check("halves keep the spin (rad/s)", world.Objects[1].AngularVelocity.z, 1.99, 2.01);
        return ok;
    }

    /// <summary>Cutting a pillar leaves its top hanging in the air: it falls off as an object.</summary>
    private static bool Detach()
    {
        using World world = CreateWorld();
        Floor(world, 8);
        for (int z = 2; z <= 6; z++)
            world.SetBlock(world.Map, new ivec3(3, 3, z), BlockType.Stone);
        world.SetBlock(world.Map, new ivec3(4, 3, 6), BlockType.Stone);
        world.ApplyTopologyChanges();

        world.RemoveBlock(world.Map, new ivec3(3, 3, 3));
        world.ApplyTopologyChanges();
        bool ok = Check("objects", world.Objects.Count, 1, 1);
        ok &= Check("blocks in it", world.Objects.Sum(o => o.Grid.Count), 4, 4);
        ok &= Check("map blocks left above the cut", world.Map.Grid.IsOccupied(new ivec3(3, 3, 4)) ? 1 : 0, 0, 0);

        Run(world, 1.5);
        ok &= Check("it fell onto the stump (m)", world.Objects[0].Position.z, 2.9, 3.2);
        return ok;
    }

    /// <summary>
    /// A glass block dropped 6 m onto stone presses 66 MPa (glass is good for 15): the glass
    /// cracks, the stone (80) does not. Dropped 0.1 m it is unharmed.
    /// </summary>
    private static bool Impact()
    {
        using World world = CreateWorld();
        Floor(world, 8);
        VoxelObject high = world.Spawn([(ivec3.Zero, BlockType.Glass)], new dvec3(2, 2, 8), dquat.Identity);
        VoxelObject low = world.Spawn([(ivec3.Zero, BlockType.Glass)], new dvec3(5, 5, 2.1), dquat.Identity);
        Run(world, 2.0);

        double highDamage = high.Grid.Count == 0 ? 1.0 : high.Grid[ivec3.Zero].DamageFraction;
        bool ok = Check("glass dropped 6 m: damage", highDamage, 0.3, 1.0);
        ok &= Check("glass dropped 0.1 m: damage", low.Grid[ivec3.Zero].DamageFraction, 0, 0);
        ok &= Check("stone floor under it: damage", world.Map.Grid[new ivec3(2, 2, 1)].DamageFraction, 0, 0);
        return ok;
    }

    /// <summary>A plank bridge of span 10 and width 3 on stone piers, its deck at height 6.</summary>
    private static void Bridge(World world)
    {
        for (int y = 4; y < 7; y++)
        {
            for (int z = 2; z <= 6; z++)
            {
                world.SetBlock(world.Map, new ivec3(2, y, z), BlockType.Stone);
                world.SetBlock(world.Map, new ivec3(13, y, z), BlockType.Stone);
            }

            for (int x = 3; x < 13; x++)
                world.SetBlock(world.Map, new ivec3(x, y, 6), BlockType.OakPlanks);
        }
    }

    private static int DeckLeft(World world)
    {
        int count = 0;
        for (int x = 3; x < 13; x++)
        for (int y = 4; y < 7; y++)
            count += world.Map.Grid[new ivec3(x, y, 6)].Type == BlockType.OakPlanks ? 1 : 0;
        return count;
    }

    /// <summary>
    /// A 3×3×3 stone cube (70 t) dropped 8 m onto a plank bridge. The impact bends the deck far
    /// past what planks take, so it snaps as the cube lands, not seconds later, and the cube
    /// carries on down with most of its momentum instead of stopping on the broken deck.
    /// </summary>
    private static bool SmashBridge()
    {
        using World world = CreateWorld();
        Floor(world, 20);
        Bridge(world);
        Run(world, 1.0);
        bool ok = Check("deck intact before", DeckLeft(world), 30, 30);

        VoxelObject cube = world.Spawn(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.Stone), new dvec3(6.5, 4, 15), dquat.Identity);
        double landing = Math.Sqrt(2.0 * 8.0 / 9.81);
        double broke = Run(world, 3.0, () => DeckLeft(world) < 30);
        ok &= Check("time from landing to the deck breaking (s)", broke - landing, 0.0, 0.2);

        Run(world, 0.4);
        // Pieces of the deck fall with it and can bump it on the way.
        ok &= Check("cube's speed 0.4 s later, through the deck (m/s)", -cube.LinearVelocity.z, 3.5, 20.0);
        Run(world, 1.5);
        ok &= Check("planks left in the deck", DeckLeft(world), 0, 24);
        ok &= Check("cube's height, below the deck (m)", cube.Position.z, 1.0, 5.0);
        return ok;
    }

    /// <summary>
    /// The same 0.5 m drop onto the tip of a 5-block stone cantilever: a plank block (0.7 t)
    /// leaves it standing, a 3×3×3 stone cube (70 t) snaps it at the root. Mass sets the
    /// impact's energy, and so how hard it pushes.
    /// </summary>
    private static bool MassMatters()
    {
        (bool Broke, double RootHealth) Drop(IEnumerable<(ivec3, BlockType)> blocks, dvec3 position)
        {
            using World world = CreateWorld();
            Floor(world, 16);
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 2; z <= 7; z++)
                world.SetBlock(world.Map, new ivec3(x, y, z), BlockType.Stone);
            for (int x = 2; x <= 6; x++)
                world.SetBlock(world.Map, new ivec3(x, 0, 7), BlockType.Stone);

            Run(world, 2.0);
            world.Spawn(blocks, position, dquat.Identity);
            Run(world, 1.5);
            ivec3 root = new(2, 0, 7);
            return (!world.Map.Grid.IsOccupied(new ivec3(6, 0, 7)), world.Map.Grid.IsOccupied(root) ? world.Map.Grid[root].Health : 0.0);
        }

        (bool lightBroke, double lightRoot) = Drop([(ivec3.Zero, BlockType.OakPlanks)], new dvec3(6, 0, 8.5));
        bool ok = Check("beam lost to a plank block", lightBroke ? 1 : 0, 0, 0);
        ok &= Check("root's health after it", lightRoot, 0.4, 1.0);

        (bool heavyBroke, _) = Drop(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.Stone), new dvec3(5, -1, 8.5));
        ok &= Check("beam lost to a stone cube", heavyBroke ? 1 : 0, 1, 1);
        return ok;
    }

    /// <summary>
    /// A stone block thrown at 22 m/s at a glass window in a stone wall: the glass it hits
    /// breaks and the block flies on through, slowed only by the little energy breaking glass
    /// takes.
    /// </summary>
    private static bool ThroughGlass()
    {
        using World world = CreateWorld();
        Floor(world, 24);
        for (int y = 0; y <= 10; y++)
        for (int z = 2; z <= 8; z++)
        {
            bool window = y >= 4 && y <= 6 && z >= 3 && z <= 5;
            world.SetBlock(world.Map, new ivec3(12, y, z), window ? BlockType.Glass : BlockType.Stone);
        }

        Run(world, 1.0);
        // Aimed between two columns of the window, so the hole it makes is wider than the block,
        // and high in it: stopped against the glass, the block falls on for the few steps the
        // impact takes to analyse, then dives through the hole with the downward speed it gets
        // back, clear of the sill.
        VoxelObject thrown = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(5, 5.5, 4.0), dquat.Identity, new dvec3(22, 0, 0));
        double through = Run(world, 1.0, () => thrown.Position.x > 13.2);
        bool ok = Check("time to get through the window (s)", through, 0.3, 0.6);
        ok &= Check("speed beyond it (m/s)", thrown.LinearVelocity.x, 15.0, 22.0);

        int glass = 0;
        for (int y = 4; y <= 6; y++)
        for (int z = 3; z <= 5; z++)
            glass += world.Map.Grid.IsOccupied(new ivec3(12, y, z)) ? 1 : 0;
        ok &= Check("glass blocks broken", 9 - glass, 1, 9);
        int stone = 0;
        for (int y = 0; y <= 10; y++)
        for (int z = 2; z <= 8; z++)
            stone += world.Map.Grid[new ivec3(12, y, z)].Type == BlockType.Stone ? 1 : 0;
        ok &= Check("stone blocks of the wall broken", 68 - stone, 0, 2);
        ok &= Check("the thrown block's health", thrown.Grid.Count == 1 ? thrown.Grid[ivec3.Zero].Health : 0.0, 0.9, 1.0);
        return ok;
    }

    /// <summary>
    /// Soil has to be crushed out of the way to break, so it takes impacts: a 70 t stone cube
    /// dropped 2 m onto grass dents it, and a glass block thrown down at it bounces off.
    /// </summary>
    private static bool SoftGround()
    {
        using World world = CreateWorld();
        for (int x = 0; x < 20; x++)
        for (int y = 0; y < 20; y++)
        {
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
            for (int z = 1; z <= 5; z++)
                world.SetBlock(world.Map, new ivec3(x, y, z), z == 5 ? BlockType.Grass : BlockType.Dirt);
        }

        int broken = 0;
        world.BlockBroken += (body, _, _, _) => broken += body is VoxelMap ? 1 : 0;
        Run(world, 0.5);
        VoxelObject cube = world.Spawn(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.Stone), new dvec3(8, 8, 8), dquat.Identity);
        world.Spawn([(ivec3.Zero, BlockType.Glass)], new dvec3(2, 2, 9), dquat.Identity, new dvec3(10, 0, -15), new dvec3(1.5, 0.7, 0.3));
        Run(world, 3.0);

        bool ok = Check("ground blocks broken", broken, 0, 0);
        ok &= Check("cube resting on the ground (m)", cube.Position.z, 5.9, 6.1);
        return ok;
    }

    /// <summary>A block placed on a sliding object joins it: one heavier object that keeps sliding.</summary>
    private static bool BuildOnObject()
    {
        using World world = CreateWorld();
        Floor(world, 30);
        VoxelObject crate = world.Spawn(TerrainGenerator.Box(new ivec3(2, 2, 2), BlockType.OakPlanks), new dvec3(4, 10, 2.02), dquat.Identity, new dvec3(3, 0, 0));
        Run(world, 0.2);
        double speedBefore = crate.LinearVelocity.x;
        double massBefore = crate.Body!.Mass;

        world.SetBlock(crate, new ivec3(0, 0, 2), BlockType.Stone);
        world.ApplyTopologyChanges();
        bool ok = Check("objects", world.Objects.Count, 1, 1);
        ok &= Check("blocks in it", crate.Grid.Count, 9, 9);
        ok &= Check("mass gained (kg)", crate.Body!.Mass - massBefore, 2599, 2601);
        ok &= Check("speed kept (m/s)", crate.LinearVelocity.x, speedBefore - 0.3, speedBefore + 0.3);
        return ok;
    }

    /// <summary>An object longer than a chunk is stored, meshed and simulated across chunks.</summary>
    private static bool BigObject()
    {
        using World world = CreateWorld();
        Floor(world, 30);
        VoxelObject beam = world.Spawn(TerrainGenerator.Box(new ivec3(20, 2, 1), BlockType.OakPlanks), new dvec3(4, 10, 6), dquat.Identity);
        world.ApplyTopologyChanges();
        bool ok = Check("chunks the object spans", beam.Grid.Chunks.Count, 2, 2);
        // Faces on chunk borders also mark the empty chunks across them, which remesh to nothing.
        ok &= Check("render chunks waiting, both occupied ones among them", beam.Grid.Chunks.All(c => beam.MeshDirty.Contains(c.Coordinate)) ? 1 : 0, 1, 1);

        Run(world, 2.0);
        ok &= Check("rests on the floor (m)", beam.Position.z, 1.95, 2.05);
        ok &= Check("blocks lost landing", 40 - beam.Grid.Count, 0, 0);
        return ok;
    }

    /// <summary>The generated world: how long building and editing take, and that it stands up on its own.</summary>
    private static bool Terrain()
    {
        using World world = CreateWorld();
        Stopwatch watch = Stopwatch.StartNew();
        TerrainGenerator.Generate(world);
        double generate = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        world.StepOnce();
        double firstStep = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"   generate {generate:F0} ms, first step (builds everything) {firstStep:F0} ms, {world.Map.Grid.Count} map blocks, {world.Physics.FixedBodies.Count} chunk colliders");

        Run(world, 1.0, () => world.Map.Stress?.Version == world.Map.TopologyVersion);
        Run(world, 2.0, () => world.Map.Stress?.Version == world.Map.TopologyVersion);
        if (world.Map.Stress != null)
            PrintMostLoaded(world.Map, 8);

        List<double> edits = [];
        for (int k = 0; k < 5; k++)
        {
            ivec3 cell = new(10 + k * 3, 10, TerrainGenerator.GroundHeight(10 + k * 3, 10));
            world.RemoveBlock(world.Map, cell);
            watch.Restart();
            world.StepOnce();
            edits.Add(watch.Elapsed.TotalMilliseconds);
        }

        watch.Restart();
        for (int k = 0; k < 30; k++)
            world.StepOnce();
        double plain = watch.Elapsed.TotalMilliseconds / 30;
        Console.WriteLine($"   step with one edit {edits.Average():F1} ms (max {edits.Max():F1}), plain step {plain:F1} ms, contacts {world.Physics.Statistics.ContactCount}");

        int objectsBefore = world.Objects.Count;
        bool blockBroke = false;
        world.BlockBroken += (_, _, _, _) => blockBroke = true;
        Run(world, 6.0);
        Console.WriteLine($"   map stress solve {world.Statistics.StressMilliseconds:F0} ms, {world.Statistics.StressIterations} iterations; most loaded map block at {MaxRatio(world.Map):F2} of its strength");

        List<(ivec3 Cell, Voxel Voxel)> damaged = [.. world.Map.Grid.Occupied().Where(c => c.Value.Damage > 0)];
        foreach ((ivec3 cell, Voxel voxel) in damaged.Take(5))
            Console.WriteLine($"      damaged: {voxel.Material.Name} at {cell}, {voxel.DamageFraction:P0}");

        // Branches carry the leaves and roots the trees, which once stood near their limits.
        double leaves = world.Map.Grid.Occupied().Where(c => c.Value.Type == BlockType.Leaves).Max(c => world.Map.StressRatio(c.Cell, c.Value));
        double trunks = world.Map.Grid.Occupied().Where(c => c.Value.Type == BlockType.OakLog).Max(c => world.Map.StressRatio(c.Cell, c.Value));
        bool ok = Check("most loaded leaves, of their elastic limit", leaves, 0, 0.5);
        ok &= Check("most loaded log, of its elastic limit", trunks, 0, 0.5);
        ok &= Check("blocks broken standing still", blockBroke ? 1 : 0, 0, 0);
        ok &= Check("blocks damaged apart from the five dug", damaged.Count, 0, 0);
        ok &= Check("objects that fell off", world.Objects.Count - objectsBefore, 0, 0);
        ok &= Check("map analysed", world.Map.Stress?.Version == world.Map.TopologyVersion ? 1 : 0, 1, 1);
        ok &= Check("step with one edit (ms)", edits.Average(), 0, 60);
        return ok;
    }

    /// <summary>A walking player put down on the map: it lands on its bottom face and stands still, unhurt.</summary>
    private static bool PlayerStands()
    {
        using World world = CreateWorld();
        Floor(world, 8);
        Player player = world.SpawnPlayer(new dvec3(4, 4, 2.5));
        player.Flying = false;
        Run(world, 2.0);

        bool ok = Check("feet on the floor (m)", player.Feet.z, 1.995, 2.005);
        ok &= Check("standing", player.Grounded ? 1 : 0, 1, 1);
        ok &= Check("speed (m/s)", player.Body.LinearVelocity.Length, 0, 0.02);
        ok &= Check("turned (rad)", 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(player.Orientation.w))), 0, 1e-6);
        ok &= Check("health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// On stone the legs take the player to walking speed in about an eighth of a second, stop
    /// it when let go, and a jump lifts its feet 1.6 m under the player's threefold gravity,
    /// once however long it is held.
    /// </summary>
    private static bool PlayerWalks()
    {
        using World world = CreateWorld();
        Floor(world, 30);
        Player player = world.SpawnPlayer(new dvec3(5, 15, 2));
        player.Flying = false;
        Run(world, 0.5);

        player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
        double reached = Run(world, 1.0, () => player.Body.LinearVelocity.x > 0.95 * Player.WalkSpeed);
        Run(world, 0.5);
        bool ok = Check("time to walking speed (s)", reached, 0.08, 0.16);
        ok &= Check("walking speed (m/s)", player.Body.LinearVelocity.x, 0.98 * Player.WalkSpeed, 1.02 * Player.WalkSpeed);

        player.Controls = default;
        Run(world, 0.5);
        ok &= Check("speed after letting go (m/s)", player.Body.LinearVelocity.Length, 0, 0.05);

        double highest = 0.0;
        player.JumpPressed = true;
        Run(world, 0.1);
        player.Controls = default;
        Run(world, 1.5, () =>
        {
            highest = Math.Max(highest, player.Feet.z - 2.0);
            return false;
        });
        ok &= Check("jump height (m)", highest, 1.45, 1.75);
        player.JumpPressed = true;
        Run(world, 1.0);
        ok &= Check("feet a second after another jump (m)", player.Feet.z, 1.99, 2.01);
        ok &= Check("landed", player.Grounded ? 1 : 0, 1, 1);
        ok &= Check("health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// The ground's friction sets how fast the player gets going and stops, not how fast it
    /// walks: on leaves (friction 0.3) it takes 7/3 as long as on stone (0.7) to reach walking
    /// speed and slides 7/3 as far when let go, and walks as fast. In the air there is no
    /// ground to push on, only a little drag: thrown at 30 m/s, the player keeps about 28 after
    /// half a second.
    /// </summary>
    private static bool PlayerGrip()
    {
        (double Reached, double Speed, double Slide) Walk(BlockType ground)
        {
            using World world = CreateWorld();
            for (int x = 0; x < 30; x++)
            for (int y = 0; y < 30; y++)
            {
                world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
                world.SetBlock(world.Map, new ivec3(x, y, 1), ground);
            }

            Player player = world.SpawnPlayer(new dvec3(5, 15, 2));
            player.Flying = false;
            Run(world, 0.5);

            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            double reached = Run(world, 1.0, () => player.Body.LinearVelocity.x > 0.95 * Player.WalkSpeed);
            Run(world, 0.5);
            double speed = player.Body.LinearVelocity.x;
            double letGo = player.Feet.x;
            player.Controls = default;
            Run(world, 1.0);
            Console.WriteLine($"      {Blocks.Get(ground).Name}: walking speed in {reached:F3} s, slid {player.Feet.x - letGo:F3} m");
            return (reached, speed, player.Feet.x - letGo);
        }

        (double stoneReached, double stoneSpeed, double stoneSlide) = Walk(BlockType.Stone);
        (double leavesReached, double leavesSpeed, double leavesSlide) = Walk(BlockType.Leaves);
        bool ok = Check("walking speed on leaves, of on stone", leavesSpeed / stoneSpeed, 0.98, 1.02);
        ok &= Check("time to walking speed on leaves, of on stone", leavesReached / stoneReached, 1.9, 2.8);
        ok &= Check("slide on leaves, of on stone", leavesSlide / stoneSlide, 1.9, 2.8);

        using World world = CreateWorld();
        Floor(world, 4);
        Player thrown = world.SpawnPlayer(new dvec3(2, 2, 60));
        thrown.Flying = false;
        thrown.Body.LinearVelocity = new dvec3(30, 0, 0);
        Run(world, 0.5);
        ok &= Check("speed thrown at 30 m/s, half a second later in the air (m/s)", thrown.Body.LinearVelocity.x, 27.0, 28.5);
        return ok;
    }

    /// <summary>
    /// Newton's third law: on a 4.8 t raft of leaves sliding freely on the floor, the push that
    /// gets the player walking pushes the raft the other way, with as much momentum.
    /// </summary>
    private static bool PlayerPushesBack()
    {
        using World world = CreateWorld();
        Floor(world, 40);
        VoxelObject raft = world.Spawn(TerrainGenerator.Box(new ivec3(8, 3, 1), BlockType.Leaves), new dvec3(10, 18, 2.0), dquat.Identity);
        world.ApplyTopologyChanges();
        foreach (FixedBody collider in world.Map.Colliders.Values)
        {
            collider.SurfaceMaterials = null;
            collider.Friction = 0.0;
        }

        Player player = world.SpawnPlayer(new dvec3(10.8, 19.5, 3.05));
        player.Flying = false;
        Run(world, 1.0);
        bool ok = Check("standing on the raft", player.Grounded ? 1 : 0, 1, 1);

        player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
        Run(world, 0.6);
        double playerMomentum = Player.Mass * player.Body.LinearVelocity.x;
        double raftMomentum = raft.Body!.Mass * raft.LinearVelocity.x;
        ok &= Check("the player's momentum (kg m/s)", playerMomentum, 300, 400);
        ok &= Check("the raft's, over the player's", raftMomentum / playerMomentum, -1.05, -0.95);
        ok &= Check("the raft's turn (rad/s)", raft.AngularVelocity.Length, 0, 0.05);
        return ok;
    }

    /// <summary>
    /// Falls hurt as impacts on a block do: soaked up unharmed up to about 4.5 m under the
    /// player's threefold gravity, worse the higher above that, on stone and grass alike,
    /// since a body is far weaker than either. With the increased damage capacity, a 40 m
    /// fall is survivable. An invulnerable player is unhurt.
    /// </summary>
    private static bool PlayerFalls()
    {
        double Drop(BlockType ground, double height, bool invulnerable = false)
        {
            using World world = CreateWorld();
            for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
                for (int z = 1; z <= 3; z++)
                    world.SetBlock(world.Map, new ivec3(x, y, z), z == 3 ? ground : BlockType.Dirt);
            }

            Player player = world.SpawnPlayer(new dvec3(4, 4, 4 + height));
            player.Flying = false;
            player.Invulnerable = invulnerable;
            Run(world, Math.Sqrt(2.0 * height / 9.81) + 0.5);
            Console.WriteLine($"      {Blocks.Get(ground).Name}, {height} m: health {player.Health:P0}, ground damage {world.Map.Grid.Occupied().Max(c => c.Value.DamageFraction):P0}");
            return player.Health;
        }

        bool ok = Check("health after 3 m onto stone", Drop(BlockType.Stone, 3), 1, 1);
        ok &= Check("health after 6 m onto stone", Drop(BlockType.Stone, 6), 0.92, 0.999);
        ok &= Check("health after 12 m onto stone", Drop(BlockType.Stone, 12), 0.82, 0.99);
        ok &= Check("health after 40 m onto stone", Drop(BlockType.Stone, 40), 0.01, 0.95);
        ok &= Check("health after 3 m onto grass", Drop(BlockType.Grass, 3), 1, 1);
        ok &= Check("health after 12 m onto grass", Drop(BlockType.Grass, 12), 0.82, 0.99);
        ok &= Check("health after 40 m onto grass", Drop(BlockType.Grass, 40), 0.01, 0.95);
        ok &= Check("health after 40 m, invulnerable", Drop(BlockType.Stone, 40, invulnerable: true), 1, 1);
        return ok;
    }

    /// <summary>
    /// A stone block resting on the player's head (25 kN, a third of what it endures) is
    /// carried; a 3×3×3 stone cube dropped on it crushes it. Either way the player stays
    /// upright and holds up what lands on it, as a block would.
    /// </summary>
    private static bool PlayerCrushed()
    {
        using World world = CreateWorld();
        Floor(world, 12);
        Player player = world.SpawnPlayer(new dvec3(6, 6, 2));
        player.Flying = false;
        Run(world, 0.5);

        VoxelObject block = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(5.5, 5.5, 3.82), dquat.Identity);
        Run(world, 2.0);
        bool ok = Check("block resting on the head (m)", block.Position.z, 3.79, 3.81);
        ok &= Check("load on the player, of its elastic limit", player.Stresses.Max(Player.Material.ElasticLimit(player.Health).Ratio), 0.2, 0.6);
        ok &= Check("health under it", player.Health, 1, 1);

        world.Spawn(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.Stone), new dvec3(4.5, 4.5, 8), dquat.Identity);
        Run(world, 2.0);
        ok &= Check("health under the cube", player.Health, 0, 0);
        return ok;
    }

    /// <summary>
    /// Objects no longer pass through the player: a plank block (0.7 t) thrown at it as the
    /// player throws blocks, 22 m/s, knocks it along and hurts it. The impact takes only the
    /// energy a collision of the two can, ½·μ·v² = 17 kJ, not the block's 170. They would share
    /// 19.7 m/s; at the solver's 10 iterations the light player gets only part of them.
    /// </summary>
    private static bool PlayerHit()
    {
        using World world = CreateWorld();
        Floor(world, 30);
        Player player = world.SpawnPlayer(new dvec3(10, 10, 2));
        player.Flying = false;
        Run(world, 0.5);

        VoxelObject block = world.Spawn([(ivec3.Zero, BlockType.OakPlanks)], new dvec3(6, 9.5, 2.75), dquat.Identity, new dvec3(22, 0, 0));
        double hit = Run(world, 1.0, () => player.Body.LinearVelocity.x > 1.0);
        double fastest = 0.0;
        Run(world, 0.1, () =>
        {
            fastest = Math.Max(fastest, player.Body.LinearVelocity.x);
            return false;
        });
        bool ok = Check("time until it is hit (s)", hit, 0.05, 0.25);
        ok &= Check("its speed after (m/s)", fastest, 8.0, 22.0);
        Run(world, 1.0);
        ok &= Check("health", player.Health, 0.5, 0.99);
        ok &= Check("block lost", block.Grid.Count == 0 ? 1 : 0, 0, 0);
        return ok;
    }

    /// <summary>
    /// A fall of 7 m onto a roof of leaves breaks it, as a falling block would: the impact
    /// tears the leaves where they are weakest, and the player goes on through to the floor.
    /// Under its threefold gravity it reaches the roof in 0.7 s; the roof tears where it is held
    /// at the walls, and its middle falls with the player on it.
    /// </summary>
    private static bool PlayerSmash()
    {
        using World world = CreateWorld();
        Floor(world, 12);
        for (int x = 2; x <= 8; x++)
        for (int y = 2; y <= 8; y++)
        {
            bool wall = x == 2 || x == 8 || y == 2 || y == 8;
            for (int z = 2; z <= 5 && wall; z++)
                world.SetBlock(world.Map, new ivec3(x, y, z), BlockType.Stone);
            world.SetBlock(world.Map, new ivec3(x, y, 6), wall ? BlockType.Stone : BlockType.Leaves);
        }

        int leavesBroken = 0;
        world.BlockBroken += (body, _, voxel, cause) => leavesBroken += body is VoxelMap && voxel.Type == BlockType.Leaves && cause.StartsWith("an impact") ? 1 : 0;
        Run(world, 1.0);
        bool ok = Check("leaves broken by their own weight", leavesBroken, 0, 0);

        Player player = world.SpawnPlayer(new dvec3(5.5, 5.5, 14));
        player.Flying = false;
        double through = Run(world, 3.0, () => player.Feet.z < 5.0);
        Run(world, 1.5);
        ok &= Check("roof leaves the impact broke", leavesBroken, 1, 25);
        ok &= Check("time to get through the roof (s)", through, 0.85, 2.5);
        ok &= Check("feet at the end (m)", player.Feet.z, 1.95, 3.5);
        ok &= Check("health", player.Health, 0.5, 1.0);
        return ok;
    }

    /// <summary>
    /// Nothing catches the player where its box meets the edges of the blocks it walks on: it
    /// walks along a block's top edge with its box hanging over it, steps back onto the block,
    /// and slides along the side of a step it walks into at an angle, on past the step's end.
    /// A block's top edge lies in the plane of the box's bottom, and so do the floor's corners
    /// at the foot of the step; they must not act as walls against the box's sides. Sprinting
    /// into the step stops the player without hurting it.
    /// </summary>
    private static bool PlayerLedges()
    {
        using World world = CreateWorld();
        Floor(world, 16);
        for (int x = 4; x <= 8; x++)
        for (int y = 2; y <= 10; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);

        Player player = world.SpawnPlayer(new dvec3(8.95, 3.0, 3.0));
        player.Flying = false;
        Run(world, 0.5);

        double Walk(dvec3 direction, double seconds)
        {
            dvec3 start = player.Feet;
            player.Controls = new PlayerControls(direction, Sprint: false);
            Run(world, seconds);
            player.Controls = default;
            Run(world, 0.3);
            return dvec3.Dot(player.Feet - start, direction);
        }

        bool ok = Check("along the top edge, hanging over it (m)", Walk(new dvec3(0, 1, 0), 1.0), 4.0, 5.0);
        ok &= Check("still on top (m)", player.Feet.z, 2.99, 3.01);
        ok &= Check("back onto the block (m)", Walk(new dvec3(-1, 0, 0), 0.5), 2.0, 3.0);

        player.MoveTo(new dvec3(9.32, 3.0, 2.0));
        Run(world, 0.5);
        dvec3 into = new dvec3(-0.6, 0.8, 0);
        dvec3 before = player.Feet;
        Walk(into, 1.0);
        ok &= Check("along the step's side, walking into it (m)", player.Feet.y - before.y, 2.8, 4.0);

        player.MoveTo(new dvec3(9.32, 9.0, 2.0));
        Run(world, 0.5);
        Walk(into, 1.2);
        ok &= Check("on past the step's end (m)", player.Feet.y, 11.5, 14.0);

        player.MoveTo(new dvec3(13.0, 6.0, 2.0));
        Run(world, 0.5);
        player.Controls = new PlayerControls(new dvec3(-Player.SprintFactor, 0, 0), Sprint: true);
        double fastest = 0.0;
        Run(world, 1.5, () =>
        {
            fastest = Math.Max(fastest, -player.Body.LinearVelocity.x);
            return false;
        });
        player.Controls = default;
        Run(world, 0.5);
        ok &= Check("sprinting speed into the step (m/s)", fastest, 6.5, 6.8);
        ok &= Check("stopped against it (m)", player.Feet.x, 9.29, 9.32);
        ok &= Check("health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// The player walks across the generated world in sixteen directions. It may stop at a
    /// step it would have to jump or against an object, but never with nothing in its way: its
    /// box, nudged 5 cm on along its way and lifted clear of the ground, must overlap a block.
    /// </summary>
    private static bool PlayerTerrain()
    {
        using World world = CreateWorld();
        TerrainGenerator.Generate(world);
        world.ApplyTopologyChanges();
        Player player = world.SpawnPlayer(TerrainGenerator.SpawnPoint);
        player.Flying = false;
        Run(world, 1.0);

        bool InTheWay(dvec3 direction)
        {
            dvec3 low = player.Position + 0.05 * direction + new dvec3(0, 0, 0.01);
            dvec3 high = low + new dvec3(Player.Width, Player.Width, Player.Height - 0.01);
            for (int x = (int)Math.Floor(low.x); x <= (int)Math.Floor(high.x); x++)
            for (int y = (int)Math.Floor(low.y); y <= (int)Math.Floor(high.y); y++)
            for (int z = (int)Math.Floor(low.z); z <= (int)Math.Floor(high.z); z++)
            {
                if (world.Map.Grid.IsOccupied(new ivec3(x, y, z)))
                    return true;
            }

            // Objects by the boxes around their cells, which a slight turn barely widens.
            foreach (VoxelObject body in world.Objects)
            {
                foreach (ivec3 cell in body.OccupiedCells())
                {
                    dvec3 center = body.ToWorld(VoxelBody.CellCenter(cell));
                    if (player.Overlaps(center - 0.55 - 0.05 * direction, center + 0.55 - 0.05 * direction))
                        return true;
                }
            }

            return false;
        }

        int caught = 0;
        double total = 0.0;
        for (int d = 0; d < 16; d++)
        {
            double yaw = d * Math.PI / 8;
            dvec3 direction = new(Math.Cos(yaw), Math.Sin(yaw), 0);
            world.RespawnPlayer();
            Run(world, 0.6);
            player.Controls = new PlayerControls(direction, Sprint: false);
            dvec3 start = player.Feet;
            int slow = 0;
            for (int step = 0; step < 240; step++)
            {
                world.StepOnce();
                world.ApplyTopologyChanges();
                slow = step > 20 && dvec3.Dot(player.Body.LinearVelocity, direction) < 1.0 ? slow + 1 : 0;
                if (slow == 10 && !InTheWay(direction))
                {
                    caught++;
                    Console.WriteLine($"   caught on nothing walking {direction:F2} at {player.Feet:F2}");
                }
            }

            player.Controls = default;
            total += (player.Feet - start).swizzle.xy.Length;
        }

        Console.WriteLine($"   walked {total:F0} m in all");
        bool ok = Check("times caught on nothing", caught, 0, 0);
        ok &= Check("health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// The player steps up onto what it walks into when that is low enough, as onto a stair:
    /// walking across a plank slab that leans from the floor onto a ledge, about 10°, into a
    /// two-block-high wall alongside it, it steps onto the wall where the wall stands 0.4 m
    /// above the slab, and is stopped where it stands 0.8 m above.
    /// </summary>
    private static bool PlayerSteps()
    {
        using World world = CreateWorld();
        Floor(world, 24);
        for (int x = 14; x < 20; x++)
        for (int y = 4; y < 14; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);
        for (int x = 4; x < 14; x++)
        for (int y = 11; y <= 13; y++)
        for (int z = 2; z <= 3; z++)
            world.SetBlock(world.Map, new ivec3(x, y, z), BlockType.Stone);

        VoxelObject slab = world.Spawn(TerrainGenerator.Box(new ivec3(6, 4, 1), BlockType.OakPlanks), new dvec3(8.5, 6.9, 2.2), dquat.FromAxisAngle(-0.18, new dvec3(0, 1, 0)));
        Run(world, 2.0);

        double Step(double along)
        {
            dvec3 top = slab.ToWorld(new dvec3(along, 3.5, 1.0));
            Player player = world.Player ?? world.SpawnPlayer(top);
            player.Flying = false;
            player.MoveTo(top + new dvec3(0, 0, 0.02));
            Run(world, 0.5);
            double below = 4.0 - player.Feet.z;
            player.Controls = new PlayerControls(new dvec3(0, 1, 0), Sprint: false);
            Run(world, 0.4);
            player.Controls = default;
            Run(world, 0.3);
            Console.WriteLine($"      the wall's top {below:F2} m above the feet: feet now at {player.Feet:F2}");
            return player.Feet.z;
        }

        bool ok = Check("onto the wall 0.4 m up (m)", Step(3.6), 3.99, 4.02);
        ok &= Check("stopped by it 0.8 m up (m)", Step(1.2), 3.0, 3.4);
        ok &= Check("health", world.Player!.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// Walking into a loose block pushes it along: a block of planks (0.7 t) as fast as the
    /// player walks, one of stone (2.6 t), which takes 18 kN to slide on stone, more slowly; the
    /// player does not climb what gives way. A 3×3×3 stone cube (70 t) does not budge, and is
    /// too tall to climb.
    /// </summary>
    private static bool PlayerPushes()
    {
        (double Moved, double Feet) Push(IEnumerable<(ivec3, BlockType)> blocks)
        {
            using World world = CreateWorld();
            Floor(world, 24);
            VoxelObject pushed = world.Spawn(blocks, new dvec3(8, 10.5, 2), dquat.Identity);
            Player player = world.SpawnPlayer(new dvec3(6, 11, 2));
            player.Flying = false;
            Run(world, 0.5);

            double start = pushed.Position.x;
            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            Run(world, 2.0);
            Console.WriteLine($"      {pushed.Body!.Mass / 1e3:F1} t moved {pushed.Position.x - start:F2} m, player at {player.Feet:F2}, health {player.Health:F2}");
            return (pushed.Position.x - start, player.Feet.z);
        }

        (double planks, double planksFeet) = Push([(ivec3.Zero, BlockType.OakPlanks)]);
        bool ok = Check("planks pushed in 2 s (m)", planks, 6.0, 9.5);
        ok &= Check("player's feet (m)", planksFeet, 1.99, 2.01);
        (double stone, double stoneFeet) = Push([(ivec3.Zero, BlockType.Stone)]);
        ok &= Check("stone pushed in 2 s (m)", stone, 1.0, 9.5);
        ok &= Check("player's feet (m)", stoneFeet, 1.99, 2.01);
        (double cube, double cubeFeet) = Push(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.Stone));
        ok &= Check("70 t cube pushed (m)", cube, -0.02, 0.02);
        ok &= Check("player's feet (m)", cubeFeet, 1.99, 2.01);
        return ok;
    }

    /// <summary>
    /// Objects of many shapes, dropped tilted and spinning onto the generated world, half of
    /// them onto the others, come to rest on what they land on, whole or in pieces: none sinks
    /// into the map or into another object, or falls through the world.
    /// </summary>
    private static bool Drops()
    {
        using World world = CreateWorld();
        TerrainGenerator.Generate(world);
        world.ApplyTopologyChanges();
        // The world's own objects start on uneven ground, partly in it.
        HashSet<VoxelObject> placed = [.. world.Objects];
        int fellOut = 0;
        world.BodyRemoved += body => fellOut += body.Position.z < 0.0 ? 1 : 0;

        (ivec3, BlockType)[][] shapes =
        [
            [(ivec3.Zero, BlockType.Stone)],
            [(ivec3.Zero, BlockType.OakPlanks), (new ivec3(1, 0, 0), BlockType.OakPlanks)],
            [(ivec3.Zero, BlockType.Stone), (new ivec3(1, 0, 0), BlockType.Stone), (new ivec3(0, 0, 1), BlockType.Stone)],
            [.. TerrainGenerator.Box(new ivec3(3, 3, 1), BlockType.OakPlanks)],
            [.. TerrainGenerator.Box(new ivec3(2, 2, 2), BlockType.Glass)],
            [.. TerrainGenerator.Box(new ivec3(4, 1, 1), BlockType.Cobblestone)]
        ];

        Random random = new(7);
        for (int wave = 0; wave < 2; wave++)
        {
            for (int k = 0; k < 12; k++)
            {
                int x = 14 + 9 * (k % 4);
                int y = 14 + 9 * (k / 4);
                double height = TerrainGenerator.GroundHeight(x, y) + 3 + 5 * random.NextDouble() + 6 * wave;
                dquat orientation = dquat.FromAxisAngle(random.NextDouble() * Math.PI, new dvec3(0, 0, 1))
                    * dquat.FromAxisAngle(0.6 * (random.NextDouble() - 0.5), new dvec3(1, 0, 0));
                dvec3 spin = 2.0 * new dvec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
                world.Spawn(shapes[(k + wave) % shapes.Length], new dvec3(x + random.NextDouble(), y + random.NextDouble(), height), orientation, dvec3.Zero, spin);
            }

            Run(world, 3.0);
        }

        int intoMap = 0;
        int intoObjects = 0;
        List<VoxelObject> dropped = [.. world.Objects.Where(o => !placed.Contains(o))];
        foreach (VoxelObject body in dropped)
        {
            foreach (ivec3 cell in body.OccupiedCells())
            {
                // A cell's center deeper than a quarter of a block inside something else.
                dvec3 center = body.ToWorld(VoxelBody.CellCenter(cell));
                for (int corner = 0; corner < 8; corner++)
                {
                    dvec3 probe = center + 0.25 * new dvec3((corner & 1) * 2 - 1, ((corner >> 1) & 1) * 2 - 1, ((corner >> 2) & 1) * 2 - 1);
                    if (world.Map.Grid.IsOccupied(new ivec3((int)Math.Floor(probe.x), (int)Math.Floor(probe.y), (int)Math.Floor(probe.z))))
                    {
                        intoMap++;
                        Console.WriteLine($"      object {body.Id} ({body.Grid.Count} blocks) cell {cell} in the map at {probe:F2}, at {body.Position:F2} turned {body.Orientation:F3}, moving {body.LinearVelocity:F2}");
                        break;
                    }
                }

                foreach (VoxelObject other in world.Objects)
                {
                    if (other == body)
                        continue;
                    dvec3 local = other.ToLocal(center);
                    if (other.Grid.IsOccupied(new ivec3((int)Math.Floor(local.x), (int)Math.Floor(local.y), (int)Math.Floor(local.z))))
                    {
                        intoObjects++;
                        Console.WriteLine($"      object {body.Id} cell {cell} inside object {other.Id}");
                    }
                }
            }
        }

        bool ok = Check("object cells sunk into the map", intoMap, 0, 0);
        ok &= Check("object cells inside other objects", intoObjects, 0, 0);
        ok &= Check("objects fallen out of the world", fellOut, 0, 0);
        return ok;
    }

    /// <summary>
    /// On a 6×4×1 plank slab leaning from the floor onto a 1-block ledge, about 10°, the player
    /// walks up, down and across the slope at its walking speed, and stands still on it.
    /// </summary>
    private static bool PlayerSlope()
    {
        using World world = CreateWorld();
        Floor(world, 24);
        for (int x = 14; x < 20; x++)
        for (int y = 4; y < 14; y++)
            world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);

        VoxelObject slab = world.Spawn(TerrainGenerator.Box(new ivec3(6, 4, 1), BlockType.OakPlanks), new dvec3(8.5, 7, 2.2), dquat.FromAxisAngle(-0.18, new dvec3(0, 1, 0)));
        Run(world, 2.0);
        double tilt = Math.Asin(Math.Clamp((slab.Orientation * new dvec3(1, 0, 0)).z, -1.0, 1.0));
        bool ok = Check("slab's slope (degrees)", tilt * 180.0 / Math.PI, 7.0, 13.0);

        Player player = world.SpawnPlayer(slab.ToWorld(new dvec3(2.5, 2, 1.1)));
        player.Flying = false;
        Run(world, 0.5);
        ok &= Check("standing on the slab", player.Grounded ? 1 : 0, 1, 1);

        dvec3 still = player.Feet;
        Run(world, 0.5);
        ok &= Check("sliding while standing (m)", (player.Feet - still).Length, 0, 0.02);

        double Walk(dvec3 direction)
        {
            dvec3 start = player.Feet;
            player.Controls = new PlayerControls(direction, Sprint: false);
            Run(world, 0.5);
            player.Controls = default;
            Run(world, 0.2);
            return dvec3.Dot(player.Feet - start, direction);
        }

        ok &= Check("up the slope in 0.5 s (m)", Walk(new dvec3(1, 0, 0)), 2.0, 2.5);
        ok &= Check("down it (m)", Walk(new dvec3(-1, 0, 0)), 2.0, 2.5);
        ok &= Check("across it (m)", Walk(new dvec3(0, 1, 0)), 1.0, 2.5);
        ok &= Check("health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// A stone block thrown at 22 m/s at a free-standing glass wall on grass shatters the glass
    /// it hits and flies on through, while the grass under the wall is unharmed: soil gives
    /// only where an impact strikes it, so it does not soak up the blow as the wall's footing.
    /// </summary>
    private static bool GlassWallOnGrass()
    {
        using World world = CreateWorld();
        for (int x = 0; x < 24; x++)
        for (int y = 0; y < 24; y++)
        {
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
            world.SetBlock(world.Map, new ivec3(x, y, 1), BlockType.Grass);
        }

        for (int y = 6; y <= 14; y++)
        for (int z = 2; z <= 4; z++)
            world.SetBlock(world.Map, new ivec3(12, y, z), BlockType.Glass);

        int glass = 0;
        int grass = 0;
        world.BlockBroken += (body, _, voxel, _) =>
        {
            glass += body is VoxelMap && voxel.Type == BlockType.Glass ? 1 : 0;
            grass += body is VoxelMap && voxel.Type == BlockType.Grass ? 1 : 0;
        };
        Run(world, 1.0);
        VoxelObject thrown = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(5, 9.5, 3.6), dquat.Identity, new dvec3(22, 0, 0));
        Run(world, 1.5);

        bool ok = Check("glass blocks broken", glass, 1, 27);
        ok &= Check("grass blocks broken", grass, 0, 0);
        ok &= Check("the block beyond the wall (m)", thrown.Position.x, 13.0, 40.0);
        return ok;
    }

    /// <summary>
    /// On an L of blocks one high, the player cuts the inside corner from one arm to the
    /// other, half its box over the air: where the box's front meets the other arm's side,
    /// level with its feet, it passes on over without catching on the side's top edge: its
    /// beveled bottom rides over the edge, a hair above its feet, in a step.
    /// </summary>
    private static bool PlayerCutsCorner()
    {
        bool ok = true;
        foreach (dvec2 start in (dvec2[])[new(10.8, 9.4), new(10.9, 9.5), new(10.7, 9.3)])
        {
            using World world = CreateWorld();
            Floor(world, 24);
            for (int y = 4; y <= 10; y++)
                world.SetBlock(world.Map, new ivec3(10, y, 2), BlockType.Stone);
            for (int x = 11; x <= 15; x++)
                world.SetBlock(world.Map, new ivec3(x, 10, 2), BlockType.Stone);

            Player player = world.SpawnPlayer(new dvec3(start, 3.0));
            player.Flying = false;
            Run(world, 0.5);
            player.Controls = new PlayerControls(new dvec3(1, 1, 0).Normalized, Sprint: false);
            Run(world, 0.05);
            double slowest = double.PositiveInfinity;
            Run(world, 0.45, () =>
            {
                slowest = Math.Min(slowest, player.Body.LinearVelocity.y);
                return false;
            });

            ok &= Check($"from {start}: on the other arm (y, m)", player.Feet.y, 10.5, 11.2);
            ok &= Check("its feet (m)", player.Feet.z, 2.99, 3.01);
            ok &= Check("slowest toward it once walking (m/s)", slowest, 2.0, 3.3);
        }

        return ok;
    }

    /// <summary>
    /// Walking onto a slab whose top stands a little above the floor, the player gets on top
    /// without stopping: a lip of 2 cm meets its beveled bottom, one of 5 cm or 30 cm it steps
    /// up onto. The slab rests in a pit on a spacer that sets the lip's height.
    /// </summary>
    private static bool PlayerOverLips()
    {
        bool ok = true;
        foreach (double lip in (double[])[0.02, 0.05, 0.3])
        {
            using World world = CreateWorld();
            Floor(world, 24);
            for (int x = 10; x < 14; x++)
            for (int y = 8; y < 13; y++)
                world.SetBlock(world.Map, new ivec3(x, y, 1), BlockType.Air);
            world.Physics.Add(new FixedBody(TestMeshes.Box(new dvec3(12, 10.5, 1 + lip / 2), new dvec3(4, 5, lip))));
            VoxelObject slab = world.Spawn(TerrainGenerator.Box(new ivec3(4, 5, 1), BlockType.OakPlanks), new dvec3(10, 8, 1 + lip + 0.005), dquat.Identity);
            world.ApplyTopologyChanges();
            Run(world, 0.5);

            Player player = world.SpawnPlayer(new dvec3(8.5, 10.5, 2));
            player.Flying = false;
            Run(world, 0.5);
            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            Run(world, 0.8);
            player.Controls = default;
            Run(world, 0.3);

            ok &= Check($"onto a lip of {lip * 100:F0} cm: along (m)", player.Feet.x, 11.5, 13.5);
            ok &= Check("its feet on the slab (m)", player.Feet.z - (slab.Position.z + 1.0), -0.01, 0.01);
        }

        return ok;
    }

    /// <summary>
    /// Jumping at a block one high from too close, the player's feet hit its side before they
    /// clear its top: the side stops the player without throwing it back, it rises on past the
    /// top, its steering in the air carries it over, and it lands on the block.
    /// </summary>
    private static bool PlayerJumpsOnto()
    {
        bool ok = true;
        foreach (double gap in (double[])[0.05, 0.2])
        {
            using World world = CreateWorld();
            Floor(world, 24);
            for (int y = 4; y < 16; y++)
            for (int x = 10; x < 14; x++)
                world.SetBlock(world.Map, new ivec3(x, y, 2), BlockType.Stone);

            Player player = world.SpawnPlayer(new dvec3(10 - 0.3 - gap - 1.0, 10, 2));
            player.Flying = false;
            Run(world, 0.5);
            player.Controls = new PlayerControls(new dvec3(1, 0, 0), Sprint: false);
            Run(world, 1.0 / Player.WalkSpeed - 0.02);
            player.JumpPressed = true;
            double backward = 0.0;
            Run(world, 1.0, () =>
            {
                backward = Math.Min(backward, player.Body.LinearVelocity.x);
                return false;
            });

            ok &= Check($"jumping {gap} m from it: thrown back (m/s)", -backward, 0.0, 0.2);
            ok &= Check("on top of it (m)", player.Feet.z, 2.99, 3.01);
            ok &= Check("across it (m)", player.Feet.x, 10.5, 14.0);
        }

        return ok;
    }
}
