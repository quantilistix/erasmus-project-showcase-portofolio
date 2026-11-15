using GlmSharp;
using VoxelGame;
using static VoxelTests.ReferenceBoxCollision;

namespace VoxelTests;

// Independent original recovery loop: preserves traversal/tie order and correction arithmetic.
internal sealed class ReferenceVoxelRecovery(VoxelMap map, IReadOnlyList<VoxelObject> objects, Player? player)
{
    private IEnumerable<VoxelBody> Bodies => objects.Prepend<VoxelBody>(map);
    private IEnumerable<CellBody> AwakeBodies => (player == null ? Enumerable.Empty<CellBody>() : [player])
        .Concat(objects.Where(o => o.Body != null));
    private static (dvec3 Low, dvec3 High) BoundsOf(CellBody body)
    {
        if (body is VoxelObject obj)
        {
            dvec3 center = obj.ToWorld(obj.Bounds.Center);
            return (center - obj.Bounds.Radius, center + obj.Bounds.Radius);
        }
        return (body.Position, body.Position + new dvec3(Player.Width, Player.Width, ((Player)body).CurrentHeight));
    }
    private static CollisionBox PlayerBox(dvec3 feet, double height) =>
        new(feet + new dvec3(0, 0, height / 2), new dvec3(Player.Width / 2, Player.Width / 2, height / 2), dquat.Identity);

    // Query only grid cells inside the box's bounds in the target body's frame.
    private static IEnumerable<CollisionBox> NearbyCells(CollisionBox box, VoxelBody target)
    {
        dvec3 center = target.ToLocal(box.Center);
        dquat inverse = target.Orientation.Inverse;
        dvec3 extent = dvec3.Abs(inverse * box.Axis(0)) * box.Half.x
            + dvec3.Abs(inverse * box.Axis(1)) * box.Half.y
            + dvec3.Abs(inverse * box.Axis(2)) * box.Half.z;
        ivec3 low = (ivec3)dvec3.Floor((center - extent) / target.CellEdge);
        ivec3 high = (ivec3)dvec3.Floor((center + extent) / target.CellEdge);
        for (int z = low.z; z <= high.z; z++)
        for (int y = low.y; y <= high.y; y++)
        for (int x = low.x; x <= high.x; x++)
        {
            ivec3 cell = new(x, y, z);
            if (Blocks.IsSolid(target.Grid[cell].Type))
                yield return new CollisionBox(target.ToWorld((new dvec3(cell) + 0.5) * target.CellEdge), new dvec3(target.CellEdge / 2), target.Orientation);
        }
    }

    /// <summary>Recover pre-existing discrete overlaps by translation, without adding momentum.</summary>
    public void Recover()
    {
        CellBody[] moving = [.. AwakeBodies];
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            foreach (CellBody body in moving)
            {
                IEnumerable<CollisionBox> boxes = body is Player player
                    ? [PlayerBox(player.Feet, player.CurrentHeight)]
                    : body.OccupiedCells().Select(cell => new CollisionBox(body.ToWorld((new dvec3(cell) + 0.5) * body.CellEdge), new dvec3(body.CellEdge / 2), body.Orientation));
                dvec3 deepest = dvec3.Zero;
                VoxelBody? other = null;
                foreach (VoxelBody target in Bodies)
                {
                    if (target == body)
                        continue;
                    var bounds = target is VoxelObject ? BoundsOf(target) : (Low: new dvec3(double.NegativeInfinity), High: new dvec3(double.PositiveInfinity));
                    var ownBounds = BoundsOf(body);
                    if (ownBounds.High.x < bounds.Low.x || ownBounds.Low.x > bounds.High.x
                        || ownBounds.High.y < bounds.Low.y || ownBounds.Low.y > bounds.High.y
                        || ownBounds.High.z < bounds.Low.z || ownBounds.Low.z > bounds.High.z)
                        continue;
                    foreach (CollisionBox box in boxes)
                    {
                        if ((box.Center + box.Extent).x < bounds.Low.x || (box.Center - box.Extent).x > bounds.High.x
                            || (box.Center + box.Extent).y < bounds.Low.y || (box.Center - box.Extent).y > bounds.High.y
                            || (box.Center + box.Extent).z < bounds.Low.z || (box.Center - box.Extent).z > bounds.High.z)
                            continue;
                        foreach (CollisionBox cell in NearbyCells(box, target))
                            if (BoxPenetration(box, cell, out dvec3 push) && push.LengthSqr > deepest.LengthSqr)
                            {
                                deepest = push;
                                other = target;
                            }
                    }
                }
                // Ordinary contact slop is left to the contact solver. This recovers actual
                // embeddings (spawns, edits and resizes), including those with no prior contact.
                if (deepest.Length <= 0.01 || other == null)
                    continue;
                double share = other.Rigid == null ? 1 : other.Rigid.Mass / (body.Rigid!.Mass + other.Rigid.Mass);
                body.Rigid!.Position += share * deepest;
                if (other.Rigid != null)
                    other.Rigid.Position -= (1 - share) * deepest;
                changed = true;
            }
            if (!changed)
                break;
        }
    }
}
