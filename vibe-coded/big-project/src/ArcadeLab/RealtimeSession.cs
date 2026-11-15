using NeuralGraph;
using System.Diagnostics;
using System.Text.Json;

namespace ArcadeLab;

public sealed record RealtimeConfig
{
    public int Layers { get; init; } = 8;
    public int Width { get; init; } = 512;
    public int LearnedPainInputs { get; init; } = 16;
    public int PrimitivePainInputs { get; init; } = 16;
    public int PrimitiveRewardInputs { get; init; } = 16;
    public int RewardProbes { get; init; } = 128;
    public int RewardHidden { get; init; } = 32;
    public int CalibrationFrames { get; init; } = 2000;
    public int Seed { get; init; } = 42;
    public int Fps { get; init; } = 60;
    public int PainHoldFrames { get; init; } = 12;
    public float GameSpeed { get; init; } = .5f;
    public float ClockHz { get; init; } = 1;
    public float Rate { get; init; } = .0025f;
    public float RewardRate { get; init; } = .025f;
    public float PleasureGain { get; init; } = .0625f;
    public float LearnedRewardWeight { get; init; } = .25f;
    public float LearnedPainScale { get; init; } = .25f;
    public float PrimitiveRewardWeight { get; init; } = 1;
    public float PrimitiveRewardGain { get; init; } = .25f;
    public float Discount { get; init; } = .92f;
    public int InputCount => Sensors.InputSize + LearnedPainInputs + PrimitivePainInputs + ClockInput.Count + PrimitiveRewardInputs;
    public void Validate()
    {
        if (Layers < 1 || Layers > 32 || Width < 1 || Width > 4096 || LearnedPainInputs < 1 || PrimitivePainInputs < 1 || PrimitiveRewardInputs < 1 || PrimitiveRewardInputs > 1024 ||
            LearnedPainInputs > 1024 || PrimitivePainInputs > 1024 || RewardProbes < 1 || RewardProbes > Layers * Width ||
            RewardHidden < 1 || RewardHidden > 4096 || CalibrationFrames < 0 || Fps < 1 || Fps > 240 || PainHoldFrames < 1 || PainHoldFrames > 3600 ||
            !float.IsFinite(GameSpeed) || GameSpeed < .05f || GameSpeed > 2 ||
            !float.IsFinite(ClockHz) || ClockHz <= 0 || ClockHz > 1000 ||
            !float.IsFinite(Rate) || Rate <= 0 || !float.IsFinite(RewardRate) || RewardRate <= 0 ||
            !float.IsFinite(LearnedRewardWeight) || LearnedRewardWeight < 0 || LearnedRewardWeight > 4 ||
            !float.IsFinite(LearnedPainScale) || LearnedPainScale < 0 || LearnedPainScale > 1 ||
            !float.IsFinite(PrimitiveRewardWeight) || PrimitiveRewardWeight < 0 || PrimitiveRewardWeight > 4 ||
            !float.IsFinite(PrimitiveRewardGain) || PrimitiveRewardGain < 0 || PrimitiveRewardGain > 1 ||
            !float.IsFinite(PleasureGain) || PleasureGain < 0 || PleasureGain > 1 || !float.IsFinite(Discount) || Discount < 0 || Discount >= 1)
            throw new ArgumentException("Invalid realtime size/rate/gain/pain/clock settings.");
    }
}

