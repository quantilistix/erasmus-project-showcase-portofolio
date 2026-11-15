using System.Diagnostics;

namespace ArcadeLab;

internal static class RealtimeCommand
{
    public static void Run(Options options)
    {
        string gameName = options.Get("game", "catch");
        if (!Enum.TryParse<GameKind>(gameName, true, out var game) || !Enum.IsDefined(game)) throw new ArgumentException("Realtime --game must be catch, dodge or rally.");
        string backend = options.Get("backend", "vulkan"), directory = options.Get("checkpoint", $"artifacts/realtime/{gameName.ToLowerInvariant()}");
        int device = options.Int("device", -1);
        var config = new RealtimeConfig
        {
            Layers = options.Int("layers", 8), Width = options.Int("width", 512),
            LearnedPainInputs = options.Int("pain-inputs", 16), PrimitivePainInputs = options.Int("primitive-pain-inputs", 16),
            PrimitiveRewardInputs = options.Int("primitive-reward-inputs", 16),
            RewardProbes = options.Int("probes", 128), RewardHidden = options.Int("reward-hidden", 32),
            CalibrationFrames = options.Int("calibration-frames", 2000), Seed = options.Int("seed", 42), Fps = options.Int("fps", 60),
            GameSpeed = options.Float("game-speed", .5f), PainHoldFrames = options.Int("pain-hold-frames", 12), ClockHz = options.Float("clock-hz", 1),
            Rate = options.Float("rate", .0025f), RewardRate = options.Float("reward-rate", .025f), PleasureGain = options.Float("pleasure-gain", .0625f), LearnedRewardWeight = options.Float("learned-reward-weight", .25f),
            LearnedPainScale = options.Float("learned-pain-scale", .25f), PrimitiveRewardWeight = options.Float("primitive-reward-weight", 1),
            PrimitiveRewardGain = options.Float("primitive-reward-gain", .25f)
        };
        if (options.Has("new") && options.Has("resume")) throw new ArgumentException("Choose --new or --resume, not both.");
        bool loadExisting = !options.Has("new") && (options.Has("resume") || File.Exists(Path.Combine(directory, "session.json")));
        if (options.Has("recalibrate-reward") && !loadExisting) throw new ArgumentException("Reward recalibration requires an existing checkpoint.");
        if (!loadExisting) config.Validate();
        Console.WriteLine(loadExisting ? $"Continuing saved realtime session: {directory}" : $"Creating {config.Layers} hidden layers × {config.Width} neurons, with adjacent + odd-to-odd connections…");
        using var session = loadExisting ? RealtimeSession.Load(directory, backend, device) : new RealtimeSession(game, config, backend, device);
        session.ConfigureGameSpeed(options.Has("game-speed") ? options.Float("game-speed", .5f) : null);
        session.ConfigureSignals(options.Has("pain-hold-frames") ? options.Int("pain-hold-frames", 12) : null,
            options.Has("clock-hz") ? options.Float("clock-hz", 1) : null);
        session.ConfigureRewardEffects(options.Has("learned-reward-weight") ? options.Float("learned-reward-weight", .25f) : null,
            options.Has("pleasure-gain") ? options.Float("pleasure-gain", .0625f) : null,
            options.Has("learned-pain-scale") ? options.Float("learned-pain-scale", .25f) : null,
            options.Has("primitive-reward-weight") ? options.Float("primitive-reward-weight", 1) : null,
            options.Has("primitive-reward-gain") ? options.Float("primitive-reward-gain", .25f) : null);
        if (session.MainInputsUpgraded)
            Console.WriteLine($"Added {session.AddedMainInputs} missing clock/primitive-reward inputs; existing weights/biases/plasticity retained, added input weights start at zero.");
        if (session.RewardEffectsUpgraded)
            Console.WriteLine("Migrated to proximity-only learned pleasure, weaker learned effects and an independent primitive catch reward; recalibrating the head.");
        if (session.RewardInputsUpgraded)
            Console.WriteLine("Upgraded legacy reward inputs with direct raw vision and executed controls; recalibrating the head while preserving main weights.");
        if (options.Has("recalibrate-reward"))
        {
            session.RecalibrateReward(options.Int("calibration-frames", 6000));
            Console.WriteLine($"Recalibrating ONLY the reward head for {session.CalibrationFramesRemaining} frames; preserving main weights and training progress.");
        }
        Console.WriteLine($"{session.MainBackend.DeviceName}; {session.MainModel.Topology.NodeCount:N0} logical units / {session.MainModel.Topology.ActivationCount:N0} nonnegative rate neurons; {session.MainModel.Topology.Edges.Length:N0} edges; reward head {session.RewardModel.Topology.InputCount}→{session.Config.RewardHidden}→2 ({session.Config.RewardProbes} hidden probes + {RewardInputs.VisualCount} raw pixels + 3 executed controls).");
        Console.WriteLine($"Game speed {session.Config.GameSpeed:F2}x; learning remains frame-by-frame at target {session.Config.Fps} FPS. All neuron channels use nonnegative dual-rail encoding.");
        Console.WriteLine($"Calibration: {session.Config.CalibrationFrames} frames. Then freeze reward weights, biases and plasticity; continue main learning every active frame.");
        Console.WriteLine($"Pain samples hold for {session.Config.PainHoldFrames} frames ({(float)session.Config.Fps / session.Config.PainHoldFrames:F2} Hz refresh at target pace); one {session.Config.ClockHz:F2} Hz sine clock input.");
        Console.WriteLine($"Noise ONLY in {session.Config.LearnedPainInputs} learned-pain and {session.Config.PrimitivePainInputs} primitive-pain input neurons. Raw senses and controls unchanged.");
        Console.WriteLine($"Learned reward weight {session.Config.LearnedRewardWeight:F3}, pleasure gain {session.Config.PleasureGain:F4}, learned pain scale {session.Config.LearnedPainScale:F3}; primitive catch weight {session.Config.PrimitiveRewardWeight:F2}, trace inputs {session.Config.PrimitiveRewardInputs} (deterministic).");
        int frames = options.Int("frames", options.Has("headless") ? checked(session.Config.CalibrationFrames + 300) : 0);
        if (frames < 0 || (options.Has("headless") && frames == 0)) throw new ArgumentException("Headless realtime needs a positive --frames count.");
        double autosaveSeconds = options.Float("autosave-seconds", 30);
        if (!double.IsFinite(autosaveSeconds) || autosaveSeconds < 0) throw new ArgumentException("--autosave-seconds must be finite and nonnegative.");
        var statistics = new FrameStatistics(); var timer = Stopwatch.StartNew(); double lastSave = 0; bool stopping = false;
        void Autosave()
        {
            if (options.Has("no-save") || autosaveSeconds == 0 || timer.Elapsed.TotalSeconds - lastSave < autosaveSeconds) return;
            session.Save(directory); lastSave = timer.Elapsed.TotalSeconds;
            Console.WriteLine($"Autosaved frame {session.Frames} to {directory}");
        }
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Volatile.Write(ref stopping, true); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (options.Has("headless"))
            {
                for (int frame = 0; frame < frames && !Volatile.Read(ref stopping); frame++)
                {
                    bool wasRealtime = session.IsRealtime; session.Step(); statistics.Add(session.FrameMilliseconds); Autosave();
                    if (wasRealtime != session.IsRealtime || (frame + 1) % 120 == 0 || frame == frames - 1) PrintStatus(session, statistics);
                }
            }
            else
            {
                using var viewer = new Viewer(); viewer.PlayRealtime(session, frames, options.Has("mute"), statistics, Autosave, () => Volatile.Read(ref stopping));
            }
        }
        finally { Console.CancelKeyPress -= cancel; }
        Console.WriteLine($"Completed {statistics.Count} learning frames in {timer.Elapsed.TotalSeconds:F2}s. {statistics.Summary}");
        if (session.CalibrationStatistics.Count > 0) Console.WriteLine($"Calibration ({session.CalibrationStatistics.Count} frames): {session.CalibrationStatistics.Summary}");
        if (session.RealtimeStatistics.Count > 0) Console.WriteLine($"Frozen-head realtime ({session.RealtimeStatistics.Count} frames): {session.RealtimeStatistics.Summary}");
        if (!options.Has("no-save")) { session.Save(directory); Console.WriteLine($"Saved main.ngb, reward.json and session.json to {directory}"); }
    }
    public static void PrintStatus(RealtimeSession s, FrameStatistics statistics) => Console.WriteLine(
        $"frame {s.Frames,6} {(s.IsRealtime ? "REALTIME reward FROZEN" : "CALIBRATING reward TRAINING")} P={s.Pleasure:F3} targetP={s.Game.RewardTarget.Pleasure:F3} pain={s.Pain:F3} primitivePain={s.PrimitivePain:F3} primitiveReward={s.PrimitiveReward:F3} catchPulse={s.PrimitiveRewardPulse:F0} R={s.LearningReward:F3} clock={s.ClockValue:F3} gain={s.Gain:F3} envReward={s.EpisodeReward:F3} score={s.Game.Score} mainLoss={s.Loss:F5} headLoss={s.RewardLoss:F5} mainUpdates={s.MainUpdates} headUpdates={s.RewardUpdates}; {statistics.Summary}");
}

public sealed class FrameStatistics
{
    private readonly double[] recent = new double[512];
    private double sum;
    public long Count { get; private set; }
    public double Mean => Count == 0 ? 0 : sum / Count;
    public void Add(double milliseconds) { recent[Count % recent.Length] = milliseconds; Count++; sum += milliseconds; }
    public string Summary
    {
        get
        {
            var sorted = recent.Take((int)Math.Min(Count, recent.Length)).Order().ToArray();
            double p95 = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            return $"mean {Mean:F2} ms/frame (~{(Mean > 0 ? 1000 / Mean : 0):F1} compute fps), recent p95 {p95:F2} ms";
        }
    }
}
