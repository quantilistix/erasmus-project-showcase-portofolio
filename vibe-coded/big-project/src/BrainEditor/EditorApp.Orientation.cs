using System.Numerics;
using MYT.Kit.Gui;
using GlmSharp;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private void DrawOrientation(GuiRect clip,Position center,float fit,bool grid)
    {
        var batch=viewer.Graph;
        batch.Clip=clip.Scaled(gui.Scale);
        var camera=Camera(center,fit,graphSpan);
        Vector3 Pixel(Vector3 p)=>new(p.X*gui.Scale,p.Y*gui.Scale,p.Z);
        Vector3 ProjectGuide(Vector3 p)
        {
            camera.TryProject(p,out var screen);return Pixel(screen);
        }
        void Guide(Vector3 a,Vector3 b,GuiColor color)
        {
            if(camera.ProjectSegment(a,b,out var from,out var to))batch.Guide(Pixel(from),Pixel(to),color);
        }
        if(grid)
        {
            float spacing=GraphOrientation.AddGrid(batch,new(center.X,center.Y,center.Z),fit*zoom,yaw,pitch,ProjectGuide,Guide);
            gui.Text(new vec2(clip.Left+8,clip.Top+8),$"GRID {spacing:G3} u",GuiColor.FromRgb(0x789BB7),9);
        }
        // Fixed-size triad follows rotation but stays visible while the scene is panned or zoomed.
        var origin=new Vector3(clip.Right-58,clip.Top+54,.5f);
        var axes=new[]{(Vector3.UnitX,GraphOrientation.XColor,"X"),(Vector3.UnitY,GraphOrientation.YColor,"Y"),(Vector3.UnitZ,GraphOrientation.ZColor,"Z")};
        foreach(var (axis,color,label) in axes.OrderByDescending(a=>GraphCamera.Rotate(a.Item1,yaw,pitch).Z))
        {
            var r=GraphCamera.Rotate(axis,yaw,pitch);
            var tip=new Vector3(origin.X+r.X*28,origin.Y+r.Y*28,.5f);
            batch.Guide(Pixel(origin),Pixel(tip),color.WithAlpha(.9f));
            batch.Neuron(Pixel(tip),2.5f*gui.Scale,color);
            gui.Text(new vec2(tip.X+5,tip.Y-4),label,color,10);
        }
    }
}
