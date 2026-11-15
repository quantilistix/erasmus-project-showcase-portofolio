using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Gui;

using Silk.NET.GLFW;

namespace VoxelGame;


/// <summary>
/// The game as the player meets it: the title screen and the menus behind it (the saved
/// worlds, the servers, the settings), and the world being played (<see cref="Game"/>), with
/// the game menu and the death screen over it. The menus are laid out as Minecraft's are, in
/// interface units, over tiled dirt, or over the world, dimmed, while one is being played.
/// <para>
/// MYT_MENU=title|worlds|create|servers|settings|pause|inventory opens that screen;
/// MYT_WORLD=folder plays that saved world; with MYT_SCREENSHOT and neither, the game starts
/// straight in a world that is not saved, in creative unless MYT_GAMEMODE=survival.
/// </para>
/// </summary>
internal sealed unsafe class App : IDisposable
{
    private enum Screen
    {
        Title,
        Worlds,
        CreateWorld,
        RenameWorld,
        Servers,
        EditServer,
        Settings,
        Confirm,
        Message,
        Loading,
        Playing,
        Inventory,
        Paused
    }

    /// <summary>A question to confirm, as before deleting something: what is asked, the button that does it, what it does, and where it goes back to.</summary>
    private sealed record Confirmation(string Question, string Detail, string Action, Action Yes, Screen Back);

    /// <summary>A message, as of something that failed, and the screen it goes back to.</summary>
    private sealed record Notice(string Title, string Text, Screen Back);

    private const float ButtonWidth = 200f;
    private const float ButtonHeight = 20f;
    private const float RowHeight = 36f;
    // Pixels per pixel of the title image, per pixel of the interface, about.
    private const float TitleScale = 1.5f;
    // The smallest screen (units) the automatic interface scale leaves, as the HUD was laid
    // out at 720p, and the smallest any scale may leave.
    private static readonly vec2 AutoScaleMinimum = new(640f, 360f);
    private static readonly vec2 ScaleMinimum = new(320f, 240f);
    private static readonly vec2 DirtTile = new(32f);
    private static readonly GuiColor TitleDirt = GuiColor.FromRgb(0x707070);
    private static readonly GuiColor MenuDirt = GuiColor.FromRgb(0x505050);
    private static readonly GuiColor ListDirt = GuiColor.FromRgb(0x303030);
    private static readonly GuiColor Gray = GuiColor.FromRgb(0xA0A0A0);
    private static readonly GuiColor DarkGray = GuiColor.FromRgb(0x808080);

    private static Screen StartScreen(string? requested, bool automated)
    {
        return requested switch
        {
            null => automated ? Screen.Playing : Screen.Title,
            "title" => Screen.Title,
            "worlds" => Screen.Worlds,
            "create" => Screen.CreateWorld,
            "servers" => Screen.Servers,
            "settings" => Screen.Settings,
            "pause" => Screen.Paused,
            "inventory" => Screen.Inventory,
            _ => throw new ArgumentException($"MYT_MENU={requested} names no screen: title, worlds, create, servers, settings, pause or inventory")
        };
    }

