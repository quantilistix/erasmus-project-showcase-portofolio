using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GlmSharp;

namespace MYT.Kit.Vulkan;


[StructLayout(LayoutKind.Sequential)]
internal struct GpuTriangle
{
    public uvec4 Vertices;  // xyz = collision vertices, w = collision body
    public uvec4 Edges;     // xyz = edges v0v1, v1v2, v2v0; w = ownership bits (0-2 vertices, 3-5 edges) | material << 8
}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuCollisionBody
{
    public uvec4 Info;      // x = kind, y = rigid block or NoBlock, z = first triangle, w = triangle count
    public uvec4 Extra;     // x = BVH root node or NoBlock, y = first vertex, z = vertex count, w = flags
    public vec4 BoundsMin;
    public vec4 BoundsMax;
    public uvec4 SweepMin;  // running bounds of a detection pass, as order-preserving keys
    public uvec4 SweepMax;
    public uvec4 Features;  // x = feature lifetime handle, y = first edge; zw reserved
}


/// <summary>
/// The collision surfaces of every body, concatenated into the arrays the collision shaders read
/// (contact_common.glsl). Bodies are ordered rigid, soft, then fixed, so the triangles and
/// vertices of dynamic bodies come first; material ids follow the same order.
/// <para>
/// Every triangle names its material. One per body comes first, its id the body's, written
/// from <see cref="PhysicsBody.Friction"/> and <see cref="PhysicsBody.Restitution"/> on every
/// update; then the distinct <see cref="PhysicsBody.SurfaceMaterials"/> of each body that has
/// them (<see cref="SurfacePalette"/>).
/// </para>
/// <para>
/// What depends only on a body's shape (welded vertices, edges and ownership, the BVH) is
/// cooked once per fixed body and per rigid mesh and kept for later rebuilds, which therefore
/// cost little more than copying arrays when most bodies are unchanged. Every fixed body is
/// its own collision body with its own BVH, so changing one rebuilds only that one.
/// </para>
/// </summary>
internal sealed class CollisionGeometry
{
    public const uint KindFixed = 0;
    public const uint KindNode = 1;
    public const uint KindRigid = 2;
    public const uint NoBlock = uint.MaxValue;
    /// <summary>Collision body flag (Extra.w): its open edges continue into other bodies (<see cref="FixedBody.OpenEdgesContinue"/>).</summary>
    public const uint BodyOpenEdgesContinue = 2;
    /// <summary>Where a triangle's material sits in its ownership word (Edges.w), above the ownership bits.</summary>
    public const int MaterialShift = 8;

    // Order-preserving keys of +infinity and -infinity (contact_common.glsl): empty running bounds.
    private const uint KeyPositiveInfinity = 0xFF800000;
    private const uint KeyNegativeInfinity = 0x007FFFFF;

    private const uint LeafSize = 4;
    /// <summary>
    /// The shape-only part of a collision surface, with body-local indices. Every vertex and
    /// every edge is owned by the first triangle using it, so the detection shader tests each
    /// feature pair once.
    /// </summary>
    private sealed class CookedSurface
    {
        public required dvec3[] Vertices;
        /// <summary>Per vertex, the vertex of the input it is (a copy of, where fans were split).</summary>
        public required int[] SourceVertices;
        public required uvec3[] Triangles;
        /// <summary>Per triangle, its index among the body's own, which welding can thin out.</summary>
        public required int[] SourceTriangles;
        /// <summary>xyz = edges v0v1, v1v2, v2v0; w = ownership bits (0-2 vertices, 3-5 edges).</summary>
        public required uvec4[] TriangleEdges;
        /// <summary>xy = vertices, zw = the triangles on either side or NoBlock.</summary>
        public required uvec4[] Edges;
        public required GpuBvhNode[] BvhNodes;
        public required uint[] BvhPrimitives;
        public FixedBounds? Fixed;
    }

    private sealed record FixedBounds(vec4[] Triangles, dvec3 Lower, dvec3 Upper);

    /// <summary>
    /// A body's <see cref="PhysicsBody.SurfaceMaterials"/>, each once, and which of them each
    /// triangle of its cooked surface has, for the surface and list it was made from.
    /// </summary>
    private sealed record SurfacePalette
    (
        IReadOnlyList<SurfaceMaterial> Source,
        CookedSurface Surface,
        SurfaceMaterial[] Distinct,
        uint[] TriangleMaterials
    );

    private readonly record struct VertexSource(uint Material, dmat3 Rotation,
        dvec3 Center, SoftBody? Soft = null, List<uint>? SurfaceNodes = null, uint NodeOffset = 0);

    private readonly record struct MaterialSource(uint First, uint[]? Indices = null)
    {
        public uint At(int triangle) => Indices == null ? First : First + Indices[triangle];
    }

