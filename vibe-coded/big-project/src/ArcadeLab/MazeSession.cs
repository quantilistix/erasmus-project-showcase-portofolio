using NeuralGraph;
using ArcadeLab.Editor;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace ArcadeLab;

public sealed record MazeConfig
{
    public bool AdaptiveControl { get; init; }
    public int Layers { get; init; }=8;
    public int Width { get; init; }=512;
    public int Seed { get; init; }=42;
    public int PropagationSteps { get; init; }=8;
    public float ActivityStep { get; init; }=.04f;
    public float WeightStep { get; init; }=.02f;
    public float WeightLimit { get; init; }=8f;
    public float Rate { get; init; }=.00001f;
    public float ThinkingBias { get; init; }=.1f;
    public float RewardRate { get; init; }=.00001f;
    public float Speed { get; init; }=.5f;
    public int RewardHidden { get; init; }=128;
    public void Validate()
    {
        if(!float.IsFinite(WeightLimit)||WeightLimit<=0)throw new ArgumentException("Weight limit must be finite and positive.");
        if(!float.IsFinite(ThinkingBias)||ThinkingBias<0)throw new ArgumentException("Thinking bias must be finite and nonnegative.");
        if(Layers<1||Layers>32||Width<16||Width>4096||RewardHidden<4||RewardHidden>2048||PropagationSteps<1||PropagationSteps>256||!float.IsFinite(ActivityStep)||ActivityStep<=0||ActivityStep>.1f||!float.IsFinite(WeightStep)||WeightStep<0||WeightStep>1||!float.IsFinite(Rate)||Rate<0||!float.IsFinite(RewardRate)||RewardRate<0||!float.IsFinite(Speed)||Speed<.05f||Speed>2)throw new ArgumentException("Invalid predictive maze settings.");
    }
}

