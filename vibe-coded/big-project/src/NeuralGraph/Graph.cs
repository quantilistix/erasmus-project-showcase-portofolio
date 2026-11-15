using System.Text.Json;

namespace NeuralGraph;

public readonly record struct Edge(int Source, int Destination);

/// <summary>Inputs occupy [0, InputCount). Every edge reads the preceding synchronous step.
/// Cycles, self connections, skips and disconnected nodes are legal. Input nodes are clamped.</summary>
public sealed class GraphTopology
{
    public int InputCount { get; }
    public int NodeCount { get; }
    public int Steps { get; }
    public int[] Outputs { get; }
    public Edge[] Edges { get; }
    /// <summary>Legacy encoded graph activation count. PredictiveCodingModel exposes its own normal/error population sizes.</summary>
    public int ActivationCount => checked(NodeCount * 2);
    public int EncodedInputCount => checked(InputCount * 2);
    public int EncodedOutputCount => checked(Outputs.Length * 2);
    public int ParameterCount => Edges.Length + NodeCount - InputCount;
    internal int[] IncomingOffsets { get; }
    internal int[] IncomingEdges { get; }
    internal int[] OutgoingOffsets { get; }
    internal int[] OutgoingEdges { get; }
    internal bool[] IsOutput { get; }

    public GraphTopology(int inputCount, int nodeCount, int steps, int[] outputs, Edge[] edges)
    {
        if (inputCount < 1 || nodeCount <= inputCount || steps < 1 || steps > 256)
            throw new ArgumentException("Require inputs > 0, nodes > inputs and steps in [1,256].");
        if (outputs.Length == 0 || outputs.Distinct().Count() != outputs.Length || outputs.Any(n => n < inputCount || n >= nodeCount))
            throw new ArgumentException("Outputs must be unique non-input nodes.");
        if (edges.Any(e => e.Source < 0 || e.Source >= nodeCount || e.Destination < inputCount || e.Destination >= nodeCount))
            throw new ArgumentException("Edges must connect existing nodes and cannot target clamped inputs.");
        InputCount = inputCount; NodeCount = nodeCount; Steps = steps;
        Outputs = (int[])outputs.Clone(); Edges = (Edge[])edges.Clone();
        IsOutput = new bool[nodeCount]; foreach (int n in Outputs) IsOutput[n] = true;
        (IncomingOffsets, IncomingEdges) = Index(true); (OutgoingOffsets, OutgoingEdges) = Index(false);
    }

    private (int[], int[]) Index(bool incoming)
    {
        var offsets = new int[NodeCount + 1];
        foreach (var e in Edges) offsets[(incoming ? e.Destination : e.Source) + 1]++;
        for (int i = 1; i <= NodeCount; i++) offsets[i] += offsets[i - 1];
        var cursor = (int[])offsets.Clone(); var indices = new int[Edges.Length];
        for (int i = 0; i < Edges.Length; i++) indices[cursor[incoming ? Edges[i].Destination : Edges[i].Source]++] = i;
        return (offsets, indices);
    }

