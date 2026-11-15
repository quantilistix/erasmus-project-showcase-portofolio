using System.Runtime.CompilerServices;
using GlmSharp;
using static VoxelGame.BoxCollision;

namespace VoxelGame;

/// <summary>CPU voxel queries at one simulation pose, with immediate refits after positional recovery.</summary>
internal sealed class VoxelCollisionQueries
{
    private const double RecoveryTolerance = 0.01;
    private sealed class Shape
    {
        public int Version = -1, Count = -1;
        public dvec3[] Centers = [];
        public dvec3[] HalfEdges = [];
        public dvec3 Center, Half;
        public SpatialIndex? Cells;
        public long TerrainGeneration = -1;
        public dvec3 TerrainCenter;
        public dquat TerrainRotation;
        public double TerrainAllowance;
    }
    private static readonly bool ReuseTerrain = Environment.GetEnvironmentVariable("MYT_TERRAIN_REUSE") != "0";
    private readonly ConditionalWeakTable<VoxelObject, Shape> _shapes = new();
    private readonly SpatialIndex _index = new();
    private readonly List<int> _candidates = [], _sourceCandidates = [];
    private Shape? _sourceShape;
    private CellBody _sourceBody = null!;
    private VoxelObject?[] _members = [];
    private SpatialBounds[] _bounds = [], _targetBounds = [];
    private CollisionBox[] _source = [];
    private IReadOnlyList<VoxelObject> _objects = null!;
    private VoxelMap _map = null!;
    private int _count = -1;
    private long _mapRevision = -1, _terrainGeneration;
    public long TerrainQueriesSkipped { get; private set; }
    public long CellTests { get; private set; }
    public long CandidateBodies { get; private set; }

    private static SpatialBounds BoundsOf(VoxelObject body)
    {
        dvec3 center = body.ToWorld(body.Bounds.Center);
        return new(center - body.Bounds.Radius, center + body.Bounds.Radius);
    }

    public void Prepare(VoxelMap map, IReadOnlyList<VoxelObject> objects)
    {
        if (!ReferenceEquals(_map, map) || _mapRevision != map.Grid.Revision)
        {
            _terrainGeneration++;
            _mapRevision = map.Grid.Revision;
        }
        _map = map;
        _objects = objects;
        int count = objects.Count;
        bool rebuild = count != _count;
        if (_bounds.Length < count)
        {
            int capacity = Math.Max(count, _bounds.Length * 2);
            _bounds = new SpatialBounds[capacity];
            _targetBounds = new SpatialBounds[capacity];
            Array.Resize(ref _members, capacity);
        }
        for (int i = 0; i < count; i++)
        {
            VoxelObject body = objects[i];
            rebuild |= !ReferenceEquals(_members[i], body);
            _members[i] = body;
            _targetBounds[i] = BoundsOf(body);
            _bounds[i] = TightBounds(body);
        }
        if (_count > count) Array.Clear(_members, count, _count - count);
        _count = count;
        if (rebuild) _index.Build(_bounds.AsSpan(0, count));
        else _index.Refit(_bounds.AsSpan(0, count));
    }

