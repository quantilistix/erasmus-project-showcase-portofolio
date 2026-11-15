using System.Runtime.InteropServices;

using GlmSharp;

namespace MYT.Kit.Vulkan;


/// <summary>
/// One node of a <see cref="BvhBuilder"/> hierarchy, matching the GLSL BVH node. The w components
/// hold uint bit patterns. An interior node has count 0, its left child is the next node and its
/// right child is node <c>a</c>; a leaf's triangles are <c>PrimitiveOrder[a .. a + count)</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuBvhNode
{
    public vec4 Min;    // xyz = lower bound, w = a
    public vec4 Max;    // xyz = upper bound, w = count
}


/// <summary>
/// Builds a static bounding volume hierarchy over a triangle mesh with binned SAH, flattened
/// depth-first into the layout the collision shaders traverse.
/// </summary>
internal static class BvhBuilder
{
    private const int BinCount = 16;

    private struct Primitive
    {
        public dvec3 Min;
        public dvec3 Max;
        public uint Triangle;
    }

    private struct Bin
    {
        public dvec3 Min;
        public dvec3 Max;
        public int Count;
    }

    /// <summary>
    /// A range of primitives still to become a subtree. <c>RightChildOf</c> is the interior node
    /// that must point at it, or -1 for the root and left children, which the layout implies.
    /// </summary>
    private readonly record struct BuildTask(int Start, int Count, int RightChildOf);

    /// <summary>
    /// Builds the hierarchy over <paramref name="triangles"/>, whose indices refer to
    /// <paramref name="vertices"/> in the BVH's own frame. Nodes are in depth-first order with the
    /// root at 0; <c>PrimitiveOrder</c> lists every triangle index once, ordered so each leaf's
    /// triangles are contiguous. A leaf holds at most <paramref name="maxLeafSize"/> triangles
    /// unless their centroids all coincide. Bounds are rounded outward to float, so they always
    /// contain the double-precision triangles. With no triangles both arrays are empty, since a
    /// leaf cannot encode a count of 0: callers must treat zero nodes as nothing to collide with.
    /// </summary>
    public static (GpuBvhNode[] Nodes, uint[] PrimitiveOrder) Build
    (
        ReadOnlySpan<dvec3> vertices,
        ReadOnlySpan<uvec3> triangles,
        int maxLeafSize = 4
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLeafSize, 1);

        if (triangles.Length == 0)
            return ([], []);