    /// <summary>Input is layer zero; hidden layers start at one. Odd layers also connect to layer + 2.</summary>
    public static GraphTopology Layered(int inputs, int[] hiddenSizes, int outputs, bool oddSkips = true)
    {
        if (inputs < 1 || outputs < 1 || hiddenSizes.Length == 0 || hiddenSizes.Any(size => size < 1))
            throw new ArgumentException("Layer sizes must be positive and at least one hidden layer is required.");
        var sizes = new[] { inputs }.Concat(hiddenSizes).Append(outputs).ToArray();
        var starts = new int[sizes.Length];
        for (int layer = 1; layer < sizes.Length; layer++) starts[layer] = checked(starts[layer - 1] + sizes[layer - 1]);
        long count = 0;
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            count += (long)sizes[layer] * sizes[layer + 1];
            if (oddSkips && layer % 2 == 1 && layer + 2 < sizes.Length) count += (long)sizes[layer] * sizes[layer + 2];
        }
        if (count > int.MaxValue) throw new ArgumentException("Too many connections.");
        var edges = new Edge[(int)count]; int cursor = 0;
        void Connect(int sourceLayer, int destinationLayer)
        {
            for (int destination = 0; destination < sizes[destinationLayer]; destination++)
                for (int source = 0; source < sizes[sourceLayer]; source++)
                    edges[cursor++] = new(starts[sourceLayer] + source, starts[destinationLayer] + destination);
        }
        for (int layer = 0; layer < sizes.Length - 1; layer++)
        {
            Connect(layer, layer + 1);
            if (oddSkips && layer % 2 == 1 && layer + 2 < sizes.Length) Connect(layer, layer + 2);
        }
        return new(inputs, checked(starts[^1] + outputs), sizes.Length - 1,
            Enumerable.Range(starts[^1], outputs).ToArray(), edges);
    }

    public static GraphTopology Dense(int inputs, int hidden, int outputs, bool recurrent = false)
    {
        if (hidden < 1 || outputs < 1) throw new ArgumentOutOfRangeException(nameof(hidden));
        var edges = new List<Edge>();
        for (int h = inputs; h < inputs + hidden; h++)
        {
            for (int i = 0; i < inputs; i++) edges.Add(new(i, h));
            if (recurrent) edges.Add(new(h, h));
        }
        var outputNodes = Enumerable.Range(inputs + hidden, outputs).ToArray();
        foreach (int o in outputNodes)
        {
            for (int h = inputs; h < inputs + hidden; h++) edges.Add(new(h, o));
            for (int i = 0; i < inputs; i++) edges.Add(new(i, o));
        }
        return new(inputs, inputs + hidden + outputs, recurrent ? 4 : 2, outputNodes, edges.ToArray());
    }
}

public sealed class GraphModel
{
    public GraphTopology Topology { get; }
    public float[] Parameters { get; }
    /// <summary>Fixed per-edge multipliers; never optimized. Zero freezes an edge; defaults to one.</summary>
    public float[] Plasticity { get; }
    public bool IsFrozen { get; private set; }
    /// <summary>Freeze every parameter, including biases; set every edge's fixed plasticity to zero.</summary>
    public void Freeze() { Array.Clear(Plasticity); IsFrozen = true; }
    /// <summary>Explicitly permit training again, initializing fixed edge multipliers; never called by an optimizer.</summary>
    public void Unfreeze(float plasticity = 1)
    {
        if (!float.IsFinite(plasticity) || plasticity < 0) throw new ArgumentOutOfRangeException(nameof(plasticity));
        Array.Fill(Plasticity, plasticity); IsFrozen = false;
    }
    public GraphModel(GraphTopology topology, int seed = 1)
    {
        Topology = topology; Parameters = new float[topology.ParameterCount];
        Plasticity = Enumerable.Repeat(1f, topology.Edges.Length).ToArray();
        var rng = new Random(seed);
        for (int e = 0; e < topology.Edges.Length; e++)
        {
            int n = topology.Edges[e].Destination;
            int fanIn = topology.IncomingOffsets[n + 1] - topology.IncomingOffsets[n];
            Parameters[e] = (float)(rng.NextDouble() * 2 - 1) * MathF.Sqrt(3f / Math.Max(1, fanIn));
        }
    }
    public GraphModel Clone()
    {
        var model = new GraphModel(Topology); Parameters.CopyTo(model.Parameters, 0); Plasticity.CopyTo(model.Plasticity, 0); if (IsFrozen) model.Freeze(); return model;
    }
    internal void ValidateValues()
    {
        if (Parameters.Any(x => !float.IsFinite(x))) throw new ArgumentException("Parameters must be finite.");
        if (Plasticity.Any(x => !float.IsFinite(x) || x < 0)) throw new ArgumentException("Plasticity must be finite and nonnegative.");
    }
    public void Save(string path)
    {
        if (path.EndsWith(".ngb", StringComparison.OrdinalIgnoreCase)) { GraphBinary.Save(this, path); return; }
        ValidateValues();
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Snapshot(4, Topology.InputCount,
            Topology.NodeCount, Topology.Steps, Topology.Outputs, Topology.Edges, Parameters, Plasticity, IsFrozen)));
        File.Move(temporary, full, true);
    }
    public static GraphModel Load(string path)
    {
        if (path.EndsWith(".ngb", StringComparison.OrdinalIgnoreCase)) return GraphBinary.Load(path);
        var data = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty checkpoint.");
        if (data.Version is not (1 or 2 or 3 or 4)) throw new InvalidDataException("Unsupported checkpoint version.");
        if (data.Version >= 4 && data.ActivationEncoding != "nonnegative-dual-rail") throw new InvalidDataException("Unsupported neuron activation encoding.");
        var model = new GraphModel(new(data.InputCount, data.NodeCount, data.Steps, data.Outputs, data.Edges));
        if (data.Parameters.Length != model.Parameters.Length || data.Parameters.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid parameters.");
        data.Parameters.CopyTo(model.Parameters, 0);
        if (data.Plasticity is not null)
        {
            if (data.Plasticity.Length != model.Plasticity.Length || data.Plasticity.Any(x => !float.IsFinite(x) || x < 0))
                throw new InvalidDataException("Invalid plasticity values.");
            data.Plasticity.CopyTo(model.Plasticity, 0);
        }
        else if (data.Version >= 2) throw new InvalidDataException("Missing plasticity values.");
        if (data.IsFrozen) model.Freeze();
        return model;
    }
    private sealed record Snapshot(int Version, int InputCount, int NodeCount, int Steps, int[] Outputs, Edge[] Edges, float[] Parameters, float[]? Plasticity = null, bool IsFrozen = false, string ActivationEncoding = "nonnegative-dual-rail");
}

