using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;

/// <summary>A conservative body hierarchy for a batch of debris rays at one world pose.</summary>
internal sealed class ParticleRaycaster
{
    private readonly record struct Entry(VoxelObject Body, dvec3 Center, double Radius, int Order);
    private readonly record struct Node(dvec3 Low, dvec3 High, int Left, int Right, int Entry);
    private sealed class AxisComparer(int axis) : IComparer<Entry>
    {
        public int Compare(Entry a, Entry b) => a.Center[axis].CompareTo(b.Center[axis]);
    }
    private static readonly AxisComparer[] Comparers = [new(0), new(1), new(2)];
    private Entry[] _entries = [];
    private Node[] _nodes = [];
    private readonly List<(VoxelObject Body, int Order)> _dirty = [];
    private int _nodeCount;
    private int _entryCount;
    private int _preparedCount = -1;
    private int _root = -1;
    private VoxelMap _map = null!;

    // Rebuild once after motion and damage, never in the middle of a particle batch.
    public void Prepare(World world)
    {
        int count = world.Objects.Count;
        if (_entries.Length < count)
        {
            _entries = new Entry[Math.Max(count, _entries.Length * 2)];
            _nodes = new Node[_entries.Length * 2];
        }
        _map = world.Map;
        bool refit = count == _preparedCount;
        for (int i = 0; refit && i < _entryCount; i++)
        {
            Entry entry = _entries[i];
            refit = ReferenceEquals(entry.Body, world.Objects[entry.Order]) && !world.HasPendingTopology(entry.Body);
        }
        foreach (var dirty in _dirty)
        {
            if (!refit)
                break;
            refit = ReferenceEquals(dirty.Body, world.Objects[dirty.Order]) && world.HasPendingTopology(dirty.Body);
        }
        if (refit)
        {
            for (int i = 0; i < _entryCount; i++)
            {
                Entry entry = _entries[i];
                _entries[i] = new(entry.Body, entry.Body.ToWorld(entry.Body.Bounds.Center), entry.Body.Bounds.Radius + 1e-7, entry.Order);
            }
            // Children follow parents in storage, so reverse order updates bounds bottom up.
            for (int i = _nodeCount - 1; i >= 0; i--)
            {
                Node node = _nodes[i];
                dvec3 low, high;
                if (node.Entry >= 0)
                {
                    Entry entry = _entries[node.Entry];
                    low = entry.Center - entry.Radius;
                    high = entry.Center + entry.Radius;
                }
                else
                {
                    low = dvec3.Min(_nodes[node.Left].Low, _nodes[node.Right].Low);
                    high = dvec3.Max(_nodes[node.Left].High, _nodes[node.Right].High);
                }
                _nodes[i] = new(low, high, node.Left, node.Right, node.Entry);
            }
            return;
        }
        _dirty.Clear();
        int live = 0;
        for (int i = 0; i < count; i++)
        {
            VoxelObject body = world.Objects[i];
            if (world.HasPendingTopology(body))
                _dirty.Add((body, i)); // An edit may extend beyond the cooked bounds.
            else
                _entries[live++] = new(body, body.ToWorld(body.Bounds.Center), body.Bounds.Radius + 1e-7, i);
        }
        Array.Clear(_entries, live, _entries.Length - live);
        _nodeCount = 0;
        _entryCount = live;
        _preparedCount = count;
        _root = live == 0 ? -1 : Build(0, live);
    }

    private int Build(int begin, int count)
    {
        dvec3 low = new(double.PositiveInfinity), high = new(double.NegativeInfinity);
        for (int i = begin; i < begin + count; i++)
        {
            Entry entry = _entries[i];
            low = dvec3.Min(low, entry.Center - entry.Radius);
            high = dvec3.Max(high, entry.Center + entry.Radius);
        }
        int node = _nodeCount++;
        if (count == 1)
            _nodes[node] = new(low, high, -1, -1, begin);
        else
        {
            dvec3 extent = high - low;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            Array.Sort(_entries, begin, count, Comparers[axis]);
            int half = count / 2;
            int left = Build(begin, half), right = Build(begin + half, count - half);
            _nodes[node] = new(low, high, left, right, -1);
        }
        return node;
    }

    public VoxelHit? Raycast(dvec3 origin, dvec3 direction, double maxDistance)
    {
        double length = direction.Length;
        if (maxDistance < 0.0)
            return null;
        // A stationary particle still needs an overlap check when geometry moves into it.
        if (!(length > 0.0))
        {
            direction = dvec3.UnitX;
            length = 1;
            maxDistance = 0;
        }
        dvec3 unit = direction / length;
        VoxelHit? nearest = null;
        int nearestOrder = int.MaxValue;
        Test(_map, -1);
        foreach (var dirty in _dirty)
            Test(dirty.Body, dirty.Order);
        if (_root >= 0)
            Visit(_root);
        return nearest;

        void Test(VoxelBody body, int order)
        {
            if (!BlockShapes.Raycast(body.Grid, body.ToLocal(origin) / VoxelBody.CellSize, body.DirectionToLocal(direction),
                (nearest?.Distance ?? maxDistance) / VoxelBody.CellSize, out Grid3DHit hit))
                return;
            double distance = hit.Distance * VoxelBody.CellSize;
            if (nearest == null || distance < nearest.Value.Distance || (distance == nearest.Value.Distance && order < nearestOrder))
            {
                nearest = new(body, hit.Cell, hit.Normal, distance);
                nearestOrder = order;
            }
        }

        void Visit(int index)
        {
            Node node = _nodes[index];
            double near = 0.0, far = nearest?.Distance ?? maxDistance;
            for (int axis = 0; axis < 3; axis++)
            {
                if (unit[axis] == 0.0)
                {
                    if (origin[axis] < node.Low[axis] || origin[axis] > node.High[axis])
                        return;
                }
                else
                {
                    double a = (node.Low[axis] - origin[axis]) / unit[axis];
                    double b = (node.High[axis] - origin[axis]) / unit[axis];
                    near = Math.Max(near, Math.Min(a, b));
                    far = Math.Min(far, Math.Max(a, b));
                    if (near > far)
                        return;
                }
            }
            if (node.Entry >= 0)
            {
                Entry entry = _entries[node.Entry];
                Test(entry.Body, entry.Order);
            }
            else
            {
                Visit(node.Left);
                Visit(node.Right);
            }
        }
    }
}
