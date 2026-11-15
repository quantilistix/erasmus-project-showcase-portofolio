using System.Text;

using GlmSharp;

using MYT.Kit.Gui;
using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

using Silk.NET.GLFW;

namespace VoxelGame;


/// <summary>
/// Event-driven keyboard and polled mouse state, with presses detected as edges, and what
/// the interface reads: the text typed and the editing keys pressed or repeated.
/// </summary>
internal sealed unsafe class Input
{
    public Input(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw;
        _window = window;
        _physicalKeyboard = PhysicalKeyboard.TryCreate(glfw, PhysicalKeys);
        // GLFW holds only native pointers to these; the fields keep them alive.
        _onScroll = (_, _, y) => _scroll += y;
        _onChar = (_, codepoint) => _typing.Append(char.ConvertFromUtf32((int)codepoint));
        _onKey = OnKey;
        glfw.SetScrollCallback(window, _onScroll);
        glfw.SetCharCallback(window, _onChar);
        glfw.SetKeyCallback(window, _onKey);
        _onFocus = (_, focused) => { if (!focused) _keyboard.Clear(); };
        glfw.SetWindowFocusCallback(window, _onFocus);
    }

    private static GuiKeys GuiKeyOf(Keys key)
    {
        return key switch
        {
            Keys.Backspace => GuiKeys.Backspace,
            Keys.Delete => GuiKeys.Delete,
            Keys.Left => GuiKeys.Left,
            Keys.Right => GuiKeys.Right,
            Keys.Up => GuiKeys.Up,
            Keys.Down => GuiKeys.Down,
            Keys.Home => GuiKeys.Home,
            Keys.End => GuiKeys.End,
            Keys.Enter or Keys.KeypadEnter => GuiKeys.Enter,
            Keys.Escape => GuiKeys.Escape,
            Keys.Tab => GuiKeys.Tab,
            _ => GuiKeys.None
        };
    }

    private static readonly Keys[] PhysicalKeys = Enum.GetValues<Keys>().Where(k => (int)k >= 32 && (int)k <= 348).Distinct().ToArray();
    private readonly PhysicalKeyboard? _physicalKeyboard;
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly GlfwCallbacks.ScrollCallback _onScroll;
    private readonly GlfwCallbacks.CharCallback _onChar;
    private readonly GlfwCallbacks.KeyCallback _onKey;
    private readonly GlfwCallbacks.WindowFocusCallback _onFocus;
    // Typed and pressed since the last update.
    private readonly StringBuilder _typing = new();
    private GuiKeys _guiKeys;
    private readonly KeyboardState _keyboard = new();
    private readonly bool[] _buttons = new bool[3];
    private readonly bool[] _buttonPressed = new bool[3];
    private double _lastX;
    private double _lastY;
    private bool _hasCursor;
    private double _scroll;

    public IEnumerable<Keys> PressedKeys => _keyboard.PressedKeys;
    public bool Down(int binding) => binding < 0 ? _buttons[-binding - 1] : Down((Keys)binding);
    public bool Pressed(int binding) => binding < 0 ? _buttonPressed[-binding - 1] : Pressed((Keys)binding);

    public bool Captured { get; private set; }
    public dvec2 MouseDelta { get; private set; }
    public double Scroll { get; private set; }

    /// <summary>The characters typed since the last update, pastes included.</summary>
    public string Typed { get; private set; } = "";

    /// <summary>The interface's keys pressed, or repeated by being held, since the last update.</summary>
    public GuiKeys GuiKeys { get; private set; }

    /// <summary>Where the cursor is, in framebuffer pixels from the top-left corner, as the interface is laid out.</summary>
    public dvec2 Cursor { get; private set; }

    public bool Down(Keys key) => _keyboard.Down(key);
    public bool Pressed(Keys key) => _keyboard.Pressed(key);
    public bool ButtonDown(MouseButton button) => _buttons[(int)button];
    public bool ButtonPressed(MouseButton button) => _buttonPressed[(int)button];

