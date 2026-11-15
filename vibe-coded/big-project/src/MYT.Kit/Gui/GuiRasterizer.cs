namespace MYT.Kit.Gui;

/// <summary>Nearest-neighbor scanline adapter for GUI quads on software or rectangle canvases.
/// The destination blends each span's straight alpha over its existing scene.</summary>
public static class GuiRasterizer
{
    public static void Draw(GuiDrawList list, GuiAtlas atlas, Action<int, int, int, GuiColor> span)
    {
        var vertices = list.Vertices;
        for (int i = 0; i < vertices.Length; i += 6)
        {
            var a = vertices[i];
            var c = vertices[i + 2];
            int left = (int)a.Position.x, top = (int)a.Position.y;
            int right = (int)c.Position.x, bottom = (int)c.Position.y;
            // Flat fills and gradients sample one atlas texel: emit one span per row.
            // Large window/panel backgrounds must not visit every pixel in managed code.
            if (a.Uv == c.Uv)
            {
                int tx=Math.Clamp((int)(a.Uv.x*atlas.Width),0,atlas.Width-1);
                int ty=Math.Clamp((int)(a.Uv.y*atlas.Height),0,atlas.Height-1);
                int pixel=(ty*atlas.Width+tx)*4;
                for(int y=top;y<bottom;y++)
                {
                    var tint=GuiColor.Lerp(a.Color,c.Color,(y+.5f-top)/(bottom-top));
                    var color=new GuiColor((byte)(atlas.Pixels[pixel]*tint.R/255),(byte)(atlas.Pixels[pixel+1]*tint.G/255),
                        (byte)(atlas.Pixels[pixel+2]*tint.B/255),(byte)(atlas.Pixels[pixel+3]*tint.A/255));
                    if(color.A>0)span(left,y,right-left,color);
                }
                continue;
            }
            for (int y = top; y < bottom; y++)
            {
                float t = (y + .5f - top) / (bottom - top);
                var tint = GuiColor.Lerp(a.Color, c.Color, t);
                int ty = Math.Clamp((int)((a.Uv.y + (c.Uv.y-a.Uv.y)*t)*atlas.Height), 0, atlas.Height-1);
                int run = left;
                GuiColor previous = default;
                for (int x = left; x <= right; x++)
                {
                    GuiColor color = default;
                    if (x < right)
                    {
                        float u = (x + .5f-left)/(right-left);
                        int tx = Math.Clamp((int)((a.Uv.x+(c.Uv.x-a.Uv.x)*u)*atlas.Width), 0, atlas.Width-1);
                        int pixel = (ty*atlas.Width+tx)*4;
                        color = new GuiColor((byte)(atlas.Pixels[pixel]*tint.R/255),
                            (byte)(atlas.Pixels[pixel+1]*tint.G/255),
                            (byte)(atlas.Pixels[pixel+2]*tint.B/255),
                            (byte)(atlas.Pixels[pixel+3]*tint.A/255));
                    }
                    if (x == left) previous = color;
                    if (color == previous && x < right) continue;
                    if (previous.A > 0 && x > run) span(run, y, x-run, previous);
                    run = x;
                    previous = color;
                }
            }
        }
    }
}
