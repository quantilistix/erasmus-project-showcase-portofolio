namespace ArcadeLab;

/// <summary>Sample-and-hold pain banks. Noise changes only at a slower frame interval or on pain onset.</summary>
public sealed class HeldPainInputs
{
    private readonly float[] learned, primitive;
    private int holdFrames;
    private long nextLearned, nextPrimitive;
    private bool learnedActive, primitiveActive;
    public HeldPainInputs(int learnedCount, int primitiveCount, int holdFrames)
    {
        if (learnedCount < 1 || primitiveCount < 1 || holdFrames < 1) throw new ArgumentOutOfRangeException(nameof(holdFrames));
        learned = new float[learnedCount]; primitive = new float[primitiveCount]; this.holdFrames = holdFrames;
    }
    public void ForcePrimitiveRefresh()=>nextPrimitive=0;
    public void SetHoldFrames(int value)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
        if (holdFrames == value) return;
        holdFrames = value; nextLearned = nextPrimitive = 0;
    }
    public void Write(Span<float> destination, long frame, float learnedPain, float primitivePain, Random random)
    {
        if (destination.Length != learned.Length + primitive.Length || frame < 0 || !float.IsFinite(learnedPain) || !float.IsFinite(primitivePain))
            throw new ArgumentException("Invalid held pain shape/frame/level.");
        Sample(learned, destination[..learned.Length], frame, learnedPain, ref nextLearned, ref learnedActive, random, false);
        Sample(primitive, destination[learned.Length..], frame, primitivePain, ref nextPrimitive, ref primitiveActive, random, true);
    }
    private void Sample(float[] samples, Span<float> destination, long frame, float level, ref long next, ref bool active, Random random, bool immediateOnset)
    {
        float p = Math.Clamp(level, 0, 1);
        if (p <= 0) { destination.Clear(); active = false; return; }
        if (frame >= next || (immediateOnset && !active))
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = random.NextDouble() < p ? 1 : 0;
            next = checked(frame + holdFrames);
        }
        samples.CopyTo(destination); active = true;
    }
    public HeldPainState Capture() => new((float[])learned.Clone(), (float[])primitive.Clone(), nextLearned, nextPrimitive, learnedActive, primitiveActive);
    public void Restore(HeldPainState state)
    {
        if (state.Learned?.Length != learned.Length || state.Primitive?.Length != primitive.Length || state.NextLearned < 0 || state.NextPrimitive < 0 ||
            state.Learned.Any(v => v is not (0 or 1)) || state.Primitive.Any(v => v is not (0 or 1))) throw new InvalidDataException("Invalid held pain samples.");
        state.Learned.CopyTo(learned, 0); state.Primitive.CopyTo(primitive, 0);
        nextLearned = state.NextLearned; nextPrimitive = state.NextPrimitive; learnedActive = state.LearnedActive; primitiveActive = state.PrimitiveActive;
    }
}
public sealed record HeldPainState(float[] Learned, float[] Primitive, long NextLearned, long NextPrimitive, bool LearnedActive, bool PrimitiveActive);
