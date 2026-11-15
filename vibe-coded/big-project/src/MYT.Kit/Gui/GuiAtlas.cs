using GlmSharp;

namespace MYT.Kit.Gui;


/// <summary>An image in a <see cref="GuiAtlas"/>: its corners in texture coordinates and its size in pixels.</summary>
public readonly record struct GuiImage(vec2 UvMin, vec2 UvMax, int Width, int Height)
{
    public vec2 Size => new(Width, Height);

    /// <summary>A part of the image, in its pixels from its top-left corner.</summary>
    public GuiImage Region(int x, int y, int width, int height)
    {
        vec2 texel = (UvMax - UvMin) / Size;
        vec2 low = UvMin + texel * new vec2(x, y);
        return new GuiImage(low, low + texel * new vec2(width, height), width, height);
    }
}


/// <summary>
/// The one texture the interface draws from: images packed side by side in rows (shelf
/// packing), each with its edge pixels repeated once around it, so that sampling at its edge
/// never picks up a neighbour. Its first image is a white pixel, which flat colors are drawn
/// with. The game uploads <see cref="Pixels"/> as an sRGB RGBA texture, sampled nearest and
/// clamped to its edge, once every image is in, and again whenever <see cref="Version"/> changes.
/// </summary>
public sealed class GuiAtlas
{
    private const int Padding = 1;

    public GuiAtlas(int width = 1024, int height = 1024)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        GuiBitmap white = new(1, 1);
        white[0, 0] = GuiColor.White;
        White = Add(white);
    }

    private int _shelfX;
    private int _shelfY;
    private int _shelfHeight;

    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA, a row at a time from the top.</summary>
    public byte[] Pixels { get; }

    /// <summary>Bumped by every image added.</summary>
    public int Version { get; private set; }

    /// <summary>A white pixel.</summary>
    public GuiImage White { get; }

    /// <summary>Copies an image in.</summary>
    /// <exception cref="InvalidOperationException">The atlas has no room left for it.</exception>
    public GuiImage Add(GuiBitmap bitmap)
    {
        int width = bitmap.Width + 2 * Padding;
        int height = bitmap.Height + 2 * Padding;
        if (width > Width)
            throw new ArgumentException($"A {bitmap.Width}×{bitmap.Height} image does not fit across a {Width}×{Height} atlas", nameof(bitmap));

        if (_shelfX + width > Width)
        {
            _shelfY += _shelfHeight;
            _shelfX = 0;
            _shelfHeight = 0;
        }

        if (_shelfY + height > Height)
            throw new InvalidOperationException($"The {Width}×{Height} atlas has no room left for a {bitmap.Width}×{bitmap.Height} image");

        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Clamp(y - Padding, 0, bitmap.Height - 1);
            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Clamp(x - Padding, 0, bitmap.Width - 1);
                int source = (sourceY * bitmap.Width + sourceX) * 4;
                int target = ((_shelfY + y) * Width + _shelfX + x) * 4;
                bitmap.Pixels.AsSpan(source, 4).CopyTo(Pixels.AsSpan(target));
            }
        }

        vec2 low = new vec2(_shelfX + Padding, _shelfY + Padding) / new vec2(Width, Height);
        vec2 high = low + new vec2(bitmap.Width, bitmap.Height) / new vec2(Width, Height);
        _shelfX += width;
        _shelfHeight = Math.Max(_shelfHeight, height);
        Version++;
        return new GuiImage(low, high, bitmap.Width, bitmap.Height);
    }
}
