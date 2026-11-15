namespace MYT.Kit.Vulkan;


/// <summary>
/// What every body shares: its surface material and which <see cref="PhysicsSystem"/> it is in.
/// </summary>
public abstract class PhysicsBody
{
    private protected PhysicsBody()
    {
    }

    private static long _nextContactIdentity;
    internal uint ContactIdentity { get; private set; } = AllocateContactIdentity();

    // Never reuse a feature lifetime handle, even when an object leaves and rejoins a
    // system. Exhaustion throws instead of allowing an old cache entry to alias a body.
    private static uint AllocateContactIdentity()
    {
        long identity = Interlocked.Increment(ref _nextContactIdentity);
        if (identity >= uint.MaxValue)
            throw new InvalidOperationException("Contact identity space exhausted");
        return (uint)identity;
    }

    internal void InvalidateContactIdentity() => ContactIdentity = AllocateContactIdentity();

    private IReadOnlyList<SurfaceMaterial>? _surfaceMaterials;

    /// <summary>Coulomb friction coefficient. A contact uses the geometric mean of both sides' values.</summary>
    public double Friction { get; set; } = 0.5;

    /// <summary>
    /// Bounciness from 0 to 1. A contact mixes both sides' values as
    /// <see cref="PhysicsSettings.RestitutionMixing"/> says. Only rigid bodies bounce; soft
    /// bodies get their bounce from their springs.
    /// </summary>
    public double Restitution { get; set; }

    /// <summary>
    /// For a body made of several materials: one per triangle of its collision mesh, in order (a
    /// fixed or rigid body's geometry faces, a soft body's <see cref="SoftBody.SurfaceTriangles"/>),
    /// used there in place of <see cref="Friction"/> and <see cref="Restitution"/>. Null for one
    /// material throughout. A vertex or an edge touches with the material of the first triangle
    /// that uses it. Setting it on an added body rebuilds the simulation on the next update;
    /// set a new list to change it, since a list changed in place goes unnoticed.
    /// </summary>
    public IReadOnlyList<SurfaceMaterial>? SurfaceMaterials
    {
        get => _surfaceMaterials;
        set
        {
            if (value != null && value.Count != SurfaceTriangleCount)
                throw new ArgumentException($"One material per collision triangle ({SurfaceTriangleCount}) is required", nameof(value));

            _surfaceMaterials = value;
            InvalidateContactIdentity();
            System?.MarkDirty();
        }
    }

    /// <summary>The system simulating this body, or null when it has not been added to one.</summary>
    public PhysicsSystem? System { get; internal set; }

    /// <summary>Triangles of the collision mesh, which <see cref="SurfaceMaterials"/> gives a material each.</summary>
    private protected abstract int SurfaceTriangleCount { get; }
}
