using GlmSharp;
using MYT.Kit.Vulkan;

namespace PhysicsTests;

internal static partial class Program
{
    private static bool NotchedWall(HeadlessVulkan v)
    {
        bool ok = true;
        foreach (double direction in new[] { -1.0, 1.0 })
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.Gravity = dvec3.Zero;
            physics.Settings.ReportContacts = true;
            List<ivec3> wall = [];
            for (int y = -4; y <= 4; y++)
            for (int z = 0; z < 2; z++)
                if (y != 0 || z != 0)
                    wall.Add(new ivec3(0, y, z));
            physics.Add(new FixedBody(TestMeshes.Voxels(wall)) { Friction = 0 });
            RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.6, 0.6, 1.8)), 80)
            {
                Position = new dvec3(-0.2995, 0.5 - direction * 2, 0.9),
                FixedRotation = true,
                Friction = 0,
                LinearVelocity = new dvec3(0, direction * 3, 0)
            };
            physics.Add(box);
            double opposition = 0;
            for (int step = 0; step < 80; step++)
            {
                physics.Update(TimeStep);
                opposition += physics.ContactReports.Sum(c => c.NormalImpulse * Math.Max(0,
                    -direction * (c.BodyA == box ? c.Normal.y : -c.Normal.y)));
            }
            ok &= Check($"direction {direction}: distance along notched wall", direction * (box.Position.y - 0.5) + 2, 3.98, 4.02);
            ok &= Check("opposing impulse at the hole", opposition, 0, 0.02);
        }
        return ok;
    }

    // A spinning box dropped onto a floor lands on one corner and swings the others down. Until
    // it lands, such a corner is not its body's lowest point, yet it must get its contact.
    private static bool TumblingDrop(HeadlessVulkan v)
    {
        int through = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(4, 4, 1))));
            Random random = new(seed);
            RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(0.25)), 1)
            {
                Position = new dvec3(0, 0, 0.5 + random.NextDouble()),
                Orientation = dquat.FromAxisAngle(random.NextDouble() * Math.PI, new dvec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5).Normalized),
                AngularVelocity = new dvec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5) * 4,
                Friction = 0.7
            };
            physics.Add(box);
            double lowest = 0;
            for (int step = 0; step < 180; step++)
            {
                physics.Update(TimeStep);
                lowest = Math.Min(lowest, MinZ(box));
            }
            through += lowest < -0.005 ? 1 : 0;
        }
        return Check("tumbling boxes sunk into the floor", through, 0, 0);
    }

    // Boxes dropped onto a checkerboard of raised blocks, every one of which meets its diagonal
    // neighbours along shared edges and corners (see nonmanifold-topology). No point of a box may
    // end up inside a block by more than an impact's transient penetration.
    private static bool CheckerboardDrop(HeadlessVulkan v)
    {
        List<ivec3> cells = [];
        for (int x = -4; x < 4; x++)
        for (int y = -4; y < 4; y++)
        {
            cells.Add(new ivec3(x, y, -1));
            if (((x + y) & 1) == 0)
                cells.Add(new ivec3(x, y, 0));
        }
        HashSet<ivec3> solid = [.. cells];
        double deepest = 0;
        for (int seed = 0; seed < 4; seed++)
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Add(new FixedBody(TestMeshes.Voxels(cells)));
            Random random = new(seed);
            List<(RigidBody Body, double Size)> boxes = [];
            for (int i = 0; i < 12; i++)
            {
                double size = i % 3 == 0 ? 1.4 : 0.45;
                RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(size)), 1)
                {
                    Position = new dvec3(random.NextDouble() * 6 - 3, random.NextDouble() * 6 - 3, 3 + i * 0.5),
                    Orientation = dquat.FromAxisAngle(random.NextDouble() * Math.PI, new dvec3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5).Normalized),
                    Friction = 0.6
                };
                boxes.Add((box, size));
                physics.Add(box);
            }

            for (int step = 0; step < 240; step++)
            {
                physics.Update(TimeStep);
                foreach ((RigidBody box, double size) in boxes)
                {
                    dmat3 rotation = box.Orientation.ToMat3;
                    for (int k = 0; k < 125; k++)
                    {
                        dvec3 local = new dvec3(k % 5, k / 5 % 5, k / 25) / 4.0 - 0.5;
                        if (Math.Abs(local.x) < 0.5 && Math.Abs(local.y) < 0.5 && Math.Abs(local.z) < 0.5)
                            continue;
                        dvec3 p = box.Position + rotation * (local * size);
                        ivec3 cell = new((int)Math.Floor(p.x), (int)Math.Floor(p.y), (int)Math.Floor(p.z));
                        if (solid.Contains(cell))
                            deepest = Math.Max(deepest, Math.Min(Math.Min(p.x - cell.x, cell.x + 1 - p.x), Math.Min(Math.Min(p.y - cell.y, cell.y + 1 - p.y), Math.Min(p.z - cell.z, cell.z + 1 - p.z))));
                    }
                }
            }
        }
        return Check("deepest box point inside a block (m)", deepest, 0, 0.02);
    }

    // Two blocks that touch only along an edge, and two that touch only at a corner, as diagonal
    // neighbours in a voxel map do: four triangles share the edge and two fans share the corner.
    // Every edge must join two triangles of one block, and each block's corner must be a vertex of
    // its own, or the normal cones of those features belong to neither block.
    private static bool NonManifoldTopology(HeadlessVulkan v)
    {
        bool ok = true;
        foreach ((string name, ivec3 other) in new[] { ("edge", new ivec3(1, 1, 0)), ("corner", new ivec3(1, 1, 1)) })
        {
            FixedBody body = new(TestMeshes.Voxels([ivec3.Zero, other]));
            CollisionGeometry geometry = new([body], [], [], 0);
            dvec3 Centroid(uint t)
            {
                GpuTriangle triangle = geometry.Triangles[(int)t];
                return (geometry.VertexWorld[(int)triangle.Vertices.x].xyz + geometry.VertexWorld[(int)triangle.Vertices.y].xyz + geometry.VertexWorld[(int)triangle.Vertices.z].xyz) / 3.0;
            }
            bool InFirst(dvec3 p) => p.x + p.y + (other.z == 0 ? 0.0 : p.z) < (other.z == 0 ? 2.0 : 3.0);

            int open = 0, mixed = 0;
            foreach (uvec4 edge in geometry.Edges)
            {
                if (edge.w == CollisionGeometry.NoBlock)
                    open++;
                else if (InFirst(Centroid(edge.z)) != InFirst(Centroid(edge.w)))
                    mixed++;
            }
            int sharedVertices = 0;
            for (int i = 0; i < geometry.VertexWorld.Count; i++)
            {
                bool first = false, second = false;
                for (int t = 0; t < geometry.Triangles.Count; t++)
                {
                    uvec4 vertices = geometry.Triangles[t].Vertices;
                    if (vertices.x != i && vertices.y != i && vertices.z != i)
                        continue;
                    if (InFirst(Centroid((uint)t)))
                        first = true;
                    else
                        second = true;
                }
                sharedVertices += first && second ? 1 : 0;
            }
            ok &= Check($"touching at {(name == "edge" ? "an" : "a")} {name}: open edges", open, 0, 0);
            ok &= Check("edges joining the two blocks", mixed, 0, 0);
            ok &= Check("vertices shared by the two blocks", sharedVertices, 0, 0);
            ok &= Check("vertices", geometry.VertexWorld.Count, 16, 16);
        }
        return ok;
    }

    // The same geometry as small dropped block items. Check overlap throughout impact,
    // not just the final positions, so passing through and emerging cannot pass the test.
    private static bool SmallBoxPile(HeadlessVulkan v)
    {
        using PhysicsSystem physics = CreateSystem(v);
        const double size = 0.25;
        physics.Add(new FixedBody(TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(4, 4, 1))));
        List<RigidBody> boxes = [];
        for (int i = 0; i < 8; i++)
        {
            RigidBody box = new(TestMeshes.Box(dvec3.Zero, new dvec3(size)), 1)
            {
                Position = new dvec3(0.02 * Math.Sin(i), 0.02 * Math.Cos(i), 0.2 + 0.4 * i),
                Orientation = dquat.FromAxisAngle(0.23 * i, new dvec3(1, 2, 3).Normalized),
                Friction = 0.7
            };
            boxes.Add(box);
            physics.Add(box);
        }
        double overlap = 0, lowest = 0;
        for (int step = 0; step < 180; step++)
        {
            physics.Update(TimeStep);
            foreach (RigidBody box in boxes)
                lowest = Math.Min(lowest, MinZ(box));
            for (int a = 0; a < boxes.Count; a++)
            for (int b = 0; b < a; b++)
                overlap = Math.Max(overlap, BoxOverlap(boxes[a], boxes[b], size));
        }
        return Check("maximum box overlap (m)", overlap, 0, 0.01)
            & Check("lowest corner (m)", lowest, -0.005, 0);
    }

    // Independent 15-axis OBB SAT oracle (not the triangle detector under test).
    private static double BoxOverlap(RigidBody a, RigidBody b, double size)
    {
        dvec3[] axesA = [a.Orientation * dvec3.UnitX, a.Orientation * dvec3.UnitY, a.Orientation * dvec3.UnitZ];
        dvec3[] axesB = [b.Orientation * dvec3.UnitX, b.Orientation * dvec3.UnitY, b.Orientation * dvec3.UnitZ];
        IEnumerable<dvec3> axes = axesA.Concat(axesB).Concat(axesA.SelectMany(x => axesB.Select(y => dvec3.Cross(x, y))));
        double depth = double.PositiveInfinity;
        foreach (dvec3 axis in axes)
        {
            if (axis.LengthSqr < 1e-12)
                continue;
            dvec3 n = axis.Normalized;
            double radius = 0.5 * size * (axesA.Sum(x => Math.Abs(dvec3.Dot(n, x))) + axesB.Sum(x => Math.Abs(dvec3.Dot(n, x))));
            depth = Math.Min(depth, radius - Math.Abs(dvec3.Dot(n, a.Position - b.Position)));
        }
        return Math.Max(0, depth);
    }

    // Rotate the entire experiment, including gravity. Contact validity must not depend on
    // world up, fixed rotation, or which body happens to own a triangulation diagonal.
    private static bool EdgeSliding(HeadlessVulkan v)
    {
        bool ok = true;
        dquat[] rotations = [dquat.Identity,
            dquat.FromAxisAngle(Math.PI / 2, dvec3.UnitY),
            dquat.FromAxisAngle(0.71, new dvec3(1, 2, 3).Normalized)];
        foreach (bool upright in new[] { false, true })
        foreach (bool movingPlatform in new[] { false, true })
        for (int r = 0; r < rotations.Length; r++)
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.Gravity = dvec3.Zero;
            physics.Settings.ReportContacts = true;
            dquat rotation = rotations[r];
            dmat3 frame = rotation.ToMat3;
            Mesh3D platformMesh = TestMeshes.Box(new dvec3(0, 0, -0.5), new dvec3(4, 4, 1));
            PhysicsBody platform = movingPlatform
                ? new RigidBody(platformMesh, 10000) { Orientation = rotation }
                : new FixedBody(platformMesh, rotation.ToMat4);
            platform.Friction = 0;
            if (platform is RigidBody rigidPlatform)
                physics.Add(rigidPlatform);
            else
                physics.Add((FixedBody)platform);
            RigidBody block = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones))
            {
                Position = frame * new dvec3(-1, 0.13, 0.4995),
                Orientation = rotation,
                LinearVelocity = frame * new dvec3(3, 0, 0),
                FixedRotation = upright,
                Friction = 0
            };
            physics.Add(block);
            double opposingImpulse = 0, falseImpactEnergy = 0;
            for (int step = 0; step < 90; step++)
            {
                physics.Update(TimeStep);
                foreach (ContactReport contact in physics.ContactReports)
                {
                    dvec3 forceDirection = contact.BodyA == block ? contact.Normal : -contact.Normal;
                    opposingImpulse += contact.NormalImpulse * Math.Max(0, -dvec3.Dot(forceDirection, frame * dvec3.UnitX));
                    if (contact.ApproachSpeed >= 1)
                        falseImpactEnergy += 0.5 * contact.NormalImpulse * contact.ApproachSpeed;
                }
            }
            string label = $"rotation {r}, {(upright ? "locked" : "free")} rotation, {(movingPlatform ? "moving" : "fixed")} platform";
            ok &= Check($"{label}: slide distance", (frame.Transposed * block.Position).x + 1, 4.45, 4.55);
            ok &= Check($"{label}: opposing impulse", opposingImpulse, 0, 0.02);
            ok &= Check($"{label}: false impact energy", falseImpactEnergy, 0, 0.01);
        }
        return ok;
    }

    // Two long, thin prisms cross midway along their ridges. No vertex projects into the
    // other prism, so vertex/triangle detection alone cannot stop this fast crossing.
    private static bool EdgeCcd(HeadlessVulkan v)
    {
        bool ok = true;
        foreach (dvec3 spin in new[] { dvec3.Zero, new dvec3(0, 0, 12), new dvec3(0, 120, 0) })
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.Gravity = dvec3.Zero;
            physics.Settings.ReportContacts = true;
            Mesh3D rod = TestMeshes.Box(dvec3.Zero, new dvec3(4, 0.1, 0.1));
            physics.Add(new FixedBody(rod, dquat.FromAxisAngle(Math.PI / 4, dvec3.UnitX).ToMat4));
            RigidBody bullet = new(rod)
            {
                Position = new dvec3(0.1, 0.2, 1),
                Orientation = dquat.FromAxisAngle(Math.PI / 2, dvec3.UnitZ) * dquat.FromAxisAngle(Math.PI / 4, dvec3.UnitX),
                LinearVelocity = new dvec3(0, 0, -120),
                AngularVelocity = spin,
                Friction = 0
            };
            physics.Add(bullet);
            physics.Update(TimeStep);
            ok &= Check($"spin {spin}: stopped above crossing", bullet.Position.z, 0.08, 1.1);
            ok &= Check($"spin {spin}: reported impact impulse", physics.ContactReports.Sum(c => c.NormalImpulse), 50, 180);
            ok &= Check($"spin {spin}: load-bearing edge contacts", physics.DownloadContacts().Count(c => ((c.Header.x >> 8) & 15) == 1 && c.Lambda.z < 0), 1, 100);
        }
        return ok;
    }

    // Cross at the joined ends of two ridges, where EE hands contact over to VT.
    // Both sides support the rod, so sliding off a lone tip cannot satisfy the test.
    // Lock rotation to make its stopping height independent of rolling about the ridge.
    // The footprint allowance must be a world distance, independent of triangle size.
    private static bool EndpointCcd(HeadlessVulkan v)
    {
        bool ok = true;
        foreach (double scale in new[] { 0.25, 1.0, 4.0 })
        foreach (double side in new[] { -0.5, 0.5 })
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.Gravity = dvec3.Zero;
            physics.Settings.ReportContacts = true;
            Mesh3D rod = TestMeshes.Box(dvec3.Zero, scale * new dvec3(4, 0.1, 0.1));
            physics.Add(new FixedBody(rod, dquat.FromAxisAngle(Math.PI / 4, dvec3.UnitX).ToMat4));
            Mesh3D continuation = TestMeshes.Box(new dvec3(4 * scale, 0, 0), scale * new dvec3(4, 0.1, 0.1));
            physics.Add(new FixedBody(continuation, dquat.FromAxisAngle(Math.PI / 4, dvec3.UnitX).ToMat4));
            RigidBody bullet = new(rod)
            {
                Position = new dvec3(2 * scale + side * physics.Settings.PenetrationTolerance, 0.2 * scale, scale),
                Orientation = dquat.FromAxisAngle(Math.PI / 2, dvec3.UnitZ) * dquat.FromAxisAngle(Math.PI / 4, dvec3.UnitX),
                LinearVelocity = new dvec3(0, 0, -120 * scale),
                FixedRotation = true,
                Friction = 0
            };
            physics.Add(bullet);
            physics.Update(TimeStep);
            string label = $"scale {scale}, endpoint offset {side} tolerances";
            ok &= Check($"{label}: height / scale", bullet.Position.z / scale, 0.13, 0.15);
            ok &= Check($"{label}: impact impulse / scale", physics.ContactReports.Sum(c => c.NormalImpulse) / scale, 50, 180);
        }
        return ok;
    }

    private static bool ConcaveSliding(HeadlessVulkan v)
    {
        // An L cross-section extruded along y: floor z=0 and wall x=0 meet at a
        // concave edge. Rotate the channel and vary the box's orientation inside it.
        Mesh3D channel = TestMeshes.Voxels(Enumerable.Range(-3, 6).SelectMany(y =>
            new[] { new ivec3(-1, y, -1), new ivec3(0, y, -1), new ivec3(-1, y, 0) }));
        bool ok = true;
        foreach (bool upright in new[] { false, true })
        foreach (double tilt in new[] { 0.0, 0.37 })
        foreach (dvec3 axis in new[] { dvec3.UnitX, dvec3.UnitY, new dvec3(1, 2, 3).Normalized })
        {
            using PhysicsSystem physics = CreateSystem(v);
            physics.Settings.Gravity = dvec3.Zero;
            physics.Settings.ReportContacts = true;
            dquat rotation = dquat.FromAxisAngle(1.2, axis);
            dmat3 frame = rotation.ToMat3;
            physics.Add(new FixedBody(channel, rotation.ToMat4) { Friction = 0 });
            double radius = 0.5 * (Math.Cos(tilt) + Math.Sin(tilt));
            RigidBody block = new(TestMeshes.Box(dvec3.Zero, dvec3.Ones))
            {
                Position = frame * new dvec3(radius - 0.0005, -1.5, radius - 0.0005),
                Orientation = rotation * dquat.FromAxisAngle(tilt, dvec3.UnitY),
                LinearVelocity = frame * new dvec3(0, 3, 0),
                FixedRotation = upright,
                Friction = 0
            };
            physics.Add(block);
            double energy = 0;
            for (int step = 0; step < 60; step++)
            {
                physics.Update(TimeStep);
                energy += physics.ContactReports.Where(c => c.ApproachSpeed >= 1).Sum(c => 0.5 * c.NormalImpulse * c.ApproachSpeed);
            }
            string label = $"L channel, axis {axis}, tilt {tilt}, locked {upright}";
            ok &= Check($"{label}: distance", (frame.Transposed * block.Position).y + 1.5, 2.98, 3.02);
            ok &= Check($"{label}: false impact energy", energy, 0, 0.01);
        }
        return ok;
    }
}