    private Shape ShapeOf(VoxelObject body)
    {
        Shape shape = _shapes.GetValue(body, static _ => new Shape());
        if (shape.Version != body.TopologyVersion || shape.Count != body.Grid.Count)
        {
            shape.TerrainGeneration = -1;
            List<dvec3> centers = [];
            List<dvec3> halfEdges = [];
            int i = 0;
            dvec3 low = new(double.PositiveInfinity), high = new(double.NegativeInfinity);
            foreach (var (cell, voxel) in body.Grid.Occupied())
            {
                if (!Blocks.IsSolid(voxel.Type)) continue;
                bool partial = BlockShapes.HasShape(voxel.Type);
                foreach (ivec3 part in partial ? BlockShapes.Parts(voxel) : new[] { ivec3.Zero })
                {
                    var bounds = BlockShapes.PartBounds(voxel, part);
                    dvec3 halfEdge = (bounds.High - bounds.Low) * (body.CellEdge / 2);
                    dvec3 offset = (bounds.Low + bounds.High) / 2;
                    dvec3 center = (new dvec3(cell) + offset) * body.CellEdge;
                    centers.Add(center); halfEdges.Add(halfEdge); i++;
                    low = dvec3.Min(low, center - halfEdge);
                    high = dvec3.Max(high, center + halfEdge);
                }
            }
            shape.Centers = [.. centers];
            shape.HalfEdges = [.. halfEdges];
            shape.Center = i > 0 ? (low + high) / 2 : dvec3.Zero;
            shape.Half = i > 0 ? (high - low) / 2 : dvec3.Zero;
            if (i > 8)
            {
                SpatialBounds[] bounds = new SpatialBounds[i];
                for (int j = 0; j < i; j++) bounds[j] = new(shape.Centers[j] - shape.HalfEdges[j], shape.Centers[j] + shape.HalfEdges[j]);
                shape.Cells ??= new SpatialIndex();
                shape.Cells.Build(bounds);
            }
            else shape.Cells = null;
            shape.Version = body.TopologyVersion;
            shape.Count = body.Grid.Count;
        }
        return shape;
    }

    private SpatialBounds TightBounds(VoxelObject body)
    {
        Shape shape = ShapeOf(body);
        CollisionBox box = new(body.ToWorld(shape.Center), shape.Half, body.Orientation);
        dvec3 extent = box.Extent + 1e-7;
        return new(box.Center - extent, box.Center + extent);
    }

    public bool Blocked(in CollisionBox box, bool supportOnly = false)
    {
        if (BlockedBy(box, _map, supportOnly)) return true;
        dvec3 extent = box.Extent;
        _index.Query(new SpatialBounds(box.Center - extent, box.Center + extent), _candidates);
        foreach (int entry in _candidates)
            if (BlockedBy(box, _objects[entry], supportOnly)) return true;
        return false;
    }

    /// <summary>Side faces near a proposed footprint, including glancing approaches missed by rays.</summary>
    public List<(dvec3 Point, dvec3 Normal)> StepSides(CollisionBox box, dvec3 motion)
    {
        List<(dvec3 Point, dvec3 Normal)> sides = [];
        void Add(VoxelBody target)
        {
            foreach (CollisionBox cell in new VoxelCellQuery(box, target))
            {
                if (!BoxPenetration(box, cell, out dvec3 push)) continue;
                dvec3 normal = push.Normalized;
                if (Math.Abs(normal.z) >= Player.SupportSlope || dvec3.Dot(normal, motion) >= -1e-10) continue;
                double radius = Math.Abs(dvec3.Dot(normal, cell.Axis(0))) * cell.Half.x
                    + Math.Abs(dvec3.Dot(normal, cell.Axis(1))) * cell.Half.y
                    + Math.Abs(dvec3.Dot(normal, cell.Axis(2))) * cell.Half.z;
                sides.Add((cell.Center + normal * radius, normal));
            }
        }
        Add(_map);
        dvec3 extent = box.Extent;
        _index.Query(new(box.Center - extent, box.Center + extent), _candidates);
        foreach (int entry in _candidates) Add(_objects[entry]);
        return sides;
    }

    private static bool BlockedBy(in CollisionBox box, VoxelBody target, bool supportOnly)
    {
        foreach (CollisionBox cell in new VoxelCellQuery(box, target))
            if (supportOnly ? WalkableFaceIn(box, cell) : BoxPenetration(box, cell, out _)) return true;
        return false;
    }

