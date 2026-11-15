namespace ArcadeLab;

/// <summary>Outcome feedback only. No map, path-to-exit or enemy state is exposed to the policy.</summary>
public sealed class MazeAdaptiveRewards
{
    public float ExplorationEarned { get; private set; }
    public float EpisodeReturn { get; private set; }
    public float LastReward { get; private set; }
    public void Reset() { ExplorationEarned=EpisodeReturn=LastReward=0; }
    public float Observe(MazeGame game)
    {
        float dt=game.Speed/60f;
        // Finite per-episode budget: wandering cannot outpay the exit, revisits pay nothing.
        float exploration=Math.Min(2-ExplorationEarned,.04f*game.NewCellsThisFrame+.12f*game.NewRegionsThisFrame);
        ExplorationEarned+=exploration;
        var e=game.Events;
        LastReward=exploration+(e.Completed?8:0)-(game.DiedThisFrame?4:0)
            +.05f*e.Hits+.25f*e.Kills+.08f*e.AmmoPickups+.005f*e.Healed
            -.04f*e.Damage-.03f*e.MissedShots
            -dt*(.01f+.1f*game.StationaryTeacherPain)
            -.1f*game.WallCollisionPain/60;
        EpisodeReturn+=LastReward;
        return LastReward;
    }
}
