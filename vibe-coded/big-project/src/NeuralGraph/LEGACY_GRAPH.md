Historical documentation of the separate legacy feedforward/backpropagation APIs. The active maze uses [the implemented predictive model](PREDICTIVE_CODING.md). Status statements below predate its implementation.

# NeuralGraph

Copy this directory and reference `NeuralGraph.csproj` to integrate it elsewhere. Dependencies are .NET 10 and Silk.NET.Vulkan 2.23.0. Games, rewards, GLFW, audio and rendering live in ArcadeLab. Compiled SPIR-V is embedded, so consumers do not need a shader compiler.

**Architecture status:** [PREDICTIVE_CODING.md](PREDICTIVE_CODING.md) records the agreed replacement model, including explicit normal/error pairs and the statistics-only sum of squared errors. It is a specification, not the currently implemented API. The code and examples below describe the existing persistent-propagation/backpropagation backend. Its two-channel signed representation is not a normal/error neuron pair. Old training progress may be discarded when implementing the replacement.

## Nonnegative neuron rates

Every logical graph unit is implemented as **two nonnegative rate neurons**: positive and negative components. For external signed value `x`, the input channels are `[max(x,0), max(-x,0)]`. Application readouts decode by subtracting the second channel from the first. This preserves signed PCM samples, errors and Q scores without any negative neuron activations.

For net potential `z`, a hidden pair fires `[max(tanh(z),0), max(tanh(-z),0)]`. An output pair fires `[max(z,0), max(-z,0)]`. Thus both components are nonnegative; the term "negative channel" describes what its activity encodes, not a negative firing rate. Signed weights can provide inhibitory effects. Biases, net potentials, gradients and decoded application values may be negative; actual input/hidden/output activation buffers may not.

Paired channels share tied opposite-signed parameters. Their decoded function and gradients equal those of the historical signed graph, preserving learned weights rather than requiring retraining after the encoding change. All CPU and Vulkan kernels store activation pairs, including recurrent/unrolled execution. This is a rate-coded mathematical model, not a spiking-neuron simulation.

Topology dimensions remain **logical**: `InputCount`, `NodeCount` and `Outputs.Length`. Physical channel counts are `EncodedInputCount = 2*InputCount`, `ActivationCount = 2*NodeCount` and `EncodedOutputCount = 2*Outputs.Length`. Channel order is interleaved: `[node0 positive, node0 negative, node1 positive, node1 negative, ...]`.

```csharp
using NeuralGraph;

var topology = new GraphTopology(2, 4, steps: 4, outputs: [3], edges:
    [new(0, 2), new(1, 2), new(2, 3), new(3, 2), new(2, 2), new(0, 3)]);
var model = new GraphModel(topology, seed: 42);
model.Plasticity[0] = 0;   // freeze this weight
model.Plasticity[1] = .5f; // halve this weight's changes

using IGraphBackend backend = new VulkanBackend(model, capacity: 64);
// CPU reference: new CpuBackend(model, capacity: 64)
var rateInputs = new float[topology.EncodedInputCount];
RateEncoding.Encode([.4f, -.2f], rateInputs); // [.4, 0, 0, .2]
var rateOutputs = new float[topology.EncodedOutputCount];
backend.PredictEncoded(rateInputs, rateOutputs, batchSize: 1);
var rates = new float[topology.ActivationCount];
backend.ReadActivations(rates); // every value is a nonnegative physical neuron rate

float[] decodedOutput = new float[topology.Outputs.Length];
RateEncoding.Decode(rateOutputs, decodedOutput); // application-level signed score
float[] targetRates = new float[topology.EncodedOutputCount];
RateEncoding.Encode([-.8f], targetRates);
float loss = backend.TrainEncoded(rateInputs, targetRates, weights: [1],
    batchSize: 1, learningRate: .01f);
backend.DownloadParameters();
model.Save("graph.json");
```

