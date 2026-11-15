using GlmSharp;

namespace VoxelGame;

internal static class BoxCollision
{
    // A box in world space. Voxel objects can rotate; the player's box stays upright.
    internal readonly record struct CollisionBox
    {
        public CollisionBox(dvec3 center, dvec3 half, dquat rotation)
        {
            Center = center;
            Half = half;
            Rotation = rotation;
            _x = rotation * dvec3.UnitX;
            _y = rotation * dvec3.UnitY;
            _z = rotation * dvec3.UnitZ;
        }

        public dvec3 Center { get; init; }
        public dvec3 Half { get; init; }
        public dquat Rotation { get; }
        private readonly dvec3 _x, _y, _z;
        public dvec3 Axis(int i) => i == 0 ? _x : i == 1 ? _y : _z;
        public dvec3 Extent => dvec3.Abs(_x) * Half.x + dvec3.Abs(_y) * Half.y + dvec3.Abs(_z) * Half.z;
    }

    /// <summary>Separating axis test, including edge cross products for inclined boxes.</summary>
    internal static bool BoxPenetration(in CollisionBox a, in CollisionBox b, out dvec3 separation)
    {
        dvec3 delta = a.Center - b.Center;
        double depth = double.PositiveInfinity;
        dvec3 normal = dvec3.Zero;
        // Terrain and upright objects need only the three unique world axes. This has
        // the same axis/tie order and arithmetic as the general fifteen-axis test.
        if (a.Rotation == dquat.Identity && b.Rotation == dquat.Identity)
        {
            for (int i = 0; i < 3; i++)
            {
                double overlap = a.Half[i] + b.Half[i] - Math.Abs(delta[i]);
                if (overlap <= 1e-8)
                {
                    separation = dvec3.Zero;
                    return false;
                }
                if (overlap < depth)
                {
                    depth = overlap;
                    normal = i == 0 ? dvec3.UnitX : i == 1 ? dvec3.UnitY : dvec3.UnitZ;
                    if (delta[i] < 0)
                        normal = -normal;
                }
            }
            separation = normal * depth;
            return true;
        }
        for (int k = 0; k < 15; k++)
        {
            dvec3 axis = k < 3 ? a.Axis(k) : k < 6 ? b.Axis(k - 3) : dvec3.Cross(a.Axis((k - 6) / 3), b.Axis((k - 6) % 3));
            if (axis.LengthSqr < 1e-12)
                continue;
            axis = axis.Normalized;
            double Radius(in CollisionBox box) => Math.Abs(dvec3.Dot(axis, box.Axis(0))) * box.Half.x
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

    // Every cell in a voxel-body pair has the same orientation and half extents.
    // Compute the separating axes/radii once per pair, retaining the original axis order.
    internal readonly ref struct BoxPair
    {
        private readonly ReadOnlySpan<dvec3> _axes;
        private readonly ReadOnlySpan<double> _radii;
        public BoxPair(in CollisionBox a, in CollisionBox b, Span<dvec3> axes, Span<double> radii)
        {
            int count = 0;
            bool aligned = a.Rotation == dquat.Identity && b.Rotation == dquat.Identity;
            for (int k = 0; k < (aligned ? 3 : 15); k++)
            {
                dvec3 axis = k < 3 ? a.Axis(k) : k < 6 ? b.Axis(k - 3) : dvec3.Cross(a.Axis((k - 6) / 3), b.Axis((k - 6) % 3));
                if (axis.LengthSqr < 1e-12) continue;
                axis = axis.Normalized;
                double ra = Math.Abs(dvec3.Dot(axis, a.Axis(0))) * a.Half.x
                    + Math.Abs(dvec3.Dot(axis, a.Axis(1))) * a.Half.y + Math.Abs(dvec3.Dot(axis, a.Axis(2))) * a.Half.z;
                double rb = Math.Abs(dvec3.Dot(axis, b.Axis(0))) * b.Half.x
                    + Math.Abs(dvec3.Dot(axis, b.Axis(1))) * b.Half.y + Math.Abs(dvec3.Dot(axis, b.Axis(2))) * b.Half.z;
                axes[count] = axis;
                radii[count++] = ra + rb;
            }
            _axes = axes[..count];
            _radii = radii[..count];
        }
        public bool Penetration(dvec3 delta, out dvec3 separation)
        {
            double depth = double.PositiveInfinity;
            dvec3 normal = dvec3.Zero;
            for (int i = 0; i < _axes.Length; i++)
            {
                dvec3 axis = _axes[i];
                double signed = dvec3.Dot(delta, axis);
                double overlap = _radii[i] - Math.Abs(signed);
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
    }

    internal static bool WalkableTriangleIn(in CollisionBox sole, dvec3 a, dvec3 b, dvec3 c)
    {
        dvec3 normal = dvec3.Cross(b - a, c - a);
        if (normal.LengthSqr < 1e-20 || normal.z < Player.SupportSlope * normal.Length) return false;
        Span<dvec3> polygon = stackalloc dvec3[12];
        polygon[0] = a; polygon[1] = b; polygon[2] = c;
        return FaceIn(sole, polygon, 3);
    }

    private static bool FaceIn(in CollisionBox sole, Span<dvec3> polygon, int count)
    {
        dvec3 low = sole.Center - sole.Half, high = sole.Center + sole.Half;
        Span<dvec3> clipped = stackalloc dvec3[12];
        for (int plane = 0; plane < 6 && count > 0; plane++)
        {
            int component = plane / 2;
            bool lower = plane % 2 == 0;
            double boundary = lower ? low[component] : high[component];
            double Distance(dvec3 point) => lower ? point[component] - boundary : boundary - point[component];
            int written = 0;
            dvec3 previous = polygon[count - 1];
            double before = Distance(previous);
            foreach (dvec3 current in polygon[..count])
            {
                double after = Distance(current);
                if ((before >= 0) != (after >= 0))
                    clipped[written++] = previous + (current - previous) * (before / (before - after));
                if (after >= 0) clipped[written++] = current;
                previous = current;
                before = after;
            }
            clipped[..written].CopyTo(polygon);
            count = written;
        }
        return count >= 3;
    }

    // Clip each upward voxel face to the sole's search volume. Using the face normal
    // rather than the minimum separation axis keeps a narrow overlap at a ledge valid.
    internal static bool WalkableFaceIn(in CollisionBox sole, in CollisionBox cell)
    {
        Span<dvec3> first = stackalloc dvec3[12];
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
            // A convex quadrilateral clipped against six planes has at most ten vertices.
            // Reuse stack buffers, including at a lip's sixteen bisection queries.
            Span<dvec3> polygon = first;
            polygon[0] = center - u - v;
            polygon[1] = center + u - v;
            polygon[2] = center + u + v;
            polygon[3] = center - u + v;
            int count = 4;
            if (FaceIn(sole, polygon, count))
                return true;
        }
        return false;
    }

}
