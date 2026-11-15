using System.Runtime.InteropServices;

namespace MYT.Kit.Gui;


/// <summary>
/// An sRGB color with straight alpha, a byte a channel, as the interface's vertices carry it
/// (<c>R8G8B8A8_UNORM</c>). A renderer drawing into an sRGB target turns it linear first.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct GuiColor(byte R, byte G, byte B, byte A = 255)
{
    public static readonly GuiColor White = new(255, 255, 255);
    public static readonly GuiColor Black = new(0, 0, 0);
    public static readonly GuiColor Transparent = new(0, 0, 0, 0);

    /// <summary>An opaque color from 0xRRGGBB.</summary>
    public static GuiColor FromRgb(uint rgb)
    {
        return new GuiColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>A color from 0xRRGGBBAA.</summary>
    public static GuiColor FromRgba(uint rgba)
    {
        return new GuiColor((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
    }

    /// <summary>The color a fraction <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static GuiColor Lerp(GuiColor a, GuiColor b, float t)
    {
        return new GuiColor(Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t), Mix(a.A, b.A, t));
    }

    private static byte Mix(byte a, byte b, float t)
    {
        return (byte)MathF.Round(a + (b - a) * t);
    }

    /// <summary>The same color with an opacity from 0 to 1.</summary>
    public GuiColor WithAlpha(float alpha)
    {
        return this with { A = (byte)MathF.Round(Math.Clamp(alpha, 0f, 1f) * 255f) };
    }

    /// <summary>The red, green and blue channels times <paramref name="factor"/>, alpha kept: darker below 1.</summary>
    public GuiColor Scaled(float factor)
    {
        return new GuiColor(Scale(R, factor), Scale(G, factor), Scale(B, factor), A);
    }

    private static byte Scale(byte channel, float factor)
    {
        return (byte)Math.Clamp(MathF.Round(channel * factor), 0f, 255f);
    }
}
