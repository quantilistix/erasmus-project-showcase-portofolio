using ArcadeLab;
using ArcadeLab.Editor;
using NeuralGraph;
using System.Numerics;
using MYT.Kit.Gui;
using GlmSharp;

internal static class MazeBakedChecks
{
    internal static void Run(Action<bool,string> check,string output)
    {
        var audio=new BrainModule {Name="Audio Input",Kind=ModuleKind.InputPanel,Channels=2};
        var video=new BrainModule {Name="Video Input",Kind=ModuleKind.InputPanel,Channels=3};
        var pain=new BrainModule {Name="Tactile",Kind=ModuleKind.InputPanel,Panel=PanelKind.Tactile,Channels=16};
        var clock=new BrainModule {Kind=ModuleKind.Clock,Frequency=8};
        var reward=new BrainModule {Kind=ModuleKind.Reward};
        var motor=new BrainModule {Kind=ModuleKind.OutputPanel,Channels=8};
        // Reward readouts deliberately precede the actual motor panel in topology output order.
        var document=new BrainDocument {Modules=[audio,video,pain,clock,reward,motor],Links=[new() {Source=new() {ModuleId=video.Id},Destination=new() {ModuleId=motor.Id},Plasticity=0,WeightKind=LinkWeightKind.Constant,Weight=.2f}]};
        var brain=Baking.Bake(document);var weights=(float[])brain.Model.Weights.Clone();var plasticity=(float[])brain.Model.Plasticity.Clone();
        using var session=new MazeSession(new() {PropagationSteps=2},false,"cpu",bakedBrain:brain);
        check(session.MainModel.Topology.Outputs.Length>8&&session.Baked!.Motors.Length==8,"maze selects motor panel independently of other baked readouts");
        var inputs=session.Baked!.Inputs(session.Game);
        check(inputs.Length==brain.Model.Topology.InputCount&&inputs.All(v=>float.IsFinite(v)&&v>=0),"baked maze sensory boundary shape");
        int videoIndex=brain.Manifest.Neurons.FindIndex(n=>n.ModuleId==video.Id);
        check(Math.Abs(inputs[videoIndex]-Enumerable.Range(0,MazeGame.PixelCount).Average(i=>session.Game.RgbPixels[3*i]))<.00001f,"compact baked video maps actual RGB observation");
        // Feed distinguishable values and verify the live boundary neurons, not only the encoder.
        for(int i=0;i<MazeGame.PixelCount;i++){session.Game.RgbPixels[3*i]=.2f;session.Game.RgbPixels[3*i+1]=.4f;session.Game.RgbPixels[3*i+2]=.6f;}
        Array.Fill(session.Game.Audio,.25f);Array.Fill(session.Game.AudioRight,.75f);
        var expectedInputs=session.Baked.Inputs(session.Game);
        session.StepControls(MazeControls.None);
        check(session.Baked.InputLabels.Where(p=>!p.Value.StartsWith("clock")).All(p=>Math.Abs(session.NormalActivities[p.Key]-expectedInputs[p.Key])<.00001f),"every named sensory signal reaches its actual clamped neuron");
        int audioIndex=brain.Manifest.Neurons.FindIndex(n=>n.ModuleId==audio.Id);
        check(Math.Abs(expectedInputs[audioIndex]-.25f)<.00001f&&Math.Abs(expectedInputs[audioIndex+1]-.75f)<.00001f,"left and right audio remain separate at the baked boundaries");
        var controls=MazeControls.Forward|MazeControls.StrafeLeft|MazeControls.Fire;
        session.StepControls(controls);
        check(session.LastControls==controls&&session.ControlOrigin=="MANUAL","baked maze accepts simultaneous manual buttons");
        check(session.Baked.Motors.Select((node,i)=>session.NormalActivities[node]==((controls&MazeControlPolicy.Buttons[i])!=0?1:0)).All(v=>v),"manual buttons clamp actual baked motor activities");
        check(brain.Model.Weights.SequenceEqual(weights)&&brain.Model.Plasticity.SequenceEqual(plasticity),"baked maze preserves fixed zero plasticity");
        check(session.BakedRuntime!.Time>0&&session.Backend.PropagationSteps==4&&double.IsFinite(session.Loss),"baked maze propagates finite live state and clocks");
        string checkpoint=Path.Combine(output,"baked-maze-"+Guid.NewGuid().ToString("N"));session.Save(checkpoint);
        using var resumed=MazeSession.Load(checkpoint,false,"cpu");
        check(resumed.IsBakedBrain&&resumed.Frames==session.Frames&&resumed.BakedRuntime!.Time==session.BakedRuntime.Time,"baked maze checkpoint restores network type, frame and clock time");
        check(resumed.NormalActivities.SequenceEqual(session.NormalActivities)&&resumed.MainModel.Weights.SequenceEqual(session.MainModel.Weights),"baked maze checkpoint restores activities and weights");
        resumed.StepControls();check(resumed.Frames==session.Frames+1&&double.IsFinite(resumed.Loss),"resumed baked maze continues autonomous inference");
        session.BotEnabled=true;session.StepControls();check(session.ControlOrigin=="BOT","baked maze teacher bot uses the shared controls path");
        Viewer.CaptureMaze(session,1100,740,Path.Combine(output,"baked-maze.ppm"));
        check(true,"baked maze viewer renders arbitrary topology");
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"maze-toggle.ppm"),(canvas,w,h)=>
        {
            // The fourth toolbar button toggles on mouse press; release before pressing again.
            var cursor=new vec2(.54f*w,.04f*h+20);
            canvas.RenderMazePreview(session,w,h,new GuiInput {Cursor=cursor});
            check(canvas.Graph.Lines.Length==brain.Model.Weights.Length*2,"maze graph draws all baked edges with the editor renderer");
            canvas.Graph.Clear();canvas.RenderMazePreview(session,w,h,new GuiInput {Cursor=cursor,MouseDown=true});
            check(!canvas.MazeGraphVisible,"Hide Graph toolbar button responds to a click");
            canvas.Graph.Clear();canvas.RenderMazePreview(session,w,h,new GuiInput {Cursor=cursor});
            check(canvas.Graph.Lines.Length==0&&canvas.Graph.Neurons.Length==0,"hidden graph emits no geometry");
            canvas.Graph.Clear();canvas.RenderMazePreview(session,w,h,new GuiInput {Cursor=cursor,MouseDown=true});
            check(canvas.MazeGraphVisible,"Show Graph toolbar button restores visibility");
        });
        var arena=new MazeScenario(["#########","#.......#","#.......#","#.......#","#.......#","#.......#","#########"],new Vector2(4,3),0,[],Ammo:4,Reserve:24);
        motor.ChannelBindings=["X","F","E","Q","D","A","S","W"];
        foreach(var binding in MazeKeyBinding.All)
        {
            using var autonomous=new MazeSession(new() {PropagationSteps=1},false,"cpu",scenario:arena,bakedBrain:Baking.Bake(document));
            var state=autonomous.Backend.CaptureState();
            int slot=Array.IndexOf(MazeControlPolicy.Buttons.ToArray(),binding.Control);
            state.Normal[autonomous.Baked!.Motors[slot]]=1;autonomous.Backend.RestoreState(state);
            var expected=new MazeGame(42,.5f,arena);expected.Step(binding.Control,false);
            autonomous.StepControls();
            check(autonomous.LastControls==binding.Control&&autonomous.ControlOrigin=="BAKED",$"autonomous output bound to {binding.Key} triggers {binding.Action}");
            check(Vector2.Distance(autonomous.Game.Position,expected.Position)<.00001f&&Math.Abs(autonomous.Game.Heading-expected.Heading)<.00001f&&autonomous.Game.Ammo==expected.Ammo&&autonomous.Game.Reloading==expected.Reloading,$"{binding.Key} neural action matches the physical key's game effects");
            check(MazeKeyBinding.Read(key=>key==binding.Code)==binding.Control,$"keyboard {binding.Key} uses the same action binding");
        }
        motor.ChannelBindings=["W","W","A","D","Q","E","F","X"];
        try{_ = new MazeBakedBrain(Baking.Bake(document));check(false,"duplicate output key rejected");}
        catch(InvalidDataException){check(true,"duplicate output key rejected");}
        motor.Channels=4;
        try{_ = new MazeBakedBrain(Baking.Bake(document));check(false,"invalid motor panel rejected");}
        catch(InvalidDataException){check(true,"invalid motor panel rejected");}
    }
}