    /// <param name="dataDirectory">Where the settings, the server list and the saved worlds are kept.</param>
    /// <param name="automated">Ignore the keyboard and mouse, for screenshots taken without a player.</param>
    public App
    (
        Glfw glfw,
        WindowHandle* window,
        GraphicsContext graphics,
        Renderer renderer,
        Input input,
        InterfaceArt art,
        ItemSprites sprites,
        string shaders,
        string dataDirectory,
        bool automated
    )
    {
        _glfw = glfw;
        _window = window;
        _graphics = graphics;
        _renderer = renderer;
        _input = input;
        _art = art;
        _sprites = sprites;
        _shaders = shaders;
        _automated = automated;
        _gui = new GuiContext(art.Atlas, art.Style);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        _serversPath = Path.Combine(dataDirectory, "servers.json");
        _store = new WorldStore(Path.Combine(dataDirectory, "saves"));
        _settings = GameSettings.Load(_settingsPath);
        if (int.TryParse(Environment.GetEnvironmentVariable("MYT_RENDER_DISTANCE"), out int render))
            _settings.RenderDistance = render;
        if (int.TryParse(Environment.GetEnvironmentVariable("MYT_SIMULATION_DISTANCE"), out int simulation))
            _settings.SimulationDistance = simulation;
        _settings.Clamp();
        _servers = ServerList.Load(_serversPath);
        _worlds = _store.List();

        string? folder = Environment.GetEnvironmentVariable("MYT_WORLD");
        if (folder != null)
        {
            Play(_worlds.Find(w => w.Folder == folder) ?? throw new ArgumentException($"MYT_WORLD={folder} names no saved world in {_store.Directory}"));
            return;
        }

        Screen start = StartScreen(Environment.GetEnvironmentVariable("MYT_MENU"), automated);
        if (start is Screen.Playing or Screen.Paused or Screen.Inventory)
        {
            int seed = int.TryParse(Environment.GetEnvironmentVariable("MYT_SEED"), out int fixedSeed) ? fixedSeed : Random.Shared.Next();
            GameMode mode = Environment.GetEnvironmentVariable("MYT_GAMEMODE") == "survival" ? GameMode.Survival : GameMode.Creative;
            StartGame(seed, mode, null, null);
        }

        if (start == Screen.CreateWorld)
            OpenCreateWorld();
        else
            Open(start);
    }

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private readonly GraphicsContext _graphics;
    private readonly Renderer _renderer;
    private readonly Input _input;
    private readonly InterfaceArt _art;
    private readonly ItemSprites _sprites;
    private readonly string _shaders;
    private readonly bool _automated;
    private readonly GuiContext _gui;
    private readonly string _settingsPath;
    private readonly string _serversPath;
    private readonly WorldStore _store;
    private readonly GameSettings _settings;
    private readonly List<ServerInfo> _servers;
    private List<WorldInfo> _worlds;
    private Game? _game;
    private Screen _screen;
    // The screen the settings go back to: the title screen or the game menu.
    private Screen _settingsBack;
    private int _selectedWorld = -1;
    private int _selectedServer = -1;
    // What the open screen's text fields hold, and the world or server it edits (−1 for a new server).
    private string _name = "";
    private string _secondField = "";
    private GameMode _newMode = GameMode.Survival;
    private int _editing = -1;
    private Confirmation? _confirmation;
    private Notice? _notice;
    // What the loading screen says, what it waits to do, and whether it has been shown yet,
    // which it is before the work that would hold the frame up.
    private string _loadingText = "";
    private Action? _loading;
    private bool _loadingShown;

    /// <summary>Whether the player asked to quit the game.</summary>
    public bool Quitting { get; private set; }

    /// <summary>Profiling counters of the world being played; empty on the menus.</summary>
    public string ProfExtra => _game?.ProfExtra ?? ""; // PROF

    // ---------------------------------------------------------------------------------------
    // Frames
    // ---------------------------------------------------------------------------------------

    /// <summary>A frame: the menus' input, the world, the interface, and drawing it all.</summary>
    /// <param name="time">Seconds on a steady clock.</param>
    /// <param name="observedFrameTime">Real frame-loop interval, before simulation time is capped.</param>
    public void Frame(double dt, double time, double? observedFrameTime = null)
    {
        if (_screen == Screen.Loading && _loadingShown && _loading is Action work)
        {
            _loading = null;
            work();
        }

        HandleEscape();
        HandleInventoryKey();
        bool dead = _game != null && _game.Player.Dead;
        if (dead && _screen == Screen.Inventory)
            CloseInventory();

        bool playing = _game != null && _screen == Screen.Playing;
        bool running = _game != null && _screen is Screen.Playing or Screen.Inventory;
        bool controlled = playing && !dead && !_automated;
        if (!_automated && _input.Captured != controlled)
            _input.Capture(controlled);

        _game?.Update(dt, paused: !running && !_automated, controlled, observedFrameTime);

        if (_screen == Screen.Playing && _game?.StationOpened == true)
            Open(Screen.Inventory);

        vec2 pixels = new(_graphics.Extent.Width, _graphics.Extent.Height);
        GuiInput input = _automated ? new GuiInput { Cursor = new vec2(-1f), Time = time } : _input.Gui(time);
        _gui.Begin(input, pixels, InterfaceScale(pixels));
        _game?.BuildHud(_gui, crosshair: playing && !dead);
        if (playing && dead)
            DeathScreen();
        Screen drawn = _screen;
        DrawScreen();
        _gui.End();

        long beginStart = Stopwatch.GetTimestamp(); // PROF
        bool begun = _graphics.BeginFrame(out Frame frame);
        Prof.Add("begin", Stopwatch.GetElapsedTime(beginStart).TotalMilliseconds); // PROF
        if (!begun)
            return;

        long drawStart = Stopwatch.GetTimestamp(); // PROF
        if (_game != null)
            _game.Draw(frame, _gui.DrawList.Vertices);
        else
            _renderer.DrawInterface(frame, _gui.DrawList.Vertices);
        Prof.Add("draw", Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds); // PROF
        long endStart = Stopwatch.GetTimestamp(); // PROF
        _graphics.EndFrame(frame);
        Prof.Add("present", Stopwatch.GetElapsedTime(endStart).TotalMilliseconds); // PROF
        _loadingShown = drawn == Screen.Loading && _screen == Screen.Loading;
    }

