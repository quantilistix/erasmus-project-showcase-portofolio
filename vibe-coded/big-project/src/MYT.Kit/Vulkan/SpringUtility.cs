
using GlmSharp;

namespace MYT.Kit.Vulkan;


public static class SpringUtility
{
    public static Spring[] GenerateFromMeshEdges(Mesh3D mesh, double stiffness, double damping)
    {
        var edges = new HashSet<(uint, uint)>();

        for (int i = 0; i < mesh.Faces.Length; i += 3)
        {
            uint v0 = (uint)mesh.Faces[i];
            uint v1 = (uint)mesh.Faces[i + 1];
            uint v2 = (uint)mesh.Faces[i + 2];

            AddEdge(edges, v0, v1);
            AddEdge(edges, v1, v2);
            AddEdge(edges, v2, v0);
        }

        var springs = new Spring[edges.Count];
        int idx = 0;

        foreach (var (n1, n2) in edges)
        {
            double restLength = (mesh.Vertices[n1] - mesh.Vertices[n2]).Length;

            springs[idx++] = new Spring
            {
                Node1 = n1,
                Node2 = n2,
                RestLength = restLength,
                Stiffness = stiffness,
                Damping = damping,
                Durability = double.MaxValue
            };
        }

        return springs;
    }

    public static uvec3[] ExtractSurfaceTriangles(Mesh3D mesh)
    {
        var triangles = new uvec3[mesh.Faces.Length / 3];

        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = new uvec3(
                (uint)mesh.Faces[i * 3],
                (uint)mesh.Faces[i * 3 + 1],
                (uint)mesh.Faces[i * 3 + 2]
            );
        }

