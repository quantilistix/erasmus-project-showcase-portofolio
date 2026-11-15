using System.Diagnostics;
using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Gui;
using MYT.Kit.Vulkan;

using Silk.NET.GLFW;
using Silk.NET.Vulkan;

namespace VoxelGame;


/// <summary>Where a world being played is saved: its store and its entry there.</summary>
internal sealed record SaveTarget(WorldStore Store, WorldInfo World);


/// <summary>
/// A world being played: the world and the map streaming in around the player, the player's
/// controls, what it aims at and holds, its inventory, the HUD, and saving the world, when it
/// is quit and every <see cref="AutosaveSeconds"/> in between. The menus over it are
/// <see cref="App"/>'s.
/// <para>
/// How it is played follows its <see cref="GameMode"/>. In survival the player starts with
/// nothing, mines with what it holds (<see cref="Tool"/>), and what it mines drops as an item
/// to pick up; placing a block uses one up. It walks, and it can be hurt. In creative, items
/// never run out, mined blocks drop nothing, and the player can fly (F), turn its
/// invulnerability off and on (I), break any block in one hit or not (B), and drop a cube of
/// the block it holds (G).
/// </para>
/// </summary>
internal sealed class Game : IDisposable
{
    /// <summary>How far (m) the player reaches to mine, to build, and to a workbench to craft at.</summary>
    public const double Reach = 8.0;
    // Mining strikes a block this often while the button is held, each hit starting a swing.
    private const double HitsPerSecond = Player.MiningHitsPerSecond;
    // How much wider the view gets while sprinting, and how long (s) it takes to widen or
    // narrow back about two thirds of the way.
    private const double SprintWidening = 10.0 * Math.PI / 180.0;
    private const double WideningSmoothing = 0.08;
    private const double ThrowSpeed = 22.0;
    // An item the player drops leaves at this speed (m/s) along the view, a little upward, and
    // cannot be picked up again for a while (s).
    private const double DropSpeed = 5.0;
    private const double DropLift = 1.5;
    private const double DroppedPickupDelay = 1.5;
    // A saved world is written this often (s) while it is played, on a background thread.
    private const double AutosaveSeconds = 300.0;
    // The held item takes this long (s) to come up after another is chosen; its name shows
    // this long over the hotbar.
    private const double EquipSeconds = 0.15;
    private const double HeldNameSeconds = 2.0;

    // The status bars: icons each, their size and step along a bar, and the gap between the
    // left bars and the right ones, all interface units.
    private const int BarIcons = 10;
    private const float BarIconSize = 12f;
    private const float BarStep = 10f;
    private const float BarGap = 8f;

    // Where the held item is gripped, in its model: a tool a little up its handle from the
    // image's lower left; a block from below and behind; the arm at the shoulder, its near end.
    private static readonly vec3 ToolGrip = new(4.5f / ItemSprites.Size - 0.5f, 0f, 0.5f - 12.5f / ItemSprites.Size);
    private static readonly vec3 BlockGrip = new(0f, -0.6f, -0.6f);
    private static readonly vec3 Shoulder = new(0f, -0.5f, 0f);
    // Moves whatever is held from where its pose places it, in eye space: right and nearer the eye.
    private static readonly vec3 HeldShift = new(0.1f, -0.18f, 0f);

    private static readonly GuiColor Dim = GuiColor.FromRgba(0xEDF3FFE6);
    private static readonly GuiColor OverloadColor = GuiColor.FromRgb(0xFFAA95);

    private static string Describe(CellBody body)
    {
        return body switch
        {
            VoxelMap => "the map",
            VoxelGame.Player => "the player",
            _ => $"object {body.Id}"
        };
    }

    /// <summary>
    /// A status bar showing a fraction from 0 to 1 in half icons, rounded up, so it shows
    /// empty only at 0: whole icons, a half one, then empty ones. Its first icon is at
    /// <paramref name="first"/> and the rest follow <paramref name="step"/> apart, leftwards if negative.
    /// </summary>
    private static void StatusBar(GuiContext gui, StatusIcon icon, double fraction, vec2 first, float step)
    {
        // Less a hair, so a fraction a rounding error over a half icon does not show another.
        int halves = (int)Math.Ceiling(Math.Clamp(fraction, 0.0, 1.0) * 2 * BarIcons - 1e-9);
        for (int k = 0; k < BarIcons; k++)
        {
            GuiRect rect = new(first.x + k * step, first.y, BarIconSize, BarIconSize);
            int shown = halves - 2 * k;
            if (shown >= 2)
                gui.Image(rect, icon.Full);
            else if (shown == 1)
                gui.Image(rect, icon.Half);
            else
                gui.Image(rect, icon.Empty);
        }
    }

    private static void SpawnBenchmarkObjects(World world, int seed)
    {
        string? requested = Environment.GetEnvironmentVariable("MYT_SPAWN_OBJECTS");
        if (requested == null)
            return;
        if (!int.TryParse(requested, out int count) || count < 0 || count > 10000)
            throw new ArgumentException("MYT_SPAWN_OBJECTS must be an integer between 0 and 10000.");
        string mode = Environment.GetEnvironmentVariable("MYT_SPAWN_MODE") ?? "mixed";
        if (mode != "mixed" && mode != "blocks")
            throw new ArgumentException("MYT_SPAWN_MODE must be mixed or blocks.");

        // Keep every column inside the already loaded spawn area. Separate the initial
        // rotated shapes so the benchmark starts with falls, rather than penetrations.
        bool mixed = mode == "mixed";
        int side = mixed ? 10 : 24;
        double pitch = mixed ? 5.0 : 2.0;
        double low = (TerrainGenerator.SpawnSize - side * pitch) / 2.0;
        double floor = 0.0;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
            floor = Math.Max(floor, 64 + TerrainGenerator.GroundHeight((int)(low + x * pitch), (int)(low + y * pitch)));

        Random random = new(seed);
        for (int i = 0; i < count; i++)
        {
            int slot = i % (side * side);
            bool cube = mixed && i % 6 == 5;
            double yaw = random.NextDouble() * Math.Tau;
            dquat rotation = dquat.FromAxisAngle(yaw, dvec3.UnitZ)
                * dquat.FromAxisAngle((random.NextDouble() - 0.5) * 0.2, dvec3.UnitX);
            dvec3 position = new(low + slot % side * pitch, low + slot / side * pitch,
                floor + 16.0 + i / (side * side) * pitch);
            dvec3 velocity = new(3.0 * Math.Cos(yaw), 3.0 * Math.Sin(yaw), -2.0);
            dvec3 spin = new(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5);
            int size = cube ? 3 : 1;
            world.Spawn(TerrainGenerator.Box(new ivec3(size), cube ? BlockType.OakPlanks : BlockType.Stone),
                position - rotation * new dvec3(size * VoxelBody.CellSize / 2.0), rotation, velocity, spin);
        }
        Console.WriteLine($"Generated {count} additional {mode} objects (seed {seed}).");
    }

