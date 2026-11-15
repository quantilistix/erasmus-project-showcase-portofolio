namespace NeuralGraph;

/// <summary>Topological execution groups for acyclic graphs with arbitrary skip connections.</summary>
internal sealed class LayerSchedule
{
    public int[] Nodes { get; }
    public (int Start, int Count)[] Layers { get; }
    public LayerSchedule(GraphTopology topology)
    {
        int n = topology.NodeCount;
        var remaining = new int[n]; var depth = new int[n]; var queue = new Queue<int>();
        for (int node = 0; node < n; node++)
        {
            remaining[node] = topology.IncomingOffsets[node + 1] - topology.IncomingOffsets[node];
            if (remaining[node] == 0) { depth[node] = node < topology.InputCount ? 0 : 1; queue.Enqueue(node); }
        }
        int visited = 0;
        while (queue.TryDequeue(out int node))
        {
            visited++;
            for (int k = topology.OutgoingOffsets[node]; k < topology.OutgoingOffsets[node + 1]; k++)
            {
                int destination = topology.Edges[topology.OutgoingEdges[k]].Destination;
                depth[destination] = Math.Max(depth[destination], depth[node] + 1);
                if (--remaining[destination] == 0) queue.Enqueue(destination);
            }
        }
        if (visited != n) throw new ArgumentException("Layered execution requires an acyclic graph.");
        if (topology.Steps < depth.Max()) throw new ArgumentException("Graph steps must cover its longest feed-forward path.");
        Nodes = Enumerable.Range(topology.InputCount, n - topology.InputCount).OrderBy(node => depth[node]).ToArray();
        var layers = new List<(int, int)>(); int start = 0;
        while (start < Nodes.Length)
        {
            int end = start + 1;
            while (end < Nodes.Length && depth[Nodes[end]] == depth[Nodes[start]]) end++;
            layers.Add((start, end - start)); start = end;
        }
        Layers = layers.ToArray();
    }
}
