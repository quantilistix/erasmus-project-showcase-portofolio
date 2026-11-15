using GlmSharp;

namespace MYT.Kit.Gui;


public enum GuiAlign
{
    Left,
    Center,
    Right
}


/// <summary>How a list row was clicked this frame.</summary>
public enum GuiClick
{
    None,
    Single,
    Double
}


/// <summary>
/// A list being drawn, from <see cref="GuiContext.BeginList"/> to <see cref="GuiContext.EndList"/>:
/// where its rows go and which of them show.
/// </summary>
/// <param name="Area">Where the rows go, beside the scrollbar.</param>
/// <param name="Scroll">How far (units) the rows are scrolled up.</param>
/// <param name="First">The first row that shows.</param>
/// <param name="End">One past the last row that shows.</param>
public readonly record struct GuiList(string Id, GuiRect Area, float RowHeight, float Scroll, int First, int End)
{
    public GuiRect Row(int index)
    {
        return new GuiRect(Area.X, Area.Y + index * RowHeight - Scroll, Area.Width, RowHeight);
    }
}


/// <summary>
/// An immediate-mode interface. Every frame, between <see cref="Begin"/> and <see cref="End"/>,
/// the game calls a method for each element it shows, which draws it into
/// <see cref="DrawList"/> and returns what the player did to it: a button returns whether it
/// was clicked, a slider or a text field changes the value it is handed. The game keeps its
/// own state and lays the screen out anew each frame; all this keeps is what elements need
/// while in use: which one the pointer drags, which one takes the keys, where the caret is
/// and how far each list is scrolled. Elements that need that have a string id, the same
/// from frame to frame and unique on the screen.
/// <para>
/// Everything is laid out in units, <see cref="Scale"/> pixels each: at a whole scale, a
/// pixel-art font and images drawn at their own size, or a whole multiple of it, stay sharp.
/// </para>
/// </summary>
public sealed class GuiContext
{
    // Two presses on a list row this close (s) are a double click.
    private const double DoubleClickSeconds = 0.35;
    // The caret shows this long (s), then hides as long.
    private const double CaretBlinkSeconds = 0.5;

    /// <summary>
    /// The largest whole scale, at most <paramref name="largest"/>, at which a screen of
    /// <paramref name="pixels"/> is at least <paramref name="minimum"/> units each way; 1 at least.
    /// </summary>
    public static int FitScale(vec2 pixels, vec2 minimum, int largest = int.MaxValue)
    {
        int scale = (int)MathF.Min(pixels.x / minimum.x, pixels.y / minimum.y);
        return Math.Clamp(scale, 1, Math.Max(largest, 1));
    }

    private static float Wrap(float value, float period)
    {
        return value - MathF.Floor(value / period) * period;
    }

    /// <param name="atlas">The atlas the style's font and every image drawn are in.</param>
    public GuiContext(GuiAtlas atlas, GuiStyle style)
    {
        _white = (atlas.White.UvMin + atlas.White.UvMax) / 2f;
        Style = style;
    }

    private readonly vec2 _white;
    // The clips in units, for telling what the pointer can reach; the draw list has them in pixels.
    private readonly Stack<GuiRect> _clips = new();
    private readonly Dictionary<string, float> _scrolls = [];
    private GuiInput _input;
    private bool _wasDown;
    private bool _secondaryWasDown;
    // The element the pointer drags, from the press that grabbed it until the button is let
    // go, and where on a scrollbar's thumb it holds it (units from its top).
    private string? _active;
    private float _grab;
    // The element taking the keys, and the one this frame's press went to if it takes keys.
    private string? _focused;
    private string? _pressed;
    // The focused text field's caret (characters into its text), how far its text is scrolled
    // left (units), and when the caret last moved, which restarts its blinking.
    private int _caret;
    private float _fieldScroll;
    private double _caretMoved;
    // TAB moves the keys on to the next text field drawn, or round to the first.
    private bool _tabbing;
    private bool _tabTaken;
    private string? _firstField;
    // The last press on a list row, for telling double clicks.
    private string? _clickedList;
    private int _clickedRow;
    private double _clickTime;

