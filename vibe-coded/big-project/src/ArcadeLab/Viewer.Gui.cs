using GlmSharp;
using MYT.Kit.Gui;
using System.Diagnostics;

namespace ArcadeLab;

public sealed unsafe partial class Viewer
{
    private readonly GuiAtlas guiAtlas = new();
    private GuiFont? guiFont;

    public GuiContext CreateGui()
    {
        guiFont ??= GuiDefaultFont.Create(guiAtlas);
        GuiButtonColors Colors(uint face, uint edge) => new(GuiColor.FromRgb(face), GuiColor.FromRgb(edge), GuiColor.FromRgb(0x101C2B), GuiColor.FromRgb(0x0B1420));
        return new GuiContext(guiAtlas, new GuiStyle(guiFont)
        {
            TextSize = 12, Text = GuiColor.FromRgb(0xDCEAF6), TextHovered = GuiColor.White,
            Button = Colors(0x203247,0x39556F), ButtonHovered = Colors(0x28516B,0x52BED2),
            ButtonDisabled = Colors(0x172330,0x263442),
            FieldBackground = GuiColor.FromRgb(0x0C1724), FieldBorder = GuiColor.FromRgb(0x39556F),
            FieldBorderFocused = GuiColor.FromRgb(0x52D6CB), ShadowBrightness = .15f
        });
    }

    public void RenderGui(GuiContext gui)
    {
        GuiRasterizer.Draw(gui.DrawList, guiAtlas, (x,y,length,color) =>
        {
            if (y < 0 || y >= extent.Height) return;
            int left = Math.Max(0,x), right = Math.Min((int)extent.Width,x+length);
            if (left >= right) return;
            if (framePixels is null && softwarePixels is null)
            {
                Rect(left,y,right-left,1,color.R/255f,color.G/255f,color.B/255f);
                return;
            }
            Span<int> pixels = softwarePixels is not null ? softwarePixels.AsSpan() : framePixels!.Ints;
            var row = pixels.Slice(y*(int)extent.Width+left,right-left);
            int packed = PackColor(color.R/255f,color.G/255f,color.B/255f);
            if (color.A == 255) { row.Fill(packed); return; }
            int alpha = color.A;
            for (int i=0;i<row.Length;i++)
            {
                int old = row[i];
                int r = ((packed&255)*alpha+(old&255)*(255-alpha))/255;
                int g = (((packed>>8)&255)*alpha+((old>>8)&255)*(255-alpha))/255;
                int b = (((packed>>16)&255)*alpha+((old>>16)&255)*(255-alpha))/255;
                row[i] = unchecked((int)0xFF000000) | r | g<<8 | b<<16;
            }
        });
    }

    private GuiInput? mazePreviewInput;
    private bool mazeManual, mazePaused, mazeReset, mazeGuiFrame;
    private void BeginMazeGui(float w,float h)
    {
        mazeGui ??= CreateGui();
        var cursor=window==0?(-100f,-100f):Cursor;
        mazeGui!.Begin(mazePreviewInput??new GuiInput { Cursor=new vec2(cursor.Item1,cursor.Item2),MouseDown=window!=0 && MouseButton(0),Time=(double)Stopwatch.GetTimestamp()/Stopwatch.Frequency },new vec2(w,h),1);
        mazeGuiFrame=true;
    }
    private void MazeToolbar(MazeSession session, float w, float h)
    {
        float y=h*.04f;
        mazeGui!.Gradient(new GuiRect(.025f*w,y,.61f*w,42),GuiColor.FromRgb(0x1A2D40),GuiColor.FromRgb(0x101E2D));
        float x=.035f*w, bw=(.58f*w-24)/4;
        if(mazeGui!.Button(new GuiRect(x,y+6,bw,30),mazeManual?"CONTROL: MANUAL":"CONTROL: AI"))mazeManual=!mazeManual;
        if(mazeGui!.Button(new GuiRect(x+bw+8,y+6,bw,30),mazePaused?"RESUME":"PAUSE"))mazePaused=!mazePaused;
        if(mazeGui!.Button(new GuiRect(x+2*(bw+8),y+6,bw,30),"RESTART EPISODE"))mazeReset=true;
        if(mazeGui!.Button(new GuiRect(x+3*(bw+8),y+6,bw,30),MazeGraphVisible?"HIDE GRAPH":"SHOW GRAPH"))MazeGraphVisible=!MazeGraphVisible;
        mazeGui!.Text(new vec2(.035f*w,.725f*h),$"LEVEL {session.Game.Level}   HP {session.Game.Health:F0}   AMMO {session.Game.Ammo}/{session.Game.Reserve}   EXITS {session.CompletedMazes}",GuiColor.FromRgb(0x86B4CC),10);
        mazeGui!.Text(new vec2(.035f*w,12),$"MYT / NEURAL MAZE   {(mazePaused?"PAUSED":session.Training?"TRAINING":"PLAYING")}",GuiColor.FromRgb(0x57D6C4),12);
        mazeGui!.End();RenderGui(mazeGui);mazeGuiFrame=false;
    }
    private GuiContext? mazeGui;
}
