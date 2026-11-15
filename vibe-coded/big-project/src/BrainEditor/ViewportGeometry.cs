using System.Numerics;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public static class ViewportGeometry
{
    public static GuiRect CanvasBounds(int view,ModuleKind? kind,float width,float height)
    {
        float top=view==1?(kind==ModuleKind.Custom?228:190):154;
        return new(12,top,Math.Max(1,width-474),Math.Max(1,height-top-74));
    }
    public static bool Overlaps(GuiRect canvas,GuiRect item)=>float.IsFinite(item.X)&&float.IsFinite(item.Y)&&!canvas.Intersect(item).IsEmpty;
    public static bool MarkerVisible(GuiRect canvas,Vector3 center,float radius)
        =>float.IsFinite(center.X)&&float.IsFinite(center.Y)&&center.Z>=0&&center.Z<=1
            &&Overlaps(canvas,new(center.X-radius,center.Y-radius,2*radius,2*radius));
    public static bool ClipLine(GuiRect canvas,ref Vector2 from,ref Vector2 to)
    {
        if(!float.IsFinite(from.X)||!float.IsFinite(from.Y)||!float.IsFinite(to.X)||!float.IsFinite(to.Y))return false;
        var delta=to-from;float low=0,high=1;
        if(!Clip(-delta.X,from.X-canvas.Left,ref low,ref high)||!Clip(delta.X,canvas.Right-from.X,ref low,ref high)
            ||!Clip(-delta.Y,from.Y-canvas.Top,ref low,ref high)||!Clip(delta.Y,canvas.Bottom-from.Y,ref low,ref high))return false;
        to=from+delta*high;from+=delta*low;return true;
    }
    private static bool Clip(float p,float q,ref float low,ref float high)
    {
        if(p==0)return q>=0;
        float t=q/p;
        if(p<0){if(t>high)return false;low=Math.Max(low,t);}
        else{if(t<low)return false;high=Math.Min(high,t);}
        return true;
    }
}