`PredictEncoded` and `TrainEncoded` reject negative/nonfinite rate inputs and targets. Loss weights are one nonnegative value per **logical** output, rather than one per channel. Loss is `mean(samples) sum(outputs) 0.5*weight*(decodedPrediction-decodedTarget)^2`, preserving the previous scalar learning objective.

For compatibility, `Predict(inputs, outputs, batch)` and `Train(inputs, targets, weights, batch, rate)` accept decoded application values and encode them at the boundary. Their returned signed scores are not neuron activations. `ReadValues(destination, sampleIndex)` similarly decodes logical values for diagnostics. `ReadActivations` requires **ActivationCount floats**, not the old logical NodeCount. This inspection API shape changed deliberately to expose the actual nonnegative neurons.

## Persistent propagation

`AdvanceEncoded(inputs, outputs, steps: 1)` advances **one live stream** without clearing its neuron state. Each tick clamps the current inputs, reads every non-input source from the preceding tick, and writes the next activation state into a separate bank. A signal travels one connection hop per tick, including through cycles and self-connections. A layered graph is not swept instantly from bottom to top in this API. `Topology.Steps` controls legacy stateless unrolling, not how long the live stream persists.

```csharp
using IGraphBackend backend = new VulkanBackend(model, capacity: 1, layered: true);
var input = new float[model.Topology.EncodedInputCount];
var output = new float[model.Topology.EncodedOutputCount];
var state = new float[model.Topology.ActivationCount];

// On each simulation frame: encode ONLY the current sensory values.
RateEncoding.Encode(currentValues, input);
backend.AdvanceEncoded(input, output, steps: 2);
backend.ReadStateActivations(state);

// Ordinary minibatch learning uses separate working buffers and changes weights,
// leaving the live activation state and PropagationSteps untouched.
backend.TrainEncoded(trainingInputs, trainingTargets, lossWeights, batchSize, learningRate);

// The host can checkpoint these physical rate channels and the propagation counter.
backend.ReadStateActivations(state);
long ticks = backend.PropagationSteps;
backend.RestoreStateActivations(state, ticks);
// backend.ResetState(); // Explicit reset only, never needed between frames.
```

`Advance` is the corresponding decoded-value adapter. Native inputs/outputs and state snapshots remain nonnegative dual-rail rates. `ReadStateActivations` always reads live state, even after `Predict`/`Train` have used the stateless working buffers. `ReadActivations` and `ReadValues` retain their existing meaning: inspection of the last stateless prediction/training example.

Live state is initialized once when a backend is created. Changing weights/gain affects subsequent propagation ticks without erasing current activities. `Freeze()` freezes parameters, not activation dynamics. An `Advance` call accepts 1–1,024 ticks with a constant input during that call. Calls are synchronous; the host supplies the simulation clock rather than a background busy loop. CPU uses two dedicated arrays; Vulkan keeps two banks on the GPU in a region separate from training state and uses a cooperative kernel for all directed connections.

Persistent propagation does not itself implement predictive-coding error-neuron dynamics or temporal backpropagation. The existing `TrainEncoded` objective remains stateless supervised loss with clipped SGD. Applications choose their learning rule independently of live-state evolution.

## Arbitrary graphs and timing

Inputs occupy logical IDs `[0, InputCount)` and stay clamped. Other logical units have trainable biases. Output IDs must be unique non-input units. Duplicate edges, self-connections, cycles, skips and disconnected units are legal; edges targeting inputs are rejected. Treat topology arrays as immutable after construction.

Default prediction initializes non-input rates to zero and runs exactly `Steps` synchronous propagation steps (1–256). Every edge reads the preceding step. A two-edge input/hidden/output path needs at least two steps. Backpropagation covers the full finite unroll. There is no persistent hidden state between **stateless prediction** calls. Use `AdvanceEncoded` for a continuous activation stream instead of supplying a previous image.

