using ArcadeLab;
using System.Globalization;

try
{
    var options = new Options(args);
    if (args.Length == 0 || options.Has("help")) { Options.Help(); return 0; }
    if(options.Has("list-devices"))
    {
        using var devices=new NeuralGraph.VulkanDevice();
        foreach(string device in devices.AvailableDevices)Console.WriteLine(device);
        return 0;
    }
    if (options.Has("train") == options.Has("play")) throw new ArgumentException("Choose exactly one running mode: --train or --play.");
    MazeCommand.Run(options); return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Error: {e.Message}");
    if (Environment.GetEnvironmentVariable("ARCADE_DEBUG") == "1") Console.Error.WriteLine(e);
    return 1;
}

public sealed class Options
{
    public string Command => Has("train") ? "--train" : Has("play") ? "--play" : "help";
    private readonly Dictionary<string, string> values = [];
    public Options(string[] args)
    {
        string[] flags = ["train", "play", "help", "headless", "mute", "no-save", "new", "list-devices", "bot", "adaptive", "no-adaptive"];
        string[] known = ["thinking-bias", "brain", "backend", "seed", "rate", "reward-rate", "device", "frames", "layers", "width", "reward-hidden", "checkpoint", "autosave-seconds", "game-speed", "fps", "propagation-steps", "activity-step", "weight-step", "weight-limit"];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help") { values["help"] = "true"; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Expected --train or --play; invalid option {args[i]}");
            string key = args[i][2..];
            if (flags.Contains(key)) values.Add(key, "true");
            else if (known.Contains(key) && i + 1 < args.Length) values.Add(key, args[++i]);
            else throw new ArgumentException($"Unknown option or missing value: --{key}");
        }
    }
    public bool Has(string key) => values.ContainsKey(key);
    public string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    public int Int(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    public float Float(string key, float fallback) => float.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    public static void Help() => Console.WriteLine("""
        Neural Maze — C# / GLFW / Vulkan
        Default: sparse recurrent editor brain, CPU backend, artifacts/modular-maze checkpoint.
        --train    Paired-error inference and local plasticity; teach reward outputs with ideal labels.
        --play     Freeze reward weights; KEEP main local plasticity and both networks
                   propagating in real time. No ideal game rewards are supplied.
        Examples:
          dotnet run --project src/ArcadeLab -c Release -- --train
          dotnet run --project src/ArcadeLab -c Release -- --play
          dotnet run --project src/ArcadeLab -c Release -- --train --bot --headless --frames 20000
        --brain PATH|latest|modular  Use an editor bake (or newest src/BrainEditor/artifacts/editor/baked-*).
                            Saves/resumes separately in artifacts/baked-maze; --new reloads the bake.
        --brain modular  Build the sparse recurrent editor brain; checkpoint in artifacts/modular-maze.
        --thinking-bias 0.1  Implicit learning bias for every baked cortex; 0 disables it.
        --adaptive Enable online neural action learning on an existing checkpoint (default for new runs).
        --no-adaptive Use the original threshold motor policy.
        --bot      Roaming teacher: explore, collect and fight while clamping motor neurons.
        --list-devices  List Vulkan device indices and types, then exit.
        Options: --checkpoint artifacts/predictive-maze --new --backend vulkan|cpu|auto --device INDEX
                 --layers 8 --width 512 --reward-hidden 128 --seed 42
                 --rate 0.00001 --reward-rate 0.00001 --game-speed 0.5 --fps 60 --propagation-steps 8 --activity-step 0.04 --weight-step 0.02 --weight-limit 8
                 --frames COUNT --headless --bot --mute --no-save --autosave-seconds 30
        Training resumes automatically. --new starts fresh training. Play requires a trained checkpoint.
        Controls: Tab AI/manual (bot/manual with --bot); W/S forward/back; A/D turn; Q/E strafe; F fire;
                  Hold keys together for simultaneous controls; X reload; Space pause; R reset; Escape save and exit.
        Left: large top-down observer map and current first-person RGB line. Right: normal/error pairs and the statistics-only SUM(E^2) bar.
        Bars: green learned pleasure, red learned pain, amber primitive pain, blue primitive pleasure.
        """);
}