    public void Capture(bool capture)
    {
        Captured = capture;
        _glfw.SetInputMode(_window, CursorStateAttribute.Cursor, capture ? CursorModeValue.CursorDisabled : CursorModeValue.CursorNormal);
        if (capture && _glfw.RawMouseMotionSupported())
            _glfw.SetInputMode(_window, CursorStateAttribute.RawMouseMotion, true);
        _hasCursor = false;
    }

    /// <summary>Reads the devices; call once per frame after polling events.</summary>
    public void Update()
    {
        if (_glfw.GetWindowAttrib(_window, WindowAttributeGetter.Focused))
        {
            _physicalKeyboard?.Update();
            foreach (Keys key in PhysicalKeys)
                _keyboard.Reconcile(key, _physicalKeyboard?.Down(key)
                    ?? _glfw.GetKey(_window, key) != (int)InputAction.Release);
        }
        else _keyboard.Clear();
        _keyboard.Update();

        for (int b = 0; b < 3; b++)
        {
            bool down = _glfw.GetMouseButton(_window, b) == (int)InputAction.Press;
            _buttonPressed[b] = down && !_buttons[b];
            _buttons[b] = down;
        }

        _glfw.GetCursorPos(_window, out double x, out double y);
        MouseDelta = _hasCursor && Captured ? new dvec2(x - _lastX, y - _lastY) : dvec2.Zero;
        _glfw.GetWindowSize(_window, out int windowWidth, out int windowHeight);
        _glfw.GetFramebufferSize(_window, out int pixelWidth, out int pixelHeight);
        Cursor = new dvec2(x * pixelWidth / Math.Max(windowWidth, 1), y * pixelHeight / Math.Max(windowHeight, 1));
        _lastX = x;
        _lastY = y;
        _hasCursor = true;

        Scroll = _scroll;
        _scroll = 0;
        Typed = _typing.ToString();
        _typing.Clear();
        GuiKeys = _guiKeys;
        _guiKeys = GuiKeys.None;
    }

    /// <summary>The input as the interface reads it, the pointer free; nothing while the mouse is captured.</summary>
    public GuiInput Gui(double time)
    {
        if (Captured)
            return new GuiInput { Cursor = new vec2(-1f), Time = time };

        return new GuiInput
        {
            Cursor = (vec2)Cursor,
            MouseDown = ButtonDown(MouseButton.Left),
            SecondaryDown = ButtonDown(MouseButton.Right),
            Shift = Down(Keys.ShiftLeft) || Down(Keys.ShiftRight),
            Scroll = (float)Scroll,
            Text = Typed,
            Keys = GuiKeys,
            Time = time
        };
    }

    private void OnKey(WindowHandle* window, Keys key, int scanCode, InputAction action, KeyModifiers modifiers)
    {
        _keyboard.Handle(key, action, physicalPolling: _physicalKeyboard != null);
        if (action == InputAction.Release)
            return;

        _guiKeys |= GuiKeyOf(key);
        if (key == Keys.V && (modifiers & KeyModifiers.Control) != 0 && action == InputAction.Press)
        {
            string? pasted = _glfw.GetClipboardString(_window);
            if (pasted != null)
                _typing.Append(pasted.ReplaceLineEndings(" "));
        }
    }
}


/// <summary>What the player asks of its body, set every frame.</summary>
/// <param name="Direction">
/// The velocity asked for, world axes, in units of the walking speed (flying: of the flying
/// speed): horizontal when walking, and with up and down when flying. At most unit length,
/// but for sprinting, which stretches the part of it straight ahead by
/// <see cref="Player.SprintFactor"/>.
/// </param>
/// <param name="Sprint">Whether the player sprints, which it does only forward.</param>
internal readonly record struct PlayerControls(dvec3 Direction, bool Sprint, bool Crouch = false);


