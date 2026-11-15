using GlmSharp;
using MYT.Kit.Vulkan;

namespace VoxelGame;

/// <summary>Shapes on a half-cell grid. Geometry rotates; texture coordinates stay in the body grid.</summary>
internal static class BlockShapes
{
    public static readonly BlockType[] Bases = [BlockType.Bedrock, BlockType.Stone, BlockType.Cobblestone,
        BlockType.Dirt, BlockType.Sand, BlockType.OakPlanks, BlockType.AcaciaPlanks, BlockType.Glass];
    public static bool IsPartial(BlockType type) => type >= BlockType.BedrockSlab && (int)type < (int)BlockType.BedrockSlab + Bases.Length * 4;
    public static bool IsDoor(BlockType type) => type is BlockType.AcaciaDoorBottom or BlockType.AcaciaDoorTop;
    public static bool HasShape(BlockType type) => IsPartial(type) || IsDoor(type);
    public static int Kind(BlockType type) => ((int)type - (int)BlockType.BedrockSlab) % 4;
    public static BlockType BaseOf(BlockType type) => IsPartial(type) ? Bases[((int)type - (int)BlockType.BedrockSlab) / 4] : type;

    public static BlockOrientation Placement(BlockType type, ivec3 normal, dvec3 towardPlayer, dvec3 point)
    {
        if (Kind(type) == 0)
        {
            if (normal.z != 0) return normal.z > 0 ? BlockOrientation.SlabBottom : BlockOrientation.SlabTop;
            double horizontal = normal.x != 0 ? point.y - 0.5 : point.x - 0.5;
            double vertical = point.z - 0.5;
            // The diagonals split the face into top, bottom, left and right triangles.
            // Ties belong to a horizontal slab; the exact center selects the bottom half.
            if (Math.Abs(vertical) >= Math.Abs(horizontal))
                return vertical > 0 ? BlockOrientation.SlabTop : BlockOrientation.SlabBottom;
            return normal.x != 0
                ? horizontal > 0 ? BlockOrientation.SlabNorth : BlockOrientation.SlabSouth
                : horizontal > 0 ? BlockOrientation.SlabEast : BlockOrientation.SlabWest;
        }
        dvec3 away = -towardPlayer;
        int turn = Math.Abs(away.x) > Math.Abs(away.y) ? away.x > 0 ? 1 : 3 : away.y < 0 ? 2 : 0;
        if (Kind(type) >= 2)
        {
            // Pick an actual grid quadrant, rather than rotating a view-dependent left/right
            // choice. The same hit must keep its corner when the view crosses a diagonal.
            bool missing = Kind(type) == 3;
            double x = point.x - 0.5, y = point.y - 0.5;
            if (normal.z == 0)
            {
                // On a side, an outer corner rises against the clicked face. An inner
                // corner leaves its notch facing out of that face, on the aimed side.
                if (normal.x != 0) x = normal.x * (missing ? 1 : -1);
                if (normal.y != 0) y = normal.y * (missing ? 1 : -1);
            }
            double fallbackX = missing ? towardPlayer.x : away.x;
            double fallbackY = missing ? towardPlayer.y : away.y;
            bool east = Math.Abs(x) > 1e-8 ? x > 0 : fallbackX > 0;
            bool north = Math.Abs(y) > 1e-8 ? y > 0 : fallbackY >= 0;
            int quadrant = north ? east ? 1 : 0 : east ? 2 : 3;
            // Canonical outer stairs raise NW; canonical inner stairs omit SE.
            turn = (quadrant + (missing ? 2 : 0)) & 3;
        }
        bool top = normal.z < 0 || (normal.z == 0 && point.z > 0.5);
        return (BlockOrientation)(64 + turn + (top ? 4 : 0));
    }