    private readonly record struct SurfaceAssembly(
        CookedSurface Surface, uint Kind, uint RigidBlock, uint Root,
        uint ContactIdentity, uint Flags, uint Body, uint FirstVertex,
        uint FirstTriangle, uint FirstEdge,
        VertexSource Vertex, MaterialSource Material);

    private readonly List<SurfaceAssembly> _assemblies = [];
    private static readonly bool ParallelGeometry = Environment.GetEnvironmentVariable("MYT_PARALLEL_GEOMETRY") != "0";

    private static readonly ConditionalWeakTable<FixedBody, CookedSurface> CookedFixed = new();
    // Per mesh, per center of mass: a rigid surface is cooked relative to its center of mass.
    private static readonly ConditionalWeakTable<Mesh3D, List<(dvec3 Center, CookedSurface Surface)>> CookedRigid = new();
    private static readonly ConditionalWeakTable<PhysicsBody, SurfacePalette> Palettes = new();

    internal static readonly double[] ProfTimes = new double[6]; // PROF

    /// <summary>
    /// The topology of a surface: its edges with the triangles on either side, and who owns each
    /// feature. Where solids touch along an edge or at a point only, as diagonal neighbours do in a
    /// voxel mesh, more than two triangles share an edge and a vertex has several separate fans.
    /// Edge adjacency must then join the two triangles that bound one solid wedge, and each fan
    /// must be a vertex of its own, or the normal cones the contact tests read from them
    /// (contact_detect.comp) belong to no actual corner: a box resting on such corners sank into them.
    /// </summary>
    /// <param name="sources">Per triangle, its index among the body's own.</param>
    private static CookedSurface Cook(dvec3[] vertices, uvec3[] triangles, int[] sources)
    {
        long profT = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        Dictionary<(uint, uint), uint> edgeIds = [];
        List<uvec4> edges = [];
        // The third and later triangles of edges shared by more than two.
        List<(uint Edge, uint Triangle)> crowded = [];
        uvec4[] triangleEdges = new uvec4[triangles.Length];
        triangles = (uvec3[])triangles.Clone();

        for (int t = 0; t < triangles.Length; t++)
        {
            uvec3 triangle = triangles[t];
            uvec4 own = new();
            for (int k = 0; k < 3; k++)
            {
                uint a = triangle[k];
                uint b = triangle[(k + 1) % 3];
                (uint, uint) key = a < b ? (a, b) : (b, a);
                if (edgeIds.TryGetValue(key, out uint edge))
                {
                    uvec4 existing = edges[(int)edge];
                    if (existing.w == NoBlock)
                        edges[(int)edge] = existing with { w = (uint)t };
                    else
                        crowded.Add((edge, (uint)t));
                }
                else
                {
                    edge = (uint)edges.Count;
                    edgeIds[key] = edge;
                    edges.Add(new uvec4(a, b, (uint)t, NoBlock));
                }

                own[k] = edge;
            }

            triangleEdges[t] = own;
        }

        if (crowded.Count > 0)
            PairWedges(vertices, triangles, edges, crowded, triangleEdges);
        int[] sourceVertices = SplitFans(ref vertices, triangles, edges, triangleEdges);

        // Every vertex and every edge is owned by the first triangle using it.
        bool[] vertexOwned = new bool[vertices.Length];
        for (int t = 0; t < triangles.Length; t++)
        {
            uint ownership = 0;
            for (int k = 0; k < 3; k++)
            {
                if (!vertexOwned[triangles[t][k]])
                {
                    vertexOwned[triangles[t][k]] = true;
                    ownership |= 1u << k;
                }

                if (edges[(int)triangleEdges[t][k]].z == (uint)t)
                    ownership |= 8u << k;
            }

            triangleEdges[t].w = ownership;
        }

        ProfTimes[1] += System.Diagnostics.Stopwatch.GetElapsedTime(profT).TotalMilliseconds; profT = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        (GpuBvhNode[] nodes, uint[] primitives) = BvhBuilder.Build(vertices, triangles, (int)LeafSize);
        ProfTimes[2] += System.Diagnostics.Stopwatch.GetElapsedTime(profT).TotalMilliseconds; // PROF
        return new CookedSurface
        {
            Vertices = vertices,
            SourceVertices = sourceVertices,
            Triangles = triangles,
            SourceTriangles = sources,
            TriangleEdges = triangleEdges,
            Edges = [.. edges],
            BvhNodes = nodes,
            BvhPrimitives = primitives
        };
    }