/// <summary>
/// The player's body: an upright 0.6 × 0.6 × 1.8 m box of 80 kg,
/// that never turns, simulated as a rigid body among the objects. Impacts and loads see it as
/// three stacked 0.6 m cells of <see cref="Material"/> that share one health, which wears as a
/// block's does. The view turns freely; the box does not. How it walks, jumps and flies is up to <see cref="World"/>, which
/// knows what it stands on.
/// </summary>
internal sealed class Player : CellBody
{
    public const double Width = 0.6;
    public const double Height = 1.8;
    public const double EyeHeight = 1.62;
    public const double CrouchHeight = Height * 0.75;
    public const double CrouchSpeedFactor = 0.5;
    public const double Mass = 80.0;
    public const double MiningHitsPerSecond = 6.0;
    /// <summary>Average hand thrust (N): each hit gives the target and player opposite Force / HitsPerSecond impulses.</summary>
    public const double MiningForce = 120.0;
    public const double WalkSpeed = 4.5;
    public const double FlySpeed = 10.0;
    public const double SprintFactor = 1.5;
    /// <summary>
    /// How hard the legs push the player the way it asks on ground of friction 1, m/s²; on other
    /// ground, in proportion to its friction. The ground's friction also drags on the player's
    /// speed over it, in proportion to that speed and to the friction, by as much at the speed
    /// asked for (<see cref="WalkSpeed"/>, stretched when sprinting), which is where the two
    /// balance and so the most the player walks at. The speed relaxes toward it with a time
    /// constant of WalkSpeed / (WalkAcceleration · friction): 0.04 s on stone, 0.09 s on leaves.
    /// </summary>
    public const double WalkAcceleration = 160.0;
    /// <summary>The most steering in the air can change the speed, m/s²: a little, as it pushes on nothing.</summary>
    public const double AirAcceleration = 3.0;
    /// <summary>
    /// The air's drag on the player in the air, per speed squared, 1/m: ½·ρ·C_d·A / m for a
    /// person of <see cref="Mass"/> (air at 1.2 kg/m³, C_d·A about 0.7 m²). It slows a run
    /// by a fraction of a percent per second, and caps a long fall at about 75 m/s.
    /// </summary>
    public const double AirDrag = 0.0053;
    /// <summary>The most flight can change the speed, m/s², on top of holding the player up.</summary>
    public const double FlyAcceleration = 30.0;
    /// <summary>
    /// How many times the world's gravity pulls the player: falling and landing as briskly as a
    /// game wants, while blocks and loads keep the real one.
    /// </summary>
    public const double GravityScale = 3.0;
    /// <summary>Upward takeoff speed relative to the ground, m/s: 1.67 m high under <see cref="GravityScale"/>, clearing a block.</summary>
    public const double JumpSpeed = 9.9;
    /// <summary>How long (s) a jump pressed in the air waits for the feet to touch down.</summary>
    public const double JumpBuffer = 0.1;
    /// <summary>
    /// The highest ledge (m) the player steps up onto by walking into it, like a stair or a
    /// slab; a whole block takes a jump.
    /// </summary>
    public const double StepHeight = 0.6;
    /// <summary>How long (s) the view takes to catch up with a step up, smoothing it out.</summary>
    public const double StepSmoothing = 0.08;
    /// <summary>
    /// The force (N) the player's hands push what it walks into with: enough to slide a loose
    /// block of stone (18 kN on stone), not a 3×3×3 cube of it.
    /// </summary>
    public const double PushForce = 30e3;
    /// <summary>
    /// A contact supports the player when it pushes it up at least this steeply (the cosine of
    /// 45°) within <see cref="SupportReach"/> of its bottom face; one less steep either way is
    /// something in its way, such as a wall, a step or an object, even a tilted one.
    /// </summary>
    public const double SupportSlope = 0.7;
    public const double SupportReach = 0.1;
    private const double LookSensitivity = 0.0025;

