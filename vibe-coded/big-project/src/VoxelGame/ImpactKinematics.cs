using GlmSharp;

namespace VoxelGame;

/// <summary>A moving side's incoming motion and the resultant normal contact impulse.</summary>
internal sealed class ImpactMotion(double mass, dmat3 inverseInertia, dvec3 linear, dvec3 angular)
{
    public dvec3 Impulse { get; set; }
    public dvec3 AngularImpulse { get; set; }

    public double ClosingWork => -dvec3.Dot(Impulse, linear) - dvec3.Dot(AngularImpulse, angular);
    public double InverseMassWork => Impulse.LengthSqr / mass + dvec3.Dot(AngularImpulse, inverseInertia * AngularImpulse);
}

internal static class ImpactKinematics
{
    /// <summary>
    /// Energy available at peak compression along the contact impulse distribution.
    /// For generalized impulse p, velocity v and inverse mass M^-1, scaling the impulse by
    /// a removes a*C - a²*D/2 of kinetic energy, where C = -p·v and D = p·M^-1·p.
    /// Compression ends at a = C/D, giving C²/(2D). The magnitude of p cancels: a hit near
    /// the end of a timestep has the same energy as one near the start, even though the
    /// position solver supplies only part of the stopping impulse before restitution.
    /// This is the incoming elastic energy, including the part subsequently returned by
    /// bounce. Tangential translation and common center-of-mass motion contribute no work.
    /// </summary>
    public static double CompressionEnergy(IEnumerable<ImpactMotion> motion)
    {
        double closing = 0, inverseMass = 0;
        foreach (ImpactMotion side in motion)
        {
            closing += side.ClosingWork;
            inverseMass += side.InverseMassWork;
        }
        return closing > 0 && inverseMass > 0 ? 0.5 * closing * (closing / inverseMass) : 0;
    }
}
