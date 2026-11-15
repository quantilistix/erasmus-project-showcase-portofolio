using NeuralGraph;

namespace ArcadeLab;

public sealed class Agent : IDisposable
{
    public GraphModel Model { get; }
    public IGraphBackend Backend { get; }
    private readonly float[] candidateInputs = new float[3 * Sensors.InputSize];
    private readonly float[] candidateOutputs = new float[3 * Sensors.OutputSize];
    public Agent(GraphModel model, string backend, int capacity = 96, int deviceIndex = -1)
    {
        if (model.Topology.InputCount != Sensors.InputSize || model.Topology.Outputs.Length != Sensors.OutputSize)
            throw new ArgumentException("Checkpoint topology does not match the demo's sensory/action contract.");
        Model = model;
        Backend = CreateBackend(model, backend, capacity, deviceIndex);
    }
    public static IGraphBackend CreateBackend(GraphModel model, string backend, int capacity, int deviceIndex = -1, bool layered = false)
    {
        if (backend == "cpu") return new CpuBackend(model, capacity, layered);
        if (backend is not ("auto" or "vulkan")) throw new ArgumentException("Backend must be auto, cpu or vulkan.");
        try { return new VulkanBackend(model, capacity, deviceIndex: deviceIndex, layered: layered); }
        catch (Exception e) when (backend == "auto" && e is (InvalidOperationException or NotSupportedException or DllNotFoundException))
        { Console.Error.WriteLine($"Vulkan unavailable: {e.Message}; using CPU."); return new CpuBackend(model, capacity, layered); }
    }
    public int Act(ReadOnlySpan<float> observation)
    {
        for (int a = 0; a < 3; a++) Sensors.Input(observation, a, candidateInputs.AsSpan(a * Sensors.InputSize, Sensors.InputSize));
        Backend.Predict(candidateInputs, candidateOutputs, 3);
        int best = 0; for (int a = 1; a < 3; a++) if (candidateOutputs[a * Sensors.OutputSize + a] > candidateOutputs[best * Sensors.OutputSize + best]) best = a;
        return best;
    }
    public void Save(string path) { Backend.DownloadParameters(); Model.Save(path); }
    public void Dispose() => Backend.Dispose();
}

