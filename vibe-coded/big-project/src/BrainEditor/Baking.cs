using NeuralGraph;
using System.Text.Json;
namespace ArcadeLab.Editor;

public sealed class BakedManifest
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public List<GeneratedNeuron> Neurons { get; set; } = [];
    public List<BrainModule> Modules { get; set; } = [];
    public List<RewardGroup> RewardGroups { get; set; } = [];
    public string SourceDocument { get; set; } = "";
}
public sealed record BakedBrain(PredictiveCodingModel Model,BakedManifest Manifest)
{
    public void Save(string directory)
    {
        string full=Path.GetFullPath(directory);Generation.Require(!Directory.Exists(full)&&!File.Exists(full),"Choose a new directory for the baked network.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);string staging=full+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);Model.Save(Path.Combine(staging,"network.pcn"));
            File.WriteAllText(Path.Combine(staging,"manifest.json"),JsonSerializer.Serialize(Manifest,BrainDocument.Json));
            Directory.Move(staging,full);
        }
        finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
    }
    public static BakedBrain Load(string directory)
    {
        var model=PredictiveCodingModel.Load(Path.Combine(directory,"network.pcn"));
        var manifest=JsonSerializer.Deserialize<BakedManifest>(File.ReadAllText(Path.Combine(directory,"manifest.json")),BrainDocument.Json)??throw new InvalidDataException("Missing manifest.");
        Generation.Require(manifest.Version==1&&manifest.Neurons.Count==model.NormalCount,"Baked manifest does not match model.");
        for(int i=0;i<model.NormalCount;i++)
        {
            var n=manifest.Neurons[i];Generation.Require(n.Boundary==(i<model.Topology.InputCount),"Manifest boundary order mismatch.");
            Generation.Require(float.IsFinite(n.ActivationCap)&&n.ActivationCap>0,"Invalid activation cap.");
        }
        return new(model,manifest);
    }
}
public sealed record CompiledBrainGraph(List<GeneratedNeuron> Neurons,List<GeneratedEdge> Edges,
    Dictionary<string,int[]> ModuleIndices,List<string?> EdgeLinkIds,Dictionary<string,string> LinkErrors);
