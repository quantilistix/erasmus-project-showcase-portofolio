using System.Numerics;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private sealed record ConnectionLabel(string Id,GuiRect Rect,string Text,GuiColor Color);
    private List<ConnectionLabel> DrawConnections(LinkScene scene,PerspectiveCamera camera,
        Action<Vector3,Vector3,string> consider,Action<Vector3,Vector3,GuiColor,bool> weight,Action<Vector3,Vector3,GuiColor,bool> arrow)
    {
        var labels=new List<ConnectionLabel>();int index=0;
        foreach(var link in document.Links)
        {
            if(view==1&&link.Id!=selectedLink)continue;
            if(!scene.ById.TryGetValue(link.Source.ModuleId,out var source)||!scene.ById.TryGetValue(link.Destination.ModuleId,out var dest))continue;
            bool selected=link.Id==selectedLink;
            uint[] palette=[0x65DACC,0xF1B96B,0x9CA8FF,0xE794CA,0x86CDEF];
            var color=GuiColor.FromRgb(scene.Graph.LinkErrors.ContainsKey(link.Id)?0xFF7585u:selected?0xFFE19Du:palette[index%palette.Length]);
            Vector3 a=scene.Port(link.Source),b=scene.Port(link.Destination);
            float lift=Math.Max(1,(b-a).Length()*.18f)+(index%4)*.6f;
            var offset=Vector3.UnitZ*lift;
            // A bowed path separates parallel links and self-links from internal weights.
            Vector3 control=(a+b)*.5f+offset;
            bool loop=Vector3.DistanceSquared(a,b)<.01f;
            Vector3 Point(float t)
            {
                float u=1-t;
                if(loop)return a*(u*u*u)+(a+new Vector3(lift*2,-lift,lift*2))*(3*u*u*t)
                    +(b+new Vector3(-lift*2,lift,lift*2))*(3*u*t*t)+b*(t*t*t);
                return a*(u*u)+control*(2*t*u)+b*(t*t);
            }
            Vector3 previous=Point(0);Vector3 labelPoint=default;bool visible=false;
            for(int step=1;step<=32;step++)
            {
                var next=Point(step/32f);
                if(camera.ProjectSegment(previous,next,out var pa,out var pb))
                {
                    consider(pa,pb,link.Id);
                    for(int thickness=-1;thickness<=1;thickness++)weight(pa+new Vector3(thickness,0,0),pb+new Vector3(thickness,0,0),color,true);
                    if(step==16){labelPoint=pb;visible=true;}
                }
                previous=next;
            }
            if(camera.ProjectSegment(Point(.45f),Point(.92f),out var arrowStart,out var arrowEnd))arrow(arrowStart,arrowEnd,color,true);
            if(selected&&view==0&&visible&&ViewportGeometry.MarkerVisible(CanvasContent,labelPoint,20))
            {
                string text=link.Name=="Link"?source.Module.Name+" > "+dest.Module.Name:link.Name;
                int maxChars=Math.Max(8,Math.Min(28,(int)((CanvasContent.Width-16)/6.75f)));
                if(text.Length>maxChars)text=text[..(maxChars-3)]+"...";
                float w=Math.Min(CanvasContent.Width-12,gui.Style.Font.Measure(text)*9/gui.Style.Font.Height+8);
                labels.Add(new(link.Id,new(Math.Clamp(labelPoint.X-w*.5f,CanvasContent.Left+4,CanvasContent.Right-w-4),
                    Math.Clamp(labelPoint.Y-22,CanvasContent.Top+4,CanvasContent.Bottom-44),w,18),text,color));
            }
            index++;
        }
        return labels;
    }
}
