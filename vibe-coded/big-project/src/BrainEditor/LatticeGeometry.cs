using System.Numerics;

namespace ArcadeLab.Editor;

/// <summary>Cell counts and physical coordination of flat one-, two- and three-dimensional lattices.</summary>
public static class LatticeGeometry
{
    public static int[] Sizes(BrainModule module)
    {
        Generation.Require(module.Dimensions is >0 and <=64,"Dimensions must be in [1,64].");
        if(module.AxisSizes.Length>0)
        {
            Generation.Require(module.AxisSizes.Length==module.Dimensions,"Provide one size per lattice dimension.");
            Count(module.AxisSizes);return module.AxisSizes.ToArray();
        }
        // Older projects stored only a total. Factor that total into balanced axes without
        // changing their neuron count; future size edits are authoritative.
        long legacy=module.NeuronCount>0?module.NeuronCount:(long)module.Inputs+module.Hidden+module.Outputs;
        Generation.Require(legacy is >0 and <=Generation.MaxNeurons,"Invalid lattice neuron count.");
        var factors=new List<int>();int remaining=(int)legacy;
        for(int factor=2;factor<=remaining/factor;factor++)while(remaining%factor==0){factors.Add(factor);remaining/=factor;}
        if(remaining>1)factors.Add(remaining);
        var sizes=Enumerable.Repeat(1,module.Dimensions).ToArray();
        foreach(int factor in factors.OrderDescending())
        {
            int axis=Array.IndexOf(sizes,sizes.Min());sizes[axis]*=factor;
        }
        return sizes;
    }
    public static int Count(IReadOnlyList<int> sizes)
    {
        Generation.Require(sizes.Count is >0 and <=64,"Lattices require 1 to 64 dimensions.");
        int count=1;
        foreach(int size in sizes)
        {
            Generation.Require(size>0,"Lattice dimension sizes must be positive.");
            Generation.Require(size<=Generation.MaxNeurons/count,"Lattice exceeds pair limit.");count*=size;
        }
        return count;
    }
    public static string Name(int dimensions,int neighbors)=>(dimensions,neighbors) switch
    {
        (1,2)=>"Chain",(2,3)=>"Honeycomb",(2,4)=>"Square",(2,6)=>"Triangular",(2,8)=>"Square + diagonals",
        (3,4)=>"Diamond",(3,6)=>"Cubic",(3,8)=>"Body-centered cubic",(3,12)=>"Face-centered cubic",
        _=>"Grid / nearest neighbors"
    };
    private static float Radius(int dimensions,int neighbors)=>(dimensions,neighbors) switch
    {
        (1,2) or (2,3) or (2,4) or (2,6) or (3,6)=>1,
        (2,8)=>MathF.Sqrt(2),(3,4)=>MathF.Sqrt(3)/4,(3,8)=>MathF.Sqrt(3)/2,(3,12)=>MathF.Sqrt(.5f),_=>0
    };
    public static Position[] Positions(int[] sizes,int neighbors)
    {
        Generation.Require(sizes.Length is >=1 and <=3,"Physical lattice layout supports up to three dimensions.");
        var points=new Vector3[Count(sizes)];var center=Vector3.Zero;
        for(int i=0;i<points.Length;i++)
        {
            int x=i%sizes[0],y=sizes.Length>1?i/sizes[0]%sizes[1]:0,z=sizes.Length>2?i/(sizes[0]*sizes[1]):0;
            var p=new Vector3(x,y,z);
            if(sizes.Length==2)
            {
                if(neighbors==3)p=new(x*MathF.Sqrt(3)/2,y*1.5f-((x+y)%2)*.5f,0);
                if(neighbors==6)p=new(x+.5f*(y%2),y*MathF.Sqrt(3)/2,0);
            }
            if(sizes.Length==3)
            {
                if(neighbors==4)p=new Vector3((y+z)*.5f,(x/2+z)*.5f,(x/2+y)*.5f)+new Vector3((x%2)*.25f);
                if(neighbors==8)p=new((x-y+z)*.5f,(x+y-z)*.5f,(-x+y+z)*.5f);
                if(neighbors==12)p=new((y+z)*.5f,(x+z)*.5f,(x+y)*.5f);
            }
            points[i]=p;center+=p;
        }
        center/=points.Length;
        return points.Select(p=>ModuleTransform.Position(p-center)).ToArray();
    }
    /// <summary>Open boundaries lose missing neighbors. Do not fill them with distant cells.
    /// Spatial buckets keep the standard layouts linear in the number of cells.</summary>
    public static int[][]? Neighbors(Position[] positions,int dimensions,int neighbors)
    {
        float radius=Radius(dimensions,neighbors);if(radius==0)return null;
        float cellSize=radius*1.001f;
        (int X,int Y,int Z) Cell(Position p)=>((int)MathF.Floor(p.X/cellSize),(int)MathF.Floor(p.Y/cellSize),(int)MathF.Floor(p.Z/cellSize));
        var cells=new Dictionary<(int,int,int),List<int>>();
        for(int i=0;i<positions.Length;i++)
        {
            var key=Cell(positions[i]);if(!cells.TryGetValue(key,out var bucket))cells[key]=bucket=[];bucket.Add(i);
        }
        var result=new int[positions.Length][];
        for(int i=0;i<positions.Length;i++)
        {
            var key=Cell(positions[i]);var adjacent=new List<int>();
            for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                if(cells.TryGetValue((key.X+x,key.Y+y,key.Z+z),out var bucket))
                    foreach(int j in bucket)if(i!=j&&Vector3.DistanceSquared(ModuleTransform.Vector(positions[i]),ModuleTransform.Vector(positions[j]))<=radius*radius+1e-5f)adjacent.Add(j);
            result[i]=adjacent.OrderBy(j=>j).ToArray();
        }
        return result;
    }
}
