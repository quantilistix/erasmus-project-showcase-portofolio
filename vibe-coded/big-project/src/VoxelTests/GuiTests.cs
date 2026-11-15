using GlmSharp;

using MYT.Kit.Gui;

namespace VoxelTests;


/// <summary>
/// Checks of the immediate-mode interface (MYT.Kit.Gui), on the CPU: packing the atlas,
/// cutting a font, clipping quads, and what each element does with the pointer and the keys.
/// The font is made up, so that its metrics are known: 'a' is 3 pixels of ink, 'b' 5, '?' 2.
/// </summary>
internal static class GuiTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("gui-atlas", Atlas),
        ("gui-font", Font),
        ("gui-clipping", Clipping),
        ("gui-button", Button),
        ("gui-slider", Slider),
        ("gui-text-field", TextField),
        ("gui-list", List),
        ("gui-wrap", Wrap)
    ];

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static bool Expect(string what, bool condition)
    {
        Console.WriteLine($"   {(condition ? "ok  " : "BAD ")} {what}");
        return condition;
    }

    /// <summary>A font sheet of four 8 × 8 cells: 'a', 'b', a blank one and '?', ink from the left edge.</summary>
    private static (GuiAtlas Atlas, GuiFont Font) MadeUpFont(int? spacing = 1)
    {
        GuiAtlas atlas = new(64, 64);
        GuiBitmap sheet = new(32, 8);
        int[] widths = [3, 5, 0, 2];
        for (int cell = 0; cell < widths.Length; cell++)
        for (int x = 0; x < widths[cell]; x++)
        for (int y = 1; y < 7; y++)
            sheet[cell * 8 + x, y] = GuiColor.White;

        GuiFont font = GuiFont.FromGrid(atlas, sheet, new GuiFontGrid(8, 8, 8, 8), [('a', 0), ('b', 1), (' ', 2), ('?', 3)], spacing);
        return (atlas, font);
    }

    private static GuiContext Context()
    {
        (GuiAtlas atlas, GuiFont font) = MadeUpFont();
        return new GuiContext(atlas, new GuiStyle(font));
    }

    /// <summary>Runs one frame of the interface at scale 1 on a 400 × 300 screen.</summary>
    private static void Frame(GuiContext gui, GuiInput input, Action draw)
    {
        gui.Begin(input, new vec2(400f, 300f), 1f);
        draw();
        gui.End();
    }

    private static GuiInput Pointer(float x, float y, bool down = false, double time = 0.0)
    {
        return new GuiInput { Cursor = new vec2(x, y), MouseDown = down, Time = time };
    }

    /// <summary>Every image lands whole, with its edges repeated around it, and a full atlas says so.</summary>
    private static bool Atlas()
    {
        GuiAtlas atlas = new(32, 16);
        bool ok = Expect("the first image is a white pixel", atlas.White.Width == 1 && GuiColorAt(atlas, atlas.White, 0, 0) == GuiColor.White);

        GuiBitmap checker = new(4, 3);
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 4; x++)
            checker[x, y] = new GuiColor((byte)(x * 60), (byte)(y * 100), 7);

        GuiImage first = atlas.Add(checker);
        GuiImage second = atlas.Add(checker);
        bool same = true;
        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 4; x++)
            same &= GuiColorAt(atlas, first, x, y) == checker[x, y] && GuiColorAt(atlas, second, x, y) == checker[x, y];
        ok &= Expect("both copies hold the image exactly", same);
        ok &= Expect("the edge pixels are repeated around it", GuiColorAt(atlas, first, -1, -1) == checker[0, 0] && GuiColorAt(atlas, first, 4, 2) == checker[3, 2]);
        ok &= Expect("images do not overlap", first.UvMax.x <= second.UvMin.x || first.UvMax.y <= second.UvMin.y);
        ok &= Check("version, bumped by each image", atlas.Version, 3, 3);

        bool full = false;
        try
        {
            for (int i = 0; i < 100; i++)
                atlas.Add(checker);
        }
        catch (InvalidOperationException)
        {
            full = true;
        }

        ok &= Expect("a full atlas refuses more", full);
        return ok;
    }

    private static GuiColor GuiColorAt(GuiAtlas atlas, GuiImage image, int x, int y)
    {
        int left = (int)MathF.Round(image.UvMin.x * atlas.Width) + x;
        int top = (int)MathF.Round(image.UvMin.y * atlas.Height) + y;
        int i = (top * atlas.Width + left) * 4;
        return new GuiColor(atlas.Pixels[i], atlas.Pixels[i + 1], atlas.Pixels[i + 2], atlas.Pixels[i + 3]);
    }

    /// <summary>A proportional font advances each glyph its ink and the spacing; a fixed-width one its cell.</summary>
    private static bool Font()
    {
        (_, GuiFont font) = MadeUpFont();
        bool ok = Check("advance of 'a'", font.Glyph('a').Advance, 4, 4);
        ok &= Check("advance of 'b'", font.Glyph('b').Advance, 6, 6);
        ok &= Check("advance of a space, half a cell", font.Glyph(' ').Advance, 4, 4);
        ok &= Check("width of 'a' cut to its ink", font.Glyph('a').Image.Width, 3, 3);
        ok &= Check("a missing character falls back to '?'", font.Glyph('z').Advance, 3, 3);
        ok &= Check("width of \"ab a\"", font.Measure("ab a"), 18, 18);

        (_, GuiFont fixedWidth) = MadeUpFont(spacing: null);
        ok &= Check("fixed width: advance of 'a'", fixedWidth.Glyph('a').Advance, 8, 8);
        ok &= Check("fixed width: glyph image is the whole cell", fixedWidth.Glyph('a').Image.Width, 8, 8);

        GuiContext gui = Context();
        Frame(gui, default, () => gui.Text(new vec2(10f, 10f), "ab", GuiColor.White, size: 16f));
        // Two glyphs, each with its shadow: four quads, at twice the font's size.
        ok &= Check("vertices for \"ab\" with a shadow", gui.DrawList.Vertices.Length, 24, 24);
        ok &= Check("the second glyph starts after the first's advance, doubled", gui.DrawList.Vertices[18].Position.x, 18, 18);
        return ok;
    }

    /// <summary>A quad is cut to the clip, its texture coordinates with it, and rounded to whole pixels.</summary>
    private static bool Clipping()
    {
        GuiDrawList list = new();
        list.PushClip(new GuiRect(10f, 10f, 20f, 20f));
        list.Quad(new GuiRect(0f, 0f, 20.4f, 40f), new vec2(0f, 0f), new vec2(1f, 1f), GuiColor.White);
        list.Quad(new GuiRect(40f, 40f, 5f, 5f), new vec2(0f, 0f), new vec2(1f, 1f), GuiColor.White);
        list.PopClip();

        ReadOnlySpan<GuiVertex> v = list.Vertices;
        bool ok = Check("vertices: the quad outside the clip is left out", v.Length, 6, 6);
        ok &= Check("left, cut to the clip", v[0].Position.x, 10, 10);
        ok &= Check("right, rounded to a whole pixel", v[1].Position.x, 20, 20);
        ok &= Check("bottom, cut to the clip", v[2].Position.y, 30, 30);
        ok &= Check("left texture coordinate where the clip cut", v[0].Uv.x, 0.5, 0.5);
        ok &= Check("bottom texture coordinate where the clip cut", v[2].Uv.y, 0.75, 0.75);

        list.Clear();
        list.PushClip(new GuiRect(0f, 5f, 10f, 10f));
        list.Quad(new GuiRect(0f, 0f, 10f, 20f), vec2.Zero, vec2.Ones, GuiColor.Black, GuiColor.White);
        list.PopClip();
        ok &= Check("a gradient cut a quarter down starts a quarter of the way", list.Vertices[0].Color.R, 63, 65);
        ok &= Check("and ends three quarters of the way", list.Vertices[2].Color.R, 190, 192);
        return ok;
    }

    /// <summary>A button clicks once, on the press over it; never disabled, nor from a press elsewhere dragged over it.</summary>
    private static bool Button()
    {
        GuiContext gui = Context();
        GuiRect rect = new(100f, 100f, 200f, 20f);
        int clicks = 0;
        int disabledClicks = 0;
        void Draw()
        {
            clicks += gui.Button(rect, "a") ? 1 : 0;
            disabledClicks += gui.Button(rect.Offset(0f, 40f), "b", enabled: false) ? 1 : 0;
        }

        Frame(gui, Pointer(150f, 110f), Draw);
        Frame(gui, Pointer(150f, 110f, down: true), Draw);
        Frame(gui, Pointer(150f, 110f, down: true), Draw);
        Frame(gui, Pointer(150f, 110f), Draw);
        bool ok = Check("clicks from one press held over it", clicks, 1, 1);

        Frame(gui, Pointer(10f, 10f, down: true), Draw);
        Frame(gui, Pointer(150f, 110f, down: true), Draw);
        Frame(gui, Pointer(150f, 110f), Draw);
        ok &= Check("clicks from a press elsewhere dragged over it", clicks, 1, 1);

        Frame(gui, Pointer(150f, 150f, down: true), Draw);
        ok &= Check("clicks of a disabled button", disabledClicks, 0, 0);
        return ok;
    }

    /// <summary>A slider follows the pointer while dragged, even off it, keeps to its steps, and LEFT and RIGHT step it once clicked.</summary>
    private static bool Slider()
    {
        GuiContext gui = Context();
        // Handle 8 wide: the pointer's x maps from 104 (the low end) to 304 (the high end).
        GuiRect rect = new(100f, 100f, 208f, 20f);
        int value = 4;
        void Draw() => gui.Slider("s", rect, ref value, 4, 32, "value");

        Frame(gui, Pointer(204f, 110f, down: true), Draw);
        bool ok = Check("value with the pointer half way", value, 18, 18);
        Frame(gui, Pointer(500f, 400f, down: true), Draw);
        ok &= Check("value dragged far past the end", value, 32, 32);
        Frame(gui, Pointer(500f, 400f), Draw);
        Frame(gui, new GuiInput { Cursor = new vec2(500f, 400f), Keys = GuiKeys.Left }, Draw);
        ok &= Check("value after LEFT", value, 31, 31);

        float fraction = 0.5f;
        Frame(gui, Pointer(0f, 0f), () => gui.Slider("f", rect, ref fraction, 0f, 1f, "fraction", step: 0.25f));
        Frame(gui, Pointer(170f, 110f, down: true), () => gui.Slider("f", rect, ref fraction, 0f, 1f, "fraction", step: 0.25f));
        ok &= Check("value snapped to its step", fraction, 0.25, 0.25);
        return ok;
    }

    /// <summary>Clicking a field gives it the keys; typing and the editing keys change it; TAB moves on; a click elsewhere takes the keys away.</summary>
    private static bool TextField()
    {
        GuiContext gui = Context();
        string first = "ab";
        string second = "";
        GuiRect rect = new(100f, 100f, 200f, 20f);
        bool digitsOnly(char c) => char.IsAsciiDigit(c);
        void Draw()
        {
            gui.TextField("first", rect, ref first, maxLength: 6);
            gui.TextField("second", rect.Offset(0f, 40f), ref second, accepts: digitsOnly);
        }

        void Type(string text = "", GuiKeys keys = GuiKeys.None)
        {
            Frame(gui, new GuiInput { Cursor = new vec2(0f, 0f), Text = text, Keys = keys }, Draw);
        }

        Frame(gui, Pointer(150f, 110f), Draw);
        Type("x");
        bool ok = Expect("typing into a field without the keys does nothing", first == "ab");

        // Padding 4: the text starts at 104; "ab" ends at 114, between 'a' (to 108) and 'b'.
        Frame(gui, Pointer(109f, 110f, down: true), Draw);
        Frame(gui, Pointer(109f, 110f), Draw);
        ok &= Expect("the click gave the field the keys", gui.Focused == "first");
        Type("ba");
        ok &= Expect($"typed at the caret, between the letters: \"{first}\"", first == "abab");
        Type(keys: GuiKeys.Backspace);
        Type(keys: GuiKeys.Home);
        Type(keys: GuiKeys.Delete);
        // "abab", the caret before the last 'b': BACKSPACE takes the 'a' before it, DELETE the first.
        ok &= Expect($"BACKSPACE, HOME, DELETE: \"{first}\"", first == "bb");
        Type(keys: GuiKeys.End);
        Type("abababab");
        ok &= Expect($"no longer than its limit: \"{first}\"", first == "bbabab");

        Type(keys: GuiKeys.Tab);
        ok &= Expect("TAB gave the keys to the next field", gui.Focused == "second");
        Type("a1b2");
        ok &= Expect($"only the characters it accepts: \"{second}\"", second == "12");
        Type(keys: GuiKeys.Tab);
        ok &= Expect("TAB from the last field goes round to the first", gui.Focused == "first");

        Frame(gui, Pointer(10f, 10f, down: true), Draw);
        ok &= Expect("a click on nothing took the keys away", gui.Focused == null);
        return ok;
    }

    /// <summary>A list shows the rows in view, scrolls with the wheel and its scrollbar, and tells clicks from double clicks.</summary>
    private static bool List()
    {
        GuiContext gui = Context();
        GuiRect rect = new(100f, 50f, 200f, 100f);
        int selected = -1;
        bool doubled = false;
        GuiList shown = default;
        void Draw()
        {
            shown = gui.BeginList("list", rect, 20, 20f);
            for (int i = shown.First; i < shown.End; i++)
            {
                GuiClick click = gui.ListRow(shown, i, i == selected);
                if (click != GuiClick.None)
                    selected = i;
                doubled |= click == GuiClick.Double;
            }

            gui.EndList();
        }

        Frame(gui, Pointer(150f, 75f), Draw);
        bool ok = Check("first row shown", shown.First, 0, 0) & Check("rows shown", shown.End - shown.First, 5, 5);
        ok &= Check("row width beside the scrollbar", shown.Area.Width, 194, 194);

        Frame(gui, new GuiInput { Cursor = new vec2(150f, 75f), Scroll = -2f }, Draw);
        Frame(gui, Pointer(150f, 75f), Draw);
        ok &= Check("scrolled by two notches of half a row", shown.Scroll, 20, 20);
        Frame(gui, new GuiInput { Cursor = new vec2(150f, 75f), Scroll = -100f }, Draw);
        Frame(gui, Pointer(150f, 75f), Draw);
        ok &= Check("scrolled no further than the end", shown.Scroll, 300, 300);

        // Rows 15 to 19 show; the pointer at y = 75 is over row 16.
        Frame(gui, Pointer(150f, 75f, down: true, time: 1.0), Draw);
        Frame(gui, Pointer(150f, 75f, time: 1.1), Draw);
        ok &= Check("the row clicked is selected", selected, 16, 16);
        ok &= Expect("one click is not a double one", !doubled);
        Frame(gui, Pointer(150f, 75f, down: true, time: 1.2), Draw);
        ok &= Expect("a second click soon after is", doubled);

        // The thumb, 25 high, is at the bottom; dragging it to the top scrolls to the start.
        Frame(gui, Pointer(150f, 75f, time: 1.3), Draw);
        Frame(gui, Pointer(297f, 140f, down: true), Draw);
        Frame(gui, Pointer(297f, -500f, down: true), Draw);
        Frame(gui, Pointer(297f, -500f), Draw);
        ok &= Check("scroll after dragging the thumb to the top", shown.Scroll, 0, 0);
        return ok;
    }

    private static bool Wrap()
    {
        GuiContext gui = Context();
        // "ab" is 10 wide, a space 4: "ab ab" is 24.
        List<string> lines = gui.Wrap("ab ab ab  b", 24f);
        bool ok = Check("lines", lines.Count, 2, 2);
        ok &= Expect($"first line \"{lines[0]}\"", lines[0] == "ab ab");
        ok &= Expect($"second line \"{lines[1]}\"", lines[1] == "ab b");
        ok &= Check("a word wider than a line has one to itself", gui.Wrap("bbbbbb a", 10f).Count, 2, 2);
        return ok;
    }
}
