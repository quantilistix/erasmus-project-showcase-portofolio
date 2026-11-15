using GlmSharp;

namespace MYT.Kit.Vulkan;


/// <summary>
/// Static collision geometry, such as a level. It never moves and has infinite mass; the mesh
/// should be wound counter-clockwise seen from the side bodies may touch. To move one, remove
/// it and add a new one.
/// </summary>
public sealed class FixedBody : PhysicsBody
{
    /// <param name="geometry">The collision mesh.</param>
    /// <param name="transform">Mesh space to world; identity when omitted.</param>
    public FixedBody(Mesh3D geometry, dmat4? transform = null)
    {
        Geometry = geometry;
        Transform = transform ?? dmat4.Identity;
        Triangles = SpringUtility.ExtractSurfaceTriangles(geometry);

        WorldVertices = new dvec3[geometry.Vertices.Length];
        for (int i = 0; i < geometry.Vertices.Length; i++)
            WorldVertices[i] = (Transform * new dvec4(geometry.Vertices[i], 1.0)).swizzle.xyz;
    }

    public Mesh3D Geometry { get; }
    public dmat4 Transform { get; }

    /// <summary>
    /// Whether the surface goes on past this body's open edges into neighbouring fixed bodies,
    /// as a terrain split into chunks does, rather than ending there like a sheet's rim.
    /// Contacts then take an open edge, and the vertices on it, as lying inside the surface, so
    /// bodies sliding over a seam between chunks do not catch on it. Read when the body is added.
    /// </summary>
    public bool OpenEdgesContinue { get; init; }

    internal dvec3[] WorldVertices { get; }
    internal uvec3[] Triangles { get; }

    /// <summary>
    /// Prepares what the physics needs of the body's surface (welded, its edges and their
    /// owners, a bounding volume hierarchy, its materials), which the update after adding it
    /// would otherwise do, at a cost that grows with the mesh. Safe on any thread, so that a
    /// body can be made ready in the background before it is added. Set
    /// <see cref="PhysicsBody.SurfaceMaterials"/> first.
    /// </summary>
    public void Cook()
    {
        CollisionGeometry.Prepare(this);
    }

    private protected override int SurfaceTriangleCount => Triangles.Length;
}