    /// <summary>Its cells, feet first, each <see cref="Width"/> on a side.</summary>
    public static readonly ivec3[] Cells = [new(0, 0, 0), new(0, 0, 1), new(0, 0, 2)];

    /// <summary>
    /// What the player is made of, as a block. A body is soft and weak next to stone, so it is
    /// the side of an impact that gives: unhurt it soaks up about 11 kJ (a fall of 4.5 m under
    /// its threefold gravity, or a full-tilt run into a wall many times over), pressing with up
    /// to 70 kN. Its damage capacity is doubled to make repeated impacts and falls more survivable.
    /// As with a block, the first damage costs little health and the last a lot, and a hurt
    /// player endures less. Its body has no friction, as its feet grip by pushing
    /// (<see cref="World"/> drives them), not by sliding friction, which would fight every
    /// change of pace; and it never bounces.
    /// </summary>
    public static readonly BlockMaterial Material = new
    (
        "Player",
        Mass / (Width * Width * Height),
        new ElasticLimits(150e3, 200e3, 100e3),
        Stiffness: 1.0,
        Durability: 36.0,
        YoungsModulus: 0.43e6,
        PoissonRatio: 0.45,
        MiningSeconds: double.PositiveInfinity,
        Friction: 0.0,
        Damping: 1.0,
        Top: "",
        East: "",
        West: "",
        North: "",
        South: "",
        Bottom: ""
    );

    /// <summary>The body frame's origin, the box's lowest corner, for the middle of the feet.</summary>
    private static dvec3 CornerOf(dvec3 feet)
    {
        return feet - new dvec3(Width / 2, Width / 2, 0);
    }

    /// <summary>A square sole supports the player right up to its edge; wound outward.</summary>
    private static Mesh3D BoxMesh(double height = Height)
    {
        dvec2[] square = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        dvec3[] vertices = new dvec3[8];
        for (int k = 0; k < 4; k++)
        {
            vertices[k] = new dvec3(square[k] * Width, 0);
            vertices[k + 4] = new dvec3(square[k] * Width, height);
        }

        List<ulong> faces = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7];
        for (ulong k = 0; k < 4; k++)
        {
            ulong next = (k + 1) % 4;
            faces.AddRange([k, next, next + 4, k, next + 4, k + 4]);
        }