    public static bool Occupied(Voxel voxel, ivec3 half)
    {
        if (voxel.Type == BlockType.Air) return false;
        if (IsDoor(voxel.Type))
        {
            int turn = (int)voxel.Orientation;
            int doorX = half.x, doorY = half.y;
            for (int i = 0; i < (turn & 3); i++) (doorX, doorY) = (1 - doorY, doorX);
            return doorY == 0;
        }
        if (!IsPartial(voxel.Type)) return true;
        if (Kind(voxel.Type) == 0)
            return voxel.Orientation switch
            {
                BlockOrientation.SlabTop => half.z == 1,
                BlockOrientation.SlabWest => half.x == 0,
                BlockOrientation.SlabEast => half.x == 1,
                BlockOrientation.SlabSouth => half.y == 0,
                BlockOrientation.SlabNorth => half.y == 1,
                _ => half.z == 0
            };
        int rotation = voxel.Orientation >= BlockOrientation.StairsNorth ? ((int)voxel.Orientation - 64) : 0;
        int x = half.x, y = half.y;
        for (int i = 0; i < (rotation & 3); i++) (x, y) = (1 - y, x);
        if (half.z == (rotation >= 4 ? 1 : 0)) return true;
        return Kind(voxel.Type) switch { 1 => y == 1, 2 => y == 1 && x == 0, _ => y == 1 || x == 0 };
    }

    public const double DoorThickness = 3.0 / 16;

    /// <summary>Maps the shared half-grid door panel to its actual thickness.</summary>
    public static dvec3 Point(Voxel voxel, dvec3 point)
    {
        if (!IsDoor(voxel.Type)) return point;
        int turn = (int)voxel.Orientation & 3;
        int axis = (turn & 1) == 0 ? 1 : 0;
        point[axis] = turn < 2 ? point[axis] * (DoorThickness * 2)
            : 1 - (1 - point[axis]) * (DoorThickness * 2);
        return point;
    }

    public static (dvec3 Low, dvec3 High) PartBounds(Voxel voxel, ivec3 part)
    {
        if (!HasShape(voxel.Type)) return (dvec3.Zero, dvec3.Ones);
        return (Point(voxel, new dvec3(part) / 2), Point(voxel, (new dvec3(part) + 1) / 2));
    }