    public GuiStyle Style { get; set; }

    public GuiDrawList DrawList { get; } = new();

    /// <summary>Pixels per unit this frame.</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>The whole screen this frame, units.</summary>
    public GuiRect Screen { get; private set; }

    /// <summary>Where the pointer is, units.</summary>
    public vec2 Cursor { get; private set; }

    public GuiInput Input => _input;

    /// <summary>Whether the primary button went down this frame.</summary>
    public bool MousePressed { get; private set; }

    /// <summary>Whether the secondary button went down this frame.</summary>
    public bool SecondaryPressed { get; private set; }

    /// <summary>The element taking the keys, if any.</summary>
    public string? Focused => _focused;

    /// <summary>Whether a key was pressed, or repeated by being held, this frame.</summary>
    public bool KeyPressed(GuiKeys key)
    {
        return (_input.Keys & key) != 0;
    }

    // ---------------------------------------------------------------------------------------
    // Frames and focus
    // ---------------------------------------------------------------------------------------

    /// <summary>Starts a frame: empties the draw list and reads the input. A unit is <paramref name="scale"/> pixels.</summary>
    /// <param name="pixels">The size of the screen, pixels.</param>
    public void Begin(GuiInput input, vec2 pixels, float scale)
    {
        _input = input;
        MousePressed = input.MouseDown && !_wasDown;
        _wasDown = input.MouseDown;
        SecondaryPressed = input.SecondaryDown && !_secondaryWasDown;
        _secondaryWasDown = input.SecondaryDown;
        if (!input.MouseDown)
            _active = null;

        Scale = scale;
        Screen = new GuiRect(0f, 0f, pixels.x / scale, pixels.y / scale);
        Cursor = input.Cursor / scale;
        DrawList.Clear();
        _clips.Clear();
        _pressed = null;
        _tabTaken = false;
        _firstField = null;
    }

    /// <summary>Ends a frame. A press on anything but an element that takes keys takes the keys away from the one that had them.</summary>
    public void End()
    {
        if (_clips.Count != 0)
            throw new InvalidOperationException("A clip pushed this frame was not popped");

        if (_tabbing)
        {
            _tabbing = false;
            if (_firstField != null)
                Focus(_firstField);
        }

        if (MousePressed && _pressed != _focused)
            _focused = _pressed;
    }

    /// <summary>Gives the keys to an element, as to a screen's first text field when it opens; the caret goes to the end.</summary>
    public void Focus(string id)
    {
        _focused = id;
        _caret = int.MaxValue;
        _fieldScroll = 0f;
        _caretMoved = _input.Time;
    }

    public void ClearFocus()
    {
        _focused = null;
    }

    // ---------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------

    /// <summary>Limits what is drawn, and what the pointer reaches, to a rectangle within the current limit, until <see cref="PopClip"/>.</summary>
    public void PushClip(GuiRect rect)
    {
        _clips.Push(_clips.Count > 0 ? rect.Intersect(_clips.Peek()) : rect);
        DrawList.PushClip(rect.Scaled(Scale));
    }

    public void PopClip()
    {
        _clips.Pop();
        DrawList.PopClip();
    }

    public void Rect(GuiRect rect, GuiColor color)
    {
        DrawList.Quad(rect.Scaled(Scale), _white, _white, color);
    }

    /// <summary>A rectangle shading from one color at its top to another at its bottom.</summary>
    public void Gradient(GuiRect rect, GuiColor top, GuiColor bottom)
    {
        DrawList.Quad(rect.Scaled(Scale), _white, _white, top, bottom);
    }

    /// <summary>The edges of a rectangle, <paramref name="thickness"/> units wide, inside it.</summary>
    public void Outline(GuiRect rect, GuiColor color, float thickness = 1f)
    {
        float sides = rect.Height - 2f * thickness;
        Rect(rect with { Height = thickness }, color);
        Rect(rect with { Y = rect.Bottom - thickness, Height = thickness }, color);
        Rect(new GuiRect(rect.X, rect.Y + thickness, thickness, sides), color);
        Rect(new GuiRect(rect.Right - thickness, rect.Y + thickness, thickness, sides), color);
    }

