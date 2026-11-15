using System.Numerics;

namespace ArcadeLab;

public sealed partial class MazeGame
{
    private readonly Queue<Vector2> observerTrail = new();
    /// <summary>Human-only overview. Rendering reads world data without changing sensors, learning or physics.</summary>
    public void RenderTopView(Span<uint> pixels,int width,int height)
    {
        if(width<1||height<1||pixels.Length!=width*height)throw new ArgumentException("Top-view shape.");
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            float wx=(x+.5f)*MapWidth/width,wy=(y+.5f)*MapHeight/height;
            int tx=(int)wx,ty=(int)wy;float fx=wx-tx,fy=wy-ty;
            uint color;
            if(map[ty][tx]=='#')
            {
                color=fx<.045f||fy<.045f?0x9AB5CBu:fx>.9f||fy>.9f?0x30465Bu:0x536E89u;
                if(fx>.12f&&fx<.88f&&fy>.12f&&fy<.88f)color=0x607C96;
            }
            else
            {
                color=(tx+ty)%2==0?0x1E2D3Fu:0x223246u;
                if(tx>0&&map[ty][tx-1]=='#'&&fx<.22f || ty>0&&map[ty-1][tx]=='#'&&fy<.22f)
                    color=0x152331;
                if(fx<.018f||fy<.018f)color=0x2C4052;
            }
            pixels[y*width+x]=color;
        }
        float scale=MathF.Min((float)width/MapWidth,(float)height/MapHeight);
        Vector2 Pixel(Vector2 world)=>new(world.X*width/MapWidth,world.Y*height/MapHeight);
        Vector2? last=null;
        foreach(var point in observerTrail)
        { Vector2 p=Pixel(point);if(last.HasValue)TopLine(pixels,width,height,last.Value,p,0x386F88);last=p; }
        // Show the edge/centre rays actually sampled by the 1D sensor, ending at its closest hit.
        foreach(int ray in new[]{0,VisionRayCount/2,VisionRayCount-1})
        {
            float angle=heading+((ray+.5f)/VisionRayCount-.5f)*VisionFieldOfView;
            Vector2 end=position+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*VisionDepth[ray];
            TopLine(pixels,width,height,Pixel(position),Pixel(end),ray==VisionRayCount/2?0x7194A0u:0x37626Bu,dashed:true);
        }
        for(int y=0;y<MapHeight;y++)for(int x=0;x<MapWidth;x++)
        {
            Vector2 p=Pixel(new(x+.5f,y+.5f));int cx=(int)p.X,cy=(int)p.Y,r=Math.Max(2,(int)(scale*.27f));
            switch(map[y][x])
            {
                case 'H':
                    TopDisc(pixels,width,height,cx,cy,r+1,0x123D36);
                    TopRect(pixels,width,height,cx-1,cy-r,3,2*r+1,0x6EF0B0);TopRect(pixels,width,height,cx-r,cy-1,2*r+1,3,0x6EF0B0);break;
                case 'A':
                    TopRect(pixels,width,height,cx-r,cy-r,2*r+1,2*r+1,0xC49239);
                    for(int dx=-r+1;dx<r;dx+=2)TopRect(pixels,width,height,cx+dx,cy-r,1,2*r,0xFFE19D);break;
                case 'E':
                    r=Math.Max(3,(int)(scale*.4f));
                    TopRect(pixels,width,height,cx-r,cy-r,2*r+1,2*r+1,0x76EDE5);
                    TopRect(pixels,width,height,cx-r+2,cy-r+2,2*r-3,2*r-3,0x184D59);break;
            }
        }
        foreach(var enemy in enemies)
        {
            Vector2 p=Pixel(enemy.Position);int r=Math.Max(2,(int)(scale*.28f));
            TopDisc(pixels,width,height,(int)p.X,(int)p.Y,r+1,0x281E2D);
            TopDisc(pixels,width,height,(int)p.X,(int)p.Y,r,enemy.Health>0?0xED6370u:0x67414Cu);
        }
        foreach(var projectile in projectiles)
        {
            Vector2 p=Pixel(projectile.Position),tail=Pixel(projectile.Position-Vector2.Normalize(projectile.Velocity)*.45f);
            uint color=projectile.Enemy?0xFFA861u:0xFFF2B0u;
            TopLine(pixels,width,height,tail,p,color);TopDisc(pixels,width,height,(int)p.X,(int)p.Y,Math.Max(1,(int)(scale*.07f)),color);
        }
        Vector2 agent=Pixel(position),forward=new(MathF.Cos(heading),MathF.Sin(heading)),side=new(-forward.Y,forward.X);
        int radius=Math.Max(4,(int)(scale*.26f));
        TopDisc(pixels,width,height,(int)agent.X,(int)agent.Y,radius+2,0x0E263B);
        TopDisc(pixels,width,height,(int)agent.X,(int)agent.Y,radius,0x6CCFF6);
        Vector2 tip=agent+forward*MathF.Max(radius+7,scale*.8f);
        TopLine(pixels,width,height,agent,tip,0xE0F8FF);
        TopLine(pixels,width,height,tip,agent+side*radius,0xE0F8FF);
        TopLine(pixels,width,height,tip,agent-side*radius,0xE0F8FF);
    }
    private static void TopRect(Span<uint> pixels,int width,int height,int x,int y,int w,int h,uint color)
    {
        int left=Math.Clamp(x,0,width),right=Math.Clamp(x+w,0,width),top=Math.Clamp(y,0,height),bottom=Math.Clamp(y+h,0,height);
        for(int row=top;row<bottom;row++)pixels.Slice(row*width+left,Math.Max(0,right-left)).Fill(color);
    }
    private static void TopDisc(Span<uint> pixels,int width,int height,int x,int y,int radius,uint color)
    {
        for(int dy=-radius;dy<=radius;dy++)
        { int half=(int)MathF.Sqrt(Math.Max(0,radius*radius-dy*dy));TopRect(pixels,width,height,x-half,y+dy,2*half+1,1,color); }
    }
    private static void TopLine(Span<uint> pixels,int width,int height,Vector2 a,Vector2 b,uint color,bool dashed=false)
    {
        int steps=Math.Max(1,(int)MathF.Ceiling(Vector2.Distance(a,b)));
        for(int i=0;i<=steps;i++)
        {
            if(dashed&&i%8>=4)continue;
            Vector2 p=Vector2.Lerp(a,b,(float)i/steps);int x=(int)p.X,y=(int)p.Y;
            if(x>=0&&y>=0&&x<width&&y<height)pixels[y*width+x]=color;
        }
    }
}