    public void Recover(Player? player)
    {
        CellTests = CandidateBodies = TerrainQueriesSkipped = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = player != null && RecoverBody(player, -1);
            for (int i = 0; i < _count; i++)
                if (_objects[i].Body != null) changed |= RecoverBody(_objects[i], i);
            if (!changed) break;
        }
    }

    private bool RecoverBody(CellBody body, int entry)
    {
        int count;
        SpatialBounds bounds;
        _sourceShape = null;
        _sourceBody = body;
        if (body is Player player)
        {
            count = 1;
            if (_source.Length == 0) _source = new CollisionBox[1];
            dvec3 half = new(Player.Width / 2, Player.Width / 2, player.CurrentHeight / 2);
            _source[0] = new(player.Feet + new dvec3(0, 0, half.z), half, dquat.Identity);
            bounds = new(body.Position, body.Position + 2 * half);
        }
        else
        {
            _sourceShape = ShapeOf((VoxelObject)body);
            ReadOnlySpan<dvec3> centers = _sourceShape.Centers;
            count = centers.Length;
            if (_source.Length < count) Array.Resize(ref _source, Math.Max(count, _source.Length * 2));
            dvec3 position = body.Position;
            dquat rotation = body.Orientation;
            CollisionBox template = new(dvec3.Zero, new dvec3(body.CellEdge / 2), rotation);
            for (int i = 0; i < count; i++) _source[i] = template with { Center = position + rotation * centers[i], Half = _sourceShape.HalfEdges[i] };
            bounds = _bounds[entry];
        }

        dvec3 deepest = dvec3.Zero;
        VoxelBody? other = null;
        int otherEntry = -1;
        TestTarget(_map, count, null, null, ref deepest, ref other);
        _index.Query(bounds, _candidates);
        // Preserve the original map/object/cell traversal and equal-depth winner. Tree
        // traversal order must never determine the physical correction.
        _candidates.Sort();
        foreach (int candidate in _candidates)
        {
            if (candidate == entry) continue;
            VoxelBody? previous = other;
            TestTarget(_objects[candidate], count, _targetBounds[candidate], _bounds[candidate], ref deepest, ref other);
            if (!ReferenceEquals(previous, other)) otherEntry = candidate;
        }
        if (deepest.Length <= RecoveryTolerance || other == null) return false;
        double share = other.Rigid == null ? 1 : other.Rigid.Mass / (body.Rigid!.Mass + other.Rigid.Mass);
        body.Rigid!.Position += share * deepest;
        if (entry >= 0) Refit(entry);
        if (other.Rigid != null)
        {
            other.Rigid.Position -= (1 - share) * deepest;
            Refit(otherEntry);
        }
        return true;
    }

    private void Refit(int entry)
    {
        _targetBounds[entry] = BoundsOf(_objects[entry]);
        _bounds[entry] = TightBounds(_objects[entry]);
        _index.Update(entry, _bounds[entry]);
    }

    private double TerrainMotionBound(Shape cached)
    {
        dquat rotation = _sourceBody.Orientation, previous = cached.TerrainRotation;
        // q and -q describe the same pose. Quaternion distance avoids cancellation
        // from acos(dot(q, previous)) for the tiny rotations of resting bodies.
        double dot = rotation.x * previous.x + rotation.y * previous.y + rotation.z * previous.z + rotation.w * previous.w;
        double sign = dot < 0 ? -1 : 1;
        double dx = rotation.x - sign * previous.x, dy = rotation.y - sign * previous.y;
        double dz = rotation.z - sign * previous.z, dw = rotation.w - sign * previous.w;
        // Twice quaternion distance bounds a rotated unit point's displacement;
        // use twice that bound plus an absolute roundoff allowance.
        return (_sourceBody.ToWorld(cached.Center) - cached.TerrainCenter).Length
            + 4 * Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw) * cached.Half.Length + 1e-9;
    }

    private void TestTarget(VoxelBody target, int count, SpatialBounds? bounds, SpatialBounds? queryBounds, ref dvec3 deepest, ref VoxelBody? other)
    {
        CandidateBodies++;
        if (count == 0) return;
        bool terrain = ReuseTerrain && ReferenceEquals(target, _map);
        if (terrain && _sourceShape is Shape cached && cached.TerrainGeneration == _terrainGeneration)
        {
            if (TerrainMotionBound(cached) < cached.TerrainAllowance)
            {
                TerrainQueriesSkipped++;
                return;
            }
        }
        double terrainDepth = 0;
        Span<dvec3> axes = stackalloc dvec3[15];
        Span<double> radii = stackalloc double[15];
        VoxelGridPose pose = new(target);
        BoxPair pair = new(_source[0], pose.Template, axes, radii);
        dvec3 queryExtent = pose.ExtentOf(_source[0]);
        bool single = false;
        CollisionBox singleBox = default;
        if (target is VoxelObject obj)
        {
            Shape shape = ShapeOf(obj);
            if (shape.Centers.Length == 1)
            {
                single = true;
                if (!Blocks.IsSolid(obj.Grid[(ivec3)dvec3.Floor(shape.Centers[0] / obj.CellEdge)].Type)) return;
                singleBox = pose.Template with { Center = obj.ToWorld(shape.Centers[0]) };
            }
        }
        bool indexed = queryBounds.HasValue && _sourceShape?.Cells != null;
        int tested = count;
        if (indexed)
        {
            SpatialBounds world = queryBounds!.Value;
            dvec3 half = (world.High - world.Low) / 2;
            dvec3 center = _sourceBody.ToLocal((world.High + world.Low) / 2);
            dquat inverse = _sourceBody.Orientation.Inverse;
            dvec3 extent = dvec3.Abs(inverse * dvec3.UnitX) * half.x
                + dvec3.Abs(inverse * dvec3.UnitY) * half.y + dvec3.Abs(inverse * dvec3.UnitZ) * half.z + 1e-7;
            _sourceShape!.Cells!.Query(new(center - extent, center + extent), _sourceCandidates);
            _sourceCandidates.Sort();
            tested = _sourceCandidates.Count;
        }
        for (int j = 0; j < tested; j++)
        {
            int i = indexed ? _sourceCandidates[j] : j;
            CollisionBox box = _source[i];
            if (bounds is SpatialBounds targetBounds)
            {
                dvec3 extent = box.Extent;
                if (!targetBounds.Intersects(new SpatialBounds(box.Center - extent, box.Center + extent))) continue;
            }
            if (single)
            {
                CellTests++;
                dvec3 singletonPush;
                bool singletonHit = box.Half == _source[0].Half
                    ? pair.Penetration(box.Center - singleBox.Center, out singletonPush)
                    : BoxPenetration(box, singleBox, out singletonPush);
                if (singletonHit && singletonPush.LengthSqr > deepest.LengthSqr)
                {
                    deepest = singletonPush;
                    other = target;
                }
                continue;
            }
            dvec3 extentForBox = box.Half == _source[0].Half ? queryExtent : pose.ExtentOf(box);
            foreach (CollisionBox cell in new VoxelCellQuery(box.Center, extentForBox, pose))
            {
                CellTests++;
                if (terrain && _sourceShape != null)
                {
                    // Fixed world axes are always part of terrain SAT. Their minimum
                    // overlap bounds the full SAT depth, even when a cross axis rejects.
                    dvec3 overlap = extentForBox + cell.Half - dvec3.Abs(box.Center - cell.Center);
                    terrainDepth = Math.Max(terrainDepth, Math.Min(overlap.x, Math.Min(overlap.y, overlap.z)));
                }
                dvec3 push;
                bool penetrates = box.Half == _source[0].Half && cell.Half == pose.Template.Half
                    ? pair.Penetration(box.Center - cell.Center, out push) : BoxPenetration(box, cell, out push);
                if (penetrates && push.LengthSqr > deepest.LengthSqr)
                {
                    deepest = push;
                    other = target;
                }
            }
        }
        if (terrain && _sourceShape is Shape shapeCache)
        {
            shapeCache.TerrainGeneration = _terrainGeneration;
            shapeCache.TerrainCenter = _sourceBody.ToWorld(shapeCache.Center);
            shapeCache.TerrainRotation = _sourceBody.Orientation;
            // Only certify that no correction exceeds the existing 1 cm threshold.
            // Motion consumes the bound; topology/terrain edits invalidate it.
            shapeCache.TerrainAllowance = RecoveryTolerance - terrainDepth - 1e-9;
        }
    }
}
