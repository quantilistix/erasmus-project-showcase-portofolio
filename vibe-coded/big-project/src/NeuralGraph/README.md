# NeuralGraph: paired-error predictive coding

The new predictive APIs implement the [agreed model](PREDICTIVE_CODING.md), independently of games, rewards, GLFW, audio or rendering. Copy this directory and reference `NeuralGraph.csproj`. Dependencies are .NET 10 and Silk.NET.Vulkan. SPIR-V is embedded; a shader compiler is needed only when changing GLSL.

## Minimal use

```csharp
using NeuralGraph;

// Edge(i,j) means error_i -> normal_j with w, and normal_j -> error_i with -w.
var topology = new GraphTopology(2, 4, steps: 2, outputs: [3], edges:
    [new(0, 2), new(1, 2), new(2, 3)]);
var model = new PredictiveCodingModel(topology, seed: 42);
model.Plasticity[0] = 0;   // Fixed multiplier: freeze this shared weight.
model.Plasticity[1] = .5f; // Halve this connection's local changes.

using IPredictiveCodingBackend network = new VulkanPredictiveCodingBackend(model);
// CPU reference: new CpuPredictiveCodingBackend(model)
network.Step(inputs: [0.4f, 0.8f], steps: 8,
    activityStep: .04f, weightStep: .02f, learningRate: .00001f, learn: true);

var state = network.CaptureState();
float nonnegativeOutput = state.Normal[topology.Outputs[0]];
float signedError0 = state.ErrorRates[0] - state.ErrorRates[1];
double statisticsOnly = network.MeasureTotalError(); // Sum of ALL represented errors squared.
network.DownloadParameters();
model.Save("network.pcn");
```

Every graph node has one nonnegative normal activity and two nonnegative error channels representing a signed error. There are no biases. Each edge has exactly one trainable weight and one fixed plasticity value; fixed local +1/−1 connections and the reciprocal −w are implicit and cannot drift independently.

Inputs must be nonnegative physical channels. For signed external features, call `RateEncoding.Encode` and allocate two input normal units per signed feature. Outputs are direct nonnegative normal activities; do not subtract two output neurons to get an action value. Error channels alone encode a signed internal discrepancy.

## Update semantics

A `Step` call clamps current inputs, performs the requested number of activity ticks, and applies at most one local weight update. Each tick uses projected symplectic Euler: integrate all errors from the preceding normal state (including current clamps), then integrate free normals from the new errors and project them to zero or above. Weights change after the activity ticks by `weightStep * learningRate * plasticity * error_source * normal_destination`. There is no global-loss gradient, SGD gradient clipping, normalization, damping or tanh. The optional library gain multiplies w and −w symmetrically; the active maze keeps it at 1. Signed effective learning rates are allowed: a negative rate reverses the local weight increment, while a zero rate leaves weights unchanged. Fixed +1/−1 connections are unchanged.

`outputClamps` optionally supplies nonnegative teaching boundaries for all declared outputs. Clamped states still have paired errors, and all those errors contribute to the statistic. `Topology.Steps` is legacy topology metadata; the call's `steps` controls predictive integration. Cycles, skips, self-connections and disconnected nodes are supported. `GraphTopology` disallows variable edges targeting clamped input normals; predictions into their paired errors are provided by reciprocal edges.

`MeasureTotalError()` computes exactly `sum((ePositive - eNegative)^2)` over every node, including clamped inputs and outputs. It is neither averaged nor multiplied by 1/2. It is a read-only statistic: omit it entirely and inference/learning remain identical. Integrating discrepancies without damping can accumulate error under forcing; no monotonic convergence or variational-free-energy claim is made.

## Freezing, persistence and state

`model.Freeze()` disables updates while activities continue. It does not change the stored fixed plasticity values. `Unfreeze()` re-enables them. `learn: false` disables updates for a call. Explicit CPU array edits are immediate; GPU edits require `UploadParameters()`. Call `DownloadParameters()` before inspecting or saving GPU weights.

`CaptureState()` copies all normal activities, both error channels and the activity counter. `RestoreState()` restores those arrays/counter without modifying weights. It does not implicitly clear state or undo learning. Model `.pcn` files store topology, one weight and plasticity per edge, and the frozen flag. Serialize the returned state separately if persistent activity is required. The maze does this in its checkpoint metadata.

The CPU and GPU use the same ordering and clamping rules. GPU kernels use cooperative reductions for incoming/outgoing connection sums and a separate per-edge local-update dispatch without atomics. A derived outgoing-order weight cache accelerates backward prediction; uploads and the same per-edge update refresh it from the single stored weight. It is not an independently learned reverse matrix. Model parameters and state remain GPU-resident. This is a rate model, not a spiking simulation.

The older signed-pair feedforward/backpropagation APIs remain available for compatibility. Their behavior and examples are in [LEGACY_GRAPH.md](LEGACY_GRAPH.md). They are separate mechanisms and are not used by the active predictive maze.
