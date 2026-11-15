using MYT.Kit.Graph;
using MYT.Kit.Gui;
using Silk.NET.Vulkan;

namespace ArcadeLab;

public sealed unsafe partial class Viewer
{
    public GraphDrawList Graph {get;}=new();
    private GraphGpuRenderer? graphRenderer;
    private Framebuffer[] graphFrames=[];
    private readonly GraphRasterizer graphReference=new();
    /// <summary>Small module labels use the GUI font in the final graph overlay, above weights.</summary>
    public void GraphLabel(GuiContext gui,GuiRect rect,string text,GuiColor color,bool selected=false)
    {
        Graph.OverlayRect(rect.Scaled(gui.Scale),GuiColor.FromRgb(0x0C1724).WithAlpha(.94f));
        if(selected)
        {
            Graph.OverlayRect(new GuiRect(rect.X,rect.Y,rect.Width,1).Scaled(gui.Scale),color);
            Graph.OverlayRect(new GuiRect(rect.X,rect.Bottom-1,rect.Width,1).Scaled(gui.Scale),color);
        }
        var font=gui.Style.Font;float step=9f/font.Height;
        float x=rect.X+4,y=rect.Y+4;
        foreach(char c in text)
        {
            var glyph=font.Glyph(c);var image=glyph.Image;
            int left=(int)MathF.Round(image.UvMin.x*guiAtlas.Width),top=(int)MathF.Round(image.UvMin.y*guiAtlas.Height);
            for(int row=0;row<image.Height;row++)for(int col=0;col<image.Width;col++)
                if(guiAtlas.Pixels[((top+row)*guiAtlas.Width+left+col)*4+3]>0)
                    Graph.OverlayRect(new GuiRect(x+col*step,y+row*step,step,step).Scaled(gui.Scale),color);
            x+=glyph.Advance*step;
        }
    }
    private void CreateGraphRenderer(Format format)
    {
        graphRenderer=new(context,format,extent);
        graphFrames=views.Select(graphRenderer.AddTarget).ToArray();
    }
    private void DrawGraphReference()
    {
        if(Graph.Guides.Length+Graph.Lines.Length+Graph.Highlights.Length+Graph.Neurons.Length+Graph.Overlays.Length==0)return;
        if(softwarePixels is not null)graphReference.Draw(Graph,softwarePixels,(int)extent.Width,(int)extent.Height,blueFirst);
        else if(framePixels is not null)graphReference.Draw(Graph,framePixels.Ints,(int)extent.Width,(int)extent.Height,blueFirst);
    }
    /// <summary>Offscreen captures call this after all UI backgrounds have been composed.</summary>
    public void FinishGraphPreview()
    {
        if(window==0)DrawGraphReference();
    }
}
