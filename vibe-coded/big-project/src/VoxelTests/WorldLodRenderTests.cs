using System.Diagnostics;
using GlmSharp;
using VoxelGame;

namespace VoxelTests;

/// <summary>Exercises the real Vulkan LOD pipeline, including indirect-command layout.</summary>
internal static class WorldLodRenderTests
{
    public static bool Run(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        renderer.SetTerrain(new TerrainGenerator(1));
        renderer.ViewDistance = 64;
        Camera camera = new(new(-6001, 5998, 180), new dvec3(-.5, 1, -.3).Normalized, 1.2, 640.0 / 480);
        BlockParticles particles = new();
        bool showWorld = false;
        void Draw(string? path = null)
        {
            if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Frame unavailable");
            graphics.CapturePath = path;
            renderer.Draw(frame, camera, showWorld ? world.Bodies : Array.Empty<VoxelBody>(), b => (b.Position, b.Orientation), particles, [], null, [], [], false);
            graphics.EndFrame(frame);
        }
        void Settle()
        {
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                Draw();
                if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("LOD did not settle");
            } while (!renderer.TerrainReady);
        }
        Settle();
        renderer.IndirectDraws = false;
        string serialPath = Path.Combine(captures, "lod-direct.ppm");
        Draw(serialPath);
        int patches = renderer.LodTilesDrawn;
        renderer.IndirectDraws = true;
        string indirectPath = Path.Combine(captures, "lod-indirect.ppm");
        Draw(indirectPath);
        bool equal = File.ReadAllBytes(serialPath).AsSpan().SequenceEqual(File.ReadAllBytes(indirectPath));
        bool ok = equal && patches > 100;
        Console.WriteLine($"   {patches} terrain patches beyond 64-block voxel detail, direct/indirect pixels identical: {equal}; {(ok ? "PASS" : "FAIL")}");
        Camera initial = camera;
        int cached = renderer.CachedTerrainMeshes;
        camera = camera with { Forward = -camera.Forward };
        Draw();
        bool turnReady = renderer.TerrainReady && renderer.CachedTerrainMeshes == cached;
        ok &= turnReady;
        Console.WriteLine($"   turning around uses already-generated terrain immediately: {turnReady}; {(turnReady ? "PASS" : "FAIL")}");
        // Repartition every frame without draining the workers. Old parent/child
        // meshes must cover regions whose newly selected replacements are pending.
        bool movingCover = true;
        for (int step = 1; step <= 12; step++)
        {
            camera = initial with { Position = initial.Position + new dvec3(step * 16, step * 8, 0) };
            Draw();
            movingCover &= renderer.TerrainCoverageGaps == 0;
        }
        camera = initial;
        Draw();
        ok &= movingCover;
        Console.WriteLine($"   moving across chunk boundaries retains terrain coverage while replacements build: {movingCover}; {(movingCover ? "PASS" : "FAIL")}");
        // Outlast the previous 600-frame eviction while the original terrain is
        // outside the current view and selection. No new world or cache reset.
        camera = camera with { Position = camera.Position + new dvec3(8192, 0, 0) };
        for (int i = 0; i < 605; i++) Draw();
        camera = initial;
        Draw();
        bool retained = renderer.TerrainReady;
        ok &= retained;
        Console.WriteLine($"   returning after 600+ frames uses retained meshes immediately: {retained}; {(retained ? "PASS" : "FAIL")}");
        TerrainGenerator generator = new(1);
        using ChunkStreamer streamer = new(world, generator, 4);
        renderer.SetTerrain(generator, streamer);
        renderer.ViewDistance = 16;
        camera = new(new(32, -64, 160), new dvec3(0, 1, -.65).Normalized, 1.2, 640.0 / 480);
        showWorld = true;
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            List<(int, Voxel[])> chunks = [];
            for (int z = 0; z < 6; z++)
            {
                Voxel[] cells = new Voxel[4096];
                for (int i = 0; i < cells.Length; i++) cells[i] = new(z == 5 && i >= 3840 ? BlockType.Grass : BlockType.Stone, 0);
                chunks.Add((z, cells));
            }
            world.LoadColumn(new(new(x, y), chunks.ToArray()), edited: true);
        }
        void Sync()
        {
            world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
            renderer.Sync(world, camera.Position);
        }
        byte[] Capture(string name)
        {
            Settle();
            string path = Path.Combine(captures, name + ".ppm");
            Draw(path);
            return File.ReadAllBytes(path);
        }
        Sync();
        byte[] intact = Capture("lod-intact");
        for (int z = 64; z < 96; z++)
        for (int y = 16; y < 48; y++)
        for (int x = 16; x < 48; x++) world.Map.Grid.Set(new(x, y, z), Voxel.Air);
        Sync();
        byte[] crater = Capture("lod-crater");
        bool changed = !intact.AsSpan().SequenceEqual(crater);
        List<MapColumn> saved = [];
        foreach (var column in world.Map.LoadedColumns!.ToArray())
            if (world.UnloadColumn(column) is MapColumn edited) saved.Add(edited);
        Sync();
        byte[] unloaded = Capture("lod-crater-unloaded");
        renderer.Clear();
        renderer.SetTerrain(generator, streamer);
        foreach (MapColumn column in saved) streamer.Keep(column);
        byte[] restored = Capture("lod-crater-restored");
        bool persists = !intact.AsSpan().SequenceEqual(unloaded) && !intact.AsSpan().SequenceEqual(restored);
        ok &= changed && persists;
        Console.WriteLine($"   crater changes distant pixels: {changed}; remains visible after unload and restored saved columns: {persists}; {(changed && persists ? "PASS" : "FAIL")}");
        if (ok) Directory.Delete(captures, true);
        else Console.WriteLine($"   Comparison images: {captures}");
        return ok;
    }
}
