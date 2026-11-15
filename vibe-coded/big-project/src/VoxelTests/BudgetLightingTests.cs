using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class BudgetLightingTests
{
    public static bool Run(GraphicsContext graphics, string shaders, World world)
    {
        using ComputeLighting gate = new(graphics, shaders) { DeferredDaylight = true };
        vec3 direction = new vec3(.45f, .3f, .84f).Normalized;
        gate.Configure(new(.2f), true, true, direction, timeSeconds: 0);
        int first = gate.ConfigurationChanges;
        for (int i = 1; i < 60; i++) gate.Configure(new(.2f), true, true, direction + new vec3(i * .00001f, 0, 0), timeSeconds: i / 60.0);
        bool throttled = gate.ConfigurationChanges == first;
        gate.Configure(new(.2f), false, true, direction, timeSeconds: .99);
        bool immediate = gate.ConfigurationChanges == first + 1;
        world.Map.LoadedColumns = [new(0, 0), new(10, 0)]; world.Map.ColumnVersion++;
        world.Map.Grid[new(2, 8, 8)] = new(BlockType.Furnace, 0, Lit: true);
        world.Map.Grid[new(162, 8, 8)] = new(BlockType.Furnace, 0, Lit: true);
        for (int z = 4; z <= 8; z++)
        for (int y = 4; y <= 8; y++)
        for (int x = 6; x <= 10; x++)
            if (x == 6 || x == 10 || y == 4 || y == 8 || z == 4 || z == 8)
                world.Map.Grid[new(x, y, z)] = new(BlockType.Stone, 0);
        using ComputeLighting bounded = new(graphics, shaders)
        { BudgetedDiffusion = true, AdaptiveTileBudget = false, TileBudget = 8 };
        using ComputeLighting reference = new(graphics, shaders);
        bounded.Configure(vec3.Zero, true, false);
        reference.Configure(vec3.Zero, true, false);
        int frame = 0, max = 0;
        void Step(ComputeLighting field, int iterations = 2)
        {
            uint f = (uint)(frame++ % GraphicsContext.FramesInFlight);
            field.Build(world.Bodies, b => (b.Position, b.Orientation), true); field.Prepare(f);
            if (ReferenceEquals(field, bounded)) max = Math.Max(max, field.ActiveTiles);
            graphics.Submit(cmd => field.Record(new Frame(cmd, f, 0), iterations));
        }
        Step(reference, 512);
        for (int i = 0; i < 128; i++)
        {
            world.Map.Grid[ivec3.Zero] = (i & 1) == 0 ? new(BlockType.Stone, 0) : Voxel.Air;
            bounded.Invalidate(world.Map, ivec3.Zero, true);
            Step(bounded);
        }
        var during = bounded.Capture();
        var farChunk = during.First(c => c.Coordinate == new ivec3(10, 0, 0));
        uint farValue = farChunk.Rgb.Length == 1 ? farChunk.Rgb[0] : farChunk.Rgb[3 | 8 << 4 | 8 << 8];
        bool fair = (farValue & 511) != 0;
        for (int i = 128; i < 1024; i++) Step(bounded);
        static float Sample(LightChunkState[] chunks, ivec3 cell)
        {
            var chunk = chunks.First(c => c.Coordinate == cell >> 4);
            uint packed = chunk.Rgb.Length == 1 ? chunk.Rgb[0] : chunk.Rgb[(cell.x & 15) | (cell.y & 15) << 4 | (cell.z & 15) << 8];
            return (packed & 511) * MathF.Pow(2, (int)(packed >> 27) - 24);
        }
        var actual = bounded.Capture(); var expected = reference.Capture();
        float near = Sample(actual, new(3, 8, 8)), far = Sample(actual, new(163, 8, 8));
        float target = Sample(expected, new(3, 8, 8));
        float farTarget = Sample(expected, new(163, 8, 8));
        bool converged = near > 0 && far > 0 && Math.Abs(near - target) < target * .03f && Math.Abs(far - farTarget) < farTarget * .03f;
        bool shielding = Sample(actual, new(8, 6, 6)) == 0;
        world.Map.Grid[new(6, 6, 6)] = Voxel.Air;
        bounded.Invalidate(world.Map, ivec3.Zero, true);
        for (int i = 0; i < 1024; i++) Step(bounded);
        float open = Sample(bounded.Capture(), new(8, 6, 6));
        world.Map.Grid[new(6, 6, 6)] = new(BlockType.Stone, 0);
        bounded.Invalidate(world.Map, ivec3.Zero, true);
        for (int i = 0; i < 1024; i++) Step(bounded);
        shielding &= open > 0 && Sample(bounded.Capture(), new(8, 6, 6)) < open * .01f + .00001f;
        ulong oldBytes = bounded.GridBytes;
        for (int x = 20; x < 40; x++) world.Map.LoadedColumns.Add(new(x, 0));
        world.Map.ColumnVersion++;
        for (int i = 0; i < 4; i++) Step(bounded);
        bool growth = bounded.GridBytes > oldBytes && Sample(bounded.Capture(), new(163, 8, 8)) > far * .95f;
        for (int i = 0; i < 128; i++) Step(bounded);
        world.Map.Grid[new(2, 8, 8)] = new(BlockType.Furnace, 0);
        bounded.Invalidate(world.Map, ivec3.Zero, true);
        for (int i = 0; i < 768; i++) Step(bounded);
        float dark = Sample(bounded.Capture(), new(3, 8, 8));
        bool removed = dark < near * .1f;
        bool ok = throttled && immediate && max <= 8 && converged && removed && growth && shielding && fair;
        Console.WriteLine($"   tiny daylight changes coalesced: {throttled}; settings immediate: {immediate}; max selected tiles {max}/8");
        Console.WriteLine($"   near/far light {near:F4}/{far:F4}, reference {target:F4}; removed source {dark:F4}; {(ok ? "PASS" : "FAIL")}");
        Console.WriteLine($"   bounded queue growth preserves existing light: {growth}");
        Console.WriteLine($"   sealed room stays dark; opening/closing a wall propagates correctly: {shielding}");
        Console.WriteLine($"   distant source progresses despite continuous urgent edits nearby: {fair}");
        return ok;
    }
}
