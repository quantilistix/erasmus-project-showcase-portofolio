using NeuralGraph;

namespace ArcadeLab;

/// <summary>Reward evidence: sampled hidden neurons, unmodified visual history, then the executed raw button action.</summary>
public static class RewardInputs
{
    public const int VisualCount = 2 * Sensors.PixelCount;
    public const int ControlCount = 3;
    public static int Count(int probes) => checked(probes + VisualCount + ControlCount);

    public static void Compose(ReadOnlySpan<float> probes, ReadOnlySpan<float> observation, int action, Span<float> destination)
    {
        if (observation.Length != Sensors.ObservationSize || destination.Length != Count(probes.Length) || action is < 0 or > 2)
            throw new ArgumentException("Invalid reward evidence shape or executed action.");
        probes.CopyTo(destination); observation[..VisualCount].CopyTo(destination[probes.Length..]);
        destination[^ControlCount..].Clear(); destination[destination.Length - ControlCount + action] = 1;
    }

    /// <summary>Preserve all existing readout weights/biases and behavior; added visual/control connections start at zero.</summary>
    public static GraphModel ExpandLegacy(GraphModel source, int probes)
    {
        var old = source.Topology;
        if (old.InputCount != probes || old.Outputs.Length != 2) throw new ArgumentException("Not a legacy reward readout.");
        int newInputs = Count(probes), extra = newInputs - probes;
        int[] outputs = old.Outputs.Select(n => n + extra).ToArray();
        var edges = new Edge[checked(old.Edges.Length + extra * (old.NodeCount - probes))];
        for (int i = 0; i < old.Edges.Length; i++)
        {
            var e = old.Edges[i]; edges[i] = new(e.Source < probes ? e.Source : e.Source + extra, e.Destination + extra);
        }
        int cursor = old.Edges.Length;
        for (int node = probes; node < old.NodeCount; node++)
            for (int input = probes; input < newInputs; input++) edges[cursor++] = new(input, node + extra);
        var expanded = new GraphModel(new(newInputs, old.NodeCount + extra, old.Steps, outputs, edges));
        Array.Clear(expanded.Parameters);
        source.Parameters.AsSpan(0, old.Edges.Length).CopyTo(expanded.Parameters);
        source.Parameters.AsSpan(old.Edges.Length).CopyTo(expanded.Parameters.AsSpan(edges.Length));
        source.Plasticity.CopyTo(expanded.Plasticity, 0);
        if (source.IsFrozen) expanded.Freeze();
        return expanded;
    }
}