public static class Baking
{
    /// <summary>Generate the exact pre-boundary-sort topology shared by baking and Link preview.
    /// In-progress previews may collect invalid link errors while retaining valid module geometry.</summary>
    public static CompiledBrainGraph Compile(BrainDocument doc,bool strictLinks=true)
    {
        Generation.Require(doc.Modules.Count>0&&doc.Modules.Select(m=>m.Id).Distinct().Count()==doc.Modules.Count,"Require modules with unique IDs.");
        Generation.Require(doc.Links.Select(l=>l.Id).Distinct().Count()==doc.Links.Count,"Duplicate link IDs.");
        var ns=new List<GeneratedNeuron>();var es=new List<GeneratedEdge>();var modules=new Dictionary<string,int[]>();
        var edgeLinks=new List<string?>();var errors=new Dictionary<string,string>();
        var groupIndices=new Dictionary<string,Dictionary<string,int[]>>();var generated=new Dictionary<string,List<GeneratedNeuron>>();
        foreach(var m in doc.Modules)
        {
            Generation.Require(!string.IsNullOrWhiteSpace(m.Id),"Empty module ID.");var g=Generation.Generate(m);int offset=ns.Count;
            Generation.Require(ns.Count+g.Neurons.Count<=Generation.MaxNeurons&&es.Count+g.Edges.Count<=Generation.MaxEdges,"Brain exceeds generation limits.");
            modules[m.Id]=Enumerable.Range(offset,g.Neurons.Count).ToArray();ns.AddRange(g.Neurons);
            generated[m.Id]=g.Neurons;groupIndices[m.Id]=NeuronGroups.Resolve(m,g.Neurons);
            es.AddRange(g.Edges.Select(e=>e with { Source=e.Source+offset,Destination=e.Destination+offset }));
            edgeLinks.AddRange(Enumerable.Repeat<string?>(null,g.Edges.Count));
        }
        var degree=new int[ns.Count];foreach(var e in es){degree[e.Source]++;degree[e.Destination]++;}
        var unique=es.Select(e=>(e.Source,e.Destination)).ToHashSet();
        foreach(var link in doc.Links)
        {
            int start=es.Count;
            try
            {
                var rng=new Random(link.Seed);Generation.Percent(link.CrossPercent);Generation.Finite(link.WeightScale);Generation.Finite(link.Plasticity);
                Generation.Require(link.Plasticity>=0&&link.WeightScale>=0,"Invalid link weight/plasticity.");
                Generation.Finite(link.Weight);Generation.Require(Enum.IsDefined(link.Pattern)&&Enum.IsDefined(link.WeightKind)&&link.FanOut>0,"Invalid link pattern or fan-out.");
                int[] Select(LinkEndpoint ep)
                {
                    Generation.Require(modules.ContainsKey(ep.ModuleId),"Link refers to a missing module.");Generation.Percent(ep.Percent);
                    Generation.Require((ep.Scope!=EndpointScope.LegacyRoles||(ep.Roles!=0&&(ep.Roles&~NeuronRole.All)==0))&&Enum.IsDefined(ep.Selection)&&double.IsFinite(ep.DegreeExponent)&&ep.DegreeExponent>0,"Invalid endpoint settings.");
                    var module=doc.Modules.Single(m=>m.Id==ep.ModuleId);
                    int[] candidates=NeuronGroups.Select(ep,module,generated[ep.ModuleId],groupIndices[ep.ModuleId]).Select(i=>modules[ep.ModuleId][i]).ToArray();int count=(int)Math.Ceiling(candidates.Length*ep.Percent/100);
                    return ep.Selection switch
                    {
                        SelectionKind.HighestDegree=>candidates.OrderByDescending(i=>degree[i]).ThenBy(i=>i).Take(count).ToArray(),
                        SelectionKind.DegreeWeighted=>candidates.OrderBy(i=>Math.Log(-Math.Log(Math.Max(double.Epsilon,rng.NextDouble())))-ep.DegreeExponent*Math.Log(degree[i]+1)).Take(count).ToArray(),
                        _=>Generation.RandomSubset(candidates,ep.Percent,rng)
                    };
                }
                int[] source=Select(link.Source),dest=Select(link.Destination);
                IEnumerable<(int A,int B)> Pairs()
                {
                    if(link.Pattern==LinkPattern.AllToAll)
                    {
                        foreach(int b in dest)foreach(int a in Generation.RandomSubset(source,link.CrossPercent,rng))yield return(a,b);
                        yield break;
                    }
                    // Stable ID order pairs selected groups predictably, regardless of selection RNG.
                    Array.Sort(source);Array.Sort(dest);
                    if(link.Pattern==LinkPattern.OneToOne)
                    {
                        Generation.Require(source.Length==dest.Length,"One-to-one links require equal selected group sizes.");
                        for(int i=0;i<source.Length;i++)if(rng.NextDouble()*100<link.CrossPercent)yield return(source[i],dest[i]);
                    }
                    else foreach(int a in source)
                    {
                        var targets=link.Pattern==LinkPattern.Nearest
                            ?dest.OrderBy(b=>System.Numerics.Vector3.DistanceSquared(ModuleTransform.Vector(ns[a].Position),ModuleTransform.Vector(ns[b].Position))).ThenBy(b=>b).Take(link.FanOut)
                            :Generation.RandomSubset(dest,100,rng).Take(link.FanOut);
                        foreach(int b in targets)if(rng.NextDouble()*100<link.CrossPercent)yield return(a,b);
                    }
                }
                foreach(var (a,b) in Pairs())
                {
                    if(a==b||unique.Contains((a,b)))continue;
                    Generation.Require(!ns[b].Boundary,$"Link '{link.Name}' targets a clamped external input. Use a free module input group instead.");
                    Generation.Require(es.Count<Generation.MaxEdges,"Brain exceeds edge limit.");
                    unique.Add((a,b));es.Add(new(a,b,link.WeightKind==LinkWeightKind.Constant?link.Weight:(float)(rng.NextDouble()*2-1)*link.WeightScale,link.Plasticity));
                    edgeLinks.Add(link.Id);
                }
            }
            catch(InvalidDataException e) when(!strictLinks)
            {
                for(int i=start;i<es.Count;i++)unique.Remove((es[i].Source,es[i].Destination));
                edgeLinks.RemoveRange(start,es.Count-start);es.RemoveRange(start,es.Count-start);
                errors[link.Id]=e.Message;
            }
        }

        return new(ns,es,modules,edgeLinks,errors);
    }
    public static BakedBrain Bake(BrainDocument document)
    {
        // Clone before compiling so the artifact is independent of subsequent edits.
        var doc=BrainDocument.Parse(document.Serialize());
        var compiled=Compile(doc);
        var ns=compiled.Neurons;var es=compiled.Edges;var modules=compiled.ModuleIndices;
        var grouped=new HashSet<string>();var groupNames=new HashSet<string>();
        foreach(var group in doc.RewardGroups)
        {
            Generation.Require(!string.IsNullOrWhiteSpace(group.Name)&&groupNames.Add(group.Name),"Reward groups need unique names.");
            Generation.Require(modules.ContainsKey(group.RewardModuleId)&&doc.Modules.Single(m=>m.Id==group.RewardModuleId).Kind==ModuleKind.Reward,"Reward source must be a reward module.");
            Generation.Require(group.RewardNeuron>=0&&group.RewardNeuron<modules[group.RewardModuleId].Length,"Reward pair index is out of range.");
            foreach(string id in group.ModuleIds)Generation.Require(modules.ContainsKey(id)&&grouped.Add(id),"Reward member is missing or belongs to multiple groups.");
        }
        // Engine requires clamped inputs first. Preserve all other identities in the manifest.
        int[] order=Enumerable.Range(0,ns.Count).OrderBy(i=>ns[i].Boundary?0:1).ToArray();int[] map=new int[ns.Count];for(int i=0;i<order.Length;i++)map[order[i]]=i;
        var readouts=new HashSet<int>();
        foreach(var module in doc.Modules)
        {
            var local=modules[module.Id].Select(i=>ns[i]).ToArray();var groups=NeuronGroups.Resolve(module,local);
            foreach(var group in module.Groups.Where(g=>g.Kind==GroupKind.Output))
                foreach(int i in groups[group.Id])readouts.Add(modules[module.Id][i]);
        }
        int inputs=ns.Count(n=>n.Boundary);int[] outputs=order.Where(i=>!ns[i].Boundary&&readouts.Contains(i)).Select(i=>map[i]).ToArray();
        Generation.Require(inputs>0&&inputs<ns.Count&&outputs.Length>0,"Bake needs an input panel or clock and at least one free output pair.");
        var edges=es.Select(e=>new Edge(map[e.Source],map[e.Destination])).ToArray();
        Generation.Require(es.All(e=>!ns[e.Destination].Boundary),"Internal edges cannot target clamped input boundaries.");
        var model=new PredictiveCodingModel(new(inputs,ns.Count,1,outputs,edges));
        for(int i=0;i<es.Count;i++){model.Weights[i]=es[i].Weight;model.Plasticity[i]=es[i].Plasticity;}
        model.SetActivationLimit(8);
        return new(model,new() { Name=doc.Name,Neurons=order.Select(i=>ns[i]).ToList(),Modules=doc.Modules,RewardGroups=doc.RewardGroups,SourceDocument=doc.Serialize() });
    }
}
public sealed record GraphMetrics(double Clustering,double MeanPath,double ReachableFraction,int SampledSources);
public static class NetworkMetrics
{
    public static GraphMetrics Measure(ModuleGraph g)
    {
        int count=g.Neurons.Count;var adjacent=Enumerable.Range(0,count).Select(_=>new HashSet<int>()).ToArray();
        foreach(var e in g.Edges){adjacent[e.Source].Add(e.Destination);adjacent[e.Destination].Add(e.Source);}
        // Deterministic source sampling bounds metric cost; topology itself is never sampled at bake.
        int[] sources=Enumerable.Range(0,Math.Min(count,128)).Select(i=>i*count/Math.Min(count,128)).ToArray();double clustering=0,totalPath=0,reachable=0;
        foreach(int source in sources)
        {
            int[] neighbors=adjacent[source].Take(256).ToArray();int bonds=0;
            for(int i=0;i<neighbors.Length;i++)for(int j=i+1;j<neighbors.Length;j++)if(adjacent[neighbors[i]].Contains(neighbors[j]))bonds++;
            if(neighbors.Length>1)clustering+=2d*bonds/(neighbors.Length*(neighbors.Length-1));
            int[] distance=Enumerable.Repeat(-1,count).ToArray();distance[source]=0;var queue=new Queue<int>();queue.Enqueue(source);
            while(queue.TryDequeue(out int a))foreach(int b in adjacent[a])if(distance[b]<0){distance[b]=distance[a]+1;queue.Enqueue(b);totalPath+=distance[b];reachable++;}
        }
        return new(clustering/Math.Max(1,sources.Length),reachable==0?0:totalPath/reachable,reachable/Math.Max(1d,sources.Length*(count-1d)),sources.Length);
    }
}
