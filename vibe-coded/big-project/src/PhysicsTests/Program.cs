using System.Diagnostics;
using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Vulkan;

namespace PhysicsTests;


/// <summary>
/// Headless physics scenarios with pass/fail checks. Run all with
/// <c>dotnet run --project src/PhysicsTests</c>, or name some: <c>... -- soft-ridge ccd-bullet</c>.
/// </summary>
internal static partial class Program
{
    private const double TimeStep = 1.0 / 60.0;

    private static readonly (string Name, Func<HeadlessVulkan, bool> Run)[] Scenarios =
    [
        ("rigid-rest", RigidRest),
        ("rigid-tilted", RigidTiltedOnGrid),
        ("rigid-stack", RigidStack),
        ("rigid-ridge", RigidOnRidge),
        ("soft-rest", SoftRest),
        ("soft-ridge", SoftOnRidge),
        ("rigid-on-soft", RigidOnSoft),
        ("friction", FrictionIncline),
        ("friction-surfaces", FrictionBySurface),
        ("ccd-bullet", RigidBullet),
        ("soft-bullet", SoftBullet),
        ("edits", Edits),
        ("soft-soft", SoftOnSoft),
        ("rigid-edges", RigidEdgeOnEdge),
        ("edge-sliding", EdgeSliding),
        ("concave-sliding", ConcaveSliding),
        ("edge-ccd", EdgeCcd),
        ("endpoint-ccd", EndpointCcd),
        ("notched-wall", NotchedWall),
        ("small-box-pile", SmallBoxPile),
        ("tumbling-drop", TumblingDrop),
        ("checkerboard-drop", CheckerboardDrop),
        ("mesh-soft", SoftFromMesh),
        ("rope", HardRope),
        ("substeps", Substeps),
        ("far-away", FarFromOrigin),
        ("pile", Pile),
        ("many-rigid", ManyRigid),
        ("crowded-rigid", CrowdedRigid),
        ("body-candidates", BodyCandidatesOverflow),
        ("bounce", Bounce),
        ("bounce-surfaces", BounceBySurface),
        ("bounce-masses", BounceBetweenMasses),
        ("map", TestMap),
        ("cone-soft", ConeUnderSoft),
        ("soft-cone-raw", RawSoftCone),
        ("map-sphere", DenseSphereOnMap),
        ("contact-reports", ContactReports),
        ("report-readback", ReportReadback),
        ("statistics", StatisticsLifecycle),
        ("topology-state", TopologyState),
        ("resident-topology", ResidentTopology),
        ("geometry-assembly", GeometryAssembly),
        ("nonmanifold-topology", NonManifoldTopology),
        ("contact-identities", ContactIdentities),
        ("work-lists", WorkLists),
        ("fixed-rotation", FixedRotation),
        ("gravity-scale", GravityScaled)
    ];

    private static string _shaderDirectory = "";
    private static string _assetDirectory = "";

