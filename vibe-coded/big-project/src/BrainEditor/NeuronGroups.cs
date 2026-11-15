using System.Numerics;

namespace ArcadeLab.Editor;

public sealed class NeuronGroup
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public string Name {get;set;}="Group";
    public GroupKind Kind {get;set;}=GroupKind.Other;
    public GroupCriterion Criterion {get;set;}=GroupCriterion.IndexRange;
    public NeuronRole Role {get;set;}=NeuronRole.Output;
    public int Layer {get;set;}=-1; // Negative indices count back from the last layer.
    public int Start {get;set;}
    public int Count {get;set;}=0; // Zero means all remaining matches.
    public int Step {get;set;}=1;
    public Position Min {get;set;}=new(-1,-1,-1);
    public Position Max {get;set;}=new(1,1,1);
    public List<string> NeuronIds {get;set;}=[];
}

public static class ModuleTransform
{
    public static Vector3 Vector(Position p)=>new(p.X,p.Y,p.Z);
    public static Position Position(Vector3 p)=>new(p.X,p.Y,p.Z);
    public static Quaternion Rotation(Position degrees)
    {
        const float radians=MathF.PI/180;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ,degrees.Z*radians)
            *Quaternion.CreateFromAxisAngle(Vector3.UnitY,degrees.Y*radians)
            *Quaternion.CreateFromAxisAngle(Vector3.UnitX,degrees.X*radians);
    }
    public static Position Euler(Quaternion q)
    {
        q=Quaternion.Normalize(q);const float degrees=180/MathF.PI;
        return new(MathF.Atan2(2*(q.W*q.X+q.Y*q.Z),1-2*(q.X*q.X+q.Y*q.Y))*degrees,
            MathF.Asin(Math.Clamp(2*(q.W*q.Y-q.Z*q.X),-1,1))*degrees,
            MathF.Atan2(2*(q.W*q.Z+q.X*q.Y),1-2*(q.Y*q.Y+q.Z*q.Z))*degrees);
    }
    public static Position Apply(BrainModule module,Position local)
        =>Position(Vector3.Transform(Vector(local),Rotation(module.Rotation))+Vector(module.Position));
    public static Position Local(BrainModule module,Position world)
        =>Position(Vector3.Transform(Vector(world)-Vector(module.Position),Quaternion.Inverse(Rotation(module.Rotation))));
}

public static class NeuronGroups
{
    public static List<NeuronGroup> Defaults()=>Enum.GetValues<NeuronRole>().Where(r=>r!=NeuronRole.All)
        .Select(r=>new NeuronGroup {Id=r.ToString().ToLowerInvariant(),Name=r.ToString().ToLowerInvariant(),
            Kind=Enum.Parse<GroupKind>(r.ToString()),Criterion=GroupCriterion.Role,Role=r}).ToList();

    public static Dictionary<string,int[]> Resolve(BrainModule module,IReadOnlyList<GeneratedNeuron> neurons)
    {
        var result=new Dictionary<string,int[]>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var group in module.Groups)
        {
            Generation.Require(!string.IsNullOrWhiteSpace(group.Id)&&!result.ContainsKey(group.Id),"Groups need unique IDs.");
            Generation.Require(!string.IsNullOrWhiteSpace(group.Name)&&names.Add(group.Name),"Groups need unique names within a module.");
            Generation.Require(Enum.IsDefined(group.Kind)&&Enum.IsDefined(group.Criterion),"Unknown group kind or criterion.");
            Generation.Require(group.Start>=0&&group.Count>=0&&group.Step>0,"Group start/count must be nonnegative; step must be positive.");
            if(group.Criterion==GroupCriterion.Role)Generation.Require(group.Role!=0&&(group.Role&~NeuronRole.All)==0,"Invalid group role.");
            int layer=group.Layer<0?module.LayerSizes.Length+group.Layer:group.Layer;
            if(group.Criterion==GroupCriterion.Layer)Generation.Require(module.Kind==ModuleKind.Layered&&layer>=0&&layer<module.LayerSizes.Length,"Group layer does not exist.");
            if(group.Criterion==GroupCriterion.SpatialBox)
            {
                foreach(float v in new[]{group.Min.X,group.Min.Y,group.Min.Z,group.Max.X,group.Max.Y,group.Max.Z})Generation.Finite(v);
                Generation.Require(group.Min.X<=group.Max.X&&group.Min.Y<=group.Max.Y&&group.Min.Z<=group.Max.Z,"Group minimum must not exceed maximum.");
            }
            var ids=group.NeuronIds.ToHashSet();
            if(group.Criterion==GroupCriterion.ExplicitIds)Generation.Require(ids.IsSubsetOf(neurons.Select(n=>n.Id)),"Group references a missing neuron ID.");
            bool Match(GeneratedNeuron n)
            {
                var p=n.LocalPosition;
                return group.Criterion switch
                {
                    GroupCriterion.Role=>(n.Role&group.Role)!=0,
                    GroupCriterion.Layer=>n.Layer==layer,
                    GroupCriterion.SpatialBox=>p.X>=group.Min.X&&p.X<=group.Max.X&&p.Y>=group.Min.Y&&p.Y<=group.Max.Y&&p.Z>=group.Min.Z&&p.Z<=group.Max.Z,
                    GroupCriterion.ExplicitIds=>ids.Contains(n.Id),
                    _=>true
                };
            }
            result[group.Id]=Enumerable.Range(0,neurons.Count).Where(i=>Match(neurons[i])).Skip(group.Start)
                .Where((_,i)=>i%group.Step==0).Take(group.Count==0?int.MaxValue:group.Count).ToArray();
        }
        return result;
    }
    public static int[] Select(LinkEndpoint endpoint,BrainModule module,IReadOnlyList<GeneratedNeuron> neurons,Dictionary<string,int[]> groups)
    {
        Generation.Require(Enum.IsDefined(endpoint.Scope),"Unknown endpoint scope.");
        if(endpoint.Scope==EndpointScope.AllNeurons)return Enumerable.Range(0,neurons.Count).ToArray();
        if(endpoint.GroupIds.Count==0&&endpoint.Scope==EndpointScope.LegacyRoles)return Enumerable.Range(0,neurons.Count).Where(i=>(neurons[i].Role&endpoint.Roles)!=0).ToArray();
        foreach(string id in endpoint.GroupIds)Generation.Require(groups.ContainsKey(id),$"Group '{id}' is missing from '{module.Name}'.");
        return endpoint.GroupIds.SelectMany(id=>groups[id]).Distinct().Order().ToArray();
    }
}