    /// <summary>
    /// Pixels per interface unit: the setting's, but never so many that the screen is less
    /// than <see cref="ScaleMinimum"/> units; automatically, the most that leaves it
    /// <see cref="AutoScaleMinimum"/>.
    /// </summary>
    private int InterfaceScale(vec2 pixels)
    {
        vec2 minimum = _screen == Screen.Inventory && _game?.Creative == true ? new vec2(406, ScaleMinimum.y) : ScaleMinimum;
        int largest = GuiContext.FitScale(pixels, minimum);
        return _settings.GuiScale == GameSettings.AutoGuiScale
            ? GuiContext.FitScale(pixels, AutoScaleMinimum, largest)
            : Math.Min(_settings.GuiScale, largest);
    }

    /// <summary>ESC pauses the game and resumes it, and backs out of every menu but the title screen.</summary>
    private void HandleEscape()
    {
        if (_automated || !_input.Pressed(Keys.Escape))
            return;

        if (_screen == Screen.Settings && _bindingAction != null)
        {
            _bindingAction = null;
            return;
        }
        switch (_screen)
        {
            case Screen.Playing:
                Open(Screen.Paused);
                break;
            case Screen.Paused:
                Resume();
                break;
            case Screen.Inventory:
                CloseInventory();
                break;
            case Screen.Settings:
                CloseSettings();
                break;
            case Screen.Worlds or Screen.Servers:
                Open(Screen.Title);
                break;
            case Screen.CreateWorld or Screen.RenameWorld:
                OpenWorlds();
                break;
            case Screen.EditServer:
                Open(Screen.Servers);
                break;
            case Screen.Confirm:
                Open(_confirmation!.Back);
                break;
            case Screen.Message:
                Open(_notice!.Back);
                break;
        }
    }

    /// <summary>E opens the inventory over the game being played, and closes it again.</summary>
    private void HandleInventoryKey()
    {
        if (_automated || _game == null || !_input.Pressed(_settings.Binding(GameAction.Inventory)))
            return;

        if (_screen == Screen.Playing && !_game.Player.Dead)
            Open(Screen.Inventory);
        else if (_screen == Screen.Inventory)
            CloseInventory();
    }