    /// <summary>MYT_SPAWN_ITEMS=n: drops n items, every kind in turn, in a grid before the spawn point, for screenshots.</summary>
    private static void SpawnItems(World world)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MYT_SPAWN_ITEMS"), out int count))
            return;

        dvec3 start = world.Player!.Spawn + new dvec3(-2.0, 2.5, 0.5);
        for (int i = 0; i < count; i++)
        {
            ItemType type = Items.Creative[i % Items.Creative.Length];
            dvec3 at = start + new dvec3(i % 8 * 0.6, i / 8 % 8 * 0.6, i / 64 * 0.6);
            world.DropItem(new ItemStack(type, 1), at, dvec3.Zero, orientation: dquat.FromAxisAngle(i * 0.7, dvec3.UnitZ));
        }
    }

    /// <summary>MYT_HOLD: an item for the player to hold in a screenshot, by its name, its words joined by underscores; "none" for the bare arm.</summary>
    private static ItemStack HeldForScreenshot(string name) // PROF
    {
        if (name == "none")
            return ItemStack.Empty;

        IEnumerable<ItemType> every = Items.NonBlockTypes.Concat(Enumerable.Range(1, Blocks.Count - 1).Select(b => Items.Of((BlockType)b)));
        ItemType type = every.FirstOrDefault(t => Items.Get(t).Name.Replace(' ', '_').Equals(name, StringComparison.OrdinalIgnoreCase));
        return type == ItemType.None ? throw new ArgumentException($"MYT_HOLD={name} names no item") : new ItemStack(type, 1);
    }

    /// <summary>
    /// A world of a seed, as it was saved, or new: its spawn area built and the player in it.
    /// The map streams in around the player from then on.
    /// </summary>
    /// <param name="sprites">The images of the items that are not blocks, which their dropped bodies are cut from.</param>
    /// <param name="mode">How the world is played.</param>
    /// <param name="saved">What the world held when it was last saved; null for a new world.</param>
    /// <param name="target">Where it is saved; null for a world that is not.</param>
    /// <param name="automated">Ignore the keyboard and mouse, for screenshots taken without a player.</param>
    public Game
    (
        GraphicsContext graphics,
        Renderer renderer,
        Input input,
        InterfaceArt art,
        ItemSprites sprites,
        GameSettings settings,
        string shaders,
        int seed,
        GameMode mode,
        WorldSnapshot? saved,
        SaveTarget? target,
        bool automated
    )
    {
        _graphics = graphics;
        _renderer = renderer;
        _input = input;
        _art = art;
        _settings = settings;
        _renderer.Video = settings;
        _target = target;
        _automated = automated;
        _inventoryScreen = new InventoryScreen(art, input);
        Seed = seed;
        Mode = mode;
        _showStress = Environment.GetEnvironmentVariable("MYT_STRESS") == "1";

        TerrainGenerator generator = new(seed);
        (Queue physicsQueue, CommandPool physicsPool) = graphics.PhysicsQueue;
        _world = new World(graphics.Vk, graphics.Device, graphics.PhysicalDevice, physicsPool, physicsQueue, shaders, graphics.StressQueue)
        {
            FollowPlayer = true,
            SimulationDistance = settings.SimulationDistance,
            Sprites = sprites
        };
        _world.Particles.Enabled = settings.Particles;
        // Compile simulation kernels during world loading, before the first TNT.
        _world.PrepareGpuBlasts();
        _world.BodyRemoved += renderer.RemoveBody;
        if (Environment.GetEnvironmentVariable("MYT_LOG") == "1")
        {
            _world.BlockBroken += (body, cell, voxel, cause) =>
                Console.WriteLine($"{voxel.Material.Name} at {cell} of {Describe(body)} broke: {cause}");
            _world.ImpactAnalysed += (bodies, energy, outcome, steps) =>
                Console.WriteLine($"impact of {energy / 1e3:F1} kJ between {string.Join(" and ", bodies.Select(Describe))}: {outcome.Sides.Sum(s => s.Damage.Length)} cells damaged, {(outcome.Sides.Any(s => s.Opened) ? "broke through, " : "")}{outcome.RetainedEnergy / energy:P0} of the energy left, {outcome.Rounds} rounds, {outcome.Milliseconds:F0} ms, {steps} steps after");
            _world.PlayerHurt += (damage, cause) =>
                Console.WriteLine($"the player took {damage:F1} HP from {cause}: health {_world.Player!.Health:P0}");
        }

        _streamer = new ChunkStreamer(_world, generator, settings.RenderDistance);
        _renderer.SetTerrain(generator, _streamer);
        if (saved != null)
        {
            WorldFile.Restore(saved, _world, _streamer);
            _renderer.RestoreLighting(saved.Lighting);
        }
        else
        {
            generator.BuildSpawn(_world);
            SpawnBenchmarkObjects(_world, seed);
            _world.ApplyTopologyChanges();
            Player player = _world.SpawnPlayer(generator.PlayerSpawn);
            player.Invulnerable = Creative;
            player.InstantBreak = Creative;
            SpawnItems(_world);
        }

        // Survival has none of creative's freedoms, whatever a save says.
        if (!Creative)
        {
            Player.Flying = false;
            Player.Invulnerable = false;
            Player.InstantBreak = false;
        }

        _shownHeld = Player.Inventory.Held;
        string? hold = Environment.GetEnvironmentVariable("MYT_HOLD"); // PROF
        if (hold != null) // PROF
            Player.Inventory.Slots[Player.Inventory.Selected] = HeldForScreenshot(hold); // PROF

        string? camera = Environment.GetEnvironmentVariable("MYT_CAMERA");
        if (camera != null)
        {
            double[] values = [.. camera.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))];
            Player.MoveTo(new dvec3(values[0], values[1], values[2] - Player.EyeHeight));
            Player.Yaw = values[3];
            Player.Pitch = values[4];
        }
    }

    private readonly GraphicsContext _graphics;
    private readonly Renderer _renderer;
    private readonly Input _input;
    private readonly InterfaceArt _art;
    private readonly GameSettings _settings;
    private readonly bool _automated;
    private readonly World _world;
    private readonly ChunkStreamer _streamer;
    private readonly List<LineVertex> _lines = [];
    private readonly List<DrawnItem> _drawnItems = [];
    private readonly InventoryScreen _inventoryScreen;
    private VoxelHit? _station;
    public bool StationOpened { get; private set; }
    // The held item's swing, which mining strikes with, and whether the one playing is a
    // placement's, which strikes nothing.
    private readonly HeldSwing _swing = new();
    private bool _swingPlaces;
    // MYT_SWING_PHASE: the held item held still that far through its swing, for screenshots.
    private readonly float? _frozenSwing = float.TryParse(Environment.GetEnvironmentVariable("MYT_SWING_PHASE"), System.Globalization.CultureInfo.InvariantCulture, out float phase) ? phase : null; // PROF
    private SaveTarget? _target;
    // The autosave being written, and why the last one failed, if it did.
    private Task _writing = Task.CompletedTask;
    private string? _saveError;
    private double _sinceSave;
    // Whether the button that resumed the game is still held, which must not mine.
    private bool _awaitingRelease;
    private bool _showStress;
    private bool _showHelp = true;
    private bool _showDebug;
    // The block the player aims at.
    private VoxelHit? _aimed;
    private readonly FrameTiming _frameTiming = new();
    // Time (s) until the next mining hit while the button is held.
    private double _nextHit;
    // How far the view has widened toward sprinting's, from 0 to 1.
    private double _widening;
    // How far the held item is still lowered after another was chosen (1 to 0), what it was
    // last frame, and how long its name has shown.
    private double _equip;
    private ItemStack _shownHeld;
    private double _heldNameShown = HeldNameSeconds;

    /// <summary>The seed of the world being played.</summary>
    public int Seed { get; }

    public GameMode Mode { get; }

    public bool Creative => Mode == GameMode.Creative;

    /// <summary>Whether the world is saved, which a world started for screenshots is not.</summary>
    public bool Saved => _target != null;

    public Player Player => _world.Player!;

    // PROF
    public string ProfExtra => $"lod {_renderer.LodTilesDrawn} ready {_renderer.TerrainReady} | {_renderer.LightingStatistics} colliders {_world.SimulationCounts} steps {_world.ProfSteps} cols {_streamer.LoadedColumns} gen {_streamer.Generating} mesh {_renderer.ChunksMeshing} waiting {_renderer.ChunksWaitingForColumns} posted {_renderer.MeshesPosted} gpu-mesh {_renderer.GpuMeshesDispatched}/{_renderer.GpuMeshesCompleted} {_renderer.GpuMeshMilliseconds:F3}ms snapshot {_renderer.MeshSnapshotMilliseconds:F2} ms upload {_renderer.MeshBytesUploaded / 1024} KB edit-lag {_renderer.MeshEditLatencyMilliseconds:F1}/{_renderer.MaxMeshEditLatencyMilliseconds:F1} ms drawn {_renderer.ChunksDrawn} calls {_renderer.VoxelDrawCalls} quads {_renderer.QuadsDrawn} renderGpu {_graphics.RenderGpuMilliseconds:F2} triangles {_world.Physics.Statistics.DynamicTriangleCount} pairs {_world.Physics.Statistics.CandidatePairCount} contacts {_world.Statistics.Contacts} serial {_world.Physics.Statistics.SerialRigidBodyCount} phys {_world.Statistics.PhysicsMilliseconds:F1} stress {_world.Statistics.MapStressMilliseconds:F1} [{_world.ExpStress}] pos {Player.Feet.x:F0},{Player.Feet.y:F0},{Player.Feet.z:F0}{_world.GpuBlastStatistics}";
    private int _profFrame; // PROF
    private dvec3 _profLastEye; // PROF

    private void Autopilot(double dt) // PROF
    {
        string? mode = Environment.GetEnvironmentVariable("MYT_AUTOPILOT");
        if (mode == null)
            return;
        _profFrame++;
        Player.Flying = mode is "fly" or "spin" or "tnt";
        Player.Yaw = mode == "spin" ? _profFrame * 0.035 : 0.3;
        Player.Pitch = -0.4;
        dvec3 forward = new(Math.Cos(Player.Yaw), Math.Sin(Player.Yaw), 0);
        bool sprint = mode != "walk";
        dvec3 dir = forward * (sprint ? Player.SprintFactor : 1.0);
        if (mode == "fly")
            dir.z = Player.Feet.z < 90 ? 0.3 : 0;
        if (mode is "still" or "spin" or "tnt")
            dir = dvec3.Zero;
        Player.Controls = new PlayerControls(dir, sprint && mode is not ("still" or "spin" or "tnt"));
        Player.Invulnerable = true;
        if (mode is not ("fly" or "spin" or "tnt") && _profFrame % 45 == 0)
            Player.JumpPressed = true;
        if (int.TryParse(Environment.GetEnvironmentVariable("MYT_AUTOTNT"), out int tntFrame) && _profFrame == tntFrame)
        {
            VoxelHit? ground = _world.Raycast(Player.Eye, Player.Forward, 32);
            VoxelBody body = ground?.Body ?? _world.Map;
            ivec3 cell = ground is VoxelHit hit ? hit.Cell + hit.Normal
                : (ivec3)dvec3.Floor(Player.Eye + Player.Forward * 4);
            string? fixedCell = Environment.GetEnvironmentVariable("MYT_AUTOTNT_CELL");
            if (fixedCell != null)
            {
                int[] coordinates = [.. fixedCell.Split(',').Select(int.Parse)];
                body = _world.Map;
                cell = new(coordinates[0], coordinates[1], coordinates[2]);
            }
            int count = int.TryParse(Environment.GetEnvironmentVariable("MYT_AUTOTNT_COUNT"), out int requested) ? Math.Clamp(requested, 1, 512) : 1;
            int side = (int)Math.Ceiling(Math.Cbrt(count));
            for (int i = 0; i < count; i++)
                _world.SetBlock(body, cell + new ivec3(i % side, i / side % side, i / (side * side)), BlockType.Tnt);
            Console.WriteLine($"TNT benchmark frame {_profFrame}: {count} blocks at {cell} of {body.Id}");
            Prof.Time("tnt.ignite", () => _world.Detonate(body, cell));
        }
        if (Environment.GetEnvironmentVariable("MYT_AUTOTHROW") == "1" && _profFrame > 60) // PROF
        {
            if (_profFrame % 10 == 0)
            {
                dvec3 start = Player.Eye + 1.4 * Player.Forward - new dvec3(0.5);
                _world.Spawn([(ivec3.Zero, BlockType.Stone)], start, dquat.Identity, ThrowSpeed * Player.Forward, new dvec3(1.5, 0.7, 0.3));
            }
            if (_profFrame % 60 == 0)
            {
                dvec3 ahead = Player.Eye + 7.0 * new dvec3(Math.Cos(Player.Yaw), Math.Sin(Player.Yaw), 0) + new dvec3(-1.5, -1.5, 3.0);
                _world.Spawn(TerrainGenerator.Box(new ivec3(3, 3, 3), BlockType.OakPlanks), ahead, dquat.Identity);
            }
        }
        if (Environment.GetEnvironmentVariable("MYT_AUTOEDIT") == "1" && _profFrame % 30 == 0)
        {
            VoxelHit? hit = _world.Raycast(Player.Eye, Player.Forward, Reach);
            if (hit is VoxelHit h)
            {
                if ((_profFrame / 30) % 2 == 0)
                    _world.Mine(h.Body, h.Cell, 100.0);
                else
                    Place(h, BlockType.Stone);
            }
        }
    }

    /// <summary>
    /// Moves the game on by a frame: the player's controls, the streaming, the world (held
    /// still while <paramref name="paused"/>) and what the renderer and HUD show.
    /// </summary>
    /// <param name="controlled">Whether the keyboard and mouse drive the player, as they do while no menu is open.</param>
    /// <param name="observedFrameTime">Real frame-loop interval for the HUD, independent of simulation catch-up.</param>
    public void Update(double dt, bool paused, bool controlled, double? observedFrameTime = null)
    {
        _world.Particles.Enabled = _settings.Particles;
        _frameTiming.Add(observedFrameTime ?? dt);
        if (!paused)
            _world.DaylightSeconds = (_world.DaylightSeconds + dt) % DaylightCycle.RepetitionSeconds;
        _renderer.Daylight = DaylightCycle.Evaluate(_world.DaylightSeconds);

        if (_automated)
            Autopilot(dt); // PROF
        else if (controlled)
            HandleInput(dt);
        else
        {
            Player.Controls = default;
            _swing.Update(dt);
        }

        _streamer.RenderDistance = _settings.RenderDistance;
        _world.SimulationDistance = _settings.SimulationDistance;
        _world.UpdateFurnaces(paused ? 0 : dt);
        Prof.Time("stream", () => _streamer.Update(Player.Feet));
        Prof.Time("world", () => _world.Update(paused ? 0.0 : dt));
        // Full voxel detail is streamed locally; voxel LOD continues to the horizon.
        _renderer.ViewDistance = _settings.RenderDistance * Grid3D<Voxel>.ChunkSize * VoxelBody.CellSize;
        Prof.Time("sync", () => _renderer.Sync(_world, Player.Eye));

        bool sprinting = !Player.Dead && Player.Controls.Sprint;
        _widening += ((sprinting ? 1.0 : 0.0) - _widening) * (1.0 - Math.Exp(-dt / WideningSmoothing));
        _equip = Math.Max(0.0, _equip - dt / EquipSeconds);
        _heldNameShown += dt;
        ItemStack held = Player.Inventory.Held;
        if (held.Type != _shownHeld.Type)
        {
            _equip = 1.0;
            _heldNameShown = 0.0;
        }

        _shownHeld = held;
        _aimed = Player.Dead ? null : _world.Raycast(Player.Eye, Player.Forward, Reach);
        BuildLines(_aimed);
        Autosave(dt);
    }

    /// <summary>Draws the world and over it the interface.</summary>
    public void Draw(Frame frame, ReadOnlySpan<GuiVertex> gui)
    {
        double fieldOfView = _settings.FieldOfView * Math.PI / 180.0 + _widening * SprintWidening;
        Camera camera = new(_world.DrawnEye, Player.Forward, fieldOfView, _graphics.Extent.Width / (double)_graphics.Extent.Height);
        if (Environment.GetEnvironmentVariable("MYT_EYELOG") != null && _profFrame > 400 && _profFrame < 430) // PROF
            Console.WriteLine($"eye {_profFrame} drawn {(camera.Position - _profLastEye).xy.Length * 1000:F1} mm, raw {Player.Eye.x:F3}"); // PROF
        _profLastEye = camera.Position; // PROF

        _drawnItems.Clear();
        foreach (DroppedItem item in _world.DroppedItems)
        {
            (dvec3 position, dquat orientation) = _world.DrawnPose(item);
            _drawnItems.Add(new DrawnItem(item.Stack.Type, position, orientation));
        }

        ViewModel? held = Player.Dead ? null : HeldModel(Player.Inventory.Held);
        _renderer.Draw(frame, camera, _world.Bodies, _world.DrawnPose, _world.Particles, _drawnItems, held, CollectionsMarshal.AsSpan(_lines), gui, _showStress);
    }

    /// <summary>
    /// Where the held item is drawn, relative to the eye in world axes: at the lower right of
    /// the view, a block turned to show two sides and its top, a tool held up as in a hand, the
    /// bare arm reaching in from the corner; swung about its grip on a strike (<see cref="HeldSwing"/>),
    /// a tool chopping, the arm jabbing and anything else punching, and lowered for a moment after
    /// another is chosen.
    /// </summary>
    private ViewModel HeldModel(ItemStack held)
    {
        dvec3 forward = Player.Forward;
        dvec3 right = dvec3.Cross(forward, dvec3.UnitZ).Normalized;
        dvec3 up = dvec3.Cross(right, forward);
        // Eye space: x right, y forward, z up.
        mat4 eye = new
        (
            (float)right.x, (float)right.y, (float)right.z, 0f,
            (float)forward.x, (float)forward.y, (float)forward.z, 0f,
            (float)up.x, (float)up.y, (float)up.z, 0f,
            0f, 0f, 0f, 1f
        );

        (mat4 placed, vec3 grip) = held.IsEmpty
            ? (mat4.Translate(0.42f, 0.62f, -0.42f) * mat4.RotateZ(0.35f) * mat4.RotateX(0.45f) * mat4.Scale(0.13f, 0.62f, 0.13f), Shoulder)
            : held.Info.IsBlock
                ? (mat4.Translate(0.5f, 1.05f, -0.36f) * mat4.RotateZ(0.6f) * mat4.Scale(0.44f), BlockGrip)
                : (mat4.Translate(0.5f, 0.85f, -0.3f) * mat4.RotateX(0.2f) * mat4.RotateZ(1.7f) * mat4.Scale(0.6f), ToolGrip);

        SwingStyle style = held.IsEmpty ? SwingStyle.Jab : held.Info.Tool != null ? SwingStyle.Chop : SwingStyle.Punch;
        HandPose pose = _frozenSwing is float phase ? HeldSwing.Sample(phase, style) : _swing.Pose(style); // PROF
        mat4 swung = pose.About((placed * new vec4(grip, 1f)).xyz);
        mat4 lowered = mat4.Translate(HeldShift + new vec3(0f, 0f, (float)(-0.35 * _equip * _equip)));
        return new ViewModel(held.IsEmpty ? ItemType.None : held.Type, eye * lowered * swung * placed);
    }

    /// <summary>Makes the mouse button that is down, which clicked a menu away, not mine until it is let go.</summary>
    public void AwaitRelease()
    {
        _awaitingRelease = true;
    }

    public void Respawn()
    {
        _world.RespawnPlayer();
        AwaitRelease();
    }

    /// <summary>
    /// Writes the world to its save, once any autosave being written is done, and records that
    /// it was played now. Nothing for a world that is not saved.
    /// </summary>
    public void Save()
    {
        if (_target == null)
            return;

        FinishWriting();
        WorldSnapshot snapshot = WorldFile.Capture(_world, _streamer) with { Lighting = _renderer.CaptureLighting() };
        WorldFile.Write(_target.Store.PathOf(_target.World, WorldStore.DataFile), snapshot);
        _target = _target with { World = _target.Store.Touch(_target.World) };
        _sinceSave = 0.0;
        _saveError = null;
    }

    /// <summary>
    /// Every <see cref="AutosaveSeconds"/>, takes a snapshot of the world and writes it on a
    /// background thread, unless the last is still being written.
    /// </summary>
    private void Autosave(double dt)
    {
        if (_target == null)
            return;

        if (_writing.IsFaulted)
            FinishWriting();

        _sinceSave += dt;
        if (_sinceSave < AutosaveSeconds || !_writing.IsCompleted)
            return;

        _sinceSave = 0.0;
        WorldSnapshot snapshot = WorldFile.Capture(_world, _streamer) with { Lighting = _renderer.CaptureLighting() };
        string path = _target.Store.PathOf(_target.World, WorldStore.DataFile);
        _writing = Task.Run(() => WorldFile.Write(path, snapshot));
        _target = _target with { World = _target.Store.Touch(_target.World) };
    }

    /// <summary>Waits for the autosave being written, if any; if it failed, says so on the console and the HUD.</summary>
    private void FinishWriting()
    {
        try
        {
            _writing.Wait();
        }
        catch (AggregateException failure)
        {
            Exception cause = failure.InnerException ?? failure;
            _saveError = $"Autosave failed: {cause.Message}";
            Console.Error.WriteLine($"{_saveError}\n{cause}");
        }

        _writing = Task.CompletedTask;
    }

    private bool Down(GameAction action) => _input.Down(_settings.Binding(action));
    private bool Pressed(GameAction action) => _input.Pressed(_settings.Binding(action));

    private void HandleInput(double dt)
    {
        _awaitingRelease &= Down(GameAction.Mine) || Down(GameAction.Place);

        Player.Look(_input.MouseDelta * (_settings.MouseSensitivity / 100.0));
        Player.Controls = ReadControls();

        if (Creative && _input.Pressed(Keys.F3))
            _showDebug = !_showDebug;
        if (Creative && Pressed(GameAction.Fly))
            Player.Flying = !Player.Flying;
        if (Creative && Pressed(GameAction.Invulnerable))
            Player.Invulnerable = !Player.Invulnerable;
        if (Creative && Pressed(GameAction.InstantBreak))
            Player.InstantBreak = !Player.InstantBreak;
        if (Pressed(GameAction.Stress))
            _showStress = !_showStress;
        if (Pressed(GameAction.Help))
            _showHelp = !_showHelp;

        Inventory inventory = Player.Inventory;
        for (int k = 0; k < Inventory.HotbarSize; k++)
        {
            if (Pressed(GameAction.Slot1 + k))
                inventory.Selected = k;
        }

        if (_input.Scroll != 0)
            inventory.Selected = ((inventory.Selected - Math.Sign(_input.Scroll)) % Inventory.HotbarSize + Inventory.HotbarSize) % Inventory.HotbarSize;

        ItemStack held = inventory.Held;

        // Discrete hits: one on pressing, then HitsPerSecond while held. Each starts a swing,
        // and lands as the swing strikes.
        bool mining = Down(GameAction.Mine) && !_awaitingRelease;
        SwingStyle style = held.IsEmpty ? SwingStyle.Jab : held.Info.Tool != null ? SwingStyle.Chop : SwingStyle.Punch;
        int strikes = 0;
        _nextHit = mining ? _nextHit - dt : 0.0;
        if (mining && _nextHit <= 0.0)
        {
            int landed = _swing.Start(style);
            strikes += _swingPlaces ? 0 : landed;
            _swingPlaces = false;
            _nextHit = Math.Max(_nextHit, -0.5 / HitsPerSecond) + 1.0 / HitsPerSecond;
        }

        int struck = _swing.Update(dt);
        strikes += _swingPlaces ? 0 : struck;
        for (int k = 0; k < strikes; k++)
        {
            if (_world.Raycast(Player.Eye, Player.Forward, Reach) is not VoxelHit mined)
                continue;

            Voxel voxel = mined.Body.Grid[mined.Cell];
            if (_world.Mine(mined, Player.Eye, Player.Forward, Items.ToolOf(held), Creative && Player.InstantBreak) && !Creative)
                DropMined(mined, voxel);
        }

        VoxelHit? target = _awaitingRelease ? null : _world.Raycast(Player.Eye, Player.Forward, Reach);
        if (target is VoxelHit stationHit && Pressed(GameAction.Place)
            && stationHit.Body.Grid[stationHit.Cell].Type is BlockType.Workbench or BlockType.Furnace
            && !Down(GameAction.Crouch))
        {
            _station = stationHit;
            StationOpened = true;
            _awaitingRelease = true;
        }
        else if (target is VoxelHit explosive && Pressed(GameAction.Place) && !Down(GameAction.Crouch)
            && explosive.Body.Grid[explosive.Cell].Type == BlockType.Tnt)
        {
            _world.Detonate(explosive.Body, explosive.Cell);
            _swing.Start(SwingStyle.Punch);
            _swingPlaces = true;
        }
        else if (target is VoxelHit door && Pressed(GameAction.Place) && !Down(GameAction.Crouch)
            && BlockShapes.IsDoor(door.Body.Grid[door.Cell].Type))
        {
            _world.ToggleDoor(door.Body, door.Cell);
        }
        else if (Pressed(GameAction.Place) && Player.EatHeld(Creative))
        {
            _swing.Start(SwingStyle.Punch);
            _swingPlaces = true;
        }
        else if (target is VoxelHit hit && Pressed(GameAction.Place) && held.Info.IsBlock && Place(hit, held.Info.Block))
        {
            if (!Creative)
                inventory.Take(inventory.Selected, 1);
            _swing.Start(style);
            _swingPlaces = true;
        }

        if (Pressed(GameAction.Drop) && !held.IsEmpty)
        {
            int count = Down(GameAction.Sprint) ? held.Count : 1;
            Throw(Creative ? held.WithCount(count) : inventory.Take(inventory.Selected, count));
        }

        if (Creative && Pressed(GameAction.Shoot) && held.Info.IsBlock && Blocks.IsSolid(held.Info.Block))
        {
            dvec3 start = Player.Eye + 1.4 * Player.Forward - new dvec3(0.5);
            _world.Spawn([(ivec3.Zero, held.Info.Block)], start, dquat.Identity,
                Player.LinearVelocity + ThrowSpeed * Player.Forward, new dvec3(1.5, 0.7, 0.3));
        }

        if (Creative && Pressed(GameAction.SpawnCube) && held.Info.IsBlock && Blocks.IsSolid(held.Info.Block))
        {
            dvec3 ahead = Player.Eye + 7.0 * new dvec3(Math.Cos(Player.Yaw), Math.Sin(Player.Yaw), 0) + new dvec3(-1.5, -1.5, 3.0);
            _world.Spawn(TerrainGenerator.Box(new ivec3(3, 3, 3), held.Info.Block), ahead, dquat.Identity);
        }
    }

    /// <summary>What a block the player mined out drops: an item where it was, tossed up a little, moving with what it was part of.</summary>
    private void DropMined(VoxelHit mined, Voxel voxel)
    {
        ItemType dropped = Items.DropOf(voxel.Type);
        if (dropped == ItemType.None)
            return;

        dvec3 center = mined.Body.ToWorld(VoxelBody.CellCenter(mined.Cell));
        dvec3 toss = new(Random.Shared.NextDouble() - 0.5, Random.Shared.NextDouble() - 0.5, 1.5 + Random.Shared.NextDouble());
        dquat turned = dquat.FromAxisAngle(Random.Shared.NextDouble() * Math.Tau, dvec3.UnitZ);
        _world.DropItem(new ItemStack(dropped, 1), center, mined.Body.LinearVelocity + toss, World.MinedPickupDelay, turned);
    }

    /// <summary>
    /// Drops items from the player: thrown from a little under the eyes, ahead of it, along
    /// where it looks and a little up, with the player's own motion. It starts as far ahead,
    /// along the ground, as clears the player's box, and the player cannot pick it up again
    /// for <see cref="DroppedPickupDelay"/>.
    /// </summary>
    private void Throw(ItemStack stack)
    {
        if (stack.IsEmpty)
            return;

        double radius = stack.Info.IsBlock ? 0.5 * Math.Sqrt(3.0) * Items.BlockEdge : 0.5 * Math.Sqrt(2.0) * Items.SpriteEdge;
        dvec3 ahead = new(Math.Cos(Player.Yaw), Math.Sin(Player.Yaw), 0.0);
        dvec3 start = Player.Eye - new dvec3(0, 0, 0.3);
        double distance = 0.4;
        while (distance < 1.5 && Player.Overlaps(start + distance * ahead - radius, start + distance * ahead + radius))
            distance += 0.05;

        dvec3 velocity = Player.LinearVelocity + DropSpeed * Player.Forward + new dvec3(0, 0, DropLift);
        dvec3 spin = new(Random.Shared.NextDouble() - 0.5, Random.Shared.NextDouble() - 0.5, Random.Shared.NextDouble() - 0.5);
        _world.DropItem(stack, start + distance * ahead, velocity, DroppedPickupDelay, dquat.FromAxisAngle(Player.Yaw, dvec3.UnitZ), spin);
    }

    /// <summary>The inventory screen over the world (<see cref="InventoryScreen"/>); station recipes require a right-clicked station.</summary>
    public void BuildInventory(GuiContext gui)
    {
        if (_station is VoxelHit active && ((active.Body.ToWorld(VoxelBody.CellCenter(active.Cell)) - Player.Eye).Length > Reach + 1
            || active.Body.Grid[active.Cell].Type is not (BlockType.Workbench or BlockType.Furnace)))
            _station = null;
        bool workbench = _station is VoxelHit bench && bench.Body.Grid[bench.Cell].Type == BlockType.Workbench;
        Furnace? furnace = _station is VoxelHit oven && oven.Body.Grid[oven.Cell].Type == BlockType.Furnace ? _world.FurnaceAt(oven) : null;
        _inventoryScreen.Draw(gui, Player.Inventory, Creative, workbench, Throw, furnace);
    }

    /// <summary>Closes the inventory screen: what the pointer carries goes back into the inventory, or is dropped where it has no room.</summary>
    public void CloseInventory()
    {
        InventoryScreen.Close(Player.Inventory, Throw);
        _station = null;
        StationOpened = false;
    }

    /// <summary>What the keys ask the player to do: walk or fly along where it looks, jump, sprint.</summary>
    private PlayerControls ReadControls()
    {
        dvec3 forward = new(Math.Cos(Player.Yaw), Math.Sin(Player.Yaw), 0);
        dvec3 right = new(Math.Sin(Player.Yaw), -Math.Cos(Player.Yaw), 0);
        double ahead = (Down(GameAction.Forward) ? 1 : 0) - (Down(GameAction.Backward) ? 1 : 0);
        double aside = (Down(GameAction.Right) ? 1 : 0) - (Down(GameAction.Left) ? 1 : 0);
        dvec3 direction = ahead * forward + aside * right;
        if (Player.Flying)
            direction.z = (Down(GameAction.Jump) ? 1 : 0) - (Down(GameAction.Crouch) ? 1 : 0);
        if (direction.LengthSqr > 1)
            direction = direction.Normalized;

        if (!Player.Flying && Pressed(GameAction.Jump))
            Player.JumpPressed = true;

        // Sprinting goes only forward, and speeds up only the part of the motion straight
        // ahead: going diagonally, the player sidesteps no faster than walking.
        bool crouch = !Player.Flying && Down(GameAction.Crouch);
        bool sprint = !crouch && !Player.Crouching && Down(GameAction.Sprint) && ahead > 0;
        if (sprint)
            direction += (Player.SprintFactor - 1.0) * dvec3.Dot(direction, forward) * forward;

        return new PlayerControls(direction, sprint, crouch);
    }

    /// <summary>
    /// Places a block against the face the player looks at, in whichever body that face belongs
    /// to. Returns whether it did: not where the player stands, nor above the map's top.
    /// </summary>
    private bool Place(VoxelHit hit, BlockType block)
    {
        if (block is BlockType.Water or BlockType.Lava && hit.Body is not VoxelMap)
            return false;

        ivec3 cell = hit.Cell + hit.Normal;
        if (Blocks.IsSolid(hit.Body.Grid[cell].Type)) return false;
        dvec3 towardPlayer = hit.Body.DirectionToLocal(new dvec3(-Math.Cos(Player.Yaw), -Math.Sin(Player.Yaw), 0));
        dvec3 hitPoint = hit.Body.ToLocal(Player.Eye + Player.Forward.Normalized * hit.Distance)
            / VoxelBody.CellSize - new dvec3(hit.Cell);
        BlockOrientation placement = Voxel.Placement(block, hit.Normal, towardPlayer, hitPoint);
        if (!_world.CanPlaceBlock(hit.Body, cell, new Voxel(block, 0, placement)))
            return false;

        if (BlockShapes.IsDoor(block))
            return _world.PlaceDoor(hit.Body, cell, placement);
        _world.SetBlock(hit.Body, cell, block, placement);
        return true;
    }

    /// <summary>The outline of the targeted block, drawn in its body's frame where the body is drawn.</summary>
    private void BuildLines(VoxelHit? target)
    {
        _lines.Clear();
        if (target is not VoxelHit hit)
            return;

        const double Grow = 0.004;
        vec4 color = new(0.05f, 0.05f, 0.05f, 0.8f);
        (dvec3 position, dquat orientation) = _world.DrawnPose(hit.Body);
        Voxel aimed = hit.Body.Grid[hit.Cell];
        if (BlockShapes.HasShape(aimed.Type))
        {
            foreach (var (a, b) in BlockShapes.Edges(aimed))
            {
                dvec3 start = new dvec3(hit.Cell) + (a - 0.5) * (1 + 2 * Grow) + 0.5;
                dvec3 end = new dvec3(hit.Cell) + (b - 0.5) * (1 + 2 * Grow) + 0.5;
                _lines.Add(new LineVertex((vec3)(position + orientation * start), color));
                _lines.Add(new LineVertex((vec3)(position + orientation * end), color));
            }
            return;
        }
        dvec3 low = new dvec3(hit.Cell) - Grow;
        dvec3 size = new(1 + 2 * Grow);
        for (int axis = 0; axis < 3; axis++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                dvec3 start = low;
                start[(axis + 1) % 3] += (corner & 1) * size.x;
                start[(axis + 2) % 3] += (corner >> 1) * size.x;
                dvec3 end = start;
                end[axis] += size.x;
                _lines.Add(new LineVertex((vec3)(position + orientation * start), color));
                _lines.Add(new LineVertex((vec3)(position + orientation * end), color));
            }
        }
    }

    /// <summary>The HUD, in interface units: the hotbar and status bars, the statistics, what the player aims at and the help.</summary>
    /// <param name="crosshair">Whether to draw the crosshair, which no menu should have over it.</param>
    public void BuildHud(GuiContext gui, bool crosshair)
    {
        long hudStart = Stopwatch.GetTimestamp(); // PROF
        float width = gui.Screen.Width;
        float height = gui.Screen.Height;
        GuiColor white = GuiColor.White;

        if (crosshair)
        {
            gui.Rect(new GuiRect(width / 2 - 4.5f, height / 2 - 0.5f, 9, 1), white);
            gui.Rect(new GuiRect(width / 2 - 0.5f, height / 2 - 4.5f, 1, 9), white);
        }

        // Hotbar: the inventory's first slots, the one held outlined.
        const float Slot = 20f;
        Inventory inventory = Player.Inventory;
        int slots = Inventory.HotbarSize;
        float left = MathF.Floor(width / 2 - slots * Slot / 2);
        float top = MathF.Floor(height - Slot - 4);
        for (int k = 0; k < slots; k++)
        {
            GuiRect slot = new(left + k * Slot, top, Slot, Slot);
            gui.Image(slot.Inset(1), _art.Slot);
            ItemStack stack = inventory.Slots[k];
            if (stack.IsEmpty)
                continue;

            gui.Image(new GuiRect(slot.X + 2, slot.Y + 2, 16, 16), _art.Icon(stack.Type));
            if (stack.Count > 1 && !Creative)
            {
                string count = stack.Count.ToString();
                gui.Text(new vec2(slot.Right - 2 - gui.MeasureText(count), slot.Y + 11), count, white);
            }
        }

        GuiRect held = new(left + inventory.Selected * Slot - 1, top - 1, Slot + 2, Slot + 2);
        gui.Outline(held, white, 2);

        // The status bars, two rows over the hotbar while the player can be hurt: health, and
        // defense over it, on the left, filling from the left; hunger, and thirst over it, on the
        // right, filling from the right.
        float overBars = top;
        if (!Player.Invulnerable)
        {
            float middle = left + slots * Slot / 2;
            float lower = top - 2 - BarIconSize;
            float upper = lower - BarIconSize;
            float leftmost = middle - BarGap / 2 - (BarIcons - 1) * BarStep - BarIconSize;
            float rightmost = middle + BarGap / 2 + (BarIcons - 1) * BarStep;
            StatusBar(gui, _art.Health, Player.Health, new vec2(leftmost, lower), BarStep);
            StatusBar(gui, _art.Defense, Player.Defense, new vec2(leftmost, upper), BarStep);
            StatusBar(gui, _art.Hunger, Player.Food, new vec2(rightmost, lower), -BarStep);
            StatusBar(gui, _art.Thirst, Player.Water, new vec2(rightmost, upper), -BarStep);
            overBars = upper;
        }

        if (_heldNameShown < HeldNameSeconds && !inventory.Held.IsEmpty)
        {
            float fade = (float)Math.Min(1.0, 4.0 * (HeldNameSeconds - _heldNameShown) / HeldNameSeconds);
            gui.Label(new GuiRect(0, overBars - 10, width, 8), inventory.Held.Info.Name, white.WithAlpha(fade));
        }

        // Status.
        WorldStatistics stats = _world.Statistics;
        float y = 5;
        string freedoms = Creative
            ? $"   {(Player.Flying ? "FLYING" : "WALKING")}{(Player.Invulnerable ? "   INVULNERABLE" : "")}{(Player.InstantBreak ? "   ONE-HIT BREAKING" : "")}"
            : "";
        gui.Text(new vec2(5, y), $"{_frameTiming.FramesPerSecond:F0} FPS (slowest frame {_frameTiming.SlowestSeconds * 1000:F0} ms)", white);
        if (_saveError != null)
            gui.Text(new vec2(5, y + 10), _saveError, OverloadColor, 6);
        if (!Creative || !_showDebug)
        {
            Prof.Add("hud", Stopwatch.GetElapsedTime(hudStart).TotalMilliseconds);
            return;
        }
        y += 10;
        gui.Text(new vec2(5, y), $"{(Creative ? "CREATIVE" : "SURVIVAL")}   health {Player.Health * 100:F0}%{freedoms}{(_showStress ? "   STRESS VIEW" : "")}", white);
        y += 10;
        gui.Text(new vec2(5, y), $"{stats.Objects} objects   {_world.DroppedItems.Count} items   {stats.Contacts} contacts", Dim, 6);
        y += 8;
        gui.Text(new vec2(5, y), $"physics {stats.PhysicsMilliseconds:F1} ms   map stress {stats.MapStressMilliseconds:F1} ms (residual {stats.MapStressResidual:0.0e0})   object stress {stats.StressMilliseconds:F0} ms   last impact {stats.ImpactMilliseconds:F0} ms", Dim, 6);
        y += 8;
        gui.Text(new vec2(5, y), $"LOD base distance {_settings.RenderDistance * 16} blocks (infinite LOD, {_renderer.LodTilesDrawn} tiles)   columns {_streamer.LoadedColumns} (+{_streamer.Generating} generating)   chunks {_renderer.ChunksDrawn} drawn, {_renderer.ChunksMeshing} meshing, {_renderer.MeshBytes / (1 << 20)} MB", Dim, 6);
        y += 8;
        (int colliders, int simulated, int parked) = _world.SimulationCounts;
        gui.Text(new vec2(5, y), $"simulation distance {_settings.SimulationDistance}   {simulated} objects moving, {parked} parked   {colliders} chunk colliders", Dim, 6);
        y += 8;
        dvec3 feet = Player.Feet;
        int column = (int)Math.Floor(feet.x);
        int row = (int)Math.Floor(feet.y);
        gui.Text(new vec2(5, y), $"x {feet.x:F0}  y {feet.y:F0}  z {feet.z:F0}   {_streamer.Generator.BiomeOf(column, row)}   seed {Seed}", Dim, 6);
        y += 12;

        if (_saveError != null)
        {
            gui.Text(new vec2(5, y), _saveError, OverloadColor, 6);
            y += 12;
        }

        if (!_world.RegionReady)
            gui.Label(new GuiRect(0, height / 3, width, 8), "Generating terrain...", white);

        if (_aimed is VoxelHit hit)
        {
            Voxel voxel = hit.Body.Grid[hit.Cell];
            double ratio = hit.Body.StressRatio(hit.Cell, voxel);
            string owner = hit.Body is VoxelMap ? "map" : "object";
            gui.Text(new vec2(5, y), $"{voxel.Material.Name} ({owner})", white);
            y += 10;
            gui.Text(new vec2(5, y), $"health {voxel.Health * 100:F0}%   load {ratio * 100:F0}% of its elastic limit", ratio >= 1 ? OverloadColor : Dim, 6);
            y += 8;
            bool instant = Creative && Player.InstantBreak;
            if (!instant && !voxel.Material.Unbreakable && Items.ToolOf(inventory.Held).Damage(voxel.Material, voxel.Health, 1.0) <= 0.0)
            {
                gui.Text(new vec2(5, y), "too hard: a strike of what you hold does not get past its elastic limit", OverloadColor, 6);
                y += 8;
            }

            y += 4;
        }

        if (_showHelp)
        {
            foreach (string line in new[] { "F3 diagnostics   ESC game menu   Settings > Controls for keybinds", $"{Controls.Name(_settings.Binding(GameAction.Shoot))} shoot held block   {Controls.Name(_settings.Binding(GameAction.SpawnCube))} spawn cube" })
            {
                gui.Text(new vec2(5, y), line, Dim, 6);
                y += 8;
            }
        }

        Prof.Add("hud", Stopwatch.GetElapsedTime(hudStart).TotalMilliseconds); // PROF
    }

    public void Dispose()
    {
        FinishWriting();
        _streamer.Dispose();
        _world.Dispose();
    }
}