public sealed class Trainer : IDisposable
{
    private const int Batch = 32, ReplayCapacity = 2048;
    private readonly Agent agent;
    private readonly GraphModel target;
    private readonly IGraphBackend targetBackend;
    private readonly Random random;
    private readonly Transition?[] replay = new Transition[ReplayCapacity];
    private int count, cursor, updates;
    private readonly float[] inputs = new float[Batch * Sensors.InputSize];
    private readonly float[] targets = new float[Batch * Sensors.OutputSize];
    private readonly float[] weights = new float[Batch * Sensors.OutputSize];
    private readonly float[] nextInputs = new float[Batch * 3 * Sensors.InputSize];
    private readonly float[] nextOutputs = new float[Batch * 3 * Sensors.OutputSize];
    private readonly Transition[] samples = new Transition[Batch];
    public Trainer(Agent agent, string backend, int seed, int deviceIndex = -1)
    {
        this.agent = agent; random = new(seed); agent.Backend.DownloadParameters(); target = agent.Model.Clone();
        targetBackend = Agent.CreateBackend(target, backend, Batch * 3, deviceIndex);
    }
    public void Train(GameKind game, int episodes, float rate, int seed, string? checkpointPath = null)
    {
        var env = new ArcadeGame(game, seed); var sensor = new Sensors();
        var observation = new float[Sensors.ObservationSize]; var next = new float[Sensors.ObservationSize];
        float recent = 0, loss = 0; int ticks = 0;
        agent.Backend.DownloadParameters();
        var bestParameters = (float[])agent.Model.Parameters.Clone();
        float bestReward = Evaluation.Run(agent, game, 10, 17001);
        Console.WriteLine($"Selection baseline reward {bestReward:F3} (seed 17001; separate from final evaluation).");
        for (int episode = 0; episode < episodes; episode++)
        {
            env.Reset(); sensor.Reset(env); sensor.Observe(env, observation);
            float epsilon = .1f + .9f * MathF.Max(0, 1 - episode / MathF.Max(1, episodes * .8f));
            float total = 0;
            while (!env.Done)
            {
                int action = random.NextDouble() < epsilon ? random.Next(3) : agent.Act(observation);
                float reward = env.Step(Sensors.Control(action)); total += reward;
                sensor.Observe(env, next);
                var item = replay[cursor];
                if (item is null) replay[cursor] = item = new Transition();
                observation.CopyTo(item.Observation, 0); next.CopyTo(item.Next, 0);
                item.Action = action; item.Reward = reward; item.Done = env.Done;
                cursor = (cursor + 1) % ReplayCapacity; count = Math.Min(count + 1, ReplayCapacity);
                (observation, next) = (next, observation);
                if (++ticks % 8 == 0 && count >= Batch) loss = Update(rate);
            }
            recent += total;
            if ((episode + 1) % 10 == 0 || episode == episodes - 1)
            {
                int window = (episode % 10) + 1;
                float validation = Evaluation.Run(agent, game, 10, 17001);
                if (validation > bestReward)
                {
                    bestReward = validation; agent.Backend.DownloadParameters(); agent.Model.Parameters.CopyTo(bestParameters, 0);
                }
                if (checkpointPath is not null)
                {
                    var checkpoint = agent.Model.Clone(); bestParameters.CopyTo(checkpoint.Parameters, 0); checkpoint.Save(checkpointPath);
                }
                Console.WriteLine($"{game,-5} episode {episode + 1,4}/{episodes} reward {recent / window,7:F3} loss {loss:F5} epsilon {epsilon:F2} selection {validation:F3} best {bestReward:F3}"); recent = 0;
            }
        }
        bestParameters.CopyTo(agent.Model.Parameters, 0); agent.Backend.UploadParameters();
        Console.WriteLine($"Restored best validation policy: {bestReward:F3}.");
    }
    private float Update(float rate)
    {
        for (int b = 0; b < Batch; b++)
        {
            var s = samples[b] = replay[random.Next(count)]!;
            Sensors.Input(s.Observation, s.Action, inputs.AsSpan(b * Sensors.InputSize, Sensors.InputSize));
            for (int a = 0; a < 3; a++) Sensors.Input(s.Next, a, nextInputs.AsSpan((b * 3 + a) * Sensors.InputSize, Sensors.InputSize));
        }
        targetBackend.Predict(nextInputs, nextOutputs, Batch * 3);
        for (int b = 0; b < Batch; b++)
        {
            var s = samples[b]; var y = targets.AsSpan(b * Sensors.OutputSize, Sensors.OutputSize);
            float best = float.NegativeInfinity;
            for (int a = 0; a < 3; a++) best = MathF.Max(best, nextOutputs[(b * 3 + a) * Sensors.OutputSize + a]);
            y.Clear(); y[s.Action] = Math.Clamp(s.Reward + (s.Done ? 0 : .92f * best), -5, 5); y[3] = s.Reward;
            Sensors.CoarseFrame(s.Next, y[4..]);
            var w = weights.AsSpan(b * Sensors.OutputSize, Sensors.OutputSize); w.Fill(.1f);
            w[0] = w[1] = w[2] = 0; w[s.Action] = 1; w[3] = .25f;
        }
        float loss = agent.Backend.Train(inputs, targets, weights, Batch, rate);
        if (!float.IsFinite(loss)) throw new InvalidOperationException("Training diverged; reduce --rate.");
        if (++updates % 50 == 0)
        {
            agent.Backend.DownloadParameters(); agent.Model.Parameters.CopyTo(target.Parameters, 0); targetBackend.UploadParameters();
        }
        return loss;
    }
    public void Dispose() => targetBackend.Dispose();
    private sealed class Transition
    {
        public readonly float[] Observation = new float[Sensors.ObservationSize], Next = new float[Sensors.ObservationSize];
        public int Action; public float Reward; public bool Done;
    }
}

public static class Evaluation
{
    public static float Run(Agent agent, GameKind kind, int episodes, int seed, bool randomPolicy = false)
    {
        var env = new ArcadeGame(kind, seed); var sensor = new Sensors(); var obs = new float[Sensors.ObservationSize]; var rng = new Random(seed + 1);
        float total = 0;
        for (int i = 0; i < episodes; i++)
        {
            env.Reset(); sensor.Reset(env); sensor.Observe(env, obs);
            while (!env.Done)
            {
                int action = randomPolicy ? rng.Next(3) : agent.Act(obs);
                total += env.Step(Sensors.Control(action)); sensor.Observe(env, obs);
            }
        }
        return total / episodes;
    }
}
