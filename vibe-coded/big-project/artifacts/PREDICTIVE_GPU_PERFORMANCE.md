# Predictive GPU performance changes

The application already defaulted to Vulkan compute. Lag can persist because its default main network contains 4,214,784 connections and performs eight error/normal integration ticks per frame. Training also runs free and teaching reward passes. A previous hardware run in `predictive-coding-optimized-benchmark.log` reported 42.92 ms/frame on Intel Arc (MTL), before rendering; this historical run is not a before/after comparison for these changes.

Changes:

- Dispatch normal integration only for free, non-input nodes. Copy input boundary values cooperatively, without reductions over their empty incoming adjacency. Skip incoming reductions for teaching-clamped output nodes as well. Error integration still covers every node and uses current boundary values.
- Prefer host-visible, coherent device-local memory for predictive buffers when the hardware exposes it, retaining coherent host memory as the fallback. Other backends retain their previous allocation preference.
- Read only the viewer's sampled weights at its existing 2 Hz refresh rate. Previously each refresh copied and validated all 4.2 million main weights. Save/export still performs a full parameter download and validation.
- Add `--list-devices` and identify CPU Vulkan devices explicitly at startup.

No neural dimensions, equations, activity tick counts, weight update schedules or checkpoint formats were changed.

## Local comparison

Environment: .NET 10.0.113, Release build, llvmpipe (LLVM 20.1.2, 256 bits). No `/dev/dri` device is exposed. These measurements exercise **software Vulkan**, not physical GPU hardware.

Both runs used the same fresh default network, seed and 20-frame headless workload, with no saves:

```bash
dotnet src/ArcadeLab/bin/Release/net10.0/ArcadeLab.dll \
  --train --new --headless --no-save --frames 20 --checkpoint /tmp/pc-perf-unused
```

| Measurement | Before | After |
| --- | ---: | ---: |
| Mean learning frame | 198.46 ms | 158.12 ms |
| Compute FPS | 5.0 | 6.3 |
| Recent p95 | 192.24 ms | 163.16 ms |
| Main error sum at frame 20 | 10183.521580 | 10183.521580 |
| Free reward error sum at frame 20 | 10715.982966 | 10715.982966 |
| Teaching error sum at frame 20 | 11801.981640 | 11801.981640 |

This short comparison showed about 20% less mean frame time, or 25% more compute throughput. It includes startup/JIT effects in learning-frame timing and is not a hardware performance guarantee. Headless timing excludes viewer rendering and therefore does not measure the sparse viewer refresh improvement. Physical GPU memory-placement benefits and graphical responsiveness require validation on the user's hardware.

## Verification

- Rebuilt `predictive.spv` with `glslangValidator -V`.
- Release project build: zero warnings/errors (`dotnet build tests/NeuralGraph.Checks/NeuralGraph.Checks.csproj -c Release --no-restore -m:1`).
- Predictive checks with required Vulkan: 4,111 assertions passed. Coverage includes recurrence, teaching clamps, positive/negative learning rates, state restoration, 257 sensory inputs with only four free nodes, incoming reductions larger than one workgroup, and ordered/duplicate sparse weight reads that leave the host parameter cache untouched.
- Predictive maze integration: 919 assertions passed.
- Reward-drive checks: 7 assertions passed.
- Continuous CPU/Vulkan checks: 587 assertions passed.
- `--list-devices` reported device 0 as llvmpipe / Cpu.

For hardware validation, list devices, choose a GPU index with `--device INDEX`, and compare repeated Release headless runs on the same checkpoint and frame count with `--no-save`. Startup output identifies the selected device. Then run the viewer to assess total displayed frame rate.


## Incoming adjacency cache (2026-10-27)

Normal integration previously loaded an incoming edge index, then gathered its
source index and canonical weight. The shader now reads sources and a derived
weight copy directly in incoming adjacency order. Each local plasticity update
writes the same computed float to the canonical weight and both adjacency views;
parameter uploads rebuild both views. The cache adds no learned parameters and
is not serialized. Lane assignments, reduction order, equations, eight activity
ticks, learning schedules and checkpoint formats are unchanged.

The tradeoff is one extra float and one inverse index per connection (about
32.2 MiB for the default main graph), plus a cache write per weight update. Graphs
must still fit the device's storage-buffer range limits. Hardware GPU speed and
displayed frame rate have not been measured here; this environment exposes only
llvmpipe and has no `/dev/dri` device.

Verification:

- Release build with zero warnings/errors; shader rebuilt with `glslc` and checked
  with `spirv-val`.
- Required-Vulkan predictive checks: 4,287 assertions, including recurrence,
  teaching clamps, wide incoming reductions, positive/negative learning rates,
  sparse reads, state restoration, parameter reuploads, fixed zero plasticity,
  frozen models, zero rates and inference-only passes.
- Predictive maze integration: 937 assertions, including teaching/freeze and
  state persistence.

Controlled comparison: both baseline and optimized shaders compiled with the same
`glslc`, same Release build, seed, fresh default graph and 120-frame command above
(with `--frames 120`). No checkpoints were loaded or saved.

| Measurement | Before | After |
| --- | ---: | ---: |
| Mean learning frame | 129.05 ms | 121.48 ms |
| Compute FPS (1000 / mean) | 7.75 | 8.23 |
| Recent p95 | 132.72 ms | 126.33 ms |
| Main error sum at frame 120 | 437974.390126 | 437974.390126 |
| Free reward error sum at frame 120 | 459239.484441 | 459239.484441 |
| Teaching error sum at frame 120 | 466892.768065 | 466892.768065 |

