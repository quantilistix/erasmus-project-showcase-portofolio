using GlmSharp;
using static VoxelGame.BoxCollision;

namespace VoxelGame;

internal sealed partial class World
{
    public bool PlaceDoor(VoxelBody body, ivec3 bottom, BlockOrientation orientation)
    {
        orientation = (BlockOrientation)((int)orientation & 3);
        ivec3 top = bottom + ivec3.UnitZ;
        if (!Blocks.IsSolid(body.Grid[bottom - ivec3.UnitZ].Type)
            || !CanPlaceBlock(body, bottom, new Voxel(BlockType.AcaciaDoorBottom, 0, orientation))
            || !CanPlaceBlock(body, top, new Voxel(BlockType.AcaciaDoorTop, 0, orientation))) return false;
        SetBlock(body, bottom, BlockType.AcaciaDoorBottom, orientation);
        SetBlock(body, top, BlockType.AcaciaDoorTop, orientation);
        return true;
    }

    public bool ToggleDoor(VoxelBody body, ivec3 cell)
    {
        Voxel door = body.Grid[cell];
        if (!BlockShapes.IsDoor(door.Type)) return false;
        ivec3 bottom = cell - (door.Type == BlockType.AcaciaDoorTop ? ivec3.UnitZ : ivec3.Zero);
        ivec3 top = bottom + ivec3.UnitZ;
        Voxel lower = body.Grid[bottom], upper = body.Grid[top];
        if (lower.Type != BlockType.AcaciaDoorBottom || upper.Type != BlockType.AcaciaDoorTop) return false;
        int turn = (int)lower.Orientation;
        // Opening rotates the panel a quarter turn; closing restores the placement facing.
        BlockOrientation next = turn >= 16
            ? (BlockOrientation)((turn - 1) & 3) : (BlockOrientation)(16 + ((turn + 1) & 3));
        lower = lower with { Orientation = next };
        upper = upper with { Orientation = next };
        if (!CanPlaceBlock(body, bottom, lower, replacing: true)
            || !CanPlaceBlock(body, top, upper, replacing: true)) return false;
        body.Grid[bottom] = lower;
        body.Grid[top] = upper;
        NoteEdit(body, bottom);
        NoteEdit(body, top);
        _topologyChanged.Add(body);
        return true;
    }

    /// <summary>Lava is a non-solid fluid that burns an intersecting player during simulation.</summary>
    public void UpdateHazards(double seconds)
    {
        if (!(seconds > 0) || Player is not { Dead: false } player) return;
        CollisionBox person = new(player.Feet + new dvec3(0, 0, player.CurrentHeight / 2),
            new dvec3(Player.Width / 2, Player.Width / 2, player.CurrentHeight / 2), dquat.Identity);
        foreach (VoxelBody body in Bodies)
        {
            dvec3 low = new(double.PositiveInfinity), high = new(double.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                dvec3 corner = person.Center + person.Half * new dvec3((i & 1) == 0 ? -1 : 1,
                    (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                dvec3 local = body.ToLocal(corner) / body.CellEdge;
                low = dvec3.Min(low, local);
                high = dvec3.Max(high, local);
            }
            ivec3 min = (ivec3)dvec3.Floor(low), max = (ivec3)dvec3.Floor(high);
            for (int z = min.z; z <= max.z; z++)
            for (int y = min.y; y <= max.y; y++)
            for (int x = min.x; x <= max.x; x++)
            {
                ivec3 cell = new(x, y, z);
                if (body.Grid[cell].Type != BlockType.Lava) continue;
                CollisionBox lava = new(body.ToWorld((new dvec3(cell) + 0.5) * body.CellEdge),
                    new dvec3(body.CellEdge / 2), body.Orientation);
                if (!BoxPenetration(person, lava, out _)) continue;
                DamagePlayer(6 * seconds, "lava");
                return;
            }
        }
    }
}
