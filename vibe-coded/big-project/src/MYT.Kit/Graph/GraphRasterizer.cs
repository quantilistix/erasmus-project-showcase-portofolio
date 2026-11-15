using System.Numerics;
using MYT.Kit.Gui;

namespace MYT.Kit.Graph;

/// <summary>Offscreen reference for the Vulkan weight and neuron passes. No GUI quads per line pixel.</summary>
public sealed class GraphRasterizer
{
    private float[] depth=[];
    public void Draw(GraphDrawList list,Span<int> pixels,int width,int height,bool blueFirst=false)
    {
        if(depth.Length!=pixels.Length)depth=new float[pixels.Length];
        Array.Fill(depth,float.PositiveInfinity);
        int left=Math.Clamp((int)MathF.Ceiling(list.Clip.Left),0,width),top=Math.Clamp((int)MathF.Ceiling(list.Clip.Top),0,height);
        int right=Math.Clamp((int)MathF.Floor(list.Clip.Right),left,width),bottom=Math.Clamp((int)MathF.Floor(list.Clip.Bottom),top,height);
        if(right<=left||bottom<=top)return;
        DrawLines(list.Guides,pixels,width,left,top,right,bottom,blueFirst,false);
        DrawLines(list.Lines,pixels,width,left,top,right,bottom,blueFirst,true);
        DrawLines(list.Highlights,pixels,width,left,top,right,bottom,blueFirst,true);
        DrawNodes(list.Neurons,pixels,width,left,top,right,bottom,blueFirst,true);
        DrawNodes(list.Overlays,pixels,width,left,top,right,bottom,blueFirst,false);
    }
    private void DrawNodes(ReadOnlySpan<GraphVertex> nodes,Span<int> pixels,int width,int left,int top,int right,int bottom,bool blueFirst,bool testDepth)
    {
        for(int i=0;i<nodes.Length;i+=6)
        {
            var a=nodes[i];var c=nodes[i+2];
            int x0=Math.Max(left,(int)MathF.Ceiling(a.Position.X)),x1=Math.Min(right,(int)MathF.Ceiling(c.Position.X));
            int y0=Math.Max(top,(int)MathF.Ceiling(a.Position.Y)),y1=Math.Min(bottom,(int)MathF.Ceiling(c.Position.Y));
            int color=Pack(a.Color,blueFirst);bool round=a.Uv.X<1.5f;
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
            {
                float u=(x+.5f-a.Position.X)/(c.Position.X-a.Position.X)*2-1;
                float v=(y+.5f-a.Position.Y)/(c.Position.Y-a.Position.Y)*2-1;
                if(round&&u*u+v*v>1)continue;
                int at=y*width+x;if(testDepth&&a.Position.Z>depth[at])continue;
                if(testDepth)depth[at]=a.Position.Z;
                pixels[at]=Blend(color,pixels[at],a.Color.A);
            }
        }
    }
    private void DrawLines(ReadOnlySpan<GraphVertex> lines,Span<int> pixels,int width,int left,int top,int right,int bottom,bool blueFirst,bool testDepth)
    {
        for(int i=0;i<lines.Length;i+=2)
        {
            Vector3 a=lines[i].Position,b=lines[i+1].Position,d=b-a;
            float low=0,high=1;
            if(!Clip(-d.X,a.X-left,ref low,ref high)||!Clip(d.X,right-1-a.X,ref low,ref high)
                ||!Clip(-d.Y,a.Y-top,ref low,ref high)||!Clip(d.Y,bottom-1-a.Y,ref low,ref high)
                ||!Clip(-d.Z,a.Z,ref low,ref high)||!Clip(d.Z,1-a.Z,ref low,ref high))continue;
            b=a+d*high;a+=d*low;d=b-a;
            int steps=Math.Max(1,(int)MathF.Ceiling(MathF.Max(MathF.Abs(d.X),MathF.Abs(d.Y))));
            int color=Pack(lines[i].Color,blueFirst),previousAt=-1;
            for(int step=0;step<=steps;step++)
            {
                var p=a+d*((float)step/steps);int x=(int)MathF.Round(p.X),y=(int)MathF.Round(p.Y);
                if(x<left||x>=right||y<top||y>=bottom)continue;
                int at=y*width+x;
                if(at==previousAt)continue;previousAt=at;
                if(testDepth&&p.Z>depth[at])continue;
                if(testDepth)depth[at]=p.Z;
                pixels[at]=Blend(color,pixels[at],lines[i].Color.A);
            }
        }
    }
    private static int Blend(int source,int destination,int alpha)
    {
        if(alpha==255)return source;
        int r=((source&255)*alpha+(destination&255)*(255-alpha))/255;
        int g=(((source>>8)&255)*alpha+((destination>>8)&255)*(255-alpha))/255;
        int b=(((source>>16)&255)*alpha+((destination>>16)&255)*(255-alpha))/255;
        return unchecked((int)0xFF000000)|r|g<<8|b<<16;
    }
    private static bool Clip(float p,float q,ref float low,ref float high)
    {
        if(p==0)return q>=0;
        float r=q/p;
        if(p<0){if(r>high)return false;low=Math.Max(low,r);}
        else{if(r<low)return false;high=Math.Min(high,r);}
        return true;
    }
    private static int Pack(GuiColor c,bool blueFirst)=>unchecked((int)0xFF000000)|c.G<<8|(blueFirst?c.R<<16|c.B:c.B<<16|c.R);
}
