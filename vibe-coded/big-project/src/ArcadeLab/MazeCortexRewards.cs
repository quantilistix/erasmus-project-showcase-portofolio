using ArcadeLab.Editor;

namespace ArcadeLab;

public enum CortexRewardKind { None, Motor, Central, Spatial, Audio, Video, RewardSystem }
public readonly record struct CortexRewardSignal(float Reward,float Punishment)
{
    public float Net=>Reward-Punishment;
}

/// <summary>Event labels train separate reward predictors; labels never gate cortical weights directly.</summary>
public sealed class MazeCortexRewards
{
    public const float Hit=.05f,Miss=.03f,Kill=.25f,Completion=8,Death=4,Neutral=4,TraceHalfLife=1;
    public IReadOnlyDictionary<string,CortexRewardSignal> Signals=>signals;
    public IReadOnlyDictionary<string,float> Targets=>targets;
    public IReadOnlyDictionary<string,float> RewardFactorOverrides=>factorOverrides;
    private readonly Dictionary<string,CortexRewardSignal> signals=[];
    private readonly Dictionary<string,float> targets=[],factorOverrides=[];
    private readonly Dictionary<string,CortexRewardKind> kinds=[];
    private readonly Dictionary<string,int> rewardIndices=[];
    public MazeCortexRewards(BakedBrain brain)
    {
        foreach(var group in brain.Manifest.RewardGroups)
        {
            string text=(group.Name+" "+string.Join(" ",brain.Manifest.Modules.Where(m=>group.ModuleIds.Contains(m.Id)).Select(m=>m.Name))).ToLowerInvariant();
            var kind=text.Contains("audio")?CortexRewardKind.Audio:text.Contains("video")||text.Contains("visual")?CortexRewardKind.Video
                :text.Contains("motor")?CortexRewardKind.Motor:text.Contains("spatial")?CortexRewardKind.Spatial:text.Contains("central")?CortexRewardKind.Central
                :text.Contains("reward")?CortexRewardKind.RewardSystem:CortexRewardKind.None;
            kinds[group.Name]=kind;targets[group.Name]=Neutral;
            rewardIndices[group.Name]=brain.Manifest.Neurons.Select((n,i)=>(n,i)).Where(x=>x.n.ModuleId==group.RewardModuleId).ElementAt(group.RewardNeuron).i;
            if(kind is CortexRewardKind.Audio or CortexRewardKind.Video)factorOverrides[group.Name]=0;
        }
    }
    public static CortexRewardSignal Motor(MazeEvents e)=>new(e.VisibleShotHits*Hit,e.MissedShots*Miss);
    public static CortexRewardSignal Central(MazeEvents e,bool died,float primitivePain=0)
    {
        if(!float.IsFinite(primitivePain)||primitivePain<0)throw new ArgumentOutOfRangeException(nameof(primitivePain));
        return new(e.Kills*Kill+(e.Completed?Completion:0),(died?Death:0)+e.UnseenShotMisses*Miss+primitivePain);
    }
    public void Observe(MazeEvents events,bool died,double dt,float primitivePain=0,bool updateTargets=true)
    {
        var motor=Motor(events);var central=Central(events,died,primitivePain);
        // Spatial sums the motor and central labels; audio/video have no assigned reward.
        var spatial=new CortexRewardSignal(motor.Reward+central.Reward,motor.Punishment+central.Punishment);
        foreach(var (name,kind) in kinds)
        {
            var signal=kind switch
            {
                CortexRewardKind.Motor=>motor,CortexRewardKind.Central=>central,CortexRewardKind.Spatial or CortexRewardKind.RewardSystem=>spatial,
                _=>default
            };
            signals[name]=signal;
            // Observer readouts remain live in play, without changing teaching traces.
            if(!updateTargets)continue;
            float decay=(float)Math.Pow(.5,dt/TraceHalfLife);
            targets[name]=kind is CortexRewardKind.Audio or CortexRewardKind.Video?Neutral:Math.Clamp(Neutral+(targets[name]-Neutral)*decay+signal.Net,0,8);
        }
    }
    public void ObserveOutcome(float outcome,double dt,bool training)
    {
        if(!float.IsFinite(outcome)||!double.IsFinite(dt)||dt<=0)throw new ArgumentOutOfRangeException(nameof(outcome));
        foreach(var (name,kind) in kinds)
        {
            bool active=kind is not (CortexRewardKind.None or CortexRewardKind.Audio or CortexRewardKind.Video);
            signals[name]=active?new(Math.Max(0,outcome),Math.Max(0,-outcome)):default;
            if(training)targets[name]=active?Math.Clamp(Neutral+(targets[name]-Neutral)*(float)Math.Pow(.5,dt/TraceHalfLife)+outcome,0,8):Neutral;
        }
    }
    public Dictionary<int,float> TeachingTargets()=>kinds.Where(k=>k.Value is not (CortexRewardKind.None or CortexRewardKind.Audio or CortexRewardKind.Video)).ToDictionary(k=>rewardIndices[k.Key],k=>targets[k.Key]);
    public Dictionary<string,float> Capture()=>new(targets);
    public void Restore(IReadOnlyDictionary<string,float> saved)
    {
        foreach(var (name,value) in saved)
            if(targets.ContainsKey(name)&&float.IsFinite(value)&&value>=0&&value<=8)targets[name]=factorOverrides.ContainsKey(name)?Neutral:value;
            else throw new InvalidDataException("Invalid per-cortex reward trace.");
    }
}
