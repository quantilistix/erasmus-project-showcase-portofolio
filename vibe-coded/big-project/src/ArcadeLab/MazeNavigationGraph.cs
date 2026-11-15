using System.Numerics;

namespace ArcadeLab;

/// <summary>Rooms and straight corridor regions, connected by unobstructed passages.</summary>
internal sealed class MazeNavigationGraph(int width,int height)
{
    internal enum RegionKind { Room,Corridor,Junction,Doorway }
    internal sealed class Node(int id,Vector2 position,RegionKind kind,int[] cells)
    {
        internal readonly int Id=id;
        internal readonly Vector2 Position=position;
        internal readonly RegionKind Kind=kind;
        internal readonly int[] Cells=cells;
        internal int Visits;
        internal readonly List<int> Neighbors=[];
    }
    private readonly char[] tiles=new char[width*height];
    private readonly int[] owners=Enumerable.Repeat(-1,width*height).ToArray(),visits=new int[width*height];
    private readonly List<Node> nodes=[];
    private int last=-1;
    internal IEnumerable<Node> Nodes=>nodes;
    internal int KnownFloorCount=>tiles.Count(IsFloor);
    private static bool IsFloor(char tile)=>tile!='\0'&&tile!='#';
    private char Tile(int x,int y)=>x>=0&&y>=0&&x<width&&y<height?tiles[y*width+x]:'#';
    internal Node? At(int x,int y)=>x>=0&&y>=0&&x<width&&y<height&&owners[y*width+x]>=0?nodes[owners[y*width+x]]:null;
    internal bool HasSupply(char tile)=>tiles.Contains(tile);
    internal void Observe(MazeGame game)
    {
        int cx=(int)game.Position.X,cy=(int)game.Position.Y;bool changed=false;
        for(int y=Math.Max(0,cy-6);y<=Math.Min(height-1,cy+6);y++)for(int x=Math.Max(0,cx-6);x<=Math.Min(width-1,cx+6);x++)
        {
            char tile=game.TeacherVisibleMapTile(x,y);if(tile=='\0')continue;
            int id=y*width+x;changed|=IsFloor(tile)!=IsFloor(tiles[id]);tiles[id]=tile;
        }
        int current=cy*width+cx;if(current!=last){visits[current]++;last=current;}
        if(changed)BuildRegions();
        foreach(var node in nodes)node.Visits=node.Cells.Sum(cell=>visits[cell]);
    }
    private void BuildRegions()
    {
        nodes.Clear();Array.Fill(owners,-1);
        var heights=new int[width];var stack=new int[width+1];
        // Repeated maximal rectangles extract large rooms first, then long
        // corridor runs. No navigation node is allocated for individual tiles.
        while(true)
        {
            Array.Clear(heights);int bestArea=0,bx=0,by=0,bw=0,bh=0;
            for(int y=0;y<height;y++)
            {
                for(int x=0;x<width;x++)heights[x]=IsFloor(tiles[y*width+x])&&owners[y*width+x]<0?heights[x]+1:0;
                int top=0;
                for(int x=0;x<=width;x++)
                {
                    int h=x==width?0:heights[x];
                    while(top>0&&heights[stack[top-1]]>h)
                    {
                        int bar=stack[--top],left=top==0?0:stack[top-1]+1,w=x-left,area=w*heights[bar];
                        if(area>bestArea){bestArea=area;bx=left;by=y-heights[bar]+1;bw=w;bh=heights[bar];}
                    }
                    if(x<width)stack[top++]=x;
                }
            }
            if(bestArea==0)break;
            var cells=new int[bestArea];int index=0,id=nodes.Count;
            for(int y=by;y<by+bh;y++)for(int x=bx;x<bx+bw;x++){int cell=y*width+x;owners[cell]=id;cells[index++]=cell;}
            var kind=bw>=3&&bh>=3&&Math.Max(bw,bh)<=3*Math.Min(bw,bh)?RegionKind.Room:bestArea==1?RegionKind.Junction:RegionKind.Corridor;
            nodes.Add(new(id,new(bx+bw*.5f,by+bh*.5f),kind,cells));
        }
        var portals=new Dictionary<(int,int),(Vector2 A,Vector2 B,float Cost)>();
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int a=owners[y*width+x];if(a<0)continue;
            Portal(x+1,y,a,new(x+.5f,y+.5f),new(x+1.5f,y+.5f));Portal(x,y+1,a,new(x+.5f,y+.5f),new(x+.5f,y+1.5f));
        }
        void Portal(int x,int y,int a,Vector2 sideA,Vector2 sideB)
        {
            var other=At(x,y);if(other is null||a==other.Id)return;
            int b=other.Id;var key=(Math.Min(a,b),Math.Max(a,b));
            float cost=Vector2.Distance(nodes[a].Position,sideA)+Vector2.Distance(nodes[b].Position,sideB);
            if(!portals.TryGetValue(key,out var prior)||cost<prior.Cost)portals[key]=a==key.Item1?(sideA,sideB,cost):(sideB,sideA,cost);
        }
        int regionCount=nodes.Count;
        foreach(var (pair,portal) in portals)
        {
            var a=nodes[pair.Item1];var b=nodes[pair.Item2];
            if(Clear(a.Position,b.Position))Connect(a,b);
            else if(Clear(a.Position,portal.A)&&Clear(portal.A,portal.B)&&Clear(portal.B,b.Position))
            {
                // Approach a narrow opening along its two floor centers. A line
                // to the boundary midpoint alone can graze a wall at a bend.
                var entry=new Node(nodes.Count,portal.A,RegionKind.Doorway,[]);nodes.Add(entry);
                var exit=new Node(nodes.Count,portal.B,RegionKind.Doorway,[]);nodes.Add(exit);
                Connect(a,entry);Connect(entry,exit);Connect(exit,b);
            }
        }
        // Regions with a direct, wall-free sightline may connect across another
        // open region. Doorway nodes remain available for obstructed bends.
        for(int a=0;a<regionCount;a++)for(int b=a+1;b<regionCount;b++)
            if(!nodes[a].Neighbors.Contains(b)&&Clear(nodes[a].Position,nodes[b].Position))Connect(nodes[a],nodes[b]);
        static void Connect(Node a,Node b){a.Neighbors.Add(b.Id);b.Neighbors.Add(a.Id);}
    }
    internal bool Clear(Vector2 from,Vector2 to,float radius=.22f)
    {
        int steps=Math.Max(1,(int)MathF.Ceiling(Vector2.Distance(from,to)/.08f));
        for(int k=0;k<=steps;k++)
        {
            var p=Vector2.Lerp(from,to,(float)k/steps);
            if(!IsFloor(Tile((int)MathF.Floor(p.X-radius),(int)MathF.Floor(p.Y-radius)))||
               !IsFloor(Tile((int)MathF.Floor(p.X+radius),(int)MathF.Floor(p.Y-radius)))||
               !IsFloor(Tile((int)MathF.Floor(p.X-radius),(int)MathF.Floor(p.Y+radius)))||
               !IsFloor(Tile((int)MathF.Floor(p.X+radius),(int)MathF.Floor(p.Y+radius))))return false;
        }
        return true;
    }
    internal List<Vector2> Plan(Vector2 position,char supply,IReadOnlyList<Vector2> avoid,int seed)
    {
        if(At((int)position.X,(int)position.Y) is not {} start)return [];
        var costs=new float[nodes.Count];Array.Fill(costs,float.PositiveInfinity);
        var parents=new int[nodes.Count];Array.Fill(parents,-1);costs[start.Id]=Vector2.Distance(position,start.Position);
        var open=new PriorityQueue<int,float>();open.Enqueue(start.Id,costs[start.Id]);
        while(open.TryDequeue(out int id,out float cost))
        {
            if(cost>costs[id])continue;var node=nodes[id];
            foreach(int next in node.Neighbors)
            {
                var neighbor=nodes[next];float candidate=cost+TravelCost(node.Position,neighbor.Position,avoid);
                if(candidate>=costs[next])continue;costs[next]=candidate;parents[next]=id;open.Enqueue(next,candidate);
            }
        }
        Node? target=null;Vector2 goal=default;float best=float.PositiveInfinity;
        void Consider(Node node,Vector2 point,int cell,bool frontier)
        {
            if(!float.IsFinite(costs[node.Id])||Vector2.Distance(position,point)<.6f||!Clear(node.Position,point))return;
            float cost=costs[node.Id]+TravelCost(node.Position,point,avoid);
            float score=supply!='\0'?cost:visits[cell]*12+node.Visits*1.5f+cost*.35f-Vector2.Distance(position,point)*.4f-(frontier?5:0);
            score+=((uint)(cell*397^seed)&255)*.0001f;
            if(score<best){best=score;target=node;goal=point;}
        }
        foreach(var node in nodes)
        {
            if(node.Kind==RegionKind.Doorway)continue;
            if(supply!='\0')
            {
                foreach(int cell in node.Cells)if(tiles[cell]==supply)Consider(node,new(cell%width+.5f,cell/width+.5f),cell,false);
                continue;
            }
            int center=(int)node.Position.Y*width+(int)node.Position.X;
            Consider(node,node.Position,center,false);
            // Room/corridor ends and undiscovered boundaries are local goals,
            // not thousands of tile nodes competing to change the route.
            int minX=node.Cells.Min(c=>c%width),maxX=node.Cells.Max(c=>c%width),minY=node.Cells.Min(c=>c/width),maxY=node.Cells.Max(c=>c/width);
            foreach(int cell in node.Cells)
            {
                int x=cell%width,y=cell/width;
                bool frontier=Tile(x+1,y)=='\0'||Tile(x-1,y)=='\0'||Tile(x,y+1)=='\0'||Tile(x,y-1)=='\0';
                bool end=(x==minX||x==maxX)&&(y==minY||y==maxY);
                if(frontier||end)Consider(node,new(x+.5f,y+.5f),cell,frontier);
            }
        }
        if(target is null)return [];
        var path=new List<Vector2>{goal};int cursor=target.Id;
        while(cursor!=start.Id){path.Add(nodes[cursor].Position);cursor=parents[cursor];}
        path.Add(start.Position);path.Add(position);path.Reverse();
        for(int i=path.Count-1;i>0;i--)if(Vector2.DistanceSquared(path[i],path[i-1])<.0001f)path.RemoveAt(i);
        return avoid.Count==0?Round(path,avoid):Round(Detour(Round(path,avoid),avoid),avoid);
    }
    private List<Vector2> Detour(List<Vector2> path,IReadOnlyList<Vector2> avoid)
    {
        if(avoid.Count==0)return path;
        var result=new List<Vector2>{path[0]};
        for(int i=1;i<path.Count;i++)
        {
            var from=result[^1];var to=path[i];var delta=to-from;
            if(delta.LengthSquared()>.01f)
            {
                var side=Vector2.Normalize(new Vector2(-delta.Y,delta.X));float best=TravelCost(from,to,avoid);Vector2? detour=null;
                foreach(var enemy in avoid)foreach(float sign in new[]{-1f,1f})foreach(float radius in new[]{2.5f,3.5f,4.5f})
                {
                    var candidate=enemy+side*sign*radius;
                    if(!Clear(from,candidate)||!Clear(candidate,to))continue;
                    float cost=TravelCost(from,candidate,avoid)+TravelCost(candidate,to,avoid);
                    if(cost<best*.97f){best=cost;detour=candidate;}
                }
                if(detour is {} point)result.Add(point);
            }
            result.Add(to);
        }
        return result;
    }
    private static float TravelCost(Vector2 from,Vector2 to,IReadOnlyList<Vector2> avoid)
    {
        float length=Vector2.Distance(from,to),cost=0;if(avoid.Count==0)return length;int samples=Math.Max(1,(int)MathF.Ceiling(length/.2f));
        for(int k=0;k<samples;k++)
        {
            var p=Vector2.Lerp(from,to,(k+.5f)/samples);float danger=0;
            foreach(var enemy in avoid)danger+=8*MathF.Pow(MathF.Max(0,5-Vector2.Distance(p,enemy)),2);
            cost+=(1+danger)*length/samples;
        }
        return cost;
    }
    private List<Vector2> Round(List<Vector2> path,IReadOnlyList<Vector2> avoid)
    {
        // Pull straight segments tight, then round bends with quadratic arcs.
        var cumulative=new float[path.Count];
        for(int i=1;i<path.Count;i++)cumulative[i]=cumulative[i-1]+TravelCost(path[i-1],path[i],avoid);
        var pulled=new List<Vector2>{path[0]};int anchor=0;
        while(anchor<path.Count-1)
        {
            int end=path.Count-1;while(end>anchor+1&&(!Clear(path[anchor],path[end])||TravelCost(path[anchor],path[end],avoid)>(cumulative[end]-cumulative[anchor])*1.03f))end--;
            pulled.Add(path[end]);anchor=end;
        }
        var rounded=new List<Vector2>{pulled[0]};
        for(int i=1;i<pulled.Count-1;i++)
        {
            var corner=pulled[i];float radius=MathF.Min(.45f,MathF.Min(Vector2.Distance(corner,pulled[i-1]),Vector2.Distance(corner,pulled[i+1]))*.35f);
            var entry=corner+Vector2.Normalize(pulled[i-1]-corner)*radius;
            var exit=corner+Vector2.Normalize(pulled[i+1]-corner)*radius;
            var arc=new List<Vector2>{entry};
            for(int k=1;k<=8;k++){float t=k/8f;arc.Add((1-t)*(1-t)*entry+2*(1-t)*t*corner+t*t*exit);}
            bool safe=Clear(rounded[^1],entry);
            for(int k=1;k<arc.Count;k++)safe&=Clear(arc[k-1],arc[k]);
            if(safe)rounded.AddRange(arc);else rounded.Add(corner);
        }
        rounded.Add(pulled[^1]);return rounded;
    }
}
