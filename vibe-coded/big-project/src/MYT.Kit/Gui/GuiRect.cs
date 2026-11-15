using GlmSharp;

namespace MYT.Kit.Gui;


/// <summary>An axis-aligned rectangle, y down: its top-left corner and its size.</summary>
public readonly record struct GuiRect(float X, float Y, float Width, float Height)
{
    public static GuiRect FromEdges(float left, float top, float right, float bottom)
    {
        return new GuiRect(left, top, right - left, bottom - top);
    }

    /// <summary>A rectangle of a size centered on a point.</summary>
    public static GuiRect Centered(vec2 center, vec2 size)
    {
        return new GuiRect(center.x - size.x / 2f, center.y - size.y / 2f, size.x, size.y);
    }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public vec2 Position => new(X, Y);
    public vec2 Size => new(Width, Height);
    public vec2 Center => new(X + Width / 2f, Y + Height / 2f);

    /// <summary>Whether it covers nothing.</summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>Whether a point lies in it, its left and top edges included, its right and bottom edges not.</summary>
    public bool Contains(vec2 point)
    {
        return point.x >= X && point.y >= Y && point.x < Right && point.y < Bottom;
    }

    /// <summary>The part of it inside <paramref name="other"/>; empty where they do not overlap.</summary>
    public GuiRect Intersect(GuiRect other)
    {
        float left = Math.Max(Left, other.Left);
        float top = Math.Max(Top, other.Top);
        float right = Math.Max(left, Math.Min(Right, other.Right));
        float bottom = Math.Max(top, Math.Min(Bottom, other.Bottom));
        return FromEdges(left, top, right, bottom);
    }

    /// <summary>It shrunk by <paramref name="amount"/> on every side, or grown for a negative amount.</summary>
    public GuiRect Inset(float amount)
    {
        return new GuiRect(X + amount, Y + amount, Width - 2f * amount, Height - 2f * amount);
    }

    public GuiRect Offset(float x, float y)
    {
        return this with { X = X + x, Y = Y + y };
    }

    /// <summary>It with every coordinate times <paramref name="factor"/>, as from units to pixels.</summary>
    public GuiRect Scaled(float factor)
    {
        return new GuiRect(X * factor, Y * factor, Width * factor, Height * factor);
    }

    /// <summary>
    /// The <paramref name="index"/>th of <paramref name="count"/> equal columns it splits into,
    /// <paramref name="gap"/> apart, as for a row of buttons.
    /// </summary>
    public GuiRect Column(int index, int count, float gap)
    {
        float width = (Width - gap * (count - 1)) / count;
        return this with { X = X + index * (width + gap), Width = width };
    }
}
