using ArcadeLab;
using ArcadeLab.Editor;

internal static class ModularBrainChecks
{
    internal static void Run(Action<bool,string> check,string output)
    {
        var doc=MazeBrainTemplate.Create();var brain=Baking.Bake(doc);
        check(doc.Modules.All(m=>m.Kind!=ModuleKind.Layered)&&doc.Modules.Any(m=>m.Kind==ModuleKind.Lattice)&&doc.Modules.Any(m=>m.Kind==ModuleKind.Hub),"maze template uses recurrent lattices and hubs rather than dense layers");
        check(brain.Model.NormalCount<=512&&brain.Model.Weights.Length<=4096,"modular maze stays within a measured realtime topology budget");
        bool HasRoute(string a,string b)=>brain.Model.Topology.Edges.Any(e=>brain.Manifest.Neurons[e.Source].ModuleId==a&&brain.Manifest.Neurons[e.Destination].ModuleId==b);
        check(HasRoute("spatial","memory")&&HasRoute("memory","spatial")&&HasRoute("central","memory")&&HasRoute("memory","central"),"spatial memory and decision loops contain actual reciprocal neural connections");
        check(HasRoute("motor","central")&&HasRoute("buttons","memory"),"motor feedback reaches decision and temporal-memory circuits");
        var rewards=new MazeCortexRewards(brain);
        check(rewards.RewardFactorOverrides.ContainsKey("Video")&&!rewards.RewardFactorOverrides.ContainsKey("Spatial"),"visual reward isolation leaves spatial learning enabled");
        using var session=new MazeSession(new() {AdaptiveControl=true,PropagationSteps=4},true,"cpu",bakedBrain:brain);
        for(int i=0;i<32;i++)session.Step();
        var hidden=brain.Manifest.Neurons.Select((n,i)=>(n,i)).Where(x=>x.n.Role==NeuronRole.Hidden).Select(x=>x.i).ToArray();
        check(hidden.Any(i=>session.NormalActivities[i]>0)&&session.NormalActivities.All(float.IsFinite),"modular hidden circuits are active and finite during training");
        var timer=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<120;i++)session.Step();
        timer.Stop();Console.WriteLine($"Modular CPU training: {brain.Model.NormalCount} neurons, {brain.Model.Weights.Length} edges, {timer.Elapsed.TotalMilliseconds/120:F2} ms/frame (simulation and learning; rendering excluded)");
        Viewer.CaptureMaze(session,1100,740,Path.Combine(output,"modular-maze.ppm"));
    }
}
