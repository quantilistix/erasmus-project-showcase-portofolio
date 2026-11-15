using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool TntGlobalGrid()
    {
        using World world = CreateWorld();
        world.Particles.Enabled = false;
        var body = world.Spawn([(ivec3.Zero, BlockType.Tnt)], new(-3.7, 1.2, 5.3),
            dquat.FromAxisAngle(.7, new dvec3(1, 2, 3).Normalized));
        dvec3 center = body.ToWorld(VoxelBody.CellCenter(ivec3.Zero));
        BlastPressure expected = new();
        expected.Emit(VoxelBody.CellCenter((ivec3)dvec3.Floor(center)));
        bool fired = world.Detonate(body, ivec3.Zero);
        bool same = true;
        for (int i = 0; i < 30; i++)
        {
            world.AdvanceExplosions(World.Step);
            expected.Advance(World.Step, _ => false);
            same &= world.Pressure.Cells.Count == expected.Cells.Count && expected.Cells.All(pair =>
                pair.Value == world.Pressure.Cells.GetValueOrDefault(pair.Key));
        }
        return Check("rotated moving-body TNT injects once onto the global voxel grid", fired && same ? 1 : 0, 1, 1);
    }

    private static bool PressureGridBenchmark()
    {
        bool ok = true;
        foreach (int sources in new[] { 1, 8 })
        {
            (double Time, double Worst, double Sum, long Bytes) Run()
            {
                BlastPressure pressure = new();
                for (int i = 0; i < sources; i++) pressure.Emit(new(.5 + i % 2, .5 + i / 2 % 2, .5 + i / 4));
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                double worst = 0, sum = 0;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                for (int step = 0; step < 80; step++)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    pressure.Advance(World.Step, _ => false);
                    worst = Math.Max(worst, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    sum += pressure.Sample(new(4.5, .5, .5)).Pressure;
                }
                return (timer.Elapsed.TotalMilliseconds, worst, sum, GC.GetAllocatedBytesForCurrentThread() - bytes);
            }
            Run(); Run();
            var runs = Enumerable.Range(0, 5).Select(_ => Run()).OrderBy(r => r.Time).ToArray();
            var median = runs[2];
            Console.WriteLine($"   {sources} TNT, 80 local steps: median {median.Time:F2} ms, worst step {median.Worst:F2} ms, {median.Bytes / 1024.0:F0} KB allocated after injection, integrated sample {median.Sum:G17}");
            ok &= median.Sum > 0 && runs.All(r => Math.Abs(r.Sum - median.Sum) < 1e-10);
        }
        return ok;
    }

    private static bool TntSpatialBenchmark()
    {
        using World world = CreateWorld();
        for (int i = 0; i < 256; i++)
            world.Spawn([(ivec3.Zero, BlockType.Bedrock)], new(1000 + i * 2, 1000, 10), dquat.Identity);
        for (int y = -4; y <= 4; y++)
        for (int x = -4; x <= 4; x++) world.SetBlock(world.Map, new(x, y, 0), BlockType.Bedrock);
        world.ApplyTopologyChanges();
        (double Time, double Pressure, int Cells) Measure(bool spatial)
        {
            world.SpatialBlastBounds = spatial;
            world.Pressure.Emit(new(.5, .5, 2.5));
            double pressure = 0;
            int cells = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int step = 0; step < 40; step++)
            {
                world.AdvanceExplosions(World.Step);
                cells += world.Pressure.Cells.Count;
                foreach (var value in world.Pressure.Cells.Values) pressure += value.Pressure;
            }
            return (timer.Elapsed.TotalMilliseconds, pressure, cells);
        }
        Measure(false); Measure(true);
        var reference = Measure(false);
        var optimized = Measure(true);
        bool same = reference.Cells == optimized.Cells && reference.Pressure == optimized.Pressure;
        Console.WriteLine($"   256 distant objects, identical 40-step blast: full scans {reference.Time:F1} ms, bounded {optimized.Time:F1} ms ({reference.Time / optimized.Time:F1}x); field identical: {same}");
        return same && world.Objects.Count == 256;
    }

    private static bool PressureSupport()
    {
        BlastPressure pressure = new();
        pressure.Emit(new(-18.2, 4.7, 8.1));
        pressure.Emit(new(-13.5, 2.5, 9.5));
        if (pressure.MayReach(new(1000, 1000, 1000), 4)) return false;
        for (int step = 0; step < 50; step++)
        {
            pressure.Advance(World.Step, _ => false);
            foreach (var (cell, value) in pressure.Cells)
            {
                dvec3 center = VoxelBody.CellCenter(cell);
                if (!pressure.MayReach(center, 0) || !pressure.MayLoad(center, 0) || pressure.Sample(center) != value) return false;
            }
            dvec3 sample = new(-15.1, 3.2, 8.9);
            dvec3 p = sample - .5;
            ivec3 low = (ivec3)dvec3.Floor(p);
            dvec3 f = p - new dvec3(low);
            BlastPressure.Load expected = default;
            for (int z = 0; z < 2; z++)
            for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                expected += pressure.Cells.GetValueOrDefault(low + new ivec3(x, y, z))
                    * ((x == 0 ? 1 - f.x : f.x) * (y == 0 ? 1 - f.y : f.y) * (z == 0 ? 1 - f.z : f.z));
            if (pressure.Sample(sample) != expected) return false;
        }
        BlastPressure outgoing = new();
        outgoing.Emit(new(.5)); outgoing.Advance(1, _ => false);
        if (outgoing.Active || outgoing.Cells.Count == 0) return false;
        outgoing.Emit(new(1000));
        foreach (var cell in outgoing.Cells.Keys)
            if (!outgoing.MayReach(VoxelBody.CellCenter(cell), 0)) return false;
        BlastPressure corner = new();
        corner.Emit(new(.5)); corner.Advance(.001, _ => false);
        dvec3 cornerSample = new(1.49);
        if (corner.Sample(cornerSample).Pressure <= 0 || !corner.MayLoad(cornerSample, 0)) return false;
        Console.WriteLine("   support bound retains every nonzero field cell and outgoing chain loads; centered and interpolated samples match the reference");
        return true;
    }

    private static bool PressureWaves()
    {
        static bool Air(ivec3 _) => false;
        dvec3 source = new(.5, .5, .5);
        BlastPressure single = new(), combined = new();
        single.Emit(source);
        combined.Emit(source);
        combined.Emit(source);
        bool positive = true;
        double singlePeak = 0, combinedPeak = 0, farSingle = 0, farCombined = 0;
        single.Advance(World.Step, Air);
        bool ok = Check("pressure takes time to travel", single.Sample(source + new dvec3(4, 0, 0)).Pressure, 0, 0);
        combined.Advance(World.Step, Air);
        dvec3 net = single.Cells.Values.Aggregate(dvec3.Zero, (sum, load) => sum + load.Difference);
        ok &= Check("an isolated blast has no directional bias", net.Length, 0, 1e-10);
        for (int frame = 1; frame < 45; frame++)
        {
            single.Advance(World.Step, Air);
            combined.Advance(World.Step, Air);
            foreach (var load in combined.Cells.Values)
                positive &= load.Pressure >= 0 && double.IsFinite(load.Pressure) && load.Difference.Length <= load.Pressure + 1e-10;
            singlePeak = Math.Max(singlePeak, single.Sample(source + new dvec3(4, 0, 0)).Pressure);
            combinedPeak = Math.Max(combinedPeak, combined.Sample(source + new dvec3(4, 0, 0)).Pressure);
            farSingle = Math.Max(farSingle, single.Sample(source + new dvec3(18, 0, 0)).Pressure);
            farCombined = Math.Max(farCombined, combined.Sample(source + new dvec3(18, 0, 0)).Pressure);
        }
        ok &= Check("pressure stays nonnegative and finite", positive ? 1 : 0, 1, 1)
            & Check("overlapping TNT doubles pressure", combinedPeak / singlePeak, 2 - 1e-10, 2 + 1e-10)
            & Check("one TNT is below threshold beyond 16 blocks", farSingle < BlastPressure.Threshold ? 1 : 0, 1, 1)
            & Check("combined TNT extends the effective radius", farCombined > BlastPressure.Threshold ? 1 : 0, 1, 1)
            & Check("waves dissipate", combined.Active || combined.Cells.Count != 0 ? 1 : 0, 0, 0);

        BlastPressure opposing = new();
        opposing.Emit(source - new dvec3(2, 0, 0));
        opposing.Emit(source + new dvec3(2, 0, 0));
        opposing.Advance(.05, Air);
        var middle = opposing.Sample(source);
        ok &= Check("opposing blasts retain damaging compression", middle.Pressure, .1, 2)
            & Check("opposing pressure loads cancel momentum", middle.Difference.Length, 0, 1e-10);
        BlastPressure reversed = new();
        reversed.Emit(source + new dvec3(2, 0, 0));
        reversed.Emit(source - new dvec3(2, 0, 0));
        reversed.Advance(.05, Air);
        ok &= Check("simultaneous blasts do not depend on source order", opposing.Cells.All(pair =>
            Math.Abs(pair.Value.Pressure - reversed.Cells.GetValueOrDefault(pair.Key).Pressure) < 1e-10
            && (pair.Value.Difference - reversed.Cells.GetValueOrDefault(pair.Key).Difference).Length < 1e-10) ? 1 : 0, 1, 1);

        BlastPressure wall = new();
        wall.Emit(source);
        bool closed = true;
        bool Solid(ivec3 cell) => closed && cell.x == 2;
        wall.Advance(.04, Solid);
        ok &= Check("wave reaches the wall", wall.Sample(source + new dvec3(2, 0, 0)).Pressure, .01, 1)
            & Check("intact wall shields air behind it", wall.Sample(source + new dvec3(3, 0, 0)).Pressure, 0, 0);
        closed = false;
        wall.ReleaseOpenCells(Solid);
        wall.Advance(.04, Solid);
        ok &= Check("broken wall transmits remaining pressure", wall.Sample(source + new dvec3(3, 0, 0)).Pressure, .01, 1);

        foreach (dvec3 position in new[] { new dvec3(.1, .8, .2), new dvec3(-.1, -2.8, -.2) })
        {
            BlastPressure fractional = new(), snapped = new();
            fractional.Emit(position);
            snapped.Emit(dvec3.Floor(position) + .5);
            fractional.Advance(.08, Air); snapped.Advance(.08, Air);
            ok &= Check("off-grid TNT uses its containing global cell (including negatives)",
                fractional.Cells.Count == snapped.Cells.Count && fractional.Cells.All(pair =>
                    pair.Value == snapped.Cells.GetValueOrDefault(pair.Key)) ? 1 : 0, 1, 1);
        }

        // Bins must preserve travel timing for steps smaller than a bin as well as
        // large catch-up steps. Compare the integrated impulse over both schedules.
        BlastPressure fine = new(), coarse = new();
        fine.Emit(source); coarse.Emit(source);
        double finePressure = 0, coarsePressure = 0;
        dvec3 fineMomentum = default, coarseMomentum = default;
        for (int i = 0; i < 500; i++)
        {
            fine.Advance(.001, Air);
            var load = fine.Sample(source + new dvec3(5, 2, 1));
            finePressure += load.Pressure * .001;
            fineMomentum += load.Difference * .001;
        }
        for (int i = 0; i < 10; i++)
        {
            coarse.Advance(.05, Air);
            var load = coarse.Sample(source + new dvec3(5, 2, 1));
            coarsePressure += load.Pressure * .05;
            coarseMomentum += load.Difference * .05;
        }
        ok &= Check("time bins preserve integrated pressure across timesteps", Math.Abs(finePressure - coarsePressure), 0, 1e-10)
            & Check("time bins preserve integrated momentum across timesteps", (fineMomentum - coarseMomentum).Length, 0, 1e-10);

        BlastPressure expired = new();
        expired.Emit(source);
        expired.Advance(1, cell => cell.x == 2);
        expired.ReleaseOpenCells(Air);
        ok &= Check("opening a wall after the pulse passed creates no new pressure", expired.Active ? 1 : 0, 0, 0);

        BlastPressure detour = new();
        detour.Emit(source);
        detour.Advance(.066, cell => cell == new ivec3(2, 0, 0));
        detour.ReleaseOpenCells(Air);
        detour.Advance(.008, Air);
        ok &= Check("an opened shortcut does not delay the wave already traveling around it",
            detour.Sample(source + new dvec3(3, 0, 0)).Pressure, .001, 1);
        return ok;
    }

    private static bool TntSuperposition()
    {
        (double Near, double Far) Kick(bool multiple)
        {
            using World world = CreateWorld();
            ivec3 first = new(0, 0, 5), second = new(1, 0, 5);
            world.SetBlock(world.Map, first, BlockType.Tnt);
            if (multiple) world.SetBlock(world.Map, second, BlockType.Tnt);
            var near = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), new dvec3(6.5, .5, 5.5), dvec3.Zero);
            var far = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), new dvec3(18.5, .5, 5.5), dvec3.Zero);
            world.Detonate(world.Map, first);
            AdvanceBlast(world, .7);
            return (near.LinearVelocity.x, far.LinearVelocity.x);
        }
        var single = Kick(false);
        var combined = Kick(true);
        return Check("one TNT pushes a nearby entity", single.Near, .001, 100)
            & Check("nearby TNT produces a stronger combined impulse", combined.Near / single.Near, 1.5, 4)
            & Check("one TNT leaves an entity beyond its radius still", single.Far, 0, 0)
            & Check("overlapping TNT reaches that farther entity", combined.Far, .0001, 100);
    }

    private static bool TntWorldStep()
    {
        using World world = CreateWorld();
        world.Physics.Settings.Gravity = dvec3.Zero;
        ivec3 cell = new(0, 0, 5);
        dvec3 center = VoxelBody.CellCenter(cell);
        world.SetBlock(world.Map, cell, BlockType.Tnt);
        world.Detonate(world.Map, cell);
        for (int i = 0; i < 12; i++) world.StepOnce();
        Camera camera = new(center - 40 * dvec3.UnitY, dvec3.UnitY, Math.PI / 2, 1);
        var vertices = world.Particles.BuildVertices(camera, new int[Blocks.Count, 6], 100, dustFirstLayer: 100, explosionFirstLayer: 200);
        List<double> radii = [];
        for (int i = 0; i < vertices.Length; i += 6)
            if (vertices[i].UvLayer.z >= 100 && vertices[i].UvLayer.z < 105)
                radii.Add((((dvec3)vertices[i].Position + (dvec3)vertices[i + 2].Position) / 2 - center).Length);
        return Check("fixed world steps advance the traveling pressure", world.Pressure.Active ? 1 : 0, 1, 1)
            & Check("all dust participates in normal game steps", radii.Count, 720, 720)
            & Check("pressure drives initially resting dust outward", radii.Average(), 1.5, 12);
    }

    private static bool TntStone()
    {
        using World world = CreateWorld();
        ivec3 tnt = new(0, 0, 5);
        for (int z = -2; z <= 2; z++)
        for (int y = -2; y <= 2; y++)
        for (int x = -2; x <= 2; x++)
            world.SetBlock(world.Map, tnt + new ivec3(x, y, z), BlockType.Stone);
        ivec3 anchor = tnt - ivec3.UnitZ;
        world.SetBlock(world.Map, anchor, BlockType.Bedrock);
        world.SetBlock(world.Map, tnt, BlockType.Tnt);
        world.Detonate(world.Map, tnt);
        AdvanceBlast(world, .7);
        int remaining = world.Bodies.Sum(body => body.Grid.Occupied().Count(c => c.Value.Type == BlockType.Stone));
        return Check("one TNT breaks adjacent intact stone", world.Map.Grid[tnt + ivec3.UnitX].Type == BlockType.Air ? 1 : 0, 1, 1)
            & Check("TNT carves a stone cavity without destroying all surrounding stone", remaining, 1, 122)
            & Check("bedrock still resists the blast", world.Map.Grid[anchor].Type == BlockType.Bedrock ? 1 : 0, 1, 1)
            & Check("breaking stone emits debris beyond the TNT effects", world.Particles.Count, 737, BlockParticles.MaxParticles);
    }
}
