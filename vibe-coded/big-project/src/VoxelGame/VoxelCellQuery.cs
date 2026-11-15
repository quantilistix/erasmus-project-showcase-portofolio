using GlmSharp;
using MYT.Kit.Vulkan;
using static VoxelGame.BoxCollision;

namespace VoxelGame;

/// <summary>One target transform, shared by every cell query in a body pair.</summary>
internal readonly struct VoxelGridPose
{
    public readonly VoxelBody Body;
    public readonly dvec3 Position;
    public readonly dquat Rotation, Inverse;
    public readonly CollisionBox Template;
    public readonly bool Identity;
    public VoxelGridPose(VoxelBody body)
    {
        Body = body;
        Position = body.Position;
        Rotation = body.Orientation;
        Inverse = Rotation.Inverse;
        Template = new(dvec3.Zero, new dvec3(body.CellEdge / 2), Rotation);
        Identity = Rotation == dquat.Identity && Position == dvec3.Zero;
    }
    public dvec3 ExtentOf(in CollisionBox box) => Identity ? box.Extent
        : dvec3.Abs(Inverse * box.Axis(0)) * box.Half.x + dvec3.Abs(Inverse * box.Axis(1)) * box.Half.y
            + dvec3.Abs(Inverse * box.Axis(2)) * box.Half.z;
}

/// <summary>Solid cells intersecting a box's conservative local bounds, in original z/y/x order.</summary>
internal struct VoxelCellQuery
{
    private readonly Grid3D<Voxel> _grid;
    private readonly ivec3 _low, _high;
    private readonly dvec3 _position;
    private readonly dquat _rotation;
    private readonly CollisionBox _template;
    private readonly double _edge;
    private readonly bool _identity;
    private ivec3 _cell, _chunkCoordinate;
    private Grid3DChunk<Voxel>? _chunk;
    private bool _started, _hasChunk;
    private int _part = -1;
    private Voxel _voxel;

    public VoxelCellQuery(in CollisionBox box, VoxelBody body) : this(box, new VoxelGridPose(body)) { }
    private VoxelCellQuery(in CollisionBox box, in VoxelGridPose pose) : this(box.Center, pose.ExtentOf(box), pose) { }
    public VoxelCellQuery(dvec3 worldCenter, dvec3 localExtent, in VoxelGridPose pose)
    {
        _grid = pose.Body.Grid;
        _position = pose.Position;
        _rotation = pose.Rotation;
        _identity = pose.Identity;
        _edge = pose.Body.CellEdge;
        dvec3 center = _identity ? worldCenter : _rotation.Conjugate * (worldCenter - _position);
        _low = (ivec3)dvec3.Floor((center - localExtent) / _edge);
        _high = (ivec3)dvec3.Floor((center + localExtent) / _edge);
        _template = pose.Template;
        _cell = _low;
    }
    public VoxelCellQuery GetEnumerator() => this;
    public CollisionBox Current
    {
        get
        {
            var bounds = BlockShapes.PartBounds(_voxel, new(_part & 1, (_part >> 1) & 1, _part >> 2));
            dvec3 offset = (bounds.Low + bounds.High) / 2;
            dvec3 center = (new dvec3(_cell) + offset) * _edge;
            return _template with { Center = _identity ? center : _position + _rotation * center,
                Half = (bounds.High - bounds.Low) * (_edge / 2) };
        }
    }
    public bool MoveNext()
    {
        while (true)
        {
            if (BlockShapes.HasShape(_voxel.Type))
                while (++_part < 8)
                    if (BlockShapes.Occupied(_voxel, new(_part & 1, (_part >> 1) & 1, _part >> 2))) return true;
            _part = -1;
            _voxel = default;
            if (_started)
            {
                if (++_cell.x > _high.x)
                {
                    _cell.x = _low.x;
                    if (++_cell.y > _high.y)
                    {
                        _cell.y = _low.y;
                        if (++_cell.z > _high.z) return false;
                    }
                }
            }
            else _started = true;
            ivec3 coordinate = Grid3D<Voxel>.ChunkOf(_cell);
            if (!_hasChunk || coordinate != _chunkCoordinate)
            {
                _grid.TryGetChunk(coordinate, out _chunk);
                _chunkCoordinate = coordinate;
                _hasChunk = true;
            }
            if (_chunk != null)
            {
                _voxel = _chunk.Cells[Grid3D<Voxel>.IndexInChunk(_cell)];
                if (!Blocks.IsSolid(_voxel.Type)) { _voxel = default; continue; }
                if (!BlockShapes.HasShape(_voxel.Type)) return true;
                while (++_part < 8)
                    if (BlockShapes.Occupied(_voxel, new(_part & 1, (_part >> 1) & 1, _part >> 2))) return true;
            }
        }
    }
}
