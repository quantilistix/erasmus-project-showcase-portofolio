namespace NeuralGraph;

/// <summary>Signed application values are carried by two nonnegative neuron channels: positive, then negative.</summary>
public static class RateEncoding
{
    public static void Encode(ReadOnlySpan<float> values, Span<float> channels)
    {
        if (channels.Length != checked(values.Length * 2)) throw new ArgumentException("Rate channel shape.");
        for (int i = 0; i < values.Length; i++)
        {
            float value = values[i]; if (!float.IsFinite(value)) throw new ArgumentException("Encoded values must be finite.");
            channels[2 * i] = MathF.Max(value, 0); channels[2 * i + 1] = MathF.Max(-value, 0);
        }
    }
    public static void Decode(ReadOnlySpan<float> channels, Span<float> values)
    {
        if (channels.Length != checked(values.Length * 2)) throw new ArgumentException("Rate channel shape.");
        Validate(channels);
        for (int i = 0; i < values.Length; i++) values[i] = channels[2 * i] - channels[2 * i + 1];
    }
    public static void Validate(ReadOnlySpan<float> channels)
    {
        foreach (float value in channels) if (!float.IsFinite(value) || value < 0) throw new ArgumentException("Neuron activation channels must be finite and nonnegative.");
    }
    internal static float Value(float[] channels, int logicalIndex) => channels[logicalIndex * 2] - channels[logicalIndex * 2 + 1];
    internal static void Activate(float[] channels, int logicalIndex, float potential, bool output)
    {
        channels[2 * logicalIndex] = output ? MathF.Max(potential, 0) : MathF.Max(MathF.Tanh(potential), 0);
        channels[2 * logicalIndex + 1] = output ? MathF.Max(-potential, 0) : MathF.Max(MathF.Tanh(-potential), 0);
    }
}
