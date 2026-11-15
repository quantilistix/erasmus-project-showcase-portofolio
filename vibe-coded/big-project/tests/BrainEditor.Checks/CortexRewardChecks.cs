using ArcadeLab;
using ArcadeLab.Editor;
using NeuralGraph;
using System.Numerics;
using System.Text.Json;

internal static class CortexRewardChecks
{
    private static BakedBrain Fixture()
    {
        var input=new BrainModule {Id="input",Name="Body",Kind=ModuleKind.InputPanel,Channels=1,ChannelBindings=["body.health"]};
        var core=new BrainModule {Id="core",Name="Central Cortex",Kind=ModuleKind.Custom};
        var reward=new BrainModule {Id="reward",Name="Reward Central",Kind=ModuleKind.Reward};
        var motor=new BrainModule {Id="motor",Kind=ModuleKind.OutputPanel,Channels=8};
        var neurons=new List<GeneratedNeuron> {new("0","input",NeuronRole.Input,new(),true,1),new("0","core",NeuronRole.Hidden,new(1,0,0),false,1),new("0","reward",NeuronRole.Output,new(2,0,0),false,8)};
        for(int i=0;i<8;i++)neurons.Add(new(i.ToString(),"motor",NeuronRole.Output,new(3,i,0),false,1));
        var model=new PredictiveCodingModel(new(1,11,1,Enumerable.Range(2,9).ToArray(),[new(1,3),new(0,2)]));Array.Fill(model.Weights,.2f);
        return new(model,new() {Modules=[input,core,reward,motor],Neurons=neurons,RewardGroups=[new() {Name="Central",ModuleIds=["core"],RewardModuleId="reward"}]});
    }
    internal static void Run(Action<bool,string> check,string output)
    {
        foreach(var (previous,expected) in new[]{(3f,1),(5f,-1),(4f,0)})
        {
            using var runtime=new BrainRuntime(Fixture());var state=runtime.CaptureState();
            state.Network.Normal[1]=.8f;state.Network.Normal[2]=4;state.Network.Normal[3]=.6f;state.Network.ErrorRates[2]=.3f;
            runtime.RestoreState(state with {PreviousRewards=new() {{"Central",previous}}});
            float before=runtime.Brain.Model.Weights[0];
            runtime.Step([1],1,activityStep:.000001f,weightStep:.1f,rate:.1f);
            float after=runtime.Brain.Model.Weights[0];
            check(Math.Sign(after-before)==expected,$"reward derivative {expected} selects learning, unlearning or zero cortical plasticity");
            double Energy(float weight)=>.5*Math.Pow(.8-weight*.6,2);
            check(expected==0?after==before:Math.Sign(Energy(after)-Energy(before))==-expected,$"reward factor {expected} has the intended free-energy direction at fixed activities");
            check(runtime.Brain.Model.Weights[1]==.2f,"free inference freezes reward-path parameters");
            if(expected==1)
            {
                var free=runtime.CaptureState();float cortexWeight=after;
                runtime.TeachRewards([1],1,new Dictionary<int,float> {{2,6}},8,.04f,.1f,.1f);
                check(runtime.Brain.Model.Weights[1]!=.2f,"clamped reward teaching actually changes reward-path weights");
                check(runtime.Brain.Model.Weights[0]==cortexWeight,"reward teaching leaves cortical weights unchanged");
                var restored=runtime.CaptureState();
                check(restored.Time==free.Time&&restored.Network.Normal.SequenceEqual(free.Network.Normal)&&restored.Network.ErrorRates.SequenceEqual(free.Network.ErrorRates)&&restored.PreviousRewards.SequenceEqual(free.PreviousRewards),"reward teaching restores free activities, errors, clock and derivative history");
            }
        }
        foreach(float bias in new[]{0f,.1f})
        {
            using var runtime=new BrainRuntime(Fixture());var state=runtime.CaptureState();
            state.Network.Normal[1]=.8f;state.Network.Normal[2]=4;state.Network.Normal[3]=.6f;state.Network.ErrorRates[2]=.3f;
            runtime.RestoreState(state with {PreviousRewards=new() {{"Central",4}}});
            float before=runtime.Brain.Model.Weights[0];
            runtime.Step([1],1,activityStep:.000001f,weightStep:.1f,rate:.1f,learningBias:bias);
            check(runtime.RewardDerivatives["Central"]==0&&runtime.GroupFactors["Central"]==0,"implicit thinking bias does not modify reward perception or reward factors");
            check(bias==0?runtime.Brain.Model.Weights[0]==before:runtime.Brain.Model.Weights[0]>before,"thinking bias promotes cortical plasticity even with zero perceived reward");
            check(runtime.Brain.Model.Weights[1]==.2f,"thinking bias does not train reward-path weights");
        }
        foreach(string name in new[]{"Central","Spatial","Motory","Audio","Video"})
        {
            var brain=Fixture();brain.Manifest.RewardGroups[0].Name=name;
            using var runtime=new BrainRuntime(brain);var state=runtime.CaptureState();
            state.Network.Normal[1]=.8f;state.Network.Normal[2]=4;state.Network.Normal[3]=.6f;state.Network.ErrorRates[2]=.3f;
            runtime.RestoreState(state with {PreviousRewards=new() {{name,5}}});
            float before=brain.Model.Weights[0];
            runtime.Step([1],1,activityStep:.000001f,weightStep:.1f,rate:.1f,factorOverrides:new Dictionary<string,float> {{name,0}},learningBias:.1f);
            check(brain.Model.Weights[0]>before,$"implicit thinking bias reaches {name} cortex without reward-system modulation");
        }
        foreach(var (previous,sign) in new[]{(4.05f,1),(4.2f,-1)})
        {
            using var runtime=new BrainRuntime(Fixture());var state=runtime.CaptureState();
            state.Network.Normal[1]=.8f;state.Network.Normal[2]=4;state.Network.Normal[3]=.6f;state.Network.ErrorRates[2]=.3f;
            runtime.RestoreState(state with {PreviousRewards=new() {{"Central",previous}}});
            float before=runtime.Brain.Model.Weights[0];runtime.Step([1],1,activityStep:.000001f,weightStep:.1f,rate:.1f,learningBias:.1f);
            check(runtime.RewardDerivatives["Central"]<0&&Math.Sign(runtime.Brain.Model.Weights[0]-before)==sign,"negative reward reverses plasticity only when it exceeds the thinking bias");
        }
        var inactive=Fixture();var inactiveManifest=inactive.Manifest;
        inactiveManifest.Modules.Add(new BrainModule {Id="audio-reward",Kind=ModuleKind.Reward,Name="Reward Audio"});
        inactiveManifest.Neurons.Add(new GeneratedNeuron("0","audio-reward",NeuronRole.Output,new(),false,8));
        var inactiveModel=new PredictiveCodingModel(new(1,12,1,inactive.Model.Topology.Outputs.Append(11).ToArray(),inactive.Model.Topology.Edges.Append(new Edge(0,11)).ToArray()));
        Array.Fill(inactiveModel.Weights,.2f);
        using(var runtime=new BrainRuntime(new BakedBrain(inactiveModel,inactiveManifest)))
        {
            runtime.TeachRewards([1],1,new Dictionary<int,float> {{2,6}},8,.04f,.1f,.1f);
            check(inactiveModel.Weights[1]!=.2f&&inactiveModel.Weights[2]==.2f,"teaching an active reward head leaves disabled sensory reward-head connections untouched");
        }
        using(var trained=new MazeSession(new() {PropagationSteps=4,RewardRate=.1f},true,"cpu",bakedBrain:Fixture()))
        {
            float initial=trained.MainModel.Weights[1];trained.StepControls(MazeControls.None);
            check(trained.RewardUpdates==1&&trained.RewardTeachingTicks==4&&trained.MainModel.Weights[1]!=initial,"maze training performs and counts reward teaching updates");
            check(trained.NormalActivities[2]<1&&trained.CortexRewards!.Targets["Central"]==4,"teacher target is not substituted for perceived reward");
            string checkpoint=Path.Combine(output,"cortex-reward-"+Guid.NewGuid().ToString("N"));trained.Save(checkpoint);
            using var resumed=MazeSession.Load(checkpoint,true,"cpu",rewardRate:.2f,thinkingBias:.3f);
            check(resumed.Config.ThinkingBias==.3f,"thinking bias can be overridden when resuming a checkpoint");
            check(resumed.Config.RewardRate==.2f,"resumed baked checkpoints honor reward learning-rate overrides");
            check(resumed.RewardUpdates==trained.RewardUpdates&&resumed.RewardTeachingTicks==trained.RewardTeachingTicks&&resumed.CortexRewards!.Targets.SequenceEqual(trained.CortexRewards!.Targets),"checkpoint retains reward teaching counts and target traces");
            trained.SetMode(false);float frozen=trained.MainModel.Weights[1];trained.StepControls();
            check(trained.MainModel.Weights[1]==frozen&&trained.RewardUpdates==1,"play freezes reward learning and supplies no teacher updates");
        }
        var body=new BrainModule {Kind=ModuleKind.InputPanel,Channels=1,ChannelBindings=["body.health"]};
        var outputs=new BrainModule {Kind=ModuleKind.OutputPanel,Channels=8};
        string[] names=["Motory","Central","Spatial","Audio","Video","Reward System"];
        var doc=new BrainDocument {Modules=[body,outputs]};
        foreach(string name in names){var reward=new BrainModule {Kind=ModuleKind.Reward,Name="Reward "+name};doc.Modules.Add(reward);doc.RewardGroups.Add(new() {Name=name,RewardModuleId=reward.Id});}
        var policy=new MazeCortexRewards(Baking.Bake(doc));
        var events=new MazeEvents(2,1,0,0,0,true,3,1,2);policy.Observe(events,true,1d/60);
        var motor=policy.Signals["Motory"];var central=policy.Signals["Central"];var spatial=policy.Signals["Spatial"];
        check(motor.Reward==MazeCortexRewards.Hit&&motor.Punishment==3*MazeCortexRewards.Miss,"motor rewards visible-at-shot hits and punishes every miss");
        check(central.Reward==MazeCortexRewards.Kill+MazeCortexRewards.Completion&&central.Punishment==MazeCortexRewards.Death+2*MazeCortexRewards.Miss,"central rewards kills/exits and punishes death/unseen-shot misses");
        check(spatial.Reward==motor.Reward+central.Reward&&spatial.Punishment==motor.Punishment+central.Punishment,"spatial receives summed motor and central reward and punishment");
        check(policy.RewardFactorOverrides["Audio"]==0&&policy.RewardFactorOverrides["Video"]==0&&policy.Signals["Audio"]==default&&policy.Signals["Video"]==default,"audio and video have no assigned reward or reward modulation");
        var disabledHeads=doc.Modules.Where(m=>m.Name is "Reward Audio" or "Reward Video").Select(m=>m.Id).ToHashSet();
        check(policy.TeachingTargets().Keys.All(i=>!disabledHeads.Contains(Baking.Bake(doc).Manifest.Neurons[i].ModuleId)),"audio and video reward heads receive no teaching targets");
        policy.Observe(events,true,1d/60,primitivePain:.4f);
        check(Math.Abs(policy.Signals["Central"].Punishment-(central.Punishment+.4f))<.00001f,"central receives primitive pain in addition to event punishments");
        check(Math.Abs(policy.Signals["Spatial"].Punishment-(spatial.Punishment+.4f))<.00001f&&policy.Signals["Reward System"]==policy.Signals["Spatial"],"central primitive pain propagates through spatial and reward-system aggregates");
        check(policy.Signals["Motory"]==motor&&policy.Signals["Audio"].Punishment==0&&policy.Signals["Video"].Punishment==0,"primitive pain does not alter motor or disabled sensory assignments");
        var targetsBefore=policy.Capture();
        policy.Observe(default,false,1d/60,primitivePain:.8f,updateTargets:false);
        check(policy.Signals["Central"].Punishment==.8f&&policy.Targets.SequenceEqual(targetsBefore),"observer reward assignments stay live in play while teacher targets remain frozen");
        var arena=new MazeScenario(["#########","#.......#","#.......#","#.......#","#.......#","#.......#","#########"],new(2.5f,3.5f),0,[new(4.5f,3.5f,1)],Ammo:8);
        using(var painful=new MazeSession(new() {PropagationSteps=1},true,"cpu",scenario:arena with {Health=20,Enemies=[]},bakedBrain:Fixture()))
        {
            painful.StepControls(MazeControls.None);
            check(painful.PrimitivePain>0&&painful.CortexRewards!.Signals["Central"].Punishment==painful.PrimitivePain&&painful.CortexRewards.Targets["Central"]<MazeCortexRewards.Neutral,"maze feeds its actual primitive pain into central reward teaching");
            var target=painful.CortexRewards!.Targets["Central"];painful.SetMode(false);painful.StepControls();
            check(painful.CortexRewards.Signals["Central"].Punishment==painful.PrimitivePain&&painful.CortexRewards.Targets["Central"]==target,"play displays current primitive punishment without advancing reward targets");
            Viewer.CaptureMaze(painful,1100,740,Path.Combine(output,"assigned-rewards-small.ppm"));
            Viewer.CaptureMaze(painful,1600,1000,Path.Combine(output,"assigned-rewards-large.ppm"));
        }
        var game=new MazeGame(42,1,arena);game.Step(MazeControls.Fire,true);int hits=game.Events.VisibleShotHits;
        for(int i=0;i<15&&hits==0;i++){game.Step(MazeControls.TurnLeft,true);hits+=game.Events.VisibleShotHits;}
        check(hits==1,"delayed projectile impact retains target visibility from the firing frame");
        game=new MazeGame(42,1,arena with {Enemies=[new(1.5f,3.5f)]});game.Step(MazeControls.Fire,true);
        int unseen=game.Events.UnseenShotMisses,misses=game.Events.MissedShots;
        for(int i=0;i<20&&misses==0;i++){game.Step(MazeControls.None,true);unseen+=game.Events.UnseenShotMisses;misses+=game.Events.MissedShots;}
        check(misses==1&&unseen==1,"a shot fired without a camera-visible enemy produces motor and central miss credit");
        game=new MazeGame(42,1,arena with {Enemies=[new(5.5f,2.5f)]});game.Step(MazeControls.Fire,true);unseen=game.Events.UnseenShotMisses;misses=game.Events.MissedShots;
        for(int i=0;i<20&&misses==0;i++){game.Step(MazeControls.None,true);unseen+=game.Events.UnseenShotMisses;misses+=game.Events.MissedShots;}
        check(misses==1&&unseen==0,"missing a visible off-axis enemy still punishes motor without unseen-shot central credit");
        var longArena=new MazeScenario([new string('#',25),"#"+new string('.',23)+"#","#"+new string('.',23)+"#","#"+new string('.',23)+"#","#"+new string('.',23)+"#",new string('#',25)],new(2.5f,2.5f),0,[new(17.5f,2.5f,1)]);
        game=new MazeGame(42,1,longArena);game.Step(MazeControls.Fire,true);hits=game.Events.VisibleShotHits;
        for(int i=0;i<30&&hits==0;i++){game.Step(MazeControls.TurnLeft,true);hits+=game.Events.VisibleShotHits;}
        check(hits==1&&Math.Abs(game.Heading)>MazeGame.VisionFieldOfView/2,"visible-hit credit survives turning the target completely out of view before impact");
        var occluded=arena with {Map=["#########","#...#...#","#...#...#","#...#...#","#...#...#","#.......#","#########"],Enemies=[new(6.5f,3.5f)]};
        game=new MazeGame(42,1,occluded);game.Step(MazeControls.Fire,true);unseen=game.Events.UnseenShotMisses;
        for(int i=0;i<10&&unseen==0;i++){game.Step(MazeControls.None,true);unseen+=game.Events.UnseenShotMisses;}
        check(unseen==1,"wall-occluded enemy is excluded from shot-time visibility");
        Tactile(check,output,arena);
    }
    private static void Tactile(Action<bool,string> check,string output,MazeScenario arena)
    {
        var game=new MazeGame(42,1,arena with {Player=new(1.21f,3.5f),Enemies=[]});
        float contact=game.Tactile[8];game.Step(MazeControls.Backward,false);
        check(contact==MazeGame.TactileTouch&&game.Tactile[8]>=MazeGame.TactilePush,"tactile rear neuron distinguishes wall contact from pushing");
        game.Step(MazeControls.Forward,false);check(game.Tactile[8]<MazeGame.TactilePush,"releasing wall pressure removes the push signal");
        var rotated=new MazeGame(42,1,arena with {Player=new(1.21f,3.5f),Heading=MathF.PI/2,Enemies=[]});
        check(rotated.Tactile[4]==MazeGame.TactileTouch,"tactile ring rotates with the player: index 0 front, 4 right");
        game=new MazeGame(42,1,arena with {Enemies=[new(4.5f,3.5f,100)]});
        for(int i=0;i<180&&game.Events.Damage==0;i++)game.Step(MazeControls.None,false);
        float scream=game.Tactile.Max();check(game.Events.Damage>0&&scream>=MazeGame.TactileShotMinimum,"being shot produces a loud random tactile pulse");
        game.Step(MazeControls.None,false);check(game.Tactile.Max()<scream&&game.Tactile.Max()>MazeGame.TactilePush,"damage pulse fades over simulation time");
        game.Reset();check(game.Tactile.All(v=>v==0),"episode reset clears tactile trauma");
        var input=new BrainModule {Name="INPUTPANEL",Kind=ModuleKind.InputPanel,Channels=1};var motor=new BrainModule {Kind=ModuleKind.OutputPanel,Channels=8};
        var original=Baking.Bake(new BrainDocument {Modules=[input,motor],Links=[new() {Source=new() {ModuleId=input.Id},Destination=new() {ModuleId=motor.Id}}]});
        var expanded=MazeTactileBrain.Upgrade(original);
        check(expanded.Model.Topology.InputCount==original.Model.Topology.InputCount+15&&expanded.Manifest.Modules.Single(m=>m.Id==input.Id).ChannelBindings.Length==16,"scalar touch bake migrates into sixteen actual sensory neurons");
        check(Math.Abs(expanded.Model.Weights.Take(16).Sum()-original.Model.Weights[0])<.00001f,"tactile migration preserves aggregate outgoing scalar weights");
        using var oldRuntime=new BrainRuntime(original);var oldState=oldRuntime.CaptureState();oldState.Network.Normal[original.Model.Topology.Outputs[0]]=.75f;
        string path=Path.Combine(output,"scalar-migration-"+Guid.NewGuid().ToString("N"));original.Save(path);
        File.WriteAllText(Path.Combine(path,"brain-maze.json"),JsonSerializer.Serialize(new {Config=new MazeConfig(),Frames=2,MainUpdates=2,Runtime=oldState}));
        using var resumed=MazeSession.Load(path,false,"cpu");
        check(resumed.MainModel.Topology.InputCount==16&&resumed.NormalActivities[resumed.Baked!.Motors[0]]==.75f,"old scalar checkpoint remaps motor state while adding tactile boundaries");
        var signals=resumed.Baked!.Inputs(game);
        check(resumed.Baked.InputLabels.Where(p=>p.Value.StartsWith("tactile.")).All(p=>signals[p.Key]==game.Tactile[int.Parse(p.Value[8..])]),"all sixteen tactile signals bind to their matching ring neurons");
    }
}
