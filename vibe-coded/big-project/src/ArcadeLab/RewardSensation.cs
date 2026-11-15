namespace ArcadeLab;

/// <summary>Peak-preserving feedback envelope on the maze's 60 Hz simulation clock.</summary>
internal static class RewardSensation
{
    internal const float HalfLifeSeconds=1;
    private static readonly float Decay=MathF.Pow(.5f,1/(60*HalfLifeSeconds));

    // New sensations take effect immediately. Repeated input sustains its level
    // rather than accumulating an unbounded reward from repeated frames.
    internal static float Extend(float previous,float current)=>MathF.Max(previous*Decay,current);
}
