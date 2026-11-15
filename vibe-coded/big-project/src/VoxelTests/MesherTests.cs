using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;
using VoxelGame;

namespace VoxelTests;

/// <summary>Collision surface coverage, orientation, materials and adjacency, without a GPU.</summary>
internal static class MesherTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("collision-meshing", Shapes),
        ("collision-shape-cache", ShapeCache),
        ("collision-meshing-bedrock", OpenBedrock),
        ("collision-meshing-benchmark", Benchmark)
    ];

    private static Grid3D<Voxel> Box(ivec3 origin, ivec3 size)
    {
        Grid3D<Voxel> grid = new();
        for (int z = 0; z < size.z; z++)
        for (int y = 0; y < size.y; y++)
        for (int x = 0; x < size.x; x++)
            grid[origin + new ivec3(x, y, z)] = new Voxel(BlockType.Stone, 0);
        return grid;
    }

    private static bool ShapeCache()
    {
        bool ok = true;
        foreach (ivec3 origin in new[] { ivec3.Zero, new ivec3(-2, 15, 14) })
        {
            Grid3D<Voxel> grid = Box(origin, new ivec3(3));
            var bounds = grid.Bounds();
            var first = VoxelMesher.BuildRigidCollisionMesh(grid, bounds.Min, bounds.Max);
            var reference = VoxelMesher.BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate), mergeFaces: false)!.Value;
            ok &= first.Mesh.Vertices.SequenceEqual(reference.Mesh.Vertices)
                && first.Mesh.Faces.SequenceEqual(reference.Mesh.Faces)
                && first.Surfaces.SequenceEqual(reference.Surfaces);
            Grid3D<Voxel> copy = Box(origin, new ivec3(3));
            var repeated = VoxelMesher.BuildRigidCollisionMesh(copy, bounds.Min, bounds.Max);
            ok &= ReferenceEquals(first.Mesh, repeated.Mesh);
            // Damage leaves the occupied surface intact; an edited material or hole does not.
            copy[origin] = new Voxel(BlockType.Stone, Voxel.ToDamage(0.5));
            ok &= ReferenceEquals(first.Mesh, VoxelMesher.BuildRigidCollisionMesh(copy, bounds.Min, bounds.Max).Mesh);
            copy[origin] = new Voxel(BlockType.Glass, 0);
            var material = VoxelMesher.BuildRigidCollisionMesh(copy, bounds.Min, bounds.Max);
            ok &= !ReferenceEquals(first.Mesh, material.Mesh) && material.Surfaces.Contains(new Voxel(BlockType.Glass, 0).Material.Surface);
            copy[origin + 1] = Voxel.Air;
            var hole = VoxelMesher.BuildRigidCollisionMesh(copy, bounds.Min, bounds.Max);
            reference = VoxelMesher.BuildCollisionMesh(copy, copy.Chunks.Select(c => c.Coordinate), mergeFaces: false)!.Value;
            ok &= hole.Mesh.Vertices.SequenceEqual(reference.Mesh.Vertices)
                && hole.Mesh.Faces.SequenceEqual(reference.Mesh.Faces)
                && hole.Surfaces.SequenceEqual(reference.Surfaces);
        }
        Console.WriteLine($"   identical box reuse, health changes, materials, holes and chunk boundaries: {(ok ? "ok" : "FAIL")}");
        return ok;
    }

    private static bool Shapes()
    {
        bool ok = Verify("solid chunk", Box(ivec3.Zero, new ivec3(16)), expectedTriangles: 12);
        ok &= Verify("negative box", Box(new ivec3(-10, -9, -8), new ivec3(6, 4, 3)), expectedTriangles: 12);
        ok &= Verify("chunk boundaries", Box(new ivec3(12, -4, 14), new ivec3(9, 8, 7)));

        Grid3D<Voxel> stairs = new();
        for (int x = 0; x < 8; x++)
        for (int y = 0; y < 5; y++)
        for (int z = 0; z <= x; z++)
            stairs[new ivec3(x, y, z)] = new Voxel(BlockType.Stone, 0);
        ok &= Verify("stairs and T-junctions", stairs);

        Grid3D<Voxel> cavity = Box(ivec3.Zero, new ivec3(8));
        for (int z = 2; z < 6; z++)
        for (int y = 2; y < 6; y++)
        for (int x = 2; x < 6; x++)
            cavity[new ivec3(x, y, z)] = Voxel.Air;
        ok &= Verify("sealed cavity", cavity);

        Grid3D<Voxel> materials = Box(ivec3.Zero, new ivec3(8, 7, 3));
        for (int y = 0; y < 7; y++)
        for (int x = 0; x < 8; x++)
            materials[new ivec3(x, y, 2)] = new Voxel(x < 3 ? BlockType.Glass : BlockType.Dirt, 0);
        ok &= Verify("material boundaries", materials);

        Random random = new(917);
        for (int run = 0; run < 12; run++)
        {
            Grid3D<Voxel> grid = new();
            for (int z = 0; z < 5; z++)
            for (int y = 0; y < 6; y++)
            for (int x = 0; x < 7; x++)
                if (random.NextDouble() < 0.65)
                    grid[new ivec3(x - 3, y - 3, z - 2)] = new Voxel(random.Next(3) == 0 ? BlockType.Glass : BlockType.Stone, 0);
            ok &= Verify($"random concave {run}", grid);
        }
        return ok;
    }

    private static bool Verify(string name, Grid3D<Voxel> grid, int? expectedTriangles = null)
    {
        ivec3[] chunks = [.. grid.Chunks.Select(c => c.Coordinate)];
        long start = Stopwatch.GetTimestamp();
        (Mesh3D mesh, SurfaceMaterial[] surfaces) = VoxelMesher.BuildCollisionMesh(grid, chunks, mergeFaces: true)!.Value;
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Mesh3D reference = VoxelMesher.BuildCollisionMesh(grid, chunks, mergeFaces: false)!.Value.Mesh;
        int errors = 0;
        double area = 0;
        double volume = 0;
        Dictionary<(ulong, ulong), int> edges = [];
        for (int t = 0; t < mesh.Faces.Length / 3; t++)
        {
            dvec3 a = mesh.Vertices[mesh.Faces[3 * t]];
            dvec3 b = mesh.Vertices[mesh.Faces[3 * t + 1]];
            dvec3 c = mesh.Vertices[mesh.Faces[3 * t + 2]];
            dvec3 normal = dvec3.Cross(b - a, c - a);
            area += 0.5 * normal.Length;
            volume += dvec3.Dot(a, dvec3.Cross(b, c)) / 6.0;
            if (normal.Length < 1e-10)
            {
                errors++;
                continue;
            }
            normal = normal.Normalized;
            dvec3 inside = (a + b + c) / 3.0 - 1e-5 * normal;
            ivec3 cell = new((int)Math.Floor(inside.x), (int)Math.Floor(inside.y), (int)Math.Floor(inside.z));
            if (grid[cell].Type == BlockType.Air || grid[cell].Material.Surface != surfaces[t])
                errors++;
            for (int k = 0; k < 3; k++)
            {
                ulong u = mesh.Faces[3 * t + k];
                ulong v = mesh.Faces[3 * t + (k + 1) % 3];
                var edge = u < v ? (u, v) : (v, u);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }
        // Diagonally touching voxels may be non-manifold, but no closed surface has an
        // unmatched edge. A T-junction would leave unmatched long and short segments.
        errors += edges.Values.Count(n => n < 2);
        (ivec3 min, ivec3 max) = grid.Bounds();
        int exposed = 0;
        for (int z = min.z; z < max.z; z++)
        for (int y = min.y; y < max.y; y++)
        for (int x = min.x; x < max.x; x++)
        {
            ivec3 cell = new(x, y, z);
            if (grid[cell].Type == BlockType.Air)
                continue;
            for (int face = 0; face < 6; face++)
            {
                ivec3 normal = Grid3D<Voxel>.FaceDirections[face];
                if (grid[cell + normal].Type != BlockType.Air)
                    continue;
                exposed++;
                dvec3 point = new dvec3(cell) + new dvec3(0.371, 0.613, 0.427);
                int axis = face >> 1;
                point[axis] = cell[axis] + (normal[axis] > 0 ? 1 : 0);
                int hits = 0;
                for (int t = 0; t < mesh.Faces.Length / 3; t++)
                {
                    dvec3 a = mesh.Vertices[mesh.Faces[3 * t]];
                    dvec3 b = mesh.Vertices[mesh.Faces[3 * t + 1]];
                    dvec3 c = mesh.Vertices[mesh.Faces[3 * t + 2]];
                    dvec3 n = dvec3.Cross(b - a, c - a);
                    if (dvec3.Dot(n, new dvec3(normal)) <= 0 || Math.Abs(dvec3.Dot(n, point - a)) > 1e-8)
                        continue;
                    if (dvec3.Dot(dvec3.Cross(b - a, point - a), n) >= -1e-8
                        && dvec3.Dot(dvec3.Cross(c - b, point - b), n) >= -1e-8
                        && dvec3.Dot(dvec3.Cross(a - c, point - c), n) >= -1e-8)
                    {
                        hits++;
                        if (surfaces[t] != grid[cell].Material.Surface)
                            errors++;
                    }
                }
                if (hits != 1)
                    errors++;
            }
        }
        if (Math.Abs(area - exposed) > 1e-7 || Math.Abs(volume - grid.Count) > 1e-7)
            errors++;
        RigidBody mergedBody = new(mesh, grid.Count);
        RigidBody referenceBody = new(reference, grid.Count);
        if ((mergedBody.CenterOfMass - referenceBody.CenterOfMass).Length > 1e-8)
            errors++;
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
            if (Math.Abs(mergedBody.LocalInertia[column, row] - referenceBody.LocalInertia[column, row]) > 1e-7)
                errors++;
        if (expectedTriangles is int expected && mesh.Faces.Length != 3 * expected)
            errors++;

        // The background baker and the synchronous builder must use the same surface.
        if (chunks.Length == 1)
        {
            Voxel[] padded = new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(grid, chunks[0], padded);
            var baked = VoxelMesher.BuildCollisionMesh(padded, chunks[0], false, mergeFaces: true)!.Value;
            if (!mesh.Vertices.SequenceEqual(baked.Mesh.Vertices)
                || !mesh.Faces.SequenceEqual(baked.Mesh.Faces) || !surfaces.SequenceEqual(baked.Surfaces))
                errors++;
        }
        Console.WriteLine($"   {(errors == 0 ? "ok " : "BAD")} {name}: {exposed * 2} -> {mesh.Faces.Length / 3} triangles, {milliseconds:F2} ms, {errors} errors");
        return errors == 0;
    }

    private static bool OpenBedrock()
    {
        Grid3D<Voxel> grid = Box(ivec3.Zero, new ivec3(8, 6, 1));
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 8; x++)
            grid[new ivec3(x, y, 0)] = new Voxel(BlockType.Bedrock, 0);
        var built = VoxelMesher.BuildCollisionMesh(grid, [ivec3.Zero], openBelowBedrock: true, mergeFaces: true)!.Value;
        bool ok = built.Mesh.Faces.Length == 30;
        for (int t = 0; t < built.Mesh.Faces.Length; t += 3)
        {
            dvec3 a = built.Mesh.Vertices[built.Mesh.Faces[t]];
            dvec3 b = built.Mesh.Vertices[built.Mesh.Faces[t + 1]];
            dvec3 c = built.Mesh.Vertices[built.Mesh.Faces[t + 2]];
            ok &= dvec3.Cross(b - a, c - a).z >= 0;
        }
        ok &= VoxelMesher.BuildCollisionMesh(new Grid3D<Voxel>(), [ivec3.Zero]) == null;
        Console.WriteLine($"   {(ok ? "ok " : "BAD")} omitted bedrock underside and empty mesh");
        return ok;
    }

    private static bool Benchmark()
    {
        Grid3D<Voxel> grid = new();
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        for (int z = 0; z < 1 + (x / 8 + y / 8) % 4; z++)
            grid[new ivec3(x, y, z)] = new Voxel(BlockType.Stone, 0);
        ivec3[] chunks = [.. grid.Chunks.Select(c => c.Coordinate)];
        double[][] milliseconds = [new double[7], new double[7]];
        long[] allocations = new long[2];
        int[] triangles = new int[2];
        // Alternate the two paths and discard their first runs (JIT and allocation warmup).
        for (int run = -1; run < 7; run++)
        for (int mode = 0; mode < 2; mode++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            var built = VoxelMesher.BuildCollisionMesh(grid, chunks, mergeFaces: mode == 1)!.Value;
            new FixedBody(built.Mesh) { SurfaceMaterials = built.Surfaces }.Cook();
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (run >= 0)
            {
                milliseconds[mode][run] = elapsed;
                allocations[mode] += allocated;
            }
            triangles[mode] = built.Mesh.Faces.Length / 3;
        }
        for (int mode = 0; mode < 2; mode++)
        {
            Array.Sort(milliseconds[mode]);
            Console.WriteLine($"   {(mode == 0 ? "unit faces" : "merged faces")}: {triangles[mode]} triangles, build+cook p50 {milliseconds[mode][3]:F2} ms, mean allocated {allocations[mode] / 7} B");
        }
        // Timing is diagnostic: machines and GC differ. The shape reduction is invariant.
        return triangles[1] < triangles[0] / 4;
    }
}
