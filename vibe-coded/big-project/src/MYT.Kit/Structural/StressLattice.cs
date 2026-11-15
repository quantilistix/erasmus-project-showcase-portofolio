using System.Numerics;
using System.Runtime.InteropServices;

using GlmSharp;

namespace MYT.Kit.Structural;


/// <summary>Elastic moduli of the material filling one cell, in Pa.</summary>
public readonly record struct CellMaterial(double YoungsModulus, double ShearModulus);

/// <summary>
/// The worst stresses over the faces of one cell, in Pa, each zero or positive: the peak
/// tensile and compressive normal stress (axial force plus bending) and the peak shear
/// stress (shear force plus torsion).
/// </summary>
public readonly record struct CellStress(double Tension, double Compression, double Shear);

/// <param name="Iterations">Conjugate gradient iterations run.</param>
/// <param name="RelativeResidual">Out-of-balance load left over the total load.</param>
public readonly record struct LatticeSolveResult(int Iterations, double RelativeResidual, bool Converged);


/// <summary>
/// Linear elastic stress analysis of a structure built from equal cubic cells, such as a
/// voxel object: a rigid-body-spring lattice (Kawai's RBSM). Every cell is a rigid block with
/// three translations and three rotations; every face two cells share is an elastic
/// interface between their centers with axial, shear, bending and torsional stiffness, sized
/// so a row of cells bends and twists like a Timoshenko beam of the same section. The
/// interface forces then give the normal and shear stresses each face carries.
/// <para>
/// Usage: fill <see cref="Forces"/> and <see cref="Moments"/> with the loads (gravity,
/// contacts, inertia) on each cell, call <see cref="Solve"/>, then
/// <see cref="ComputeStresses"/>. Anchored cells are held in place by stiff springs to the
/// ground. A free structure (a group of connected cells with no anchor) is only meaningful if
/// its loads balance, forces and moments summing to zero as inertia relief guarantees; its
/// middle cell is then anchored automatically to remove the rigid-body motions, and carries
/// only rounding error.
/// </para>
/// <para>
/// The solve is a conjugate gradient iteration preconditioned by an algebraic multigrid
/// V-cycle over aggregates of 2×2×2 cells, each aggregate moving rigidly (unsmoothed
/// aggregation with the rigid-body modes, Galerkin coarse operators). Without it, a slender
/// beam of n cells needs on the order of n² iterations; with it, a few tens. The solution of
/// the previous solve is the starting guess, so small load changes converge in a few
/// iterations. Not thread safe; use one lattice from one thread at a time.
/// </para>
/// </summary>
public sealed class StressLattice
{
    // Torsion constant of a square section of side a: J = TorsionFactor · a⁴.
    private const double TorsionFactor = 0.1406;
    // Peak torsional shear stress of a square section: τ = T / (TorsionShearFactor · a³).
    private const double TorsionShearFactor = 0.208;
    // Peak shear stress of a rectangular section over the mean V / A.
    private const double ShearPeakFactor = 1.5;
    // Anchor springs are this much stiffer than the stiffest interface they hold.
    private const double AnchorFactor = 1e3;

    private const int Dof = 6;
    private const int Block = Dof * Dof;
    private const int CoarsestNodes = 64;
    private const int MaxLevels = 24;
    private const int ParallelThreshold = 2048;
    private const int ParallelBatch = 512;
    private const int ResidualReplacementInterval = 20;