/// <summary>Every Step advances one game frame, predicts, learns once and refreshes activations.
/// Calibration teaches both networks from environment rewards; realtime freezes the reward network.</summary>
public sealed class RealtimeSession : IDisposable
{
    public RealtimeConfig Config { get; private set; }
    public GraphModel MainModel { get; }
    public GraphModel RewardModel { get; }
    public IGraphBackend MainBackend { get; }
    private readonly CpuBackend rewardBackend;
    public ArcadeGame Game { get; }
    public float[] Observation { get; } = new float[Sensors.ObservationSize];
    public int[] SampledNodes { get; }
    public long Frames { get; private set; }
    public long MainUpdates { get; private set; }
    public long RewardUpdates { get; private set; }
    public bool IsRealtime => RewardModel.IsFrozen;
    public bool RewardInputsUpgraded { get; private set; }
    public bool MainInputsUpgraded { get; private set; }
    public int AddedMainInputs { get; private set; }
    public bool RewardEffectsUpgraded { get; private set; }
    public float ClockValue => ClockInput.Value(Frames, Config.Fps, Config.ClockHz);
    public ReadOnlySpan<float> LastRewardInput => rewardInputRates;
    public ReadOnlySpan<float> LastRewardValues => normalizedFeatures;
    public bool RewardOnlyCalibration { get; private set; }
    private long calibrationEndFrame;
    public long CalibrationFramesRemaining => IsRealtime ? 0 : Math.Max(0, calibrationEndFrame - Frames);
    public float MaximumPleasure => RewardTeachingTarget.MaximumPleasure(Game.Kind);
    public float Pleasure { get; private set; }
    public float Pain { get; private set; }
    public float PrimitivePain { get; private set; }
    public float PrimitiveReward { get; private set; }
    public float PrimitiveRewardPulse => Game.CaughtThisFrame ? 1 : 0;
    public float LearnedPainSignal => Math.Clamp(Config.LearnedPainScale * Pain, 0, 1);
    public float LearningReward { get; private set; }
    public float Gain => 1 + Config.PleasureGain * Pleasure + Config.PrimitiveRewardGain * PrimitiveReward;
    public float Loss { get; private set; }
    public float RewardLoss { get; private set; }
    public float EpisodeReward { get; private set; }
    public double FrameMilliseconds { get; private set; }
    public FrameStatistics CalibrationStatistics { get; } = new();
    public FrameStatistics RealtimeStatistics { get; } = new();
    public int LastAction { get; private set; }
    public ReadOnlySpan<float> Activations => activations;
    public ReadOnlySpan<float> LastNetworkInput => networkInputRates;
    public ReadOnlySpan<float> LastNetworkValues => nextInput;
    private readonly Sensors sensors = new();
    private readonly HeldPainInputs heldPain;
    private readonly Random controlsRandom, painRandom, teacherRandom;
    private readonly float[] input, nextInput, output = new float[Sensors.OutputSize], nextOutput = new float[Sensors.OutputSize];
    private readonly float[] networkInputRates, networkOutputRates, networkTargetRates, rewardInputRates, rewardOutputRates, teacherInputRates, teacherTargetRates;
    private readonly float[] targets = new float[Sensors.OutputSize], weights = new float[Sensors.OutputSize];
    private readonly float[] activations, features, normalizedFeatures, featureMean, featureM2, featureScale, rewardOutput = new float[2];
    private int featureStatisticsCount;
    private readonly float[] teacherInputs, teacherTargets = new float[32], teacherWeights = Enumerable.Repeat(1f, 32).ToArray();
    private readonly List<TeacherSample> allExamples = [], pleasureExamples = [], painExamples = [];
    private bool disposed;