        return Build(CreatePrimitives(vertices, triangles), maxLeafSize);
    }

    /// <summary>A top-level hierarchy over the nonempty collision bodies' initial world bounds.</summary>
    public static (GpuBvhNode[] Nodes, uint[] PrimitiveOrder) BuildBodies(ReadOnlySpan<GpuCollisionBody> bodies)
    {
        List<Primitive> primitives = [];
        for (int i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].Info.w == 0)
                continue;
            primitives.Add(new Primitive
            {
                Min = new dvec3(bodies[i].BoundsMin.xyz),
                Max = new dvec3(bodies[i].BoundsMax.xyz),
                Triangle = (uint)i
            });
        }
        return primitives.Count == 0 ? ([], []) : Build(primitives.ToArray(), 2);
    }

    private static (GpuBvhNode[] Nodes, uint[] PrimitiveOrder) Build(Primitive[] primitives, int maxLeafSize)
    {
        // Every leaf holds at least one triangle, so the tree has at most 2n - 1 nodes.
        GpuBvhNode[] nodes = new GpuBvhNode[2 * primitives.Length - 1];
        int nodeCount = 0;

        Stack<BuildTask> tasks = new();
        tasks.Push(new BuildTask(0, primitives.Length, -1));

        while (tasks.TryPop(out BuildTask task))
        {
            int nodeIndex = nodeCount++;
            if (task.RightChildOf >= 0)
                nodes[task.RightChildOf].Min.w = BitConverter.UInt32BitsToSingle((uint)nodeIndex);

            Span<Primitive> range = primitives.AsSpan(task.Start, task.Count);
            (dvec3 min, dvec3 max, dvec3 centroidMin, dvec3 centroidMax) = MeasureRange(range);
            int leftCount = range.Length > maxLeafSize ? Split(range, min, max, centroidMin, centroidMax) : 0;

            if (leftCount == 0)
            {
                nodes[nodeIndex] = CreateNode(min, max, (uint)task.Start, (uint)task.Count);
                continue;
            }

            // The right child's index is filled in once the whole left subtree has been laid out.
            nodes[nodeIndex] = CreateNode(min, max, 0, 0);
            tasks.Push(new BuildTask(task.Start + leftCount, task.Count - leftCount, nodeIndex));
            tasks.Push(new BuildTask(task.Start, leftCount, -1));
        }

        uint[] primitiveOrder = new uint[primitives.Length];
        for (int i = 0; i < primitives.Length; i++)
            primitiveOrder[i] = primitives[i].Triangle;

        Array.Resize(ref nodes, nodeCount);
        return (nodes, primitiveOrder);
    }

    private static Primitive[] CreatePrimitives(ReadOnlySpan<dvec3> vertices, ReadOnlySpan<uvec3> triangles)
    {
        Primitive[] primitives = new Primitive[triangles.Length];

        for (int i = 0; i < triangles.Length; i++)
        {
            uvec3 t = triangles[i];
            if (t.x >= vertices.Length || t.y >= vertices.Length || t.z >= vertices.Length)
                throw new ArgumentException($"Triangle {i} indexes past the {vertices.Length} vertices", nameof(triangles));

            dvec3 a = vertices[(int)t.x];
            dvec3 b = vertices[(int)t.y];
            dvec3 c = vertices[(int)t.z];
            dvec3 min = dvec3.Min(dvec3.Min(a, b), c);
            dvec3 max = dvec3.Max(dvec3.Max(a, b), c);
            if (!IsFinite(min) || !IsFinite(max))
                throw new ArgumentException($"Triangle {i} has a non-finite vertex", nameof(vertices));

            primitives[i] = new Primitive { Min = min, Max = max, Triangle = (uint)i };
        }

        return primitives;
    }

    private static (dvec3 Min, dvec3 Max, dvec3 CentroidMin, dvec3 CentroidMax) MeasureRange(ReadOnlySpan<Primitive> range)
    {
        dvec3 min = new(double.PositiveInfinity);
        dvec3 max = new(double.NegativeInfinity);
        dvec3 centroidMin = min;
        dvec3 centroidMax = max;

        foreach (ref readonly Primitive primitive in range)
        {
            min = dvec3.Min(min, primitive.Min);
            max = dvec3.Max(max, primitive.Max);
            dvec3 centroid = 0.5 * (primitive.Min + primitive.Max);
            centroidMin = dvec3.Min(centroidMin, centroid);
            centroidMax = dvec3.Max(centroidMax, centroid);
        }

        return (min, max, centroidMin, centroidMax);
    }

    /// <summary>
    /// Reorders the range into two non-empty halves and returns the size of the left one, or 0 when
    /// every centroid coincides and no split can separate the triangles.
    /// </summary>
    private static int Split
    (
        Span<Primitive> range,
        dvec3 min,
        dvec3 max,
        dvec3 centroidMin,
        dvec3 centroidMax
    )
    {
        dvec3 centroidExtent = centroidMax - centroidMin;
        int axis = LongestAxis(centroidExtent);
        double extent = Component(centroidExtent, axis);
        if (extent == 0)
            return 0;

        double axisMin = Component(centroidMin, axis);
        int lastLeftBin = FindSahSplit(range, axis, axisMin, extent, HalfArea(min, max));
        if (lastLeftBin >= 0)
            return PartitionByBin(range, axis, axisMin, extent, lastLeftBin);

        SelectMedian(range, axis);
        return range.Length / 2;
    }

    /// <summary>
    /// Bins centroids along the axis and returns the last bin of the cheapest split's left half,
    /// or -1 when no split is cheaper than keeping every triangle in one node.
    /// </summary>
    private static int FindSahSplit
    (
        ReadOnlySpan<Primitive> range,
        int axis,
        double axisMin,
        double extent,
        double nodeArea
    )
    {
        Span<Bin> bins = stackalloc Bin[BinCount];
        bins.Fill(new Bin { Min = new dvec3(double.PositiveInfinity), Max = new dvec3(double.NegativeInfinity) });

        foreach (ref readonly Primitive primitive in range)
        {
            ref Bin bin = ref bins[BinIndex(primitive, axis, axisMin, extent)];
            bin.Min = dvec3.Min(bin.Min, primitive.Min);
            bin.Max = dvec3.Max(bin.Max, primitive.Max);
            bin.Count++;
        }

        // leftCosts[i] covers bins 0 ..= i; entries whose bins are all empty are never read.
        Span<double> leftCosts = stackalloc double[BinCount - 1];
        dvec3 leftMin = new(double.PositiveInfinity);
        dvec3 leftMax = new(double.NegativeInfinity);
        int leftCount = 0;
        for (int i = 0; i < BinCount - 1; i++)
        {
            leftMin = dvec3.Min(leftMin, bins[i].Min);
            leftMax = dvec3.Max(leftMax, bins[i].Max);
            leftCount += bins[i].Count;
            leftCosts[i] = leftCount * HalfArea(leftMin, leftMax);
        }

        double bestCost = range.Length * nodeArea;
        int bestBin = -1;
        dvec3 rightMin = new(double.PositiveInfinity);
        dvec3 rightMax = new(double.NegativeInfinity);
        int rightCount = 0;
        for (int i = BinCount - 1; i > 0; i--)
        {
            rightMin = dvec3.Min(rightMin, bins[i].Min);
            rightMax = dvec3.Max(rightMax, bins[i].Max);
            rightCount += bins[i].Count;
            if (rightCount == 0 || rightCount == range.Length)
                continue;

            double cost = leftCosts[i - 1] + rightCount * HalfArea(rightMin, rightMax);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestBin = i - 1;
            }
        }

        return bestBin;
    }

    private static int PartitionByBin
    (
        Span<Primitive> range,
        int axis,
        double axisMin,
        double extent,
        int lastLeftBin
    )
    {
        int left = 0;
        int right = range.Length;

        while (left < right)
        {
            if (BinIndex(range[left], axis, axisMin, extent) <= lastLeftBin)
            {
                left++;
            }
            else
            {
                right--;
                (range[left], range[right]) = (range[right], range[left]);
            }
        }

        return left;
    }

    /// <summary>
    /// Hoare quickselect around the middle index: afterwards no centroid before it lies above any
    /// centroid from it onward.
    /// </summary>
    private static void SelectMedian(Span<Primitive> range, int axis)
    {
        int middle = range.Length / 2;
        int low = 0;
        int high = range.Length - 1;

        while (low < high)
        {
            double pivot = Centroid(range[low + (high - low) / 2], axis);
            int i = low;
            int j = high;

            while (i <= j)
            {
                while (Centroid(range[i], axis) < pivot)
                    i++;
                while (Centroid(range[j], axis) > pivot)
                    j--;

                if (i <= j)
                {
                    (range[i], range[j]) = (range[j], range[i]);
                    i++;
                    j--;
                }
            }

            if (middle <= j)
                high = j;
            else if (middle >= i)
                low = i;
            else
                break;
        }
    }

    private static int BinIndex(in Primitive primitive, int axis, double axisMin, double extent)
    {
        int bin = (int)(BinCount * ((Centroid(primitive, axis) - axisMin) / extent));
        return Math.Clamp(bin, 0, BinCount - 1);
    }

    private static double Centroid(in Primitive primitive, int axis)
    {
        return 0.5 * (Component(primitive.Min, axis) + Component(primitive.Max, axis));
    }

    private static double Component(in dvec3 v, int axis)
    {
        return axis switch
        {
            0 => v.x,
            1 => v.y,
            _ => v.z
        };
    }

    private static int LongestAxis(dvec3 extent)
    {
        if (extent.x >= extent.y && extent.x >= extent.z)
            return 0;
        return extent.y >= extent.z ? 1 : 2;
    }

    private static double HalfArea(dvec3 min, dvec3 max)
    {
        dvec3 size = max - min;
        return size.x * size.y + size.y * size.z + size.z * size.x;
    }

    private static bool IsFinite(dvec3 v)
    {
        return double.IsFinite(v.x) && double.IsFinite(v.y) && double.IsFinite(v.z);
    }

    private static GpuBvhNode CreateNode(dvec3 min, dvec3 max, uint a, uint count)
    {
        return new GpuBvhNode
        {
            Min = new vec4(RoundDown(min.x), RoundDown(min.y), RoundDown(min.z), BitConverter.UInt32BitsToSingle(a)),
            Max = new vec4(RoundUp(max.x), RoundUp(max.y), RoundUp(max.z), BitConverter.UInt32BitsToSingle(count))
        };
    }

    /// <summary>The largest float not above <paramref name="value"/>.</summary>
    internal static float RoundDown(double value)
    {
        float rounded = (float)value;
        return rounded > value ? MathF.BitDecrement(rounded) : rounded;
    }

    /// <summary>The smallest float not below <paramref name="value"/>.</summary>
    internal static float RoundUp(double value)
    {
        float rounded = (float)value;
        return rounded < value ? MathF.BitIncrement(rounded) : rounded;
    }
}
