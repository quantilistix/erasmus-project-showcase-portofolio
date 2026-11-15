namespace ArcadeLab;

/// <summary>Environment-only supervision; never supplied as sensory input to either network.</summary>
public readonly record struct RewardTeachingTarget(float Pleasure, float Pain)
{
    public static float MaximumPleasure(GameKind kind) => 1;

    public static RewardTeachingTarget Catch(float horizontalDistance, float environmentReward, bool? success = null)
    {
        if (!float.IsFinite(horizontalDistance) || horizontalDistance < 0 || !float.IsFinite(environmentReward))
            throw new ArgumentException("Reward teaching values must be finite and distance nonnegative.");
        // Maximum attainable paddle/object separation is Width - 5. Stationary alignment remains rewarding.
        float alignment = Math.Clamp(1 - horizontalDistance / (ArcadeGame.Width - 5), 0, 1);
        float pain = success.HasValue ? (success.Value ? 0 : 1) : Math.Clamp(-environmentReward, 0, 1);
        return new(alignment * alignment, pain);
    }
    public static RewardTeachingTarget Signed(float environmentReward) => new(Math.Clamp(environmentReward, 0, 1), Math.Clamp(-environmentReward, 0, 1));
}
