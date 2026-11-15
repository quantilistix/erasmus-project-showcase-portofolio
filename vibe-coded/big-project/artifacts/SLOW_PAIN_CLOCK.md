# Slower pain signals and sine clock — 26 October 2026

Implemented the revised request: pain samples now persist across frames, and the main network receives one deterministic oscillator input. No adversarial error-maximization pain mode is included.

## Timing

Default settings:

- **Pain holding interval: 12 simulation frames**, giving 5 Hz random-pattern refresh at the default 60-frame/s simulation pace.
- **Clock frequency: 1 Hz**, represented by one input with value `0.5 + 0.5 * sin(2π * hertz * frame / fps)`.

The clock ranges from zero to one and completes one cycle every 60 simulation frames at the default settings. It pauses when the simulation is paused, continues across episodes, and resumes its phase from the saved global frame count. Headless simulation uses virtual time, not wall time, so accelerating training does not change the relationship between clock cycles and game steps.

Learned and primitive pain have separate sample-and-hold banks. Repeated predictions during a frame reuse their samples. Positive pain-level fluctuations do not force learned-pain resampling inside the holding interval. A zero level gates its bank off; learned pain can resume the held pattern without generating new high-frequency randomness. A new direct injury may refresh the primitive bank immediately. The random-pattern carrier is slower; the actual pain gate can still respond to events.

All visual, PCM, FFT and control values remain untouched. The clock is an explicitly requested deterministic input. Bernoulli probability one still produces all-one pain samples; changing the holding interval does not change that property.

Configuration:

```bash
dotnet run --project src/ArcadeLab -c Release -- realtime --game catch --pain-hold-frames 12 --clock-hz 1
```

For a slower experiment, use `--pain-hold-frames 30 --clock-hz 0.5`. Existing checkpoints load automatically; these signal settings can be overridden on resumed sessions and are saved on normal exit/autosave.

## Preserving training progress

The main input count increases from 995 to **996**, its node count from 5,107 to **5,108**, and its connection count from 3,147,264 to **3,147,776** for the default model.

Older main checkpoints migrate in memory when loaded:

1. Add one clamped input after the pain banks.
2. Shift existing hidden/output node IDs by one.
3. Preserve every existing edge weight, bias and fixed plasticity.
4. Add 512 zero-weight, trainable clock connections to the first hidden layer.
5. Remap saved reward probes to the same physical hidden neurons.

The zero new weights preserve the existing prediction function for every clock phase. Subsequent ordinary weight updates can learn nonzero clock weights. The reward head remains unchanged and frozen. Session format **5** stores the holding interval, oscillator frequency, current held samples/refresh deadlines and current modulation levels; the stored frame counter determines clock phase. Loading an already migrated checkpoint does not add another clock input.

The tests below used `--no-save` for the user's real checkpoint. The original saved main and reward files were not overwritten. Normal user runs will persist the expanded model and timing state via the existing autosave/exit path. The game episode and random-generator stream still restart on load, so this is not exact simulation-state replay.

## Verification

Release build succeeded with warnings treated as errors. **7,886 assertions passed** on Intel Arc and on llvmpipe, including:

- Samples remain identical through frames 0–11 without further random draws and refresh at frame 12.
- Restored samples retain their remaining holding interval.
- Learned-pain zero crossings do not cause rapid random redraws.
- Primitive injury can fire immediately without resampling the learned bank.
- Correct sine values at frames 0, 15, 30 and 45, and a full 60-frame period.
- Clock migration preserves all existing weights, biases and per-edge plasticities.
- Zero clock weights preserve predictions at phases 0, 0.5 and 1.
- Ordinary learning updates new clock connections; migrated GPU predictions match the CPU reference.
- Reward probes/head and main update counters survive migration; clock insertion happens once.
- Timing phase and modulation values survive checkpoint save/load.
- Existing gradient, GPU parity, reward-input, frozen-head, persistence and pain-isolation checks continue passing.

[Validation log](slow-pain-clock-validation.log).

A full-sized saved Catch network completed 180 uncapped learning frames on Intel Arc using the default timing, with `--no-save`: mean **6.24 ms/frame**, recent p95 **11.31 ms**, and no reward updates. [Smoke log](slow-pain-clock-smoke.log). This verifies operation, not improved pain avoidance or catching quality; no controlled gameplay comparison has been performed.

The viewer was also checked for 60 frames with Vulkan validation, overridden 30-frame holding and a 0.5 Hz clock, without saving. [Viewer log](slow-pain-clock-viewer-validation.log).
