using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>The latest stress analysis of a body, valid for the cells it lists.</summary>
/// <param name="Version">The topology version it was computed for.</param>
/// <param name="Cells">Every cell of the body at that version.</param>
/// <param name="Stresses">Per cell, the worst stresses over its faces.</param>
/// <param name="Settled">
/// Solved closely enough to damage blocks by; an unsettled one, a few iterations from it,
/// is good enough to show.
/// </param>
/// <param name="Index">
/// Where each cell is in <paramref name="Cells"/>, for the map's hundreds of thousands of
/// cells in a box; without it, the body indexes them itself.
/// </param>
internal sealed record StressSnapshot(int Version, ivec3[] Cells, CellStress[] Stresses, bool Settled, BoxIndex? Index = null);


/// <summary>Where each cell of a box [low, high) is in a list of cells, −1 for those not in it, found without hashing.</summary>
internal sealed class BoxIndex
{
    public BoxIndex(ivec3 low, ivec3 high)
    {
        _low = low;
        _size = high - low;
        _indices = new int[_size.x * _size.y * _size.z];
        Array.Fill(_indices, -1);
    }

    private readonly ivec3 _low;
    private readonly ivec3 _size;
    private readonly int[] _indices;

    public int this[ivec3 cell]
    {
        get
        {
            int slot = SlotOf(cell);
            return slot < 0 ? -1 : _indices[slot];
        }
        set => _indices[SlotOf(cell)] = value;
    }

    private int SlotOf(ivec3 cell)
    {
        ivec3 local = cell - _low;
        if (local.x < 0 || local.y < 0 || local.z < 0 || local.x >= _size.x || local.y >= _size.y || local.z >= _size.z)
            return -1;

        return local.x + _size.x * (local.y + _size.y * local.z);
    }
}


/// <summary>
/// A body made of equal cubic cells that contacts press on: the fixed map, a voxel object or
/// the player. Cell (x, y, z) is the cube [x, x + 1) × [y, y + 1) × [z, z + 1) scaled by
/// <see cref="CellEdge"/>, in the body's own frame, which <see cref="Position"/> and
/// <see cref="Orientation"/> place in the world. Impacts and loads treat every kind alike.
/// </summary>
internal abstract class CellBody
{
    protected CellBody(int id)
    {
        Id = id;
    }

    public int Id { get; }

    /// <summary>
    /// Contact loads per cell in the body's frame, force and moment about the cell center,
    /// smoothed over time so that what the stress analysis sees is the sustained load.
    /// </summary>
    public Dictionary<ivec3, (dvec3 Force, dvec3 Moment)> ContactLoads { get; } = [];

    /// <summary>Contact loads gathered during the current step, before smoothing.</summary>
    public Dictionary<ivec3, (dvec3 Force, dvec3 Moment)> StepLoads { get; } = [];

    /// <summary>Edge length of the cells, m.</summary>
    public abstract double CellEdge { get; }

    public abstract dvec3 Position { get; }
    public abstract dquat Orientation { get; }

    /// <summary>The rigid body that moves it; null for one that never moves.</summary>
    public abstract RigidBody? Rigid { get; }

    public virtual dvec3 LinearVelocity => Rigid?.LinearVelocity ?? dvec3.Zero;
    public virtual dvec3 AngularVelocity => Rigid?.AngularVelocity ?? dvec3.Zero;

    public dvec3 ToWorld(dvec3 local)
    {
        return Position + Orientation * local;
    }

    public dvec3 ToLocal(dvec3 world)
    {
        return Orientation.Conjugate * (world - Position);
    }

    public dvec3 DirectionToLocal(dvec3 direction)
    {
        return Orientation.Conjugate * direction;
    }

    /// <summary>A cell's center in the body's frame, m.</summary>
    public dvec3 CenterOf(ivec3 cell)
    {
        return (new dvec3(cell) + 0.5) * CellEdge;
    }

    /// <summary>Every cell of the body.</summary>
    public abstract IEnumerable<ivec3> OccupiedCells();

