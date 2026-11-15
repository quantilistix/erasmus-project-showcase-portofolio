using System.Numerics;
using System.Text.Json.Nodes;
using ArcadeLab.Editor;

internal static class GroupChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        var source=new BrainModule {Name="Source",LayerSizes=[4,12,20]};
        var first=new NeuronGroup {Name="output0",Kind=GroupKind.Output,Criterion=GroupCriterion.Layer,Layer=-1,Count=10};
        var second=new NeuronGroup {Name="output1",Kind=GroupKind.Output,Criterion=GroupCriterion.Layer,Layer=-1,Start=10,Count=10};
        source.Groups.AddRange([first,second]);
        var generated=Generation.Generate(source);var groups=NeuronGroups.Resolve(source,generated.Neurons);
        check(groups[first.Id].Length==10&&groups[second.Id].Length==10&&!groups[first.Id].Intersect(groups[second.Id]).Any(),"last-layer groups split outputs without mixing");
        var box=new NeuronGroup {Name="local box",Criterion=GroupCriterion.SpatialBox,Min=new(-1,-1,-1),Max=new(1,1,1)};
        var explicitGroup=new NeuronGroup {Name="chosen IDs",Criterion=GroupCriterion.ExplicitIds,NeuronIds=[generated.Neurons[2].Id,generated.Neurons[5].Id]};
        source.Groups.AddRange([box,explicitGroup]);groups=NeuronGroups.Resolve(source,generated.Neurons);
        check(groups[box.Id].Length==4&&groups[explicitGroup.Id].SequenceEqual([2,5]),"local box and explicit ID criteria resolve exact memberships");
        var targetA=new BrainModule {Name="A",LayerSizes=[10,2],Position=new(20,0,0)};
        var targetB=new BrainModule {Name="B",LayerSizes=[10,2],Position=new(20,20,0)};
        var clock=new BrainModule {Kind=ModuleKind.Clock};
        var linkA=new BrainLink {Source=new(){ModuleId=source.Id,GroupIds=[first.Id]},Destination=new(){ModuleId=targetA.Id,GroupIds=["input"]},Pattern=LinkPattern.OneToOne,WeightKind=LinkWeightKind.Constant,Weight=.7f};
        var linkB=new BrainLink {Source=new(){ModuleId=source.Id,GroupIds=[second.Id]},Destination=new(){ModuleId=targetB.Id,GroupIds=["input"]}};
        var document=new BrainDocument {Modules=[source,targetA,targetB,clock],Links=[linkA,linkB]};
        var compiled=Baking.Compile(document);
        var edgesA=compiled.Edges.Where((_,i)=>compiled.EdgeLinkIds[i]==linkA.Id).ToArray();
        var edgesB=compiled.Edges.Where((_,i)=>compiled.EdgeLinkIds[i]==linkB.Id).ToArray();
        check(edgesA.Length==10&&edgesA.All(e=>e.Weight==.7f)&&edgesA.Select(e=>e.Source).Distinct().Count()==10,"one-to-one groups pair once with constant weights");
        check(edgesB.Length==100&&!edgesA.Select(e=>e.Source).Intersect(edgesB.Select(e=>e.Source)).Any(),"separate module links use disjoint source neurons");
        linkA.Source.GroupIds=[first.Id,first.Id,second.Id];linkA.Pattern=LinkPattern.AllToAll;
        compiled=Baking.Compile(document);
        check(compiled.EdgeLinkIds.Count(id=>id==linkA.Id)==200,"multiple endpoint groups form a deduplicated union");
        linkA.Pattern=LinkPattern.FanOut;linkA.FanOut=3;
        compiled=Baking.Compile(document);edgesA=compiled.Edges.Where((_,i)=>compiled.EdgeLinkIds[i]==linkA.Id).ToArray();
        check(edgesA.Length==60&&edgesA.GroupBy(e=>e.Source).All(g=>g.Count()==3),"fan-out caps targets per selected source");
        linkA.Pattern=LinkPattern.Nearest;linkA.FanOut=1;
        compiled=Baking.Compile(document);edgesA=compiled.Edges.Where((_,i)=>compiled.EdgeLinkIds[i]==linkA.Id).ToArray();
        check(edgesA.Length==20&&edgesA.All(e=>compiled.ModuleIndices[targetA.Id].Take(10).All(i=>Vector3.DistanceSquared(ModuleTransform.Vector(compiled.Neurons[e.Source].Position),ModuleTransform.Vector(compiled.Neurons[e.Destination].Position))<=Vector3.DistanceSquared(ModuleTransform.Vector(compiled.Neurons[e.Source].Position),ModuleTransform.Vector(compiled.Neurons[i].Position))+.0001f)),"nearest links connect to nearest selected targets");
        linkA.Source.GroupIds=["missing"];
        var tolerant=Baking.Compile(document,false);bool rejected=false;
        try{Baking.Bake(document);}catch(InvalidDataException){rejected=true;}
        check(rejected&&tolerant.LinkErrors.ContainsKey(linkA.Id)&&!tolerant.EdgeLinkIds.Contains(linkA.Id),"missing groups reject baking and leave no partial preview link");
        linkA.Source.GroupIds=[first.Id];linkA.Pattern=LinkPattern.OneToOne;
        linkA.Destination.Percent=50;rejected=false;
        try{Baking.Compile(document);}catch(InvalidDataException){rejected=true;}
        check(rejected,"one-to-one rejects unequal selected group sizes");linkA.Destination.Percent=100;
        var scene=new LinkScene(document);var oldEdges=scene.Graph.Edges;
        source.Rotation=new(22,37,-13);source.Position=new(3,5,-2);scene.SynchronizePositions();
        var regenerated=Generation.Generate(source);
        check(scene.Modules[0].Neurons.Select((i,n)=>Vector3.Distance(ModuleTransform.Vector(scene.Graph.Neurons[i].Position),ModuleTransform.Vector(regenerated.Neurons[n].Position))).All(d=>d<.0001f)&&ReferenceEquals(oldEdges,scene.Graph.Edges),"cached transforms agree with regeneration without rebuilding weights");
        var transformedGroups=NeuronGroups.Resolve(source,regenerated.Neurons);
        check(groups[first.Id].SequenceEqual(transformedGroups[first.Id]),"module rotation and movement preserve group membership");
        check(groups[box.Id].SequenceEqual(transformedGroups[box.Id]),"spatial groups use local coordinates after module transforms");
        foreach(var angles in new[]{new Position(20,30,40),new Position(-72,85,130),new Position(130,-45,-170)})
        {
            var q=ModuleTransform.Rotation(angles);var roundtrip=ModuleTransform.Rotation(ModuleTransform.Euler(q));
            check(Math.Abs(Quaternion.Dot(q,roundtrip))>.99999f,"Euler rotation round trip "+angles);
        }
        var hub=Generation.Generate(new BrainModule {Kind=ModuleKind.Hub,Inputs=0,Hidden=64,Outputs=0});
        Vector3 p0=ModuleTransform.Vector(hub.Neurons[0].Position),p1=ModuleTransform.Vector(hub.Neurons[1].Position),p4=ModuleTransform.Vector(hub.Neurons[4].Position),p16=ModuleTransform.Vector(hub.Neurons[16].Position);
        check(Math.Abs(Vector3.Dot(Vector3.Cross(p1-p0,p4-p0),p16-p0))>.9f,"hub layout occupies 3D volume");
        check(Generation.Generate(new BrainModule {Kind=ModuleKind.Hub,NeuronCount=50}).Neurons.Count==50,"single neuron count controls hub size independently of legacy role counts");
        source.Rotation=default;source.Position=default;
        var aligned=Generation.Generate(source);source.Seed++;
        var reseeded=Generation.Generate(source);
        check(aligned.Neurons.Select(n=>n.Position).SequenceEqual(reseeded.Neurons.Select(n=>n.Position))&&aligned.Neurons.All(n=>n.Position.X==n.Layer*4),"layers have stable parallel YZ planes across seeds");
        source.LayerSizes=[4,5,6,7,20];
        var five=Generation.Generate(source);
        check(five.Neurons.Select(n=>n.Layer).Distinct().Count()==5&&NeuronGroups.Resolve(source,five.Neurons)[first.Id].Length==10,"changing layer count preserves last-layer criteria");
        source.Groups.Remove(explicitGroup);
        source.Modifiers.Add(new(){Dimensions=1,AxisSizes=[2]});
        var array=Generation.Generate(source);
        check(NeuronGroups.Resolve(source,array.Neurons)["output"].Length==40&&NeuronGroups.Resolve(source,array.Neurons)[first.Id].Length==10,"array groups retain layer metadata with deterministic range selection");
        source.Modifiers.Clear();
        source.Groups.Add(new(){Name="internal readout",Kind=GroupKind.Output,Criterion=GroupCriterion.Layer,Layer=1,Count=2});
        var baked=Baking.Bake(document);string path=Path.Combine(output,"group-bake-"+Guid.NewGuid().ToString("N"));baked.Save(path);
        var loaded=BakedBrain.Load(path);var loadedSource=BrainDocument.Parse(loaded.Manifest.SourceDocument);
        check(loadedSource.Modules[0].Groups.Any(g=>g.Name=="output0")&&loadedSource.Links[0].Source.GroupIds.SequenceEqual([first.Id])&&loaded.Model.Topology.Outputs.Length==26,"group definitions, endpoint routes and custom readouts survive bake/save/load");
        var legacy=JsonNode.Parse(BrainDocument.Example().Serialize())!;
        foreach(var module in legacy["Modules"]!.AsArray()){module!.AsObject().Remove("Groups");module.AsObject().Remove("Rotation");}
        foreach(var link in legacy["Links"]!.AsArray()){link!["Source"]!.AsObject().Remove("GroupIds");link["Destination"]!.AsObject().Remove("GroupIds");}
        var restored=BrainDocument.Parse(legacy.ToJsonString());
        check(restored.Modules.All(m=>m.Groups.Count==3)&&Baking.Bake(restored).Model.ParameterCount>0,"legacy documents receive default groups and remain bakeable");
        var camera=PerspectiveCamera.Create(Vector3.Zero,new(300,300),40,1,500,10,-.4f,.4f);
        camera.TryProject(new(2,0,0),out var point);
        check(TransformGizmo.AxisParameter(camera,new(point.X,point.Y),Vector3.Zero,Vector3.UnitX,out float along)&&Math.Abs(along-2)<.001f,"move gizmo ray resolves axis displacement");
        camera.TryProject(new(2,0,0),out var start);camera.TryProject(new(0,2,0),out var end);
        check(TransformGizmo.RingVector(camera,new(start.X,start.Y),Vector3.Zero,Vector3.UnitZ,out var before)&&TransformGizmo.RingVector(camera,new(end.X,end.Y),Vector3.Zero,Vector3.UnitZ,out var after)&&Math.Abs(TransformGizmo.SignedAngle(before,after,Vector3.UnitZ)-MathF.PI/2)<.001f,"rotation gizmo ray resolves a quarter-turn");
    }
}