    /// <summary>
    /// Re-pairs the triangles around each edge that more than two share. Sorted by angle about
    /// the edge, each triangle's solid lies on one side of it (the side its outward normal points
    /// away from); a triangle is joined to the next one round on its solid side, so each pair
    /// bounds one solid wedge, and every pair beyond the first gets an edge of its own. A
    /// triangle left over by inconsistent winding gets an open edge.
    /// </summary>
    private static void PairWedges(dvec3[] vertices, uvec3[] triangles, List<uvec4> edges, List<(uint Edge, uint Triangle)> crowded, uvec4[] triangleEdges)
    {
        Dictionary<uint, List<uint>> around = [];
        foreach ((uint edge, uint triangle) in crowded)
        {
            if (!around.TryGetValue(edge, out List<uint>? list))
                around[edge] = list = [edges[(int)edge].z, edges[(int)edge].w];
            list.Add(triangle);
        }

        foreach ((uint edge, List<uint> list) in around)
        {
            uvec4 record = edges[(int)edge];
            dvec3 origin = vertices[record.x];
            dvec3 axis = (vertices[record.y] - origin).Normalized;
            dvec3 first = dvec3.Zero;
            List<(double Angle, bool SolidAhead, uint Triangle)> sorted = [];
            foreach (uint t in list)
            {
                uvec3 triangle = triangles[t];
                uint far = triangle.x != record.x && triangle.x != record.y ? triangle.x
                    : triangle.y != record.x && triangle.y != record.y ? triangle.y : triangle.z;
                dvec3 into = vertices[far] - origin;
                into -= dvec3.Dot(into, axis) * axis;
                into = into.Length > 0 ? into.Normalized : dvec3.Zero;
                if (first == dvec3.Zero)
                    first = into;

                dvec3 normal = dvec3.Cross(vertices[triangle.y] - vertices[triangle.x], vertices[triangle.z] - vertices[triangle.x]);
                double angle = Math.Atan2(dvec3.Dot(into, dvec3.Cross(axis, first)), dvec3.Dot(into, first));
                // The normal turned a quarter ahead of `into` about the axis means the solid lies
                // a quarter behind it, and the other way round.
                sorted.Add((angle < 0 ? angle + 2 * Math.PI : angle, dvec3.Dot(normal, dvec3.Cross(axis, into)) < 0, t));
            }

            sorted.Sort((p, q) => p.Angle.CompareTo(q.Angle));
            bool[] paired = new bool[sorted.Count];
            bool reused = false;
            for (int i = 0; i < sorted.Count; i++)
            {
                int j = (i + 1) % sorted.Count;
                if (paired[i] || paired[j] || !sorted[i].SolidAhead || sorted[j].SolidAhead)
                    continue;

                paired[i] = paired[j] = true;
                Assign(sorted[i].Triangle, sorted[j].Triangle);
            }

            for (int i = 0; i < sorted.Count; i++)
            {
                if (!paired[i])
                    Assign(sorted[i].Triangle, NoBlock);
            }

            void Assign(uint a, uint b)
            {
                uint id = edge;
                if (reused)
                {
                    id = (uint)edges.Count;
                    edges.Add(record);
                }

                reused = true;
                edges[(int)id] = record with { z = a, w = b };
                foreach (uint t in (uint[])[a, b])
                {
                    if (t == NoBlock)
                        continue;
                    for (int k = 0; k < 3; k++)
                    {
                        if (triangleEdges[t][k] == edge)
                            triangleEdges[t][k] = id;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gives every fan of triangles around a vertex a vertex of its own: two solids touching at a
    /// point, or along an edge, share it otherwise. Rewrites the triangles and edges to the copies
    /// and returns, per vertex, the input vertex it is.
    /// </summary>
    private static int[] SplitFans(ref dvec3[] vertices, uvec3[] triangles, List<uvec4> edges, uvec4[] triangleEdges)
    {
        // Union-find over the corners of the triangles: two corners at one vertex are one fan
        // when an edge through that vertex joins their triangles.
        int[] parent = new int[3 * triangles.Length];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        int Corner(uint t, uint vertex) => (int)(3 * t) + (triangles[t].x == vertex ? 0 : triangles[t].y == vertex ? 1 : 2);

        foreach (uvec4 edge in edges)
        {
            if (edge.w == NoBlock)
                continue;
            foreach (uint vertex in (uint[])[edge.x, edge.y])
            {
                int a = Find(Corner(edge.z, vertex));
                int b = Find(Corner(edge.w, vertex));
                if (a != b)
                    parent[a] = b;
            }
        }

        int[] fanOf = new int[vertices.Length];
        Array.Fill(fanOf, -1);
        Dictionary<int, uint> copies = [];
        List<dvec3> grown = [.. vertices];
        List<int> sourceVertices = [.. Enumerable.Range(0, vertices.Length)];
        for (int t = 0; t < triangles.Length; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                uint vertex = triangles[t][k];
                int fan = Find(3 * t + k);
                if (fanOf[vertex] < 0)
                    fanOf[vertex] = fan;
                if (fanOf[vertex] == fan)
                    continue;

                if (!copies.TryGetValue(fan, out uint copy))
                {
                    copy = (uint)grown.Count;
                    copies[fan] = copy;
                    grown.Add(vertices[vertex]);
                    sourceVertices.Add((int)vertex);
                }

                uvec3 triangle = triangles[t];
                triangle[k] = copy;
                triangles[t] = triangle;
            }
        }

        if (copies.Count == 0)
            return [.. sourceVertices];

        // An edge takes the copies its first triangle uses.
        for (int e = 0; e < edges.Count; e++)
        {
            uvec4 edge = edges[e];
            uvec3 triangle = triangles[edge.z];
            int k = (int)(triangleEdges[edge.z].x == (uint)e ? 0u : triangleEdges[edge.z].y == (uint)e ? 1u : 2u);
            uint a = triangle[k];
            uint b = triangle[(k + 1) % 3];
            bool forward = sourceVertices[(int)a] == sourceVertices[(int)edge.x];
            edges[e] = edge with { x = forward ? a : b, y = forward ? b : a };
        }

        vertices = [.. grown];
        return [.. sourceVertices];
    }

    /// <summary>Cooks a fixed body's surface and its materials ahead of a rebuild, which then finds them done (<see cref="FixedBody.Cook"/>).</summary>
    internal static void Prepare(FixedBody body)
    {
        CookedSurface surface = CookFixed(body);
        long profT = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
        if (body.SurfaceMaterials is IReadOnlyList<SurfaceMaterial> materials)
            PaletteOf(body, materials, surface);
        ProfTimes[4] += System.Diagnostics.Stopwatch.GetElapsedTime(profT).TotalMilliseconds; // PROF
    }

    private static CookedSurface CookFixed(FixedBody body)
    {
        return CookedFixed.GetValue(body, b =>
        {
            long profT = System.Diagnostics.Stopwatch.GetTimestamp(); // PROF
            (dvec3[] vertices, uvec3[] triangles, int[] sources) = SpringUtility.Weld(b.WorldVertices, b.Triangles);
            ProfTimes[0] += System.Diagnostics.Stopwatch.GetElapsedTime(profT).TotalMilliseconds; // PROF
            CookedSurface surface = Cook(vertices, triangles, sources);
            // These outward-rounded bounds are immutable for a fixed body. A topology
            // change elsewhere must not recalculate them for every terrain triangle.
            vec4[] bounds = new vec4[2 * triangles.Length];
            dvec3 lower = new(double.PositiveInfinity);
            dvec3 upper = new(double.NegativeInfinity);
            for (int t = 0; t < triangles.Length; t++)
            {
                uvec3 triangle = triangles[t];
                dvec3 lo = dvec3.Min(dvec3.Min(vertices[triangle.x], vertices[triangle.y]), vertices[triangle.z]);
                dvec3 hi = dvec3.Max(dvec3.Max(vertices[triangle.x], vertices[triangle.y]), vertices[triangle.z]);
                lower = dvec3.Min(lower, lo);
                upper = dvec3.Max(upper, hi);
                bounds[2 * t] = new vec4(BvhBuilder.RoundDown(lo.x), BvhBuilder.RoundDown(lo.y), BvhBuilder.RoundDown(lo.z), 0);
                bounds[2 * t + 1] = new vec4(BvhBuilder.RoundUp(hi.x), BvhBuilder.RoundUp(hi.y), BvhBuilder.RoundUp(hi.z), 0);
            }
            surface.Fixed = new FixedBounds(bounds, lower, upper);
            return surface;
        });
    }

    private static CookedSurface CookRigid(RigidBody body)
    {
        List<(dvec3 Center, CookedSurface Surface)> cooked = CookedRigid.GetOrCreateValue(body.Geometry);
        lock (cooked)
        {
            foreach ((dvec3 center, CookedSurface surface) in cooked)
            {
                if (center == body.CenterOfMass)
                    return surface;
            }

            (dvec3[] vertices, uvec3[] triangles, int[] sources) = SpringUtility.Weld(body.Geometry.Vertices, body.Triangles);
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] -= body.CenterOfMass;

            CookedSurface fresh = Cook(vertices, triangles, sources);
            cooked.Add((body.CenterOfMass, fresh));
            return fresh;
        }
    }

    private static SurfacePalette PaletteOf(PhysicsBody body, IReadOnlyList<SurfaceMaterial> materials, CookedSurface surface)
    {
        if (Palettes.TryGetValue(body, out SurfacePalette? cached)
            && ReferenceEquals(cached.Source, materials) && ReferenceEquals(cached.Surface, surface))
        {
            return cached;
        }

        Dictionary<SurfaceMaterial, uint> indices = [];
        List<SurfaceMaterial> distinct = [];
        uint[] triangleMaterials = new uint[surface.Triangles.Length];
        for (int t = 0; t < triangleMaterials.Length; t++)
        {
            SurfaceMaterial material = materials[surface.SourceTriangles[t]];
            if (!indices.TryGetValue(material, out uint index))
            {
                index = (uint)distinct.Count;
                indices.Add(material, index);
                distinct.Add(material);
            }

            triangleMaterials[t] = index;
        }

        SurfacePalette palette = new(materials, surface, [.. distinct], triangleMaterials);
        Palettes.AddOrUpdate(body, palette);
        return palette;
    }

    private readonly Dictionary<CookedSurface, uint> _rigidBvhRoots = new(ReferenceEqualityComparer.Instance);

    public CollisionGeometry() { }

    public CollisionGeometry
    (
        IReadOnlyList<FixedBody> fixedBodies,
        IReadOnlyList<RigidBody> rigidBodies,
        IReadOnlyList<SoftBody> softBodies,
        uint nodeCount
    ) => Rebuild(fixedBodies, rigidBodies, softBodies, nodeCount);

    /// <summary>Reassembles topology using retained array capacity; no old indices survive.</summary>
    public void Rebuild
    (
        IReadOnlyList<FixedBody> fixedBodies,
        IReadOnlyList<RigidBody> rigidBodies,
        IReadOnlyList<SoftBody> softBodies,
        uint nodeCount,
        bool? parallelAssembly = null
    )
    {
        _assemblies.Clear();
        VertexLocal.Clear();
        VertexWorld.Clear();
        VertexInfo.Clear();
        Triangles.Clear();
        TriangleBounds.Clear();
        Edges.Clear();
        Bodies.Clear();
        BvhNodes.Clear();
        BvhPrimitives.Clear();
        BvhRefit.Clear();
        PaletteMaterials.Clear();
        SoftBvhNodeStart = 0;
        _rigidBvhRoots.Clear();
        // Bodies sharing a cooked surface share one BVH on the GPU.
        BodyMaterialCount = (uint)(rigidBodies.Count + softBodies.Count + fixedBodies.Count);

        for (int r = 0; r < rigidBodies.Count; r++)
        {
            RigidBody body = rigidBodies[r];
            CookedSurface surface = CookRigid(body);
            uint? root = _rigidBvhRoots.TryGetValue(surface, out uint shared) ? shared : null;

            dmat3 rotation = body.Orientation.ToMat3;
            dvec3 center = body.WorldCenterOfMass;
            uint block = nodeCount + (uint)r;
            uint appended = Append
            (
                surface, KindRigid, block, root, body.ContactIdentity, flags: 0,
                new VertexSource((uint)r, rotation, center),
                TriangleMaterials(body, (uint)r, surface)
            );
            _rigidBvhRoots[surface] = appended;
        }

        uint nodeOffset = 0;
        for (int s = 0; s < softBodies.Count; s++)
        {
            SoftBody body = softBodies[s];

            // Only surface nodes can touch anything; interior nodes are left out.
            Dictionary<uint, uint> nodeToVertex = [];
            List<uint> surfaceNodes = [];
            uvec3[] triangles = new uvec3[body.SurfaceTriangles.Length];
            for (int i = 0; i < triangles.Length; i++)
            {
                uvec3 t = body.SurfaceTriangles[i];
                triangles[i] = new uvec3(SurfaceVertex(t.x), SurfaceVertex(t.y), SurfaceVertex(t.z));
            }

            uint SurfaceVertex(uint node)
            {
                if (!nodeToVertex.TryGetValue(node, out uint vertex))
                {
                    vertex = (uint)surfaceNodes.Count;
                    nodeToVertex[node] = vertex;
                    surfaceNodes.Add(node);
                }

                return vertex;
            }

            // Soft bodies change shape, so they are cooked afresh from their current positions.
            // Their BVHs are refit every detection pass, so they are kept together.
            CookedSurface surface = Cook([.. surfaceNodes.Select(n => body.Positions[(int)n])], triangles, [.. Enumerable.Range(0, triangles.Length)]);
            int firstNode = BvhNodes.Count;
            uint firstTriangle = (uint)Triangles.Count;
            uint material = (uint)(rigidBodies.Count + s);
            Append
            (
                surface, KindNode, NoBlock, null, body.ContactIdentity, flags: 0,
                new VertexSource(material, dmat3.Identity, dvec3.Zero, body, surfaceNodes, nodeOffset),
                TriangleMaterials(body, material, surface)
            );
            AppendRefitLinks(firstNode, firstTriangle);

            nodeOffset += (uint)body.NodeCount;
        }

        SoftBvhNodeCount = (uint)BvhRefit.Count;
        DynamicBodyCount = (uint)Bodies.Count;
        DynamicTriangleCount = (uint)Triangles.Count;
        DynamicVertexCount = (uint)VertexInfo.Count;

        for (int f = 0; f < fixedBodies.Count; f++)
        {
            CookedSurface surface = CookFixed(fixedBodies[f]);
            if (surface.Triangles.Length == 0)
                continue;

            uint material = (uint)(rigidBodies.Count + softBodies.Count + f);
            Append
            (
                surface, KindFixed, NoBlock, null, fixedBodies[f].ContactIdentity,
                flags: fixedBodies[f].OpenEdgesContinue ? BodyOpenEdgesContinue : 0,
                new VertexSource(material, dmat3.Identity, dvec3.Zero),
                TriangleMaterials(fixedBodies[f], material, surface)
            );
        }

        // Allocation, palette assignment and shared-BVH lookup finish before any worker
        // starts. Each body then owns disjoint vertex/edge/triangle/body output ranges.
        if ((parallelAssembly ?? ParallelGeometry) && Bodies.Count >= 64
            && Triangles.Count >= 4096 && Environment.ProcessorCount > 1)
        {
            const int batch = 16;
            Parallel.For(0, (_assemblies.Count + batch - 1) / batch,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
                b =>
                {
                    int last = Math.Min((b + 1) * batch, _assemblies.Count);
                    for (int i = b * batch; i < last; i++)
                        Assemble(_assemblies[i]);
                });
        }
        else
        {
            foreach (SurfaceAssembly assembly in _assemblies)
                Assemble(assembly);
        }
        _assemblies.Clear();
    }

    /// <summary>
    /// xyz: fixed vertices' world position, rigid ones' position relative to the center of
    /// mass, unused for soft ones. w: unused.
    /// </summary>
    public List<dvec4> VertexLocal { get; } = [];
    /// <summary>Initial world positions, which seed the start, end and last positions alike.</summary>
    public List<dvec4> VertexWorld { get; } = [];
    /// <summary>x = collision body, y = kind, z = node or rigid block, w = the body's id, which is its own material's.</summary>
    public List<uvec4> VertexInfo { get; } = [];
    public List<GpuTriangle> Triangles { get; } = [];
    /// <summary>Per triangle: lower then upper bound of its initial world position, rounded outward.</summary>
    public List<vec4> TriangleBounds { get; } = [];
    /// <summary>xy = vertices, zw = the triangles on either side or NoBlock.</summary>
    public List<uvec4> Edges { get; } = [];
    public List<GpuCollisionBody> Bodies { get; } = [];
    public List<GpuBvhNode> BvhNodes { get; } = [];
    public List<uint> BvhPrimitives { get; } = [];
    /// <summary>
    /// Per soft body BVH node, from <see cref="SoftBvhNodeStart"/>: x = parent or NoBlock,
    /// y = children refit so far in a pass (0 between passes), z = the body's first triangle.
    /// </summary>
    public List<uvec4> BvhRefit { get; } = [];

    /// <summary>The soft bodies' BVH nodes, which are contiguous.</summary>
    public uint SoftBvhNodeStart { get; private set; }
    public uint SoftBvhNodeCount { get; private set; }
    public uint DynamicBodyCount { get; private set; }
    public uint DynamicTriangleCount { get; private set; }
    public uint DynamicVertexCount { get; private set; }
    /// <summary>The bodies' own materials, the first ones, one per body.</summary>
    public uint BodyMaterialCount { get; private set; }
    /// <summary>The materials after the bodies' own, from their <see cref="PhysicsBody.SurfaceMaterials"/>.</summary>
    public List<SurfaceMaterial> PaletteMaterials { get; } = [];
    public uint MaterialCount => BodyMaterialCount + (uint)PaletteMaterials.Count;

    /// <summary>
    /// The material of each triangle of a body's cooked surface: the body's own, or one of its
    /// <see cref="PhysicsBody.SurfaceMaterials"/>, which are appended for it.
    /// </summary>
    private MaterialSource TriangleMaterials(PhysicsBody body, uint bodyMaterial, CookedSurface surface)
    {
        if (body.SurfaceMaterials is not IReadOnlyList<SurfaceMaterial> materials)
            return new MaterialSource(bodyMaterial);

        SurfacePalette palette = PaletteOf(body, materials, surface);
        uint first = MaterialCount;
        if (first + palette.Distinct.Length > uint.MaxValue >> MaterialShift)
            throw new InvalidOperationException("Too many distinct surface materials");

        PaletteMaterials.AddRange(palette.Distinct);
        return new MaterialSource(first, palette.TriangleMaterials);
    }

    /// <summary>
    /// Appends a body built from a cooked surface and returns its BVH root. A fixed body's
    /// bounds are its triangles' (it never moves); a dynamic body's are computed every
    /// detection pass.
    /// </summary>
    /// <param name="sharedRoot">The root of an identical BVH appended before, to share it, or null to append this one.</param>
    /// <param name="flags">The collision body's flags, such as <see cref="BodyOpenEdgesContinue"/>.</param>
    /// <param name="material">Per triangle, its material.</param>
    private uint Append
    (
        CookedSurface surface,
        uint kind,
        uint rigidBlock,
        uint? sharedRoot,
        uint contactIdentity,
        uint flags,
        VertexSource vertex,
        MaterialSource material
    )
    {
        uint body = (uint)Bodies.Count;
        uint firstVertex = (uint)VertexInfo.Count;
        uint firstTriangle = (uint)Triangles.Count;
        uint firstEdge = (uint)Edges.Count;
        CollectionsMarshal.SetCount(VertexLocal, VertexLocal.Count + surface.Vertices.Length);
        CollectionsMarshal.SetCount(VertexWorld, VertexWorld.Count + surface.Vertices.Length);
        CollectionsMarshal.SetCount(VertexInfo, VertexInfo.Count + surface.Vertices.Length);
        CollectionsMarshal.SetCount(Edges, Edges.Count + surface.Edges.Length);
        CollectionsMarshal.SetCount(Triangles, Triangles.Count + surface.Triangles.Length);
        CollectionsMarshal.SetCount(TriangleBounds, TriangleBounds.Count + 2 * surface.Triangles.Length);
        uint root = sharedRoot ?? AppendBvh(surface.BvhNodes, surface.BvhPrimitives);
        Bodies.Add(default);
        _assemblies.Add(new SurfaceAssembly(surface, kind, rigidBlock, root,
            contactIdentity, flags, body, firstVertex, firstTriangle, firstEdge, vertex, material));
        return root;
    }

    private void Assemble(SurfaceAssembly assembly)
    {
        (CookedSurface surface, uint kind, uint rigidBlock, uint root,
            uint contactIdentity, uint flags, uint body, uint firstVertex,
            uint firstTriangle, uint firstEdge,
            VertexSource vertex, MaterialSource material) = assembly;
        // List counts and capacities are frozen until all workers join. Access through spans
        // avoids concurrent List index setters updating its shared version counter.
        Span<dvec4> locals = CollectionsMarshal.AsSpan(VertexLocal);
        Span<dvec4> worlds = CollectionsMarshal.AsSpan(VertexWorld);
        Span<uvec4> infos = CollectionsMarshal.AsSpan(VertexInfo);
        Span<uvec4> edgesOut = CollectionsMarshal.AsSpan(Edges);
        Span<GpuTriangle> trianglesOut = CollectionsMarshal.AsSpan(Triangles);
        Span<vec4> bounds = CollectionsMarshal.AsSpan(TriangleBounds);
        Span<GpuCollisionBody> bodies = CollectionsMarshal.AsSpan(Bodies);
        for (int i = 0; i < surface.Vertices.Length; i++)
        {
            dvec3 world;
            dvec4 local;
            uint block;
            if (kind == KindNode)
            {
                uint node = vertex.SurfaceNodes![surface.SourceVertices[i]];
                world = vertex.Soft!.Positions[(int)node];
                local = dvec4.Zero;
                block = vertex.NodeOffset + node;
            }
            else
            {
                dvec3 position = surface.Vertices[i];
                world = kind == KindRigid ? vertex.Center + vertex.Rotation * position : position;
                local = new dvec4(position, 0);
                block = rigidBlock;
            }
            uvec4 info = new(body, kind, block, vertex.Material);
            locals[(int)firstVertex + i] = local;
            worlds[(int)firstVertex + i] = new dvec4(world, 0);
            infos[(int)firstVertex + i] = info;
        }

        for (int e = 0; e < surface.Edges.Length; e++)
        {
            uvec4 edge = surface.Edges[e];
            edgesOut[(int)firstEdge + e] = new uvec4
            (
                firstVertex + edge.x,
                firstVertex + edge.y,
                edge.z == NoBlock ? NoBlock : firstTriangle + edge.z,
                edge.w == NoBlock ? NoBlock : firstTriangle + edge.w
            );
        }

        FixedBounds? fixedBounds = kind == KindFixed ? surface.Fixed : null;
        dvec3 bodyLower = fixedBounds?.Lower ?? new dvec3(double.PositiveInfinity);
        dvec3 bodyUpper = fixedBounds?.Upper ?? new dvec3(double.NegativeInfinity);
        if (fixedBounds != null)
            fixedBounds.Triangles.AsSpan().CopyTo(bounds.Slice(2 * (int)firstTriangle, 2 * surface.Triangles.Length));
        for (int t = 0; t < surface.Triangles.Length; t++)
        {
            uvec3 local = surface.Triangles[t];
            uvec4 edges = surface.TriangleEdges[t];
            trianglesOut[(int)firstTriangle + t] = new GpuTriangle
            {
                Vertices = new uvec4(firstVertex + local.x, firstVertex + local.y, firstVertex + local.z, body),
                Edges = new uvec4(firstEdge + edges.x, firstEdge + edges.y, firstEdge + edges.z, edges.w | material.At(t) << MaterialShift)
            };

            if (fixedBounds != null)
                continue;
            dvec3 p0 = worlds[(int)(firstVertex + local.x)].xyz;
            dvec3 p1 = worlds[(int)(firstVertex + local.y)].xyz;
            dvec3 p2 = worlds[(int)(firstVertex + local.z)].xyz;
            dvec3 lower = dvec3.Min(dvec3.Min(p0, p1), p2);
            dvec3 upper = dvec3.Max(dvec3.Max(p0, p1), p2);
            bodyLower = dvec3.Min(bodyLower, lower);
            bodyUpper = dvec3.Max(bodyUpper, upper);
            bounds[2 * ((int)firstTriangle + t)] = new vec4(BvhBuilder.RoundDown(lower.x), BvhBuilder.RoundDown(lower.y), BvhBuilder.RoundDown(lower.z), 0);
            bounds[2 * ((int)firstTriangle + t) + 1] = new vec4(BvhBuilder.RoundUp(upper.x), BvhBuilder.RoundUp(upper.y), BvhBuilder.RoundUp(upper.z), 0);
        }

        bodies[(int)body] = new GpuCollisionBody
        {
            Info = new uvec4(kind, rigidBlock, firstTriangle, (uint)surface.Triangles.Length),
            Extra = new uvec4(root, firstVertex, (uint)surface.Vertices.Length, flags),
            BoundsMin = new vec4(BvhBuilder.RoundDown(bodyLower.x), BvhBuilder.RoundDown(bodyLower.y), BvhBuilder.RoundDown(bodyLower.z), 0),
            BoundsMax = new vec4(BvhBuilder.RoundUp(bodyUpper.x), BvhBuilder.RoundUp(bodyUpper.y), BvhBuilder.RoundUp(bodyUpper.z), 0),
            SweepMin = new uvec4(KeyPositiveInfinity),
            SweepMax = new uvec4(KeyNegativeInfinity),
            Features = new uvec4(contactIdentity, firstEdge, 0, 0)
        };
    }

    /// <summary>Records the parent of each node from <paramref name="firstNode"/> on, all of one soft body's BVH.</summary>
    private void AppendRefitLinks(int firstNode, uint firstTriangle)
    {
        if (BvhRefit.Count == 0)
            SoftBvhNodeStart = (uint)firstNode;

        uint[] parents = new uint[BvhNodes.Count - firstNode];
        Array.Fill(parents, NoBlock);
        for (int i = firstNode; i < BvhNodes.Count; i++)
        {
            GpuBvhNode node = BvhNodes[i];
            if (BitConverter.SingleToUInt32Bits(node.Max.w) != 0)
                continue;

            parents[i + 1 - firstNode] = (uint)i;
            parents[BitConverter.SingleToUInt32Bits(node.Min.w) - firstNode] = (uint)i;
        }

        foreach (uint parent in parents)
            BvhRefit.Add(new uvec4(parent, 0, firstTriangle, 0));
    }

    /// <summary>
    /// Appends a BVH over body-local triangle indices and returns its root node, or NoBlock when
    /// there is nothing to build. Child and primitive offsets are rebased onto the shared arrays.
    /// </summary>
    private uint AppendBvh(GpuBvhNode[] nodes, uint[] primitives)
    {
        if (nodes.Length == 0)
            return NoBlock;

        uint nodeOffset = (uint)BvhNodes.Count;
        uint primitiveOffset = (uint)BvhPrimitives.Count;

        foreach (GpuBvhNode node in nodes)
        {
            GpuBvhNode rebased = node;
            uint a = BitConverter.SingleToUInt32Bits(node.Min.w);
            uint count = BitConverter.SingleToUInt32Bits(node.Max.w);
            rebased.Min.w = BitConverter.UInt32BitsToSingle(a + (count == 0 ? nodeOffset : primitiveOffset));
            BvhNodes.Add(rebased);
        }

        BvhPrimitives.AddRange(primitives);
        return nodeOffset;
    }
}
