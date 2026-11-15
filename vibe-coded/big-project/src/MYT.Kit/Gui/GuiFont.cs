namespace MYT.Kit.Gui;


/// <summary>
/// A character of a <see cref="GuiFont"/>: its image, drawn from the pen, and how far the pen
/// moves on after it, in font pixels. A blank one (a space) has an image 0 wide.
/// </summary>
public readonly record struct GuiGlyph(GuiImage Image, float Advance);


/// <summary>
/// How the glyphs of a font sheet are laid out: one per cell of a grid of equal cells, in
/// reading order, each glyph <see cref="Inset"/> pixels in from its cell's top-left corner.
/// </summary>
/// <param name="PixelSize">
/// Sheet pixels per font pixel, for a sheet drawn larger than the font is: a glyph is read
/// a pixel every this many.
/// </param>
public readonly record struct GuiFontGrid(int CellWidth, int CellHeight, int GlyphWidth, int GlyphHeight, int Inset = 0, int PixelSize = 1);


/// <summary>
/// A bitmap font: an image per character, in a <see cref="GuiAtlas"/>. Drawn <c>size</c>
/// units high, a font pixel is <c>size / Height</c> units, so a font drawn at its own height
/// at a whole scale keeps its pixels square and sharp.
/// </summary>
public sealed class GuiFont
{
    /// <summary>
    /// Cuts a font out of a sheet laid out as a grid and adds its glyphs to an atlas. Ink is
    /// what is opaque: a sheet of dark ink on a light ground needs turning into white ink on
    /// a transparent ground first.
    /// </summary>
    /// <param name="characters">Each character and the cell of the grid holding it, counted along the rows.</param>
    /// <param name="spacing">
    /// Null for a fixed-width font, whose glyphs all advance their cell's width. Otherwise
    /// the font is proportional: every glyph is cut down to the columns holding ink and
    /// advances that much plus this many pixels, and blank ones advance half a glyph's width.
    /// </param>
    /// <param name="fallback">The character drawn for those the font lacks.</param>
    public static GuiFont FromGrid
    (
        GuiAtlas atlas,
        GuiBitmap sheet,
        GuiFontGrid grid,
        IEnumerable<(char Character, int Cell)> characters,
        int? spacing = null,
        char fallback = '?'
    )
    {
        int columns = sheet.Width / grid.CellWidth;
        int width = grid.GlyphWidth / grid.PixelSize;
        int height = grid.GlyphHeight / grid.PixelSize;
        Dictionary<char, GuiGlyph> glyphs = [];
        foreach ((char character, int cell) in characters)
        {
            int left = cell % columns * grid.CellWidth + grid.Inset;
            int top = cell / columns * grid.CellHeight + grid.Inset;
            GuiBitmap glyph = new(width, height);
            int first = width;
            int last = -1;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                GuiColor pixel = sheet[left + x * grid.PixelSize, top + y * grid.PixelSize];
                glyph[x, y] = pixel;
                if (pixel.A > 0)
                {
                    first = Math.Min(first, x);
                    last = Math.Max(last, x);
                }
            }

            if (last < 0)
                glyphs[character] = new GuiGlyph(default, spacing == null ? width : width / 2f);
            else if (spacing is int gap)
                glyphs[character] = new GuiGlyph(atlas.Add(glyph.Crop(first, 0, last - first + 1, height)), last - first + 1 + gap);
            else
                glyphs[character] = new GuiGlyph(atlas.Add(glyph), width);
        }

        return new GuiFont(height, glyphs, fallback);
    }

    /// <param name="height">Font pixels from the top of a line to the top of the next.</param>
    public GuiFont(int height, IReadOnlyDictionary<char, GuiGlyph> glyphs, char fallback = '?')
    {
        if (!glyphs.ContainsKey(fallback))
            throw new ArgumentException($"The font has no '{fallback}' to draw the characters it lacks with", nameof(fallback));

        Height = height;
        _glyphs = glyphs;
        _fallback = glyphs[fallback];
    }

    private readonly IReadOnlyDictionary<char, GuiGlyph> _glyphs;
    private readonly GuiGlyph _fallback;

    /// <summary>Font pixels from the top of a line to the top of the next.</summary>
    public int Height { get; }

    public GuiGlyph Glyph(char character)
    {
        return _glyphs.TryGetValue(character, out GuiGlyph glyph) ? glyph : _fallback;
    }

    /// <summary>How far a line of text advances the pen, in font pixels.</summary>
    public float Measure(ReadOnlySpan<char> text)
    {
        float width = 0f;
        foreach (char character in text)
            width += Glyph(character).Advance;

        return width;
    }
}
