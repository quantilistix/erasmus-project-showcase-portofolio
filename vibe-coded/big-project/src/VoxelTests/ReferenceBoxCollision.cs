// Retained pre-optimization geometry for numerical and allocation comparisons.
using GlmSharp;

using VoxelGame;

namespace VoxelTests;

internal static class ReferenceBoxCollision
{
    // A box in world space. Voxel objects can rotate; the player's box stays upright.
    internal readonly record struct CollisionBox(dvec3 Center, dvec3 Half, dquat Rotation)
    {
        public dvec3 Axis(int i) => Rotation * (i == 0 ? dvec3.UnitX : i == 1 ? dvec3.UnitY : dvec3.UnitZ);
        public dvec3 Extent => dvec3.Abs(Axis(0)) * Half.x + dvec3.Abs(Axis(1)) * Half.y + dvec3.Abs(Axis(2)) * Half.z;
    }

    /// <summary>Separating axis test, including edge cross products for inclined boxes.</summary>
    internal static bool BoxPenetration(CollisionBox a, CollisionBox b, out dvec3 separation)
    {
        dvec3 delta = a.Center - b.Center;
        double depth = double.PositiveInfinity;
        dvec3 normal = dvec3.Zero;
        for (int k = 0; k < 15; k++)
        {
            dvec3 axis = k < 3 ? a.Axis(k) : k < 6 ? b.Axis(k - 3) : dvec3.Cross(a.Axis((k - 6) / 3), b.Axis((k - 6) % 3));
            if (axis.LengthSqr < 1e-12)
                continue;
            axis = axis.Normalized;
            double Radius(CollisionBox box) => Math.Abs(dvec3.Dot(axis, box.Axis(0))) * box.Half.x
                + Math.Abs(dvec3.Dot(axis, box.Axis(1))) * box.Half.y
                + Math.Abs(dvec3.Dot(axis, box.Axis(2))) * box.Half.z;
            double signed = dvec3.Dot(delta, axis);
            double overlap = Radius(a) + Radius(b) - Math.Abs(signed);
            if (overlap <= 1e-8)
            {
                separation = dvec3.Zero;
                return false;
            }
            if (overlap < depth)
            {
                depth = overlap;
                normal = signed >= 0 ? axis : -axis;
            }
        }
        separation = normal * depth;
        return true;
    }

    // Clip each upward voxel face to the sole's search volume. Using the face normal
    // rather than the minimum separation axis keeps a narrow overlap at a ledge valid.
    internal static bool WalkableFaceIn(CollisionBox sole, CollisionBox cell)
    {
        dvec3 low = sole.Center - sole.Half, high = sole.Center + sole.Half;
        for (int axis = 0; axis < 3; axis++)
        {
            dvec3 normal = cell.Axis(axis);
            if (normal.z < 0)
                normal = -normal;
            if (normal.z < Player.SupportSlope)
                continue;
            dvec3 center = cell.Center + normal * cell.Half[axis];
            dvec3 u = cell.Axis((axis + 1) % 3) * cell.Half[(axis + 1) % 3];
            dvec3 v = cell.Axis((axis + 2) % 3) * cell.Half[(axis + 2) % 3];
            List<dvec3> polygon = [center - u - v, center + u - v, center + u + v, center - u + v];
            for (int plane = 0; plane < 6 && polygon.Count > 0; plane++)
            {
                int component = plane / 2;
                bool lower = plane % 2 == 0;
                double boundary = lower ? low[component] : high[component];
                double Distance(dvec3 point) => lower ? point[component] - boundary : boundary - point[component];
                List<dvec3> clipped = [];
                dvec3 previous = polygon[^1];
                double before = Distance(previous);
                foreach (dvec3 current in polygon)
                {
                    double after = Distance(current);
                    if ((before >= 0) != (after >= 0))
                        clipped.Add(previous + (current - previous) * (before / (before - after)));
                    if (after >= 0)
                        clipped.Add(current);
                    previous = current;
                    before = after;
                }
                polygon = clipped;
            }
            if (polygon.Count >= 3)
                return true;
        }
        return false;
    }

}
