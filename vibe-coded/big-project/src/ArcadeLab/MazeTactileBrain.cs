using ArcadeLab.Editor;
using NeuralGraph;
using System.Text.Json;

namespace ArcadeLab;

/// <summary>Expand the old scalar touch port into a real ring without discarding learned edges.</summary>
public static class MazeTactileBrain
{
    public static BrainRuntimeState UpgradeState(BakedBrain original,BakedBrain upgraded,BrainRuntimeState state)
    {
        if(state.Network.Normal.Length==upgraded.Model.NormalCount)return state;
        if(state.Network.Normal.Length!=original.Model.NormalCount||upgraded.Model.NormalCount-original.Model.NormalCount!=15)
            throw new InvalidDataException("Checkpoint tactile migration does not match network state.");
        var module=original.Manifest.Modules.Single(m=>original.Manifest.Neurons.Count(n=>n.ModuleId==m.Id)==1&&upgraded.Manifest.Neurons.Count(n=>n.ModuleId==m.Id)==16);
        int port=original.Manifest.Neurons.FindIndex(n=>n.ModuleId==module.Id);
        var normal=new float[upgraded.Model.NormalCount];var errors=new float[2*normal.Length];
        for(int i=0;i<original.Model.NormalCount;i++)if(i!=port)
        {
            int mapped=i<port?i:i+15;normal[mapped]=state.Network.Normal[i];
            errors[2*mapped]=state.Network.ErrorRates[2*i];errors[2*mapped+1]=state.Network.ErrorRates[2*i+1];
        }
        return state with {Network=new(normal,errors,state.Network.Ticks)};
    }
    public static BakedBrain Upgrade(BakedBrain source)
    {
        var old=source.Manifest;
        var module=old.Modules.SingleOrDefault(m=>m.Kind==ModuleKind.InputPanel&&(m.Name.Equals("INPUTPANEL",StringComparison.OrdinalIgnoreCase)||m.Panel==PanelKind.Tactile||m.Name.Contains("tactile",StringComparison.OrdinalIgnoreCase))
            &&old.Neurons.Count(n=>n.ModuleId==m.Id)==1);
        if(module is null)return source;
        var manifest=JsonSerializer.Deserialize<BakedManifest>(JsonSerializer.Serialize(old,BrainDocument.Json),BrainDocument.Json)!;
        module=manifest.Modules.Single(m=>m.Id==module.Id);module.Panel=PanelKind.Tactile;module.Channels=MazeGame.TactileCount;
        module.ChannelBindings=Enumerable.Range(0,MazeGame.TactileCount).Select(i=>$"tactile.{i}").ToArray();
        int port=old.Neurons.FindIndex(n=>n.ModuleId==module.Id),extra=MazeGame.TactileCount-1;
        int Map(int i)=>i<=port?i:i+extra;
        var neurons=new List<GeneratedNeuron>();
        for(int i=0;i<old.Neurons.Count;i++)
        {
            if(i!=port){neurons.Add(old.Neurons[i]);continue;}
            for(int direction=0;direction<MazeGame.TactileCount;direction++)
            {
                float angle=2*MathF.PI*direction/MazeGame.TactileCount;var local=new Position(MathF.Cos(angle),MathF.Sin(angle),0);
                neurons.Add(old.Neurons[i] with {Id=direction.ToString(),Position=ModuleTransform.Apply(module,local),LocalPosition=local});
            }
        }
        var edges=new List<Edge>();var weights=new List<float>();var plasticity=new List<float>();
        for(int e=0;e<source.Model.Weights.Length;e++)
        {
            var edge=source.Model.Topology.Edges[e];int copies=edge.Source==port?MazeGame.TactileCount:1;
            for(int i=0;i<copies;i++)
            {
                edges.Add(new(Map(edge.Source)+i,Map(edge.Destination)));
                weights.Add(source.Model.Weights[e]/copies);plasticity.Add(source.Model.Plasticity[e]);
            }
        }
        manifest.Neurons=neurons;
        var topology=source.Model.Topology;var model=new PredictiveCodingModel(new(topology.InputCount+extra,topology.NodeCount+extra,topology.Steps,topology.Outputs.Select(Map).ToArray(),edges.ToArray()));
        weights.CopyTo(model.Weights);plasticity.CopyTo(model.Plasticity);if(source.Model.IsFrozen)model.Freeze();
        return new(model,manifest);
    }
}