    /// <summary>What a cell is made of.</summary>
    public abstract BlockMaterial MaterialAt(ivec3 cell);

    /// <summary>A cell's health, from 1 (intact) to 0 (broken).</summary>
    public abstract double HealthAt(ivec3 cell);

    /// <summary>
    /// The cell a contact on this body's surface presses on: the cell just inside the surface
    /// at <paramref name="localPoint"/> (body frame, in cells), going along <paramref name="inward"/>.
    /// </summary>
    public abstract bool TryFindSurfaceCell(dvec3 localPoint, dvec3 inward, out ivec3 cell);

    /// <summary>The stresses a cell carries under the sustained loads in the latest analysis; zero without one.</summary>
    public abstract CellStress StaticStress(ivec3 cell);

    /// <summary>Kinetic energy (J) of the body moving at the given velocities, the angular one in world axes.</summary>
    public abstract double KineticEnergy(dvec3 linear, dvec3 angular);
}


/// <summary>
/// Voxel cells held together: the fixed map or a moving object. Its cells are blocks of
/// <see cref="CellSize"/>, kept in a grid.
/// </summary>
internal abstract class VoxelBody : CellBody
{
    /// <summary>Cell edge length in m.</summary>
    public const double CellSize = 1.0;

    public static dvec3 CellCenter(ivec3 cell)
    {
        return (new dvec3(cell) + 0.5) * CellSize;
    }

    protected VoxelBody(int id) : base(id)
    {
    }

    public Grid3D<Voxel> Grid { get; } = new() { TrackDiagonalChanges = true };

    /// <summary>Bumped whenever cells are added or removed; stress results carry the version they were computed for.</summary>
    public int TopologyVersion { get; set; }

    /// <summary>Cells removed since the last connectivity check.</summary>
    public HashSet<ivec3> RemovedCells { get; } = [];

    /// <summary>Chunks whose geometry changed and need new render meshes.</summary>
    public HashSet<ivec3> MeshDirty { get; } = [];

    /// <summary>Chunks whose per-cell damage or stress changed and need new render data.</summary>
    public HashSet<ivec3> DataDirty { get; } = [];

    public StressSnapshot? Stress { get; set; }

    /// <summary>Index of each cell in <see cref="Stress"/>, unless it has its own <see cref="StressSnapshot.Index"/>.</summary>
    public Dictionary<ivec3, int> StressIndex { get; } = [];

    /// <summary>Damage level per cell last sent to the stress analysis, in sixteenths.</summary>
    public Dictionary<ivec3, int> SentDamage { get; } = [];

    public override double CellEdge => CellSize;

    public override IEnumerable<ivec3> OccupiedCells()
    {
        return Grid.Occupied().Select(c => c.Cell);
    }

    public override BlockMaterial MaterialAt(ivec3 cell)
    {
        return Grid[cell].Material;
    }

    public override double HealthAt(ivec3 cell)
    {
        return Grid[cell].Health;
    }