    public static int Main(string[] arguments)
    {
        string root = FindRepositoryRoot();
        _shaderDirectory = Path.Combine(root, "assets", "shaders", "physics");
        _assetDirectory = Path.Combine(root, "assets", "3d");

        HeadlessVulkan vulkan = new(enableValidation: Environment.GetEnvironmentVariable("MYT_VALIDATION") != "0");
        Console.WriteLine($"Device: {vulkan.DeviceName}, validation {(vulkan.ValidationEnabled ? "on" : "off")}");

        int failures = 0;
        foreach ((string name, Func<HeadlessVulkan, bool> run) in Scenarios)
        {
            if (arguments.Length > 0 && !arguments.Contains(name))
                continue;

            Console.WriteLine($"\n== {name}");
            int errorsBefore = vulkan.ValidationErrorCount;
            bool passed = run(vulkan) && vulkan.ValidationErrorCount == errorsBefore;
            Console.WriteLine(passed ? "   PASS" : "   FAIL");
            if (!passed)
                failures++;
        }

        vulkan.Dispose();
        Console.WriteLine($"\n{failures} failed, validation errors: {vulkan.ValidationErrorCount}");
        return failures == 0 && vulkan.ValidationErrorCount == 0 ? 0 : 1;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "shaders", "physics")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository");
    }

    private static bool GeometryAssembly(HeadlessVulkan v)
    {
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
        Mesh3D large = TestMeshes.Box(new dvec3(1, 2, 3), new dvec3(3, 2, 1));
        List<RigidBody> rigid = [];
        for (int i = 0; i < 5000; i++)
        {
            RigidBody body = new(i % 6 == 0 ? large : cube)
            {
                Position = new dvec3(i % 24 * 2, i / 24 % 24 * 2, i / 576 * 2 + 16),
                Orientation = dquat.FromAxisAngle(i * 0.17, new dvec3(1, 2, 3).Normalized)
            };
            if (i % 5 == 0)
                body.SurfaceMaterials = body.Triangles.Select((_, t) => new SurfaceMaterial(t % 3 * 0.2, t % 2 * 0.1)).ToArray();
            rigid.Add(body);
        }
        List<FixedBody> fixedBodies = [new(TestMeshes.Box(new dvec3(0, 0, -1), new dvec3(100, 100, 1)))];
        fixedBodies[0].SurfaceMaterials = fixedBodies[0].Triangles.Select((_, t) => new SurfaceMaterial(t % 2 * 0.3, 0)).ToArray();
        List<SoftBody> soft =
        [
            SoftBody.CreateLatticeBox(new dvec3(0, 0, 20), dvec3.Ones, new ivec3(3), 1, 100, 0.005),
            SoftBody.CreateLatticeBox(new dvec3(10, 0, 20), dvec3.Ones, new ivec3(2), 1, 100, 0.005)
        ];
        CollisionGeometry serial = new(), parallel = new();
        static bool Equal<T>(List<T> a, List<T> b) where T : unmanaged =>
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(a)).SequenceEqual(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(b)));
        bool Compare(string stage)
        {
            uint nodes = (uint)soft.Sum(b => b.NodeCount);
            serial.Rebuild(fixedBodies, rigid, soft, nodes, parallelAssembly: false);
            parallel.Rebuild(fixedBodies, rigid, soft, nodes, parallelAssembly: true);
            bool identical = Equal(serial.VertexLocal, parallel.VertexLocal)
                && Equal(serial.VertexWorld, parallel.VertexWorld)
                && Equal(serial.VertexInfo, parallel.VertexInfo)
                && Equal(serial.Edges, parallel.Edges)
                && Equal(serial.Triangles, parallel.Triangles)
                && Equal(serial.TriangleBounds, parallel.TriangleBounds)
                && Equal(serial.Bodies, parallel.Bodies)
                && Equal(serial.BvhNodes, parallel.BvhNodes)
                && Equal(serial.BvhPrimitives, parallel.BvhPrimitives)
                && Equal(serial.BvhRefit, parallel.BvhRefit)
                && Equal(serial.PaletteMaterials, parallel.PaletteMaterials)
                && serial.DynamicBodyCount == parallel.DynamicBodyCount
                && serial.DynamicTriangleCount == parallel.DynamicTriangleCount
                && serial.DynamicVertexCount == parallel.DynamicVertexCount
                && serial.SoftBvhNodeStart == parallel.SoftBvhNodeStart
                && serial.SoftBvhNodeCount == parallel.SoftBvhNodeCount
                && serial.BodyMaterialCount == parallel.BodyMaterialCount;
            bool same = Check($"{stage}: serial/parallel geometry byte equality", identical ? 1 : 0, 1, 1);
            int wrongOwners = 0;
            // Every test surface is nonempty, so body order also names its own material.
            // This independent check catches deferred jobs capturing a final loop index.
            for (int b = 0; b < parallel.Bodies.Count; b++)
            {
                GpuCollisionBody body = parallel.Bodies[b];
                for (uint i = body.Extra.y; i < body.Extra.y + body.Extra.z; i++)
                {
                    uvec4 info = parallel.VertexInfo[(int)i];
                    if (info.x != b || info.y != body.Info.x || info.w != b)
                        wrongOwners++;
                }
            }
            return same & Check($"{stage}: vertex owner and report body IDs", wrongOwners, 0, 0);
        }
        bool ok = Compare("mixed shapes, palettes and deformable bodies");
        // Benchmark retained-capacity assembly separately from fresh shape cooking.
        for (int i = 0; i < 5; i++)
        {
            serial.Rebuild(fixedBodies, rigid, [], 0, parallelAssembly: false);
            parallel.Rebuild(fixedBodies, rigid, [], 0, parallelAssembly: true);
        }
        const int repeats = 12;
        Stopwatch watch = Stopwatch.StartNew();
        long serialAllocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < repeats; i++)
            serial.Rebuild(fixedBodies, rigid, [], 0, parallelAssembly: false);
        serialAllocated = GC.GetAllocatedBytesForCurrentThread() - serialAllocated;
        double serialTime = watch.Elapsed.TotalMilliseconds / repeats;
        watch.Restart();
        for (int i = 0; i < repeats; i++)
            parallel.Rebuild(fixedBodies, rigid, [], 0, parallelAssembly: true);
        Console.WriteLine($"   5,000 rigid bodies: serial {serialTime:F3} ms, parallel {watch.Elapsed.TotalMilliseconds / repeats:F3} ms per geometry rebuild");
        ok &= Check("cached serial assembly allocation per rebuild", serialAllocated / repeats, 0, 0);
        rigid.RemoveRange(0, 2700);
        rigid.Reverse();
        rigid[0].Position += new dvec3(100, -50, 7);
        rigid[0].SurfaceMaterials = null;
        soft[0].Translate(new dvec3(0.1, -0.2, 0.3));
        ok &= Compare("removal, reordering, material edit and soft motion");
        fixedBodies.Clear();
        soft.Clear();
        rigid.Clear();
        ok &= Compare("empty after shrink");
        rigid.Add(new RigidBody(cube) { Position = new dvec3(-20, 30, 5) });
        ok &= Compare("single-body reuse after shrink");
        return ok;
    }

    private static PhysicsSystem CreateSystem(HeadlessVulkan v)
    {
        PhysicsSystem physics = new(v.Vk, v.Device, v.PhysicalDevice, v.CommandPool, v.Queue, _shaderDirectory);
        if (uint.TryParse(Environment.GetEnvironmentVariable("MYT_ITERATIONS"), out uint iterations))
            physics.Settings.Iterations = iterations;
        if (uint.TryParse(Environment.GetEnvironmentVariable("MYT_CCD"), out uint passes))
            physics.Settings.ContinuousCollisionPasses = passes;
        return physics;
    }

    /// <summary>Steps once with GPU profiling and prints the time per phase.</summary>
    private static void ProfileStep(PhysicsSystem physics)
    {
        physics.Profile = true;
        physics.Update(TimeStep);
        physics.Profile = false;
        foreach (IGrouping<string, (string Phase, double Milliseconds)> phase in physics.LastProfile.GroupBy(p => p.Phase))
            Console.WriteLine($"   gpu {phase.Key,-22} {phase.Sum(p => p.Milliseconds),8:F3} ms total, {phase.Count()} x");
    }

    private static bool TopologyState(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        RigidBody moving = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones), 2)
        {
            LinearVelocity = new dvec3(2, 0, 0)
        };
        SoftBody soft = SoftBody.CreateLatticeBox(new dvec3(10, 0, 0), dvec3.Ones, new ivec3(2), 1, 100, 0.005);
        dvec3 initial = soft.Positions[0];
        soft.SyncToCpu = false;
        physics.Add(moving);
        physics.Add(soft);
        soft.ApplyImpulse(new dvec3(0, 3, 0));
        for (int i = 0; i < 10; i++)
            physics.Update(TimeStep);
        bool ok = Check("unsynchronized soft state stays on GPU", (soft.Positions[0] - initial).Length, 0, 1e-9);
        RigidBody late = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones)) { Position = new dvec3(-10, 0, 0) };
        physics.Add(late);
        physics.Update(TimeStep);
        ok &= Check("rigid position survives topology rebuild", moving.Position.x, 22 * TimeStep - 1e-6, 22 * TimeStep + 1e-6);
        soft.SyncToCpu = true;
        physics.Update(TimeStep);
        ok &= Check("unsynchronized soft motion survives topology rebuild", soft.Positions[0].y - initial.y, 36 * TimeStep - 1e-5, 36 * TimeStep + 1e-5);
        moving.Position = new dvec3(20, 0, 0);
        moving.ApplyImpulse(new dvec3(4, 0, 0));
        physics.Remove(late);
        physics.Update(TimeStep);
        ok &= Check("queued impulse survives topology rebuild", moving.LinearVelocity.x, 3.99999, 4.00001);
        ok &= Check("queued pose survives topology rebuild", moving.Position.x, 20 + 4 * TimeStep - 1e-6, 20 + 4 * TimeStep + 1e-6);
        return ok;
    }

    private static bool ContactIdentities(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.MaxContacts = 1024;
        physics.Settings.MaxContactReports = 1024;
        physics.Settings.ReportContacts = true;
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
        RigidBody prefix = new(cube) { Position = new dvec3(20, 0, 20) };
        RigidBody resting = new(cube) { Position = new dvec3(0, 0, 0.5) };
        FixedBody floor = new(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(10, 10, 1)));
        physics.Add(prefix);
        physics.Add(resting);
        physics.Add(floor);
        for (int i = 0; i < 30; i++)
            physics.Update(TimeStep);
        uint lifetime = resting.ContactIdentity;
        physics.Remove(prefix); // both the resting body's and floor's dense features move
        physics.Update(TimeStep);
        bool ok = Check("removing a prefix body preserves warm starts", physics.Statistics.WarmStartedContactCount, 1, 1000);
        ok &= Check("unchanged body keeps feature lifetime", resting.ContactIdentity == lifetime ? 1 : 0, 1, 1);
        physics.Add(prefix); // moving the fixed geometry again must preserve its feature key
        physics.Update(TimeStep);
        ok &= Check("adding an unrelated body preserves warm starts", physics.Statistics.WarmStartedContactCount, 1, 1000);
        physics.Remove(resting);
        physics.Add(resting);
        physics.Update(TimeStep);
        ok &= Check("reattaching a body starts a fresh lifetime", resting.ContactIdentity != lifetime ? 1 : 0, 1, 1);
        ok &= Check("old lifetime cannot warm start a reattached body", physics.Statistics.WarmStartedContactCount, 0, 0);
        lifetime = resting.ContactIdentity;
        resting.SurfaceMaterials = Enumerable.Repeat(new SurfaceMaterial(0.5, 0), resting.Triangles.Length).ToArray();
        physics.Update(TimeStep);
        ok &= Check("surface edit starts a fresh feature lifetime", resting.ContactIdentity != lifetime ? 1 : 0, 1, 1);
        ok &= Check("old material cache cannot warm start edited surface", physics.Statistics.WarmStartedContactCount, 0, 0);
        ok &= Check("resting height after topology changes", MinZ(resting), -0.005, 0.005);
        return ok;
    }

    private static bool ResidentTopology(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        FixedBody floor = new(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(50, 50, 1)));
        floor.SurfaceMaterials = Enumerable.Repeat(new SurfaceMaterial(0.5, 0), floor.Triangles.Length).ToArray();
        physics.Add(floor);
        RigidBody first = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones)) { Position = new dvec3(0, 0, 10), LinearVelocity = dvec3.UnitX };
        physics.Add(first);
        physics.Update(TimeStep);
        ulong initialBytes = physics.TopologyUploadBytes;
        floor.SurfaceMaterials = floor.SurfaceMaterials!.ToArray();
        physics.Update(TimeStep);
        bool ok = Check("same-size rebuild grows no collision buffers", physics.TopologyBufferGrowths, 0, 0);
        ok &= Check("unchanged immutable ranges reduce uploaded bytes", physics.TopologyUploadBytes / (double)initialBytes, 0, 0.95);
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
        List<RigidBody> added = [];
        for (int i = 0; i < 40; i++)
        {
            RigidBody body = new(cube) { Position = new dvec3(i * 2, 20, 10), LinearVelocity = dvec3.UnitY };
            added.Add(body);
            physics.Add(body);
            physics.Update(TimeStep);
        }
        physics.Remove(first);
        foreach (RigidBody body in added.Where((_, i) => i % 2 == 0))
            physics.Remove(body);
        physics.Update(TimeStep);
        ok &= Check("surviving body state after growth and compaction", added[1].Position.y, 20 + 40 * TimeStep - 1e-6, 20 + 40 * TimeStep + 1e-6);
        foreach (RigidBody body in added.Where((_, i) => i % 2 != 0))
            physics.Remove(body);
        physics.Update(TimeStep);
        physics.Add(first);
        physics.Update(TimeStep);
        ok &= Check("reused smaller geometry has correct state", first.Position.x, 43 * TimeStep - 1e-6, 43 * TimeStep + 1e-6);
        return ok;
    }

    private static bool StatisticsLifecycle(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        physics.Settings.Substeps = 2;
        physics.Profile = true;
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(10, 10, 1))));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones)) { Position = new dvec3(0, 0, 0.5) };
        physics.Add(box);
        physics.Update(TimeStep);
        bool ok = Check("moving triangles", physics.Statistics.DynamicTriangleCount, 12, 12);
        ok &= Check("tested pairs", physics.Statistics.CandidatePairCount, 1, double.MaxValue);
        ok &= Check("all detection pairs include the last pass", physics.Statistics.CandidatePairCount - (double)physics.DownloadPairCount(), 0, double.MaxValue);
        ok &= Check("measured total covers recording and readback", physics.Timings.TotalMilliseconds
            - physics.Timings.RecordingMilliseconds - physics.Timings.ReadbackMilliseconds, 0, double.MaxValue);
        physics.Remove(box);
        physics.Update(TimeStep);
        ok &= Check("empty scene clears contacts", physics.Statistics.ContactCount, 0, 0);
        ok &= Check("empty scene clears pairs", physics.Statistics.CandidatePairCount, 0, 0);
        ok &= Check("empty scene clears warm starts", physics.Statistics.WarmStartedContactCount, 0, 0);
        ok &= Check("empty scene clears serial scheduling", physics.Statistics.SerialRigidBodyCount, 0, 0);
        ok &= Check("empty scene clears moving triangles", physics.Statistics.DynamicTriangleCount, 0, 0);
        ok &= Check("empty scene clears GPU time", physics.Statistics.GpuMilliseconds, 0, 0);
        ok &= Check("empty scene clears reports", physics.ContactReports.Count, 0, 0);
        ok &= Check("empty scene clears phase profile", physics.LastProfile.Count, 0, 0);
        ok &= Check("empty scene does no recording", physics.Timings.RecordingMilliseconds, 0, 0);
        return ok;
    }

    private static bool WorkLists(HeadlessVulkan v)
    {
        bool ok = true;
        foreach (int count in new[] { 1, 31, 32, 33, 63, 64, 65, 129, 414 })
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Add(new FixedBody(TestMeshes.Box(new dvec3(10, 10, -0.5), new dvec3(50, 50, 1))));
            Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
            int side = (int)Math.Ceiling(Math.Sqrt(count));
            List<RigidBody> boxes = [];
            for (int i = 0; i < count; i++)
            {
                RigidBody box = new(cube) { Position = new dvec3(i % side + 0.5, i / side + 0.5, 0.5) };
                boxes.Add(box);
                physics.Add(box);
            }
            physics.Update(TimeStep);
            var lists = physics.DownloadRigidWorkLists();
            int errors = 0;
            int[] seen = new int[count];
            for (int bin = 0; bin < lists.Arguments.Length; bin++)
            {
                uvec4 args = lists.Arguments[bin];
                if (args.w > count || args.y != 1 || args.z != 1
                    || args.x != (bin == 16 ? Math.Min(args.w, 1u) : (bin < 8 ? (args.w + 1) / 2 : args.w)))
                {
                    errors++;
                    continue;
                }
                uint previous = 0;
                for (int j = 0; j < args.w; j++)
                {
                    uint r = lists.Work[bin * count + j];
                    if (r >= count)
                    {
                        errors++;
                        continue;
                    }
                    seen[r]++;
                    if (lists.Colors[r] != (bin == 16 ? 8 : bin % 8) || (j > 0 && r <= previous))
                        errors++;
                    previous = r;
                }
            }
            errors += seen.Count(n => n != 1);
            errors += boxes.Where((box, i) => box.SolverColorHint != lists.Colors[i]).Count();
            errors += physics.Statistics.SerialRigidBodyCount == lists.Colors.Count(c => c == 8) ? 0 : 1;
            foreach (GpuContact contact in physics.DownloadContacts())
            {
                uint a = contact.Header.y, b = contact.Header.z;
                if (a < count && b < count && a != b && lists.Colors[a] < 8 && lists.Colors[a] == lists.Colors[b])
                    errors++;
            }

            ok &= Check($"{count} bodies: complete work lists and independent colors", errors, 0, 0);
            if (count == 65 || count == 414)
            {
                // An unrelated spawn must seed refinement from each survivor's last color.
                // Disable continuous additions so the final graph is the refinement graph.
                uint[] expected = [.. boxes.Select(b => b.SolverColorHint), 0u];
                physics.Settings.ContinuousCollisionPasses = 0;
                physics.Add(new RigidBody(cube) { Position = new dvec3(100, 100, 10) });
                physics.Update(TimeStep);
                GpuContact[] contacts = physics.DownloadContacts();
                for (int round = 0; round < 4; round++)
                {
                    uint[] used = new uint[count + 1];
                    foreach (GpuContact contact in contacts)
                    {
                        uint a = contact.Header.y, b = contact.Header.z;
                        if (a < expected.Length && b < expected.Length && a != b)
                        {
                            uint lower = Math.Min(a, b), upper = Math.Max(a, b);
                            used[upper] |= 1u << (int)Math.Min(expected[lower], 31u);
                        }
                    }
                    for (int r = 0; r < expected.Length; r++)
                        expected[r] = Math.Min((uint)System.Numerics.BitOperations.TrailingZeroCount(~used[r]), 7u);
                }
                uint[] pruned = (uint[])expected.Clone();
                foreach (GpuContact contact in contacts)
                {
                    uint a = contact.Header.y, b = contact.Header.z;
                    if (a < expected.Length && b < expected.Length && a != b && expected[a] == expected[b])
                        pruned[Math.Max(a, b)] = 8;
                }
                uint[] actual = physics.DownloadRigidWorkLists().Colors;
                ok &= Check($"{count} bodies: topology rebuild uses retained color hints", actual.Where((c, r) => c != pruned[r]).Count(), 0, 0);
            }
        }
        return ok;
    }

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static IEnumerable<dvec3> WorldVertices(RigidBody body)
    {
        dmat3 rotation = body.Orientation.ToMat3;
        foreach (dvec3 v in body.Geometry.Vertices)
            yield return body.Position + rotation * v;
    }

    private static double MinZ(RigidBody body) => WorldVertices(body).Min(v => v.z);

    private static double MinZ(SoftBody body)
    {
        double min = double.MaxValue;
        foreach (dvec3 p in body.Positions)
            min = Math.Min(min, p.z);
        return min;
    }

    private static double MaxNodeSpeed(SoftBody body)
    {
        double max = 0;
        foreach (dvec3 v in body.Velocities)
            max = Math.Max(max, v.Length);
        return max;
    }

    /// <summary>Deepest a point of the set lies inside the soft body's closed surface, 0 if none is inside.</summary>
    private static double DeepestInside(SoftBody body, IEnumerable<dvec3> points)
    {
        double deepest = 0;
        foreach (dvec3 p in points)
        {
            if (TestMeshes.WindingNumber(body.Positions, body.SurfaceTriangles, p) > 0.5)
                deepest = Math.Max(deepest, TestMeshes.DistanceToSurface(body.Positions, body.SurfaceTriangles, p));
        }

        return deepest;
    }

    private static void DumpContacts(PhysicsSystem physics)
    {
        foreach (GpuContact c in physics.DownloadContacts())
        {
            Console.WriteLine
            (
                $"      type {c.Header.x >> 8} kinds {c.Header.x & 15}/{(c.Header.x >> 4) & 15} key {c.Key.x:X}/{c.Key.y} " +
                $"n ({c.Normal.x:F3},{c.Normal.y:F3},{c.Normal.z:F3}) c0 {c.Normal.w:F4} lambda ({c.Lambda.x:G3},{c.Lambda.y:G3},{c.Lambda.z:G3}) " +
                $"k ({c.Penalty.x:G3},{c.Penalty.y:G3}) armA ({c.OffsetA.x:F3},{c.OffsetA.y:F3},{c.OffsetA.z:F3}) mu {c.WeightsA.w:F2} " +
                $"nodesA ({(int)c.NodesA.x},{(int)c.NodesA.y},{(int)c.NodesA.z}) w ({c.WeightsA.x:F2},{c.WeightsA.y:F2},{c.WeightsA.z:F2}) nodesB ({(int)c.NodesB.x},{(int)c.NodesB.y},{(int)c.NodesB.z})"
            );
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static bool RigidRest(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1))) { Position = new dvec3(0, 0, 2) };
        physics.Add(box);

        double lowest = double.MaxValue;
        for (int step = 0; step < 240; step++)
        {
            physics.Update(TimeStep);
            if (step >= 120)
                lowest = Math.Min(lowest, MinZ(box));
            if (Environment.GetEnvironmentVariable("MYT_TRACE") != null && step >= 20 && step < 40)
            {
                Console.WriteLine($"   step {step}: z {box.Position.z:F4} v {box.LinearVelocity} contacts {physics.Statistics.ContactCount}");
                if (step >= 31 && step < 34)
                    DumpContacts(physics);
            }
        }

        if (Environment.GetEnvironmentVariable("MYT_PROFILE") != null)
            ProfileStep(physics);

        return Check("penetration at rest (m)", -lowest, -0.002, 0.005)
            & Check("center height", box.Position.z, 0.49, 0.505)
            & Check("speed at rest", box.LinearVelocity.Length + box.AngularVelocity.Length, 0, 0.05);
    }

    /// <summary>
    /// A 2 kg box with its mass properties given explicitly, dropped 2.5 m: the reports name
    /// the right bodies, catch the impact at the free-fall speed, and at rest their impulses
    /// add up to the box's weight.
    /// </summary>
    private static bool ContactReports(HeadlessVulkan v)
    {
        const double mass = 2.0;
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        FixedBody ground = new(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1)));
        physics.Add(ground);
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)), mass, dvec3.Zero, mass / 6.0 * dmat3.Identity)
        {
            Position = new dvec3(0, 0, 3)
        };
        physics.Add(box);

        bool bodiesRight = true;
        double impactSpeed = 0;
        double restingForce = 0;
        int restingSteps = 0;
        for (int step = 0; step < 240; step++)
        {
            physics.Update(TimeStep);

            double upward = 0;
            foreach (ContactReport contact in physics.ContactReports)
            {
                bodiesRight &= (contact.BodyA == box && contact.BodyB == ground) || (contact.BodyA == ground && contact.BodyB == box);
                double sign = contact.BodyA == box ? 1 : -1;
                upward += sign * (contact.NormalImpulse * contact.Normal + contact.FrictionImpulse).z;
                impactSpeed = Math.Max(impactSpeed, contact.ApproachSpeed);
            }

            if (step >= 180)
            {
                restingForce += upward / TimeStep;
                restingSteps++;
            }
        }

        double fallSpeed = Math.Sqrt(2 * 9.81 * 2.5);
        return Check("reports name the box and the ground", bodiesRight ? 1 : 0, 1, 1)
            & Check("impact speed (m/s)", impactSpeed, 0.85 * fallSpeed, 1.05 * fallSpeed)
            & Check("support at rest over weight", restingForce / restingSteps / (mass * 9.81), 0.97, 1.03)
            & Check("dropped reports", physics.Statistics.DroppedContactReports, 0, 0)
            & Check("center height", box.Position.z, 0.49, 0.505);
    }

    /// <summary>
    /// An upright 0.6 × 0.6 × 1.8 m box with its rotation fixed, standing on the ground: a
    /// 50 kg block hitting it near the top at 6 m/s and an impulse at its top corner push it
    /// along without tipping or turning it, and its momentum is the block's.
    /// </summary>
    private static bool FixedRotation(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(40, 40, 1))) { Friction = 0.0 });
        RigidBody column = new(TestMeshes.Box(new dvec3(0, 0, 0.9), new dvec3(0.6, 0.6, 1.8)), 80.0)
        {
            FixedRotation = true,
            Friction = 0.0
        };
        physics.Add(column);
        for (int step = 0; step < 30; step++)
            physics.Update(TimeStep);

        RigidBody block = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.4, 0.4, 0.4)), 50.0)
        {
            Position = new dvec3(-1.5, 0, 1.55),
            LinearVelocity = new dvec3(6, 0, 0),
            Friction = 0.0
        };
        physics.Add(block);

        double worstTilt = 0;
        double worstSpin = 0;
        double momentumAfterHit = 0;
        for (int step = 0; step < 60; step++)
        {
            physics.Update(TimeStep);
            worstTilt = Math.Max(worstTilt, 2.0 * Math.Acos(Math.Min(1.0, Math.Abs(column.Orientation.w))));
            worstSpin = Math.Max(worstSpin, column.AngularVelocity.Length);
            if (step == 20)
                momentumAfterHit = 80.0 * column.LinearVelocity.x + 50.0 * block.LinearVelocity.x;
        }

        double sideways = column.LinearVelocity.y;
        column.ApplyImpulse(new dvec3(0, 80, 0), column.Position + new dvec3(0.3, 0.3, 1.8));
        physics.Update(TimeStep);
        bool ok = Check("tilt while hit (rad)", worstTilt, 0, 1e-5)
            & Check("spin while hit (rad/s)", worstSpin, 0, 1e-5)
            & Check("momentum kept by the hit (kg m/s)", momentumAfterHit, 0.9 * 300.0, 1.02 * 300.0)
            & Check("pushed along (m)", column.Position.x, 0.3, 3.0)
            & Check("speed an impulse at its corner adds (m/s)", column.LinearVelocity.y - sideways, 0.99, 1.01)
            & Check("spin from it (rad/s)", column.AngularVelocity.Length, 0, 1e-6);

        for (int step = 0; step < 60; step++)
            physics.Update(TimeStep);
        return ok & Check("standing on its base (m)", MinZ(column), -0.005, 0.005);
    }

    /// <summary>
    /// A box whose gravity is scaled by 4 falls four times as far as one under the world's
    /// gravity in the same time, and rests on the ground as still, pressing on it with four
    /// times its weight in every step: the solver starts it where gravity takes it, not inside
    /// the ground, so it never hops off.
    /// </summary>
    private static bool GravityScaled(HeadlessVulkan v)
    {
        const double mass = 10.0;
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        RigidBody plain = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)), mass) { Position = new dvec3(-2, 0, 3) };
        RigidBody heavy = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)), mass) { Position = new dvec3(2, 0, 3), GravityScale = 4.0 };
        physics.Add(plain);
        physics.Add(heavy);

        for (int step = 0; step < 15; step++)
            physics.Update(TimeStep);
        double ratio = (3.0 - heavy.Position.z) / (3.0 - plain.Position.z);

        for (int step = 0; step < 120; step++)
            physics.Update(TimeStep);

        double support = 0;
        double fastest = 0;
        int unsupported = 0;
        for (int step = 0; step < 60; step++)
        {
            physics.Update(TimeStep);
            double upward = 0;
            foreach (ContactReport contact in physics.ContactReports.Where(c => c.BodyA == heavy || c.BodyB == heavy))
                upward += (contact.BodyA == heavy ? 1 : -1) * contact.NormalImpulse * contact.Normal.z;

            support += upward;
            unsupported += upward > 0 ? 0 : 1;
            fastest = Math.Max(fastest, heavy.LinearVelocity.Length);
        }

        return Check("fall over the plain box's", ratio, 3.95, 4.05)
            & Check("support at rest over its scaled weight", support / (60 * TimeStep * mass * 4 * 9.81), 0.97, 1.03)
            & Check("steps it was not held up", unsupported, 0, 0)
            & Check("speed at rest (m/s)", fastest, 0, 0.01)
            & Check("resting on its base (m)", MinZ(heavy), -0.005, 0.005);
    }

    private static bool RigidTiltedOnGrid(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Grid(5, 20, 0)));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)))
        {
            Position = new dvec3(0.13, 0.07, 2),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        physics.Add(box);

        double lowest = double.MaxValue;
        for (int step = 0; step < 300; step++)
        {
            physics.Update(TimeStep);
            lowest = Math.Min(lowest, MinZ(box));
        }

        dvec3 up = box.Orientation.ToMat3 * new dvec3(0, 0, 1);
        double alignment = Math.Max(Math.Abs(up.x), Math.Max(Math.Abs(up.y), Math.Abs(up.z)));
        return Check("deepest penetration (m)", -lowest, -1, 0.02)
            & Check("face down (axis alignment)", alignment, 0.995, 1.0)
            & Check("center height", box.Position.z, 0.48, 0.52)
            & Check("speed at rest", box.LinearVelocity.Length + box.AngularVelocity.Length, 0, 0.05);
    }

    private static bool RigidStack(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));

        Mesh3D cube = TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5));
        List<RigidBody> boxes = [];
        for (int i = 0; i < 5; i++)
        {
            RigidBody box = new(cube) { Position = new dvec3(0.01 * (i % 2), 0, 0.25 + i * 0.51) };
            boxes.Add(box);
            physics.Add(box);
        }

        for (int step = 0; step < 300; step++)
            physics.Update(TimeStep);

        RigidBody top = boxes[^1];
        double drift = new dvec2(top.Position.x - 0.0, top.Position.y).Length;
        double speed = boxes.Max(b => b.LinearVelocity.Length + b.AngularVelocity.Length);
        Console.WriteLine($"   contacts {physics.Statistics.ContactCount}");
        return Check("top box height", top.Position.z, 2.2, 2.27)
            & Check("top box drift (m)", drift, 0, 0.03)
            & Check("max speed", speed, 0, 0.05);
    }

    private static bool RigidOnRidge(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        Mesh3D ridgeMesh = TestMeshes.Ridge(1.0, 0.6, 6, 0);
        physics.Add(new FixedBody(ridgeMesh));

        // The box's bottom edges cross the ridge, so only edge-edge contacts can hold it up at first.
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)))
        {
            Position = new dvec3(0, 0, 1.4),
            Orientation = dquat.FromAxisAngle(Math.PI / 5, new dvec3(0, 0, 1))
        };
        physics.Add(box);

        double deepest = 0;
        for (int step = 0; step < 120; step++)
        {
            physics.Update(TimeStep);
            deepest = Math.Max(deepest, RidgeDepthInRigid(box, 0.6));
        }

        return Check("ridge depth inside box (m)", deepest, 0, 0.02);
    }

    /// <summary>Deepest the ridge apex line (x = 0, z = apex) reaches into the rigid box.</summary>
    private static double RidgeDepthInRigid(RigidBody box, double apex)
    {
        dmat3 inverse = box.Orientation.ToMat3.Transposed;
        double deepest = 0;
        for (int i = -100; i <= 100; i++)
        {
            dvec3 local = inverse * (new dvec3(0, i * 0.03, apex) - box.Position);
            double depth = Math.Min(0.5 - Math.Abs(local.x), Math.Min(0.5 - Math.Abs(local.y), 0.5 - Math.Abs(local.z)));
            deepest = Math.Max(deepest, depth);
        }

        return deepest;
    }

    private static bool SoftRest(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Grid(5, 20, 0)));
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(0.1, 0.05, 1.5), new dvec3(1, 1, 1), new ivec3(5, 5, 5), 1.0, 200, 0.005);
        physics.Add(jelly);

        double lowest = double.MaxValue;
        for (int step = 0; step < 300; step++)
        {
            physics.Update(TimeStep);
            if (step > 60)
                lowest = Math.Min(lowest, MinZ(jelly));
        }

        if (Environment.GetEnvironmentVariable("MYT_PROFILE") != null)
            ProfileStep(physics);

        return Check("node penetration (m)", -lowest, -0.002, 0.005)
            & Check("center height", jelly.Center.z, 0.3, 0.52)
            & Check("max node speed", MaxNodeSpeed(jelly), 0, 0.05);
    }

    private static bool SoftOnRidge(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        double apex = 0.8;
        physics.Add(new FixedBody(TestMeshes.Ridge(1.2, apex, 6, 0)));

        // Like the test scene: 0.4 m between nodes, so the ridge passes between them, and turned so
        // no soft edge runs parallel to it.
        SoftBody jelly = SoftBody.CreateLatticeBox(dvec3.Zero, new dvec3(2, 2, 2), new ivec3(6, 6, 6), 1.0, 100, 0.005);
        jelly.Translate(new dvec3(0.1, 0, 2.3));
        physics.Add(jelly);

        double deepest = 0;
        List<dvec3> apexLine = [.. Enumerable.Range(-60, 121).Select(i => new dvec3(0, i * 0.05, apex))];
        for (int step = 0; step < 240; step++)
        {
            physics.Update(TimeStep);
            if (step % 4 == 0)
                deepest = Math.Max(deepest, DeepestInside(jelly, apexLine));
        }

        Console.WriteLine($"   contacts {physics.Statistics.ContactCount}");
        return Check("ridge depth inside soft body (m)", deepest, 0, 0.02)
            & Check("soft body above ground", MinZ(jelly), -0.01, 10);
    }

    private static bool RigidOnSoft(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(0, 0, 0.5), new dvec3(1, 1, 1), new ivec3(5, 5, 5), 1.0, 400, 0.005);
        physics.Add(jelly);
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.4, 0.4, 0.4)), 0.5)
        {
            Position = new dvec3(0.05, -0.03, 1.6),
            Orientation = dquat.FromAxisAngle(0.4, new dvec3(1, 0, 0))
        };
        physics.Add(box);

        double deepest = 0;
        for (int step = 0; step < 240; step++)
        {
            physics.Update(TimeStep);
            if (step % 4 == 0)
                deepest = Math.Max(deepest, DeepestInside(jelly, WorldVertices(box)));
        }

        return Check("box corner depth inside soft body (m)", deepest, 0, 0.02)
            & Check("box resting on top", box.Position.z, 0.7, 1.3)
            & Check("box speed", box.LinearVelocity.Length, 0, 0.1);
    }

    private static bool FrictionIncline(HeadlessVulkan v)
    {
        bool ok = true;
        foreach ((double degrees, bool shouldSlide) in new[] { (15.0, false), (40.0, true) })
        {
            using PhysicsSystem physics = CreateSystem(v);
            double angle = degrees * Math.PI / 180;
            dquat tilt = dquat.FromAxisAngle(angle, new dvec3(0, 1, 0));
            physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1)), tilt.ToMat4));

            // Friction 0.5 on both: static if tan(angle) < 0.5, about 26.6 degrees.
            dvec3 normal = tilt.ToMat3 * new dvec3(0, 0, 1);
            RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5))) { Orientation = tilt, Position = normal * 0.26 };
            physics.Add(box);

            for (int step = 0; step < 30; step++)
                physics.Update(TimeStep);

            dvec3 start = box.Position;
            for (int step = 0; step < 120; step++)
                physics.Update(TimeStep);

            double travelled = (box.Position - start).Length;
            ok &= shouldSlide
                ? Check($"slide at {degrees} deg over 2 s (m)", travelled, 1.0, 100)
                : Check($"creep at {degrees} deg over 2 s (m)", travelled, 0, 0.01);
        }

        return ok;
    }

    /// <summary>A 20 × 20 m floor of 1 m squares whose triangles take one material where y &lt; 0 and another elsewhere.</summary>
    private static FixedBody HalvedFloor(SurfaceMaterial below, SurfaceMaterial above, dmat4? transform = null)
    {
        Mesh3D floor = TestMeshes.Grid(10, 20, 0);
        SurfaceMaterial[] surfaces = new SurfaceMaterial[floor.Faces.Length / 3];
        for (int t = 0; t < surfaces.Length; t++)
        {
            double y = 0.0;
            for (int k = 0; k < 3; k++)
                y += floor.Vertices[floor.Faces[3 * t + k]].y;
            surfaces[t] = y < 0.0 ? below : above;
        }

        return new FixedBody(floor, transform) { SurfaceMaterials = surfaces };
    }

    /// <summary>
    /// One incline at 35° (tan 0.7), its triangles grippy on one half and slippery on the other:
    /// a box of friction 1 holds on friction 1 and slides on 0.2 (a contact's √0.2 = 0.45).
    /// </summary>
    private static bool FrictionBySurface(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        dquat tilt = dquat.FromAxisAngle(35.0 * Math.PI / 180, new dvec3(0, 1, 0));
        physics.Add(HalvedFloor(new SurfaceMaterial(1.0, 0.0), new SurfaceMaterial(0.2, 0.0), tilt.ToMat4));

        dvec3 normal = tilt.ToMat3 * new dvec3(0, 0, 1);
        RigidBody[] boxes =
        [
            .. new[] { -3.0, 3.0 }.Select(y => new RigidBody(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5)))
            {
                Orientation = tilt,
                Position = new dvec3(0, y, 0) + normal * 0.26,
                Friction = 1.0
            })
        ];
        foreach (RigidBody box in boxes)
            physics.Add(box);

        for (int step = 0; step < 30; step++)
            physics.Update(TimeStep);

        dvec3[] start = [.. boxes.Select(b => b.Position)];
        for (int step = 0; step < 120; step++)
            physics.Update(TimeStep);

        return Check("creep on the grippy half over 2 s (m)", (boxes[0].Position - start[0]).Length, 0, 0.01)
            & Check("slide on the slippery half over 2 s (m)", (boxes[1].Position - start[1]).Length, 1.0, 100);
    }

    private static bool RigidBullet(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(2, 0, 0), new dvec3(0.05, 4, 4))));
        RigidBody bullet = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.1, 0.1, 0.1)), 0.1)
        {
            Position = new dvec3(-1, 0.3, 0.2),
            LinearVelocity = new dvec3(120, 0, 0)
        };
        physics.Add(bullet);

        double furthest = double.MinValue;
        for (int step = 0; step < 60; step++)
        {
            physics.Update(TimeStep);
            furthest = Math.Max(furthest, WorldVertices(bullet).Max(p => p.x));
            if (Environment.GetEnvironmentVariable("MYT_TRACE") != null && step < 6)
            {
                Console.WriteLine($"   step {step}: x {bullet.Position.x:F4} v {bullet.LinearVelocity.x:F3} w {bullet.AngularVelocity} contacts {physics.Statistics.ContactCount}");
                DumpContacts(physics);
            }
        }

        return Check("furthest point past wall face (m)", furthest - 1.975, -10, 0.01);
    }

    private static bool SoftBullet(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(2, 0, 0), new dvec3(0.05, 4, 4))));
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(-1, 0.2, 0.1), new dvec3(0.3, 0.3, 0.3), new ivec3(4, 4, 4), 0.2, 2000, 0.002);
        jelly.SetVelocity(new dvec3(40, 0, 0));
        physics.Add(jelly);

        // Only surface nodes collide; interior ones are held by the springs alone.
        HashSet<int> surface = [];
        foreach (uvec3 t in jelly.SurfaceTriangles)
            surface.UnionWith([(int)t.x, (int)t.y, (int)t.z]);

        double furthestSurface = double.MinValue;
        double furthest = double.MinValue;
        for (int step = 0; step < 60; step++)
        {
            physics.Update(TimeStep);
            for (int n = 0; n < jelly.NodeCount; n++)
            {
                furthest = Math.Max(furthest, jelly.Positions[n].x);
                if (surface.Contains(n))
                    furthestSurface = Math.Max(furthestSurface, jelly.Positions[n].x);
            }
        }

        return Check("furthest surface node past wall face (m)", furthestSurface - 1.975, -10, 0.01)
            & Check("furthest node past wall back (m)", furthest - 2.025, -10, 0);
    }

    private static bool Edits(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)), 2.0);
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(5, 0, 0), new dvec3(1, 1, 1), new ivec3(3, 3, 3), 1.0, 100, 0.005);
        physics.Add(box);
        physics.Add(jelly);
        physics.Update(TimeStep);

        box.ApplyImpulse(new dvec3(4, 0, 0));
        jelly.ApplyImpulse(new dvec3(0, 3, 0));
        physics.Update(TimeStep);
        bool ok = Check("box speed after impulse", box.LinearVelocity.x, 1.99, 2.01)
            & Check("soft speed after impulse", jelly.Velocities[0].y, 2.99, 3.01);

        box.Position = new dvec3(0, 0, 10);
        jelly.Translate(new dvec3(0, 0, -10));
        physics.Update(TimeStep);
        ok &= Check("box after teleport", box.Position.z, 9.99, 10.01)
            & Check("soft after translate", jelly.Center.z, -10.01, -9.98);

        RigidBody late = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1))) { Position = new dvec3(-5, 0, 0) };
        physics.Add(late);
        physics.Update(TimeStep);
        ok &= Check("box keeps velocity across a rebuild", box.LinearVelocity.x, 1.99, 2.01);

        physics.Remove(late);
        physics.Update(TimeStep);
        return ok & Check("rigid bodies after removal", physics.RigidBodies.Count, 1, 1);
    }

    private static bool SoftOnSoft(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        SoftBody bottom = SoftBody.CreateLatticeBox(new dvec3(0, 0, 0.5), new dvec3(1, 1, 1), new ivec3(4, 4, 4), 1.0, 400, 0.005);
        SoftBody top = SoftBody.CreateLatticeBox(dvec3.Zero, new dvec3(0.8, 0.8, 0.8), new ivec3(4, 4, 4), 0.5, 400, 0.005);
        top.Translate(new dvec3(0.15, -0.1, 1.8));
        physics.Add(bottom);
        physics.Add(top);

        double deepest = 0;
        for (int step = 0; step < 240; step++)
        {
            physics.Update(TimeStep);
            if (step % 4 == 0)
            {
                deepest = Math.Max(deepest, DeepestInside(bottom, top.Positions.ToArray()));
                deepest = Math.Max(deepest, DeepestInside(top, bottom.Positions.ToArray()));
            }
        }

        return Check("node depth inside the other body (m)", deepest, 0, 0.02)
            & Check("top body resting on the bottom one", top.Center.z, 1.1, 1.5)
            & Check("max node speed", Math.Max(MaxNodeSpeed(top), MaxNodeSpeed(bottom)), 0, 0.1);
    }

    private static bool RigidEdgeOnEdge(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1));

        // The lower box stands on an edge-like ridge of its own: rotated about x so its top is an
        // edge along x; the upper box comes down rotated about y, so its bottom edge crosses it.
        RigidBody lower = new(cube, 4.0) { Position = new dvec3(0, 0, 0.5) };
        RigidBody upper = new(cube)
        {
            Position = new dvec3(0.05, 0.02, 2.2),
            Orientation = dquat.FromAxisAngle(Math.PI / 4, new dvec3(0, 1, 0)) * dquat.FromAxisAngle(0.3, new dvec3(0, 0, 1))
        };
        physics.Add(lower);
        physics.Add(upper);

        double deepest = 0;
        for (int step = 0; step < 180; step++)
        {
            physics.Update(TimeStep);
            deepest = Math.Max(deepest, Math.Max(EdgeDepthInBox(upper, lower), EdgeDepthInBox(lower, upper)));
        }

        return Check("edge depth inside the other box (m)", deepest, 0, 0.02);
    }

    /// <summary>Deepest any point along the edges of box a lies inside unit box b.</summary>
    private static double EdgeDepthInBox(RigidBody a, RigidBody b)
    {
        dvec3[] corners = [.. WorldVertices(a)];
        dmat3 inverse = b.Orientation.ToMat3.Transposed;
        double deepest = 0;
        for (int i = 0; i < 8; i++)
        {
            for (int j = i + 1; j < 8; j++)
            {
                if (BitCount(i ^ j) != 1)
                    continue;

                for (int k = 0; k <= 20; k++)
                {
                    dvec3 local = inverse * (dvec3.Lerp(corners[i], corners[j], k / 20.0) - b.Position);
                    double depth = Math.Min(0.5 - Math.Abs(local.x), Math.Min(0.5 - Math.Abs(local.y), 0.5 - Math.Abs(local.z)));
                    deepest = Math.Max(deepest, depth);
                }
            }
        }

        return deepest;
    }

    private static int BitCount(int value) => System.Numerics.BitOperations.PopCount((uint)value);

    private static bool SoftFromMesh(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Grid(5, 20, 0)));
        SoftBody ball = SoftBody.CreateFromMesh(TestMeshes.Icosphere(new dvec3(0, 0, 1.5), 0.5, 2), 1.0, 300, 0.005);
        physics.Add(ball);

        double lowest = double.MaxValue;
        for (int step = 0; step < 300; step++)
        {
            physics.Update(TimeStep);
            if (step > 60)
                lowest = Math.Min(lowest, MinZ(ball));
        }

        return Check("node penetration (m)", -lowest, -0.002, 0.005)
            & Check("center height (not collapsed)", ball.Center.z, 0.3, 0.55)
            & Check("max node speed", MaxNodeSpeed(ball), 0, 0.05);
    }

    private static bool HardRope(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        const int links = 20;
        const double linkLength = 0.1;
        dvec3[] positions = [.. Enumerable.Range(0, links + 1).Select(i => new dvec3(i * linkLength, 0, 3))];
        double[] masses = [.. Enumerable.Repeat(0.05, links + 1)];
        Spring[] springs =
        [
            .. Enumerable.Range(0, links).Select
            (
                i => new Spring
                {
                    Node1 = (uint)i,
                    Node2 = (uint)(i + 1),
                    RestLength = linkLength,
                    Stiffness = double.PositiveInfinity,
                    Damping = 0.02,
                    Durability = double.MaxValue
                }
            )
        ];

        // A rope has no surface triangles; it only feels gravity and its own constraints here.
        SoftBody rope = new(positions, masses, springs, []);
        rope.Pin(0);
        physics.Add(rope);

        double worstStretch = 0;
        double restingStretch = 0;
        for (int step = 0; step < 900; step++)
        {
            physics.Update(TimeStep);
            for (int i = 0; i < links; i++)
            {
                double stretch = Math.Abs((rope.Positions[i + 1] - rope.Positions[i]).Length - linkLength) / linkLength;
                worstStretch = Math.Max(worstStretch, stretch);
                if (step == 899)
                    restingStretch = Math.Max(restingStretch, stretch);
            }
        }

        return Check("pinned end stays", (rope.Positions[0] - positions[0]).Length, 0, 1e-9)
            & Check("worst link stretch while swinging (fraction)", worstStretch, 0, 0.03)
            & Check("link stretch hanging (fraction)", restingStretch, 0, 0.001)
            & Check("free end below the pin (m)", 3 - rope.Positions[links].z, links * linkLength * 0.95, links * linkLength * 1.01);
    }

    private static bool Substeps(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Substeps = 4;
        physics.Settings.Iterations = 4;
        physics.Add(new FixedBody(TestMeshes.Grid(5, 20, 0)));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)))
        {
            Position = new dvec3(0, 0, 2),
            Orientation = dquat.FromAxisAngle(0.5, new dvec3(1, 0, 0))
        };
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(2, 0, 1.5), new dvec3(1, 1, 1), new ivec3(4, 4, 4), 1.0, 300, 0.005);
        physics.Add(box);
        physics.Add(jelly);

        for (int step = 0; step < 240; step++)
            physics.Update(TimeStep);

        return Check("box center height", box.Position.z, 0.49, 0.51)
            & Check("box speed", box.LinearVelocity.Length + box.AngularVelocity.Length, 0, 0.05)
            & Check("soft penetration (m)", -MinZ(jelly), -0.002, 0.005);
    }

    private static bool FarFromOrigin(HeadlessVulkan v)
    {
        dvec3 far = new(10000, -20000, 5000);
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(far + new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(1, 1, 1)))
        {
            Position = far + new dvec3(0, 0, 2),
            Orientation = dquat.FromAxisAngle(0.4, new dvec3(1, 1, 0).Normalized)
        };
        SoftBody jelly = SoftBody.CreateLatticeBox(far + new dvec3(2, 0, 1.5), new dvec3(1, 1, 1), new ivec3(4, 4, 4), 1.0, 300, 0.005);
        physics.Add(box);
        physics.Add(jelly);

        for (int step = 0; step < 240; step++)
            physics.Update(TimeStep);

        return Check("box center height", box.Position.z - far.z, 0.49, 0.51)
            & Check("box speed", box.LinearVelocity.Length + box.AngularVelocity.Length, 0, 0.05)
            & Check("soft penetration (m)", far.z - MinZ(jelly), -0.002, 0.005)
            & Check("soft max node speed", MaxNodeSpeed(jelly), 0, 0.05);
    }

    private static bool Pile(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, new dvec3(0.4, 0.4, 0.4));
        Random random = new(7);
        List<RigidBody> boxes = [];
        for (int i = 0; i < 64; i++)
        {
            RigidBody box = new(cube, 0.5)
            {
                Position = new dvec3((i % 4) * 0.5 - 0.75, (i / 4 % 4) * 0.5 - 0.75, 0.5 + i / 16 * 0.6),
                Orientation = dquat.FromAxisAngle(random.NextDouble() * Math.PI, new dvec3(random.NextDouble(), random.NextDouble(), 1).Normalized),
                LinearVelocity = new dvec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, 0)
            };
            boxes.Add(box);
            physics.Add(box);
        }

        // The columns of boxes are unstable and topple in a chaotic, run-dependent way; 15 s leaves
        // time for the avalanche to finish. Before that only explosions are a failure.
        Stopwatch watch = new();
        double total = 0;
        double fastest = 0;
        for (int step = 0; step < 900; step++)
        {
            watch.Restart();
            physics.Update(TimeStep);
            if (step >= 840)
                total += watch.Elapsed.TotalMilliseconds;
            if (step >= 120)
                fastest = Math.Max(fastest, boxes.Max(b => b.LinearVelocity.Length));

            if (Environment.GetEnvironmentVariable("MYT_TRACE") != null && step >= 240 && step % 10 == 0)
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    RigidBody b = boxes[i];
                    if (b.LinearVelocity.Length > 0.2 || b.AngularVelocity.Length > 1)
                        Console.WriteLine($"   step {step} box {i}: p {b.Position} v {b.LinearVelocity} w {b.AngularVelocity}");
                }
            }
        }

        Console.WriteLine($"   contacts {physics.Statistics.ContactCount}, {total / 60:F2} ms per step, {physics.Statistics.GpuMilliseconds:F2} ms on the GPU");
        if (Environment.GetEnvironmentVariable("MYT_PROFILE") != null)
            ProfileStep(physics);

        return Check("lowest box corner", boxes.Min(MinZ), -0.01, 10)
            & Check("fastest box after landing (no explosions; free fall from the top reaches 7.5)", fastest, 0, 8)
            & Check("max speed after settling", boxes.Max(b => b.LinearVelocity.Length), 0, 0.2)
            & Check("dropped contacts", physics.Statistics.DroppedContacts, 0, 0);
    }

    /// <summary>
    /// Repeatable scaling benchmark with gameplay contact reporting and chunk-sized fixed
    /// colliders. Separated cubes should all remain supported as the object count grows.
    /// MYT_BENCH_COUNTS=64,144,256 and MYT_BENCH_STEPS=120 override the workload.
    /// </summary>
    private static bool ManyRigid(HeadlessVulkan v)
    {
        int[] counts = Environment.GetEnvironmentVariable("MYT_BENCH_COUNTS") is string values
            ? values.Split(',').Select(int.Parse).ToArray() : [64, 144, 256];
        int steps = int.TryParse(Environment.GetEnvironmentVariable("MYT_BENCH_STEPS"), out int n) ? Math.Max(n, 1) : 120;
        bool ok = true;
        foreach (int count in counts)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.ReportContacts = true;
            physics.Settings.MaxContacts = 1 << 17;
            physics.Settings.MaxContactReports = 1 << 16;
            int side = (int)Math.Ceiling(Math.Sqrt(count));
            int tiles = (int)Math.Ceiling(side * 2.0 / 16.0) + 2;
            for (int x = -1; x < tiles - 1; x++)
            for (int y = -1; y < tiles - 1; y++)
                physics.Add(new FixedBody(TestMeshes.Box(new dvec3(x * 16 + 8, y * 16 + 8, -0.5), new dvec3(16, 16, 1))));

            Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
            RigidBody[] boxes = new RigidBody[count];
            for (int i = 0; i < count; i++)
            {
                boxes[i] = new RigidBody(cube) { Position = new dvec3(i % side * 2 + 0.5, i / side * 2 + 0.5, 0.5) };
                physics.Add(boxes[i]);
            }

            for (int step = 0; step < 30; step++)
                physics.Update(TimeStep);

            BenchmarkSamples samples = new(steps);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int step = 0; step < steps; step++)
            {
                long start = Stopwatch.GetTimestamp();
                physics.Update(TimeStep);
                samples.Record(physics, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            samples.Print($"{count} rigid", physics, allocated);
            if (Environment.GetEnvironmentVariable("MYT_PROFILE") != null)
                ProfileStep(physics);

            ok &= Check("lowest box corner", boxes.Min(MinZ), -0.005, 0.005);
            ok &= Check("fastest resting box", boxes.Max(b => b.LinearVelocity.Length + b.AngularVelocity.Length), 0, 0.05);
            ok &= Check("reported support impulse / weight", physics.ContactReports.Sum(c => Math.Abs(c.NormalImpulse * c.Normal.z)) / (count * 9.81 * TimeStep), 0.98, 1.02);
            ok &= Check("dropped contacts", physics.Statistics.DroppedContacts, 0, 0);
            ok &= Check("dropped references", physics.Statistics.DroppedContactReferences, 0, 0);
            ok &= Check("dropped reports", physics.Statistics.DroppedContactReports, 0, 0);
        }
        return ok;
    }

    /// <summary>A packed stack exercises thousands of contacts with the game's report capacities.</summary>
    private static bool CrowdedRigid(HeadlessVulkan v)
    {
        int count = int.TryParse(Environment.GetEnvironmentVariable("MYT_BENCH_COUNT"), out int n) ? Math.Max(n, 1) : 414;
        int layers = int.TryParse(Environment.GetEnvironmentVariable("MYT_BENCH_LAYERS"), out int l) ? Math.Clamp(l, 1, count) : 1;
        int steps = int.TryParse(Environment.GetEnvironmentVariable("MYT_BENCH_STEPS"), out int s) ? Math.Max(s, 1) : 300;
        int columns = (count + layers - 1) / layers;
        int side = (int)Math.Ceiling(Math.Sqrt(columns));
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        physics.Settings.MaxContacts = 1 << 17;
        physics.Settings.MaxContactReports = 1 << 16;
        FixedBody floor = new(TestMeshes.Box(new dvec3(side / 2.0, side / 2.0, -0.5), new dvec3(side + 4, side + 4, 1)));
        physics.Add(floor);
        Mesh3D cube = TestMeshes.Box(dvec3.Zero, dvec3.Ones);
        RigidBody[] boxes = new RigidBody[count];
        for (int i = 0; i < count; i++)
        {
            int column = i % columns;
            boxes[i] = new RigidBody(cube) { Position = new dvec3(column % side + 0.5, column / side + 0.5, i / columns + 0.5) };
            physics.Add(boxes[i]);
        }
        for (int i = 0; i < 180; i++)
            physics.Update(TimeStep);
        BenchmarkSamples samples = new(steps);
        double support = 0, signedNormal = 0, signedFriction = 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < steps; i++)
        {
            long start = Stopwatch.GetTimestamp();
            physics.Update(TimeStep);
            samples.Record(physics, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            // A stack's per-step force fluctuates as finite solver iterations settle it.
            // Check sustained support over the measured interval, rather than one sample.
            for (int j = 0; j < physics.ContactReports.Count; j++)
            {
                ContactReport contact = physics.ContactReports[j];
                if (contact.BodyA == floor || contact.BodyB == floor)
                {
                    support += Math.Abs(contact.NormalImpulse * contact.Normal.z);
                    double sign = contact.BodyA == floor ? -1 : 1;
                    signedNormal += sign * contact.NormalImpulse * contact.Normal.z;
                    signedFriction += sign * contact.FrictionImpulse.z;
                }
            }
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        samples.Print($"{count} packed rigid, {layers} layers", physics, allocated);
        Console.WriteLine($"   signed floor impulse / weight: normal {signedNormal / (steps * count * 9.81 * TimeStep):F6}, friction {signedFriction / (steps * count * 9.81 * TimeStep):F6}");
        if (Environment.GetEnvironmentVariable("MYT_PROFILE") != null)
            ProfileStep(physics);
        return Check("lowest box corner", boxes.Min(MinZ), -0.005, 0.005)
            & Check("fastest resting box", boxes.Max(b => b.LinearVelocity.Length + b.AngularVelocity.Length), 0, 0.1)
            & Check("mean floor support / total weight", support / (steps * count * 9.81 * TimeStep), layers == 1 ? 0.98 : 0.75, layers == 1 ? 1.02 : 1.25)
            & Check("dropped contacts", physics.Statistics.DroppedContacts, 0, 0)
            & Check("dropped reports", physics.Statistics.DroppedContactReports, 0, 0);
    }

    /// <summary>More than 64 nearby bodies must fall back without losing any collisions.</summary>
    private static bool BodyCandidatesOverflow(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        const int floors = 80;
        Mesh3D floor = TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1));
        for (int i = 0; i < floors; i++)
            physics.Add(new FixedBody(floor));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones)) { Position = new dvec3(0, 0, 0.5) };
        physics.Add(box);
        for (int i = 0; i < 60; i++)
            physics.Update(TimeStep);
        int touched = physics.ContactReports.Select(c => c.BodyA is FixedBody ? c.BodyA : c.BodyB).Distinct().Count();
        bool ok = Check("all overlapping colliders reported after candidate overflow", touched, floors, floors)
            & Check("box supported", MinZ(box), -0.005, 0.005)
            & Check("dropped contacts", physics.Statistics.DroppedContacts, 0, 0)
            & Check("dropped reports", physics.Statistics.DroppedContactReports, 0, 0);
        box.Position = new dvec3(100, 100, 20);
        physics.Update(TimeStep);
        return ok & Check("candidate list refreshed after teleport", physics.Statistics.ContactCount, 0, 0)
            & Check("reports cleared after teleport", physics.ContactReports.Count, 0, 0);
    }

    /// <summary>Readback refreshes empty headers, preserves substep reports and reports overflow.</summary>
    private static bool ReportReadback(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.ReportContacts = true;
        physics.Settings.Substeps = 3;
        physics.Settings.MaxContactReports = 1;
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones)) { Position = new dvec3(0, 0, 0.5) };
        physics.Add(box);
        physics.Settings.Gravity = dvec3.Zero;
        physics.Update(TimeStep);
        bool ok = Check("empty report count", physics.ContactReports.Count, 0, 0);
        ok &= Check("empty dropped count", physics.Statistics.DroppedContactReports, 0, 0);

        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        physics.Settings.Gravity = new dvec3(0, 0, -9.81);
        for (int i = 0; i < 60; i++)
            physics.Update(TimeStep);
        ok &= Check("reports clamped to capacity", physics.ContactReports.Count, 1, 1);
        ok &= Check("overflow counted across substeps", physics.Statistics.DroppedContactReports, 1, 1000);
        ok &= Check("report substep duration", physics.ContactReports[0].Duration, TimeStep / 3 - 1e-10, TimeStep / 3 + 1e-10);

        physics.Settings.ReportContacts = false;
        physics.Update(TimeStep);
        ok &= Check("reporting off clears CPU reports", physics.ContactReports.Count, 0, 0);
        physics.Settings.ReportContacts = true;
        box.Position = new dvec3(0, 0, 20);
        physics.Update(TimeStep);
        ok &= Check("empty step clears previous reports", physics.ContactReports.Count, 0, 0);
        ok &= Check("empty step clears previous overflow", physics.Statistics.DroppedContactReports, 0, 0);
        return ok;
    }

    private static bool Bounce(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5))) { Position = new dvec3(0, 0, 2.25), Restitution = 0.8 };
        physics.Add(box);

        double highestAfterBounce = 0;
        bool bounced = false;
        for (int step = 0; step < 180; step++)
        {
            physics.Update(TimeStep);
            if (Environment.GetEnvironmentVariable("MYT_TRACE") != null && step >= 34 && step < 42)
                Console.WriteLine($"   step {step}: bottom {MinZ(box):F4} v {box.LinearVelocity.z:F3} w {box.AngularVelocity.Length:F3} contacts {physics.Statistics.ContactCount}");
            if (box.LinearVelocity.z > 0.5)
                bounced = true;
            if (bounced)
                highestAfterBounce = Math.Max(highestAfterBounce, MinZ(box));
        }

        // Dropped from 2 m with restitution 0.8: ideally 0.8² × 2 = 1.28 m. The bounce uses the speed at
        // the start of the impact step, slightly below the true impact speed, so it peaks a bit lower.
        return Check("first bounce height (m)", highestAfterBounce, 0.9, 1.4);
    }

    /// <summary>
    /// Boxes of restitution 0.8 dropped 2 m on a floor whose triangles have restitution 0 on one
    /// half and 1 on the other, mixed by the geometric mean: the first lands dead, the second
    /// bounces at √0.8 of its speed, ideally 0.8 × 2 = 1.6 m high.
    /// </summary>
    private static bool BounceBySurface(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.RestitutionMixing = RestitutionMixing.GeometricMean;
        physics.Add(HalvedFloor(new SurfaceMaterial(0.5, 0.0), new SurfaceMaterial(0.5, 1.0)));
        RigidBody[] boxes =
        [
            .. new[] { -3.0, 3.0 }.Select(y => new RigidBody(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5)))
            {
                Position = new dvec3(0, y, 2.25),
                Restitution = 0.8
            })
        ];
        foreach (RigidBody box in boxes)
            physics.Add(box);

        double[] fastestUp = new double[boxes.Length];
        double[] highestAfterBounce = new double[boxes.Length];
        for (int step = 0; step < 180; step++)
        {
            physics.Update(TimeStep);
            for (int i = 0; i < boxes.Length; i++)
            {
                fastestUp[i] = Math.Max(fastestUp[i], boxes[i].LinearVelocity.z);
                if (fastestUp[i] > 0.5)
                    highestAfterBounce[i] = Math.Max(highestAfterBounce[i], MinZ(boxes[i]));
            }
        }

        return Check("fastest rise on the soaking half (m/s)", fastestUp[0], 0, 0.3)
            & Check("first bounce on the elastic half (m)", highestAfterBounce[1], 1.1, 1.75);
    }

    /// <summary>
    /// A 10 kg box at 5 m/s hits a 1 kg box at rest, restitution 0.5, without gravity: they part
    /// at half the speed they met at, momentum kept, so the heavy one goes on at
    /// (10 − 0.5)/11 × 5 = 4.32 m/s and the light one leaves at 1.5 × 10/11 × 5 = 6.82 m/s,
    /// rather than the heavy one bouncing back off the light one as off a wall. At the default
    /// 10 iterations the solver itself loses about a quarter of the momentum in such a hit,
    /// bounce or not, so this runs at 40, where it keeps 99%.
    /// </summary>
    private static bool BounceBetweenMasses(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        physics.Settings.Gravity = dvec3.Zero;
        physics.Settings.Iterations = 40;
        RigidBody heavy = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5)), 10.0)
        {
            Position = new dvec3(-2, 0, 0),
            LinearVelocity = new dvec3(5, 0, 0),
            Restitution = 0.5
        };
        RigidBody light = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.5, 0.5, 0.5)), 1.0) { Restitution = 0.5 };
        physics.Add(heavy);
        physics.Add(light);

        for (int step = 0; step < 60; step++)
            physics.Update(TimeStep);

        double momentum = 10.0 * heavy.LinearVelocity.x + 1.0 * light.LinearVelocity.x;
        return Check("heavy box's speed after (m/s)", heavy.LinearVelocity.x, 4.0, 4.6)
            & Check("light box's speed after (m/s)", light.LinearVelocity.x, 6.4, 7.2)
            & Check("momentum after, of before", momentum / 50.0, 0.98, 1.02);
    }

    private static bool TestMap(HeadlessVulkan v)
    {
        Mesh3D map = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "testMap.ply"));
        Mesh3D cube = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "cube.ply"));
        double floor = map.Vertices.Min(p => p.z);

        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(map));
        RigidBody box = new(cube)
        {
            Position = new dvec3(0, 0, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(3, 0, 6), new dvec3(2, 2, 2), new ivec3(6, 6, 6), 1.0, 100, 0.005);
        physics.Add(box);
        physics.Add(jelly);

        Stopwatch watch = new();
        double slowest = 0;
        for (int step = 0; step < 600; step++)
        {
            physics.Profile = step == 500;
            watch.Restart();
            physics.Update(0.01);
            if (step > 0)
                slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);

            foreach (IGrouping<string, (string Phase, double Milliseconds)> phase in physics.LastProfile.GroupBy(p => p.Phase))
                Console.WriteLine($"   gpu {phase.Key,-22} {phase.Sum(p => p.Milliseconds),8:F3} ms total, {phase.Count()} x");

            if (step % 100 == 0 || step == 599)
            {
                Console.WriteLine
                (
                    $"   step {step}: box z {box.Position.z:F3}, soft z [{MinZ(jelly):F3}, {jelly.Center.z:F3}], " +
                    $"contacts {physics.Statistics.ContactCount}, {watch.Elapsed.TotalMilliseconds:F2} ms"
                );
            }
        }

        return Check("box above the floor", MinZ(box), floor - 0.01, 100)
            & Check("soft body above the floor", MinZ(jelly), floor - 0.01, 100)
            & Check("dropped contacts", physics.Statistics.DroppedContacts, 0, 0)
            & Check("slowest step (ms)", slowest, 0, 100);
    }

    /// <summary>
    /// Blender's cone mesh, whose tip is a ring of near-copies of one vertex with zero-area
    /// triangles between them, with a soft box dropped onto it. Unwelded, those slivers made
    /// contacts with random normals that shook both bodies and pulled the cone through the ledge
    /// it lands on.
    /// </summary>
    private static bool ConeUnderSoft(HeadlessVulkan v)
    {
        Mesh3D map = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "testMap.ply"));
        Mesh3D cone = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "cone.ply"));

        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(map));
        RigidBody body = new(cone)
        {
            Position = new dvec3(0, 0, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(1, 0, 20), new dvec3(2, 2, 2), new ivec3(6, 6, 6), 1.0, 100, 0.005);
        physics.Add(body);
        physics.Add(jelly);

        // The box lands at about step 200. It may slide off the cone, which then rolls around its
        // tip for a long while, so this checks for sinking and violence rather than rest.
        double ledge = 0;
        double sinking = 0;
        double fastest = 0;
        for (int step = 0; step < 1000; step++)
        {
            physics.Update(0.01);
            if (step == 300)
                ledge = MinZ(body);
            if (step > 300)
                sinking = Math.Max(sinking, ledge - MinZ(body));
            if (step >= 500)
                fastest = Math.Max(fastest, Math.Max(body.LinearVelocity.Length, MaxNodeSpeed(jelly)));

            if (step % 100 == 0)
            {
                Console.WriteLine
                (
                    $"   step {step}: cone z {body.Position.z:F3}, soft z {jelly.Center.z:F3}, " +
                    $"speeds {body.LinearVelocity.Length:F3} / {MaxNodeSpeed(jelly):F3}, contacts {physics.Statistics.ContactCount}"
                );
            }
        }

        return Check("cone sinking into the ledge (m)", sinking, 0, 0.01)
            & Check("fastest cone or soft node after landing (m/s)", fastest, 0, 5);
    }

    /// <summary>
    /// A soft body made straight from Blender's cone, whose tip is a ring of near-copies of one
    /// vertex with zero-area triangles between them. Soft surfaces skip the welding rigid and
    /// fixed meshes get, so these slivers reach the contact shader. The cone hangs tip down from
    /// its pinned rim, the tip a millimetre above the floor and inside the contact margin.
    /// </summary>
    private static bool RawSoftCone(HeadlessVulkan v)
    {
        Mesh3D cone = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "cone.ply"));
        dquat tipDown = dquat.FromAxisAngle(-Math.PI / 2, new dvec3(1, 0, 0));
        dvec3 offset = new(0, 0, 1.001);

        Dictionary<dvec3, uint> lookup = [];
        List<dvec3> positions = [];
        uint Node(ulong vertex)
        {
            dvec3 p = cone.Vertices[vertex];
            if (!lookup.TryGetValue(p, out uint node))
            {
                node = (uint)positions.Count;
                lookup[p] = node;
                positions.Add(offset + tipDown * p);
            }

            return node;
        }

        uvec3[] triangles = new uvec3[cone.Faces.Length / 3];
        for (int t = 0; t < triangles.Length; t++)
            triangles[t] = new uvec3(Node(cone.Faces[3 * t]), Node(cone.Faces[3 * t + 1]), Node(cone.Faces[3 * t + 2]));

        uint center = (uint)positions.Count;
        positions.Add(offset + tipDown * new dvec3(0, -0.5, 0));

        HashSet<(uint, uint)> joined = [];
        List<Spring> springs = [];
        void Join(uint a, uint b)
        {
            if (a == b || !joined.Add(a < b ? (a, b) : (b, a)))
                return;

            springs.Add(new Spring
            {
                Node1 = a,
                Node2 = b,
                RestLength = (positions[(int)a] - positions[(int)b]).Length,
                Stiffness = 2000,
                Damping = 0.005,
                Durability = double.MaxValue
            });
        }

        // Edges, plus the two far corners across every edge against folding, as CreateFromMesh does.
        Dictionary<(uint, uint), uint> opposite = [];
        foreach (uvec3 t in triangles)
        {
            for (int k = 0; k < 3; k++)
            {
                uint a = t[k];
                uint b = t[(k + 1) % 3];
                uint c = t[(k + 2) % 3];
                Join(a, b);

                (uint, uint) edge = a < b ? (a, b) : (b, a);
                if (opposite.TryGetValue(edge, out uint other))
                    Join(c, other);
                else
                    opposite[edge] = c;
            }
        }

        for (uint node = 0; node < center; node++)
            Join(node, center);

        double[] masses = new double[positions.Count];
        Array.Fill(masses, 5.0 / positions.Count);
        SoftBody jelly = new([.. positions], masses, [.. springs], triangles);
        for (int node = 0; node < center; node++)
        {
            if (positions[node].z > offset.z + 0.99)
                jelly.Pin(node);
        }

        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(20, 20, 1))));
        physics.Add(jelly);

        double lowest = double.MaxValue;
        double fastest = 0;
        for (int step = 0; step < 300; step++)
        {
            physics.Update(TimeStep);
            lowest = Math.Min(lowest, MinZ(jelly));
            fastest = Math.Max(fastest, MaxNodeSpeed(jelly));
            if (step % 50 == 0)
                Console.WriteLine($"   step {step}: tip z {MinZ(jelly):F4}, max node v {MaxNodeSpeed(jelly):F3}, contacts {physics.Statistics.ContactCount}");
        }

        return Check("tip below the floor (m)", -lowest, -0.01, 0.005)
            & Check("fastest node (m/s)", fastest, 0, 0.5)
            & Check("max node speed at rest", MaxNodeSpeed(jelly), 0, 0.05);
    }

    /// <summary>
    /// The Tester's scene: a 327k-triangle sphere dropped on the test map, then a soft box dropped
    /// onto the sphere. Every soft node under the box touches hundreds of the sphere's vertices,
    /// so this checks that dense contact stays fast enough for real time and does not explode.
    /// The map is uneven, so the ball may keep rolling.
    /// </summary>
    private static bool DenseSphereOnMap(HeadlessVulkan v)
    {
        Mesh3D map = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "testMap.ply"));
        Mesh3D sphere = Mesh3D.Loader.Load(Path.Combine(_assetDirectory, "sphere.ply"));

        using PhysicsSystem physics = CreateSystem(v);
        physics.Add(new FixedBody(map));
        RigidBody ball = new(sphere)
        {
            Position = new dvec3(0, 0, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        SoftBody jelly = SoftBody.CreateLatticeBox(new dvec3(1, 0, 20), new dvec3(2, 2, 2), new ivec3(6, 6, 6), 1.0, 100, 0.005);
        physics.Add(ball);
        physics.Add(jelly);

        Stopwatch watch = Stopwatch.StartNew();
        physics.Update(0.01);
        Console.WriteLine($"   first update (build) {watch.Elapsed.TotalMilliseconds:F0} ms");

        double floor = map.Vertices.Min(p => p.z);
        double slowest = 0;
        double slowestGpu = 0;
        double total = 0;
        double fastest = 0;
        uint dropped = 0;
        for (int step = 1; step < 600; step++)
        {
            physics.Profile = Environment.GetEnvironmentVariable("MYT_PROFILE") != null && step % 100 == 0;
            watch.Restart();
            physics.Update(0.01);
            double elapsed = watch.Elapsed.TotalMilliseconds;
            slowest = Math.Max(slowest, elapsed);
            slowestGpu = Math.Max(slowestGpu, physics.Statistics.GpuMilliseconds);
            total += elapsed;
            dropped = Math.Max(dropped, physics.Statistics.DroppedContacts);
            // The ball lands at about step 100 and the box on it at about step 190. The box often
            // tumbles off the rolling ball later and falls to the lower floor at up to ~11 m/s.
            if (step >= 300)
                fastest = Math.Max(fastest, Math.Max(ball.LinearVelocity.Length, MaxNodeSpeed(jelly)));

            foreach (IGrouping<string, (string Phase, double Milliseconds)> phase in physics.LastProfile.GroupBy(p => p.Phase))
                Console.WriteLine($"   gpu {phase.Key,-22} {phase.Sum(p => p.Milliseconds),8:F3} ms total, {phase.Count()} x");

            if (step % 50 == 0)
            {
                Console.WriteLine
                (
                    $"   step {step}: ball z {ball.Position.z:F3} v {ball.LinearVelocity.Length:F3}, soft z {jelly.Center.z:F3}, " +
                    $"contacts {physics.Statistics.ContactCount}, pairs {physics.DownloadPairCount()}, " +
                    $"{elapsed:F2} ms ({physics.Statistics.GpuMilliseconds:F2} on the GPU)"
                );
            }
        }

        Console.WriteLine($"   average {total / 599:F2} ms per step, slowest {slowest:F2} ms, slowest on the GPU {slowestGpu:F2} ms");
        return Check("ball above the floor", MinZ(ball), floor - 0.01, 100)
            & Check("soft body above the floor", MinZ(jelly), floor - 0.01, 100)
            & Check("fastest ball or soft node after landing (m/s)", fastest, 0, 15)
            & Check("most dropped contacts in a step", dropped, 0, 0)
            & Check("slowest step (ms)", slowest, 0, 33);
    }
}
