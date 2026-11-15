namespace ArcadeLab;

/// <summary>Noise is restricted to two dedicated pain banks. Raw senses and controls are never perturbed.</summary>
public static class RealtimeInputs
{
    public static void Compose(ReadOnlySpan<float> observation, int previousAction, Span<float> destination,
        int learnedPainCount, int primitivePainCount, float learnedPain, float primitivePain, Random random, float clock = .5f, HeldPainInputs? heldPain = null, long frame = 0, float primitiveReward = 0, int primitiveRewardCount = 0)
    {
        if (observation.Length != Sensors.ObservationSize || previousAction is < 0 or > 2 || learnedPainCount < 1 || primitivePainCount < 1 || primitiveRewardCount < 0 ||
            destination.Length != Sensors.InputSize + learnedPainCount + primitivePainCount + ClockInput.Count + primitiveRewardCount)
            throw new ArgumentException("Invalid realtime sensory/control shape.");
        if (!float.IsFinite(learnedPain) || !float.IsFinite(primitivePain)) throw new ArgumentException("Pain levels must be finite.");
        observation.CopyTo(destination); destination[Sensors.ObservationSize..].Clear(); destination[Sensors.ObservationSize + previousAction] = 1;
        float learnedProbability = Math.Clamp(learnedPain, 0, 1), primitiveProbability = Math.Clamp(primitivePain, 0, 1);
        if (!float.IsFinite(clock) || clock < 0 || clock > 1) throw new ArgumentException("Clock must be in [0,1].");
        if (heldPain is not null) heldPain.Write(destination.Slice(Sensors.InputSize, learnedPainCount + primitivePainCount), frame, learnedProbability, primitiveProbability, random);
        else
        {
            for (int i = 0; i < learnedPainCount; i++) destination[Sensors.InputSize + i] = random.NextDouble() < learnedProbability ? 1 : 0;
            for (int i = 0; i < primitivePainCount; i++) destination[Sensors.InputSize + learnedPainCount + i] = random.NextDouble() < primitiveProbability ? 1 : 0;
        }
        if (!float.IsFinite(primitiveReward) || primitiveReward < 0 || primitiveReward > 1) throw new ArgumentException("Primitive reward must be in [0,1].");
        int clockIndex = Sensors.InputSize + learnedPainCount + primitivePainCount;
        destination[clockIndex] = clock;
        destination.Slice(clockIndex + ClockInput.Count, primitiveRewardCount).Fill(primitiveReward);
    }
    public static float PrimitiveLevel(float previous, float environmentReward) => MathF.Max(previous * .85f, environmentReward <= -.5f ? Math.Clamp(-environmentReward, 0, 1) : 0);
}
