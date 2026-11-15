# Quadratic catch reward — 26 October 2026

The reward head is trained with a **quadratic proximity target** rather than the previous linear target:

```
a = clamp(1 - horizontalDistance / maximumDistance, 0, 1)
proximityPleasure = a * a
maximumDistance = 19
```

| Horizontal distance / maximum | Proximity target |
| --- | ---: |
| 1 (maximally far) | 0 |
| 0.75 | 0.0625 |
| 0.5 | 0.25 |
| 0.25 | 0.5625 |
| 0 (directly underneath) | 1 |

A **successful catch adds a bonus of 1**, producing a pleasure target of **2**, with pain zero. A miss produces pleasure zero and pain one. Outcome targets are recorded before spawning the next object. Thus proximity peaks at one when aligned, while actually catching is worth more than alignment alone. The clamp occurs before squaring so out-of-range distances cannot produce positive reward again.

The neural readout predicts this target; it is not a deterministic distance calculator. It still receives the sampled hidden neurons, raw visual history and executed controls. No distance/coordinate is inserted into either network's inputs.

Catch pleasure now has range [0,2]; pain remains [0,1]. Other game ranges are unchanged. The green bar uses the complete Catch range, so proximity target one is half its width and catch target two is full width. At default pleasure gain 0.25, maximum effective main edge gain is 1.5. Reward-head biases and plasticities freeze as before after calibration.

## Training completed

Loaded the saved Catch session and performed **12,000 reward-only updates** in two passes. The first 6,000 steps retained the earlier exploration scheme and showed poor held-out curve fit. Reward-only calibration was then changed to sweep left/right every 31 frames with 10% neutral actions, independently of the potentially biased main policy. The second 6,000 steps covered paddle positions across the arena and improved the held-out fit.

The final pass took **46.65 seconds**, averaging **7.71 ms/calibration frame**. [Final training log](quadratic-reward-training-sweep.log), [initial pass](quadratic-reward-training-initial.log).

The final checkpoint at `artifacts/realtime/catch` contains:

- **40,704 total frames**.
- **10,704 main updates**, unchanged during both reward-only passes.
- **32,000 reward updates**, up from 20,000 before this task.
- A frozen reward head with zero edge plasticities and frozen biases.

The stored source main network had not yet persisted the prior clock-input update. Loading it added that one input and zero-weight clock connections; all previous weights, biases and fixed plasticities were preserved, with existing node/edge IDs remapped appropriately. The new clock weights remained zero during reward-only training. This necessary topology expansion changes the binary checksum, but parameter-by-parameter comparison confirms every existing value is unchanged. [Preservation check](quadratic-main-preservation.json).

The full pre-change main model, reward head and session metadata are backed up in `artifacts/realtime/catch/before-quadratic/`.

## Held-out check

Ran 2,000 separate-seed frames with gain one, the saved slow-pain timing and sine clock, and 30-frame control sweeps with occasional neutral actions. Neither model was updated or saved. The old frozen linear-target head and new head were evaluated on the same evidence against the new quadratic/catch-bonus targets.

| Metric | New head |
| --- | ---: |
| Pleasure mean squared error | 0.12886 |
| Previous head MSE against the new targets | 0.17176 |
| Pleasure/target correlation | 0.394 |
| Mean pleasure on 21 catches | 1.439 |
| Mean pleasure on 403 near-aligned nonterminal frames | 0.660 |
| Mean pleasure on 607 far-aligned nonterminal frames | 0.535 |
| Mean pleasure on 62 misses | 0.055 |

Prediction error fell by about 25% relative to the old head on this test. The learned curve is still approximate, particularly for proximity; these numbers do not imply exact reward values on every frame, elimination of reward hacking or improved game policy. Near means target ≥ 0.8; far means target ≤ 0.3. Catch/miss frames are reported separately.

[Evaluation data](quadratic-reward-evaluation.json). Reproduce:

```bash
dotnet run --project tests/NeuralGraph.Checks -c Release -- --evaluate-reward --baseline artifacts/realtime/catch/before-quadratic
```

## Verification

Release build succeeded. **7,899 assertions passed**, including:

- Exact quadratic endpoints, midpoint and quarter-distance values.
- Monotonic proximity and clamping before squaring.
- A successful catch earns more than maximum proximity.
- Catch/miss integration targets are captured before respawn.
- Catch readouts accept pleasure above one, including checkpoint save/load.
- Reward-only calibration explores both directions and traverses the arena while preserving all main parameters.
- Existing CPU/GPU parity, fixed plasticity, frozen head, timing, direct vision/control and persistence checks continue passing.

[Validation log](quadratic-validation.log). A 30-frame viewer check with Vulkan validation completed using `--no-save`, preserving saved main progress. [Viewer log](quadratic-viewer-validation.log).

## Run

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch
```

The trained checkpoint loads automatically. To continue reward-only training with the same quadratic target:

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --recalibrate-reward --calibration-frames 6000 --headless --frames 6000
```

Use Escape to save and close an existing viewer before retraining its checkpoint. Avoid `--new` to retain existing training progress.
