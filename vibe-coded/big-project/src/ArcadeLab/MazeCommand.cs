using System.Diagnostics;
using ArcadeLab.Editor;

namespace ArcadeLab;

internal static class MazeCommand
{
    public static void Run(Options options)
    {
        bool training = options.Has("train");
        string brainChoice=options.Get("brain","modular");
        bool baked=options.Has("brain")||(!options.Has("checkpoint")&&!new[]{"layers","width","reward-hidden"}.Any(options.Has));
        if (options.Has("new") && !training) throw new ArgumentException("--new is for --train; --play requires a trained reward system.");
        string root=MazeCheckpointPath.RepositoryRoot();
        string directory = Path.GetFullPath(options.Get("checkpoint", baked?Path.Combine(root,"artifacts",brainChoice=="modular"?"modular-maze":"baked-maze"):MazeCheckpointPath.ResolveDefault(root))), backend = options.Get("backend", baked&&brainChoice=="modular"?"cpu":"vulkan");
        Console.WriteLine($"Checkpoint: {directory}");
        int device = options.Int("device", -1), fps = options.Int("fps", 60), frames = options.Int("frames", options.Has("headless") ? 20000 : 0);
        float autosave = options.Float("autosave-seconds", 30);
        if (fps < 1 || fps > 240 || frames < 0 || (options.Has("headless") && frames == 0) || !float.IsFinite(autosave) || autosave < 0)
            throw new ArgumentException("Invalid fps, frame count or autosave interval.");
        bool existing = File.Exists(Path.Combine(directory, "maze.json"))||File.Exists(Path.Combine(directory,"brain-maze.json"));
        if(existing&&options.Has("new")&&!baked&&File.Exists(Path.Combine(directory,"brain-maze.json")))throw new ArgumentException("Use --brain PATH|latest with --new to restart a baked checkpoint.");
        if(baked&&options.Has("checkpoint")&&File.Exists(Path.Combine(directory,"manifest.json"))&&!existing)throw new ArgumentException("Save maze progress in a separate --checkpoint directory, outside the source bake.");
        if(baked&&existing&&!File.Exists(Path.Combine(directory,"brain-maze.json")))throw new ArgumentException("Use a separate --checkpoint directory for --brain.");
        if (!training && !existing && !baked) throw new ArgumentException("No maze checkpoint exists. Run --train first.");
        if (existing && !options.Has("new") && new[] { "seed", "layers", "width", "reward-hidden" }.Any(options.Has))
            throw new ArgumentException("Saved network settings are resumed. Use --new with --train to change architecture or seed.");
        if(options.Has("adaptive")&&options.Has("no-adaptive"))throw new ArgumentException("Choose --adaptive or --no-adaptive.");
        var config = new MazeConfig { AdaptiveControl=!options.Has("no-adaptive"), ActivityStep=options.Float("activity-step",.04f), WeightStep=options.Float("weight-step",.02f), WeightLimit=options.Float("weight-limit",8f), PropagationSteps = options.Int("propagation-steps", baked&&brainChoice=="modular"?4:8), Layers = options.Int("layers", 8), Width = options.Int("width", 512), Seed = options.Int("seed", 42), Speed = options.Float("game-speed", .5f),
            ThinkingBias=options.Float("thinking-bias",.1f), Rate = options.Float("rate", .00001f), RewardRate = options.Float("reward-rate", .00001f), RewardHidden = options.Int("reward-hidden", 128) };
        config.Validate();
        BakedBrain? initialBrain=null;
        if(baked&&(!existing||options.Has("new")))
            initialBrain=brainChoice=="modular"?Baking.Bake(MazeBrainTemplate.Create()):BakedBrain.Load(brainChoice=="latest"?MazeBakedBrain.Latest(root):brainChoice);
        using var session = existing && !options.Has("new") ? MazeSession.Load(directory, training, backend, device, options.Has("game-speed") ? config.Speed : null, options.Has("propagation-steps") ? config.PropagationSteps : null, activityStep:options.Has("activity-step")?config.ActivityStep:null, weightStep:options.Has("weight-step")?config.WeightStep:null, rate:options.Has("rate")?config.Rate:null, rewardRate:options.Has("reward-rate")?config.RewardRate:null, weightLimit:config.WeightLimit,thinkingBias:options.Has("thinking-bias")?config.ThinkingBias:null,adaptiveControl:options.Has("adaptive")?true:options.Has("no-adaptive")?false:null) : new MazeSession(config, training, backend, device,bakedBrain:initialBrain);
        if(existing&&session.IsBakedBrain&&!options.Has("new"))Console.WriteLine("Resuming the saved baked brain; use --train --new --brain PATH|latest to reload an export.");
        if(session.Adaptive is not null)Console.WriteLine("ADAPTIVE: online neural TD learning, sensory history, bounded replay and observed-transition lookahead. TRAIN updates action values; PLAY freezes the action learner. Completion/death/180-second timeout starts a new maze.");
        session.BotEnabled=options.Has("bot");
        if(session.BotEnabled)Console.WriteLine("BOT TEACHER: graph exploration, rounded routes, independent gaze, supplies, close combat and bullet dodging. Its buttons clamp the actual motor neurons; Tab manual overrides the bot.");
        if (!training && !session.IsBakedBrain && session.RewardUpdates == 0) throw new ArgumentException("Reward network has no training updates. Run --train first.");
        if(session.IsBakedBrain)
        {
            Console.WriteLine($"BAKED: {session.Baked!.Brain.Manifest.Name}; {session.Backend.DeviceName}; {session.MainModel.NormalCount:N0} neurons, {session.MainModel.Weights.Length:N0} connections.");
            Console.WriteLine("Editor clocks, activation caps, fixed plasticity and reward-group local learning are active. Output panel drives the eight maze buttons.");
            Console.WriteLine("Stereo audio = RMS; three-channel video = mean RGB; tactile ring = 16 body-relative channels. Play freezes reward teaching.");
            Console.WriteLine($"Perceived reward = dR/dt. Every cortex has implicit thinking bias +{session.Config.ThinkingBias}; local factor = reward factor + bias. Audio/video reward modulation and teaching are disabled.");
            Console.WriteLine(session.Adaptive is null?"TRAIN teaches reward-path weights from hits/misses, kills, exit and death; PLAY uses inferred derivatives only.":"TRAIN teaches reward paths and action values from bounded exploration, progress, combat outcomes and survival costs; PLAY freezes both teaching paths.");
            foreach(var (node,signal) in session.Baked.InputLabels)Console.WriteLine($"INPUT node={node} channel={session.Baked.Brain.Manifest.Neurons[node].Id}: {signal}");
            foreach(var (node,key) in session.Baked.OutputLabels)Console.WriteLine($"OUTPUT node={node} channel={session.Baked.Brain.Manifest.Neurons[node].Id}: {key} (activity > {MazeControlPolicy.ActivationThreshold})");
        }
        else
        {
        Console.WriteLine(training?"TRAIN: paired-error inference and local plasticity; reward network taught with clamped labels.":"PLAY: paired-error inference; main plasticity active, reward weights frozen.");
        Console.WriteLine($"{session.Backend.DeviceName}; {session.MainModel.Topology.NodeCount:N0} normal neurons plus paired error populations, {session.MainModel.Weights.Length:N0} shared w/-w connections. Eight nonnegative motor outputs.");
        if(session.Backend is NeuralGraph.VulkanPredictiveCodingBackend { DeviceType: Silk.NET.Vulkan.PhysicalDeviceType.Cpu })
            Console.WriteLine("Vulkan selected a CPU renderer. Hardware acceleration requires an accessible GPU and its Vulkan driver; use --list-devices and --device INDEX to select a different device.");
        Console.WriteLine($"Integration: {session.Config.PropagationSteps} steps/frame, activity h={session.Config.ActivityStep}, weight h={session.Config.WeightStep}, eta={session.Config.Rate}, reward eta={session.Config.RewardRate}. One local weight update/frame.");
        Console.WriteLine($"Weights in both networks are clamped to [-{session.Config.WeightLimit}, +{session.Config.WeightLimit}] on load and after each update.");
        Console.WriteLine("Local weight update = -weightStep * eta * plasticity * error * normal, clamped to the weight range (both networks).");
        Console.WriteLine("Main eta = base eta * (total pleasure - total pain). Totals use normalized applied signals plus primitive sensations from the preceding feedback (ideal in train, inferred in play). Propagation gain stays 1.");
        Console.WriteLine("Inactivity pain ramps to 1 after a 0.5-second grace period and a 4.5-second ramp in simulation time; meaningful displacement resets it.");
        Console.WriteLine("Normal neuron activities in both networks are capped to [0, 1], including sensory inputs and resumed states.");
        Console.WriteLine("Motor output errors track their normal activities (e+=x, e-=0); controls read the normals. Both networks use fixed plasticity 1, including resumed checkpoints.");
        Console.WriteLine("Nonprimitive pleasure/pain and primitive pleasure fade with a 60-update half-life. Primitive pain is unchanged.");
        Console.WriteLine("Drive inputs = total pleasure + total pain * held U(0,1); noise refreshes every 12 frames. Negative reward factors reverse local plasticity.");
        Console.WriteLine(session.Adaptive is null?"Total error = SUM(E^2), statistics only; legacy controls use local predictive plasticity.":"Predictive cortex uses local plasticity; the adaptive action head learns a separate TD value objective with replay.");
        }
        Console.WriteLine($"Level {session.Game.Level}; maze geometry stage {session.Game.DifficultyStage+1}/5; corridor width {session.Game.CorridorWidth}; health drain, primitive pain and ideal training rewards retained. Checkpoint {directory}.");
        if(session.Adaptive is null)Console.WriteLine("Legacy control resets the world only after death.");
        Console.WriteLine("Start with no ammo. Level 1: no ammo pickups or enemies. Level 2: ammo pickups. Level 3+: ammo pickups and enemies.");
        Console.WriteLine("Tab manual, W/S A/D Q/E F fire X reload, Space pause, Escape save and quit.");
        var timer = Stopwatch.StartNew(); var statistics = new FrameStatistics(); double lastSave = 0; bool stopping = false;
        long lastDiagnosticFrame=session.Frames;bool wasAgainstWall=false;
        void SaveIfDue()
        {
            bool againstWall=session.Game.WallCollisionPain>0;
            if(session.Frames-lastDiagnosticFrame>=60 || session.Game.DiedThisFrame || session.Game.Events.Damage>0 || (againstWall&&!wasAgainstWall))
            { PrintError(session);lastDiagnosticFrame=session.Frames; }
            wasAgainstWall=againstWall;
            if (!options.Has("no-save") && autosave > 0 && timer.Elapsed.TotalSeconds - lastSave >= autosave) { session.Save(directory); lastSave = timer.Elapsed.TotalSeconds; Console.WriteLine($"Saved frame {session.Frames}."); } }
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; Volatile.Write(ref stopping, true); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (options.Has("headless")) for (int i = 0; i < frames && !Volatile.Read(ref stopping); i++)
            { session.Step(); statistics.Add(session.FrameMilliseconds); SaveIfDue(); if ((i + 1) % 600 == 0 || i + 1 == frames) Print(session, statistics); }
            else { using var viewer = new Viewer(); viewer.PlayMaze(session, frames, fps, options.Has("mute"), statistics, SaveIfDue, () => Volatile.Read(ref stopping)); }
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (!options.Has("no-save")) { session.Save(directory); Console.WriteLine($"Saved maze checkpoint in {directory}."); }
        }
        Console.WriteLine($"{statistics.Count} learning frames in {timer.Elapsed.TotalSeconds:F2}s; {statistics.Summary}");
    }
    internal static void PrintError(MazeSession s)
    {
        Console.WriteLine(FormattableString.Invariant($"PC frame={s.Frames} total_error_sum_e2={s.Loss:F6} reward_error_sum_e2={s.RewardLoss:F6} teaching_error_sum_e2={s.RewardTeachingError:F6} total_pleasure={s.TotalPleasure:F5} total_pain={s.TotalPain:F5} reward_factor={s.WeightRewardFactor:F5} eta_effective={s.EffectiveLearningRate:F8} main_ticks={s.Backend.PropagationSteps} reward_ticks={s.RewardPropagationSteps} local_updates={s.MainUpdates}/{s.RewardUpdates}"));
        if(s.Adaptive is {} adaptive)Console.WriteLine($"ADAPTIVE decisions={adaptive.Decisions} updates={adaptive.Updates} replay={adaptive.ReplayCount} views={adaptive.KnownViews} td={adaptive.LastTdError:F4} return={s.AdaptiveRewards.EpisodeReturn:F3}");
        Console.WriteLine(FormattableString.Invariant($"SENSE primitive_pain={s.PrimitivePain:F4} primitive_pleasure={s.PrimitivePleasure:F4} applied_pain={s.AppliedPain:F4} applied_pleasure={s.AppliedPleasure:F4} inferred_pain={s.Pain:F4} inferred_pleasure={s.Pleasure:F4} HP={s.Game.Health:F2} ammo={s.Game.Ammo}/{s.Game.Reserve} controls={s.LastControls}"));
    }
    internal static void Print(MazeSession s,FrameStatistics stats)
    {
        PrintError(s);Console.WriteLine($"Level {s.Game.Level}, geometry stage {s.Game.DifficultyStage+1}/5, completions {s.CompletedMazes}, {stats.Summary}");
    }
}
