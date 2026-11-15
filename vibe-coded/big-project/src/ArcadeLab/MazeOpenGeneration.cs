namespace ArcadeLab;

public sealed partial class MazeGame
{
    private void GenerateOpenLayout(int n)
    {
        int spacing=new[]{7,6,5,4}[DifficultyStage],width=CorridorWidth;
        var centers=Enumerable.Range(0,n).Select(i=>3+i*spacing).TakeWhile(p=>p<n-2).ToArray();
        int side=centers.Length;var visited=new bool[side,side];
        var stack=new Stack<(int X,int Y)>();stack.Push((0,0));visited[0,0]=true;
        void Carve(int cx,int cy)
        {
            int low=-width/2;
            for(int y=cy+low;y<cy+low+width;y++)for(int x=cx+low;x<cx+low+width;x++)
                if(x>0&&y>0&&x<n-1&&y<n-1)map[y][x]='.';
        }
        Carve(centers[0],centers[0]);
        (int X,int Y)[] directions=[(1,0),(-1,0),(0,1),(0,-1)];
        while(stack.Count>0)
        {
            var p=stack.Peek();random.Shuffle(directions);bool added=false;
            foreach(var d in directions)
            {
                int nx=p.X+d.X,ny=p.Y+d.Y;
                if(nx<0||ny<0||nx>=side||ny>=side||visited[nx,ny])continue;
                for(int step=0;step<=spacing;step++)Carve(centers[p.X]+d.X*step,centers[p.Y]+d.Y*step);
                visited[nx,ny]=true;stack.Push((nx,ny));added=true;break;
            }
            if(!added)stack.Pop();
        }
        // Every room is centered on an existing connected corridor junction.
        for(int i=0;i<8+DifficultyStage;i++)
        {
            int cx=centers[random.Next(side)],cy=centers[random.Next(side)];
            int rw=random.Next(RoomMinimumSide,RoomMaximumSide+1),rh=random.Next(RoomMinimumSide,RoomMaximumSide+1);
            for(int y=Math.Max(1,cy-rh/2);y<=Math.Min(n-2,cy+(rh-1)/2);y++)
                for(int x=Math.Max(1,cx-rw/2);x<=Math.Min(n-2,cx+(rw-1)/2);x++)map[y][x]='.';
        }
        // A generous spawn connects (1,1) to the first junction, with turning space.
        for(int y=1;y<=5;y++)for(int x=1;x<=5;x++)map[y][x]='.';
    }
}