    private static readonly ivec3[] Directions =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1)
    ];

    private static long Key(ivec3 cell)
    {
        const long mask = (1L << 21) - 1;
        return (cell.x & mask) | (cell.y & mask) << 21 | (cell.z & mask) << 42;
    }

    /// <summary>e × v for the unit vector along an axis.</summary>
    private static dvec3 AxisCross(int axis, dvec3 v)
    {
        return axis switch
        {
            0 => new dvec3(0.0, -v.z, v.y),
            1 => new dvec3(v.z, 0.0, -v.x),
            _ => new dvec3(-v.y, v.x, 0.0)
        };
    }

    /// <param name="cellSize">Edge length of a cell in m.</param>
    /// <param name="cells">Distinct cell coordinates; cells sharing a face are joined.</param>
    /// <param name="materials">Elastic moduli per cell.</param>
    /// <param name="anchored">Per cell, whether it is held in place.</param>
    public StressLattice(double cellSize, ReadOnlySpan<ivec3> cells, ReadOnlySpan<CellMaterial> materials, ReadOnlySpan<bool> anchored)
    {
        if (!(cellSize > 0.0))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "The cell size must be positive");
        if (materials.Length != cells.Length || anchored.Length != cells.Length)
            throw new ArgumentException("One material and one anchor flag per cell are required");

        CellSize = cellSize;
        int count = cells.Length;
        _cells = cells.ToArray();
        _materials = materials.ToArray();
        _anchored = anchored.ToArray();
        _index = new Dictionary<long, int>(count);
        for (int i = 0; i < count; i++)
        {
            if (!_index.TryAdd(Key(_cells[i]), i))
                throw new ArgumentException($"Cell {_cells[i]} is listed twice", nameof(cells));
        }

        _neighbors = new int[count * Dof];
        for (int i = 0; i < count; i++)
        {
            for (int d = 0; d < 6; d++)
                _neighbors[i * 6 + d] = _index.TryGetValue(Key(_cells[i] + Directions[d]), out int j) ? j : -1;
        }

        AnchorFreeGroups();

        _bonds = new double[count * 3 * 4];
        _anchorStiffness = new double[count];
        _forces = new dvec3[count];
        _moments = new dvec3[count];
        _x = new double[count * Dof];

        for (int i = 0; i < count; i++)
        {
            for (int axis = 0; axis < 3; axis++)
                UpdateBond(i, axis);
        }

        for (int i = 0; i < count; i++)
            UpdateSupport(i);

        _preconditionerStale = true;
    }

    private readonly ivec3[] _cells;
    private readonly CellMaterial[] _materials;
    private readonly bool[] _anchored;
    private readonly Dictionary<long, int> _index;
    // Per cell and direction (+x, −x, +y, −y, +z, −z): the neighbouring cell or −1.
    private readonly int[] _neighbors;
    // Per cell and axis, the interface to the next cell along that axis: kn, ks, kb, kt.
    private readonly double[] _bonds;
    private readonly double[] _anchorStiffness;
    private readonly dvec3[] _forces;
    private readonly dvec3[] _moments;
    // Per cell: displacement (3), rotation (3).
    private readonly double[] _x;

    // Retain Krylov work between bounded worker slices; restarting CG every slice
    // discards its search directions and repeatedly allocates five large vectors.
    private double[] _solveB = [], _solveR = [], _solveZ = [], _solveP = [], _solveQ = [];
    private bool _canResume;
    private double _solveRz;
    private int _solveIterations;

    private Level[] _levels = [];
    private bool _preconditionerStale;

    public double CellSize { get; }
    public int CellCount => _cells.Length;
    public ReadOnlySpan<ivec3> Cells => _cells;

    /// <summary>External force on each cell, N, acting at its center.</summary>
    public Span<dvec3> Forces => _forces;

    /// <summary>External moment on each cell about its center, N·m.</summary>
    public Span<dvec3> Moments => _moments;

    /// <summary>Whether a material change since the last solve left the multigrid hierarchy out of date.</summary>
    public bool PreconditionerStale => _preconditionerStale;

    public int IndexOf(ivec3 cell)
    {
        return _index.TryGetValue(Key(cell), out int i) ? i : -1;
    }

    public dvec3 Displacement(int cell)
    {
        return new dvec3(_x[cell * 6], _x[cell * 6 + 1], _x[cell * 6 + 2]);
    }

    public dvec3 Rotation(int cell)
    {
        return new dvec3(_x[cell * 6 + 3], _x[cell * 6 + 4], _x[cell * 6 + 5]);
    }

    /// <summary>
    /// Changes the moduli of a cell, for instance as damage softens it. The solution stays
    /// valid as a starting guess; the multigrid hierarchy keeps working but is rebuilt on the
    /// next <see cref="RebuildPreconditioner"/> or solve after <see cref="PreconditionerStale"/>.
    /// </summary>
    public void SetMaterial(int cell, CellMaterial material)
    {
        _materials[cell] = material;
        for (int axis = 0; axis < 3; axis++)
        {
            UpdateBond(cell, axis);
            int previous = _neighbors[cell * 6 + 2 * axis + 1];
            if (previous >= 0)
                UpdateBond(previous, axis);
        }

        UpdateSupport(cell);
        for (int d = 0; d < 6; d++)
        {
            int j = _neighbors[cell * 6 + d];
            if (j >= 0)
                UpdateSupport(j);
        }

        _preconditionerStale = true;
        _canResume = false;
    }

    /// <summary>Starts from another lattice's solution where cells coincide, for instance before and after an edit.</summary>
    public void CopySolution(StressLattice other)
    {
        _canResume = false;
        for (int i = 0; i < _cells.Length; i++)
        {
            int j = other.IndexOf(_cells[i]);
            if (j >= 0)
                Array.Copy(other._x, j * Dof, _x, i * Dof, Dof);
        }
    }

    /// <summary>Forgets the solution, so the next solve starts from rest.</summary>
    public void ResetSolution()
    {
        _canResume = false;
        Array.Clear(_x);
    }

    /// <summary>Rebuilds the multigrid hierarchy from the current materials.</summary>
    public void RebuildPreconditioner()
    {
        _canResume = false;
        _levels = BuildHierarchy();
        _preconditionerStale = false;
    }

    /// <summary>
    /// Iterates until the out-of-balance load falls below <paramref name="tolerance"/> times
    /// the total load, or <paramref name="maxIterations"/> are spent. Builds the multigrid
    /// hierarchy first if there is none yet; a stale one is used as it is unless
    /// <paramref name="refreshPreconditioner"/> is set.
    /// </summary>
    /// <param name="continuePrevious">Continue the previous CG search when loads and materials are unchanged.</param>
    public LatticeSolveResult Solve(int maxIterations, double tolerance, bool refreshPreconditioner = false, bool continuePrevious = false)
    {
        int n = _cells.Length * Dof;
        if (_levels.Length == 0 || (refreshPreconditioner && _preconditionerStale))
            RebuildPreconditioner();
        if (_solveB.Length != n)
        {
            _solveB = new double[n]; _solveR = new double[n]; _solveZ = new double[n];
            _solveP = new double[n]; _solveQ = new double[n];
            _canResume = false;
        }
        double[] b = _solveB, r = _solveR, z = _solveZ, p = _solveP, q = _solveQ;
        bool resume = continuePrevious && _canResume;
        for (int i = 0; i < _cells.Length; i++)
        {
            for (int axis = 0; axis < Dof; axis++)
            {
                double value = axis < 3 ? _forces[i][axis] : _moments[i][axis - 3];
                if (b[i * Dof + axis] != value) resume = false;
                b[i * Dof + axis] = value;
            }
        }
        _canResume = false;
        double loadNorm = ScaledNorm(b);
        if (loadNorm == 0.0)
        {
            Array.Clear(_x);
            return new LatticeSolveResult(0, 0.0, true);
        }
        double residual = resume ? ScaledNorm(r) / loadNorm : TrueResidual(b, r) / loadNorm;
        if (resume && residual <= tolerance) residual = TrueResidual(b, r) / loadNorm;
        if (residual <= tolerance)
            return new LatticeSolveResult(0, residual, true);
        double rz;
        if (resume) rz = _solveRz;
        else
        {
            _solveIterations = 0;
            Precondition(r, z);
            Array.Copy(z, p, n);
            rz = Dot(r, z);
        }
        for (int iteration = 1; iteration <= maxIterations; iteration++)
        {
            Multiply(0, p, q);
            double pq = Dot(p, q);
            if (!(pq > 0.0))
                return new LatticeSolveResult(iteration, residual, false);
            double alpha = rz / pq;
            for (int k = 0; k < n; k++)
            {
                _x[k] += alpha * p[k];
                r[k] -= alpha * q[k];
            }
            // Confirm convergence using b - K*x, including across slice boundaries.
            residual = ScaledNorm(r) / loadNorm;
            _solveIterations++;
            if (residual <= tolerance || _solveIterations % ResidualReplacementInterval == 0)
            {
                residual = TrueResidual(b, r) / loadNorm;
                if (residual <= tolerance)
                    return new LatticeSolveResult(iteration, residual, true);
            }
            Precondition(r, z);
            double rzNext = Dot(r, z);
            double beta = rzNext / rz;
            rz = rzNext;
            for (int k = 0; k < n; k++) p[k] = z[k] + beta * p[k];
        }
        _solveRz = rz;
        _canResume = true;
        return new LatticeSolveResult(maxIterations, residual, false);
    }

    /// <summary>r = b − K·x for the current solution; returns its scaled norm.</summary>
    private double TrueResidual(double[] b, double[] r)
    {
        Multiply(0, _x, r);
        for (int k = 0; k < r.Length; k++)
            r[k] = b[k] - r[k];

        return ScaledNorm(r);
    }

    /// <summary>Evaluates every interface of the current solution and keeps each cell's worst.</summary>
    public void ComputeStresses(Span<CellStress> stresses)
    {
        if (stresses.Length < _cells.Length)
            throw new ArgumentException("One entry per cell is required", nameof(stresses));

        CellStress[] result = new CellStress[_cells.Length];
        double h = CellSize;
        double area = h * h;
        double sectionModulus = h * h * h / 6.0;
        double torsionModulus = TorsionShearFactor * h * h * h;

        For(_cells.Length, i =>
        {
            double tension = 0.0;
            double compression = 0.0;
            double shear = 0.0;

            for (int d = 0; d < 6; d++)
            {
                int j = _neighbors[i * 6 + d];
                if (j < 0)
                    continue;

                int axis = d >> 1;
                bool lower = (d & 1) == 0;
                (dvec3 force, dvec3 moment) = InterfaceForces(lower ? i : j, lower ? j : i, axis);

                int a1 = (axis + 1) % 3;
                int a2 = (axis + 2) % 3;
                double axial = force[axis] / area;
                double bending = (Math.Abs(moment[a1]) + Math.Abs(moment[a2])) / sectionModulus;
                double shearForce = Math.Sqrt(force[a1] * force[a1] + force[a2] * force[a2]);

                tension = Math.Max(tension, axial + bending);
                compression = Math.Max(compression, -axial + bending);
                shear = Math.Max(shear, ShearPeakFactor * shearForce / area + Math.Abs(moment[axis]) / torsionModulus);
            }

            result[i] = new CellStress(tension, compression, shear);
        });

        result.CopyTo(stresses);
    }

    /// <summary>
    /// The strain energy of the current solution held by each cell, J: half of the energy of
    /// each interface it shares, ½·(F·δ + M·φ), and all of its anchor springs'.
    /// </summary>
    public void ComputeStrainEnergies(Span<double> energies)
    {
        if (energies.Length < _cells.Length)
            throw new ArgumentException("One entry per cell is required", nameof(energies));

        double[] result = new double[_cells.Length];
        double half = 0.5 * CellSize;
        For(_cells.Length, i =>
        {
            dvec3 u = new(_x[i * 6], _x[i * 6 + 1], _x[i * 6 + 2]);
            dvec3 t = new(_x[i * 6 + 3], _x[i * 6 + 4], _x[i * 6 + 5]);
            double energy = 0.5 * _anchorStiffness[i] * (u.LengthSqr + CellSize * CellSize * t.LengthSqr);

            for (int d = 0; d < 6; d++)
            {
                int j = _neighbors[i * 6 + d];
                if (j < 0)
                    continue;

                int axis = d >> 1;
                int lower = (d & 1) == 0 ? i : j;
                int upper = (d & 1) == 0 ? j : i;
                dvec3 uL = new(_x[lower * 6], _x[lower * 6 + 1], _x[lower * 6 + 2]);
                dvec3 tL = new(_x[lower * 6 + 3], _x[lower * 6 + 4], _x[lower * 6 + 5]);
                dvec3 uU = new(_x[upper * 6], _x[upper * 6 + 1], _x[upper * 6 + 2]);
                dvec3 tU = new(_x[upper * 6 + 3], _x[upper * 6 + 4], _x[upper * 6 + 5]);
                dvec3 gap = uU - uL + half * AxisCross(axis, tL + tU);
                dvec3 twist = tU - tL;
                (dvec3 force, dvec3 moment) = InterfaceForces(lower, upper, axis);
                energy += 0.25 * (dvec3.Dot(force, gap) + dvec3.Dot(moment, twist));
            }

            result[i] = energy;
        });

        result.CopyTo(energies);
    }

    /// <summary>
    /// Force and moment the interface between <paramref name="lower"/> and the next cell
    /// along <paramref name="axis"/>, <paramref name="upper"/>, carries; positive axial force
    /// is tension. The face center moves with each cell rigidly, so the gap across it is
    /// δ = u_U − u_L + h/2 · e × (θ_L + θ_U) and the relative rotation is φ = θ_U − θ_L.
    /// </summary>
    private (dvec3 Force, dvec3 Moment) InterfaceForces(int lower, int upper, int axis)
    {
        double half = 0.5 * CellSize;
        dvec3 uL = new(_x[lower * 6], _x[lower * 6 + 1], _x[lower * 6 + 2]);
        dvec3 tL = new(_x[lower * 6 + 3], _x[lower * 6 + 4], _x[lower * 6 + 5]);
        dvec3 uU = new(_x[upper * 6], _x[upper * 6 + 1], _x[upper * 6 + 2]);
        dvec3 tU = new(_x[upper * 6 + 3], _x[upper * 6 + 4], _x[upper * 6 + 5]);

        dvec3 gap = uU - uL + half * AxisCross(axis, tL + tU);
        dvec3 twist = tU - tL;
        int b = (lower * 3 + axis) * 4;
        return (Stiffen(axis, gap, _bonds[b], _bonds[b + 1]), Stiffen(axis, twist, _bonds[b + 3], _bonds[b + 2]));
    }

    /// <summary>Applies an interface stiffness: <paramref name="along"/> on the axis component, <paramref name="across"/> on the others.</summary>
    private static dvec3 Stiffen(int axis, dvec3 v, double along, double across)
    {
        dvec3 result = across * v;
        result[axis] = along * v[axis];
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Operator
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Interface constants between cell i and the next cell along an axis, two half cells in
    /// series: axial kn = A / (h/2E₁ + h/2E₂), shear ks likewise with G, bending
    /// kb = kn · I/A with I = a⁴/12, torsion kt = ks · J/A.
    /// </summary>
    private void UpdateBond(int i, int axis)
    {
        int b = (i * 3 + axis) * 4;
        int j = _neighbors[i * 6 + 2 * axis];
        if (j < 0)
        {
            Array.Clear(_bonds, b, 4);
            return;
        }

        double h = CellSize;
        double area = h * h;
        CellMaterial m1 = _materials[i];
        CellMaterial m2 = _materials[j];
        double kn = area / (0.5 * h / m1.YoungsModulus + 0.5 * h / m2.YoungsModulus);
        double ks = area / (0.5 * h / m1.ShearModulus + 0.5 * h / m2.ShearModulus);

        _bonds[b] = kn;
        _bonds[b + 1] = ks;
        _bonds[b + 2] = kn * h * h / 12.0;
        _bonds[b + 3] = ks * TorsionFactor * h * h;
    }

    /// <summary>Anchor and regularization springs of a cell, scaled to its stiffest interface.</summary>
    private void UpdateSupport(int i)
    {
        double stiffest = 0.0;
        for (int d = 0; d < 6; d++)
        {
            int j = _neighbors[i * 6 + d];
            if (j >= 0)
            {
                int b = ((d & 1) == 0 ? i * 3 + (d >> 1) : j * 3 + (d >> 1)) * 4;
                stiffest = Math.Max(stiffest, _bonds[b]);
            }
        }

        if (stiffest == 0.0)
            stiffest = _materials[i].YoungsModulus * CellSize;

        _anchorStiffness[i] = _anchored[i] ? AnchorFactor * stiffest : 0.0;
    }

    /// <summary>
    /// Anchors the cell nearest the middle of every face-connected group of cells that has no
    /// anchor, which would otherwise be free to move as a rigid body and leave the system
    /// singular. With balanced loads that anchor carries nothing; with unbalanced ones it
    /// carries the imbalance.
    /// </summary>
    private void AnchorFreeGroups()
    {
        int[] group = new int[_cells.Length];
        Array.Fill(group, -1);
        List<int> members = [];

        for (int seed = 0; seed < _cells.Length; seed++)
        {
            if (group[seed] >= 0)
                continue;

            members.Clear();
            members.Add(seed);
            group[seed] = seed;
            bool anchored = false;
            for (int k = 0; k < members.Count; k++)
            {
                int i = members[k];
                anchored |= _anchored[i];
                for (int d = 0; d < 6; d++)
                {
                    int j = _neighbors[i * 6 + d];
                    if (j >= 0 && group[j] < 0)
                    {
                        group[j] = seed;
                        members.Add(j);
                    }
                }
            }

            if (anchored)
                continue;

            dvec3 middle = dvec3.Zero;
            foreach (int i in members)
                middle += new dvec3(_cells[i]);
            middle /= members.Count;

            // Keep first-minimum tie ordering without a captured per-seed closure.
            int nearest = members[0];
            double best = double.PositiveInfinity;
            foreach (int i in members)
            {
                double distance = (new dvec3(_cells[i]) - middle).LengthSqr;
                if (distance < best)
                {
                    best = distance;
                    nearest = i;
                }
            }
            _anchored[nearest] = true;
        }
    }

    /// <summary>Row i of K·x on the finest level, matrix-free from the interfaces.</summary>
    private void FineRow(int i, double[] x, Span<double> row)
    {
        double half = 0.5 * CellSize;
        dvec3 ui = new(x[i * 6], x[i * 6 + 1], x[i * 6 + 2]);
        dvec3 ti = new(x[i * 6 + 3], x[i * 6 + 4], x[i * 6 + 5]);

        double support = _anchorStiffness[i];
        double rotationalSupport = support * CellSize * CellSize;
        dvec3 force = support * ui;
        dvec3 moment = rotationalSupport * ti;

        for (int d = 0; d < 6; d++)
        {
            int j = _neighbors[i * 6 + d];
            if (j < 0)
                continue;

            int axis = d >> 1;
            bool lower = (d & 1) == 0;
            dvec3 uj = new(x[j * 6], x[j * 6 + 1], x[j * 6 + 2]);
            dvec3 tj = new(x[j * 6 + 3], x[j * 6 + 4], x[j * 6 + 5]);

            int b = ((lower ? i : j) * 3 + axis) * 4;
            dvec3 gap = lower ? uj - ui : ui - uj;
            gap += half * AxisCross(axis, ti + tj);
            dvec3 twist = lower ? tj - ti : ti - tj;

            dvec3 f = Stiffen(axis, gap, _bonds[b], _bonds[b + 1]);
            dvec3 m = Stiffen(axis, twist, _bonds[b + 3], _bonds[b + 2]);
            dvec3 arm = half * AxisCross(axis, f);

            // Gradient of the interface energy with respect to this cell's freedoms.
            if (lower)
            {
                force -= f;
                moment -= arm + m;
            }
            else
            {
                force += f;
                moment += m - arm;
            }
        }

        row[0] = force.x;
        row[1] = force.y;
        row[2] = force.z;
        row[3] = moment.x;
        row[4] = moment.y;
        row[5] = moment.z;
    }

    /// <summary>
    /// One 6×6 block of an interface's stiffness, K_ab = B_aᵀ·Kt·B_b + C_aᵀ·Kr·C_b, where the
    /// gap across the face is B_L·x_L + B_U·x_U with B_L = [−I, h/2·[e]×], B_U = [I, h/2·[e]×],
    /// and the twist is C_L·x_L + C_U·x_U with C_L = [0, −I], C_U = [0, I].
    /// </summary>
    private void InterfaceBlock(int lower, int axis, bool rowUpper, bool columnUpper, Span<double> block)
    {
        int b = (lower * 3 + axis) * 4;
        Span<double> gapRow = stackalloc double[3 * Dof];
        Span<double> gapColumn = stackalloc double[3 * Dof];
        GapMatrix(axis, rowUpper, gapRow);
        GapMatrix(axis, columnUpper, gapColumn);
        double twistSign = rowUpper == columnUpper ? 1.0 : -1.0;

        for (int r = 0; r < Dof; r++)
        {
            for (int c = 0; c < Dof; c++)
            {
                double sum = 0.0;
                for (int s = 0; s < 3; s++)
                {
                    double kt = s == axis ? _bonds[b] : _bonds[b + 1];
                    sum += gapRow[s * Dof + r] * kt * gapColumn[s * Dof + c];
                }

                if (r >= 3 && r == c)
                    sum += twistSign * (r - 3 == axis ? _bonds[b + 3] : _bonds[b + 2]);

                block[r * Dof + c] = sum;
            }
        }
    }

    /// <summary>B_L or B_U of <see cref="InterfaceBlock"/>, 3×6 row-major.</summary>
    private void GapMatrix(int axis, bool upper, Span<double> m)
    {
        double half = 0.5 * CellSize;
        m.Clear();
        for (int s = 0; s < 3; s++)
            m[s * Dof + s] = upper ? 1.0 : -1.0;

        // h/2 · [e]× for e the unit vector along the axis.
        int a1 = (axis + 1) % 3;
        int a2 = (axis + 2) % 3;
        m[a2 * Dof + 3 + a1] = half;
        m[a1 * Dof + 3 + a2] = -half;
    }

    /// <summary>The finest level's diagonal block of a cell: its interfaces' own blocks plus its supports.</summary>
    private void FineDiagonal(int i, Span<double> block)
    {
        Span<double> own = stackalloc double[Block];
        block.Clear();
        double support = _anchorStiffness[i];
        for (int k = 0; k < 3; k++)
        {
            block[k * 7] = support;
            block[(k + 3) * 7] = support * CellSize * CellSize;
        }

        for (int d = 0; d < 6; d++)
        {
            int j = _neighbors[i * 6 + d];
            if (j < 0)
                continue;

            bool lower = (d & 1) == 0;
            InterfaceBlock(lower ? i : j, d >> 1, !lower, !lower, own);
            Add(block, own);
        }
    }

    /// <summary>The finest level's coupling block from cell i to its neighbour in direction d.</summary>
    private void FineCoupling(int i, int d, Span<double> block)
    {
        int j = _neighbors[i * 6 + d];
        bool lower = (d & 1) == 0;
        InterfaceBlock(lower ? i : j, d >> 1, !lower, lower, block);
    }

    // ---------------------------------------------------------------------------------------
    // Multigrid
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One level of the hierarchy. Level 0 is the lattice itself (its operator is applied
    /// matrix-free); every coarser node is an aggregate of up to 2×2×2 nodes of the level
    /// below that moves rigidly about <see cref="Positions"/>.
    /// </summary>
    private sealed class Level
    {
        public int Count;
        public ivec3[] Coordinates = [];
        public dvec3[] Positions = [];
        public int[] Neighbors = [];
        // Coarse levels: diagonal block per node, then one coupling block per direction.
        public double[] Diagonal = [];
        public double[] Couplings = [];
        public double[] DiagonalInverse = [];
        public int[][] Colors = [];
        // Into the next coarser level; empty on the coarsest.
        public int[] Parent = [];
        public dvec3[] ParentOffset = [];
        // From the next finer level: its nodes grouped by aggregate.
        public int[] ChildStart = [];
        public int[] Children = [];
        public double[] Right = [];
        public double[] Solution = [];
        public double[] Residual = [];
        // Coarsest level: Cholesky factor of the dense matrix.
        public double[] Dense = [];
    }

    private Level[] BuildHierarchy()
    {
        List<Level> levels = [];
        Level fine = new()
        {
            Count = _cells.Length,
            Coordinates = _cells,
            Positions = [.. _cells.Select(c => CellSize * (new dvec3(c) + 0.5))],
            Neighbors = _neighbors
        };
        fine.DiagonalInverse = new double[fine.Count * Block];
        For(fine.Count, i =>
        {
            Span<double> diagonal = stackalloc double[Block];
            FineDiagonal(i, diagonal);
            Invert(diagonal, fine.DiagonalInverse.AsSpan(i * Block, Block));
        });
        levels.Add(fine);

        while (levels[^1].Count > CoarsestNodes && levels.Count < MaxLevels)
        {
            Level coarse = Coarsen(levels[^1], levels.Count == 1);
            if (coarse.Count > levels[^1].Count * 7 / 8)
                break;

            levels.Add(coarse);
        }

        foreach (Level level in levels)
        {
            level.Colors = ColorByParity(level.Coordinates);
            level.Right = new double[level.Count * Dof];
            level.Solution = new double[level.Count * Dof];
            level.Residual = new double[level.Count * Dof];
        }

        levels[^1].Parent = [];
        levels[^1].Dense = FactorDense(levels[^1], levels.Count == 1);
        return [.. levels];
    }

    private static int[][] ColorByParity(ivec3[] coordinates)
    {
        List<int> even = [];
        List<int> odd = [];
        for (int i = 0; i < coordinates.Length; i++)
            (((coordinates[i].x + coordinates[i].y + coordinates[i].z) & 1) == 0 ? even : odd).Add(i);

        return [[.. even], [.. odd]];
    }

    /// <summary>
    /// Groups the nodes of <paramref name="fine"/> into 2×2×2 aggregates and computes the
    /// Galerkin operator Pᵀ·K·P, where P maps an aggregate's rigid motion (U, Θ) to each
    /// member's (U + Θ × r, Θ) with r its offset from the aggregate's reference point.
    /// Aggregates are aligned, so two nodes joined across a face either share an aggregate or
    /// lie in aggregates joined across the same face: the coarse level is again a lattice.
    /// </summary>
    private Level Coarsen(Level fine, bool fromLattice)
    {
        Dictionary<long, int> aggregates = [];
        List<ivec3> coordinates = [];
        int[] parent = new int[fine.Count];
        for (int i = 0; i < fine.Count; i++)
        {
            ivec3 coordinate = fine.Coordinates[i] >> 1;
            if (!aggregates.TryGetValue(Key(coordinate), out int a))
            {
                a = coordinates.Count;
                aggregates.Add(Key(coordinate), a);
                coordinates.Add(coordinate);
            }

            parent[i] = a;
        }

        int count = coordinates.Count;
        int[] childCount = new int[count + 1];
        foreach (int a in parent)
            childCount[a + 1]++;
        for (int a = 0; a < count; a++)
            childCount[a + 1] += childCount[a];

        int[] children = new int[fine.Count];
        int[] cursor = (int[])childCount.Clone();
        for (int i = 0; i < fine.Count; i++)
            children[cursor[parent[i]]++] = i;

        dvec3[] positions = new dvec3[count];
        for (int a = 0; a < count; a++)
        {
            dvec3 sum = dvec3.Zero;
            for (int c = childCount[a]; c < childCount[a + 1]; c++)
                sum += fine.Positions[children[c]];
            positions[a] = sum / (childCount[a + 1] - childCount[a]);
        }

        dvec3[] offsets = new dvec3[fine.Count];
        for (int i = 0; i < fine.Count; i++)
            offsets[i] = fine.Positions[i] - positions[parent[i]];

        fine.Parent = parent;
        fine.ParentOffset = offsets;

        Level coarse = new()
        {
            Count = count,
            Coordinates = [.. coordinates],
            Positions = positions,
            Neighbors = new int[count * 6],
            Diagonal = new double[count * Block],
            Couplings = new double[count * 6 * Block],
            DiagonalInverse = new double[count * Block],
            ChildStart = childCount,
            Children = children
        };
        Array.Fill(coarse.Neighbors, -1);

        For(count, a =>
        {
            Span<double> diagonal = coarse.Diagonal.AsSpan(a * Block, Block);
            Span<double> block = stackalloc double[Block];
            Span<double> product = stackalloc double[Block];

            for (int c = childCount[a]; c < childCount[a + 1]; c++)
            {
                int i = children[c];
                if (fromLattice)
                    FineDiagonal(i, block);
                else
                    fine.Diagonal.AsSpan(i * Block, Block).CopyTo(block);

                Galerkin(offsets[i], block, offsets[i], product);
                Add(diagonal, product);

                for (int d = 0; d < 6; d++)
                {
                    int j = fine.Neighbors[i * 6 + d];
                    if (j < 0)
                        continue;

                    if (fromLattice)
                        FineCoupling(i, d, block);
                    else
                        fine.Couplings.AsSpan((i * 6 + d) * Block, Block).CopyTo(block);

                    Galerkin(offsets[i], block, offsets[j], product);
                    if (parent[j] == a)
                    {
                        Add(diagonal, product);
                    }
                    else
                    {
                        coarse.Neighbors[a * 6 + d] = parent[j];
                        Add(coarse.Couplings.AsSpan((a * 6 + d) * Block, Block), product);
                    }
                }
            }

            Invert(diagonal, coarse.DiagonalInverse.AsSpan(a * Block, Block));
        });

        return coarse;
    }

    /// <summary>Pᵢᵀ·K·Pⱼ with P(r) = [I, −[r]×; 0, I].</summary>
    private static void Galerkin(dvec3 ri, ReadOnlySpan<double> k, dvec3 rj, Span<double> result)
    {
        Span<double> pi = stackalloc double[Block];
        Span<double> pj = stackalloc double[Block];
        Span<double> kp = stackalloc double[Block];
        Prolongator(ri, pi);
        Prolongator(rj, pj);

        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                double sum = 0.0;
                for (int s = 0; s < 6; s++)
                    sum += k[r * 6 + s] * pj[s * 6 + c];
                kp[r * 6 + c] = sum;
            }
        }

        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
            {
                double sum = 0.0;
                for (int s = 0; s < 6; s++)
                    sum += pi[s * 6 + r] * kp[s * 6 + c];
                result[r * 6 + c] = sum;
            }
        }
    }

    private static void Prolongator(dvec3 r, Span<double> p)
    {
        p.Clear();
        for (int k = 0; k < 6; k++)
            p[k * 7] = 1.0;

        // −[r]× in the displacement rows, rotation columns.
        p[0 * 6 + 4] = r.z;
        p[0 * 6 + 5] = -r.y;
        p[1 * 6 + 3] = -r.z;
        p[1 * 6 + 5] = r.x;
        p[2 * 6 + 3] = r.y;
        p[2 * 6 + 4] = -r.x;
    }

    private static void Add(Span<double> target, ReadOnlySpan<double> value)
    {
        for (int k = 0; k < target.Length; k++)
            target[k] += value[k];
    }

    /// <summary>Applies the operator of a level: y = K·x.</summary>
    private void Multiply(int levelIndex, double[] x, double[] y)
    {
        Level? level = _levels.Length > levelIndex ? _levels[levelIndex] : null;
        int count = levelIndex == 0 ? _cells.Length : level!.Count;
        For(count, i => Row(levelIndex, i, x, y.AsSpan(i * Dof, Dof)));
    }

    private void Row(int levelIndex, int i, double[] x, Span<double> row)
    {
        if (levelIndex == 0)
        {
            FineRow(i, x, row);
            return;
        }

        Level level = _levels[levelIndex];
        MultiplyBlock(level.Diagonal.AsSpan(i * Block, Block), x.AsSpan(i * Dof, Dof), row, accumulate: false);
        for (int d = 0; d < 6; d++)
        {
            int j = level.Neighbors[i * 6 + d];
            if (j >= 0)
                MultiplyBlock(level.Couplings.AsSpan((i * 6 + d) * Block, Block), x.AsSpan(j * Dof, Dof), row, accumulate: true);
        }
    }

    private static void MultiplyBlock(ReadOnlySpan<double> block, ReadOnlySpan<double> x, Span<double> y, bool accumulate)
    {
        for (int r = 0; r < 6; r++)
        {
            double sum = accumulate ? y[r] : 0.0;
            for (int c = 0; c < 6; c++)
                sum += block[r * 6 + c] * x[c];
            y[r] = sum;
        }
    }

    /// <summary>z = M⁻¹·r, one symmetric V-cycle from a zero guess.</summary>
    private void Precondition(double[] r, double[] z)
    {
        Array.Copy(r, _levels[0].Right, r.Length);
        VCycle(0);
        Array.Copy(_levels[0].Solution, z, z.Length);
    }

    /// <summary>
    /// Red-black block Gauss-Seidel before (red then black) and after (black then red) the
    /// coarse correction, so the cycle is symmetric and can precondition conjugate gradients.
    /// </summary>
    private void VCycle(int levelIndex)
    {
        Level level = _levels[levelIndex];
        if (levelIndex == _levels.Length - 1)
        {
            SolveDense(level);
            return;
        }

        Array.Clear(level.Solution);
        Smooth(levelIndex, 0);
        Smooth(levelIndex, 1);

        Multiply(levelIndex, level.Solution, level.Residual);
        for (int k = 0; k < level.Residual.Length; k++)
            level.Residual[k] = level.Right[k] - level.Residual[k];

        Level coarse = _levels[levelIndex + 1];
        For(coarse.Count, a =>
        {
            dvec3 force = dvec3.Zero;
            dvec3 moment = dvec3.Zero;
            for (int c = coarse.ChildStart[a]; c < coarse.ChildStart[a + 1]; c++)
            {
                int i = coarse.Children[c];
                dvec3 f = new(level.Residual[i * 6], level.Residual[i * 6 + 1], level.Residual[i * 6 + 2]);
                dvec3 m = new(level.Residual[i * 6 + 3], level.Residual[i * 6 + 4], level.Residual[i * 6 + 5]);
                force += f;
                moment += m + dvec3.Cross(level.ParentOffset[i], f);
            }

            WriteSix(coarse.Right, a, force, moment);
        });

        VCycle(levelIndex + 1);

        For(level.Count, i =>
        {
            int a = level.Parent[i];
            dvec3 u = new(coarse.Solution[a * 6], coarse.Solution[a * 6 + 1], coarse.Solution[a * 6 + 2]);
            dvec3 t = new(coarse.Solution[a * 6 + 3], coarse.Solution[a * 6 + 4], coarse.Solution[a * 6 + 5]);
            dvec3 move = u + dvec3.Cross(t, level.ParentOffset[i]);
            level.Solution[i * 6] += move.x;
            level.Solution[i * 6 + 1] += move.y;
            level.Solution[i * 6 + 2] += move.z;
            level.Solution[i * 6 + 3] += t.x;
            level.Solution[i * 6 + 4] += t.y;
            level.Solution[i * 6 + 5] += t.z;
        });

        Smooth(levelIndex, 1);
        Smooth(levelIndex, 0);
    }

    private void Smooth(int levelIndex, int color)
    {
        Level level = _levels[levelIndex];
        int[] nodes = level.Colors[color];

        For(nodes.Length, n =>
        {
            int i = nodes[n];
            Span<double> row = stackalloc double[Dof];
            Span<double> correction = stackalloc double[Dof];
            Row(levelIndex, i, level.Solution, row);
            for (int k = 0; k < Dof; k++)
                row[k] = level.Right[i * Dof + k] - row[k];

            MultiplyBlock(level.DiagonalInverse.AsSpan(i * Block, Block), row, correction, accumulate: false);
            for (int k = 0; k < Dof; k++)
                level.Solution[i * Dof + k] += correction[k];
        });
    }

    private static void WriteSix(double[] target, int i, dvec3 a, dvec3 b)
    {
        target[i * 6] = a.x;
        target[i * 6 + 1] = a.y;
        target[i * 6 + 2] = a.z;
        target[i * 6 + 3] = b.x;
        target[i * 6 + 4] = b.y;
        target[i * 6 + 5] = b.z;
    }

    /// <summary>
    /// Assembles the coarsest level densely and factors it (Cholesky): the factor L in the
    /// lower triangle and its transpose in the upper, so both substitutions read along rows.
    /// </summary>
    private double[] FactorDense(Level level, bool isLattice)
    {
        int n = level.Count * Dof;
        double[] a = new double[n * n];

        Span<double> block = stackalloc double[Block];
        for (int i = 0; i < level.Count; i++)
        {
            if (isLattice)
                FineDiagonal(i, block);
            else
                level.Diagonal.AsSpan(i * Block, Block).CopyTo(block);

            Place(a, n, i, i, block);

            for (int d = 0; d < 6; d++)
            {
                int j = level.Neighbors[i * 6 + d];
                if (j < 0)
                    continue;

                if (isLattice)
                    FineCoupling(i, d, block);
                else
                    level.Couplings.AsSpan((i * 6 + d) * Block, Block).CopyTo(block);

                Place(a, n, i, j, block);
            }
        }

        Cholesky(a, n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < i; j++)
                a[j * n + i] = a[i * n + j];
        }

        return a;
    }

    private static void Place(double[] a, int n, int i, int j, ReadOnlySpan<double> block)
    {
        for (int r = 0; r < 6; r++)
        {
            for (int c = 0; c < 6; c++)
                a[(i * 6 + r) * n + j * 6 + c] += block[r * 6 + c];
        }
    }

    private static void SolveDense(Level level)
    {
        int n = level.Count * Dof;
        double[] l = level.Dense;
        double[] x = level.Solution;
        Array.Copy(level.Right, x, n);

        for (int i = 0; i < n; i++)
            x[i] = (x[i] - Dot(l.AsSpan(i * n, i), x.AsSpan(0, i))) / l[i * n + i];

        for (int i = n - 1; i >= 0; i--)
            x[i] = (x[i] - Dot(l.AsSpan(i * n + i + 1, n - i - 1), x.AsSpan(i + 1, n - i - 1))) / l[i * n + i];
    }

    /// <summary>
    /// In-place Cholesky factorization of a symmetric positive definite matrix into its lower
    /// triangle. A pivot that rounding made non-positive is replaced by a tiny positive one,
    /// which only ever happens for a direction with no stiffness at all.
    /// </summary>
    private static void Cholesky(double[] a, int n)
    {
        double largest = 0.0;
        for (int i = 0; i < n; i++)
            largest = Math.Max(largest, Math.Abs(a[i * n + i]));

        double floor = Math.Max(largest * 1e-14, double.Epsilon);
        for (int j = 0; j < n; j++)
        {
            ReadOnlySpan<double> rowJ = a.AsSpan(j * n, j);
            double pivot = Math.Sqrt(Math.Max(a[j * n + j] - Dot(rowJ, rowJ), floor));
            a[j * n + j] = pivot;

            for (int i = j + 1; i < n; i++)
                a[i * n + j] = (a[i * n + j] - Dot(a.AsSpan(i * n, j), rowJ)) / pivot;
        }
    }

    /// <summary>Inverts a symmetric positive definite 6×6 block through its Cholesky factor.</summary>
    private static void Invert(ReadOnlySpan<double> block, Span<double> inverse)
    {
        double[] l = block.ToArray();
        Cholesky(l, Dof);

        Span<double> column = stackalloc double[Dof];
        for (int c = 0; c < Dof; c++)
        {
            column.Clear();
            column[c] = 1.0;

            for (int i = 0; i < Dof; i++)
            {
                double sum = column[i];
                for (int k = 0; k < i; k++)
                    sum -= l[i * Dof + k] * column[k];
                column[i] = sum / l[i * Dof + i];
            }

            for (int i = Dof - 1; i >= 0; i--)
            {
                double sum = column[i];
                for (int k = i + 1; k < Dof; k++)
                    sum -= l[k * Dof + i] * column[k];
                column[i] = sum / l[i * Dof + i];
            }

            for (int r = 0; r < Dof; r++)
                inverse[r * Dof + c] = column[r];
        }
    }

    // ---------------------------------------------------------------------------------------
    // Vector helpers
    // ---------------------------------------------------------------------------------------

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        ReadOnlySpan<Vector<double>> wideA = MemoryMarshal.Cast<double, Vector<double>>(a);
        ReadOnlySpan<Vector<double>> wideB = MemoryMarshal.Cast<double, Vector<double>>(b);
        Vector<double> wide = Vector<double>.Zero;
        for (int k = 0; k < wideA.Length; k++)
            wide += wideA[k] * wideB[k];

        double sum = Vector.Sum(wide);
        for (int k = wideA.Length * Vector<double>.Count; k < a.Length; k++)
            sum += a[k] * b[k];
        return sum;
    }

    /// <summary>Norm with moments divided by the cell size, so both halves are in newtons.</summary>
    private double ScaledNorm(double[] v)
    {
        double scale = 1.0 / (CellSize * CellSize);
        double sum = 0.0;
        for (int i = 0; i < v.Length; i += 6)
        {
            sum += v[i] * v[i] + v[i + 1] * v[i + 1] + v[i + 2] * v[i + 2];
            sum += scale * (v[i + 3] * v[i + 3] + v[i + 4] * v[i + 4] + v[i + 5] * v[i + 5]);
        }

        return Math.Sqrt(sum);
    }

    private static void For(int count, Action<int> body)
    {
        if (count < ParallelThreshold)
        {
            for (int i = 0; i < count; i++)
                body(i);
            return;
        }

        Parallel.For(0, (count + ParallelBatch - 1) / ParallelBatch, batch =>
        {
            int end = Math.Min(count, (batch + 1) * ParallelBatch);
            for (int i = batch * ParallelBatch; i < end; i++)
                body(i);
        });
    }
}
