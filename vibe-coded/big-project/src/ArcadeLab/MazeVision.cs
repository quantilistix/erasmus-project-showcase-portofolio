using NeuralGraph;

namespace ArcadeLab;

public static class MazeVision
{
    public const int Channels = 3;
    public const int FrameSize = MazeGame.PixelCount * Channels;
    public const int HistorySize = FrameSize * 2;
    public const int LegacyHistorySize = MazeGame.PixelCount * 2;
    public const int ExtraInputs = HistorySize - LegacyHistorySize;
    public static float Luminance(ReadOnlySpan<float> rgb) => .2126f * rgb[0] + .7152f * rgb[1] + .0722f * rgb[2];
    public static void CoarseFrame(ReadOnlySpan<float> observation, Span<float> output)
    {
        if (observation.Length < FrameSize || output.Length != 12) throw new ArgumentException("RGB prediction shape.");
        output.Clear();
        for(int ray=0;ray<MazeGame.VisionRayCount;ray++)
            output[ray*12/MazeGame.VisionRayCount] += Luminance(observation.Slice(3*ray,3))/(MazeGame.VisionRayCount/12);

    }
    /// <summary>Map old visual slots to red; append zero-weight green/blue edges to each old visual destination.
    /// Old weights, biases and plasticities survive exactly; replicated grayscale RGB retains the old function.</summary>
    public static GraphModel ExpandGrayscale(GraphModel source)
    {
        var old=source.Topology;
        if (old.InputCount < LegacyHistorySize) throw new ArgumentException("Not a grayscale maze model.");
        int visualEdges=old.Edges.Count(e=>e.Source<LegacyHistorySize);
        var edges=new Edge[checked(old.Edges.Length+2*visualEdges)];
        for (int i=0;i<old.Edges.Length;i++)
        {
            var edge=old.Edges[i]; edges[i]=new(edge.Source<LegacyHistorySize ? 3*edge.Source : edge.Source+ExtraInputs,edge.Destination+ExtraInputs);
        }
        int cursor=old.Edges.Length;
        foreach(var edge in old.Edges) if(edge.Source<LegacyHistorySize)
            for(int channel=1;channel<Channels;channel++) edges[cursor++]=new(3*edge.Source+channel,edge.Destination+ExtraInputs);
        var model=new GraphModel(new(old.InputCount+ExtraInputs,old.NodeCount+ExtraInputs,old.Steps,old.Outputs.Select(n=>n+ExtraInputs).ToArray(),edges));
        Array.Clear(model.Parameters);
        source.Parameters.AsSpan(0,old.Edges.Length).CopyTo(model.Parameters);
        source.Parameters.AsSpan(old.Edges.Length).CopyTo(model.Parameters.AsSpan(edges.Length));
        source.Plasticity.CopyTo(model.Plasticity,0); cursor=old.Edges.Length;
        for(int i=0;i<old.Edges.Length;i++) if(old.Edges[i].Source<LegacyHistorySize)
            for(int channel=1;channel<Channels;channel++) model.Plasticity[cursor++]=source.Plasticity[i];
        if(source.IsFrozen)model.Freeze(); return model;
    }
}