That is 5.9% less mean compute time, or 6.2% higher throughput. Earlier runs
against the original bundled baseline shader showed 7.8–11.3% higher throughput;
short-run timing varies. These measurements include learning-frame startup/JIT
cost and exclude rendering. All three emitted neural diagnostic records and all
three sensation/control records matched exactly in the controlled comparison;
this is not a claim of exhaustive bitwise state comparison. CPU/GPU numerical
agreement is covered separately by the checks above.

Raw logs: [before](predictive-incoming-cache-before.log),
[after](predictive-incoming-cache-after.log).


## Device-local storage and specialized kernels (2026-10-27)

This supersedes the mapped-only allocation path described earlier. The target is
GPU throughput on a machine with 32 GB RAM and 12 GB VRAM. Memory capacity is
ample for the default graph; where buffers live and how kernels access them matter
more than reserving more unused memory.

- Allocate weights, graph metadata and working activities with an explicit
  `DEVICE_LOCAL` requirement, without requiring CPU mapping. Discrete devices can
  therefore use their dedicated memory even without a CPU-visible VRAM heap.
  Separate coherent host staging buffers handle uploads and readbacks. See
  [Khronos staging-buffer guidance](https://docs.vulkan.org/tutorial/latest/04_Vertex_buffers/02_Staging_buffer.html).
- Upload weights and metadata at initialization or explicit parameter upload,
  rather than per frame. Copy working state to the CPU after each inference pass;
  a state restore marks the GPU copy for upload before the next operation.
  Read back canonical weights only on explicit download/save. Viewer samples use
  a GPU gather and bounded 4,096-index chunks, retaining arbitrary request order
  and duplicates without a full parameter transfer.
- Prepare sensory and teaching clamps once per inference pass. Remove per-edge
  boundary branches and output-map gathers from error integration.
- Compile separate phase pipelines using a shader specialization constant, so
  each kernel can discard the other phase branches.
- Use subgroup shuffles when supported, retaining the **same** 64 logical lanes,
  strided partial sums and `32,16,8,4,2,1` addition tree. Cross-subgroup levels use
  shared memory; levels within a subgroup use shuffles. A runtime workgroup check
  falls back to the shared tree if the subgroup layout is not full. Devices
  without relative shuffle support select the portable shader. Selection follows
  [Khronos subgroup capability queries](https://docs.vulkan.org/guide/latest/subgroups.html).
- Prefer host-cached coherent memory for CPU readback/staging and the viewer's
  CPU-composed framebuffer. Other callers retain their existing default memory
  preference.

The main graph's resident weights, metadata and state use approximately 161 MiB
before allocator alignment; host staging duplicates those arrays. No additional
model parameters, changed equations, reduced precision, skipped inference ticks,
altered learning intervals or changed checkpoint formats are involved. Both
reduction paths can be selected through the backend constructor for verification;
automatic selection is the application default. Startup identifies device-local
storage and the selected reduction shader.

### Local validation

Release build: zero warnings/errors. Both predictive shaders compiled with
`glslc` and passed `spirv-val` (Vulkan 1.1 target for the subgroup variant).
The full checks executable with `--require-vulkan` passed **18,124 assertions**.
This includes 4,343 predictive assertions and 937 predictive-maze assertions, as
well as legacy graph/continuous Vulkan paths, teaching, manual/bot clamps,
checkpoint persistence, rendering helpers, audio and sensory checks.

New checks cover actual device-local memory flags, rejection of CPU access to
device-only buffers, pending uploads before reads, explicit downloads after host
model edits, more than two sparse gather chunks, duplicate requests, untouched
activity state during readback, empty adjacency, and bit-identical subgroup/shared
trajectories and learned weights on a graph with reversed edge order. The local
llvmpipe driver exercises 8-lane subgroups; physical 32/64-lane GPU validation
is still needed. Khronos validation layers are not installed in this environment.

Both before/after runs used the previous incoming-cache baseline and the final
implementation respectively, .NET Release, the same fresh default graph/seed,
120 frames, headless operation and `--no-save`:

```bash
dotnet src/ArcadeLab/bin/Release/net10.0/ArcadeLab.dll \
  --train --new --headless --no-save --frames 120 --checkpoint /tmp/pc2-unused
```

| Measurement | Previous implementation | Device-local / specialized / subgroup |
| --- | ---: | ---: |
| Mean compute time | 119.72 ms/frame | 107.92 ms/frame |
| Compute FPS (1000 / mean) | 8.35 | 9.27 |
| Recent p95 | 123.40 ms | 107.77 ms |
| Main error sum, frame 120 | 437974.390126 | 437974.390126 |
| Free reward error sum, frame 120 | 459239.484441 | 459239.484441 |
| Teaching error sum, frame 120 | 466892.768065 | 466892.768065 |

This software-Vulkan comparison shows 9.9% less compute time and 10.9% higher
throughput. All emitted neural and sensation/control diagnostic records matched
exactly. Staged VRAM placement cannot be benchmarked meaningfully on llvmpipe,
which has no separate dedicated VRAM. Hardware FPS and graphical responsiveness
remain unmeasured because the user's physical GPU is not exposed here. These
numbers exclude rendering and include first-frame learning overhead.

Raw logs: [before](predictive-device-local-before.log),
[after](predictive-device-local-after.log).

For a physical GPU comparison, use `--list-devices`, confirm that startup names
the intended GPU and device-local weights, then repeat the command with
`--device INDEX --frames 600`. Use the same graph/seed or checkpoint for both
versions and keep `--no-save`. Run the viewer separately to measure displayed FPS;
`--fps` controls pacing and does not change the cost of a neural update.