/// <summary>Single-owner synchronous backend with persistent single-stream propagation and stateless minibatch learning. Arrays are sample-major. Training minimizes
/// mean(sample) sum(output) 0.5 * weight * (prediction-target)^2 using clipped SGD.</summary>
public interface IGraphBackend : IDisposable
{
    string DeviceName { get; }
    /// <summary>Transient effective edge-weight multiplier; does not modify stored weights or plasticity.</summary>
    float Gain { get; set; }
    /// <summary>Read the last evaluated node activations. Training leaves pre-update activations; Predict refreshes them.</summary>
    void ReadActivations(Span<float> destination, int sampleIndex = 0);
    /// <summary>Decode logical values for diagnostics/readout adapters; never used as neuron activation storage.</summary>
    void ReadValues(Span<float> destination, int sampleIndex = 0);
    /// <summary>Application-value adapter: encodes inputs and decodes output scores; these values are not neuron activations.</summary>
    void Predict(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize);
    /// <summary>Native neuron interface: every input/output channel must be nonnegative; adjacent channels carry opposite signs.</summary>
    void PredictEncoded(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize);
    /// <summary>Advance a persistent single-stream activation state synchronously by one or more connection hops.</summary>
    void AdvanceEncoded(ReadOnlySpan<float> inputs, Span<float> outputs, int steps = 1);
    /// <summary>Decoded application-value adapter for persistent propagation.</summary>
    void Advance(ReadOnlySpan<float> inputs, Span<float> outputs, int steps = 1);
    long PropagationSteps { get; }
    void ReadStateActivations(Span<float> destination);
    void RestoreStateActivations(ReadOnlySpan<float> state, long propagationSteps = 0);
    /// <summary>Explicitly clear live state; stateless prediction and training never call this.</summary>
    void ResetState();
    float TrainEncoded(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate);
    float Train(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate);
    void UploadParameters();
    void DownloadParameters();
}

internal static class BatchValidation
{
    public static void Check(GraphTopology t, int capacity, int batch, int inputs, int outputs)
    {
        if (batch < 1 || batch > capacity || inputs != batch * t.InputCount || outputs != batch * t.Outputs.Length)
            throw new ArgumentException("Invalid batch size or sample-major tensor dimensions.");
    }
    public static void Training(ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, float rate)
    {
        if (weights.Length != targets.Length || !float.IsFinite(rate) || rate <= 0) throw new ArgumentException("Invalid training arguments.");
        foreach (float x in targets) if (!float.IsFinite(x)) throw new ArgumentException("Targets must be finite.");
        foreach (float x in weights) if (!float.IsFinite(x) || x < 0) throw new ArgumentException("Weights must be finite and nonnegative.");
    }
}