    public void Image(GuiRect rect, GuiImage image)
    {
        Image(rect, image, GuiColor.White);
    }

    public void Image(GuiRect rect, GuiImage image, GuiColor tint)
    {
        DrawList.Quad(rect.Scaled(Scale), image.UvMin, image.UvMax, tint);
    }

    /// <summary>
    /// Covers a rectangle with copies of an image, each <paramref name="tile"/> units, laid from
    /// its top-left corner moved by <paramref name="offset"/>, and cut at its edges.
    /// </summary>
    public void Tile(GuiRect rect, GuiImage image, vec2 tile, GuiColor tint, vec2 offset = default)
    {
        PushClip(rect);
        float left = rect.X - Wrap(-offset.x, tile.x);
        float top = rect.Y - Wrap(-offset.y, tile.y);
        for (int row = 0; top + row * tile.y < rect.Bottom; row++)
        for (int column = 0; left + column * tile.x < rect.Right; column++)
            Image(new GuiRect(left + column * tile.x, top + row * tile.y, tile.x, tile.y), image, tint);

        PopClip();
    }

    /// <summary>A beveled frame, as a button is drawn: border, light top and left edges, dark bottom and right ones, and face.</summary>
    public void Bevel(GuiRect rect, GuiButtonColors colors)
    {
        GuiRect inner = rect.Inset(1f);
        Rect(rect, colors.Border);
        Rect(inner, colors.Shade);
        Rect(inner with { Width = inner.Width - 1f, Height = inner.Height - 1f }, colors.Highlight);
        Rect(inner.Inset(1f), colors.Face);
    }

    /// <summary>Writes a line of text from its top-left corner, <paramref name="size"/> units high (the style's by default). Returns its width.</summary>
    /// <param name="shadow">Whether to draw it over a darker copy of itself, a font pixel down and right.</param>
    public float Text(vec2 position, string text, GuiColor color, float? size = null, bool shadow = true)
    {
        float pixel = (size ?? Style.TextSize) / Style.Font.Height;
        if (shadow)
            Glyphs(position + new vec2(pixel), text, color.Scaled(Style.ShadowBrightness), pixel);

        return Glyphs(position, text, color, pixel);
    }

    /// <summary>Writes a line of text in a rectangle, centered up and down and aligned across.</summary>
    public void Label(GuiRect rect, string text, GuiColor color, GuiAlign align = GuiAlign.Center, float? size = null, bool shadow = true)
    {
        float height = size ?? Style.TextSize;
        float width = MeasureText(text, height);
        float x = align switch
        {
            GuiAlign.Left => rect.X,
            GuiAlign.Right => rect.Right - width,
            _ => rect.X + (rect.Width - width) / 2f
        };

        Text(new vec2(MathF.Floor(x), MathF.Floor(rect.Y + (rect.Height - height) / 2f)), text, color, height, shadow);
    }

    /// <summary>The width (units) of a line of text, <paramref name="size"/> units high (the style's by default).</summary>
    public float MeasureText(ReadOnlySpan<char> text, float? size = null)
    {
        return Style.Font.Measure(text) * (size ?? Style.TextSize) / Style.Font.Height;
    }

