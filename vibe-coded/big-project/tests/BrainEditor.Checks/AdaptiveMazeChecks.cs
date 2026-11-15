using ArcadeLab;
using ArcadeLab.Editor;
using System.Numerics;

internal static class AdaptiveMazeChecks
{
    internal static void Run(Action<bool,string> check,string output)
    {
        var corridor=new MazeScenario(["#########","#.....E.#","#########"],new Vector2(1.5f,1.5f),0,[],Ammo:0,Reserve:0);
        var game=new MazeGame(17,1,corridor);var reward=new MazeAdaptiveRewards();float max=0;
        for(int i=0;i<55;i++){game.Step(MazeControls.Forward,true);max=Math.Max(max,reward.Observe(game));}
        check(game.Completed&&max>7.9f,"exit reward dominates exploration and ordinary events");
        check(game.VisualDarknessTeacherPleasure==0,"darkness never pays a reward");
        float budget=reward.ExplorationEarned;
        for(int i=0;i<25;i++){game.Step(MazeControls.Backward,true);reward.Observe(game);}
        check(reward.ExplorationEarned==budget,"revisiting explored cells cannot farm exploration reward");
        check(reward.ExplorationEarned<=2,"exploration return has a finite episode budget");
        var learner=new MazeAdaptiveLearner(17);float[] cortex=new float[8];var before=learner.Capture();
        // A controlled one-step task demonstrates that outcomes actually alter action preferences.
        // It is a learning mechanism check, not evidence of general maze competence.
        for(int i=0;i<1500;i++)
        {
            learner.ResetEpisode();game.Reset();var action=learner.Choose(game,cortex,true);
            learner.Observe(game,cortex,action==MazeControls.Forward?1:-.2f,true,true);
        }
        learner.ResetEpisode();game.Reset();
        check(learner.Choose(game,cortex,false)==MazeControls.Forward,"online neural learner acquires a rewarded action without teacher controls");
        check(learner.Updates>0&&!before.Weights.SequenceEqual(learner.Capture().Weights),"TD outcomes update neural weights");
        var frozen=learner.Capture();learner.Observe(game,cortex,8,true,false);
        check(learner.Capture().Weights.SequenceEqual(frozen.Weights)&&learner.Updates==frozen.Updates,"play freezes adaptive weights and updates");
        var restored=new MazeAdaptiveLearner(123);restored.Restore(frozen);
        check(restored.Capture().Weights.SequenceEqual(frozen.Weights)&&restored.ReplayCount==0&&restored.KnownViews==0,"restore retains learned policy and clears world-specific memory");
        try{restored.Restore(frozen with {Weights=[float.NaN]});check(false,"invalid adaptive checkpoint rejected");}
        catch(InvalidDataException){check(true,"invalid adaptive checkpoint rejected");}
        using var session=new MazeSession(new() {AdaptiveControl=true,PropagationSteps=1},true,"cpu",scenario:corridor,bakedBrain:Baking.Bake(MazeBrainTemplate.Create()));
        for(int i=0;i<40;i++)session.Step();
        check(session.ControlOrigin=="ADAPTIVE"&&session.Adaptive!.Updates>0&&session.RewardUpdates>0,"baked maze integrates online action and cortical reward learning");
        check(session.NormalActivities.All(float.IsFinite),"adaptive baked inference stays finite");
        string path=Path.Combine(output,"adaptive-checkpoint");session.Save(path);
        using var resume=MazeSession.Load(path,true,"cpu");
        check(resume.Adaptive is not null&&resume.Adaptive.Capture().Weights.SequenceEqual(session.Adaptive!.Capture().Weights),"maze checkpoint restores adaptive neural weights");
        resume.Step();check(resume.ControlOrigin=="ADAPTIVE","resumed maze continues adaptive control");
        using(var disabled=MazeSession.Load(path,false,"cpu",adaptiveControl:false))
        {
            string dormant=Path.Combine(output,"adaptive-disabled");disabled.Save(dormant);
            using var enabled=MazeSession.Load(dormant,false,"cpu",adaptiveControl:true);
            check(enabled.Adaptive!.Capture().Weights.SequenceEqual(session.Adaptive!.Capture().Weights),"temporarily disabling adaptive control preserves saved policy weights");
        }
        using var complete=new MazeSession(new() {AdaptiveControl=true,PropagationSteps=1},true,"cpu",scenario:corridor with {Player=new(5.9f,1.5f)},bakedBrain:Baking.Bake(MazeBrainTemplate.Create()));
        for(int i=0;i<5&&!complete.Game.Completed;i++)complete.StepControls(MazeControls.Forward);
        int episode=complete.Game.Episode;
        check(complete.CompletedMazes==1,"session counts completion once");
        complete.StepControls(MazeControls.None);
        check(complete.Game.Episode==episode+1&&!complete.Game.Completed,"completion advances to a new maze and resets episodic memory");
        using var legacy=new MazeSession(new() {AdaptiveControl=true,Layers=1,Width=16,RewardHidden=4,PropagationSteps=1},true,"cpu",scenario:corridor);
        for(int i=0;i<20;i++)legacy.Step();
        check(legacy.ControlOrigin=="ADAPTIVE"&&legacy.Adaptive!.Updates>0,"unbaked predictive maze also supports adaptive control");
    }
}
