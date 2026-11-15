# Realtime experiment — 25 October 2026

This is the historical baseline measurement before Catch proximity supervision. The saved Catch session has since been recalibrated; see [the current proximity results](PROXIMITY_TRAINING.md).

Implemented the requested large layered main network, smaller trained/frozen reward network, pleasure amplification and two independent stochastic pain senses. The ordinary offline trained game models remain separate.

## Model and update rules

- Main: 995 inputs, eight hidden layers × 512 neurons, 16 outputs; **5,107 neurons, 3,147,264 connections**. Adjacent layers plus 1→3, 3→5, 5→7 and 7→9 skips; input is layer 0, output is layer 9.
- Inputs: original raw frame history, PCM, FFT and actual prior controls, then **16 learned-pain inputs and 16 primitive-pain inputs**. Only the two dedicated pain banks receive injected randomness.
- Head: a fixed random sample of 128 main hidden neurons, 32 hidden neurons and two bounded readout outputs, pleasure and pain. This small network runs on the CPU; the main network runs in Vulkan.
- Calibration: 2,000 frames of supervised reward-head learning from signed game rewards. Main learning also proceeds each frame. At the exact transition, all reward plasticity becomes zero and all reward parameters, including biases, freeze.
- Realtime: main learning continues each active frame with learned pleasure minus pain as reward. Pleasure multiplies main effective weights by `1 + 0.25 * previousPleasure`. The reward head is not amplified and never trained again.
- Learned pain drives Bernoulli firing of its own bank. A direct failure/injury signal drives the independent primitive bank, without needing the head. Injury means game reward ≤ -0.5; its intensity decays by 0.85 each frame. Raw senses/controls are copied exactly.
- Every frame performs prediction, one main optimization step and a post-update evaluation to refresh activations. Pain/pleasure modulation advances causally on the following frame.

## Intel Arc measurement

Release .NET 10 build, **Intel(R) Arc(tm) Graphics (MTL)**, full-sized network, one training sample per active frame. Ran 2,000 calibration frames then 600 frozen-head frames, uncapped and without rendering. Timings include game/sensor updates, reward-head CPU work, GPU submissions, backward/update and refreshed activations, and synchronous waits. Shader/device initialization and checkpoint I/O are outside the per-frame timing.

| Phase | Frames | Mean learning time | Approximate compute throughput | Recent p95 |
| --- | ---: | ---: | ---: | ---: |
| Calibration | 2,000 | 6.20 ms/frame | 161 frames/s | 6.97 ms |
| Frozen-head realtime | 600 | **5.54 ms/frame** | **180 frames/s** | **6.25 ms** |

The full 2,600-frame loop completed in 15.74 seconds, excluding initialization and saving. Percentiles cover the most recent 512 frames of each phase. See [realtime-training.log](realtime-training.log). These measurements leave headroom within a 16.67 ms (60 FPS) learning budget on the tested laptop; rendering, other workloads and validation layers affect actual displayed FPS.

The session produced by this baseline run had **2,600 main updates, 2,000 reward updates**, a frozen reward model and zero on all reward-edge plasticities. Main plasticity remains one. The compact main checkpoint is approximately 49 MB; the reward head and its fixed probe IDs are saved alongside it.

## Verification

**7,725 assertions passed** on Intel Arc with Vulkan validation enabled, and on llvmpipe:

- Adjacent and odd-to-odd topology connections, including an odd output layer.
- Numerical gradients for general cyclic graphs and CPU/Vulkan agreement.
- Single-pass layered predictions, losses and updates against both the CPU layered reference and the original sufficiently unrolled execution.
- Gain values 0.8, 1 and 1.25; per-neuron activation readback.
- Fractional, zero and amplified fixed plasticity; explicit freezing of both weights and biases; frozen binary round trip.
- Independent learned and primitive pain banks, exact copying of senses/controls, direct injury triggering and decay.
- Reward calibration ending at the specified frame; no reward updates afterward; ongoing main updates and refreshed activations.
- Realtime save/load preserving main weights/plasticity, reward frozen parameters, sampled-neuron IDs and counters.

See [realtime-validation.log](realtime-validation.log).

A resumed full-size session completed **120 displayed learning frames** with `VK_LAYER_KHRONOS_validation`, no Vulkan validation messages and no checkpoint overwrite. It took 2.18 seconds including window setup and pacing; the learning portion averaged 11.21 ms/frame with validation enabled. This exercised the Vulkan renderer, hidden activation heatmap and phase/pleasure/pain display. See [realtime-viewer-validation.log](realtime-viewer-validation.log).

These checks establish implementation correctness, freezing and speed. They do **not** establish that this reward architecture improves game performance. A frozen readout samples a representation that continues changing; outputs can drift in meaning or saturate. No controlled gameplay ablation was run for this new experiment.

## Run

```bash
# Resume the supplied calibrated network and learn every active displayed frame.
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --resume

# New calibration followed automatically by live learning.
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --new

# Reproduce the headless measurement without overwriting the saved session.
dotnet run --project src/ArcadeLab -c Release -- realtime --new --headless --frames 2600 --calibration-frames 2000 --no-save
```

The root [README](../README.md) documents sizes, learning rates, checkpoint paths, pain settings and controls. `--resume` restores the saved configuration and starts a new environment episode; it does not restore an exact random-generator or simulation-state snapshot.