        return triangles;
    }

    public static uvec2[] ExtractSurfaceEdges(Mesh3D mesh)
    {
        return ExtractEdges(ExtractSurfaceTriangles(mesh));
    }

    public static uvec2[] ExtractEdges(uvec3[] triangles)
    {
        HashSet<(uint, uint)> edges = [];

        foreach (uvec3 t in triangles)
        {
            AddEdge(edges, t.x, t.y);
            AddEdge(edges, t.y, t.z);
            AddEdge(edges, t.z, t.x);
        }

        uvec2[] edgeArray = new uvec2[edges.Count];
        int idx = 0;

        foreach ((uint n1, uint n2) in edges)
            edgeArray[idx++] = new uvec2(n1, n2);

        return edgeArray;
    }

    /// <summary>
    /// Merges vertices closer than a millionth of the mesh's size, which mesh exporters split
    /// along normal and UV seams or leave as near-copies (Blender's cone tip is a ring of them),
    /// reindexes the triangles to match and drops slivers, the triangles left without area.
    /// Merging gives every feature one owner and every edge its two real faces; the contact
    /// shader ignores slivers anyway, but would still see their vertices and edges. Sources
    /// gives, per kept triangle, its index among the given ones.
    /// </summary>
    public static (dvec3[] Vertices, uvec3[] Triangles, int[] Sources) Weld(dvec3[] vertices, uvec3[] triangles)
    {
        const double WeldTolerance = 1e-6;
        // Height over longest edge below which a triangle is a sliver; matches SLIVER_RATIO in
        // contact_detect.comp.
        const double SliverRatio = 1e-5;

        dvec3 lower = new(double.MaxValue);
        dvec3 upper = new(double.MinValue);
        foreach (dvec3 v in vertices)
        {
            lower = dvec3.Min(lower, v);
            upper = dvec3.Max(upper, v);
        }

        double tolerance = vertices.Length > 0 ? WeldTolerance * (upper - lower).Length : 0.0;
        double cellSize = tolerance > 0.0 ? tolerance : 1.0;

        Dictionary<(long, long, long), List<uint>> grid = [];
        List<dvec3> unique = [];
        uint[] remap = new uint[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
        {
            dvec3 p = vertices[i];
            (long x, long y, long z) cell = ((long)Math.Floor(p.x / cellSize), (long)Math.Floor(p.y / cellSize), (long)Math.Floor(p.z / cellSize));
            remap[i] = FindWithinTolerance(p, cell) ?? Add(p, cell);
        }

        uint? FindWithinTolerance(dvec3 p, (long x, long y, long z) cell)
        {
            for (long x = cell.x - 1; x <= cell.x + 1; x++)
            {
                for (long y = cell.y - 1; y <= cell.y + 1; y++)
                {
                    for (long z = cell.z - 1; z <= cell.z + 1; z++)
                    {
                        if (!grid.TryGetValue((x, y, z), out List<uint>? candidates))
                            continue;

                        foreach (uint candidate in candidates)
                        {
                            if ((unique[(int)candidate] - p).Length <= tolerance)
                                return candidate;
                        }
                    }
                }
            }

            return null;
        }

        uint Add(dvec3 p, (long, long, long) cell)
        {
            uint index = (uint)unique.Count;
            unique.Add(p);
            if (!grid.TryGetValue(cell, out List<uint>? members))
            {
                members = [];
                grid[cell] = members;
            }

            members.Add(index);
            return index;
        }

        List<uvec3> welded = new(triangles.Length);
        List<int> sources = new(triangles.Length);
        for (int i = 0; i < triangles.Length; i++)
        {
            uvec3 t = triangles[i];
            uvec3 w = new(remap[t.x], remap[t.y], remap[t.z]);
            dvec3 a = unique[(int)w.x];
            dvec3 b = unique[(int)w.y];
            dvec3 c = unique[(int)w.z];

            // |ab × ac| is the longest edge times the height onto it.
            double longestSquared = Math.Max((b - a).LengthSqr, Math.Max((c - b).LengthSqr, (a - c).LengthSqr));
            if (dvec3.Cross(b - a, c - a).Length > SliverRatio * longestSquared)
            {
                welded.Add(w);
                sources.Add(i);
            }
        }

        return ([.. unique], [.. welded], [.. sources]);
    }

    /// <summary>
    /// Greedy graph coloring: no spring joins two nodes of the same color, so all nodes of one
    /// color can be solved in parallel (VBD §3.9).
    /// </summary>
    public static uint[] ColorGraph(int nodeCount, Spring[] springs)
    {
        List<uint>[] neighbours = new List<uint>[nodeCount];
        for (int i = 0; i < nodeCount; i++)
            neighbours[i] = [];

        foreach (Spring spring in springs)
        {
            neighbours[spring.Node1].Add(spring.Node2);
            neighbours[spring.Node2].Add(spring.Node1);
        }

        uint[] colors = new uint[nodeCount];
        Array.Fill(colors, uint.MaxValue);
        HashSet<uint> taken = [];

        for (int i = 0; i < nodeCount; i++)
        {
            taken.Clear();
            foreach (uint n in neighbours[i])
            {
                if (colors[n] != uint.MaxValue)
                    taken.Add(colors[n]);
            }

            uint color = 0;
            while (taken.Contains(color))
                color++;
            colors[i] = color;
        }

        return colors;
    }

    /// <summary>
    /// For each vertex, the triangles that use it, in compressed rows: the triangles of vertex v
    /// are <c>indices[offsets[v] .. offsets[v + 1]]</c>.
    /// </summary>
    public static (uint[] offsets, uint[] indices) VertexTriangleAdjacency(int vertexCount, uvec3[] triangles)
    {
        uint[] offsets = new uint[vertexCount + 1];
        foreach (uvec3 t in triangles)
        {
            offsets[t.x + 1]++;
            offsets[t.y + 1]++;
            offsets[t.z + 1]++;
        }

        for (int v = 0; v < vertexCount; v++)
            offsets[v + 1] += offsets[v];

        uint[] indices = new uint[offsets[vertexCount]];
        uint[] cursor = (uint[])offsets.Clone();
        for (uint i = 0; i < triangles.Length; i++)
        {
            uvec3 t = triangles[i];
            indices[cursor[t.x]++] = i;
            indices[cursor[t.y]++] = i;
            indices[cursor[t.z]++] = i;
        }

        return (offsets, indices);
    }

    private static void AddEdge(HashSet<(uint, uint)> edges, uint a, uint b)
    {
        if (a > b) (a, b) = (b, a);
        edges.Add((a, b));
    }
}