    private void DrawScreen()
    {
        switch (_screen)
        {
            case Screen.Title:
                TitleScreen();
                break;
            case Screen.Worlds:
                WorldsScreen();
                break;
            case Screen.CreateWorld:
                CreateWorldScreen();
                break;
            case Screen.RenameWorld:
                RenameWorldScreen();
                break;
            case Screen.Servers:
                ServersScreen();
                break;
            case Screen.EditServer:
                EditServerScreen();
                break;
            case Screen.Settings:
                SettingsScreen();
                break;
            case Screen.Confirm:
                ConfirmScreen();
                break;
            case Screen.Message:
                MessageScreen();
                break;
            case Screen.Loading:
                LoadingScreen();
                break;
            case Screen.Paused:
                PauseScreen();
                break;
            case Screen.Inventory:
                _game!.BuildInventory(_gui);
                break;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Going between screens and worlds
    // ---------------------------------------------------------------------------------------

    private void Open(Screen screen)
    {
        _gui.ClearFocus();
        _screen = screen;
    }

    private void OpenWorlds()
    {
        _worlds = _store.List();
        _selectedWorld = Math.Min(_selectedWorld, _worlds.Count - 1);
        Open(Screen.Worlds);
    }

    private void OpenCreateWorld()
    {
        _name = "New World";
        _secondField = "";
        _newMode = GameMode.Survival;
        Open(Screen.CreateWorld);
        _gui.Focus("name");
    }

    private void OpenSettings(Screen back)
    {
        _settingsBack = back;
        _settingsTab = 0;
        _bindingAction = null;
        Open(Screen.Settings);
    }

    private void CloseSettings()
    {
        _settings.Save(_settingsPath);
        Open(_settingsBack);
    }

    private void Ask(string question, string detail, string action, Action yes, Screen back)
    {
        _confirmation = new Confirmation(question, detail, action, yes, back);
        Open(Screen.Confirm);
    }

    private void Tell(string title, string text, Screen back)
    {
        _notice = new Notice(title, text, back);
        Open(Screen.Message);
    }

    /// <summary>Shows a loading screen, and once it has been drawn, does <paramref name="work"/>, which opens the next screen.</summary>
    private void ShowLoading(string text, Action work)
    {
        _loadingText = text;
        _loading = work;
        _loadingShown = false;
        Open(Screen.Loading);
    }

    /// <summary>Plays a saved world, as it was saved, or new if it never was.</summary>
    private void Play(WorldInfo world)
    {
        ShowLoading("Loading world", () =>
        {
            string path = _store.PathOf(world, WorldStore.DataFile);
            WorldSnapshot? saved;
            try
            {
                saved = File.Exists(path) ? WorldFile.Read(path) : null;
            }
            catch (Exception failure) when (failure is InvalidDataException or IOException)
            {
                Tell("Could not load the world", failure.Message, Screen.Worlds);
                return;
            }

            StartGame(world.Seed, world.Mode, saved, new SaveTarget(_store, world));
            Open(Screen.Playing);
        });
    }

    private void StartGame(int seed, GameMode mode, WorldSnapshot? saved, SaveTarget? target)
    {
        _game = new Game(_graphics, _renderer, _input, _art, _sprites, _settings, _shaders, seed, mode, saved, target, _automated);
        _game.AwaitRelease();
        Console.WriteLine($"World seed {seed}, {mode}");
    }

    private void Resume()
    {
        _game!.AwaitRelease();
        Open(Screen.Playing);
    }

    /// <summary>Closes the inventory, putting back what the pointer carries, and goes back to the game.</summary>
    private void CloseInventory()
    {
        _game!.CloseInventory();
        _game.AwaitRelease();
        Open(Screen.Playing);
    }

    /// <summary>Saves the world being played, leaves it and goes back to the title screen; if it cannot be saved, says so and stays.</summary>
    private void QuitToTitle()
    {
        Game game = _game!;
        ShowLoading(game.Saved ? "Saving world" : "Leaving world", () =>
        {
            try
            {
                game.Save();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Tell("Could not save the world", failure.Message, Screen.Paused);
                return;
            }

            game.Dispose();
            _game = null;
            _renderer.Clear();
            Open(Screen.Title);
        });
    }

    // ---------------------------------------------------------------------------------------
    // Screens
    // ---------------------------------------------------------------------------------------

    /// <summary>A row of buttons <see cref="ButtonWidth"/> wide, centered, its top <paramref name="y"/> units down.</summary>
    private GuiRect CenteredButton(float y, float width = ButtonWidth)
    {
        return new GuiRect(MathF.Floor(_gui.Screen.Width / 2f - width / 2f), MathF.Floor(y), width, ButtonHeight);
    }

    private void Heading(string text, float y)
    {
        _gui.Label(new GuiRect(0f, y, _gui.Screen.Width, _gui.Style.TextSize), text, GuiColor.White);
    }

    /// <summary>
    /// A menu's backdrop: dark dirt, or the world dimmed while one is being played. The veils
    /// are more opaque than Minecraft's: the sRGB framebuffer blends in linear light, where
    /// the same opacity dims far less.
    /// </summary>
    private void Backdrop()
    {
        if (_game != null)
            _gui.Gradient(_gui.Screen, GuiColor.FromRgba(0x101010E8), GuiColor.FromRgba(0x101010F0));
        else
            _gui.Tile(_gui.Screen, _art.Dirt, DirtTile, MenuDirt);
    }

    /// <summary>A list screen's backdrop: dirt, darker between <paramref name="top"/> and <paramref name="bottom"/>, where the list is, with shadows at its edges.</summary>
    private void ListBackdrop(float top, float bottom)
    {
        GuiRect screen = _gui.Screen;
        GuiRect list = GuiRect.FromEdges(0f, top, screen.Width, bottom);
        _gui.Tile(screen, _art.Dirt, DirtTile, MenuDirt);
        _gui.Tile(list, _art.Dirt, DirtTile, ListDirt);
        _gui.Gradient(list with { Height = 4f }, GuiColor.Black, GuiColor.Transparent);
        _gui.Gradient(list with { Y = bottom - 4f, Height = 4f }, GuiColor.Transparent, GuiColor.Black);
    }

    private void TitleScreen()
    {
        GuiRect screen = _gui.Screen;
        _gui.Tile(screen, _art.Dirt, DirtTile, TitleDirt);

        // The title at a whole number of screen pixels per pixel of its image, so it stays sharp.
        float fit = (screen.Width - 20f) * _gui.Scale / _art.Title.Width;
        float pixels = MathF.Max(MathF.Floor(MathF.Min(TitleScale * _gui.Scale, fit)), 1f);
        vec2 size = _art.Title.Size * (pixels / _gui.Scale);
        _gui.Image(new GuiRect((screen.Width - size.x) / 2f, 30f, size.x, size.y), _art.Title);

        GuiRect first = CenteredButton(screen.Height / 4f + 48f);
        if (_gui.Button(first, "Singleplayer"))
            OpenWorlds();
        if (_gui.Button(first.Offset(0f, 24f), "Multiplayer", enabled: false))
            Open(Screen.Servers);

        GuiRect last = first.Offset(0f, 60f);
        if (_gui.Button(last.Column(0, 2, 4f), "Settings"))
            OpenSettings(Screen.Title);
        if (_gui.Button(last.Column(1, 2, 4f), "Quit Game"))
            Quitting = true;

        _gui.Text(new vec2(2f, screen.Height - 10f), "MYT Voxels", GuiColor.White);
    }

    private void WorldsScreen()
    {
        GuiRect screen = _gui.Screen;
        float bottom = screen.Height - 64f;
        ListBackdrop(32f, bottom);
        Heading("Select World", 16f);

        bool play = false;
        GuiRect area = GuiRect.FromEdges(MathF.Floor(screen.Width / 2f - 140f), 36f, MathF.Floor(screen.Width / 2f + 140f), bottom - 4f);
        GuiList list = _gui.BeginList("worlds", area, _worlds.Count, RowHeight);
        for (int i = list.First; i < list.End; i++)
        {
            GuiRect row = list.Row(i);
            GuiClick click = _gui.ListRow(list, i, i == _selectedWorld);
            if (click != GuiClick.None)
                _selectedWorld = i;
            play |= click == GuiClick.Double;

            WorldInfo world = _worlds[i];
            _gui.Image(new GuiRect(row.X + 2f, row.Y + 2f, 32f, 32f), _art.Icon(BlockType.Grass));
            _gui.Text(new vec2(row.X + 38f, row.Y + 3f), world.Name, GuiColor.White);
            _gui.Text(new vec2(row.X + 38f, row.Y + 14f), $"{world.Folder} ({world.LastPlayed:yyyy-MM-dd HH:mm})", DarkGray);
            _gui.Text(new vec2(row.X + 38f, row.Y + 25f), $"{world.Mode} Mode, seed {world.Seed}", DarkGray);
        }

        _gui.EndList();
        if (_worlds.Count == 0)
            _gui.Label(area with { Height = 40f }, "No worlds yet", DarkGray);

        bool chosen = _selectedWorld >= 0 && _selectedWorld < _worlds.Count;
        GuiRect upper = CenteredButton(screen.Height - 52f, 308f);
        GuiRect lower = upper.Offset(0f, 24f);
        play |= _gui.Button(upper.Column(0, 2, 8f), "Play Selected World", chosen) || (chosen && _gui.KeyPressed(GuiKeys.Enter));
        if (play && chosen)
            Play(_worlds[_selectedWorld]);

        if (_gui.Button(upper.Column(1, 2, 8f), "Create New World"))
            OpenCreateWorld();

        if (_gui.Button(lower.Column(0, 3, 4f), "Rename", chosen))
        {
            _editing = _selectedWorld;
            _name = _worlds[_editing].Name;
            Open(Screen.RenameWorld);
            _gui.Focus("name");
        }

        if (_gui.Button(lower.Column(1, 3, 4f), "Delete", chosen))
        {
            WorldInfo world = _worlds[_selectedWorld];
            Ask("Are you sure you want to delete this world?", $"'{world.Name}' will be lost forever!", "Delete", () =>
            {
                _store.Delete(world);
                _selectedWorld = -1;
                OpenWorlds();
            }, Screen.Worlds);
        }

        if (_gui.Button(lower.Column(2, 3, 4f), "Cancel"))
            Open(Screen.Title);
    }

    private void CreateWorldScreen()
    {
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading("Create New World", 20f);

        float left = MathF.Floor(screen.Width / 2f - 100f);
        _gui.Text(new vec2(left, 47f), "World Name", Gray);
        _gui.TextField("name", new GuiRect(left, 60f, 200f, 20f), ref _name);
        _gui.Text(new vec2(left, 85f), $"Will be saved in: {_store.FreeFolder(_name)}", Gray);
        _gui.Text(new vec2(left, 107f), "Seed for the World Generator", Gray);
        _gui.TextField("seed", new GuiRect(left, 120f, 200f, 20f), ref _secondField);
        _gui.Text(new vec2(left, 145f), "Leave blank for a random seed", Gray);

        if (_gui.Button(new GuiRect(left, 160f, 200f, 20f), $"Game Mode: {_newMode}"))
            _newMode = _newMode == GameMode.Survival ? GameMode.Creative : GameMode.Survival;
        string about = _newMode == GameMode.Survival
            ? "Start with nothing, mine with tools, craft, and get hurt"
            : "Every item without limit, flying, and blocks broken in one hit";
        _gui.Label(new GuiRect(0f, 185f, screen.Width, 8f), about, Gray);

        bool valid = _name.Trim().Length > 0;
        GuiRect buttons = CenteredButton(screen.Height - 28f, 310f);
        if ((_gui.Button(buttons.Column(0, 2, 10f), "Create New World", valid) || _gui.KeyPressed(GuiKeys.Enter)) && valid)
        {
            WorldInfo world = _store.Create(_name.Trim(), WorldStore.SeedFrom(_secondField), _newMode);
            _selectedWorld = 0;
            Play(world);
        }

        if (_gui.Button(buttons.Column(1, 2, 10f), "Cancel"))
            OpenWorlds();
    }

    private void RenameWorldScreen()
    {
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading("Rename World", 20f);

        float left = MathF.Floor(screen.Width / 2f - 100f);
        _gui.Text(new vec2(left, 47f), "World Name", Gray);
        _gui.TextField("name", new GuiRect(left, 60f, 200f, 20f), ref _name);

        bool valid = _name.Trim().Length > 0;
        GuiRect save = CenteredButton(screen.Height / 4f + 108f);
        if ((_gui.Button(save, "Save", valid) || _gui.KeyPressed(GuiKeys.Enter)) && valid)
        {
            _store.Rename(_worlds[_editing], _name.Trim());
            OpenWorlds();
        }

        if (_gui.Button(save.Offset(0f, 24f), "Cancel"))
            OpenWorlds();
    }

    private void ServersScreen()
    {
        GuiRect screen = _gui.Screen;
        float bottom = screen.Height - 64f;
        ListBackdrop(32f, bottom);
        Heading("Play Multiplayer", 16f);

        GuiRect area = GuiRect.FromEdges(MathF.Floor(screen.Width / 2f - 140f), 36f, MathF.Floor(screen.Width / 2f + 140f), bottom - 4f);
        GuiList list = _gui.BeginList("servers", area, _servers.Count, RowHeight);
        for (int i = list.First; i < list.End; i++)
        {
            GuiRect row = list.Row(i);
            if (_gui.ListRow(list, i, i == _selectedServer) != GuiClick.None)
                _selectedServer = i;

            ServerInfo server = _servers[i];
            _gui.Image(new GuiRect(row.X + 2f, row.Y + 2f, 32f, 32f), _art.Icon(BlockType.Stone));
            _gui.Text(new vec2(row.X + 38f, row.Y + 3f), server.Name, GuiColor.White);
            _gui.Text(new vec2(row.X + 38f, row.Y + 14f), server.Address, DarkGray);
            _gui.Text(new vec2(row.X + 38f, row.Y + 25f), "Multiplayer is not available yet", DarkGray);
        }

        _gui.EndList();
        if (_servers.Count == 0)
            _gui.Label(area with { Height = 40f }, "No servers yet", DarkGray);

        bool chosen = _selectedServer >= 0 && _selectedServer < _servers.Count;
        GuiRect upper = CenteredButton(screen.Height - 52f, 308f);
        GuiRect lower = upper.Offset(0f, 24f);
        _gui.Button(upper.Column(0, 2, 8f), "Join Server", enabled: false);
        if (_gui.Button(upper.Column(1, 2, 8f), "Add Server"))
            EditServer(-1);
        if (_gui.Button(lower.Column(0, 3, 4f), "Edit", chosen))
            EditServer(_selectedServer);

        if (_gui.Button(lower.Column(1, 3, 4f), "Delete", chosen))
        {
            ServerInfo server = _servers[_selectedServer];
            Ask("Are you sure you want to remove this server?", $"'{server.Name}' will be lost forever!", "Delete", () =>
            {
                _servers.Remove(server);
                ServerList.Save(_serversPath, _servers);
                _selectedServer = -1;
                Open(Screen.Servers);
            }, Screen.Servers);
        }

        if (_gui.Button(lower.Column(2, 3, 4f), "Cancel"))
            Open(Screen.Title);
    }

    /// <summary>Opens the server editor on a server of the list, or on a new one for −1.</summary>
    private void EditServer(int index)
    {
        _editing = index;
        _name = index >= 0 ? _servers[index].Name : "Server";
        _secondField = index >= 0 ? _servers[index].Address : "";
        Open(Screen.EditServer);
        _gui.Focus("name");
    }

    private void EditServerScreen()
    {
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading("Edit Server Info", 17f);

        float left = MathF.Floor(screen.Width / 2f - 100f);
        _gui.Text(new vec2(left, 53f), "Server Name", Gray);
        _gui.TextField("name", new GuiRect(left, 66f, 200f, 20f), ref _name);
        _gui.Text(new vec2(left, 94f), "Server Address", Gray);
        _gui.TextField("address", new GuiRect(left, 106f, 200f, 20f), ref _secondField, 128);

        bool valid = _name.Trim().Length > 0 && _secondField.Trim().Length > 0;
        GuiRect done = CenteredButton(screen.Height / 4f + 114f);
        if ((_gui.Button(done, "Done", valid) || _gui.KeyPressed(GuiKeys.Enter)) && valid)
        {
            ServerInfo server = new(_name.Trim(), _secondField.Trim());
            if (_editing >= 0)
            {
                _servers[_editing] = server;
            }
            else
            {
                _servers.Add(server);
                _selectedServer = _servers.Count - 1;
            }

            ServerList.Save(_serversPath, _servers);
            Open(Screen.Servers);
        }

        if (_gui.Button(done.Offset(0f, 24f), "Cancel"))
            Open(Screen.Servers);
    }

    private int _settingsTab;
    private int _controlsPage;
    private GameAction? _bindingAction;

    private void SettingsScreen()
    {
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading("Settings", 15f);

        GuiRect tabs = CenteredButton(32f, 310f);
        string[] names = ["General", "Video", "Controls"];
        for (int i = 0; i < names.Length; i++)
            if (_gui.Button(tabs.Column(i, 3, 3f), names[i])) { _settingsTab = i; _bindingAction = null; }
        GuiRect row = CenteredButton(58f, 310f);
        if (_settingsTab == 1) VideoSettings(row);
        else if (_settingsTab == 2) ControlSettings(row);
        else
        {
            int render = _settings.RenderDistance;
            if (_gui.Slider("render", row, ref render, ChunkStreamer.MinRenderDistance, ChunkStreamer.MaxRenderDistance, $"LOD Distance: {render * 16} blocks (x2 each level)"))
                _settings.RenderDistance = render;

            int simulation = _settings.SimulationDistance;
            if (_gui.Slider("simulation", row.Offset(0f, 24f), ref simulation, World.MinSimulationDistance, World.MaxSimulationDistance, $"Simulation Distance: {simulation} chunks"))
                _settings.SimulationDistance = simulation;

            int fieldOfView = _settings.FieldOfView;
            if (_gui.Slider("fov", row.Offset(0f, 48f), ref fieldOfView, GameSettings.MinFieldOfView, GameSettings.MaxFieldOfView, $"Field of View: {fieldOfView}"))
                _settings.FieldOfView = fieldOfView;

            int sensitivity = _settings.MouseSensitivity;
            if (_gui.Slider("sensitivity", row.Offset(0f, 72f), ref sensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity, $"Mouse Sensitivity: {sensitivity}%"))
                _settings.MouseSensitivity = sensitivity;

            // Auto, then each whole scale that leaves the screen big enough, then Auto again.
            int largest = GuiContext.FitScale(new vec2(_graphics.Extent.Width, _graphics.Extent.Height), ScaleMinimum);
            string scale = _settings.GuiScale == GameSettings.AutoGuiScale ? "Auto" : _settings.GuiScale.ToString();
            if (_gui.Button(row.Offset(0f, 96f), $"GUI Scale: {scale}"))
                _settings.GuiScale = _settings.GuiScale >= largest ? GameSettings.AutoGuiScale : _settings.GuiScale + 1;

            // Objects move only on ground that is loaded, which stops a column past the render distance.
            if (_settings.SimulationDistance > _settings.RenderDistance + 1)
            {
                string limit = $"Objects move only on loaded ground: as far as {_settings.RenderDistance + 1} chunks";
                _gui.Label(new GuiRect(0f, row.Y + 124f, screen.Width, 8f), limit, Gray);
            }

        }
        if (_gui.Button(CenteredButton(screen.Height - 27f), "Done"))
            CloseSettings();
    }

    private void VideoSettings(GuiRect row)
    {
        bool Toggle(int index, string label, bool value, int column = 0) =>
            _gui.Button(row.Offset(0, index * 22).Column(column, 2, 3), $"{label}: {(value ? "On" : "Off")}") ? !value : value;
        _settings.TransparentLeaves = Toggle(0, "Transparent leaves", _settings.TransparentLeaves);
        _settings.AmbientOcclusion = Toggle(1, "Ambient occlusion", _settings.AmbientOcclusion);
        _settings.Sun = Toggle(2, "Sun", _settings.Sun);
        _settings.SpecularLighting = Toggle(3, "Specular", _settings.SpecularLighting);
        _settings.DynamicShadows = Toggle(4, "Dynamic shadows", _settings.DynamicShadows);
        _settings.BlockLighting = Toggle(5, "Block lighting", _settings.BlockLighting);
        _settings.Particles = Toggle(1, "Particles", _settings.Particles, 1);
        int brightness = _settings.AmbientBrightness;
        if (_gui.Slider("ambient", row.Column(1, 2, 3), ref brightness, 0, 100, $"Ambient: {brightness}%"))
            _settings.AmbientBrightness = brightness;
    }

    private void ControlSettings(GuiRect row)
    {
        if (_bindingAction is GameAction waiting)
        {
            int? binding = _input.PressedKeys.Where(k => Controls.Valid((int)k)).Select(k => (int?)k).FirstOrDefault();
            for (int b = 0; b < 3; b++)
                if (_input.ButtonPressed((MouseButton)b)) binding = -b - 1;
            if (binding is int key) { _settings.Bind(waiting, key); _bindingAction = null; }
        }
        GameAction[] actions = Enum.GetValues<GameAction>();
        const int perPage = 10;
        for (int i = 0; i < perPage; i++)
        {
            int index = _controlsPage * perPage + i;
            if (index >= actions.Length) break;
            GameAction action = actions[index];
            string key = _bindingAction == action ? "Press key..." : Controls.Name(_settings.Binding(action));
            if (_gui.Button(row.Offset(0, (i / 2) * 23).Column(i % 2, 2, 3), $"{action}: {key}"))
                _bindingAction = action;
        }
        GuiRect navigation = row.Offset(0, 118);
        if (_gui.Button(navigation.Column(0, 3, 3), "Previous")) { _controlsPage = Math.Max(0, _controlsPage - 1); _bindingAction = null; }
        if (_gui.Button(navigation.Column(1, 3, 3), "Reset")) { _settings.Keybinds = Controls.Defaults(); _bindingAction = null; }
        if (_gui.Button(navigation.Column(2, 3, 3), "Next")) { _controlsPage = Math.Min((actions.Length - 1) / perPage, _controlsPage + 1); _bindingAction = null; }
        _gui.Label(row.Offset(0, 143), "F3: creative diagnostics. ESC: back / cancel binding.", Gray);
    }

    private void ConfirmScreen()
    {
        Confirmation confirmation = _confirmation!;
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading(confirmation.Question, 70f);
        Heading(confirmation.Detail, 90f);

        GuiRect buttons = CenteredButton(screen.Height / 6f + 96f, 310f);
        if (_gui.Button(buttons.Column(0, 2, 10f), confirmation.Action))
            confirmation.Yes();
        if (_gui.Button(buttons.Column(1, 2, 10f), "Cancel"))
            Open(confirmation.Back);
    }

    private void MessageScreen()
    {
        Notice notice = _notice!;
        GuiRect screen = _gui.Screen;
        Backdrop();
        float y = MathF.Floor(screen.Height / 2f - 50f);
        Heading(notice.Title, y);
        foreach (string line in _gui.Wrap(notice.Text, Math.Min(screen.Width - 40f, 400f)))
        {
            y += 12f;
            _gui.Label(new GuiRect(0f, y, screen.Width, 8f), line, Gray);
        }

        if (_gui.Button(CenteredButton(Math.Max(y + 24f, screen.Height / 2f + 30f)), "Back") || _gui.KeyPressed(GuiKeys.Enter))
            Open(notice.Back);
    }

    private void LoadingScreen()
    {
        Backdrop();
        _gui.Label(new GuiRect(0f, MathF.Floor(_gui.Screen.Height / 2f - 4f), _gui.Screen.Width, 8f), _loadingText, GuiColor.White);
    }

    private void PauseScreen()
    {
        GuiRect screen = _gui.Screen;
        Backdrop();
        Heading("Game Menu", 40f);

        GuiRect back = CenteredButton(screen.Height / 4f + 8f);
        if (_gui.Button(back, "Back to Game"))
            Resume();
        if (_gui.Button(back.Offset(0f, 24f), "Settings"))
            OpenSettings(Screen.Paused);
        if (_gui.Button(back.Offset(0f, 60f), _game!.Saved ? "Save and Quit to Title" : "Quit to Title"))
            QuitToTitle();
    }

    /// <summary>Over the world while the player is dead and no menu is open: respawn, or leave for the title screen.</summary>
    private void DeathScreen()
    {
        GuiRect screen = _gui.Screen;
        _gui.Gradient(screen, GuiColor.FromRgba(0x500000A0), GuiColor.FromRgba(0x803030D8));
        _gui.Label(new GuiRect(0f, 60f, screen.Width, 16f), "You Died!", GuiColor.White, size: 16f);

        GuiRect respawn = CenteredButton(screen.Height / 4f + 72f);
        if (_gui.Button(respawn, "Respawn") || _gui.KeyPressed(GuiKeys.Enter))
            _game!.Respawn();
        if (_gui.Button(respawn.Offset(0f, 24f), "Title Screen"))
            QuitToTitle();
    }

    /// <summary>Saves and leaves the world being played, if any; with the window closing, a save that fails can only be reported.</summary>
    public void Dispose()
    {
        if (_game == null)
            return;

        try
        {
            _game.Save();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save the world on closing: {failure.Message}");
        }

        _game.Dispose();
        _game = null;
    }
}
