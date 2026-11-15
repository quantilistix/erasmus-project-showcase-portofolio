using System.Diagnostics;

using Silk.NET.GLFW;

namespace VoxelGame;


/// <summary>
/// A voxel sandbox on the MYT physics engine: mine and build on a fixed map and on rigid
/// voxel objects, and watch structures crack and break under their own weight and under impacts.
/// It opens on a title screen (<see cref="App"/>), from which saved worlds are played; the map
/// is generated wherever the player goes (<see cref="ChunkStreamer"/>), as far around as the
/// render distance set in the settings, and objects move as far around as the simulation
/// distance set there (<see cref="World.SimulationDistance"/>). Settings and saved worlds are
/// kept in the data directory: MYT_DATA, or MYTVoxels in the user's local application data.
/// Run with <c>dotnet run --project src/VoxelGame -c Release</c>. Environment variables:
/// MYT_VALIDATION=1 turns on the Vulkan validation layer; MYT_SCREENSHOT=file.ppm with
/// MYT_FRAMES=n renders n frames without taking the mouse, saves the last one and quits, in a
/// world that is not saved unless MYT_MENU names a screen to show instead;
/// MYT_CAMERA="x,y,z,yaw,pitch" places the camera; MYT_STRESS=1 starts in the stress view;
/// MYT_LOG=1 prints every impact and every block that breaks, and why; MYT_SEED=n generates
/// that unsaved world from that seed rather than a random one, and MYT_GAMEMODE=survival plays
/// it in survival rather than creative; MYT_HOLD=name puts an item in the player's hand, as
/// MYT_HOLD=Diamond_Pickaxe, or none for the bare arm, and MYT_SWING_PHASE=f holds it still that
/// far (0 to 1) through its swing; MYT_RENDER_DISTANCE=n and
/// MYT_SIMULATION_DISTANCE=n set those distances, in chunks. MYT_PROF=1 reports frame,
/// physics and GPU render timings (the capture frame is excluded); MYT_PRESENT_MODE=immediate
/// requests uncapped presentation for benchmarks when supported, retaining FIFO otherwise;
/// MYT_SYNC_MESH=1 restores blocking meshing for comparisons; MYT_LIGHT_BUDGET=0
/// disables bounded diffusion and MYT_DAYLIGHT_DEFER=0 restores every-frame indirect daylight.
/// MYT_GPU_BLASTS=0 selects the CPU wavefront reference instead of the shared GPU acoustic field;
/// MYT_GPU_STRESS=0 selects the CPU object-stress worker; MYT_GPU_DAMAGE=0 evaluates health on the CPU;
/// MYT_GPU_IMPACTS=0 selects the CPU impact analysis; MYT_GPU_MESH=0 generates chunk and LOD
/// vertices on the CPU for comparisons.
/// MYT_CPU_REFERENCE=1 selects all five reference paths together (physics and lighting stay on the GPU).
/// MYT_FIXED_DT=seconds fixes simulation dt in screenshot runs; MYT_AUTOTNT_CELL=x,y,z
/// fixes the benchmark detonation cell for repeatable save-copy comparisons.
/// MYT_PARTICLE_GEOMETRY=0 uses instanced GPU particle expansion instead of geometry shaders;
/// MYT_INCREMENTAL_MESH=0 restores whole-chunk CPU reference meshing; with that set,
/// MYT_RENDER_MERGE=0 selects its unit-face mesher; MYT_MAXIMIZED=1
/// starts maximized for display-size profiling.
/// MYT_SPAWN_OBJECTS=n adds n airborne objects during generation; MYT_SPAWN_MODE=blocks
/// uses single stone blocks, or mixed (default) also includes 3×3×3 plank cubes;
/// MYT_SPAWN_ITEMS=n drops n items of every kind in turn before the spawn point.
/// MYT_AUTOPILOT=tnt with MYT_AUTOTNT=n detonates TNT at frame n for profiling;
/// MYT_AUTOTNT_COUNT=27 places a 3×3×3 cluster and ignites its first block.
/// </summary>
internal static unsafe class Program
{
    public static int Main()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string root = FindRepositoryRoot();
        string assets = Path.Combine(root, "assets");
        string data = Environment.GetEnvironmentVariable("MYT_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYTVoxels");

        Glfw glfw = Glfw.GetApi();
        if (!glfw.Init())
            throw new InvalidOperationException("GLFW failed to initialize");

        glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        glfw.WindowHint(WindowHintBool.Resizable, true);
        WindowHandle* window = glfw.CreateWindow(1280, 720, "MYT Voxels", null, null);
        if (window == null)
            throw new InvalidOperationException("Could not create a window");

        if (Environment.GetEnvironmentVariable("MYT_MAXIMIZED") == "1") glfw.MaximizeWindow(window);

        GraphicsContext graphics = new(glfw, window, Environment.GetEnvironmentVariable("MYT_VALIDATION") == "1");
        glfw.SetFramebufferSizeCallback(window, (_, _, _) => graphics.NotifyResized());
        Console.WriteLine($"Running {configuration} on {graphics.DeviceName}; presentation {graphics.PresentMode}; framebuffer {graphics.Extent.Width}x{graphics.Extent.Height}; saves and settings in {data}");

        ItemSprites sprites = new(Path.Combine(assets, "images"));
        InterfaceArt art = new(Path.Combine(assets, "images"), sprites);
        Renderer renderer = new(graphics, assets, art.Atlas, sprites);
        Input input = new(glfw, window);
        string? screenshot = Environment.GetEnvironmentVariable("MYT_SCREENSHOT");
        App app = new(glfw, window, graphics, renderer, input, art, sprites, Path.Combine(assets, "shaders"), data, automated: screenshot != null);

        int frames = int.TryParse(Environment.GetEnvironmentVariable("MYT_FRAMES"), out int count) ? count : 120;
        Stopwatch clock = Stopwatch.StartNew();
        double last = 0;
        for (int frame = 1; !glfw.WindowShouldClose(window) && !app.Quitting; frame++)
        {
            glfw.PollEvents();
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = now - last;
            double dt = Math.Min(elapsed, 0.1);
            if (screenshot != null && double.TryParse(Environment.GetEnvironmentVariable("MYT_FIXED_DT"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fixedDt)
                && fixedDt > 0 && fixedDt <= .1) dt = fixedDt;
            last = now;

            input.Update();
            if (screenshot != null && frame == frames)
                graphics.CapturePath = screenshot;

            Prof.ObserveInterval(frame, elapsed);
            Prof.BeginFrame(); // PROF
            app.Frame(dt, now, elapsed);
            if (Prof.On && !(screenshot != null && frame == frames))
                Prof.EndFrame(frame, app.ProfExtra);
            if (screenshot != null && frame == frames)
                break;
        }

        Prof.Summary(); // PROF

        app.Dispose();
        renderer.Dispose();
        graphics.Dispose();
        glfw.DestroyWindow(window);
        glfw.Terminate();
        return 0;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "shaders", "physics")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Run from inside the repository, which holds the assets");
    }
}
