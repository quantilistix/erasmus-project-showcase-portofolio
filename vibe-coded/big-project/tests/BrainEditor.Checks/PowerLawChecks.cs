using System.Reflection;
using ArcadeLab;
using ArcadeLab.Editor;
using MYT.Kit.Gui;

internal static class PowerLawChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        int[] Sample(HubPowerLaw settings,int available=100,int count=100000)
        {
            var distribution=new PowerLawDistribution(settings,available);var random=new Random(123);
            return Enumerable.Range(0,count).Select(_=>distribution.Sample(random)).ToArray();
        }
        var settings=new HubPowerLaw {MinDegree=1,MaxDegree=8,Exponent=2};
        var samples=Sample(settings);double sum=Enumerable.Range(1,8).Sum(k=>Math.Pow(k,-2));
        check(Enumerable.Range(1,8).All(k=>Math.Abs(samples.Count(n=>n==k)/(double)samples.Length-Math.Pow(k,-2)/sum)<.005),"sampled degrees follow the discrete power-law probabilities");
        check(samples.SequenceEqual(Sample(settings)),"power-law sampling is deterministic for the seed");
        var uniform=Sample(new(){MinDegree=3,MaxDegree=9,Exponent=0});
        check(Enumerable.Range(3,7).All(k=>Math.Abs(uniform.Count(n=>n==k)/(double)uniform.Length-1d/7)<.005),"zero exponent gives uniform degrees within the configured bounds");
        check(Sample(new(){MinDegree=4,MaxDegree=4}).All(k=>k==4),"equal minimum and maximum gives a fixed degree");
        check(Sample(new(){MinDegree=50,MaxDegree=100},available:3,count:20).All(k=>k==3),"degrees are capped by the available distinct neighbors");
        check(Sample(new(),available:0,count:5).All(k=>k==0),"single-neuron hubs have no self-connections");
        check(Sample(new(){MinDegree=2,MaxDegree=100,Exponent=double.MaxValue},count:20).All(k=>k==2),"large finite exponents remain numerically stable");
        var concentrated=new HubPowerLaw {MinDegree=1,MaxDegree=256,Exponent=10};
        var profile=new PowerLawDistribution(concentrated,511).Assign(512,42);
        check(profile.Min()==1&&profile.Max()==256&&profile.Count(d=>d==1)==511,"concentrated finite profiles still represent both configured endpoints");
        concentrated.MaxDegree=128;
        var narrower=new PowerLawDistribution(concentrated,511).Assign(512,42);
        check(narrower.Max()==128&&profile.Sum()-narrower.Sum()==128,"maximum degree changes remain effective in concentrated tails");
        concentrated.MaxDegree=256;concentrated.Exponent=2;
        var spread=new PowerLawDistribution(concentrated,511).Assign(512,42);
        check(spread.Distinct().Count()>10&&spread.Count(d=>d==1)<profile.Count(d=>d==1),"lower exponent produces a visibly broader finite degree profile");
        check(spread.Order().SequenceEqual(new PowerLawDistribution(concentrated,511).Assign(512,99).Order()),"seed changes assignments without changing the degree histogram");
        var deterministic=new PowerLawDistribution(settings,100).Assign(1000,42);
        check(Enumerable.Range(1,8).All(k=>Math.Abs(deterministic.Count(d=>d<=k)/1000d-Enumerable.Range(1,k).Sum(d=>Math.Pow(d,-2))/sum)<=.0011),"finite percentile profile tracks the power-law cumulative probabilities");
        foreach(var invalid in new HubPowerLaw[]{new(){MinDegree=0},new(){MinDegree=8,MaxDegree=4},new(){Exponent=-1},new(){Exponent=double.NaN},new(){Exponent=double.PositiveInfinity}})
        {
            bool rejected=false;try{new PowerLawDistribution(invalid,100);}catch(InvalidDataException){rejected=true;}
            check(rejected,"invalid power-law settings rejected");
        }
        var module=new BrainModule {Kind=ModuleKind.Hub,NeuronCount=128,PowerLaw=new(){MinDegree=1,MaxDegree=24,Exponent=0}};
        var dense=Generation.Generate(module);module.PowerLaw.Exponent=3;var sparse=Generation.Generate(module);
        var degrees=new int[128];foreach(var edge in sparse.Edges)degrees[edge.Source]++;
        check(sparse.Edges.Count<dense.Edges.Count/3&&degrees.All(d=>d is >=1 and <=24),"changing the exponent materially changes generated hub connectivity within bounds");
        check(dense.Neurons.SequenceEqual(sparse.Neurons),"changing the power law preserves base hub neuron positions and identities");
        check(sparse.Edges.SequenceEqual(Generation.Generate(module).Edges),"hub generation remains reproducible");
        check(degrees.Min()==1&&degrees.Max()==24,"generated hub realizes its minimum and maximum outgoing degrees");
        bool tooDense=false;
        try{Generation.Generate(new(){Kind=ModuleKind.Hub,NeuronCount=20000,PowerLaw=new(){MinDegree=19999,MaxDegree=19999}});}catch(InvalidDataException e){tooDense=e.Message.Contains("edge limit");}
        check(tooDense,"oversized hub profiles are rejected before wiring the graph");
        module.DegreeDistribution=[new(){Degree=100,Fraction=0}];
        check(sparse.Edges.SequenceEqual(Generation.Generate(module).Edges),"legacy fraction bands no longer affect hub generation");
        var doc=new BrainDocument {Modules=[new(){Kind=ModuleKind.Clock},module]};
        var restored=BrainDocument.Parse(doc.Serialize());var bake=Baking.Bake(restored);
        check(restored.Modules[1].PowerLaw.Exponent==3&&restored.Modules[1].PowerLaw.MaxDegree==24&&bake.Model.ParameterCount==sparse.Edges.Count,"power-law settings persist through project serialization and baking");
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"hub-power-law.ppm"),(canvas,w,h)=>
        {
            var editable=new BrainModule {Kind=ModuleKind.Hub,NeuronCount=64,PowerLaw=new(){MaxDegree=16,Exponent=2}};
            var document=new BrainDocument {Modules=[editable]};var editor=new EditorApp(canvas,document,"unused.json");
            void Frame(float x=0,float y=0,bool down=false,string text="",GuiKeys keys=GuiKeys.None)
                =>editor.RenderPreview(w,h,1,new GuiInput {Cursor=new(x,y),MouseDown=down,Text=text,Keys=keys});
            Frame();int before=Generation.Generate(document.Modules[0]).Edges.Count;
            Frame(720,427,true);Frame(720,427);Frame(keys:GuiKeys.Home);Frame(keys:GuiKeys.Delete);Frame(text:"0");Frame(keys:GuiKeys.Enter);
            var preview=(ModuleGraph)typeof(EditorApp).GetField("preview",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
            check(document.Modules[0].PowerLaw.Exponent==0&&preview.Edges.Count>before,"inline power-law exponent edits rebuild the visible preview");
        });
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"hub-distribution-editor.ppm"),(canvas,w,h)=>
        {
            var editable=new BrainModule {Kind=ModuleKind.Hub,NeuronCount=64,PowerLaw=new(){MinDegree=1,MaxDegree=32,Exponent=10}};
            var document=new BrainDocument {Modules=[editable]};var editor=new EditorApp(canvas,document,"unused.json");
            void Frame(float x=0,float y=0,bool down=false,string text="",GuiKeys keys=GuiKeys.None)
                =>editor.RenderPreview(w,h,1,new GuiInput {Cursor=new(x,y),MouseDown=down,Text=text,Keys=keys});
            void Replace(float x,float y,int characters,string value)
            {
                Frame(x,y,true);Frame(x,y);Frame(keys:GuiKeys.Home);
                for(int i=0;i<characters;i++)Frame(keys:GuiKeys.Delete);
                Frame(text:value);Frame(keys:GuiKeys.Enter);
            }
            Frame();Frame(980,160,true);Frame(980,160); // Distribution tab.
            Replace(720,316,2,"64");
            check(document.Modules[0].PowerLaw.MaxDegree==32,"distribution editor rejects limits above available neighbors");
            Frame(keys:GuiKeys.Escape);Replace(720,316,2,"48");
            check(document.Modules[0].PowerLaw.MaxDegree==48&&Generation.Generate(document.Modules[0]).Edges.Count==111
                &&(string)typeof(EditorApp).GetField("status",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)! =="UPDATED HUB DISTRIBUTION",
                "distribution editor applies maximum changes even at exponent ten and clears stale validation errors");
            Replace(720,218,2,"8");
            check(document.Modules[0].NeuronCount==8&&document.Modules[0].PowerLaw.MaxDegree==7,"reducing hub size adjusts distribution bounds in the same edit");
            var graph=(ModuleGraph)typeof(EditorApp).GetField("preview",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
            var histogram=(int[])typeof(EditorApp).GetField("hubHistogram",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor)!;
            check(histogram.Sum()==graph.Neurons.Count&&histogram.Select((n,k)=>n*k).Sum()==graph.Edges.Count,"distribution histogram reports actual generated neuron and edge counts");
            Replace(720,267,1,"7");
            Frame(720,365,true);Frame(720,365);
            check(typeof(EditorApp).GetField("inlineId",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(editor) is null,"exponent field is inactive when the degree range is fixed");
            Replace(720,267,1,"1");Replace(720,218,1,"64");Replace(720,316,1,"32");
        });
    }
}
