# Reward model with direct vision and executed controls

Implemented and trained on 25 October 2026.

Historical linear-target result: the saved Catch head now uses a quadratic proximity target and catch bonus. See [current training](QUADRATIC_REWARD.md).

The user's reward model now receives **995 inputs**, in this order:

1. 128 fixed sampled main-network hidden neurons, normalized using saved calibration statistics.
2. 864 unmodified visual values: the current and previous 24×18 grayscale frames.
3. Three one-hot values for the actual executed raw control action: neutral, left or right.

The visual/control evidence bypasses the main network. Controls come from the exact action passed to `Game.Step`, including manual/exploratory actions, rather than predicted Q values or a hypothetical proposed action. Replay stores the matching neural probes, image history, executed action and target together. No audio, pain noise or coordinates are substituted into the visual/control channels. Both dedicated pain input banks remain unchanged.

The smaller network retains 32 hidden neurons and two output neurons (pleasure/pain). Supervision remains Catch horizontal proximity, with catch/miss outcome overrides. During live operation its weights and biases remain frozen and all its connection plasticities are zero.

## Checkpoint migration and training

Loaded the saved session at frame 22,704, with **10,704 main updates and 14,000 reward updates**. The old neuron-only reward graph was expanded while preserving all its weights/biases and fixed probe IDs. Added input connections initially had weight zero, preserving the old prediction function before calibration. Legacy files automatically schedule reward-only calibration so the new inputs are trained before live main-network learning resumes.

Completed **6,000 reward-only calibration frames**, using the Intel Arc main backend and the CPU reward backend. Main gain stayed at one and main weights stayed fixed during this phase. The new head was then frozen and saved.

Final saved state:

- Session format **4**.
- **28,704 total frames**.
- **10,704 main updates**, unchanged.
- **20,000 reward updates**.
- Expanded 995-input reward model, frozen weights and biases, all reward plasticities zero.

`sha256sum -c artifacts/visual-controls-main-before.sha256` returns **OK**: the main network binary remains byte-for-byte identical to the saved network before this change. The previous reward model and session metadata are backed up under `artifacts/realtime/catch/before-visual-controls/`.

Calibration took **46.41 seconds**, including periodic checkpointing, with mean learning computation **7.68 ms/frame**. [Training log](visual-controls-reward-training.log).

## Held-out measurement

Compared the old and new frozen reward heads on the same main-network activations, visual states and executed actions, without updating or saving either model. Used a separate game seed 44001 and 2,000 frames of alternating 30-frame left/right sweeps with 10% neutral actions. Main gain was held at one, matching calibration. The old model used its original saved normalization; the new model used its own.

| Metric | Old neuron-only head | New visual/control head |
| --- | ---: | ---: |
| Pleasure mean squared error | 0.12246 | 0.11467 |

The new head's pleasure/target correlation was **0.192**. Mean predicted pleasure was **0.643** for 693 near-aligned nonterminal frames (target ≥ 0.8), versus **0.595** for 224 far-aligned frames (target ≤ 0.3).

This is a modest error improvement and a weak close/far distinction. It does **not** prove that reward hacking is fixed. The main network can still influence the retained hidden-neuron input channels, and it can potentially exploit inaccuracies in the visual reward model through its actions. No adversarial reward-hacking elimination test or gameplay improvement claim is made.

[Evaluation data](visual-controls-reward-evaluation.json). Reproduce the read-only evaluation:

```bash
dotnet run --project tests/NeuralGraph.Checks -c Release -- --evaluate-reward --backend vulkan
```

The harness compares against `before-visual-controls/` by default, uses `--session PATH` and `--baseline PATH` for other checkpoints, and does not save any changed simulation state or weights.

## Verification

Release solution build succeeded. **7,837 assertions passed** on Intel Arc with Vulkan validation and on llvmpipe. New checks cover:

- Exact raw visual history and executed-control evidence.
- Preserving old reward predictions when expanding the graph with zero-weight added input edges.
- Nonzero training gradients reaching the added visual/control connections.
- Preserving main weights/biases, counters and sampled neurons during legacy migration.
- Scheduling reward-only supervision before live use of newly added inputs.
- Saving/resuming an upgraded calibration without repeated expansion or reset.

Existing gradient, GPU/CPU parity, frozen plasticity/bias, pain isolation, proximity and persistence checks also pass. [Validation log](visual-controls-validation.log).

A 60-frame resumed viewer test completed with Vulkan validation enabled and no validation messages. It loaded the 995-input head correctly and used `--no-save` to preserve the checkpoint. [Viewer log](visual-controls-viewer-validation.log).

## Run

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch
```

The upgraded checkpoint loads automatically. Existing periodic and exit saves retain progress. Avoid `--new` unless intentionally starting over. To deliberately retrain only the reward head again:

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --recalibrate-reward --calibration-frames 6000 --headless --frames 6000
```