    public RealtimeSession(GameKind kind, RealtimeConfig config, string backend, int deviceIndex = -1,
        GraphModel? mainModel = null, GraphModel? rewardModel = null, int[]? sampledNodes = null)
    {
        config.Validate(); Config = config; calibrationEndFrame = config.CalibrationFrames;
        if (mainModel is not null && mainModel.Topology.InputCount == config.InputCount - config.PrimitiveRewardInputs - ClockInput.Count)
        {
            mainModel = ClockInput.ExpandLegacy(mainModel, config.Width); AddedMainInputs += ClockInput.Count;
        }
        if (mainModel is not null && mainModel.Topology.InputCount == config.InputCount - config.PrimitiveRewardInputs)
        {
            mainModel = PrimitiveRewardSignal.ExpandLegacy(mainModel, config.PrimitiveRewardInputs, config.Width); AddedMainInputs += config.PrimitiveRewardInputs;
        }
        MainInputsUpgraded = AddedMainInputs > 0;
        MainModel = mainModel ?? new(GraphTopology.Layered(config.InputCount, Enumerable.Repeat(config.Width, config.Layers).ToArray(), Sensors.OutputSize), config.Seed);
        if (MainModel.IsFrozen || MainModel.Topology.InputCount != config.InputCount || MainModel.Topology.Outputs.Length != Sensors.OutputSize ||
            MainModel.Topology.NodeCount != config.InputCount + config.Layers * config.Width + Sensors.OutputSize)
            throw new ArgumentException("Realtime main model must be trainable and match the sensory/action shape.");
        // The smaller readout is CPU-backed to avoid another GPU submission for a tiny graph.
        if (rewardModel is not null && rewardModel.Topology.InputCount == config.RewardProbes)
        {
            rewardModel = RewardInputs.ExpandLegacy(rewardModel, config.RewardProbes); RewardInputsUpgraded = true;
        }
        int rewardInputCount = RewardInputs.Count(config.RewardProbes);
        RewardModel = rewardModel ?? new(GraphTopology.Dense(rewardInputCount, config.RewardHidden, 2), config.Seed + 1);
        if (RewardModel.Topology.InputCount != rewardInputCount || RewardModel.Topology.Outputs.Length != 2)
            throw new ArgumentException("Reward network must accept hidden probes, raw visual history and executed controls, with exactly two outputs.");
        var hidden = Enumerable.Range(MainModel.Topology.InputCount, MainModel.Topology.NodeCount - MainModel.Topology.InputCount)
            .Except(MainModel.Topology.Outputs).ToArray();
        var probeRandom = new Random(config.Seed + 2); probeRandom.Shuffle(hidden);
        SampledNodes = sampledNodes is null ? hidden.Take(config.RewardProbes).ToArray() : sampledNodes.Select(n => n + AddedMainInputs).ToArray();
        if (SampledNodes.Length != config.RewardProbes || SampledNodes.Distinct().Count() != SampledNodes.Length ||
            SampledNodes.Any(n => n < MainModel.Topology.InputCount || n >= MainModel.Topology.NodeCount || MainModel.Topology.Outputs.Contains(n)))
            throw new ArgumentException("Reward probes must be unique hidden neurons from the main network.");
        MainBackend = Agent.CreateBackend(MainModel, backend, 1, deviceIndex, layered: true);
        try { rewardBackend = new(RewardModel, 16); }
        catch { MainBackend.Dispose(); throw; }
        input = new float[config.InputCount]; nextInput = new float[input.Length]; activations = new float[MainModel.Topology.ActivationCount];
        networkInputRates = new float[MainModel.Topology.EncodedInputCount]; networkOutputRates = new float[MainModel.Topology.EncodedOutputCount]; networkTargetRates = new float[networkOutputRates.Length];
        rewardInputRates = new float[RewardModel.Topology.EncodedInputCount]; rewardOutputRates = new float[RewardModel.Topology.EncodedOutputCount];
        teacherInputRates = new float[16 * RewardModel.Topology.EncodedInputCount]; teacherTargetRates = new float[16 * RewardModel.Topology.EncodedOutputCount];
        features = new float[rewardInputCount]; normalizedFeatures = new float[rewardInputCount];
        featureMean = new float[config.RewardProbes]; featureM2 = new float[config.RewardProbes]; featureScale = Enumerable.Repeat(1f, config.RewardProbes).ToArray();
        teacherInputs = new float[16 * rewardInputCount];
        controlsRandom = new(config.Seed + 3); painRandom = new(config.Seed + 4); teacherRandom = new(config.Seed + 5);
        heldPain = new(config.LearnedPainInputs, config.PrimitivePainInputs, config.PainHoldFrames);
        Game = new(kind, config.Seed, config.GameSpeed); sensors.Reset(Game); sensors.Observe(Game, Observation);
        if (config.CalibrationFrames == 0) FreezeReward();
    }
    public void ConfigureSignals(int? painHoldFrames = null, float? clockHz = null)
    {
        var settings = Config with { PainHoldFrames = painHoldFrames ?? Config.PainHoldFrames, ClockHz = clockHz ?? Config.ClockHz };
        settings.Validate(); Config = settings; heldPain.SetHoldFrames(settings.PainHoldFrames);
    }
    public void ConfigureGameSpeed(float? gameSpeed)
    {
        if (!gameSpeed.HasValue) return;
        var settings = Config with { GameSpeed = gameSpeed.Value }; settings.Validate(); Config = settings; Game.SetSpeed(settings.GameSpeed);
    }
    public void ConfigureRewardEffects(float? learnedWeight = null, float? pleasureGain = null, float? learnedPainScale = null,
        float? primitiveWeight = null, float? primitiveGain = null)
    {
        var settings = Config with { LearnedRewardWeight = learnedWeight ?? Config.LearnedRewardWeight, PleasureGain = pleasureGain ?? Config.PleasureGain,
            LearnedPainScale = learnedPainScale ?? Config.LearnedPainScale, PrimitiveRewardWeight = primitiveWeight ?? Config.PrimitiveRewardWeight,
            PrimitiveRewardGain = primitiveGain ?? Config.PrimitiveRewardGain };
        settings.Validate(); Config = settings;
    }
    public void RecalibrateReward(int frames)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        Config = Config with { CalibrationFrames = frames }; calibrationEndFrame = checked(Frames + frames);
        RewardModel.Unfreeze(); rewardBackend.UploadParameters(); RewardOnlyCalibration = true;
        allExamples.Clear(); pleasureExamples.Clear(); painExamples.Clear();
        Pleasure = Pain = 0;
        featureStatisticsCount = 0; Array.Clear(featureMean); Array.Clear(featureM2); Array.Fill(featureScale, 1);
    }
    public void NormalizeRewardFeatures(ReadOnlySpan<float> raw, Span<float> destination)
    {
        if (raw.Length != RewardInputs.Count(Config.RewardProbes) || destination.Length != raw.Length) throw new ArgumentException("Reward evidence shape.");
        for (int i = 0; i < Config.RewardProbes; i++) destination[i] = (raw[i] - featureMean[i]) * featureScale[i];
        raw[Config.RewardProbes..].CopyTo(destination[Config.RewardProbes..]);
    }
    private void UpdateFeatureStatistics()
    {
        if (featureStatisticsCount >= 256) return;
        featureStatisticsCount++;
        for (int i = 0; i < Config.RewardProbes; i++)
        {
            float delta = features[i] - featureMean[i]; featureMean[i] += delta / featureStatisticsCount;
            featureM2[i] += delta * (features[i] - featureMean[i]);
            featureScale[i] = 1 / MathF.Sqrt(MathF.Max(featureM2[i] / featureStatisticsCount, .0001f));
        }
    }
    public void ResetEpisode()
    {
        Game.Reset(); sensors.Reset(Game); sensors.Observe(Game, Observation); EpisodeReward = 0; LastAction = 0;
    }
    public void FreezeReward()
    {
        RewardModel.Freeze(); rewardBackend.UploadParameters(); RewardOnlyCalibration = false;
    }
    private void PredictMain(ReadOnlySpan<float> values, Span<float> scores)
    {
        RateEncoding.Encode(values, networkInputRates); MainBackend.PredictEncoded(networkInputRates, networkOutputRates, 1); RateEncoding.Decode(networkOutputRates, scores);
    }
    public void Step(int? forcedAction = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (forcedAction is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(forcedAction));
        long started = Stopwatch.GetTimestamp();
        if (Game.Done) ResetEpisode();
        bool calibrating = !IsRealtime;
        MainBackend.Gain = calibrating && RewardOnlyCalibration ? 1 : Gain;
        RealtimeInputs.Compose(Observation, LastAction, input, Config.LearnedPainInputs, Config.PrimitivePainInputs, LearnedPainSignal, PrimitivePain, painRandom, ClockValue, heldPain, Frames, PrimitiveReward, Config.PrimitiveRewardInputs);
        PredictMain(input, output);
        int action = 0; for (int a = 1; a < 3; a++) if (output[a] > output[action]) action = a;
        // Exploration chooses a raw button; it never perturbs sensory or control input values.
        if (forcedAction.HasValue) action = forcedAction.Value;
        else if (calibrating && RewardOnlyCalibration)
        {
            // Cover paddle positions independently of a potentially reward-hacking policy.
            long sweepFrames = (long)Math.Ceiling(31 / Config.GameSpeed);
            action = Frames / sweepFrames % 2 == 0 ? 1 : 2;
            if (controlsRandom.NextDouble() < .1) action = 0;
        }
        else if (calibrating && controlsRandom.NextDouble() < .4) action = controlsRandom.Next(3);
        float gameReward = Game.Step(Sensors.Control(action)); EpisodeReward += gameReward;
        sensors.Observe(Game, Observation);
        PrimitivePain = RealtimeInputs.PrimitiveLevel(PrimitivePain, gameReward);
        PrimitiveReward = PrimitiveRewardSignal.Level(PrimitiveReward, Game.CaughtThisFrame);
        RealtimeInputs.Compose(Observation, action, nextInput, Config.LearnedPainInputs, Config.PrimitivePainInputs, LearnedPainSignal, PrimitivePain, painRandom, ClockInput.Value(Frames + 1, Config.Fps, Config.ClockHz), heldPain, Frames, PrimitiveReward, Config.PrimitiveRewardInputs);
        PredictMain(nextInput, nextOutput); MainBackend.ReadActivations(activations);
        for (int i = 0; i < Config.RewardProbes; i++) features[i] = activations[2 * SampledNodes[i]] - activations[2 * SampledNodes[i] + 1];
        // Compose copies the probe prefix in place; visual evidence bypasses the main network entirely.
        RewardInputs.Compose(features.AsSpan(0, Config.RewardProbes), Observation, action, features);
        if (calibrating) { UpdateFeatureStatistics(); TrainReward(Game.RewardTarget); }
        NormalizeRewardFeatures(features, normalizedFeatures); RateEncoding.Encode(normalizedFeatures, rewardInputRates);
        rewardBackend.PredictEncoded(rewardInputRates, rewardOutputRates, 1); RateEncoding.Decode(rewardOutputRates, rewardOutput);
        Pleasure = Math.Clamp(rewardOutput[0], 0, MaximumPleasure); Pain = Math.Clamp(rewardOutput[1], 0, 1);
        if (!float.IsFinite(Pleasure) || !float.IsFinite(Pain)) throw new InvalidOperationException("Reward network diverged; reduce --reward-rate.");
        float teachingPleasure = calibrating ? Game.RewardTarget.Pleasure : Pleasure;
        float teachingPain = calibrating ? Game.RewardTarget.Pain : Pain;
        float teachingReward = LearningReward = PrimitiveRewardSignal.LearningReward(teachingPleasure, teachingPain, Game.CaughtThisFrame,
            Config.LearnedRewardWeight, Config.PrimitiveRewardWeight);
        targets.AsSpan().Clear(); weights.AsSpan().Fill(.05f); weights[0] = weights[1] = weights[2] = 0; weights[action] = 1; weights[3] = .1f;
        float nextQ = MathF.Max(nextOutput[0], MathF.Max(nextOutput[1], nextOutput[2]));
        targets[action] = Math.Clamp(teachingReward + (Game.Done ? 0 : Config.Discount * nextQ), -5, 5);
        targets[3] = teachingReward; Sensors.CoarseFrame(Observation, targets.AsSpan(4));
        if (!calibrating || !RewardOnlyCalibration)
        {
            RateEncoding.Encode(input, networkInputRates); RateEncoding.Encode(targets, networkTargetRates);
            Loss = MainBackend.TrainEncoded(networkInputRates, networkTargetRates, weights, 1, Config.Rate); MainUpdates++;
            if (!float.IsFinite(Loss)) throw new InvalidOperationException("Main network diverged; reduce --rate or --pleasure-gain.");
            // Refresh post-update activations; reward-only recalibration already evaluated the unchanged backbone above.
            PredictMain(nextInput, nextOutput); MainBackend.ReadActivations(activations);
        }
        else Loss = 0;
        LastAction = action; Frames++;
        if (calibrating && Frames >= calibrationEndFrame) FreezeReward();
        FrameMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        (calibrating ? CalibrationStatistics : RealtimeStatistics).Add(FrameMilliseconds);
    }
    private void TrainReward(RewardTeachingTarget target)
    {
        var example = new TeacherSample((float[])features.Clone(), target.Pleasure, target.Pain);
        Add(allExamples, example, 2048);
        if (target.Pleasure >= .75f) Add(pleasureExamples, example, 256);
        if (target.Pain >= .5f) Add(painExamples, example, 256);
        for (int b = 0; b < 16; b++)
        {
            var pool = b % 4 == 1 && pleasureExamples.Count > 0 ? pleasureExamples : b % 4 == 2 && painExamples.Count > 0 ? painExamples : allExamples;
            var sample = b == 0 ? example : pool[teacherRandom.Next(pool.Count)];
            NormalizeRewardFeatures(sample.Features, teacherInputs.AsSpan(b * features.Length, features.Length)); teacherTargets[b * 2] = sample.Pleasure; teacherTargets[b * 2 + 1] = sample.Pain;
        }
        RateEncoding.Encode(teacherInputs, teacherInputRates); RateEncoding.Encode(teacherTargets, teacherTargetRates);
        RewardLoss = rewardBackend.TrainEncoded(teacherInputRates, teacherTargetRates, teacherWeights, 16, Config.RewardRate); RewardUpdates++;
    }
    private void Add(List<TeacherSample> pool, TeacherSample example, int capacity)
    {
        if (pool.Count < capacity) pool.Add(example); else pool[teacherRandom.Next(pool.Count)] = example;
    }
    public void Save(string directory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Directory.CreateDirectory(directory); MainBackend.DownloadParameters();
        MainModel.Save(Path.Combine(directory, "main.ngb")); RewardModel.Save(Path.Combine(directory, "reward.json"));
        var state = new SessionState(7, Game.Kind, Config, SampledNodes, Frames, MainUpdates, RewardUpdates, calibrationEndFrame, RewardOnlyCalibration,
            featureStatisticsCount, featureMean, featureM2, featureScale, heldPain.Capture(), Pleasure, Pain, PrimitivePain, PrimitiveReward);
        string path = Path.Combine(directory, "session.json"); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); File.Move(path + ".tmp", path, true);
    }
    public static RealtimeSession Load(string directory, string backend, int deviceIndex = -1)
    {
        var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(Path.Combine(directory, "session.json"))) ?? throw new InvalidDataException("Empty realtime session.");
        if (state.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || state.Frames < 0 || state.MainUpdates < 0 || state.RewardUpdates < 0) throw new InvalidDataException("Unsupported realtime session.");
        var configuration = state.Version <= 5 ? state.Config with { PleasureGain = state.Config.PleasureGain * .25f } : state.Config;
        var session = new RealtimeSession(state.Game, configuration, backend, deviceIndex,
            GraphModel.Load(Path.Combine(directory, "main.ngb")), GraphModel.Load(Path.Combine(directory, "reward.json")), state.SampledNodes);
        session.Frames = state.Frames; session.MainUpdates = state.MainUpdates; session.RewardUpdates = state.RewardUpdates;
        if (state.FeatureMean is not null || state.FeatureM2 is not null || state.FeatureScale is not null)
        {
            if (state.FeatureMean?.Length != state.Config.RewardProbes || state.FeatureM2?.Length != state.Config.RewardProbes || state.FeatureScale?.Length != state.Config.RewardProbes ||
                state.FeatureStatisticsCount < 0 || state.FeatureStatisticsCount > 256 || state.FeatureMean.Any(x => !float.IsFinite(x)) ||
                state.FeatureM2.Any(x => !float.IsFinite(x) || x < 0) || state.FeatureScale.Any(x => !float.IsFinite(x) || x <= 0))
                throw new InvalidDataException("Invalid reward-feature normalization state.");
            state.FeatureMean.CopyTo(session.featureMean, 0); state.FeatureM2.CopyTo(session.featureM2, 0); state.FeatureScale.CopyTo(session.featureScale, 0);
            session.featureStatisticsCount = state.FeatureStatisticsCount;
        }
        if (!float.IsFinite(state.Pleasure) || state.Pleasure < 0 || state.Pleasure > (state.Version <= 5 && state.Game == GameKind.Catch ? 2 : session.MaximumPleasure) || !float.IsFinite(state.Pain) || state.Pain is < 0 or > 1 ||
            !float.IsFinite(state.PrimitivePain) || state.PrimitivePain is < 0 or > 1) throw new InvalidDataException("Invalid persisted modulation levels.");
        if (!float.IsFinite(state.PrimitiveReward) || state.PrimitiveReward is < 0 or > 1) throw new InvalidDataException("Invalid primitive reward trace.");
        session.Pleasure = Math.Clamp(state.Pleasure, 0, session.MaximumPleasure); session.Pain = state.Pain;
        session.PrimitivePain = state.PrimitivePain; session.PrimitiveReward = state.PrimitiveReward;
        session.RewardEffectsUpgraded = state.Version <= 5;
        if (state.HeldPain is not null) session.heldPain.Restore(state.HeldPain);
        session.calibrationEndFrame = state.CalibrationEndFrame ?? state.Config.CalibrationFrames;
        session.RewardOnlyCalibration = state.RewardOnlyCalibration && !session.IsRealtime;
        if (!session.IsRealtime && session.Frames >= session.calibrationEndFrame) session.FreezeReward();
        if (session.RewardInputsUpgraded || session.RewardEffectsUpgraded)
        {
            // New inputs require supervision before the expanded readout is used for live main learning.
            session.RecalibrateReward(Math.Max(6000, state.Config.CalibrationFrames));
        }
        return session;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; rewardBackend.Dispose(); MainBackend.Dispose();
    }
    private sealed record TeacherSample(float[] Features, float Pleasure, float Pain);
    private sealed record SessionState(int Version, GameKind Game, RealtimeConfig Config, int[] SampledNodes, long Frames, long MainUpdates, long RewardUpdates, long? CalibrationEndFrame = null, bool RewardOnlyCalibration = false, int FeatureStatisticsCount = 0,
        float[]? FeatureMean = null, float[]? FeatureM2 = null, float[]? FeatureScale = null,
        HeldPainState? HeldPain = null, float Pleasure = 0, float Pain = 0, float PrimitivePain = 0, float PrimitiveReward = 0);
}