    public static IEnumerable<ivec3> Parts(Voxel voxel)
    {
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            ivec3 half = new(x, y, z);
            if (Occupied(voxel, half)) yield return half;
        }
    }

    public static IEnumerable<(dvec3 Start, dvec3 End)> Edges(Voxel voxel)
    {
        Dictionary<(ivec3 A, ivec3 B), HashSet<int>> edges = [];
        foreach (ivec3 part in Parts(voxel))
        for (int face = 0; face < 6; face++)
        {
            ivec3 next = part + Grid3D<Voxel>.FaceDirections[face];
            if (next.x >= 0 && next.y >= 0 && next.z >= 0 && next.x < 2 && next.y < 2 && next.z < 2
                && Occupied(voxel, next)) continue;
            for (int k = 0; k < 4; k++)
            {
                ivec3 a = part + VoxelMesher.FaceCorners[face][k];
                ivec3 b = part + VoxelMesher.FaceCorners[face][(k + 1) & 3];
                if (a.x + 4 * a.y + 16 * a.z > b.x + 4 * b.y + 16 * b.z) (a, b) = (b, a);
                if (!edges.TryGetValue((a, b), out var faces)) edges[(a, b)] = faces = [];
                if (!faces.Add(face)) faces.Remove(face);
            }
        }
        foreach (var (edge, faces) in edges)
            if (faces.Count > 0) yield return (Point(voxel, new dvec3(edge.A) / 2), Point(voxel, new dvec3(edge.B) / 2));
    }

    public static Voxel AtHalf(Voxel[] cells, ivec3 half, out ivec3 part)
    {
        ivec3 cell = new(half.x >> 1, half.y >> 1, half.z >> 1);
        part = new(half.x & 1, half.y & 1, half.z & 1);
        const int padded = VoxelMesher.ChunkSize + 2;
        return cells[(cell.x + 1) + padded * ((cell.y + 1) + padded * (cell.z + 1))];
    }

    public static IEnumerable<(ivec3 Cell, ivec3 Half, int Face, Voxel Voxel)> Faces(Voxel[] cells, bool collision, bool transparentLeaves = false)
    {
        for (int z = 0; z < VoxelMesher.ChunkSize; z++)
        for (int y = 0; y < VoxelMesher.ChunkSize; y++)
        for (int x = 0; x < VoxelMesher.ChunkSize; x++)
        {
            ivec3 cell = new(x, y, z);
            Voxel voxel = AtHalf(cells, cell * 2, out _);
            if (collision ? !Blocks.IsSolid(voxel.Type) : voxel.Type == BlockType.Air) continue;
            foreach (ivec3 part in Parts(voxel))
            for (int face = 0; face < 6; face++)
            {
                ivec3 half = cell * 2 + part;
                ivec3 nextHalf = half + Grid3D<Voxel>.FaceDirections[face];
                Voxel next = AtHalf(cells, nextHalf, out ivec3 nextPart);
                bool occupied = Occupied(next, nextPart);
                bool covered = collision ? occupied && Blocks.IsSolid(next.Type)
                    : occupied && (!VoxelMesher.Visible(voxel, next, transparentLeaves)
                        || (new ivec3(nextHalf.x >> 1, nextHalf.y >> 1, nextHalf.z >> 1) == cell && next.Type == voxel.Type));
                if (!covered) yield return (cell, half, face, voxel);
            }
        }
    }

    /// <summary>Render faces clipped against actual boxes, including the door's 3/16 thickness.</summary>
    public static IEnumerable<(ivec3 Cell, ivec3 Half, int Face, Voxel Voxel, dvec3 Low, dvec3 High)> RenderFaces(
        Voxel[] cells, bool transparentLeaves)
    {
        for (int z = 0; z < VoxelMesher.ChunkSize; z++)
        for (int y = 0; y < VoxelMesher.ChunkSize; y++)
        for (int x = 0; x < VoxelMesher.ChunkSize; x++)
        {
            ivec3 cell = new(x, y, z);
            Voxel voxel = AtHalf(cells, cell * 2, out _);
            if (voxel.Type == BlockType.Air) continue;
            foreach (ivec3 part in Parts(voxel))
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1, u = (axis + 1) % 3, v = (axis + 2) % 3;
                ivec3 half = cell * 2 + part;
                dvec3 low = new dvec3(cell) + Point(voxel, new dvec3(part) / 2);
                dvec3 high = new dvec3(cell) + Point(voxel, (new dvec3(part) + 1) / 2);
                low[axis] = high[axis] = (face & 1) == 0 ? high[axis] : low[axis];
                ivec3 nextHalf = half + Grid3D<Voxel>.FaceDirections[face];
                Voxel next = AtHalf(cells, nextHalf, out ivec3 nextPart);
                ivec3 nextCell = new(nextHalf.x >> 1, nextHalf.y >> 1, nextHalf.z >> 1);
                if (Occupied(next, nextPart) && (nextCell == cell || !VoxelMesher.Visible(voxel, next, transparentLeaves)))
                {
                    dvec3 otherLow = new dvec3(nextCell) + Point(next, new dvec3(nextPart) / 2);
                    dvec3 otherHigh = new dvec3(nextCell) + Point(next, (new dvec3(nextPart) + 1) / 2);
                    double plane = (face & 1) == 0 ? otherLow[axis] : otherHigh[axis];
                    double u0 = Math.Max(low[u], otherLow[u]), u1 = Math.Min(high[u], otherHigh[u]);
                    double v0 = Math.Max(low[v], otherLow[v]), v1 = Math.Min(high[v], otherHigh[v]);
                    if (plane == low[axis] && u0 < u1 && v0 < v1)
                    {
                        if (u0 == low[u] && u1 == high[u] && v0 == low[v] && v1 == high[v]) continue;
                        // Subtract the covered rectangle, keeping up to four exposed strips.
                        foreach (var strip in new[] { (low[u], u0, low[v], high[v]), (u1, high[u], low[v], high[v]),
                            (u0, u1, low[v], v0), (u0, u1, v1, high[v]) })
                        {
                            if (strip.Item1 >= strip.Item2 || strip.Item3 >= strip.Item4) continue;
                            dvec3 a = low, b = high;
                            a[u] = strip.Item1; b[u] = strip.Item2; a[v] = strip.Item3; b[v] = strip.Item4;
                            yield return (cell, half, face, voxel, a, b);
                        }
                        continue;
                    }
                }
                yield return (cell, half, face, voxel, low, high);
            }
        }
    }

    /// <summary>Walk whole cells, refining shaped cells against their occupied half-cell boxes.</summary>
    public static bool Raycast(Grid3D<Voxel> grid, dvec3 origin, dvec3 direction, double reach, out Grid3DHit hit)
    {
        hit = default;
        double length = direction.Length;
        if (!(length > 0)) return false;
        dvec3 unit = direction / length;
        ivec3 cell = (ivec3)dvec3.Floor(origin);
        ivec3 step = new(Math.Sign(unit.x), Math.Sign(unit.y), Math.Sign(unit.z));
        dvec3 delta = new(unit.x == 0 ? double.PositiveInfinity : Math.Abs(1 / unit.x),
            unit.y == 0 ? double.PositiveInfinity : Math.Abs(1 / unit.y),
            unit.z == 0 ? double.PositiveInfinity : Math.Abs(1 / unit.z));
        dvec3 next = new(double.PositiveInfinity);
        for (int a = 0; a < 3; a++)
            next[a] = step[a] > 0 ? (cell[a] + 1 - origin[a]) * delta[a]
                : step[a] < 0 ? (origin[a] - cell[a]) * delta[a] : double.PositiveInfinity;
        double distance = 0;
        ivec3 normal = ivec3.Zero;
        while (distance <= reach)
        {
            Voxel voxel = grid[cell];
            if (Blocks.IsSolid(voxel.Type))
            {
                if (!HasShape(voxel.Type))
                {
                    hit = new(cell, normal, distance);
                    return true;
                }
                double closest = double.PositiveInfinity;
                ivec3 closestNormal = default;
                foreach (ivec3 part in Parts(voxel))
                {
                    var bounds = PartBounds(voxel, part);
                    dvec3 low = new dvec3(cell) + bounds.Low;
                    dvec3 high = new dvec3(cell) + bounds.High;
                    double enter = 0, exit = reach;
                    ivec3 enteringNormal = default;
                    bool valid = true;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        if (unit[axis] == 0)
                        {
                            if (origin[axis] < low[axis] || origin[axis] >= high[axis]) valid = false;
                            continue;
                        }
                        double a = (low[axis] - origin[axis]) / unit[axis];
                        double b = (high[axis] - origin[axis]) / unit[axis];
                        if (a > b) (a, b) = (b, a);
                        if (a > enter)
                        {
                            enter = a;
                            enteringNormal = ivec3.Zero; enteringNormal[axis] = -step[axis];
                        }
                        exit = Math.Min(exit, b);
                    }
                    if (valid && enter <= exit && exit >= distance && enter < closest)
                    {
                        closest = enter;
                        closestNormal = enteringNormal;
                    }
                }
                if (closest <= reach)
                {
                    hit = new(cell, closestNormal, closest);
                    return true;
                }
            }
            int axisNext = next.x < next.y ? (next.x < next.z ? 0 : 2) : (next.y < next.z ? 1 : 2);
            distance = next[axisNext]; next[axisNext] += delta[axisNext]; cell[axisNext] += step[axisNext];
            normal = ivec3.Zero; normal[axisNext] = -step[axisNext];
        }
        return false;
    }
}