```csharp
var topology = GraphTopology.Layered(inputs: 1012,
    hiddenSizes: [512, 512, 512, 512, 512, 512, 512, 512], outputs: 16);
var model = new GraphModel(topology);
using IGraphBackend backend = new VulkanBackend(model, capacity: 1, layered: true);
// CPU: new CpuBackend(model, capacity: 1, layered: true)
backend.Gain = 1.05f;
```

`Layered` fully connects adjacent layers and, by default, each odd layer to layer + 2. Input is layer 0, hidden layers start at 1, and the output layer participates in the numbering. Widths may differ; `oddSkips: false` omits extra edges.

`layered: true` compiles an acyclic topological schedule, evaluates each logical unit once and backpropagates in reverse order. Arbitrary node IDs and acyclic skip/output paths are accepted, cycles rejected. `Steps` must cover the longest feed-forward path. Decoded results/gradients equal a sufficiently unrolled acyclic graph without redundant intermediate passes.

## Fixed plasticity, gain and freezing

Training uses clipped SGD:

```
weightChange = -learningRate * clamp(meanGradient, -1, 1)
edgeWeight += fixedPlasticity[edge] * weightChange
bias += -learningRate * clamp(meanBiasGradient, -1, 1)
```

Plasticity defaults to one, must be finite/nonnegative, and is never optimized or decayed. Zero freezes that weight without stopping gradient propagation through it. Biases have no connection plasticity. Gain defaults to one, must be finite in [0,4], and multiplies effective edge weights in forward and backward computation. Changing gain does not rewrite stored weights or plasticity.

`model.Freeze()` sets edge plasticity to zero and `IsFrozen` true. Train calls still predict/evaluate loss but never update frozen weights or biases. `model.Unfreeze(plasticity: 1)` explicitly permits learning again while preserving stored parameters and resetting edge multipliers to the specified constant. These are explicit host operations, not optimizer changes.

After host parameter/plasticity edits or freezing a GPU-backed model, call `UploadParameters()`. Download GPU parameters before inspecting, cloning or saving after training. CPU backends operate directly on model arrays. Direct edits to public arrays remain the caller's responsibility.

## Ownership, inspection and checkpoints

Backends are synchronous, single-owner and not thread-safe. Do not mutate model parameters during execution. A supplied VulkanDevice must outlive its backends and needs external synchronization if shared across concurrent callers. Otherwise a backend owns its device.

Encoded tensors are sample-major, with adjacent positive/negative channels. Capacity bounds the batch, not graph size. `ReadActivations` requires a successful prior evaluation and returns the selected sample. Train leaves pre-update forward rates; call Predict/PredictEncoded again to refresh rates after a weight update. These working activations are separate from the persistent state. The live maze demo advances and inspects persistent state rather than refreshing it with a stateless prediction after learning.

JSON saves now use version **4**, including fixed plasticity, frozen state and `ActivationEncoding: nonnegative-dual-rail`; versions 1–3 still load. Binary `.ngb` saves use version **2**, declaring this encoding while retaining compact logical graph/parameter storage; version 1 still loads. No learned parameter conversion is needed to preserve decoded behavior. Neither model format stores transient activation tensors or gain; the host owns simulation state. ArcadeLab's separate session file also saves the main/reward live rate arrays and propagation counters, alongside configuration and training counters.

Use `.ngb` for large graphs: it streams topology, weights, plasticity and frozen state rather than constructing a large JSON string. Rebuild embedded shaders with `scripts/compile-shaders.sh` after editing GLSL.

The Vulkan kernels use CSR adjacency in both directions. Cyclic mode uses a workgroup per sample; layered mode cooperatively reduces each logical unit's fan-in. Parameter updates use a separate reduction without floating-point atomics. Activations and neural input/output buffers store only nonnegative channel pairs; signed delta buffers hold gradients, not activations. Parameters/scratch stay allocated, and weights transfer only on explicit upload/download. Mapped coherent buffers suit integrated GPUs but discrete-GPU staging/async overlap remain potential optimizations. Benchmark your actual workload; small graphs can favor the CPU reference.
