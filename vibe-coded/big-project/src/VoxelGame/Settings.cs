using System.Text.Json;
using Silk.NET.GLFW;

namespace VoxelGame;


/// <summary>The player's settings, kept between runs as JSON in the data directory.</summary>
internal sealed class GameSettings
{
    public const int MinFieldOfView = 50;
    public const int MaxFieldOfView = 120;
    public const int MinSensitivity = 10;
    public const int MaxSensitivity = 300;
    /// <summary>The interface scale that picks itself from the window's size.</summary>
    public const int AutoGuiScale = 0;

    /// <summary>The settings saved at a path, or the defaults if there are none or they cannot be read.</summary>
    public static GameSettings Load(string path)
    {
        if (!File.Exists(path))
            return new GameSettings();

        try
        {
            GameSettings settings = JsonSerializer.Deserialize<GameSettings>(File.ReadAllText(path)) ?? new GameSettings();
            settings.Clamp();
            return settings;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Could not read the settings in {path}, so the defaults are used: {exception.Message}");
            return new GameSettings();
        }
    }

    /// <summary>Base spherical LOD radius, stored in chunk columns for save compatibility; shown as blocks.</summary>
    public int RenderDistance { get; set; } = ChunkStreamer.DefaultRenderDistance;

    /// <summary>How far (chunk columns) around the player objects move.</summary>
    public int SimulationDistance { get; set; } = World.DefaultSimulationDistance;

    /// <summary>Vertical field of view, degrees.</summary>
    public int FieldOfView { get; set; } = 110;

    /// <summary>How far the view turns for a move of the mouse, percent of the default.</summary>
    public int MouseSensitivity { get; set; } = 100;

    /// <summary>Pixels per interface unit, or <see cref="AutoGuiScale"/>.</summary>
    public int GuiScale { get; set; } = AutoGuiScale;

    public bool TransparentLeaves { get; set; } = true;
    public bool Particles { get; set; } = true;
    public bool AmbientOcclusion { get; set; } = true;
    public bool Sun { get; set; } = true;
    public bool SpecularLighting { get; set; } = true;
    public bool DynamicShadows { get; set; }
    public bool BlockLighting { get; set; } = true;
    public int AmbientBrightness { get; set; } = 35;
    public Dictionary<GameAction, int> Keybinds { get; set; } = Controls.Defaults();

    public int Binding(GameAction action) => Keybinds.TryGetValue(action, out int key) ? key : Controls.Defaults()[action];

    // Swapping keeps actions accessible when the requested key is already assigned.
    public void Bind(GameAction action, int key)
    {
        int old = Binding(action);
        foreach (GameAction other in Enum.GetValues<GameAction>())
            if (other != action && Binding(other) == key) Keybinds[other] = old;
        Keybinds[action] = key;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Brings every setting into its range, as a hand-edited file may not have them.</summary>
    public void Clamp()
    {
        RenderDistance = Math.Clamp(RenderDistance, ChunkStreamer.MinRenderDistance, ChunkStreamer.MaxRenderDistance);
        SimulationDistance = Math.Clamp(SimulationDistance, World.MinSimulationDistance, World.MaxSimulationDistance);
        FieldOfView = Math.Clamp(FieldOfView, MinFieldOfView, MaxFieldOfView);
        MouseSensitivity = Math.Clamp(MouseSensitivity, MinSensitivity, MaxSensitivity);
        GuiScale = Math.Max(GuiScale, AutoGuiScale);
        AmbientBrightness = Math.Clamp(AmbientBrightness, 0, 100);
        Keybinds ??= Controls.Defaults();
        foreach (var (action, key) in Controls.Defaults())
            if (!Keybinds.TryGetValue(action, out int value) || !Controls.Valid(value)) Keybinds[action] = key;
    }
}


/// <summary>A server in the multiplayer list: what the player calls it and where it is.</summary>
internal sealed record ServerInfo(string Name, string Address);


/// <summary>The multiplayer list, kept as JSON in the data directory.</summary>
internal static class ServerList
{
    /// <summary>The servers saved at a path; none if there is no list or it cannot be read.</summary>
    public static List<ServerInfo> Load(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ServerInfo>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Could not read the server list in {path}, so it starts empty: {exception.Message}");
            return [];
        }
    }

    public static void Save(string path, IReadOnlyList<ServerInfo> servers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal enum GameAction
{
    Forward, Backward, Left, Right, Jump, Crouch, Sprint, Mine, Place, Inventory,
    Drop, Fly, Invulnerable, InstantBreak, SpawnCube, Shoot, Stress, Help,
    Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9
}

internal static class Controls
{
    public static Dictionary<GameAction, int> Defaults()
    {
        Keys[] keys = [Keys.W, Keys.S, Keys.A, Keys.D, Keys.Space, Keys.ShiftLeft, Keys.ControlLeft,
            Keys.Unknown, Keys.Unknown, Keys.E, Keys.Q, Keys.F, Keys.I, Keys.B, Keys.G, Keys.R, Keys.T, Keys.H,
            Keys.Number1, Keys.Number2, Keys.Number3, Keys.Number4, Keys.Number5, Keys.Number6, Keys.Number7, Keys.Number8, Keys.Number9];
        var result = Enum.GetValues<GameAction>().ToDictionary(a => a, a => (int)keys[(int)a]);
        result[GameAction.Mine] = -1;
        result[GameAction.Place] = -2;
        return result;
    }
    public static bool Valid(int key) => key is >= -3 and <= -1 ||
        (Enum.IsDefined(typeof(Keys), key) && key != (int)Keys.Unknown && key != (int)Keys.Escape && key != (int)Keys.F3);
    public static string Name(int key) => key < 0 ? $"Mouse {-key}" : ((Keys)key).ToString();
}
