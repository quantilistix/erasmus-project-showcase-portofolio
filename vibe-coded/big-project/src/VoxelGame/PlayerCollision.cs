using GlmSharp;
using MYT.Kit.Vulkan;
using static VoxelGame.BoxCollision;

namespace VoxelGame;

internal sealed partial class World
{
    private readonly VoxelCollisionQueries _collisionQueries = new();

    private bool PlayerBoxBlocked(CollisionBox box, bool supportOnly = false) => _collisionQueries.Blocked(box, supportOnly);

    private void UpdateCrouch(Player player, bool requested, bool grounded)
    {
        if (requested == player.Crouching)
            return;
        if (!requested)
        {
            double extra = Player.Height - player.CurrentHeight;
            double bottom = grounded ? player.Feet.z + player.CurrentHeight : player.Feet.z - extra;
            // Test only the newly occupied volume, so contact slop at the existing sole
            // cannot prevent standing on an incline. Airborne expansion is below the feet.
            CollisionBox added = new(new dvec3(player.Feet.x, player.Feet.y, bottom + extra / 2),
                new dvec3(Player.Width / 2 - 1e-4, Player.Width / 2 - 1e-4, extra / 2 - 1e-4), dquat.Identity);
            if (PlayerBoxBlocked(added))
                return;
        }
        player.SetCrouching(requested, grounded);
        if (!grounded)
            _supports.Clear();
    }

    private bool CrouchSupported(Player player, dvec3 offset)
    {
        // Extend the full sole downward and look for walkable faces inside it.
        // Keep the contact footprint outside the solver's feature-ambiguity band,
        // including on an incline where only a bottom corner supplies support.
        double inset = Math.Max(0.001, Physics.Settings.ContactMargin + Physics.Settings.PenetrationTolerance);
        CollisionBox sole = new(player.Feet + offset + new dvec3(0, 0, (GroundProbe - Player.SupportReach) / 2),
            new dvec3(Player.Width / 2 - inset, Player.Width / 2 - inset, (GroundProbe + Player.SupportReach) / 2), dquat.Identity);
        if (PlayerBoxBlocked(sole, supportOnly: true)) return true;
        foreach (DroppedItem item in _items)
        {
            if (item.Body is not { } rigid || (item.Position - sole.Center).Length > item.Radius + sole.Half.Length) continue;
            var mesh = rigid.Geometry;
            for (int i = 0; i < mesh.Faces.Length; i += 3)
            {
                dvec3 Vertex(int k) => rigid.Position + rigid.Orientation * mesh.Vertices[mesh.Faces[i + k]];
                if (WalkableTriangleIn(sole, Vertex(0), Vertex(1), Vertex(2))) return true;
            }
        }
        return false;
    }

    private dvec3 CrouchMotion(Player player, dvec3 motion)
    {
        double rise = motion.z;
        motion.z = 0;
        // Edge clipping must also follow the vertical motion along an incline.
        dvec3 vertical = new(0, 0, rise);
        dvec3 safe = dvec3.Zero;
        // Resolve axes separately so a diagonal approach to an edge can slide along it.
        for (int axis = 0; axis < 2; axis++)
        {
            dvec3 part = axis == 0 ? new dvec3(motion.x, 0, 0) : new dvec3(0, motion.y, 0);
            if (part.LengthSqr < 1e-20) continue;
            if (CrouchSupported(player, vertical + safe + part))
                safe += part;
            else
            {
                double low = 0, high = 1;
                for (int i = 0; i < 16; i++)
                {
                    double middle = (low + high) / 2;
                    if (CrouchSupported(player, vertical + safe + part * middle))
                        low = middle;
                    else
                        high = middle;
                }
                // Leave a finite patch rather than balancing on the exact clipping
                // boundary, where roundoff can lock the next move along the edge.
                safe += part * Math.Max(0, low - .001 / part.Length);
            }
        }
        return safe;
    }

    /// <summary>Placement tests actual occupied volumes, allowing the solver's contact slop.</summary>
    public bool CanPlaceBlock(VoxelBody target, ivec3 cell, Voxel voxel, bool replacing = false)
    {
        if (!replacing && Blocks.IsSolid(target.Grid[cell].Type)) return false;
        if (target is VoxelMap && (cell.z < 0 || cell.z >= VoxelMap.HeightInChunks * Grid3D<Voxel>.ChunkSize)) return false;
        double tolerance = Math.Max(1e-5, Physics.Settings.PenetrationTolerance);
        bool partial = BlockShapes.HasShape(voxel.Type);
        foreach (ivec3 part in partial ? BlockShapes.Parts(voxel) : new[] { ivec3.Zero })
        {
            var bounds = BlockShapes.PartBounds(voxel, part);
            dvec3 offset = (bounds.Low + bounds.High) / 2;
            CollisionBox box = new(target.ToWorld((new dvec3(cell) + offset) * target.CellEdge),
                (bounds.High - bounds.Low) * (target.CellEdge / 2), target.Orientation);
            if (Player is { } player)
            {
                CollisionBox person = new(player.Feet + new dvec3(0, 0, player.CurrentHeight / 2),
                    new dvec3(Player.Width / 2, Player.Width / 2, player.CurrentHeight / 2), dquat.Identity);
                if (BoxPenetration(box, person, out dvec3 push) && push.Length > tolerance) return false;
            }
            foreach (VoxelBody other in Bodies)
            {
                if (ReferenceEquals(other, target)) continue;
                foreach (CollisionBox occupied in new VoxelCellQuery(box, other))
                    if (BoxPenetration(box, occupied, out dvec3 push) && push.Length > tolerance) return false;
            }
        }
        return true;
    }

    private void SeparateOverlaps()
    {
        _collisionQueries.Prepare(Map, _objects);
        _collisionQueries.Recover(Player);
    }
}
