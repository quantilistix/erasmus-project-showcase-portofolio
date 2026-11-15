using NeuralGraph;

namespace ArcadeLab;

/// <summary>Deterministic success feedback from the game, independent of the learned readout.</summary>
public static class PrimitiveRewardSignal
{
    public static float Level(float previous, bool caught) => MathF.Max(previous * .85f, caught ? 1 : 0);
    /// <summary>The catch is credited once; the decaying sensory trace is not paid repeatedly.</summary>
    public static float LearningReward(float pleasure, float pain, bool caught, float learnedWeight = .25f, float primitiveWeight = 1)
        => learnedWeight * (pleasure - pain) + (caught ? primitiveWeight : 0);

    public static GraphModel ExpandLegacy(GraphModel source, int count, int firstHiddenWidth)
    {
        var old = source.Topology;
        if (count < 1 || firstHiddenWidth < 1 || firstHiddenWidth > old.NodeCount - old.InputCount) throw new ArgumentException("Invalid primitive reward input expansion.");
        var edges = new Edge[checked(old.Edges.Length + count * firstHiddenWidth)];
        for (int i = 0; i < old.Edges.Length; i++)
        {
            var e = old.Edges[i]; edges[i] = new(e.Source < old.InputCount ? e.Source : e.Source + count, e.Destination + count);
        }
        int cursor = old.Edges.Length;
        for (int input = 0; input < count; input++) for (int node = 0; node < firstHiddenWidth; node++)
            edges[cursor++] = new(old.InputCount + input, old.InputCount + count + node);
        var expanded = new GraphModel(new(old.InputCount + count, old.NodeCount + count, old.Steps, old.Outputs.Select(n => n + count).ToArray(), edges));
        Array.Clear(expanded.Parameters);
        source.Parameters.AsSpan(0, old.Edges.Length).CopyTo(expanded.Parameters);
        source.Parameters.AsSpan(old.Edges.Length).CopyTo(expanded.Parameters.AsSpan(edges.Length));
        source.Plasticity.CopyTo(expanded.Plasticity, 0); if (source.IsFrozen) expanded.Freeze(); return expanded;
    }
}
