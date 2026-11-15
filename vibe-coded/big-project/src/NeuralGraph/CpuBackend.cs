// Legacy GraphModel backend. The active paired-error CPU backend is in PredictiveCoding.cs.
namespace NeuralGraph;

/// <summary>Every neuron stores nonnegative positive/negative rates. Signed values exist only in readout adapters, potentials and gradients.</summary>
public sealed partial class CpuBackend : IGraphBackend
{
    private readonly GraphModel model;
    private readonly int capacity, stride;
    private readonly LayerSchedule? schedule;
    private readonly float[] activations, deltas, gradients, inputRates, outputRates, targetRates;
    private int lastBatch;
    private float gain = 1;
    public float Gain
    {
        get => gain;
        set { if (!float.IsFinite(value) || value < 0 || value > 4) throw new ArgumentOutOfRangeException(nameof(value)); gain = value; }
    }
    public string DeviceName => schedule is null ? "CPU nonnegative reference" : "CPU layered nonnegative reference";
    public CpuBackend(GraphModel model, int capacity = 256, bool layered = false)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        model.ValidateValues(); this.model = model; this.capacity = capacity;
        if (layered) schedule = new(model.Topology);
        stride = checked((layered ? 1 : model.Topology.Steps + 1) * model.Topology.NodeCount);
        int size = checked(capacity * stride);
        activations = new float[checked(size * 2)]; deltas = new float[size]; gradients = new float[model.Parameters.Length];
        stateA = new float[model.Topology.ActivationCount]; stateB = new float[stateA.Length];
        inputRates = new float[checked(capacity * model.Topology.EncodedInputCount)];
        outputRates = new float[checked(capacity * model.Topology.EncodedOutputCount)]; targetRates = new float[outputRates.Length];
    }
    public void ReadActivations(Span<float> destination, int sampleIndex = 0)
    {
        var t = model.Topology;
        if (destination.Length != t.ActivationCount || sampleIndex < 0 || sampleIndex >= lastBatch) throw new ArgumentException("Expected two nonnegative activation channels per logical node.");
        activations.AsSpan(2 * (sampleIndex * stride + (schedule is null ? t.Steps * t.NodeCount : 0)), t.ActivationCount).CopyTo(destination);
    }
    public void ReadValues(Span<float> destination, int sampleIndex = 0)
    {
        var t = model.Topology;
        if (destination.Length != t.NodeCount || sampleIndex < 0 || sampleIndex >= lastBatch) throw new ArgumentException("Logical readout shape.");
        RateEncoding.Decode(activations.AsSpan(2 * (sampleIndex * stride + (schedule is null ? t.Steps * t.NodeCount : 0)), t.ActivationCount), destination);
    }
    public void Predict(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize)
    {
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length, outputs.Length);
        var encodedInput = inputRates.AsSpan(0, checked(inputs.Length * 2)); var encodedOutput = outputRates.AsSpan(0, checked(outputs.Length * 2));
        RateEncoding.Encode(inputs, encodedInput); PredictEncoded(encodedInput, encodedOutput, batchSize); RateEncoding.Decode(encodedOutput, outputs);
    }
    public void PredictEncoded(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize)
    {
        var t = model.Topology;
        if ((inputs.Length & 1) != 0 || (outputs.Length & 1) != 0) throw new ArgumentException("Rate channels must come in pairs.");
        BatchValidation.Check(t, capacity, batchSize, inputs.Length / 2, outputs.Length / 2); RateEncoding.Validate(inputs);
        int n = t.NodeCount;
        for (int b = 0; b < batchSize; b++)
        {
            int start = b * stride; Array.Clear(activations, 2 * start, 2 * stride);
            if (schedule is not null)
            {
                inputs.Slice(b * t.EncodedInputCount, t.EncodedInputCount).CopyTo(activations.AsSpan(2 * start));
                foreach (int node in schedule.Nodes) ForwardNode(node, start, start);
            }
            else
            {
                for (int step = 0; step <= t.Steps; step++) inputs.Slice(b * t.EncodedInputCount, t.EncodedInputCount).CopyTo(activations.AsSpan(2 * (start + step * n)));
                for (int step = 1; step <= t.Steps; step++)
                    for (int node = t.InputCount; node < n; node++) ForwardNode(node, start + step * n, start + (step - 1) * n);
            }
            int final = start + (schedule is null ? t.Steps * n : 0);
            for (int o = 0; o < t.Outputs.Length; o++) activations.AsSpan(2 * (final + t.Outputs[o]), 2).CopyTo(outputs.Slice(2 * (b * t.Outputs.Length + o), 2));
        }
        lastBatch = batchSize;
    }
    private void ForwardNode(int node, int pos, int previous)
    {
        var t = model.Topology; float potential = model.Parameters[t.Edges.Length + node - t.InputCount];
        for (int k = t.IncomingOffsets[node]; k < t.IncomingOffsets[node + 1]; k++)
        {
            int e = t.IncomingEdges[k]; potential += gain * model.Parameters[e] * RateEncoding.Value(activations, previous + t.Edges[e].Source);
        }
        RateEncoding.Activate(activations, pos + node, potential, t.IsOutput[node]);
    }
    public float Train(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate)
    {
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length, targets.Length); BatchValidation.Training(targets, weights, learningRate);
        var encodedInput = inputRates.AsSpan(0, checked(inputs.Length * 2)); var encodedTarget = targetRates.AsSpan(0, checked(targets.Length * 2));
        RateEncoding.Encode(inputs, encodedInput); RateEncoding.Encode(targets, encodedTarget); return TrainEncoded(encodedInput, encodedTarget, weights, batchSize, learningRate);
    }
    public float TrainEncoded(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate)
    {
        var t = model.Topology;
        if ((inputs.Length & 1) != 0 || (targets.Length & 1) != 0) throw new ArgumentException("Rate channels must come in pairs.");
        BatchValidation.Check(t, capacity, batchSize, inputs.Length / 2, targets.Length / 2);
        if (weights.Length != targets.Length / 2) throw new ArgumentException("One loss weight is required per logical output.");
        if (!float.IsFinite(learningRate) || learningRate <= 0) throw new ArgumentException("Invalid training rate.");
        RateEncoding.Validate(weights); RateEncoding.Validate(targets);
        PredictEncoded(inputs, outputRates.AsSpan(0, targets.Length), batchSize);
        Array.Clear(deltas); Array.Clear(gradients); float loss = 0; int n = t.NodeCount;
        for (int b = 0; b < batchSize; b++)
        {
            int start = b * stride, final = start + (schedule is null ? t.Steps * n : 0);
            for (int o = 0; o < t.Outputs.Length; o++)
            {
                int j = b * t.Outputs.Length + o, at = final + t.Outputs[o];
                float error = RateEncoding.Value(activations, at) - (targets[2 * j] - targets[2 * j + 1]);
                loss += .5f * weights[j] * error * error; deltas[at] = weights[j] * error;
            }
            if (model.IsFrozen) continue;
            if (schedule is not null)
            {
                for (int k = schedule.Nodes.Length - 1; k >= 0; k--) BackwardNode(schedule.Nodes[k], start, start);
            }
            else for (int step = t.Steps; step >= 1; step--)
                for (int node = t.InputCount; node < n; node++) BackwardNode(node, start + step * n, start + (step - 1) * n);
        }
        if (!model.IsFrozen)
            for (int p = 0; p < gradients.Length; p++) model.Parameters[p] -= learningRate * Math.Clamp(gradients[p] / batchSize, -1f, 1f) * (p < t.Edges.Length ? model.Plasticity[p] : 1f);
        return loss / batchSize;
    }
    private void BackwardNode(int node, int pos, int previous)
    {
        var t = model.Topology; float d = deltas[pos + node], value = RateEncoding.Value(activations, pos + node);
        if (!t.IsOutput[node]) d *= 1 - value * value;
        deltas[pos + node] = d; gradients[t.Edges.Length + node - t.InputCount] += d;
        for (int k = t.IncomingOffsets[node]; k < t.IncomingOffsets[node + 1]; k++)
        {
            int e = t.IncomingEdges[k], source = previous + t.Edges[e].Source;
            gradients[e] += gain * d * RateEncoding.Value(activations, source); deltas[source] += gain * d * model.Parameters[e];
        }
    }
    public void UploadParameters() => model.ValidateValues();
    public void DownloadParameters() { }
    public void Dispose() { }
}
