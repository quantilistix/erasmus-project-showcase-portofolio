using GlmSharp;

namespace VoxelGame;


/// <summary>
/// Mass properties of a set of equal cubic cells moving as one rigid body: mass (kg), center of
/// mass and inertia about it, in the cells' frame.
/// </summary>
internal readonly record struct RigidMass(double Mass, dvec3 Center, dmat3 Inertia)
{
    /// <param name="cellEdge">Edge length of the cells, m: a voxel body's, or the player's smaller ones.</param>
    public static RigidMass Of(ReadOnlySpan<ivec3> cells, ReadOnlySpan<double> masses, double cellEdge = VoxelBody.CellSize)
    {
        double mass = 0.0;
        dvec3 moment = dvec3.Zero;
        for (int i = 0; i < cells.Length; i++)
        {
            mass += masses[i];
            moment += masses[i] * CellCenter(cells[i], cellEdge);
        }

        dvec3 center = mass > 0.0 ? moment / mass : dvec3.Zero;
        dmat3 inertia = dmat3.Zero;
        for (int i = 0; i < cells.Length; i++)
        {
            double m = masses[i];
            dvec3 r = CellCenter(cells[i], cellEdge) - center;
            // A cube's own inertia (m·a²/6) plus the parallel axis term m·(|r|²·I − r·rᵀ).
            double own = CellInertia(m, cellEdge) + m * r.LengthSqr;
            inertia += new dmat3
            (
                own - m * r.x * r.x, -m * r.y * r.x, -m * r.z * r.x,
                -m * r.x * r.y, own - m * r.y * r.y, -m * r.z * r.y,
                -m * r.x * r.z, -m * r.y * r.z, own - m * r.z * r.z
            );
        }

        return new RigidMass(mass, center, inertia) { CellEdge = cellEdge };
    }

    private static dvec3 CellCenter(ivec3 cell, double cellEdge)
    {
        return (new dvec3(cell) + 0.5) * cellEdge;
    }

    /// <summary>A cell's own moment of inertia about any axis through its center.</summary>
    private static double CellInertia(double mass, double cellEdge)
    {
        return mass * cellEdge * cellEdge / 6.0;
    }

    /// <summary>Edge length of the cells, m.</summary>
    public double CellEdge { get; init; } = VoxelBody.CellSize;

    /// <summary>
    /// Inertia relief (d'Alembert): adds to every cell the inertia loads −m·(a + α × r + ω × (ω × r))
    /// and −I_cell·α, where a and α are the accelerations the loads already on the cells give
    /// them as a rigid body spinning at <paramref name="omega"/>. The loads then balance, as they
    /// do on a body accelerating freely.
    /// </summary>
    public void Relieve(ReadOnlySpan<ivec3> cells, ReadOnlySpan<double> masses, Span<dvec3> forces, Span<dvec3> moments, dvec3 omega)
    {
        if (Mass <= 0.0)
            return;

        dvec3 totalForce = dvec3.Zero;
        dvec3 totalMoment = dvec3.Zero;
        for (int i = 0; i < cells.Length; i++)
        {
            dvec3 r = CellCenter(cells[i], CellEdge) - Center;
            totalForce += forces[i];
            totalMoment += dvec3.Cross(r, forces[i]) + moments[i];
        }

        dvec3 acceleration = totalForce / Mass;
        dvec3 angularAcceleration = Inertia.Inverse * (totalMoment - dvec3.Cross(omega, Inertia * omega));

        for (int i = 0; i < cells.Length; i++)
        {
            double m = masses[i];
            dvec3 r = CellCenter(cells[i], CellEdge) - Center;
            dvec3 pointAcceleration = acceleration + dvec3.Cross(angularAcceleration, r) + dvec3.Cross(omega, dvec3.Cross(omega, r));
            forces[i] -= m * pointAcceleration;
            moments[i] -= CellInertia(m, CellEdge) * angularAcceleration;
        }
    }
}