    /// <summary>
    /// A text broken at spaces into lines no wider than <paramref name="width"/> units, at
    /// <paramref name="size"/> (the style's by default); a word wider than a line has one to itself.
    /// </summary>
    public List<string> Wrap(string text, float width, float? size = null)
    {
        List<string> lines = [];
        string line = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string longer = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && MeasureText(longer, size) > width)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = longer;
            }
        }

        if (line.Length > 0)
            lines.Add(line);

        return lines;
    }

    private float Glyphs(vec2 position, string text, GuiColor color, float pixel)
    {
        float x = position.x;
        foreach (char character in text)
        {
            GuiGlyph glyph = Style.Font.Glyph(character);
            if (glyph.Image.Width > 0)
            {
                GuiRect rect = new(x, position.y, glyph.Image.Width * pixel, glyph.Image.Height * pixel);
                DrawList.Quad(rect.Scaled(Scale), glyph.Image.UvMin, glyph.Image.UvMax, color);
            }

            x += glyph.Advance * pixel;
        }

        return x - position.x;
    }

    // ---------------------------------------------------------------------------------------
    // Elements
    // ---------------------------------------------------------------------------------------

    /// <summary>Whether the pointer is over a rectangle, within the clip, while it drags nothing.</summary>
    public bool IsHovered(GuiRect rect)
    {
        return _active == null && Reaches(rect);
    }

    private bool Reaches(GuiRect rect)
    {
        return rect.Contains(Cursor) && (_clips.Count == 0 || _clips.Peek().Contains(Cursor));
    }

    /// <summary>
    /// Whether the pointer drags an element of the game's own, such as a scrollbar's thumb: from
    /// a press of the primary button on <paramref name="rect"/> until the button is let go,
    /// wherever the pointer goes meanwhile.
    /// </summary>
    public bool Drag(string id, GuiRect rect)
    {
        if (IsHovered(rect) && MousePressed)
            _active = id;

        return _active == id;
    }

    /// <summary>A button. Returns whether it was clicked, which it is on the press, as in Minecraft; a disabled one never is.</summary>
    public bool Button(GuiRect rect, string label, bool enabled = true)
    {
        bool hovered = enabled && IsHovered(rect);
        Bevel(rect, !enabled ? Style.ButtonDisabled : hovered ? Style.ButtonHovered : Style.Button);
        PushClip(rect.Inset(2f));
        Label(rect, label, !enabled ? Style.TextDisabled : hovered ? Style.TextHovered : Style.Text);
        PopClip();
        return hovered && MousePressed;
    }

    /// <summary>
    /// A slider over a dark track, its label written across it, set by dragging anywhere on
    /// it; once clicked, LEFT and RIGHT step it. Returns whether the value changed.
    /// </summary>
    /// <param name="step">What the value is a whole multiple of, from <paramref name="min"/>; 0 for any value.</param>
    public bool Slider(string id, GuiRect rect, ref float value, float min, float max, string label, float step = 0f, bool enabled = true)
    {
        float before = value;
        bool hovered = enabled && (_active == id || IsHovered(rect));
        if (hovered && MousePressed)
        {
            _active = id;
            _pressed = id;
            _focused = id;
        }

        float handle = Style.SliderHandleWidth;
        if (_active == id)
            value = min + Math.Clamp((Cursor.x - rect.X - handle / 2f) / (rect.Width - handle), 0f, 1f) * (max - min);

        if (enabled && _focused == id)
        {
            float nudge = step > 0f ? step : (max - min) / 100f;
            if (KeyPressed(GuiKeys.Left))
                value -= nudge;
            if (KeyPressed(GuiKeys.Right))
                value += nudge;
        }

        if (step > 0f)
            value = min + MathF.Round((value - min) / step) * step;
        value = Math.Clamp(value, min, max);

        float t = max > min ? (value - min) / (max - min) : 0f;
        Bevel(rect, Style.ButtonDisabled);
        Bevel(new GuiRect(rect.X + t * (rect.Width - handle), rect.Y, handle, rect.Height), !enabled ? Style.ButtonDisabled : hovered ? Style.ButtonHovered : Style.Button);
        PushClip(rect.Inset(2f));
        Label(rect, label, !enabled ? Style.TextDisabled : hovered ? Style.TextHovered : Style.Text);
        PopClip();
        return value != before;
    }

    /// <summary>A slider over whole numbers; see <see cref="Slider(string, GuiRect, ref float, float, float, string, float, bool)"/>.</summary>
    public bool Slider(string id, GuiRect rect, ref int value, int min, int max, string label, bool enabled = true)
    {
        float set = value;
        Slider(id, rect, ref set, min, max, label, 1f, enabled);
        int before = value;
        value = (int)MathF.Round(set);
        return value != before;
    }

    /// <summary>
    /// A line of editable text: clicking it gives it the keys, and then typing, BACKSPACE,
    /// DELETE, LEFT, RIGHT, HOME and END edit it, and TAB moves on to the next field. Returns
    /// whether the text changed.
    /// </summary>
    /// <param name="placeholder">Shown, dimmed, while the text is empty and the field does not have the keys.</param>
    /// <param name="accepts">Which characters may be typed; any printable one by default.</param>
    public bool TextField(string id, GuiRect rect, ref string text, int maxLength = 32, string placeholder = "", Func<char, bool>? accepts = null)
    {
        string before = text;
        float size = Style.TextSize;
        GuiRect inner = new(rect.X + Style.FieldPadding, rect.Y, rect.Width - 2f * Style.FieldPadding, rect.Height);
        _firstField ??= id;
        if (_tabbing)
        {
            _tabbing = false;
            Focus(id);
        }

        if (IsHovered(rect) && MousePressed)
        {
            _pressed = id;
            if (_focused != id)
            {
                _focused = id;
                _fieldScroll = 0f;
            }

            _caret = CaretAt(text, Cursor.x - inner.X + _fieldScroll);
            _caretMoved = _input.Time;
        }

        bool focused = _focused == id;
        if (focused)
        {
            _caret = Math.Clamp(_caret, 0, text.Length);
            int caretBefore = _caret;
            foreach (char typed in _input.Text ?? "")
            {
                if (char.IsControl(typed) || text.Length >= maxLength || accepts?.Invoke(typed) == false)
                    continue;

                text = text.Insert(_caret, typed.ToString());
                _caret++;
            }

            if (KeyPressed(GuiKeys.Backspace) && _caret > 0)
            {
                text = text.Remove(_caret - 1, 1);
                _caret--;
            }

            if (KeyPressed(GuiKeys.Delete) && _caret < text.Length)
                text = text.Remove(_caret, 1);
            if (KeyPressed(GuiKeys.Left))
                _caret = Math.Max(_caret - 1, 0);
            if (KeyPressed(GuiKeys.Right))
                _caret = Math.Min(_caret + 1, text.Length);
            if (KeyPressed(GuiKeys.Home))
                _caret = 0;
            if (KeyPressed(GuiKeys.End))
                _caret = text.Length;
            if (_caret != caretBefore || text != before)
                _caretMoved = _input.Time;

            if (KeyPressed(GuiKeys.Tab) && !_tabTaken)
            {
                _tabTaken = true;
                _tabbing = true;
                _focused = null;
            }
        }

        // Scrolled just enough that the caret shows.
        float caretX = focused ? MeasureText(text.AsSpan(0, _caret)) : 0f;
        if (focused)
        {
            _fieldScroll = Math.Clamp(_fieldScroll, caretX + 1f - inner.Width, caretX);
            _fieldScroll = Math.Clamp(_fieldScroll, 0f, Math.Max(MeasureText(text) + 1f - inner.Width, 0f));
        }

        float scroll = focused ? _fieldScroll : 0f;
        float y = MathF.Floor(rect.Y + (rect.Height - size) / 2f);
        Rect(rect, focused ? Style.FieldBorderFocused : Style.FieldBorder);
        Rect(rect.Inset(1f), Style.FieldBackground);
        PushClip(new GuiRect(inner.X, rect.Y + 1f, inner.Width + 1f, rect.Height - 2f));
        if (text.Length == 0 && !focused)
            Text(new vec2(inner.X, y), placeholder, Style.Placeholder);
        else
            Text(new vec2(inner.X - scroll, y), text, Style.Text);
        if (focused && (_input.Time - _caretMoved) % (2.0 * CaretBlinkSeconds) < CaretBlinkSeconds)
            Rect(new GuiRect(inner.X - scroll + caretX, y - 1f, 1f, size + 2f), Style.Caret);
        PopClip();
        return text != before;
    }

    /// <summary>The caret position nearest <paramref name="x"/> units into a text.</summary>
    private int CaretAt(string text, float x)
    {
        float pen = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            float advance = MeasureText(text.AsSpan(i, 1));
            if (x < pen + advance / 2f)
                return i;

            pen += advance;
        }

        return text.Length;
    }

    /// <summary>
    /// Starts a scrolling list of <paramref name="count"/> rows, each <paramref name="rowHeight"/>
    /// units high, over the style's list background. The wheel scrolls it while the pointer is
    /// over it, and a scrollbar shows at its right when its rows do not fit. Draw the rows from
    /// <see cref="GuiList.First"/> up to <see cref="GuiList.End"/>, each at its
    /// <see cref="GuiList.Row"/> and with <see cref="ListRow"/> first; they are cut to the
    /// list. Then call <see cref="EndList"/>.
    /// </summary>
    public GuiList BeginList(string id, GuiRect rect, int count, float rowHeight)
    {
        float content = count * rowHeight;
        float range = Math.Max(content - rect.Height, 0f);
        float scroll = _scrolls.GetValueOrDefault(id);
        GuiRect rows = range > 0f ? rect with { Width = rect.Width - Style.ScrollbarWidth } : rect;
        GuiRect track = new(rows.Right, rect.Y, Style.ScrollbarWidth, rect.Height);
        float thumbHeight = range > 0f ? Math.Clamp(rect.Height * rect.Height / content, 8f, rect.Height) : rect.Height;
        float travel = rect.Height - thumbHeight;

        if (range > 0f && _active == null && Reaches(rect))
            scroll -= _input.Scroll * rowHeight / 2f;

        if (range > 0f && IsHovered(track) && MousePressed)
        {
            float thumbTop = rect.Y + scroll / range * travel;
            _grab = Cursor.y >= thumbTop && Cursor.y < thumbTop + thumbHeight ? Cursor.y - thumbTop : thumbHeight / 2f;
            _active = id;
        }

        if (_active == id && travel > 0f)
            scroll = (Cursor.y - _grab - rect.Y) / travel * range;

        scroll = Math.Clamp(scroll, 0f, range);
        _scrolls[id] = scroll;

        Rect(rect, Style.ListBackground);
        if (range > 0f)
        {
            GuiRect thumb = track with { Y = rect.Y + scroll / range * travel, Height = thumbHeight };
            Rect(track, Style.ScrollTrack);
            Rect(thumb, Style.ScrollThumb);
            Rect(thumb with { Width = thumb.Width - 1f, Height = thumb.Height - 1f }, Style.ScrollThumbHighlight);
        }

        PushClip(rows);
        int first = Math.Min((int)(scroll / rowHeight), count);
        int end = Math.Min((int)MathF.Ceiling((scroll + rect.Height) / rowHeight), count);
        return new GuiList(id, rows, rowHeight, scroll, first, end);
    }

    /// <summary>
    /// Draws a row's background, outlined if it is the selected one and lit under the pointer,
    /// and returns how it was clicked: a second click on it soon after the first is a double one.
    /// </summary>
    public GuiClick ListRow(GuiList list, int index, bool selected)
    {
        GuiRect row = list.Row(index);
        bool hovered = IsHovered(row);
        if (selected)
        {
            Rect(row, Style.ListSelectionBorder);
            Rect(row.Inset(1f), Style.ListSelection);
        }
        else if (hovered)
        {
            Rect(row, Style.ListHover);
        }

        if (!hovered || !MousePressed)
            return GuiClick.None;

        bool twice = _clickedList == list.Id && _clickedRow == index && _input.Time - _clickTime <= DoubleClickSeconds;
        _clickedList = list.Id;
        _clickedRow = index;
        _clickTime = twice ? double.NegativeInfinity : _input.Time;
        return twice ? GuiClick.Double : GuiClick.Single;
    }

    public void EndList()
    {
        PopClip();
    }
}