    /// <summary>
    /// The occupied cell just inside the surface at the contact point. A contact on an edge or
    /// corner can lie between cells; then the deepest of the occupied cells around it wins.
    /// </summary>
    public override bool TryFindSurfaceCell(dvec3 localPoint, dvec3 inward, out ivec3 cell)
    {
        dvec3 probe = localPoint + 0.25 * inward;
        cell = new ivec3((int)Math.Floor(probe.x), (int)Math.Floor(probe.y), (int)Math.Floor(probe.z));
        if (Grid.IsOccupied(cell))
            return true;

        ivec3 lowest = new((int)Math.Floor(localPoint.x - 0.5), (int)Math.Floor(localPoint.y - 0.5), (int)Math.Floor(localPoint.z - 0.5));
        double best = double.NegativeInfinity;
        bool found = false;
        for (int k = 0; k < 8; k++)
        {
            ivec3 candidate = lowest + new ivec3(k & 1, (k >> 1) & 1, k >> 2);
            if (!Grid.IsOccupied(candidate))
                continue;

            double depth = dvec3.Dot(CellCenter(candidate) - localPoint, inward);
            if (depth > best)
            {
                best = depth;
                cell = candidate;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Exact specialization of TryFindSurfaceCell when the sole occupied cell is known.
    /// The probe wins first, then the same eight-cell search box. With one candidate the
    /// depth ranking cannot change the result. Failed lookups retain the probe cell too.
    /// </summary>
    internal static bool TryFindSingleSurfaceCell(ivec3 occupied, dvec3 localPoint, dvec3 inward, out ivec3 cell)
    {
        dvec3 probe = localPoint + 0.25 * inward;
        cell = new ivec3((int)Math.Floor(probe.x), (int)Math.Floor(probe.y), (int)Math.Floor(probe.z));
        if (cell == occupied)
            return true;
        ivec3 lowest = new((int)Math.Floor(localPoint.x - 0.5), (int)Math.Floor(localPoint.y - 0.5), (int)Math.Floor(localPoint.z - 0.5));
        ivec3 delta = occupied - lowest;
        if ((uint)delta.x > 1 || (uint)delta.y > 1 || (uint)delta.z > 1)
            return false;
        cell = occupied;
        return true;
    }

    /// <summary>Records a cell's damage and whether its render data needs refreshing.</summary>
    public void SetDamage(ivec3 cell, double damage)
    {
        Voxel voxel = Grid[cell];
        ushort value = Voxel.ToDamage(damage);
        if (value == voxel.Damage)
            return;

        Grid.Update(cell, voxel with { Damage = value });
        DataDirty.Add(Grid3D<Voxel>.ChunkOf(cell));
    }

    /// <summary>
    /// Takes a new analysis. Only the chunks where some cell's stress changed by a percent, or
    /// which gained or lost cells, need new render data. A snapshot of other cells than the
    /// last is matched to it cell by cell when both have a <see cref="BoxIndex"/>, as the map's
    /// do; otherwise every chunk of either is refreshed, and the body indexes the new cells.
    /// </summary>
    public void SetStress(StressSnapshot snapshot)
    {
        StressSnapshot? previous = Stress;
        Stress = snapshot;
        if (previous != null && previous.Cells == snapshot.Cells)
        {
            // Cells come chunk by chunk: a changed cell in the chunk just added adds nothing.
            ivec3 last = new(int.MinValue);
            for (int i = 0; i < snapshot.Cells.Length; i++)
            {
                if (!Changed(previous.Stresses[i], snapshot.Stresses[i]))
                    continue;
                ivec3 chunk = Grid3D<Voxel>.ChunkOf(snapshot.Cells[i]);
                if (chunk != last)
                    DataDirty.Add(last = chunk);
            }

            return;
        }

        StressIndex.Clear();
        if (previous?.Index is BoxIndex before && snapshot.Index is BoxIndex after)
        {
            for (int i = 0; i < snapshot.Cells.Length; i++)
            {
                int old = before[snapshot.Cells[i]];
                if (old < 0 || Changed(previous.Stresses[old], snapshot.Stresses[i]))
                    DataDirty.Add(Grid3D<Voxel>.ChunkOf(snapshot.Cells[i]));
            }

            // The cells left out now, as the map's analysis moves with the player, lose theirs.
            foreach (ivec3 cell in previous.Cells)
            {
                if (after[cell] < 0)
                    DataDirty.Add(Grid3D<Voxel>.ChunkOf(cell));
            }

            return;
        }

        if (previous != null)
            MarkDataDirty(previous.Cells);
        if (snapshot.Index == null)
        {
            for (int i = 0; i < snapshot.Cells.Length; i++)
                StressIndex[snapshot.Cells[i]] = i;
        }

        MarkDataDirty(snapshot.Cells);
    }

    /// <summary>Whether a cell's stress changed by more than a percent, as much as shows.</summary>
    private static bool Changed(CellStress a, CellStress b)
    {
        double scale = 0.01 * Math.Max(Math.Max(b.Tension, b.Compression), Math.Max(b.Shear, 1.0));
        return Math.Abs(a.Tension - b.Tension) > scale || Math.Abs(a.Compression - b.Compression) > scale || Math.Abs(a.Shear - b.Shear) > scale;
    }

    /// <summary>Forgets the analysis, when there is nothing left to analyse.</summary>
    public void ClearStress()
    {
        if (Stress == null)
            return;

        MarkDataDirty(Stress.Cells);
        Stress = null;
        StressIndex.Clear();
    }

    /// <summary>Where a cell is in the latest analysis, −1 if it is not in it.</summary>
    private int StressIndexOf(ivec3 cell)
    {
        if (Stress == null)
            return -1;
        if (Stress.Index is BoxIndex box)
            return box[cell];

        return StressIndex.TryGetValue(cell, out int index) ? index : -1;
    }

    public override CellStress StaticStress(ivec3 cell)
    {
        int index = StressIndexOf(cell);
        return index >= 0 ? Stress!.Stresses[index] : default;
    }

    /// <summary>
    /// How close a cell is to failing: its worst stress over its elastic limit at its current
    /// health (1 means it starts to break). Zero without a stress result for the cell.
    /// </summary>
    public double StressRatio(ivec3 cell, Voxel voxel)
    {
        int index = StressIndexOf(cell);
        return index >= 0 ? voxel.Material.ElasticLimit(voxel.Health).Ratio(Stress!.Stresses[index]) : 0.0;
    }

    private void MarkDataDirty(ivec3[] cells)
    {
        // Cells come chunk by chunk, so most are in the chunk of the one before.
        ivec3 last = new(int.MinValue);
        foreach (ivec3 cell in cells)
        {
            ivec3 chunk = Grid3D<Voxel>.ChunkOf(cell);
            if (chunk != last)
                DataDirty.Add(chunk);
            last = chunk;
        }
    }
}


/// <summary>
/// The level: fixed in the world, anchored by its bedrock. Every chunk with faces is one
/// <see cref="FixedBody"/>, so an edit rebuilds only the chunks it touches. A map built whole
/// is present everywhere; one streamed in (<see cref="World.LoadColumn"/>) only in the chunk
/// columns loaded, which reach from the bedrock up through <see cref="HeightInChunks"/> chunks.
/// </summary>
internal sealed class VoxelMap(int id) : VoxelBody(id)
{
    /// <summary>How many chunks high a streamed map's columns are at most, from z = 0.</summary>
    public const int HeightInChunks = 32;

    public static bool IsAnchor(Voxel voxel)
    {
        return voxel.Type == BlockType.Bedrock;
    }

    /// <summary>The chunk column a chunk belongs to.</summary>
    public static ivec2 ColumnOfChunk(ivec3 chunk)
    {
        return new ivec2(chunk.x, chunk.y);
    }

    /// <summary>The chunk column a cell belongs to.</summary>
    public static ivec2 ColumnOfCell(ivec3 cell)
    {
        return ColumnOfChunk(Grid3D<Voxel>.ChunkOf(cell));
    }

    public Dictionary<ivec3, FixedBody> Colliders { get; } = [];

    /// <summary>The chunk columns streamed in; null for a map built whole, present everywhere.</summary>
    public HashSet<ivec2>? LoadedColumns { get; set; }

    /// <summary>Columns changed since they were generated, which unloading must keep.</summary>
    public HashSet<ivec2> EditedColumns { get; } = [];

    /// <summary>Bumped whenever a column is loaded or unloaded.</summary>
    public int ColumnVersion { get; set; }

    public override dvec3 Position => dvec3.Zero;
    public override dquat Orientation => dquat.Identity;
    public override RigidBody? Rigid => null;

    public bool IsLoaded(ivec2 column)
    {
        return LoadedColumns == null || LoadedColumns.Contains(column);
    }

    public override double KineticEnergy(dvec3 linear, dvec3 angular)
    {
        return 0.0;
    }
}


/// <summary>A rigid voxel object: its cells move together as one <see cref="RigidBody"/>.</summary>
internal sealed class VoxelObject(int id) : VoxelBody(id)
{
    public RigidBody? Body { get; set; }

    // Pose and motion while there is no rigid body, such as before the first one is built or
    // while it is parked.
    public dvec3 PendingPosition { get; set; }
    public dquat PendingOrientation { get; set; } = dquat.Identity;
    public dvec3 PendingLinearVelocity { get; set; }
    public dvec3 PendingAngularVelocity { get; set; }

    /// <summary>
    /// Out of the physics while it lies beyond the simulated region around the player
    /// (<see cref="World.FollowPlayer"/>), held where it was, with the motion it had, until
    /// the region takes it in again.
    /// </summary>
    public bool Parked { get; set; }

    /// <summary>A sphere around its cells, in its own frame, as of its last rebuild: center and radius, m.</summary>
    public (dvec3 Center, double Radius) Bounds { get; set; }

    public override dvec3 Position => Body?.Position ?? PendingPosition;
    public override dquat Orientation => Body?.Orientation ?? PendingOrientation;
    public override RigidBody? Rigid => Body;
    public override dvec3 LinearVelocity => Body?.LinearVelocity ?? PendingLinearVelocity;
    public override dvec3 AngularVelocity => Body?.AngularVelocity ?? PendingAngularVelocity;

    /// <summary>Pushes at a world point, retaining the resulting motion even if mining rebuilds or splits this body before a step.</summary>
    public void ApplyMiningImpulse(dvec3 impulse, dvec3 worldPoint)
    {
        RigidMass mass = Body is RigidBody rigid
            ? new RigidMass(rigid.Mass, rigid.CenterOfMass, rigid.LocalInertia)
            : MassProperties();
        if (!(mass.Mass > 0.0))
            return;

        dmat3 rotation = Orientation.ToMat3;
        dvec3 linear = LinearVelocity + impulse / mass.Mass;
        dvec3 angular = AngularVelocity + rotation * mass.Inertia.Inverse * rotation.Transposed
            * dvec3.Cross(worldPoint - ToWorld(mass.Center), impulse);
        if (Body != null)
        {
            // Velocity setters also update the CPU pose used by topology changes; a queued
            // GPU-only impulse would be lost when the old rigid body is replaced.
            Body.LinearVelocity = linear;
            Body.AngularVelocity = angular;
        }
        else
        {
            PendingLinearVelocity = linear;
            PendingAngularVelocity = angular;
        }
    }

    public override double KineticEnergy(dvec3 linear, dvec3 angular)
    {
        if (Body == null)
            return 0.0;

        dvec3 spin = Orientation.Conjugate * angular;
        return 0.5 * Body.Mass * linear.LengthSqr + 0.5 * dvec3.Dot(spin, Body.LocalInertia * spin);
    }

    /// <summary>Mass (kg), center of mass and inertia about it (body frame) of the cells, from each block's density.</summary>
    public RigidMass MassProperties()
    {
        List<ivec3> cells = [];
        List<double> masses = [];
        bool shaped = Grid.Occupied().Any(c => BlockShapes.HasShape(c.Value.Type));
        double edge = shaped ? CellSize / 2 : CellSize;
        foreach ((ivec3 cell, Voxel voxel) in Grid.Occupied())
        {
            if (shaped)
            {
                foreach (ivec3 part in BlockShapes.Parts(voxel))
                {
                    cells.Add(cell * 2 + part);
                    masses.Add(voxel.Material.Density * edge * edge * edge);
                }
            }
            else
            {
                cells.Add(cell);
                masses.Add(voxel.Material.Density * edge * edge * edge);
            }
        }

        return RigidMass.Of(CollectionsMarshal.AsSpan(cells), CollectionsMarshal.AsSpan(masses), edge);
    }
}
