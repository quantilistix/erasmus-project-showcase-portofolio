using System.Runtime.InteropServices;

using GlmSharp;

namespace MYT.Kit.Gui;


/// <summary>
/// A vertex of the interface: its position in pixels from the top-left corner of the screen,
/// where it is in the atlas, and the color its texel is multiplied by. 20 bytes: two
/// <c>R32G32_SFLOAT</c> attributes and an <c>R8G8B8A8_UNORM</c> one.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GuiVertex(vec2 Position, vec2 Uv, GuiColor Color);


/// <summary>
/// What the interface draws in a frame: textured, tinted quads from one atlas, as a list of
/// triangles, in drawing order, ready to copy into a vertex buffer and draw in a single call
/// with alpha blending. Every quad is axis-aligned, so cutting it to a clip rectangle is done
/// here, exactly, and a renderer needs no scissor; its edges are rounded to whole pixels, so
/// pixel art stays sharp.
/// </summary>
public sealed class GuiDrawList
{
    private readonly List<GuiVertex> _vertices = [];
    // Each within the one under it.
    private readonly Stack<GuiRect> _clips = new();

    public ReadOnlySpan<GuiVertex> Vertices => CollectionsMarshal.AsSpan(_vertices);

    /// <summary>Where drawing is limited to now, in pixels; null for nowhere.</summary>
    public GuiRect? Clip => _clips.Count > 0 ? _clips.Peek() : null;

    public void Clear()
    {
        _vertices.Clear();
        _clips.Clear();
    }

    /// <summary>Limits what is drawn to a rectangle (pixels), within the current limit, until <see cref="PopClip"/>.</summary>
    public void PushClip(GuiRect rect)
    {
        GuiRect snapped = Snap(rect);
        _clips.Push(_clips.Count > 0 ? snapped.Intersect(_clips.Peek()) : snapped);
    }

    public void PopClip()
    {
        _clips.Pop();
    }

    /// <summary>Adds a rectangle (pixels) showing the atlas between two texture coordinates, tinted.</summary>
    public void Quad(GuiRect rect, vec2 uvMin, vec2 uvMax, GuiColor color)
    {
        Quad(rect, uvMin, uvMax, color, color);
    }

    /// <summary>Adds a rectangle (pixels) showing the atlas between two texture coordinates, tinted from one color at its top to another at its bottom.</summary>
    public void Quad(GuiRect rect, vec2 uvMin, vec2 uvMax, GuiColor top, GuiColor bottom)
    {
        if (top.A == 0 && bottom.A == 0)
            return;

        GuiRect whole = Snap(rect);
        GuiRect shown = _clips.Count > 0 ? whole.Intersect(_clips.Peek()) : whole;
        if (shown.IsEmpty)
            return;

        vec2 uvSize = uvMax - uvMin;
        vec2 uvLow = uvMin + uvSize * (shown.Position - whole.Position) / whole.Size;
        vec2 uvHigh = uvMin + uvSize * (shown.Position + shown.Size - whole.Position) / whole.Size;
        GuiColor upper = GuiColor.Lerp(top, bottom, (shown.Top - whole.Top) / whole.Height);
        GuiColor lower = GuiColor.Lerp(top, bottom, (shown.Bottom - whole.Top) / whole.Height);

        GuiVertex a = new(new vec2(shown.Left, shown.Top), uvLow, upper);
        GuiVertex b = new(new vec2(shown.Right, shown.Top), new vec2(uvHigh.x, uvLow.y), upper);
        GuiVertex c = new(new vec2(shown.Right, shown.Bottom), uvHigh, lower);
        GuiVertex d = new(new vec2(shown.Left, shown.Bottom), new vec2(uvLow.x, uvHigh.y), lower);
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _vertices.Add(a);
        _vertices.Add(c);
        _vertices.Add(d);
    }

    /// <summary>
    /// The rectangle with its edges rounded to whole pixels, halves up, so that two quads at
    /// the same fraction of a pixel move the same way and stay as far apart.
    /// </summary>
    private static GuiRect Snap(GuiRect rect)
    {
        return GuiRect.FromEdges(Round(rect.Left), Round(rect.Top), Round(rect.Right), Round(rect.Bottom));
    }

    private static float Round(float value)
    {
        return MathF.Floor(value + 0.5f);
    }
}
