using GlmSharp;

using MYT.Kit.Vulkan;

namespace PhysicsTests;


/// <summary>Procedural meshes for the scenarios, all wound counter-clockwise seen from outside.</summary>
internal static class TestMeshes
{
    // Corner i of a unit box is (i & 1, (i >> 1) & 1, (i >> 2) & 1).
    private static readonly ulong[] BoxFaces =
    [
        0, 2, 3, 0, 3, 1,
        4, 5, 7, 4, 7, 6,
        0, 1, 5, 0, 5, 4,
        2, 6, 7, 2, 7, 3,
        0, 4, 6, 0, 6, 2,
        1, 3, 7, 1, 7, 5
    ];

    public static Mesh3D Box(dvec3 center, dvec3 size)
    {
        dvec3[] vertices = new dvec3[8];
        for (int i = 0; i < 8; i++)
        {
            dvec3 corner = new(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            vertices[i] = center + (corner - 0.5) * size;
        }

        return new Mesh3D(vertices, (ulong[])BoxFaces.Clone());
    }

    /// <summary>Boundary of a union of unit voxels, including reentrant edges.</summary>
    public static Mesh3D Voxels(IEnumerable<ivec3> cells)
    {
        HashSet<ivec3> occupied = [.. cells];
        ivec3[] directions = [new(0, 0, -1), new(0, 0, 1), new(0, -1, 0), new(0, 1, 0), new(-1, 0, 0), new(1, 0, 0)];
        List<dvec3> vertices = [];
        List<ulong> faces = [];
        foreach (ivec3 cell in occupied)
        {
            Mesh3D box = Box(new dvec3(cell) + 0.5, dvec3.Ones);
            ulong offset = (ulong)vertices.Count;
            vertices.AddRange(box.Vertices);
            for (int face = 0; face < 6; face++)
                if (!occupied.Contains(cell + directions[face]))
                    faces.AddRange(box.Faces.Skip(face * 6).Take(6).Select(i => i + offset));
        }
        return new Mesh3D([.. vertices], [.. faces]);
    }

    /// <summary>A flat, open, finely triangulated square at height z, facing up.</summary>
    public static Mesh3D Grid(double halfSize, int cells, double z)
    {
        List<dvec3> vertices = [];
        List<ulong> faces = [];
        double step = 2 * halfSize / cells;

        for (int y = 0; y <= cells; y++)
        {
            for (int x = 0; x <= cells; x++)
                vertices.Add(new dvec3(-halfSize + x * step, -halfSize + y * step, z));
        }

        for (int y = 0; y < cells; y++)
        {
            for (int x = 0; x < cells; x++)
            {
                ulong a = (ulong)(y * (cells + 1) + x);
                ulong b = a + 1;
                ulong c = a + (ulong)(cells + 1);
                ulong d = c + 1;
                faces.AddRange([a, b, d, a, d, c]);
            }
        }

        return new Mesh3D([.. vertices], [.. faces]);
    }

    /// <summary>
    /// A triangular prism lying along y with a sharp ridge on top: base from x = -halfWidth to
    /// halfWidth at z = baseZ, apex at x = 0, z = baseZ + height.
    /// </summary>
    public static Mesh3D Ridge(double halfWidth, double height, double length, double baseZ)
    {
        double y0 = -length / 2;
        double y1 = length / 2;
        dvec3[] vertices =
        [
            new(-halfWidth, y0, baseZ), new(halfWidth, y0, baseZ), new(0, y0, baseZ + height),
            new(-halfWidth, y1, baseZ), new(halfWidth, y1, baseZ), new(0, y1, baseZ + height)
        ];

        ulong[] faces =
        [
            0, 1, 2,
            3, 5, 4,
            1, 5, 2, 1, 4, 5,
            0, 5, 3, 0, 2, 5,
            0, 4, 1, 0, 3, 4
        ];

        return new Mesh3D(vertices, faces);
    }

    /// <summary>A subdivided icosahedron projected onto a sphere.</summary>
    public static Mesh3D Icosphere(dvec3 center, double radius, int subdivisions)
    {
        double t = (1 + Math.Sqrt(5)) / 2;
        List<dvec3> vertices =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1)
        ];
        List<uvec3> faces =
        [
            new(0, 11, 5), new(0, 5, 1), new(0, 1, 7), new(0, 7, 10), new(0, 10, 11),
            new(1, 5, 9), new(5, 11, 4), new(11, 10, 2), new(10, 7, 6), new(7, 1, 8),
            new(3, 9, 4), new(3, 4, 2), new(3, 2, 6), new(3, 6, 8), new(3, 8, 9),
            new(4, 9, 5), new(2, 4, 11), new(6, 2, 10), new(8, 6, 7), new(9, 8, 1)
        ];

