using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.GLFW;
using VoxelGame;

namespace VoxelTests;

internal static class GpuMeshingTests
{
    public static bool Run(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        renderer.SynchronousMeshing = false;
        renderer.GpuCulledMeshing = true;
        renderer.GpuMeshInitialChunks = true;
        renderer.CaptureGpuMeshes = true;
        Camera camera = new(new(8, -28, 25), new dvec3(0, 36, -18).Normalized, 1.2, 640.0 / 480);
        Random random = new(7241);
        List<double> gpuTimes = [];
        BlockType[] types = [BlockType.Stone, BlockType.Glass, BlockType.Leaves, BlockType.Water, BlockType.OakLog, BlockType.Furnace];
        for (int i = 0; i < 1400; i++)
            world.Map.Grid[new(random.Next(-2, 19), random.Next(-2, 19), random.Next(-2, 19))] =
                new(types[random.Next(types.Length)], 0, (BlockOrientation)random.Next(6), random.Next(2) != 0);
        Voxel[] fullLeaves = new Voxel[4096];
        Array.Fill(fullLeaves, new Voxel(BlockType.Leaves, 0));
        world.Map.Grid.SetChunk(new(3, 3, 3), fullLeaves);
        void Sync()
        {
            world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
            renderer.Sync(world, camera.Position);
        }
        void Draw(string? path = null)
        {
            Glfw.GetApi().PollEvents();
            if (!graphics.BeginFrame(out var frame)) throw new InvalidOperationException("GPU mesh test frame unavailable");
            graphics.CapturePath = path;
            long completed = renderer.GpuMeshesCompleted;
            renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles, [], null, [], [], false);
            if (renderer.GpuMeshesCompleted != completed) gpuTimes.Add(renderer.GpuMeshMilliseconds);
            graphics.EndFrame(frame);
        }
        void Drain()
        {
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                Sync(); Draw();
                if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("GPU mesh queue did not drain");
            } while (renderer.ChunksMeshing != 0);
            Sync(); Draw();
        }
        bool Compare(string label)
        {
            bool matches = true;
            foreach (var chunk in world.Map.Grid.Chunks)
            {
                Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
                VoxelMesher.CopyPadded(world.Map.Grid, chunk.Coordinate, cells);
                uint[] expected = VoxelMesher.BuildRenderMeshReference(cells, renderer.FaceLayers, renderer.Video.TransparentLeaves);
                uint[] actual = renderer.UploadedMesh(world.Map.Id, chunk.Coordinate) ?? [];
                bool same = expected.SequenceEqual(actual);
                if (!same) Console.WriteLine($"   mismatch {chunk.Coordinate}: CPU {expected.Length / 8} / GPU {actual.Length / 8} faces");
                matches &= same;
            }
            Console.WriteLine($"   {label}: packed GPU vertices match CPU face/AO/texture reference: {matches}");
            return matches;
        }
        Drain();
        bool ok = renderer.GpuMeshesCompleted > 0 && Compare("opaque leaves, borders, negative chunks, oriented/lit blocks, water");
        renderer.Video.TransparentLeaves = true;
        Drain(); ok &= Compare("transparent leaves");
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 100; j++) world.Map.Grid[new(random.Next(-2, 19), random.Next(-2, 19), random.Next(-2, 19))] = Voxel.Air;
            Sync(); Draw();
        }
        Drain(); ok &= Compare("rapid overlapping edits");
        world.Map.Grid[new(8, 8, 8)] = new(BlockType.StoneSlab, 0, BlockOrientation.SlabTop);
        Drain(); ok &= Compare("GPU partial blocks");
        world.Map.Grid[new(8, 8, 8)] = Voxel.Air;
        Drain(); ok &= Compare("return from shapes to GPU cubes");
        renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
        string gpu = Path.Combine(captures, "gpu-mesh.ppm"); Draw(gpu);
        renderer.SynchronousMeshing = true;
        renderer.GpuCulledMeshing = false;
        renderer.IncrementalMeshing = false;
        bool merge = VoxelMesher.MergeRenderFaces;
        VoxelMesher.MergeRenderFaces = false;
        graphics.Vk.DeviceWaitIdle(graphics.Device);
        renderer.Clear();
        world.Map.MeshDirty.UnionWith(world.Map.Grid.Chunks.Select(c => c.Coordinate));
        Drain(); renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
        string cpu = Path.Combine(captures, "cpu-mesh.ppm"); Draw(cpu);
        VoxelMesher.MergeRenderFaces = merge;
        byte[] a = File.ReadAllBytes(gpu), b = File.ReadAllBytes(cpu);
        int differences = a.Zip(b).Count(p => Math.Abs(p.First - p.Second) > 8);
        ok &= a.Length == b.Length && differences < 50;
        Console.WriteLine($"   rendered GPU/CPU pixel differences >8: {differences}; {renderer.GpuMeshesCompleted} GPU meshes completed; {(ok ? "PASS" : "FAIL")}");
        if (gpuTimes.Count > 0) Console.WriteLine($"   GPU count/emission batches: median {gpuTimes.Order().ElementAt(gpuTimes.Count / 2):F3} ms; max {gpuTimes.Max():F3} ms");
        ok &= GameplayEdits(graphics, renderer, world, captures);
        if (ok) Directory.Delete(captures, true);
        else Console.WriteLine($"   GPU mesh captures: {captures}");
        return ok;
    }

    private static bool GameplayEdits(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        graphics.Vk.DeviceWaitIdle(graphics.Device);
        renderer.Clear();
        world.Map.Grid.Clear();
        renderer.SynchronousMeshing = false;
        renderer.GpuCulledMeshing = true;
        renderer.CaptureGpuMeshes = false; // Exercise production completion, without vertex readback.
        renderer.ViewDistance = 64;
        renderer.SetTerrain(new TerrainGenerator(713));
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
        {
            Voxel[] cells = new Voxel[4096];
            Array.Fill(cells, new Voxel(BlockType.Bedrock, 0), 0, 256);
            Array.Fill(cells, new Voxel(BlockType.Stone, 0), 256, 256);
            world.LoadColumn(new(new(x, y), [(0, cells)]), edited: true);
        }
        Camera camera = new(new(8, -8, 14), new dvec3(0, 16, -11).Normalized, 1.2, 640.0 / 480);
        void Draw()
        {
            world.ApplyTopologyChanges();
            renderer.Sync(world, camera.Position);
            Glfw.GetApi().PollEvents();
            if (!graphics.BeginFrame(out var frame)) throw new InvalidOperationException("Edit test frame unavailable");
            renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles, [], null, [], [], false);
            graphics.EndFrame(frame);
        }
        byte[] Capture(string name, bool settleTerrain = true, ivec3? chunk = null)
        {
            Stopwatch timer = Stopwatch.StartNew();
            int frames = 0;
            (long Requested, long Displayed) versions;
            do
            {
                // Use the gameplay edit -> topology -> renderer path, without directly
                // consuming Grid.TakeChangedChunks as the packing tests above do.
                Draw();
                frames++;
                versions = renderer.MeshVersions(world.Map.Id, chunk ?? ivec3.Zero);
                if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("Gameplay geometry did not update");
                if (!settleTerrain && frames > 4) throw new TimeoutException($"Edit starved behind background meshes: {versions}");
            } while (settleTerrain ? renderer.ChunksMeshing != 0 || !renderer.TerrainReady
                : versions.Requested != versions.Displayed);
            string path = Path.Combine(captures, name + ".ppm");
            if (!graphics.BeginFrame(out var capture)) throw new InvalidOperationException("Edit capture unavailable");
            graphics.CapturePath = path;
            renderer.Draw(capture, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles, [], null, [], [], false);
            graphics.EndFrame(capture);
            if (versions.Requested == 0 || versions.Displayed != versions.Requested)
                throw new InvalidOperationException($"Gameplay mesh versions did not converge: {versions}");
            Console.WriteLine($"   {name}: displayed after {frames} frames ({timer.Elapsed.TotalMilliseconds:F1} ms)");
            return File.ReadAllBytes(path);
        }
        byte[] initial = Capture("edit-initial");
        for (int z = 2; z < 5; z++)
        for (int y = 6; y < 10; y++)
        for (int x = 6; x < 10; x++) world.SetBlock(world.Map, new(x, y, z), BlockType.Stone);
        byte[] placed = Capture("edit-placed");
        for (int z = 2; z < 5; z++)
        for (int y = 6; y < 10; y++)
        for (int x = 6; x < 10; x++) world.RemoveBlock(world.Map, new(x, y, z));
        byte[] broken = Capture("edit-broken");
        bool changed = !initial.AsSpan().SequenceEqual(placed) && !placed.AsSpan().SequenceEqual(broken);
        Console.WriteLine($"   gameplay placement/removal with loaded columns, terrain LOD and no vertex readback changes rendered pixels: {changed}");
        // The surrounding outer columns cannot mesh until more columns stream in.
        // An empty chunk must not retain its old geometry waiting for neighbours
        // that have never had any displayed geometry in this session.
        world.SetBlock(world.Map, new(8, 8, 16), BlockType.Stone);
        Capture("edit-new-chunk", chunk: ivec3.UnitZ);
        world.RemoveBlock(world.Map, new(8, 8, 16));
        Capture("edit-empty-chunk", chunk: ivec3.UnitZ);
        Console.WriteLine("   deleting the last voxel commits despite unrendered neighbours waiting for streamed columns: True");
        // One blocked empty chunk must not hold every other deletion in the world.
        for (int y = -1; y <= 1; y++) world.LoadColumn(new(new(2, y), []));
        Capture("edit-neighbour-ready", settleTerrain: false, chunk: ivec3.UnitX);
        world.SetBlock(world.Map, new(15, 8, 16), BlockType.Stone);
        world.SetBlock(world.Map, new(8, 8, 48), BlockType.Stone);
        Capture("edit-dependent-source", settleTerrain: false, chunk: ivec3.UnitZ);
        Capture("edit-independent-source", settleTerrain: false, chunk: new ivec3(0, 0, 3));
        world.UnloadColumn(new(2, 0));
        world.SetBlock(world.Map, new(20, 8, 1), BlockType.Glass);
        world.RemoveBlock(world.Map, new(15, 8, 16));
        world.RemoveBlock(world.Map, new(8, 8, 48));
        Capture("edit-independent-deletion", settleTerrain: false, chunk: new ivec3(0, 0, 3));
        world.LoadColumn(new(new(2, 0), []));
        Capture("edit-dependent-deletion", settleTerrain: false, chunk: ivec3.UnitZ);
        // Grow the real terrain request queue while the player stays still. Edits
        // must finish without waiting for the much larger initial/LOD backlog.
        renderer.ViewDistance = ChunkStreamer.DefaultRenderDistance * 16;
        for (int i = 0; i < 32; i++) Draw();
        bool backlog = renderer.ChunksMeshing > 64;
        world.SetBlock(world.Map, new(8, 8, 2), BlockType.Glass);
        Capture("edit-streaming-placed", settleTerrain: false);
        world.RemoveBlock(world.Map, new(8, 8, 2));
        Capture("edit-streaming-broken", settleTerrain: false);
        world.SetBlock(world.Map, new(8, 8, 2), BlockType.Stone);
        world.ApplyTopologyChanges();
        renderer.Sync(world, camera.Position); // Queue the stone snapshot without dispatching it.
        world.SetBlock(world.Map, new(8, 8, 2), BlockType.Glass);
        Capture("edit-queued-snapshot-replaced", settleTerrain: false);
        Dictionary<long, int> issued = [];
        long previousDisplayed = renderer.MeshVersions(world.Map.Id, ivec3.Zero).Displayed;
        int progress = 0, oldestDisplayed = 0;
        for (int frame = 0; frame < 12; frame++)
        {
            world.SetBlock(world.Map, new(8, 8, 2), (frame & 1) == 0 ? BlockType.Stone : BlockType.Glass);
            Draw();
            var versions = renderer.MeshVersions(world.Map.Id, ivec3.Zero);
            issued[versions.Requested] = frame;
            if (versions.Displayed != previousDisplayed)
            {
                progress++;
                if (issued.TryGetValue(versions.Displayed, out int submitted)) oldestDisplayed = Math.Max(oldestDisplayed, frame - submitted);
                previousDisplayed = versions.Displayed;
            }
        }
        Capture("edit-continuous-final", settleTerrain: false);
        bool continuous = progress >= 3 && oldestDisplayed <= 4;
        Console.WriteLine($"   continuous edits: {progress} displayed versions in 12 frames, maximum displayed snapshot age {oldestDisplayed} frames: {continuous}");
        world.SetBlock(world.Map, new(8, 8, 2), BlockType.Tnt);
        world.Detonate(world.Map, new(8, 8, 2));
        for (int i = 0; i < 8; i++) world.AdvanceExplosions(World.Step);
        Capture("edit-streaming-exploded", settleTerrain: false);
        Console.WriteLine($"   placement, breaking, queued edits and explosion mesh versions converge within 4 frames during a real terrain backlog: {backlog}");
        return changed && backlog && continuous;
    }
}
