using System.Numerics;

namespace ArcadeLab.Editor;

public sealed class SceneModule
{
    public BrainModule Module {get;}
    public int[] Neurons {get;}
    public Vector3 Min {get;private set;}
    public Vector3 Max {get;private set;}
    private Position position,rotation;
    public Vector3 Center=>(Min+Max)*.5f;
    public SceneModule(BrainModule module,int[] indices,IReadOnlyList<GeneratedNeuron> neurons)
    {
        Module=module;Neurons=indices;position=module.Position;rotation=module.Rotation;
        Min=new(float.PositiveInfinity);Max=new(float.NegativeInfinity);
        foreach(int i in indices){var p=neurons[i].Position;var v=new Vector3(p.X,p.Y,p.Z);Min=Vector3.Min(Min,v);Max=Vector3.Max(Max,v);}
        float padding=Math.Max(.45f,(Max-Min).Length()*.025f);
        Min-=new Vector3(padding);Max+=new Vector3(padding);
    }
    internal void Synchronize(List<GeneratedNeuron> neurons)
    {
        if(Module.Position==position&&Module.Rotation==rotation)return;
        Min=new(float.PositiveInfinity);Max=new(float.NegativeInfinity);
        foreach(int i in Neurons)
        {
            var n=neurons[i];var p=ModuleTransform.Apply(Module,n.LocalPosition);
            neurons[i]=n with {Position=p};var v=ModuleTransform.Vector(p);Min=Vector3.Min(Min,v);Max=Vector3.Max(Max,v);
        }
        float padding=Math.Max(.45f,(Max-Min).Length()*.025f);Min-=new Vector3(padding);Max+=new Vector3(padding);
        position=Module.Position;rotation=Module.Rotation;
    }

    public Vector3 Corner(int i)=>new((i&1)==0?Min.X:Max.X,(i&2)==0?Min.Y:Max.Y,(i&4)==0?Min.Z:Max.Z);
    public bool IntersectRay(PerspectiveCamera camera,Vector2 cursor,out float distance)
    {
        var start=camera.Unproject(cursor,camera.Near);var end=camera.Unproject(cursor,camera.Far);
        var direction=Vector3.Normalize(end-start);float near=0,far=float.PositiveInfinity;
        for(int axis=0;axis<3;axis++)
        {
            float s=axis==0?start.X:axis==1?start.Y:start.Z,d=axis==0?direction.X:axis==1?direction.Y:direction.Z;
            float min=axis==0?Min.X:axis==1?Min.Y:Min.Z,max=axis==0?Max.X:axis==1?Max.Y:Max.Z;
            if(Math.Abs(d)<.000001f){if(s<min||s>max){distance=0;return false;}continue;}
            float a=(min-s)/d,b=(max-s)/d;if(a>b)(a,b)=(b,a);
            near=Math.Max(near,a);far=Math.Min(far,b);
            if(far<near){distance=0;return false;}
        }
        distance=near;return true;
    }
}

public sealed class LinkScene
{
    public CompiledBrainGraph Graph {get;}
    public SceneModule[] Modules {get;}
    public Dictionary<string,SceneModule> ById {get;}
    public HashSet<string> NonemptyLinks {get;}
    private readonly Dictionary<LinkEndpoint,int[]> ports=[];
    public LinkScene(BrainDocument document)
    {
        Graph=Baking.Compile(document,strictLinks:false);
        Modules=document.Modules.Select(m=>new SceneModule(m,Graph.ModuleIndices[m.Id],Graph.Neurons)).ToArray();
        ById=Modules.ToDictionary(m=>m.Module.Id);
        NonemptyLinks=Graph.EdgeLinkIds.Where(id=>id is not null).Select(id=>id!).ToHashSet();
        foreach(var link in document.Links)foreach(var endpoint in new[]{link.Source,link.Destination})
        {
            if(!ById.TryGetValue(endpoint.ModuleId,out var module))continue;
            var ns=module.Neurons.Select(i=>Graph.Neurons[i]).ToArray();
            try {ports[endpoint]=NeuronGroups.Select(endpoint,module.Module,ns,NeuronGroups.Resolve(module.Module,ns)).Select(i=>module.Neurons[i]).ToArray();}
            catch(InvalidDataException){ports[endpoint]=[];}
        }
    }
    public int[] PortIndices(LinkEndpoint endpoint)=>ports.GetValueOrDefault(endpoint)??[];
    public Vector3 Port(LinkEndpoint endpoint)
    {
        if(!ports.TryGetValue(endpoint,out var indices)||indices.Length==0)return ById[endpoint.ModuleId].Center;
        var center=Vector3.Zero;foreach(int i in indices)center+=ModuleTransform.Vector(Graph.Neurons[i].Position);
        return center/indices.Length;
    }
    public void SynchronizePositions(){foreach(var module in Modules)module.Synchronize(Graph.Neurons);}
}
