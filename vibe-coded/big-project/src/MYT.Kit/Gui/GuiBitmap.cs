using StbImageSharp;

namespace MYT.Kit.Gui;


/// <summary>An image in memory: RGBA bytes, a row at a time from the top, to add to a <see cref="GuiAtlas"/>.</summary>
public sealed class GuiBitmap
{
    /// <summary>Reads an image file (PNG, among others) as RGBA.</summary>
    public static GuiBitmap Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return new GuiBitmap(image.Width, image.Height, image.Data);
    }

    /// <summary>A transparent image.</summary>
    public GuiBitmap(int width, int height) : this(width, height, new byte[width * height * 4])
    {
    }

    /// <param name="pixels">Width × height RGBA pixels, kept, not copied.</param>
    public GuiBitmap(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException($"An image must be at least 1×1, not {width}×{height}");
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"A {width}×{height} image has {width * height * 4} bytes, not {pixels.Length}", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public GuiColor this[int x, int y]
    {
        get
        {
            int i = (y * Width + x) * 4;
            return new GuiColor(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
        set
        {
            int i = (y * Width + x) * 4;
            Pixels[i] = value.R;
            Pixels[i + 1] = value.G;
            Pixels[i + 2] = value.B;
            Pixels[i + 3] = value.A;
        }
    }

    /// <summary>A copy of a part of it.</summary>
    public GuiBitmap Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) {width}×{height} reaches outside the {Width}×{Height} image");

        GuiBitmap part = new(width, height);
        for (int row = 0; row < height; row++)
            Pixels.AsSpan(((y + row) * Width + x) * 4, width * 4).CopyTo(part.Pixels.AsSpan(row * width * 4));

        return part;
    }
}
