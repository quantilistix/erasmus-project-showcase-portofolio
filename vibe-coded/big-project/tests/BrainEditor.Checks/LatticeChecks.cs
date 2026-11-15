using System.Numerics;
using ArcadeLab;
using ArcadeLab.Editor;
using MYT.Kit.Gui;

internal static class LatticeChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        var sized=new BrainModule {Kind=ModuleKind.Lattice,Dimensions=3,AxisSizes=[3,4,5],NeuronCount=2,Neighbors=6};
        var graph=Generation.Generate(sized);
        check(graph.Neurons.Count==60,"lattice axis product overrides the legacy neuron count");
        check(graph.Neurons.Select(n=>n.Position.X).Distinct().Count()==3&&graph.Neurons.Select(n=>n.Position.Y).Distinct().Count()==4&&graph.Neurons.Select(n=>n.Position.Z).Distinct().Count()==5,"cubic lattice fills each dimension's requested extent");
        check(NeuronGroups.Resolve(sized,graph.Neurons)["output"].Length==4,"resized lattice retains a default output group");
        foreach(var (dimensions,neighbors,radius) in new[]{(1,2,1f),(2,3,1f),(2,4,1f),(2,6,1f),(2,8,MathF.Sqrt(2)),(3,4,MathF.Sqrt(3)/4),(3,6,1f),(3,8,MathF.Sqrt(3)/2),(3,12,MathF.Sqrt(.5f))})
        {
            var module=new BrainModule {Kind=ModuleKind.Lattice,Dimensions=dimensions,AxisSizes=Enumerable.Repeat(6,dimensions).ToArray(),Neighbors=neighbors};
            var generated=Generation.Generate(module);var degree=new int[generated.Neurons.Count];
            foreach(var edge in generated.Edges)degree[edge.Source]++;
            check(degree.Max()==neighbors&&degree.Min()<neighbors&&generated.Edges.All(e=>Vector3.Distance(ModuleTransform.Vector(generated.Neurons[e.Source].Position),ModuleTransform.Vector(generated.Neurons[e.Destination].Position))<=radius+.0001f),
                LatticeGeometry.Name(dimensions,neighbors)+" has the expected interior coordination and open boundaries");
        }
        var square=new BrainModule {Kind=ModuleKind.Lattice,AxisSizes=[6,6],Neighbors=4};
        var squarePoints=Generation.Generate(square).Neurons.Select(n=>n.Position).ToArray();
        square.Neighbors=6;var triangular=Generation.Generate(square);
        check(!squarePoints.SequenceEqual(triangular.Neurons.Select(n=>n.Position)),"changing neighbors changes lattice neuron positions");
        var legacy=new BrainModule {Kind=ModuleKind.Lattice,NeuronCount=35};
        check(LatticeGeometry.Sizes(legacy).SequenceEqual([7,5])&&Generation.Generate(legacy).Neurons.Count==35,"legacy count-only lattices infer complete axes without changing their count");
        foreach(var sizes in new[]{new[]{0,4},new[]{-1,4},new[]{int.MaxValue,int.MaxValue},new[]{201,100}})
        {
            bool rejected=false;try{Generation.Generate(new(){Kind=ModuleKind.Lattice,AxisSizes=sizes});}catch(InvalidDataException){rejected=true;}
            check(rejected,"invalid or oversized lattice sizes rejected: "+string.Join(",",sizes));
        }
        bool mismatch=false;try{Generation.Generate(new(){Kind=ModuleKind.Lattice,Dimensions=3,AxisSizes=[4,4]});}catch(InvalidDataException){mismatch=true;}
        check(mismatch,"lattice requires exactly one size per dimension");
        var document=new BrainDocument {Modules=[sized,new(){Kind=ModuleKind.Clock}]};
        var restored=BrainDocument.Parse(document.Serialize());var baked=Baking.Bake(restored);
        check(restored.Modules[0].AxisSizes.SequenceEqual([3,4,5])&&baked.Manifest.Neurons.Count(n=>n.ModuleId==sized.Id)==60,"dimension sizes survive project round trip and baking");
        var large=Generation.Generate(new BrainModule {Kind=ModuleKind.Lattice,Dimensions=3,AxisSizes=[20,20,20],Neighbors=12});
        check(large.Neurons.Count==8000&&large.Edges.Count<=96000,"large coordinated lattices generate a bounded local neighborhood");
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"lattice-axis-editor.ppm"),(canvas,w,h)=>
        {
            var module=new BrainModule {Kind=ModuleKind.Lattice,AxisSizes=[3,4],Neighbors=4};
            var doc=new BrainDocument {Modules=[module]};var editor=new EditorApp(canvas,doc,"unused.json");
            void Frame(float x=0,float y=0,bool down=false,string text="",GuiKeys keys=GuiKeys.None)
                =>editor.RenderPreview(w,h,1,new GuiInput {Cursor=new(x,y),MouseDown=down,Text=text,Keys=keys});
            void Replace(float x,float y,string value)
            {
                Frame(x,y,true);Frame(x,y);Frame(keys:GuiKeys.Home);Frame(keys:GuiKeys.Delete);Frame(text:value);Frame(keys:GuiKeys.Enter);
            }
            Frame();Replace(720,319,"5"); // Size X, after Name and Dimensions.
            check(doc.Modules[0].AxisSizes.SequenceEqual([5,4])&&Generation.Generate(doc.Modules[0]).Neurons.Count==20,"editing an individual axis size changes generated neuron count");
            Replace(720,265,"3");
            check(doc.Modules[0].Dimensions==3&&doc.Modules[0].AxisSizes.SequenceEqual([5,4,2]),"adding a lattice dimension creates its size field");
            Replace(720,427,"3"); // Size Z.
            check(doc.Modules[0].AxisSizes.SequenceEqual([5,4,3])&&Generation.Generate(doc.Modules[0]).Neurons.Count==60,"third dimension size is editable inline");
            Replace(720,265,"2");
            check(doc.Modules[0].AxisSizes.SequenceEqual([5,4]),"removing a dimension removes its size in the same edit");
        });
    }
}
