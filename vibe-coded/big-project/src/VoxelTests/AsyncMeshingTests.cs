using System.Diagnostics;
using GlmSharp;
using Silk.NET.GLFW;
using VoxelGame;

namespace VoxelTests;

internal static class AsyncMeshingTests
{
    public static unsafe bool Run(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        renderer.SynchronousMeshing = false;
        renderer.CaptureGpuMeshes = true;
        renderer.MeshSnapshotBudgetMilliseconds = 0; // exercise a strict one-snapshot-per-Sync limit
        Camera camera = new(new(12, -15, 16), new dvec3(0, 26, -12).Normalized, 1.2, 640.0 / 480);
        for (int x = -2; x < 34; x++)
        for (int y = 0; y < 8; y++)
        for (int z = 0; z < 3; z++) world.Map.Grid[new(x, y, z)] = new(BlockType.Stone, 0);
        bool bounded = true;
        void Sync()
        {
            world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
            renderer.Sync(world, camera.Position);
            bounded &= renderer.ImmediateMeshesBuilt == 0 && renderer.MeshesPosted <= 1;
        }
        void Draw(string? path = null)
        {
            Glfw.GetApi().PollEvents();
            if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Mesh test frame unavailable");
            graphics.CapturePath = path;
            renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles, [], null, [], [], false);
            graphics.EndFrame(frame);
            bounded &= renderer.MeshBytesUploaded <= renderer.MeshUploadBudgetBytes;
        }
        void Drain()
        {
            Stopwatch timer = Stopwatch.StartNew();
            do
            {
                Sync(); Draw();
                if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException("Mesh queue did not drain");
            } while (renderer.ChunksMeshing != 0);
            Sync(); Draw();
        }
        Sync();
        bool deferred = renderer.UploadedMesh(world.Map.Id, ivec3.Zero)?.Length == 0;
        Drain();
        bool initial = renderer.UploadedMesh(world.Map.Id, ivec3.Zero)?.Length > 0;
        long beforeNoop = renderer.GpuMeshesCompleted;
        world.Map.MeshDirty.UnionWith(world.Map.Grid.Chunks.Select(c => c.Coordinate));
        Drain();
        bool compressedRetained = renderer.GpuMeshesCompleted == beforeNoop;
        int accepted = renderer.MeshResultsAccepted;
        for (int i = 0; i < 40; i++)
        {
            for (int x = 4; x < 28; x++) world.Map.Grid[new(x, 3, 3)] = (i & 1) == 0 ? new(BlockType.Glass, 0) : Voxel.Air;
            Sync(); Draw();
        }
        bool progress = renderer.MeshResultsAccepted > accepted;
        Drain();
        foreach (var chunk in world.Map.Grid.Chunks)
        {
            var versions = renderer.MeshVersions(world.Map.Id, chunk.Coordinate);
            bounded &= versions.Requested == versions.Displayed;
        }
        // Delete a whole chunk while adjacent chunks need newly exposed faces.
        var old = renderer.UploadedMesh(world.Map.Id, ivec3.Zero);
        world.Map.Grid.SetChunk(ivec3.Zero, new Voxel[4096]);
        Sync();
        bool retained = ReferenceEquals(old, renderer.UploadedMesh(world.Map.Id, ivec3.Zero));
        Draw(); Drain();
        bool deleted = renderer.UploadedMesh(world.Map.Id, ivec3.Zero)?.Length == 0;
        // Old-world jobs may complete after Clear; record identity must reject them.
        world.Map.Grid[new(18, 2, 3)] = new(BlockType.Stone, 0);
        Sync(); renderer.Clear();
        world.Map.MeshDirty.UnionWith(world.Map.Grid.Chunks.Select(c => c.Coordinate));
        Drain();
        renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
        string asyncPath = Path.Combine(captures, "async.ppm"); Draw(asyncPath);
        renderer.SynchronousMeshing = true;
        graphics.Vk.DeviceWaitIdle(graphics.Device);
        renderer.Clear();
        world.Map.MeshDirty.UnionWith(world.Map.Grid.Chunks.Select(c => c.Coordinate));
        renderer.Sync(world, camera.Position);
        renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
        string reference = Path.Combine(captures, "sync.ppm"); Draw(reference);
        byte[] a = File.ReadAllBytes(asyncPath), b = File.ReadAllBytes(reference);
        int differences = a.Zip(b).Count(pair => Math.Abs(pair.First - pair.Second) > 8);
        bool pixels = a.Length == b.Length && differences < 50;
        // Unload a newly posted column before its result is consumed. It must not
        // return as a ghost mesh or wait forever for unloaded neighbouring columns.
        renderer.SynchronousMeshing = false;
        world.Map.LoadedColumns = [.. world.Map.Grid.Chunks.Select(c => new ivec2(c.Coordinate.x, c.Coordinate.y))];
        world.Map.ColumnVersion++;
        for (int y = 7; y <= 9; y++)
        for (int x = 7; x <= 9; x++)
        {
            Voxel[] cells = new Voxel[4096];
            if (x == 8 && y == 8) cells[0] = new(BlockType.Leaves, 0);
            world.LoadColumn(new(new(x, y), x == 8 && y == 8 ? [(0, cells)] : []));
        }
        Sync();
        world.UnloadColumn(new(8, 8));
        Drain();
        bool unloaded = renderer.UploadedMesh(world.Map.Id, new(8, 8, 0)) == null;
        bool ok = bounded && deferred && initial && progress && retained && deleted && pixels && unloaded && compressedRetained;
        Console.WriteLine($"   bounded snapshots/uploads: {bounded}; deferred: {deferred}; initial meshes: {initial}; progress during edits: {progress}; old mesh retained: {retained}; deletion committed: {deleted}");
        Console.WriteLine($"   after rapid edits, deletion and Clear: async/reference differences >8: {differences}; {(ok ? "PASS" : "FAIL")}");
        Console.WriteLine($"   unloaded in-flight column cannot return as a ghost: {unloaded}");
        Console.WriteLine($"   no-op streaming invalidations retain compressed terrain without GPU remeshing: {compressedRetained}");
        if (ok) Directory.Delete(captures, true);
        else Console.WriteLine($"   Async captures: {captures}");
        return ok;
    }
}
