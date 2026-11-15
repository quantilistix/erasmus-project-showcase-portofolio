namespace MYT.Kit.Gui;


/// <summary>The colors of a beveled frame, as of a button: a border, light top and left edges, dark bottom and right ones.</summary>
public readonly record struct GuiButtonColors(GuiColor Face, GuiColor Highlight, GuiColor Shade, GuiColor Border);


/// <summary>
/// How the interface looks: its font, colors and the sizes of the parts of elements, in
/// units. The defaults are those of Minecraft's classic menus: gray stone buttons that turn
/// blue under the pointer, light gray text with a shadow a quarter as bright, and black text
/// fields.
/// </summary>
public sealed record GuiStyle(GuiFont Font)
{
    /// <summary>Height of a line of text, units; the font's own height keeps its pixels whole.</summary>
    public float TextSize { get; init; } = Font.Height;

    /// <summary>How bright a text's shadow is next to the text, a font pixel down and right of it.</summary>
    public float ShadowBrightness { get; init; } = 0.25f;

    public GuiColor Text { get; init; } = GuiColor.FromRgb(0xE0E0E0);
    public GuiColor TextHovered { get; init; } = GuiColor.FromRgb(0xFFFFA0);
    public GuiColor TextDisabled { get; init; } = GuiColor.FromRgb(0xA0A0A0);
    public GuiColor Placeholder { get; init; } = GuiColor.FromRgb(0x707070);

    public GuiButtonColors Button { get; init; } = new
    (
        GuiColor.FromRgb(0x6F6F6F),
        GuiColor.FromRgb(0xA9A9A9),
        GuiColor.FromRgb(0x4A4A4A),
        GuiColor.Black
    );

    public GuiButtonColors ButtonHovered { get; init; } = new
    (
        GuiColor.FromRgb(0x7E88BF),
        GuiColor.FromRgb(0xBCC5F5),
        GuiColor.FromRgb(0x565F95),
        GuiColor.Black
    );

    public GuiButtonColors ButtonDisabled { get; init; } = new
    (
        GuiColor.FromRgb(0x2C2C2C),
        GuiColor.FromRgb(0x3A3A3A),
        GuiColor.FromRgb(0x1E1E1E),
        GuiColor.Black
    );

    public GuiColor FieldBackground { get; init; } = GuiColor.Black;
    public GuiColor FieldBorder { get; init; } = GuiColor.FromRgb(0xA0A0A0);
    public GuiColor FieldBorderFocused { get; init; } = GuiColor.White;
    public GuiColor Caret { get; init; } = GuiColor.FromRgb(0xD0D0D0);

    /// <summary>Space between a text field's border and its text, units.</summary>
    public float FieldPadding { get; init; } = 4f;

    public GuiColor ListBackground { get; init; } = GuiColor.Black.WithAlpha(0.5f);
    public GuiColor ListHover { get; init; } = GuiColor.White.WithAlpha(0.08f);
    public GuiColor ListSelectionBorder { get; init; } = GuiColor.FromRgb(0x808080);
    public GuiColor ListSelection { get; init; } = GuiColor.Black;

    public GuiColor ScrollTrack { get; init; } = GuiColor.Black;
    public GuiColor ScrollThumb { get; init; } = GuiColor.FromRgb(0x808080);
    public GuiColor ScrollThumbHighlight { get; init; } = GuiColor.FromRgb(0xC0C0C0);
    public float ScrollbarWidth { get; init; } = 6f;

    /// <summary>Width of a slider's handle, units.</summary>
    public float SliderHandleWidth { get; init; } = 8f;
}
