using System.Numerics;
using MYT.Kit.Graph;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public static class GraphOrientation
{
    public static readonly GuiColor XColor=GuiColor.FromRgb(0xEF8490);
    public static readonly GuiColor YColor=GuiColor.FromRgb(0x6CDBB2);
    public static readonly GuiColor ZColor=GuiColor.FromRgb(0x82A5EF);

    /// <summary>World-unit spacing with about 50 screen pixels between grid lines.</summary>
    public static float Spacing(float pixelsPerUnit)
    {
        float desired=50/Math.Max(.0001f,pixelsPerUnit);
        float decade=MathF.Pow(10,MathF.Floor(MathF.Log10(desired)));
        float fraction=desired/decade;
        return decade*(fraction<=1?1:fraction<=2?2:fraction<=5?5:10);
    }

    /// <summary>Three world-aligned planes around the viewed network. Edge-on planes are omitted
    /// to avoid piling transparent lines on top of one another. The batch stays bounded at all zooms.</summary>
    public static float AddGrid(GraphDrawList batch,Vector3 center,float pixelsPerUnit,float yaw,float pitch,Func<Vector3,Vector3> project,Action<Vector3,Vector3,GuiColor>? drawLine=null)
    {
        void Line(Vector3 a,Vector3 b,GuiColor color)
        {
            if(drawLine is null)batch.Guide(project(a),project(b),color);
            else drawLine(a,b,color);
        }
        float spacing=Spacing(pixelsPerUnit),extent=spacing*8;
        var origin=new Vector3(MathF.Round(center.X/spacing),MathF.Round(center.Y/spacing),MathF.Round(center.Z/spacing))*spacing;
        Vector3[] axes=[Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ];
        GuiColor[] colors=[XColor,YColor,ZColor];
        foreach(var (u,v) in new[]{(0,1),(0,2),(1,2)})
        {
            float facing=MathF.Abs(GraphCamera.Rotate(Vector3.Cross(axes[u],axes[v]),yaw,pitch).Z);
            if(facing<.08f)continue;
            for(int i=-8;i<=8;i++)
            {
                float opacity=(i%4==0?.20f:.10f)*MathF.Sqrt(facing);
                var color=GuiColor.FromRgb(0x789BB7).WithAlpha(opacity);
                if(i!=0)
                {
                    var offset=origin+axes[u]*(i*spacing);
                    Line(offset-axes[v]*extent,offset+axes[v]*extent,color);
                    offset=origin+axes[v]*(i*spacing);
                    Line(offset-axes[u]*extent,offset+axes[u]*extent,color);
                }
            }
        }
        for(int axis=0;axis<3;axis++)
        {
            Line(origin-axes[axis]*extent,origin,colors[axis].WithAlpha(.24f));
            Line(origin,origin+axes[axis]*extent,colors[axis].WithAlpha(.48f));
        }
        return spacing;
    }
}
