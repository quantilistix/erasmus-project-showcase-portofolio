# Catch proximity reward and persistent progress

Updated on 25 October 2026.

Historical proximity-only result: the saved reward model now also receives direct vision and executed controls. See [the latest results](VISUAL_CONTROLS_REWARD.md).

Catch now teaches its reward head to produce pleasure from **current horizontal paddle/object alignment**, including when the paddle holds still:

```
pleasureTarget = clamp(1 - horizontalDistance / 19, 0, 1)
```

A successful catch overrides this to pleasure 1/pain 0; a miss overrides it to pleasure 0/pain 1. Targets are captured before a new falling object spawns. Other nonterminal pain retains the signed environment penalty. Distance is an environment-only teaching label; the smaller network still receives only sampled main-network neurons. No coordinate, distance or target is added to the agent's sensory inputs.

## Training performed

Loaded the user's saved Catch session after 10,704 main updates and 2,000 original reward updates. Preserved the existing main graph, every stored main weight and bias, and main plasticity.

Performed 6,000 reward-only proximity calibration steps, then another 6,000 with standardized probe activations to improve the close/far distinction. Normalization statistics are fitted during the first 256 calibration frames, then held fixed. Replay stores raw neuron samples and applies the same transform at training time. Means, variance accumulators, scales and fitting count persist in `session.json`, including across interrupted calibration.

The final session has:

- **22,704 total frames**.
- **10,704 main updates**, unchanged throughout reward-only recalibration.
- **14,000 reward updates**, including the original 2,000 and 12,000 proximity updates.
- A frozen reward head with all edge plasticities zero and frozen biases.

The final normalized calibration took 22.61 seconds on Intel Arc, averaging 3.77 ms per calibration frame. It excludes main optimization intentionally. [Training log](proximity-reward-training-normalized.log).

`sha256sum -c artifacts/proximity-main-before.sha256` reports **OK** after both recalibrations and evaluation: the main-network binary is byte-for-byte identical to the pre-recalibration file. The original reward head and session metadata were retained under `realtime/catch/before-proximity/` for comparison.

## Held-out check

Evaluated without updating or saving either network, on a separate game seed (44001), fixed main gain one (the calibration condition), and the saved frozen head. An external evaluation harness drove raw buttons; neither model received coordinates.

A 2,000-frame alternating left/right sweep provided 693 near-aligned and 224 far-aligned nonterminal frames. Near means target pleasure ≥ 0.8; far means ≤ 0.3.

| Measure | Retrained head |
| --- | ---: |
| Mean predicted pleasure, near | 0.583 |
| Mean predicted pleasure, far | 0.543 |
| Pleasure mean squared error | 0.123 |
| Previous head's MSE on the same features/targets | 0.293 |
| Pleasure/target correlation | 0.172 |

The approximation is still noisy: it has a modest positive close/far difference rather than a precise proximity estimate. A separate random-walk check gave near 0.662/far 0.438, but contained only 15 far frames, so that estimate is less informative. These checks do not establish improved catching performance or a guarantee under a changing main representation and pleasure gain.

[Repeated-sweep evaluation](proximity-reward-evaluation-sweep.json), [random-walk evaluation](proximity-reward-evaluation-randomwalk.json).

## Saving and resuming

`realtime` now **loads an existing checkpoint automatically**. An explicit `--new` is required to intentionally create a fresh network at an existing path. `--resume` remains an optional explicit requirement that a checkpoint exist.

Realtime saves every **30 seconds**, on Escape/window close, and on Ctrl+C. `--autosave-seconds N` changes the interval; zero disables periodic saving; `--no-save` disables periodic and exit saves. Force-killing a process can lose work since the last completed checkpoint.

Files in `artifacts/realtime/catch/`:

- `main.ngb`: complete main graph, weights, biases and fixed plasticity.
- `reward.json`: reward head, plasticity and frozen state.
- `session.json`: saved configuration, probe IDs, normalization, frame/update counters, calibration end and whether calibration is reward-only.

Restarting restores these models and counters. The game episode, transient neural activations, random streams and replay restart; this is not exact simulation-state recovery. Ordinary `train` now also loads existing model files by default, saves its best model every ten episodes, and requires `--new` for intentional reset.

Persistence was exercised in separate CLI launches: one saved frame 4; the next loaded that model and advanced to frame 8, with periodic autosaves at frames 5, 6 and 8. A separate headless run received SIGINT and cleanly saved frame 9,400 with periodic saving disabled. Tests also cover saving halfway through reward-only recalibration and completing only its remaining frames after loading.

**7,821 assertions passed**, including proximity monotonicity, real catch/miss target overrides before respawn, unchanged main weights during reward-only calibration, refreezing, normalization persistence, and the previous GPU/gradient/pain-isolation checks. [Validation log](proximity-validation.log). A 30-frame viewer check automatically loaded the updated checkpoint and completed with Vulkan validation enabled and no errors; it used `--no-save`. [Viewer log](proximity-viewer-validation.log).

Run without resetting progress:

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch
```

Explicitly recalibrate the reward head again, preserving main progress:

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --recalibrate-reward --calibration-frames 6000 --headless --frames 6000
```