        for (int level = 0; level < subdivisions; level++)
        {
            Dictionary<(uint, uint), uint> midpoints = [];
            uint Midpoint(uint a, uint b)
            {
                (uint, uint) key = a < b ? (a, b) : (b, a);
                if (!midpoints.TryGetValue(key, out uint index))
                {
                    index = (uint)vertices.Count;
                    vertices.Add((vertices[(int)a] + vertices[(int)b]) / 2);
                    midpoints[key] = index;
                }

                return index;
            }

            List<uvec3> next = [];
            foreach (uvec3 f in faces)
            {
                uint ab = Midpoint(f.x, f.y);
                uint bc = Midpoint(f.y, f.z);
                uint ca = Midpoint(f.z, f.x);
                next.AddRange([new(f.x, ab, ca), new(f.y, bc, ab), new(f.z, ca, bc), new(ab, bc, ca)]);
            }

            faces = next;
        }

        dvec3[] positions = [.. vertices.Select(v => center + v.Normalized * radius)];
        ulong[] indices = [.. faces.SelectMany(f => new ulong[] { f.x, f.y, f.z })];
        return new Mesh3D(positions, indices);
    }

    /// <summary>Signed volume-weighted winding number of point p with respect to a closed mesh: about 1 inside, 0 outside.</summary>
    public static double WindingNumber(ReadOnlySpan<dvec3> positions, ReadOnlySpan<uvec3> triangles, dvec3 p)
    {
        double total = 0;
        foreach (uvec3 t in triangles)
        {
            dvec3 a = positions[(int)t.x] - p;
            dvec3 b = positions[(int)t.y] - p;
            dvec3 c = positions[(int)t.z] - p;
            double la = a.Length;
            double lb = b.Length;
            double lc = c.Length;
            double numerator = dvec3.Dot(a, dvec3.Cross(b, c));
            double denominator = la * lb * lc + dvec3.Dot(a, b) * lc + dvec3.Dot(b, c) * la + dvec3.Dot(c, a) * lb;
            total += 2 * Math.Atan2(numerator, denominator);
        }

        return total / (4 * Math.PI);
    }

    /// <summary>Distance from p to the closest point of any triangle.</summary>
    public static double DistanceToSurface(ReadOnlySpan<dvec3> positions, ReadOnlySpan<uvec3> triangles, dvec3 p)
    {
        double best = double.MaxValue;
        foreach (uvec3 t in triangles)
            best = Math.Min(best, (p - ClosestPointOnTriangle(p, positions[(int)t.x], positions[(int)t.y], positions[(int)t.z])).Length);

        return best;
    }

    // Ericson, Real-Time Collision Detection, 5.1.5.
    private static dvec3 ClosestPointOnTriangle(dvec3 p, dvec3 a, dvec3 b, dvec3 c)
    {
        dvec3 ab = b - a;
        dvec3 ac = c - a;
        dvec3 ap = p - a;
        double d1 = dvec3.Dot(ab, ap);
        double d2 = dvec3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return a;

        dvec3 bp = p - b;
        double d3 = dvec3.Dot(ab, bp);
        double d4 = dvec3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return b;

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return a + d1 / (d1 - d3) * ab;

        dvec3 cp = p - c;
        double d5 = dvec3.Dot(ab, cp);
        double d6 = dvec3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return c;

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return a + d2 / (d2 - d6) * ac;

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);

        double denominator = 1 / (va + vb + vc);
        return a + ab * (vb * denominator) + ac * (vc * denominator);
    }
}