        return new Mesh3D(vertices, [.. faces]);
    }

    /// <param name="feet">Where it stands, the middle of its bottom face; also where it respawns.</param>
    public Player(int id, dvec3 feet) : base(id)
    {
        Spawn = feet;
        Body = new RigidBody(BoxMesh(), Mass)
        {
            Position = CornerOf(feet),
            FixedRotation = true,
            GravityScale = Player.GravityScale,
            Friction = Material.Surface.Friction,
            Restitution = Material.Surface.Restitution
        };
    }

    public RigidBody Body { get; }
    public dvec3 Spawn { get; }

    /// <summary>What it carries; dropped items it touches go in (<see cref="World"/> picks them up).</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>From 1 (unhurt) to 0 (dead), as a block's.</summary>
    public double Health { get; set; } = 1.0;
    public bool Dead => Health <= 0.0;

    /// <summary>
    /// From 1 (fed, watered, fully guarded) to 0, as the hunger, thirst and defense bars show
    /// them. Placeholders: nothing changes them yet, and saves leave them out.
    /// </summary>
    /// <summary>Consumes one held cookie when it can restore food or health.</summary>
    public bool EatHeld(bool unlimited)
    {
        if (Dead || Inventory.Held.Type != ItemType.Cookie || Inventory.Held.IsEmpty
            || (Food >= 1 && Health >= 1)) return false;
        Food = Math.Min(1, Food + 0.2);
        Health = Math.Min(1, Health + 0.1);
        if (!unlimited) Inventory.Take(Inventory.Selected, 1);
        return true;
    }

    public double Food { get; set; } = 1.0;
    public double Water { get; set; } = 1.0;
    public double Defense { get; set; } = 1.0;

    public bool Invulnerable { get; set; }
    public bool Flying { get; set; } = true;

    /// <summary>Whether its strikes break any block at once, as creative mode lets it choose.</summary>
    public bool InstantBreak { get; set; }
    public PlayerControls Controls { get; set; }

    /// <summary>Set when jump is pressed; the world jumps once, as soon as the feet are on the ground.</summary>
    public bool JumpPressed { get; set; }

    /// <summary>How far (m) the view still lags below the eyes after stepping up, closing over <see cref="StepSmoothing"/>.</summary>
    public double ViewLag { get; set; }

    /// <summary>Whether its bottom face pushed on something in the last step.</summary>
    public bool Grounded { get; set; }
    public bool Crouching { get; private set; }
    public double CurrentHeight => Crouching ? CrouchHeight : Height;

    /// <summary>Grounded changes keep the feet fixed; airborne changes keep the top fixed.</summary>
    public void SetCrouching(bool crouching, bool grounded)
    {
        if (Crouching == crouching)
            return;
        double oldHeight = CurrentHeight;
        Crouching = crouching;
        Body.SetGeometry(BoxMesh(CurrentHeight));
        if (!grounded)
            Body.Position += new dvec3(0, 0, oldHeight - CurrentHeight);
    }

    /// <summary>Per cell, the stresses its sustained loads put it under.</summary>
    public CellStress[] Stresses { get; } = new CellStress[Cells.Length];

    public double Yaw { get; set; } = Math.PI / 2;
    public double Pitch { get; set; } = -0.25;

    public override double CellEdge => Width;
    public override dvec3 Position => Body.Position;
    public override dquat Orientation => Body.Orientation;
    public override RigidBody? Rigid => Body;

    /// <summary>Middle of the feet.</summary>
    public dvec3 Feet => Body.Position + new dvec3(Width / 2, Width / 2, 0);
    public dvec3 Eye => Feet + new dvec3(0, 0, EyeHeight - (Height - CurrentHeight) - ViewLag);
    public dvec3 Forward => new(Math.Cos(Pitch) * Math.Cos(Yaw), Math.Cos(Pitch) * Math.Sin(Yaw), Math.Sin(Pitch));

    public void Look(dvec2 delta)
    {
        Yaw -= delta.x * LookSensitivity;
        Pitch = Math.Clamp(Pitch - delta.y * LookSensitivity, -1.55, 1.55);
    }

    /// <summary>Puts the player somewhere at rest.</summary>
    public void MoveTo(dvec3 feet)
    {
        Body.Position = CornerOf(feet);
        Body.LinearVelocity = dvec3.Zero;
    }

    /// <summary>Whether the player's box overlaps a box of world space.</summary>
    public bool Overlaps(dvec3 low, dvec3 high)
    {
        dvec3 corner = Body.Position;
        return high.x > corner.x && low.x < corner.x + Width
            && high.y > corner.y && low.y < corner.y + Width
            && high.z > corner.z && low.z < corner.z + CurrentHeight;
    }

    public override IEnumerable<ivec3> OccupiedCells()
    {
        return Cells;
    }

    public override BlockMaterial MaterialAt(ivec3 cell)
    {
        return Material;
    }

    public override double HealthAt(ivec3 cell)
    {
        return Health;
    }

    /// <summary>Every contact is on the box, and presses on the cell at its height.</summary>
    public override bool TryFindSurfaceCell(dvec3 localPoint, dvec3 inward, out ivec3 cell)
    {
        cell = new ivec3(0, 0, Math.Clamp((int)Math.Floor(localPoint.z + 0.25 * inward.z), 0, Cells.Length - 1));
        return true;
    }

    public override CellStress StaticStress(ivec3 cell)
    {
        return Stresses[cell.z];
    }

    public override double KineticEnergy(dvec3 linear, dvec3 angular)
    {
        return 0.5 * Mass * linear.LengthSqr;
    }
}
