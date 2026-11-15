using ArcadeLab.Editor;
using System.Diagnostics;
using System.Text.Json;

namespace ArcadeLab;

public sealed partial class MazeSession
{
    public MazeBakedBrain? Baked { get; }
    public BrainRuntime? BakedRuntime { get; }
    public MazeCortexRewards? CortexRewards { get; }
    public bool IsBakedBrain=>Baked is not null;
    public int GraphLayers=>IsBakedBrain?1:Config.Layers;
    public int GraphWidth=>IsBakedBrain?MainModel.NormalCount-MainModel.Topology.InputCount:Config.Width;
    private void StepBaked(MazeControls? forcedControls)
    {
        long started=Stopwatch.GetTimestamp();
        if(Game.Done||(Adaptive is not null&&(Game.Completed||Game.Tick*Game.Speed>=10800)))ResetEpisode();
        bool botControl=BotEnabled&&!forcedControls.HasValue;
        if(botControl)forcedControls=teacherBot.Controls(Game);else teacherBot.ReleaseControl();
        bool adaptiveControl=Adaptive is not null&&!forcedControls.HasValue;
        if(adaptiveControl)forcedControls=Adaptive!.Choose(Game,AdaptiveReadout(),Training);
        else Adaptive?.ResetEpisode();
        var clamps=new Dictionary<int,float>();
        if(forcedControls.HasValue)for(int i=0;i<OutputCount;i++)clamps[Baked!.Motors[i]]=(forcedControls.Value&MazeControlPolicy.Buttons[i])!=0?1:0;
        TotalPleasure=AppliedPleasure/MazeGame.MaximumPleasure+PrimitivePleasure;TotalPain=AppliedPain+PrimitivePain;
        WeightRewardFactor=TotalPleasure-TotalPain;EffectiveLearningRate=Config.Rate;
        double dt=Game.Speed/60d;
        BakedRuntime!.Step(Baked!.Inputs(Game),dt,Config.PropagationSteps,Config.ActivityStep,Config.WeightStep,Config.Rate,CortexRewards!.RewardFactorOverrides,learn:true,clamps:clamps,learningBias:Config.ThinkingBias);
        MainUpdates++;RefreshMain();RewardLoss=BakedRuntime.MeasureRewardError();
        var controls=forcedControls??MazeControlPolicy.FromActivities(scores);
        ControlOrigin=adaptiveControl?"ADAPTIVE":forcedControls.HasValue?botControl?"BOT":"MANUAL":"BAKED";
        Game.Step(controls,Training);LastControls=controls;
        TotalHits+=Game.Events.Hits;TotalKills+=Game.Events.Kills;TotalMisses+=Game.Events.MissedShots;TotalAmmo+=Game.Events.AmmoPickups;if(Game.Events.Healed>0)TotalHealing++;
        TotalExploration+=Game.NewCellsThisFrame;TotalRegions+=Game.NewRegionsThisFrame;if(Game.StationaryTeacherPain>0)StationaryFrames++;if(Game.Events.Completed)CompletedMazes++;
        PrimitivePain=Game.PrimitivePain;PrimitivePleasure=RewardSensation.Extend(PrimitivePleasure,Game.PrimitivePleasure);
        // Perceived reward is the derivative of free reward-neuron activity, never the teacher label.
        var derivatives=BakedRuntime.RewardDerivatives.Where(p=>!CortexRewards.RewardFactorOverrides.ContainsKey(p.Key)).Select(p=>p.Value).ToArray();
        Pleasure=derivatives.Sum(v=>(float)Math.Max(0,v));Pain=derivatives.Sum(v=>(float)Math.Max(0,-v));
        AppliedPleasure=Pleasure;AppliedPain=Pain;TotalPleasure=Pleasure;TotalPain=Pain;
        WeightRewardFactor=(derivatives.Length==0?0:(float)derivatives.Average())+Config.ThinkingBias;
        EffectiveLearningRate=Config.Rate*WeightRewardFactor;
        LearningReward=Pleasure-Pain;Observe();
        if(Adaptive is not null)
        {
            LearningReward=AdaptiveRewards.Observe(Game);
            CortexRewards.ObserveOutcome(LearningReward,dt,Training);
            if(adaptiveControl)Adaptive.Observe(Game,AdaptiveReadout(),LearningReward,Game.Done||Game.Completed||Game.Tick*Game.Speed>=10800,Training);
        }
        else CortexRewards.Observe(Game.Events,Game.DiedThisFrame,dt,PrimitivePain,updateTargets:Training);
        if(Training)
        {
            var targets=CortexRewards.TeachingTargets();
            var motorLabels=new Dictionary<int,float>();
            for(int i=0;i<OutputCount;i++)motorLabels[Baked.Motors[i]]=(controls&MazeControlPolicy.Buttons[i])!=0?1:0;
            if(targets.Count>0)
            {
                RewardTeachingError=BakedRuntime.TeachRewards(Baked.Inputs(Game),dt,targets,Config.PropagationSteps,Config.ActivityStep,Config.WeightStep,Config.RewardRate,motorLabels);
                RewardUpdates++;RewardTeachingTicks+=Config.PropagationSteps;
            }
        }
        if(!double.IsFinite(Loss))throw new InvalidOperationException("Nonfinite baked brain state.");
        Frames++;FrameMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    private void SaveBaked(string directory)
    {
        if(File.Exists(Path.Combine(directory,"maze.json")))throw new InvalidOperationException("Choose a separate checkpoint directory for a baked maze brain.");
        Backend.DownloadParameters();Baked!.Save(directory);
        var state=new BakedMazeState(Config,Frames,MainUpdates,CompletedMazes,TotalHits,TotalKills,TotalMisses,TotalAmmo,TotalHealing,TotalExploration,TotalRegions,StationaryFrames,Pleasure,PrimitivePleasure,AppliedPleasure,BakedRuntime!.CaptureState(),RewardUpdates,RewardTeachingTicks,CortexRewards!.Capture(),CaptureAdaptive());
        string path=Path.Combine(directory,"brain-maze.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(state));File.Move(path+".tmp",path,true);
    }
    private static MazeSession LoadBaked(string directory,bool training,string backend,int device,float? speed,int? steps,float? activityStep,float? weightStep,float? rate,float? rewardRate,float? limit,float? thinkingBias,bool? adaptiveControl)
    {
        var state=JsonSerializer.Deserialize<BakedMazeState>(File.ReadAllText(Path.Combine(directory,"brain-maze.json")))??throw new InvalidDataException("Missing baked maze state.");
        var savedBrain=BakedBrain.Load(directory);
        var session=new MazeSession(state.Config with {AdaptiveControl=adaptiveControl??state.Config.AdaptiveControl,Speed=speed??state.Config.Speed,PropagationSteps=steps??state.Config.PropagationSteps,ActivityStep=activityStep??state.Config.ActivityStep,WeightStep=weightStep??state.Config.WeightStep,Rate=rate??state.Config.Rate,RewardRate=rewardRate??state.Config.RewardRate,WeightLimit=limit??state.Config.WeightLimit,ThinkingBias=thinkingBias??state.Config.ThinkingBias},training,backend,device,bakedBrain:savedBrain);
        try
        {
            session.Frames=state.Frames;session.MainUpdates=state.MainUpdates;session.CompletedMazes=state.CompletedMazes;
            session.TotalHits=state.Hits;session.TotalKills=state.Kills;session.TotalMisses=state.Misses;session.TotalAmmo=state.Ammo;session.TotalHealing=state.Healing;
            session.TotalExploration=state.Exploration;session.TotalRegions=state.Regions;session.StationaryFrames=state.Stationary;
            session.ResetEpisode();session.BakedRuntime!.RestoreState(MazeTactileBrain.UpgradeState(savedBrain,session.Baked!.Brain,state.Runtime));session.RefreshMain();
            session.RewardUpdates=state.RewardUpdates;session.RewardTeachingTicks=state.RewardTeachingTicks;
            if(state.RewardTargets is not null)session.CortexRewards!.Restore(state.RewardTargets);
            session.Pleasure=state.Pleasure;session.PrimitivePleasure=state.PrimitivePleasure;session.AppliedPleasure=state.AppliedPleasure;
            if(state.Adaptive is not null)session.RestoreAdaptive(state.Adaptive);
            return session;
        }
        catch{session.Dispose();throw;}
    }
    private sealed record BakedMazeState(MazeConfig Config,long Frames,long MainUpdates,int CompletedMazes,long Hits,long Kills,long Misses,long Ammo,long Healing,long Exploration,long Regions,long Stationary,float Pleasure,float PrimitivePleasure,float AppliedPleasure,BrainRuntimeState Runtime,long RewardUpdates=0,long RewardTeachingTicks=0,Dictionary<string,float>? RewardTargets=null,MazeAdaptiveState? Adaptive=null);
}
