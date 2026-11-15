using NeuralGraph;

namespace ArcadeLab;

public static class ClockInput
{
    public const int Count = 1;
    /// <summary>Nonnegative sine oscillator, one cycle per simulated second at one hertz. Pauses with frame advancement.</summary>
    public static float Value(long frame, int fps, float hertz)
    {
        if (frame < 0 || fps < 1 || !float.IsFinite(hertz) || hertz <= 0) throw new ArgumentException("Invalid clock frame/rate.");
        double phase = (frame * (double)hertz / fps) % 1;
        return (float)(.5 + .5 * Math.Sin(2 * Math.PI * phase));
    }
    /// <summary>Append a clamped clock input, shift hidden/output IDs and preserve the existing function with zero new clock weights.</summary>
    public static GraphModel ExpandLegacy(GraphModel source, int firstHiddenWidth)
    {
        var old = source.Topology;
        if (firstHiddenWidth < 1 || old.InputCount + firstHiddenWidth > old.NodeCount) throw new ArgumentException("Invalid first hidden layer.");
        var edges = new Edge[checked(old.Edges.Length + firstHiddenWidth)];
        for (int i = 0; i < old.Edges.Length; i++)
        {
            var e = old.Edges[i]; edges[i] = new(e.Source < old.InputCount ? e.Source : e.Source + 1, e.Destination + 1);
        }
        for (int i = 0; i < firstHiddenWidth; i++) edges[old.Edges.Length + i] = new(old.InputCount, old.InputCount + 1 + i);
        var expanded = new GraphModel(new(old.InputCount + 1, old.NodeCount + 1, old.Steps, old.Outputs.Select(n => n + 1).ToArray(), edges));
        Array.Clear(expanded.Parameters);
        source.Parameters.AsSpan(0, old.Edges.Length).CopyTo(expanded.Parameters);
        source.Parameters.AsSpan(old.Edges.Length).CopyTo(expanded.Parameters.AsSpan(edges.Length));
        source.Plasticity.CopyTo(expanded.Plasticity, 0);
        if (source.IsFrozen) expanded.Freeze();
        return expanded;
    }
}