/// <summary>Predictive cortical runtime with an optional online neural action learner.</summary>
public sealed partial class MazeSession : IDisposable
{
    // Observation buffer retains the viewer's PCM/FFT offsets; only current sensory features are encoded into neurons.
    public const int LeftAudioOffset=MazeVision.HistorySize,LeftSpectrumOffset=LeftAudioOffset+MazeGame.AudioSize;
    public const int LegacyObservationCount=LeftSpectrumOffset+MazeGame.AudioSize/2,RightAudioOffset=LegacyObservationCount,RightSpectrumOffset=RightAudioOffset+MazeGame.AudioSize;
    public const int ObservationCount=RightSpectrumOffset+MazeGame.AudioSize/2;
    public const int PainCount=16,PleasureCount=16,ProbeCount=128,OutputCount=8;
    public const string EngineName="paired-error-pc-v2";
    public const int FeatureCount=MazeVision.FrameSize+192+8+3+2*PainCount+PleasureCount+1;
    public const int PainFeatureOffset=MazeVision.FrameSize+192+8+3;
    public const int InputCount=2*FeatureCount;
    public const int RewardFeatureCount=MazeVision.FrameSize+192+8+3+ProbeCount+MazeRewardContext.Count+1;
    public const int RewardInputCount=2*RewardFeatureCount;
    public MazeConfig Config { get; }
    public MazeAdaptiveLearner? Adaptive { get; }
    public MazeAdaptiveRewards AdaptiveRewards { get; }=new();
    private MazeAdaptiveState? dormantAdaptive;
    private MazeAdaptiveState? CaptureAdaptive()=>Adaptive?.Capture()??dormantAdaptive;
    private void RestoreAdaptive(MazeAdaptiveState state)
    {
        (Adaptive??new MazeAdaptiveLearner(Config.Seed+919)).Restore(state);
        dormantAdaptive=state;
    }
    public MazeGame Game { get; }
    public PredictiveCodingModel MainModel { get; }
    public PredictiveCodingModel RewardModel { get; }
    public IPredictiveCodingBackend Backend { get; }
    private readonly IPredictiveCodingBackend rewardBackend;
    public bool Training { get; private set; }
    public long Frames { get; private set; }
    public long MainUpdates { get; private set; }
    public long RewardUpdates { get; private set; }
    public long RewardPropagationSteps=>IsBakedBrain?RewardTeachingTicks:rewardBackend.PropagationSteps;
    public long RewardTeachingTicks { get; private set; }
    public int CompletedMazes { get; private set; }
    public long TotalHits { get; private set; }
    public long TotalKills { get; private set; }
    public long TotalMisses { get; private set; }
    public long TotalAmmo { get; private set; }
    public long TotalHealing { get; private set; }
    public long TotalExploration { get; private set; }
    public long TotalRegions { get; private set; }
    public long StationaryFrames { get; private set; }
    public float TotalPleasure { get; private set; }
    public float TotalPain { get; private set; }
    public float WeightRewardFactor { get; private set; }
    public float EffectiveLearningRate { get; private set; }
    public float Pleasure { get; private set; }
    public float Pain { get; private set; }
    public float PrimitivePain { get; private set; }
    public float PrimitivePleasure { get; private set; }
    public float AppliedPleasure { get; private set; }
    public float AppliedPain { get; private set; }
    public float LearningReward { get; private set; } // Observer-only reward, never an optimization objective.
    public double Loss { get; private set; }
    public double RewardLoss { get; private set; }
    public double RewardTeachingError { get; private set; }
    public double FrameMilliseconds { get; private set; }
    public MazeControls LastControls { get; private set; }
    public string ControlOrigin { get; private set; }="THRESHOLD";
    public ReadOnlySpan<float> ControlScores=>scores;
    public float[] Observation { get; }=new float[ObservationCount];
    public float[] NormalActivities { get; }
    public float[] ErrorActivities { get; }
    public float[] Activations { get; } // Viewer adapter: [normal,0], never a signed output.
    public float[] RewardNormalActivities { get; }
    public float[] RewardErrorActivities { get; }
    private readonly float[] features=new float[FeatureCount],inputs=new float[InputCount],rewardFeatures=new float[RewardFeatureCount],rewardInputs=new float[RewardInputCount],scores=new float[OutputCount],probes=new float[ProbeCount];
    private int[]? adaptiveContextIndices;
    private readonly float[] adaptiveContext=new float[8];
    private ReadOnlySpan<float> AdaptiveReadout()
    {
        if(!IsBakedBrain)return scores;
        if(adaptiveContextIndices is null)
        {
            var hidden=Baked!.Brain.Manifest.Neurons.Select((n,i)=>(n,i)).Where(x=>x.n.Role==NeuronRole.Hidden).Select(x=>x.i).ToArray();
            adaptiveContextIndices=hidden.Length==0?Baked.Motors:Enumerable.Range(0,8).Select(i=>hidden[i*hidden.Length/8]).ToArray();
        }
        for(int i=0;i<8;i++)adaptiveContext[i]=NormalActivities[adaptiveContextIndices[i]];
        return adaptiveContext;
    }
    private readonly Complex[] fft=new Complex[MazeGame.AudioSize];
    private readonly MazeRewardContext rewardContext=new();
    private readonly RewardDriveInputs rewardDrive=new(2*PainCount,12);
    private readonly Random random;
    private readonly MazeTeacherBot teacherBot;
    public bool BotEnabled { get; set; }
    public string BotStatus=>teacherBot.Dodging?"DODGE":teacherBot.Goal;
    public MazeSession(MazeConfig config,bool training,string backend="vulkan",int device=-1,PredictiveCodingModel? main=null,PredictiveCodingModel? reward=null,MazeScenario? scenario=null,BakedBrain? bakedBrain=null)
    {
        config.Validate();Config=config;if(config.AdaptiveControl)Adaptive=new(config.Seed+919);Training=training;random=new(config.Seed+17);teacherBot=new(config.Seed+413);
        if(bakedBrain is not null)
        {
            bakedBrain=MazeTactileBrain.Upgrade(bakedBrain);
            Baked=new(bakedBrain);CortexRewards=new(bakedBrain);MainModel=bakedBrain.Model;RewardModel=MainModel;
            MainModel.SetWeightLimit(config.WeightLimit);MainModel.Unfreeze();
            Backend=CreateBackend(MainModel,backend,device);rewardBackend=Backend;
            BakedRuntime=new(bakedBrain,Backend);
            NormalActivities=new float[MainModel.NormalCount];ErrorActivities=new float[2*MainModel.NormalCount];Activations=new float[2*MainModel.NormalCount];
            RewardNormalActivities=[];RewardErrorActivities=[];
            Game=new(config.Seed,config.Speed,scenario,training&&scenario is null);PrimitivePain=Game.PrimitivePain;Observe();
            return;
        }
        MainModel=main??new(GraphTopology.Layered(InputCount,Enumerable.Repeat(config.Width,config.Layers).ToArray(),OutputCount),config.Seed);
        RewardModel=reward??new(GraphTopology.Layered(RewardInputCount,[config.RewardHidden],2,false),config.Seed+1);
        // Uniform, fixed plasticity for this experiment, including resumed models.
        Array.Fill(MainModel.Plasticity,1f);Array.Fill(RewardModel.Plasticity,1f);
        MainModel.SetWeightLimit(config.WeightLimit);RewardModel.SetWeightLimit(config.WeightLimit);
        MainModel.SetActivationLimit(1);RewardModel.SetActivationLimit(1);
        if(MainModel.Topology.InputCount!=InputCount||MainModel.Topology.Outputs.Length!=8||MainModel.Topology.NodeCount!=InputCount+config.Layers*config.Width+8||RewardModel.Topology.InputCount!=RewardInputCount||RewardModel.Topology.Outputs.Length!=2||RewardModel.Topology.NodeCount!=RewardInputCount+config.RewardHidden+2)throw new ArgumentException("Predictive checkpoint does not match its sensory graph.");
        MainModel.Unfreeze();if(training)RewardModel.Unfreeze();else RewardModel.Freeze();
        Backend=CreateBackend(MainModel,backend,device);
        Backend.MatchOutputErrorsToNormal=true;
        try{rewardBackend=CreateBackend(RewardModel,backend,device);}catch{Backend.Dispose();throw;}
        NormalActivities=new float[MainModel.Topology.NodeCount];ErrorActivities=new float[2*NormalActivities.Length];Activations=new float[2*NormalActivities.Length];
        RewardNormalActivities=new float[RewardModel.Topology.NodeCount];RewardErrorActivities=new float[2*RewardNormalActivities.Length];
        Game=new(config.Seed,config.Speed,scenario,training&&scenario is null);PrimitivePain=Game.PrimitivePain;Observe();
    }
    private static IPredictiveCodingBackend CreateBackend(PredictiveCodingModel model,string backend,int device)
    {
        var selected = backend switch
        {
            "cpu" => MYT.Kit.Neural.NeuralBackend.Cpu,
            "vulkan" => MYT.Kit.Neural.NeuralBackend.Vulkan,
            "auto" => MYT.Kit.Neural.NeuralBackend.Auto,
            _ => throw new ArgumentException("Backend must be cpu, vulkan or auto.")
        };
        return MYT.Kit.Neural.PredictiveNetworks.Create(model,selected,device,Console.WriteLine);
    }
    public void SetMode(bool training){if(Training!=training)Adaptive?.ResetEpisode();if(IsBakedBrain){Training=training;return;}if(Training!=training){AppliedPleasure=0;AppliedPain=0;}Training=training;if(training)RewardModel.Unfreeze();else RewardModel.Freeze();}
    public void ResetEpisode(){Adaptive?.ResetEpisode();AdaptiveRewards.Reset();Game.SetCompletedMazes(CompletedMazes);Game.SetTrainingLayout(Training&&Frames<12000);Game.Reset();rewardContext.Reset();LastControls=MazeControls.None;PrimitivePain=Game.PrimitivePain;Observe();}
    private void Observe()
    {
        Game.RgbPixels.CopyTo(Observation,0);
        void Ear(float[] audio,int at,int spectrum)
        {
            audio.CopyTo(Observation,at);
            for(int i=0;i<fft.Length;i++)fft[i]=audio[i]*(.5-.5*Math.Cos(2*Math.PI*i/(fft.Length-1)));
            Fourier.Forward(fft);for(int i=0;i<fft.Length/2;i++)Observation[spectrum+i]=(float)fft[i].Magnitude/fft.Length;
        }
        Ear(Game.Audio,LeftAudioOffset,LeftSpectrumOffset);Ear(Game.AudioRight,RightAudioOffset,RightSpectrumOffset);
    }
    private int Sensory(float[] destination)
    {
        Observation.AsSpan(0,MazeVision.FrameSize).CopyTo(destination);int at=MazeVision.FrameSize;
        Observation.AsSpan(LeftAudioOffset,96).CopyTo(destination.AsSpan(at));at+=96;
        Observation.AsSpan(RightAudioOffset,96).CopyTo(destination.AsSpan(at));return at+96;
    }
    private int ButtonsAndVitals(float[] destination,int at)
    {
        foreach(var button in MazeControlPolicy.Buttons)destination[at++]=(LastControls&button)!=0?1:0;
        destination[at++]=Game.Health/100;destination[at++]=(Game.Ammo+Game.Reserve)/104f;destination[at++]=Game.Ammo/8f;return at;
    }
    private void RefreshMain()
    {
        Backend.ReadNormal(NormalActivities);Backend.ReadErrorRates(ErrorActivities);
        for(int i=0;i<NormalActivities.Length;i++){Activations[2*i]=NormalActivities[i];Activations[2*i+1]=0;}
        for(int i=0;i<8;i++)scores[i]=NormalActivities[Baked?.Motors[i]??MainModel.Topology.Outputs[i]];
        Loss=Backend.MeasureTotalError();
    }
    public void StepControls(MazeControls? forcedControls=null)
    {
        if(forcedControls.HasValue&&(forcedControls.Value&~MazeControlPolicy.All)!=0)throw new ArgumentOutOfRangeException(nameof(forcedControls));
        if(IsBakedBrain){StepBaked(forcedControls);return;}
        long started=Stopwatch.GetTimestamp();
        float feedbackPleasure=AppliedPleasure,feedbackPain=AppliedPain,bodyPain=PrimitivePain,bodyPleasure=PrimitivePleasure;
        if(Game.Done||(Adaptive is not null&&(Game.Completed||Game.Tick*Game.Speed>=10800)))ResetEpisode();
        bool botControl=BotEnabled&&!forcedControls.HasValue;
        if(botControl)forcedControls=teacherBot.Controls(Game);
        else teacherBot.ReleaseControl();
        bool adaptiveControl=Adaptive is not null&&!forcedControls.HasValue;
        if(adaptiveControl)forcedControls=Adaptive!.Choose(Game,AdaptiveReadout(),Training);
        else Adaptive?.ResetEpisode();
        Span<float> motorClamps=stackalloc float[OutputCount];
        if(forcedControls.HasValue)
            for(int i=0;i<OutputCount;i++)motorClamps[i]=(forcedControls.Value&MazeControlPolicy.Buttons[i])!=0?1:0;
        Array.Clear(features);int at=ButtonsAndVitals(features,Sensory(features));
        TotalPleasure=feedbackPleasure/MazeGame.MaximumPleasure+bodyPleasure;
        TotalPain=feedbackPain+bodyPain;
        // Main Δw = -weightStep * baseRate * plasticity * error * normal * (total reward - total punishment).
        // No baseline: zero net reward stops plasticity; negative net reward reverses it.
        WeightRewardFactor=TotalPleasure-TotalPain;
        EffectiveLearningRate=Config.Rate*WeightRewardFactor;
        rewardDrive.Write(features.AsSpan(at,2*PainCount),Frames,TotalPleasure,TotalPain,random);at+=2*PainCount;
        features.AsSpan(at,PleasureCount).Fill(bodyPleasure);features[^1]=ClockInput.Value(Frames,60,1);
        RateEncoding.Encode(features,inputs);Backend.Gain=1;
        Backend.Step(inputs,Config.PropagationSteps,Config.ActivityStep,Config.WeightStep,EffectiveLearningRate,true,forcedControls.HasValue?motorClamps:default);MainUpdates++;RefreshMain();
        for(int i=0;i<ProbeCount;i++)probes[i]=NormalActivities[InputCount+i*(Config.Width*Config.Layers)/ProbeCount];
        MazeControls controls=MazeControlPolicy.FromActivities(scores);ControlOrigin="THRESHOLD";
        if(forcedControls.HasValue){controls=forcedControls.Value;ControlOrigin=adaptiveControl?"ADAPTIVE":botControl?"BOT":"MANUAL";}
        Game.Step(controls,Training);LastControls=controls;
        TotalHits+=Game.Events.Hits;TotalKills+=Game.Events.Kills;TotalMisses+=Game.Events.MissedShots;TotalAmmo+=Game.Events.AmmoPickups;if(Game.Events.Healed>0)TotalHealing++;
        TotalExploration+=Game.NewCellsThisFrame;TotalRegions+=Game.NewRegionsThisFrame;if(Game.StationaryTeacherPain>0)StationaryFrames++;if(Game.Events.Completed)CompletedMazes++;
        PrimitivePain=Game.PrimitivePain;PrimitivePleasure=RewardSensation.Extend(PrimitivePleasure,Game.PrimitivePleasure);Observe();rewardContext.Observe(Observation,LastControls);
        Array.Clear(rewardFeatures);at=ButtonsAndVitals(rewardFeatures,Sensory(rewardFeatures));probes.CopyTo(rewardFeatures,at);at+=ProbeCount;rewardContext.Values.CopyTo(rewardFeatures,at);rewardFeatures[^1]=Game.VisualDarknessScore;
        RateEncoding.Encode(rewardFeatures,rewardInputs);
        rewardBackend.Step(rewardInputs,Config.PropagationSteps,Config.ActivityStep,Config.WeightStep,Config.RewardRate,false);
        rewardBackend.ReadNormal(RewardNormalActivities);rewardBackend.ReadErrorRates(RewardErrorActivities);RewardLoss=rewardBackend.MeasureTotalError();
        Pleasure=Math.Clamp(RewardNormalActivities[RewardModel.Topology.Outputs[0]],0,1)*MazeGame.MaximumPleasure;
        Pain=Math.Clamp(RewardNormalActivities[RewardModel.Topology.Outputs[1]],0,1);
        if(Training)
        {
            // Teaching starts from this same live state, with labeled boundary conditions.
            // Restore the free inference state afterwards; local weight changes remain.
            var free=new PredictiveCodingState(RewardNormalActivities,RewardErrorActivities,rewardBackend.PropagationSteps);
            float[] labels=[Game.TeacherTarget.Pleasure/MazeGame.MaximumPleasure,Game.TeacherTarget.Pain];
            rewardBackend.Step(rewardInputs,Config.PropagationSteps,Config.ActivityStep,Config.WeightStep,Config.RewardRate,true,labels);
            RewardTeachingError=rewardBackend.MeasureTotalError();RewardUpdates++;RewardTeachingTicks+=Config.PropagationSteps;
            rewardBackend.RestoreState(free);
        }
        AppliedPleasure=RewardSensation.Extend(AppliedPleasure,Training?Game.TeacherTarget.Pleasure:Pleasure);
        AppliedPain=RewardSensation.Extend(AppliedPain,Training?Game.TeacherTarget.Pain:Pain);
        LearningReward=(Training?Game.AppliedIdealReward:Pleasure-Pain)+Game.PrimitiveLearningReward;
        if(Adaptive is not null)
        {
            LearningReward=AdaptiveRewards.Observe(Game);
            if(adaptiveControl)Adaptive.Observe(Game,AdaptiveReadout(),LearningReward,Game.Done||Game.Completed||Game.Tick*Game.Speed>=10800,Training);
        }
        if(!double.IsFinite(Loss)||!double.IsFinite(RewardLoss))throw new InvalidOperationException("Nonfinite predictive state. Reduce the activity step or weight learning rate.");
        Frames++;FrameMilliseconds=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    public void Step(MazeAction? action=null)=>StepControls(action.HasValue?MazeControlPolicy.FromLegacy(action.Value):null);
    public void Save(string directory)
    {
        if(IsBakedBrain){SaveBaked(directory);return;}
        Directory.CreateDirectory(directory);Backend.DownloadParameters();rewardBackend.DownloadParameters();MainModel.Save(Path.Combine(directory,"main.pcn"));RewardModel.Save(Path.Combine(directory,"reward.pcn"));
        var state=new State(EngineName,Config,Frames,MainUpdates,RewardUpdates,RewardTeachingTicks,CompletedMazes,TotalHits,TotalKills,TotalMisses,TotalAmmo,TotalHealing,TotalExploration,TotalRegions,StationaryFrames,Pleasure,Pain,PrimitivePain,PrimitivePleasure,LastControls,Backend.CaptureState(),rewardBackend.CaptureState(),rewardDrive.Capture(),Feedback:new(AppliedPleasure,AppliedPain,Training),Adaptive:CaptureAdaptive());
        string path=Path.Combine(directory,"maze.json");File.WriteAllText(path+".tmp",JsonSerializer.Serialize(state));File.Move(path+".tmp",path,true);
    }
    public static MazeSession Load(string directory,bool training,string backend,int device=-1,float? speed=null,int? propagationSteps=null,float? activityStep=null,float? weightStep=null,float? rate=null,float? rewardRate=null,float? weightLimit=null,float? thinkingBias=null,bool? adaptiveControl=null)
    {
        if(File.Exists(Path.Combine(directory,"brain-maze.json")))return LoadBaked(directory,training,backend,device,speed,propagationSteps,activityStep,weightStep,rate,rewardRate,weightLimit,thinkingBias,adaptiveControl);
        var s=JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(directory,"maze.json")))??throw new InvalidDataException("Empty predictive checkpoint.");
        if(s.Engine is not ("paired-error-pc-v1" or "paired-error-pc-v2"))throw new InvalidDataException("This is an old non-predictive checkpoint. Use the new default checkpoint or --train --new in another directory.");
        if(s.Feedback is {} feedback&&(!float.IsFinite(feedback.Pleasure)||feedback.Pleasure<0||feedback.Pleasure>MazeGame.MaximumPleasure||!float.IsFinite(feedback.Pain)||feedback.Pain<0||feedback.Pain>1))
            throw new InvalidDataException("Invalid reward feedback trace.");
        var session=new MazeSession(s.Config with{AdaptiveControl=adaptiveControl??s.Config.AdaptiveControl,Speed=speed??s.Config.Speed,PropagationSteps=propagationSteps??s.Config.PropagationSteps,ActivityStep=activityStep??s.Config.ActivityStep,WeightStep=weightStep??s.Config.WeightStep,Rate=rate??s.Config.Rate,RewardRate=rewardRate??s.Config.RewardRate,WeightLimit=weightLimit??s.Config.WeightLimit,ThinkingBias=thinkingBias??s.Config.ThinkingBias},training,backend,device,PredictiveCodingModel.Load(Path.Combine(directory,"main.pcn")),PredictiveCodingModel.Load(Path.Combine(directory,"reward.pcn")));
        session.Frames=s.Frames;session.MainUpdates=s.MainUpdates;session.RewardUpdates=s.RewardUpdates;session.RewardTeachingTicks=s.RewardTeachingTicks;session.CompletedMazes=s.CompletedMazes;session.TotalHits=s.Hits;session.TotalKills=s.Kills;session.TotalMisses=s.Misses;session.TotalAmmo=s.Ammo;session.TotalHealing=s.Healing;session.TotalExploration=s.Exploration;session.TotalRegions=s.Regions;session.StationaryFrames=s.Stationary;
        session.ResetEpisode();session.Backend.RestoreState(s.MainState);session.rewardBackend.RestoreState(s.RewardState);session.RefreshMain();session.rewardBackend.ReadNormal(session.RewardNormalActivities);session.rewardBackend.ReadErrorRates(session.RewardErrorActivities);session.RewardLoss=session.rewardBackend.MeasureTotalError();session.Pleasure=s.Pleasure;session.Pain=s.Pain;session.PrimitivePain=s.PrimitivePain;session.PrimitivePleasure=s.PrimitivePleasure;if(s.DriveState is not null)session.rewardDrive.Restore(s.DriveState);
        // Teacher traces must never enter play when switching a checkpoint's mode.
        if(s.Feedback is {} saved&&saved.Training==training)
        {session.AppliedPleasure=saved.Pleasure;session.AppliedPain=saved.Pain;}
        else if(!training){session.AppliedPleasure=session.Pleasure;session.AppliedPain=session.Pain;}
        if(s.Adaptive is not null)session.RestoreAdaptive(s.Adaptive);
        return session;
    }
    public void Dispose(){if(!IsBakedBrain)rewardBackend.Dispose();Backend.Dispose();}
    private sealed record State(string Engine,MazeConfig Config,long Frames,long MainUpdates,long RewardUpdates,long RewardTeachingTicks,int CompletedMazes,long Hits,long Kills,long Misses,long Ammo,long Healing,long Exploration,long Regions,long Stationary,float Pleasure,float Pain,float PrimitivePain,float PrimitivePleasure,MazeControls Controls,PredictiveCodingState MainState,PredictiveCodingState RewardState,RewardDriveState? DriveState=null,HeldPainState? PainBanks=null,FeedbackState? Feedback=null,MazeAdaptiveState? Adaptive=null);
    private sealed record FeedbackState(float Pleasure,float Pain,bool Training);
}
