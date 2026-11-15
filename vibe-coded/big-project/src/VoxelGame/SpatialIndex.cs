using GlmSharp;

namespace VoxelGame;

/// <summary>Reusable conservative bounds with stable input indices; queries never allocate.</summary>
internal readonly record struct SpatialBounds(dvec3 Low, dvec3 High)
{
    public bool Intersects(in SpatialBounds other) => High.x >= other.Low.x && Low.x <= other.High.x
        && High.y >= other.Low.y && Low.y <= other.High.y && High.z >= other.Low.z && Low.z <= other.High.z;
    public static SpatialBounds Union(in SpatialBounds a, in SpatialBounds b) => new(dvec3.Min(a.Low, b.Low), dvec3.Max(a.High, b.High));
}

/// <summary>A balanced bounds hierarchy, refitted after motion and updated immediately after corrections.</summary>
internal sealed class SpatialIndex
{
    private struct Node
    {
        public SpatialBounds Bounds;
        public int Left, Right, Parent, Entry;
    }
    private sealed class AxisComparer(SpatialIndex owner, int axis) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            SpatialBounds x = owner._bounds[a], y = owner._bounds[b];
            int order = (x.Low[axis] + x.High[axis]).CompareTo(y.Low[axis] + y.High[axis]);
            return order != 0 ? order : a.CompareTo(b);
        }
    }
    private readonly AxisComparer[] _comparers;
    private Node[] _nodes = [];
    private int[] _order = [], _leaves = [];
    private SpatialBounds[] _bounds = [];
    private int _nodeCount;
    public int Count { get; private set; }

    public SpatialIndex() => _comparers = [new(this, 0), new(this, 1), new(this, 2)];

    public void Build(ReadOnlySpan<SpatialBounds> bounds)
    {
        Count = bounds.Length;
        if (_order.Length < Count)
        {
            int capacity = Math.Max(Count, _order.Length * 2);
            _order = new int[capacity];
            _leaves = new int[capacity];
            _bounds = new SpatialBounds[capacity];
            _nodes = new Node[capacity * 2];
        }
        bounds.CopyTo(_bounds);
        for (int i = 0; i < Count; i++) _order[i] = i;
        _nodeCount = 0;
        if (Count > 0) BuildNode(0, Count, -1);
    }

    private int BuildNode(int start, int count, int parent)
    {
        int index = _nodeCount++;
        SpatialBounds bounds = _bounds[_order[start]];
        for (int i = start + 1; i < start + count; i++) bounds = SpatialBounds.Union(bounds, _bounds[_order[i]]);
        _nodes[index] = new Node { Bounds = bounds, Parent = parent, Entry = -1 };
        if (count == 1)
        {
            int entry = _order[start];
            _nodes[index].Entry = entry;
            _leaves[entry] = index;
        }
        else
        {
            dvec3 extent = bounds.High - bounds.Low;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            Array.Sort(_order, start, count, _comparers[axis]);
            int half = count / 2;
            int left = BuildNode(start, half, index), right = BuildNode(start + half, count - half, index);
            _nodes[index].Left = left;
            _nodes[index].Right = right;
        }
        return index;
    }

    public void Refit(ReadOnlySpan<SpatialBounds> bounds)
    {
        if (bounds.Length != Count) throw new ArgumentException("Refit needs the same entries", nameof(bounds));
        for (int i = 0; i < Count; i++) _nodes[_leaves[i]].Bounds = bounds[i];
        for (int i = _nodeCount - 1; i >= 0; i--)
            if (_nodes[i].Entry < 0)
                _nodes[i].Bounds = SpatialBounds.Union(_nodes[_nodes[i].Left].Bounds, _nodes[_nodes[i].Right].Bounds);
    }

    public void Update(int entry, in SpatialBounds bounds)
    {
        int leaf = _leaves[entry];
        _nodes[leaf].Bounds = bounds;
        for (int i = _nodes[leaf].Parent; i >= 0; i = _nodes[i].Parent)
            _nodes[i].Bounds = SpatialBounds.Union(_nodes[_nodes[i].Left].Bounds, _nodes[_nodes[i].Right].Bounds);
    }

    public void Query(in SpatialBounds bounds, List<int> entries)
    {
        entries.Clear();
        if (Count > 0) Visit(0, bounds, entries);
    }

    private void Visit(int index, in SpatialBounds bounds, List<int> entries)
    {
        ref Node node = ref _nodes[index];
        if (!node.Bounds.Intersects(bounds)) return;
        if (node.Entry >= 0) entries.Add(node.Entry);
        else
        {
            Visit(node.Left, bounds, entries);
            Visit(node.Right, bounds, entries);
        }
    }
}
