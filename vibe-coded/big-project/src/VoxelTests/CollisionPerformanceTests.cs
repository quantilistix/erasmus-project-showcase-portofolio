using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;
using PhysicsTests;
using VoxelGame;
using static VoxelGame.BoxCollision;

namespace VoxelTests;

internal static class CollisionPerformanceTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("collision-numerical-equivalence", Geometry),
        ("collision-spatial-index", Index),
        ("voxel-recovery-equivalence", Recovery),
        ("voxel-recovery-benchmark", Benchmark),
        ("voxel-terrain-certificates", TerrainCertificates)
    ];
    private static ReferenceBoxCollision.CollisionBox Reference(CollisionBox b) => new(b.Center, b.Half, b.Rotation);
    private static bool Check(string what, bool ok)
    {
        Console.WriteLine($"   {(ok ? "ok" : "BAD")} {what}");
        return ok;
    }

    private static bool Geometry()
    {
        Random random = new(7821);
        dvec3 Vector() => new(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
        int different = 0;
        Span<dvec3> axes = stackalloc dvec3[15];
        Span<double> radii = stackalloc double[15];
        for (int i = 0; i < 20000; i++)
        {
            dquat rotation = i % 3 == 0 ? dquat.Identity : dquat.FromAxisAngle(random.NextDouble() * Math.Tau, Vector().Normalized);
            CollisionBox a = new(Vector(), dvec3.Abs(Vector()) + 0.01, rotation);
            CollisionBox b = new(Vector(), dvec3.Abs(Vector()) + 0.01, i % 2 == 0 ? dquat.Identity : rotation);
            bool before = ReferenceBoxCollision.BoxPenetration(Reference(a), Reference(b), out dvec3 expected);
            bool after = BoxPenetration(a, b, out dvec3 actual);
            if (before != after || (expected - actual).Length > 1e-12) different++;
            BoxPair pair = new(a, b, axes, radii);
            if (pair.Penetration(a.Center - b.Center, out dvec3 paired) != before || (expected - paired).Length > 1e-12) different++;
            CollisionBox sole = new(Vector(), new dvec3(0.3, 0.3, 0.05), dquat.Identity);
            if (ReferenceBoxCollision.WalkableFaceIn(Reference(sole), Reference(b)) != WalkableFaceIn(sole, b)) different++;
        }
        CollisionBox foot = new(new dvec3(0.5, 0.5, 0.95), new dvec3(0.3, 0.3, 0.05), dquat.Identity);
        CollisionBox cube = new(new dvec3(0.5), new dvec3(0.5), dquat.Identity);
        for (int i = 0; i < 100; i++) { WalkableFaceIn(foot, cube); BoxPenetration(foot, cube, out _); }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        CollisionBox rotated = new(cube.Center, cube.Half, dquat.FromAxisAngle(0.2, dvec3.UnitY));
        for (int i = 0; i < 10000; i++) { WalkableFaceIn(foot, rotated); BoxPenetration(foot, rotated, out _); }
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        return Check("20,000 rotated/axis-aligned contacts and slope clips agree within 1e-12", different == 0)
            & Check($"geometry query allocations: {bytes} bytes", bytes == 0);
    }

    private static bool Index()
    {
        SpatialIndex index = new();
        Random random = new(431);
        SpatialBounds[] bounds = new SpatialBounds[513];
        List<int> hits = [];
        bool ok = true;
        for (int count = 0; count <= bounds.Length; count = count == 0 ? 1 : count * 2 + 1)
        {
            for (int i = 0; i < count; i++)
            {
                dvec3 low = new(random.Next(-20, 20), random.Next(-20, 20), random.Next(-20, 20));
                bounds[i] = new(low, low + new dvec3(1 + random.NextDouble() * 5));
            }
            index.Build(bounds.AsSpan(0, count));
            for (int step = 0; step < 100; step++)
            {
                dvec3 low = new(random.Next(-20, 20), random.Next(-20, 20), random.Next(-20, 20));
                SpatialBounds query = new(low, low + 3);
                index.Query(query, hits);
                hits.Sort();
                int[] expected = [.. Enumerable.Range(0, count).Where(i => bounds[i].Intersects(query))];
                ok &= hits.SequenceEqual(expected);
                if (count > 0)
                {
                    int moved = random.Next(count);
                    bounds[moved] = query;
                    index.Update(moved, query); // Later queries must see corrections immediately.
                }
                if (step % 10 == 0) index.Refit(bounds.AsSpan(0, count));
            }
        }
        index.Build([]);
        index.Query(new(dvec3.Zero, dvec3.Ones), hits);
        return Check("hierarchy agrees with exhaustive bounds after build, refit, movement and shrink", ok && hits.Count == 0);
    }

    private sealed class Scene
    {
        public readonly VoxelMap Map = new(0);
        public readonly List<VoxelObject> Objects = [];
        public readonly Player Player = new(1, new dvec3(-2, 0, 2));
        public readonly VoxelCollisionQueries Queries = new();
        public void Prepare() => Queries.Prepare(Map, Objects);
    }

    private static VoxelObject Object(int id, IEnumerable<(ivec3 Cell, Voxel Voxel)> cells, dvec3 position, dquat rotation)
    {
        VoxelObject body = new(id);
        double mass = 0;
        foreach (var cell in cells) { body.Grid[cell.Cell] = cell.Voxel; mass += cell.Voxel.Material.Density; }
        var (low, high) = body.Grid.Bounds();
        dvec3 size = new(high - low), center = (new dvec3(low) + new dvec3(high)) / 2;
        body.Bounds = (center, size.Length / 2);
        body.Body = new RigidBody(TestMeshes.Box(center, size), mass) { Position = position, Orientation = rotation };
        return body;
    }

    private static Scene Make(int count, bool overlapping)
    {
        Scene scene = new();
        for (int y = -8; y < 9; y++)
        for (int x = -8; x < count * 2 + 5; x++) scene.Map.Grid[new ivec3(x, y, 0)] = new Voxel(BlockType.Bedrock, 0);
        Random random = new(123);
        for (int i = 0; i < count; i++)
        {
            int size = i % 5 == 0 ? 3 : 1;
            var cells = TerrainGenerator.Box(new ivec3(size), BlockType.Stone).Select(c => (c.Cell, new Voxel(c.Type, 0)));
            scene.Objects.Add(Object(i + 2, cells, new dvec3(i * (overlapping ? 0.9 : 5), 0, overlapping ? 0.8 : 1.02),
                i % 3 == 0 ? dquat.Identity : dquat.FromAxisAngle(random.NextDouble() * 0.4, new dvec3(1, 2, 3).Normalized)));
        }
        return scene;
    }

    private static bool Same(Scene a, Scene b)
    {
        if ((a.Player.Position - b.Player.Position).Length > 1e-9) return false;
        for (int i = 0; i < a.Objects.Count; i++)
            if ((a.Objects[i].Position - b.Objects[i].Position).Length > 1e-9
                || a.Objects[i].LinearVelocity != b.Objects[i].LinearVelocity) return false;
        return true;
    }

    private static bool Recovery()
    {
        bool ok = true;
        foreach (bool overlaps in new[] { false, true })
        {
            Scene before = Make(40, overlaps), after = Make(40, overlaps);
            ReferenceVoxelRecovery reference = new(before.Map, before.Objects, before.Player);
            for (int step = 0; step < 10; step++)
            {
                if (step == 2)
                {
                    before.Objects[5].Body!.Position += new dvec3(-4, 0, 0);
                    after.Objects[5].Body!.Position += new dvec3(-4, 0, 0);
                }
                if (step == 3) // Same-count topology replacement invalidates cached local cells.
                    foreach (Scene scene in new[] { before, after })
                    {
                        scene.Objects[5].Grid[ivec3.Zero] = default;
                        scene.Objects[5].Grid[new ivec3(1, 0, 0)] = new Voxel(BlockType.Stone, 0);
                        scene.Objects[5].TopologyVersion++;
                    }
                if (step == 5) { before.Objects.RemoveAt(7); after.Objects.RemoveAt(7); }
                if (step == 6)
                    foreach (Scene scene in new[] { before, after }) scene.Objects[3].Body = null; // Parked target.
                if (step == 7)
                    foreach (Scene scene in new[] { before, after })
                        scene.Objects.Add(Object(500, [(ivec3.Zero, new Voxel(BlockType.Stone, 0))], new dvec3(3, 0, 1), dquat.Identity));
                reference.Recover();
                after.Prepare();
                after.Queries.Recover(after.Player);
                ok &= Same(before, after);
            }
        }
        return Check("ordered recovery matches original through overlaps, slopes, edits, removal, parking and spawns", ok);
    }

    private static bool TerrainCertificates()
    {
        Scene before = Make(12, false), after = Make(12, false);
        ReferenceVoxelRecovery reference = new(before.Map, before.Objects, before.Player);
        bool ok = true;
        long skipped = 0;
        Random random = new(9281);
        for (int step = 0; step < 240; step++)
        {
            // Sub-threshold drift, rotations, and occasional larger jumps consume
            // certificates; compare every correction against exhaustive SAT.
            for (int i = 0; i < before.Objects.Count; i++)
            {
                dvec3 delta = new((random.NextDouble() - 0.5) * 0.003, 0,
                    (random.NextDouble() - 0.55) * (step % 23 == 0 ? 0.1 : 0.001));
                dquat rotation = dquat.FromAxisAngle((random.NextDouble() - 0.5) * 0.001, dvec3.UnitY);
                before.Objects[i].Body!.Position += delta;
                after.Objects[i].Body!.Position += delta;
                before.Objects[i].Body!.Orientation = rotation * before.Objects[i].Orientation;
                after.Objects[i].Body!.Orientation = rotation * after.Objects[i].Orientation;
            }
            foreach (Scene scene in new[] { before, after })
            {
                // Every mutation path must invalidate, even unmarked updates and
                // streaming outside the map's structural simulation window.
                ivec3 cell = new(5, 0, 1);
                if (step == 30) scene.Map.Grid[cell] = new Voxel(BlockType.Water, 0);
                if (step == 31) scene.Map.Grid.Update(cell, new Voxel(BlockType.Stone, 0));
                if (step == 60) scene.Map.Grid.RemoveChunk(ivec3.Zero);
                if (step == 90)
                {
                    Voxel[] cells = new Voxel[Grid3D<Voxel>.ChunkVolume];
                    Array.Fill(cells, new Voxel(BlockType.Stone, 0));
                    scene.Map.Grid.SetChunk(ivec3.Zero, cells);
                }
                if (step == 120) scene.Map.Grid.Clear();
                if (step == 150)
                    for (int x = -8; x < 70; x++)
                        scene.Map.Grid[new ivec3(x, 0, 0)] = new Voxel(BlockType.Bedrock, 0);
            }
            reference.Recover();
            after.Prepare();
            after.Queries.Recover(after.Player);
            skipped += after.Queries.TerrainQueriesSkipped;
            ok &= Same(before, after);
        }
        return Check("bounded terrain reuse agrees through drift, rotation, impacts and all terrain mutation paths", ok)
            & Check($"terrain queries safely reused: {skipped}", skipped > 0);
    }

    private static Scene FromSave(string path)
    {
        WorldSnapshot snapshot = WorldFile.Read(path);
        Scene scene = new();
        foreach (MapColumn column in snapshot.Columns)
        foreach (var chunk in column.Chunks) scene.Map.Grid.SetChunk(new ivec3(column.Coordinate, chunk.Z), chunk.Cells);
        scene.Player.MoveTo(snapshot.Player.Feet);
        foreach (ObjectState body in snapshot.Objects) scene.Objects.Add(Object(scene.Objects.Count + 2, body.Cells, body.Position, body.Orientation));
        Console.WriteLine($"   saved workload: {scene.Objects.Count} bodies, {scene.Objects.Sum(b => b.Grid.Count)} cells");
        return scene;
    }

    private static bool Benchmark()
    {
        string? path = Environment.GetEnvironmentVariable("MYT_COLLISION_SAVE");
        Scene before = path == null ? Make(170, false) : FromSave(path);
        Scene after = path == null ? Make(170, false) : FromSave(path);
        ReferenceVoxelRecovery reference = new(before.Map, before.Objects, before.Player);
        for (int i = 0; i < 3; i++) { reference.Recover(); after.Prepare(); after.Queries.Recover(after.Player); }
        bool same = Same(before, after);
        const int steps = 20;
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        for (int i = 0; i < steps; i++) reference.Recover();
        double original = watch.Elapsed.TotalMilliseconds / steps;
        long originalBytes = (GC.GetAllocatedBytesForCurrentThread() - bytes) / steps;
        bytes = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < steps; i++) { after.Prepare(); after.Queries.Recover(after.Player); }
        double optimized = watch.Elapsed.TotalMilliseconds / steps;
        long optimizedBytes = (GC.GetAllocatedBytesForCurrentThread() - bytes) / steps;
        long prepareBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < steps; i++) after.Prepare();
        prepareBytes = (GC.GetAllocatedBytesForCurrentThread() - prepareBytes) / steps;
        Console.WriteLine($"   prepare allocations/step: {prepareBytes}");
        Console.WriteLine($"   recovery/step: original {original:F3} ms, {originalBytes} B; indexed {optimized:F3} ms, {optimizedBytes} B; {after.Queries.CandidateBodies} body candidates, {after.Queries.CellTests} cell tests, {after.Queries.TerrainQueriesSkipped} terrain queries reused");
        return Check("final poses match exhaustive recovery", same && Same(before, after))
            & Check("steady recovery allocates no managed memory", optimizedBytes == 0);
    }
}
