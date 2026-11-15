using GlmSharp;

namespace MYT.Kit.Gui;


/// <summary>The keys the interface reads: for editing text, moving between elements and stepping sliders.</summary>
[Flags]
public enum GuiKeys
{
    None = 0,
    Backspace = 1 << 0,
    Delete = 1 << 1,
    Left = 1 << 2,
    Right = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Home = 1 << 6,
    End = 1 << 7,
    Enter = 1 << 8,
    Escape = 1 << 9,
    Tab = 1 << 10
}


/// <summary>What the player did since the last frame, as the game hands it to <see cref="GuiContext.Begin"/>.</summary>
public readonly record struct GuiInput
{
    /// <summary>Where the pointer is, in pixels from the top-left corner of the screen.</summary>
    public vec2 Cursor { get; init; }

    /// <summary>Whether the primary mouse button is held; presses and releases are told from it.</summary>
    public bool MouseDown { get; init; }

    /// <summary>Whether the secondary (right) mouse button is held, for elements that tell the two apart.</summary>
    public bool SecondaryDown { get; init; }

    /// <summary>Whether SHIFT is held, which changes what some clicks do.</summary>
    public bool Shift { get; init; }

    /// <summary>Wheel notches turned, positive away from the player: scrolling up.</summary>
    public float Scroll { get; init; }

    /// <summary>The characters typed, in order; null for none.</summary>
    public string? Text { get; init; }

    /// <summary>The keys pressed, or repeated by being held.</summary>
    public GuiKeys Keys { get; init; }

    /// <summary>Seconds on any steady clock, for blinking the caret and telling double clicks.</summary>
    public double Time { get; init; }
}
