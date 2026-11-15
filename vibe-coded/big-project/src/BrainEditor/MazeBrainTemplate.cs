namespace ArcadeLab.Editor;

/// <summary>Sparse recurrent modules, with local sensory processing and separate integration loops.</summary>
public static class MazeBrainTemplate
{
    public static BrainDocument Create()
    {
        BrainModule Panel(string id,string name,string[] channels,Position position,PanelKind kind=PanelKind.Other)=>new()
        {Id=id,Name=name,Kind=ModuleKind.InputPanel,Channels=channels.Length,ChannelBindings=channels,Position=position,Panel=kind};
        BrainModule Hub(string id,string name,int count,Position position)=>new()
        {Id=id,Name=name,Kind=ModuleKind.Hub,NeuronCount=count,Inputs=8,Outputs=8,Position=position,Seed=42+count,
            PowerLaw=new() {MinDegree=2,MaxDegree=8,Exponent=2},WeightScale=.08f};
        BrainModule Sheet(string id,string name,int x,int y,Position position)=>new()
        {Id=id,Name=name,Kind=ModuleKind.Lattice,Dimensions=2,AxisSizes=[x,y],Neighbors=4,Inputs=8,Outputs=8,Position=position,WeightScale=.08f};
        var vision=Panel("vision-input","Video Input",Enumerable.Range(0,72).Select(i=>$"video.rgb.{i/3*16*3+i%3}").ToArray(),new(-24,-12,0));
        var touch=Panel("touch-input","Tactile",Enumerable.Range(0,16).Select(i=>$"tactile.{i}").ToArray(),new(-24,10,0),PanelKind.Tactile);
        var body=Panel("body-input","Body and Stereo Audio",["body.health","body.ammo","body.reserve","body.pain","body.pleasure","audio.left.rms","audio.right.rms"],new(-24,0,-10));
        var visual=Sheet("visual","Visual Cortex",8,6,new(-12,-12,0));
        var spatial=Sheet("spatial","Spatial Cortex",8,8,new(0,-10,0));
        var memory=Hub("memory","Temporal Memory",64,new(0,10,0));
        var central=Hub("central","Central Cortex",96,new(12,0,0));
        var motor=Hub("motor","Motor Cortex",32,new(24,0,0));
        var output=new BrainModule {Id="buttons",Name="Motor Buttons",Kind=ModuleKind.OutputPanel,Channels=8,ChannelBindings=["W","S","A","D","Q","E","F","X"],Position=new(34,0,0)};
        var clock=new BrainModule {Id="clock",Name="Memory Clock",Kind=ModuleKind.Clock,Frequency=2,Position=new(-10,10,-8)};
        var doc=new BrainDocument {Name="Sparse Recurrent Maze Brain",Modules=[vision,touch,body,visual,spatial,memory,central,motor,output,clock]};
        void Route(BrainModule a,BrainModule b,int fanOut=3,bool allSource=false,bool allDestination=true)
        {
            doc.Links.Add(new() {Id=a.Id+"-"+b.Id,Source=new() {ModuleId=a.Id,Roles=allSource?NeuronRole.All:NeuronRole.Output},
                Destination=new() {ModuleId=b.Id,Roles=allDestination?NeuronRole.All:NeuronRole.Input},Pattern=LinkPattern.FanOut,FanOut=fanOut,WeightScale=.08f});
        }
        Route(vision,visual,2,true);Route(touch,spatial,3,true);Route(body,memory,3,true);
        Route(visual,spatial,4,true);Route(spatial,memory,3);Route(memory,spatial,3);
        Route(spatial,central,4);Route(memory,central,4);Route(central,memory,3);
        Route(central,motor,4);Route(motor,central,2);Route(motor,output,3,true);Route(output,memory,2);
        Route(clock,memory,8);
        foreach(var (name,members) in new[]{("Video",new[]{visual.Id}),("Spatial",new[]{spatial.Id}),("Central",new[]{memory.Id,central.Id}),("Motor",new[]{motor.Id,output.Id})})
        {
            var reward=new BrainModule {Id="reward-"+name.ToLowerInvariant(),Name="Reward "+name,Kind=ModuleKind.Reward,Outputs=1,Position=new(12,(doc.RewardGroups.Count-1)*8,12)};
            doc.Modules.Add(reward);doc.RewardGroups.Add(new() {Name=name,ModuleIds=members.ToList(),RewardModuleId=reward.Id});
            foreach(string id in members)Route(doc.Modules.Single(m=>m.Id==id),reward,1,true);
        }
        return doc;
    }
}
