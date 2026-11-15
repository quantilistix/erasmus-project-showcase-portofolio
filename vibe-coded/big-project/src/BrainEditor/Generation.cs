using NeuralGraph;
using System.Numerics;
namespace ArcadeLab.Editor;

public sealed record GeneratedNeuron(string Id, string ModuleId, NeuronRole Role, Position Position, bool Boundary, float ActivationCap)
{
    public int Layer {get;init;}=-1;
    public Position LocalPosition {get;init;}
}
public sealed record GeneratedEdge(int Source, int Destination, float Weight, float Plasticity);
public sealed class ModuleGraph
{
    public List<GeneratedNeuron> Neurons { get; } = [];
    public List<GeneratedEdge> Edges { get; } = [];
}
public static class Generation
{
    public const int MaxNeurons=20000, MaxEdges=2000000;
    internal static void Require(bool condition,string message) { if(!condition)throw new InvalidDataException(message); }
    internal static void Percent(double value) => Require(double.IsFinite(value)&&value>=0&&value<=100,"Percentages must be in [0,100].");
    internal static void Finite(float value) => Require(float.IsFinite(value),"Settings and coordinates must be finite.");
    public static ModuleGraph Generate(BrainModule m)
    {
        Require(Enum.IsDefined(m.Kind),"Unknown module kind.");
        foreach(float v in new[]{m.Position.X,m.Position.Y,m.Position.Z,m.Rotation.X,m.Rotation.Y,m.Rotation.Z,m.WeightScale,m.Plasticity,m.Frequency,m.Amplitude,m.Phase})Finite(v);
        Require(m.WeightScale>=0&&m.Plasticity>=0&&m.Frequency>=0&&m.Amplitude>=0,"Negative module weight scale, plasticity, frequency or amplitude.");
        var rng=new Random(m.Seed);var g=new ModuleGraph();var unique=new HashSet<(int,int)>();
        void Add(NeuronRole role,Position p,string? id=null)
        {
            Require(g.Neurons.Count<MaxNeurons,"Module exceeds pair limit.");
            Finite(p.X);Finite(p.Y);Finite(p.Z);
            Require(role is NeuronRole.Input or NeuronRole.Hidden or NeuronRole.Output,"A pair must have exactly one role.");
            g.Neurons.Add(new(id??g.Neurons.Count.ToString(),m.Id,role,p,m.Kind is ModuleKind.InputPanel or ModuleKind.Clock,m.Kind==ModuleKind.Reward?8:1));
        }
        void Connect(int a,int b,bool locked=false,float? weight=null,float? plasticity=null)
        {
            if(a==b||!unique.Add((a,b)))return;
            Require(g.Edges.Count<MaxEdges,"Module exceeds edge limit.");
            float w=weight??(locked?1:(float)(rng.NextDouble()*2-1)*m.WeightScale),p=plasticity??(locked?0:m.Plasticity);
            Finite(w);Finite(p);Require(p>=0,"Plasticity cannot be negative.");g.Edges.Add(new(a,b,w,p));
        }
        if(m.Kind==ModuleKind.Custom)
        {
            Require(m.Neurons.Select(n=>n.Id).Distinct().Count()==m.Neurons.Count,"Duplicate custom pair IDs.");
            foreach(var n in m.Neurons)Add(n.Role,n.Position,n.Id);
            var ids=g.Neurons.Select((n,i)=>(n.Id,i)).ToDictionary(x=>x.Id,x=>x.i);
            foreach(var e in m.Edges){Require(ids.ContainsKey(e.Source)&&ids.ContainsKey(e.Destination),"Custom edge refers to a missing pair.");Connect(ids[e.Source],ids[e.Destination],weight:e.Weight,plasticity:e.Plasticity);}
        }
        else if(m.Kind==ModuleKind.Layered)
        {
            Require(m.LayerSizes.Length>=2&&m.LayerSizes.All(n=>n>0)&&m.LayerSizes.Sum(n=>(long)n)<=MaxNeurons,"Invalid layer sizes.");
            Require(m.ResidualStep>=2&&m.ResidualDensity>=0&&m.ResidualDensity<10000,"Invalid residual settings.");
            int[] starts=new int[m.LayerSizes.Length];
            for(int layer=0;layer<m.LayerSizes.Length;layer++)
            {
                starts[layer]=g.Neurons.Count;
                // Retain the legacy RNG consumption so aligning layers does not shuffle their weights.
                _=Positions(m.LayerSizes[layer],m.Dimensions,m.AxisSizes,LatticeShape.Flat,[],rng);
                // Each layer shares a stable YZ plane; seed changes wiring, never its orientation.
                int columns=m.AxisSizes.Length>0?m.AxisSizes[0]:(int)Math.Ceiling(Math.Sqrt(m.LayerSizes[layer]));
                int rows=(m.LayerSizes[layer]+columns-1)/columns;
                for(int i=0;i<m.LayerSizes[layer];i++)
                {
                    Add(layer==0?NeuronRole.Input:layer==m.LayerSizes.Length-1?NeuronRole.Output:NeuronRole.Hidden,
                        new(layer*4,(i%columns-(columns-1)*.5f),(i/columns-(rows-1)*.5f)));
                    g.Neurons[^1]=g.Neurons[^1] with {Layer=layer};
                }
            }
            for(int layer=0;layer<m.LayerSizes.Length-1;layer++)
            {
                var cross=m.Crosses.Count>layer?m.Crosses[layer]:new CrossSettings();Percent(cross.SourcePercent);Percent(cross.DestinationPercent);
                int[] sources=RandomSubset(Enumerable.Range(starts[layer],m.LayerSizes[layer]),cross.SourcePercent,rng);
                foreach(int dest in RandomSubset(Enumerable.Range(starts[layer+1],m.LayerSizes[layer+1]),cross.DestinationPercent,rng))foreach(int source in sources)Connect(source,dest);
                if(m.ResidualDensity>0&&layer%(m.ResidualDensity+1)<m.ResidualDensity&&layer+m.ResidualStep<m.LayerSizes.Length)
                    foreach(int source in Enumerable.Range(starts[layer],m.LayerSizes[layer]))foreach(int dest in Enumerable.Range(starts[layer+m.ResidualStep],m.LayerSizes[layer+m.ResidualStep]))Connect(source,dest,m.FixedResidual);
            }
        }
        else if(m.Kind is ModuleKind.InputPanel or ModuleKind.OutputPanel)
        {
            Require(m.Channels>0&&m.Width>0&&m.Height>0,"Panel dimensions must be positive.");
            int count=m.Panel==PanelKind.Visual?checked(m.Width*m.Height):m.Channels;
            Require(count<=MaxNeurons,"Panel exceeds pair limit.");Finite(m.ResolutionDecay);Require(m.ResolutionDecay>=0,"Resolution decay cannot be negative.");
            if(m.Panel==PanelKind.Tactile)Require(m.Channels==16,"A tactile ring requires sixteen channels.");
            if(m.Panel==PanelKind.Audio)Require(m.SampleRate>0&&float.IsFinite(m.MinFrequency)&&float.IsFinite(m.MaxFrequency)&&m.MinFrequency>0&&m.MaxFrequency>=m.MinFrequency&&m.MaxFrequency<m.SampleRate/2f,"Invalid audio frequency band.");
            for(int i=0;i<count;i++)
            {
                float Center(float v)=>MathF.CopySign(MathF.Pow(MathF.Abs(v),1+m.ResolutionDecay),v)*4;
                Add(m.Kind==ModuleKind.InputPanel?NeuronRole.Input:NeuronRole.Output,m.Panel==PanelKind.Tactile?new(MathF.Cos(2*MathF.PI*i/count),MathF.Sin(2*MathF.PI*i/count),0):m.Panel==PanelKind.Visual?new(Center(2f*(i%m.Width+.5f)/m.Width-1),Center(2f*(i/m.Width+.5f)/m.Height-1),0):new(0,i*.4f,0));
            }
        }
        else if(m.Kind is ModuleKind.Clock or ModuleKind.Pain or ModuleKind.Reward)
        {
            int count=m.Kind==ModuleKind.Clock?1:m.Outputs;Require(count>0&&count<=MaxNeurons,"Signal pair count must be positive.");
            for(int i=0;i<count;i++)Add(NeuronRole.Output,new(0,i*.5f,0));
        }
        else
        {
            Require(m.Inputs>=0&&m.Hidden>=0&&m.Outputs>=0,"Negative pair counts.");
            Require(m.NeuronCount>=0,"Negative neuron count.");
            int[]? latticeSizes=m.Kind==ModuleKind.Lattice?LatticeGeometry.Sizes(m):null;
            long total=latticeSizes is not null?LatticeGeometry.Count(latticeSizes):m.NeuronCount>0?m.NeuronCount:(long)m.Inputs+m.Hidden+m.Outputs;
            Require(total>0&&total<=MaxNeurons,"Invalid module pair count.");int count=(int)total;
            var positions=Positions(count,m.Dimensions,latticeSizes??m.AxisSizes,m.Shape,m.CustomPositions,rng);
            int[][]? latticeNeighbors=null;
            if(m.Kind==ModuleKind.Lattice&&m.Shape==LatticeShape.Flat&&m.Dimensions<=3)
            {
                positions=LatticeGeometry.Positions(latticeSizes!,m.Neighbors);
                latticeNeighbors=LatticeGeometry.Neighbors(positions,m.Dimensions,m.Neighbors);
            }
            // Geometry changes should not reshuffle an existing hub's seeded connections/weights.
            if(m.Kind==ModuleKind.Hub)positions=Positions(count,3,[],m.Shape,m.CustomPositions,new Random(m.Seed));
            int inputs=m.Kind==ModuleKind.Lattice?Math.Min(m.Inputs,Math.Max(0,count-Math.Min(m.Outputs,count))):m.Inputs;
            int outputStart=m.Kind==ModuleKind.Lattice||m.NeuronCount>0?Math.Max(inputs,count-m.Outputs):m.Inputs+m.Hidden;
            for(int i=0;i<count;i++)Add(i<inputs?NeuronRole.Input:i<outputStart?NeuronRole.Hidden:NeuronRole.Output,positions[i]);
            Require(double.IsFinite(m.Imperfection)&&m.Imperfection>=0&&m.Imperfection<=1,"Imperfection must be in [0,1].");
            Require(m.Neighbors>=0&&m.Neighbors<MaxNeurons,"Invalid neighbor count.");
            var hubDegrees=m.Kind==ModuleKind.Hub?new PowerLawDistribution(m.PowerLaw,count-1).Assign(count,m.Seed):null;
            Require(hubDegrees is null||hubDegrees.Sum(n=>(long)n)<=MaxEdges,"Hub distribution exceeds the edge limit; lower its degree range or neuron count.");
            var degree=new int[count];
            for(int i=0;i<count;i++)
            {
                int connections=hubDegrees is not null?hubDegrees[i]:m.Neighbors;
                var candidates=Enumerable.Range(0,count).Where(j=>j!=i);
                int[] nearest=latticeNeighbors is not null?latticeNeighbors[i]:m.Kind==ModuleKind.Hub?candidates.OrderBy(j=>-Math.Log(Math.Max(double.Epsilon,rng.NextDouble()))/(degree[j]+1)).Take(connections).ToArray():
                    candidates.OrderBy(j=>IntrinsicDistance(i,j,count,m,latticeSizes)).ThenBy(j=>j).Take(connections).ToArray();
                foreach(int neighbor in nearest){int dest=m.Kind==ModuleKind.Lattice&&rng.NextDouble()<m.Imperfection?rng.Next(count):neighbor;int before=g.Edges.Count;Connect(i,dest);if(g.Edges.Count!=before){degree[i]++;degree[dest]++;}}
            }
        }
        foreach(var modifier in m.Modifiers.Where(a=>a.Enabled))
        {
            Require(float.IsFinite(modifier.Spacing)&&modifier.Spacing>0,"Array spacing must be positive.");
            Require(modifier.AxisSizes.Length==modifier.Dimensions&&modifier.AxisSizes.All(n=>n>0),"Array requires one positive size per dimension.");
            long copies=modifier.AxisSizes.Aggregate(1L,(a,b)=>checked(a*b));Require(copies<=MaxNeurons&&copies*g.Neurons.Count<=MaxNeurons&&copies*g.Edges.Count<=MaxEdges,"Array exceeds generation limits.");
            var offsets=Positions((int)copies,modifier.Dimensions,modifier.AxisSizes,modifier.Shape,modifier.CustomPositions,rng);
            var ns=g.Neurons.ToArray();var es=g.Edges.ToArray();g.Neurons.Clear();g.Edges.Clear();
            for(int copy=0;copy<copies;copy++)
            {
                foreach(var n in ns)g.Neurons.Add(n with { Id=$"{copy}/{n.Id}",Position=n.Position+offsets[copy]*modifier.Spacing });
                foreach(var e in es)g.Edges.Add(e with { Source=e.Source+copy*ns.Length,Destination=e.Destination+copy*ns.Length });
            }
        }
        Require(g.Neurons.Count>0,"A module must contain at least one pair.");
        for(int i=0;i<g.Neurons.Count;i++)
        {
            Position local=g.Neurons[i].Position;Position position=ModuleTransform.Apply(m,local);Finite(position.X);Finite(position.Y);Finite(position.Z);
            g.Neurons[i]=g.Neurons[i] with { Position=position,LocalPosition=local };
        }
        return g;
    }
    internal static int[] RandomSubset(IEnumerable<int> items,double percent,Random rng)
    {
        Percent(percent);int[] all=items.ToArray();rng.Shuffle(all);return all.Take((int)Math.Ceiling(all.Length*percent/100)).ToArray();
    }
    private static int[] Sizes(int count,int dimensions,int[] sizes)
    {
        Require(dimensions>0&&dimensions<=64,"Dimensions must be in [1,64].");
        if(sizes.Length==0){int side=(int)Math.Ceiling(Math.Pow(count,1.0/dimensions));return Enumerable.Repeat(side,dimensions).ToArray();}
        Require(sizes.Length==dimensions&&sizes.All(n=>n>0),"Provide one positive axis size per dimension.");
        double capacity=sizes.Aggregate(1d,(a,b)=>a*b);Require(capacity>=count,"Axis sizes cannot fit the pair count.");return sizes;
    }
    private static double[] Coordinate(int index,int[] sizes)
    {
        var c=new double[sizes.Length];for(int d=0;d<sizes.Length;d++){c[d]=index%sizes[d]-(sizes[d]-1)*.5;index/=sizes[d];}return c;
    }
    private static double IntrinsicDistance(int a,int b,int count,BrainModule m,int[]? latticeSizes=null)
    {
        if(m.Shape==LatticeShape.Custom){var x=m.CustomPositions[a];var y=m.CustomPositions[b];return Math.Pow(x.X-y.X,2)+Math.Pow(x.Y-y.Y,2)+Math.Pow(x.Z-y.Z,2);}
        int[] sizes=latticeSizes??Sizes(count,m.Dimensions,m.AxisSizes);double[] p=Coordinate(a,sizes),q=Coordinate(b,sizes);
        if(m.Dimensions==2&&m.Shape==LatticeShape.Flat&&m.Neighbors is 3 or 6)
        {
            // Triangular coordinates; remove alternate vertical bonds for honeycomb.
            int ax=a%sizes[0],ay=a/sizes[0],bx=b%sizes[0],by=b/sizes[0];
            if(m.Neighbors==3){bool horizontal=ay==by&&Math.Abs(ax-bx)==1;bool vertical=ax==bx&&by-ay==(((ax+ay)%2==0)?1:-1);if(horizontal||vertical)return 0;return 1000+Math.Abs(ax-bx)+Math.Abs(ay-by);}
            p[0]+=.5*(ay%2);p[1]*=Math.Sqrt(3)/2;q[0]+=.5*(by%2);q[1]*=Math.Sqrt(3)/2;
        }
        if(m.Shape==LatticeShape.Spherical){double dot=0,np=0,nq=0;for(int d=0;d<p.Length;d++){dot+=p[d]*q[d];np+=p[d]*p[d];nq+=q[d]*q[d];}return 1-dot/Math.Max(1e-9,Math.Sqrt(np*nq));}
        double distance=p.Zip(q,(x,y)=>(x-y)*(x-y)).Sum();
        if(m.Shape==LatticeShape.Hyperbolic)
        {
            double dot=p.Zip(q,(x,y)=>x*y).Sum();
            return Math.Acosh(Math.Max(1,Math.Sqrt(1+p.Sum(x=>x*x))*Math.Sqrt(1+q.Sum(x=>x*x))-dot));
        }
        return distance;
    }
    public static Position[] Positions(int count,int dimensions,int[] sizes,LatticeShape shape,List<Position> custom,Random rng)
    {
        if(shape==LatticeShape.Custom){Require(custom.Count==count,"Custom shape needs one XYZ coordinate per pair/copy.");return custom.ToArray();}
        Require(Enum.IsDefined(shape),"Unknown lattice shape.");int[] axes=Sizes(count,dimensions,sizes);
        // Orthonormal rows of a seeded 3xN projection, followed by a random 3D rotation.
        var basis=new double[3][];
        for(int row=0;row<3;row++)
        {
            basis[row]=Enumerable.Range(0,dimensions).Select(_=>rng.NextDouble()*2-1).ToArray();
            for(int prev=0;prev<row;prev++){double dot=basis[row].Zip(basis[prev],(x,y)=>x*y).Sum();for(int d=0;d<dimensions;d++)basis[row][d]-=dot*basis[prev][d];}
            double norm=Math.Sqrt(basis[row].Sum(x=>x*x));for(int d=0;d<dimensions;d++)basis[row][d]=norm>1e-8?basis[row][d]/norm:0;
        }
        var rotation=Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble()*6.28f,(float)rng.NextDouble()*6.28f,(float)rng.NextDouble()*6.28f);
        var positions=new Position[count];
        for(int i=0;i<count;i++)
        {
            double[] c=Coordinate(i,axes);double radius=Math.Sqrt(c.Sum(x=>x*x));
            if(shape==LatticeShape.Spherical&&radius>0)for(int d=0;d<c.Length;d++)c[d]*=Math.Max(1,Math.Pow(count,1.0/dimensions))/radius;
            if(shape==LatticeShape.Hyperbolic)for(int d=0;d<c.Length;d++)c[d]=Math.Sinh(c[d]/Math.Max(1,axes[d]))*2;
            float Dot(int row)=>(float)c.Zip(basis[row],(x,y)=>x*y).Sum();var projected=Vector3.Transform(new Vector3(Dot(0),Dot(1),Dot(2)),rotation);positions[i]=new(projected.X,projected.Y,projected.Z);
        }
        return positions;
    }
}
