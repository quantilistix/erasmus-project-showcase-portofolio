using NeuralGraph;

namespace ArcadeLab;

/// <summary>Temporal perceptual summaries, computed solely from pixels and executed controls.</summary>
public sealed class MazeRewardContext
{
    public const int Count = 8;
    public float[] Values { get; } = new float[Count];
    private readonly float[] visualMean = new float[MazeVision.FrameSize], previous = new float[MazeVision.FrameSize];
    private float stillFrames;
    private bool initialized;
    public void Reset() { initialized = false; stillFrames = 0; Array.Clear(Values); Array.Clear(visualMean); Array.Clear(previous); }
    public void Observe(ReadOnlySpan<float> observation, MazeControls controls)
    {
        float change = 0, novelty = 0; const int count = MazeGame.VisionRayCount * MazeVision.Channels;
        for (int i = 0; i < count; i++)
        {
            float value = observation[i]; change += initialized ? MathF.Abs(value - previous[i]) : 0; previous[i] = value;
            if (!initialized) visualMean[i] = value;
            novelty += MathF.Abs(value - visualMean[i]); visualMean[i] += .005f * (value - visualMean[i]);
        }
        initialized = true; change /= count; novelty /= count;
        bool translating = (controls&(MazeControls.Forward|MazeControls.Backward|MazeControls.StrafeLeft|MazeControls.StrafeRight))!=0;
        stillFrames = translating && change > .002f ? 0 : stillFrames + 1;
        Values[0] = change; Values[1] += .1f * (change - Values[1]); Values[2] += .01f * (change - Values[2]);
        Values[3] = Math.Clamp(stillFrames / 180, 0, 1);
        Values[4] += .02f * ((translating ? 1 : 0) - Values[4]);
        Values[5] += .02f * (((controls&(MazeControls.TurnLeft|MazeControls.TurnRight))!=0 ? 1 : 0) - Values[5]);
        Values[6] += .02f * (((controls&MazeControls.Fire)!=0 ? 1 : 0) - Values[6]); Values[7] = novelty;
    }
    /// <summary>Insert zero-weight sensory ports while retaining all existing parameters and their logical mappings.</summary>
    public static GraphModel InsertInputs(GraphModel source, int at, int count, int hiddenWidth)
    {
        var old = source.Topology;
        if (at < 0 || at > old.InputCount || count < 1 || hiddenWidth < 1 || hiddenWidth > old.NodeCount - old.InputCount) throw new ArgumentException("Invalid input expansion.");
        var edges = new Edge[old.Edges.Length + count * hiddenWidth];
        for (int i = 0; i < old.Edges.Length; i++)
        { var edge = old.Edges[i]; edges[i] = new(edge.Source < at ? edge.Source : edge.Source + count, edge.Destination + count); }
        int cursor = old.Edges.Length;
        for (int i = 0; i < count; i++) for (int h = 0; h < hiddenWidth; h++) edges[cursor++] = new(at + i, old.InputCount + count + h);
        var result = new GraphModel(new(old.InputCount + count, old.NodeCount + count, old.Steps, old.Outputs.Select(n => n + count).ToArray(), edges));
        Array.Clear(result.Parameters); source.Parameters.AsSpan(0, old.Edges.Length).CopyTo(result.Parameters);
        source.Parameters.AsSpan(old.Edges.Length).CopyTo(result.Parameters.AsSpan(edges.Length)); source.Plasticity.CopyTo(result.Plasticity, 0);
        if (source.IsFrozen) result.Freeze(); return result;
    }
}
