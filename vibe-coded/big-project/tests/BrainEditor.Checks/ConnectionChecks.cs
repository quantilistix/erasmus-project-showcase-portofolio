using ArcadeLab.Editor;

internal static class ConnectionChecks
{
    public static void Run(Action<bool,string> check)
    {
        // More sources than the old global sample can represent: it necessarily hid neurons.
        var input=new BrainModule {Kind=ModuleKind.InputPanel,Channels=8192};
        var output=new BrainModule {Kind=ModuleKind.OutputPanel,Channels=2};
        var link=new BrainLink {Source=new(){ModuleId=input.Id},Destination=new(){ModuleId=output.Id},Pattern=LinkPattern.AllToAll};
        var document=new BrainDocument {Modules=[input,output],Links=[link]};
        var scene=new LinkScene(document);var graph=scene.Graph;
        var expected=graph.ModuleIndices[input.Id].SelectMany(a=>graph.ModuleIndices[output.Id].Select(b=>(a,b))).ToHashSet();
        check(graph.Edges.Select(e=>(e.Source,e.Destination)).ToHashSet().SetEquals(expected),
            "100% all-to-all compiles every source/destination pair above the former preview budget");
        var baked=Baking.Bake(document);
        check(baked.Model.Topology.Edges.Length==expected.Count,"all-to-all baking retains every compiled connection");

    }
}
