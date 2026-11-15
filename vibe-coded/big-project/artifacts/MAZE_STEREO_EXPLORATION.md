Historical results below predate independent threshold controls and the stronger exploration schedule. See [current results](THRESHOLD_EXPLORATION.md). The original reward evaluation files are archived in `maze/before-threshold-controls/`.

# Stereo audio, exploration reward and stationary punishment

The latest reward rules remove all penalties for revisiting cells. A previously visited cell supplies no exploration reward but remains safe to traverse. Training credits **+0.30 once for each newly entered cell**, excluding initial spawn, per episode.

Missed-shot pain is now **0.65** per resolved player projectile, increased from 0.15. A projectile must actually miss; hits, shots still in flight, enemy bullets and empty-magazine attempts are excluded.

Staying punishment depends on physical displacement, not visitation. An anchor follows the agent only after it moves at least 0.65 tile. After 45 physics ticks without that displacement, pain ramps over 90 more ticks to `0.10 * gameSpeed` each frame. At default half speed this begins after about 1.5 seconds and reaches 0.05/frame after 4.5 seconds. Moving meaningfully clears it, including movement through old cells. Turning, firing, jittering in the small spot or pushing against a wall do not clear it. The time-scaled continuous cost is much stronger in aggregate than the previous sparse revisit pulse.

## Training and inference

Training applies exact ideal pleasure minus ideal pain, plus primitive healing/damage/low-health feedback, to policy learning. The same ideal channels supervise the reward translator. Ideal channels also drive training gain and learned-pain inputs; weak translator predictions do not weaken the policy's teaching signal. White bar ticks and the window title distinguish ideal/applied values from learned predictions.

The translator receives raw visual history, stereo sound/FFT, controls, temporal perceptual context and sampled neurons. Probes are captured before the current action/feedback so that the current ideal label cannot be read back from its own injected pain signal. Eight extra temporal features summarize visual changes, perceived translation, recent controls and visual drift. They use no world position, map visits or teaching labels. Oversampled exploration/miss/stationary examples are downweighted to reduce false rewards/punishments on neutral frames.

Play freezes the translator and keeps main learning active. It uses predicted pleasure/pain plus primitive senses; ideal exploration, missed-shot and staying labels are not calculated in ordinary play.

## Stereo

Each sound event has a facing-relative pan. Enemy footsteps/shots, enemy hit sounds and projectile impacts have directional placement; self-generated sounds are centered. Equal-power gains feed independent left/right PCM channels and FFTs to both networks. Playback uses interleaved 48 kHz stereo; neural audio is 64 PCM samples and 32 FFT bins per ear at 3,840 Hz. Sine voices retain zero-pressure starts/ends and smooth envelopes. Cyan plots represent the left ear and pink plots the right ear.

Legacy model ports are expanded with zero new connection weights, preserving prior weights and biases. The main graph now has 3,248,640 stored connections and 10,596 physical nonnegative rate neurons. Reward input evidence is 1,266 logical values. Version-2 session saves still load version-1 snapshots. The prior checkpoint was backed up under `artifacts/maze/before-exploration-stereo/`.

## Training performed

A corrected **4,320-frame reward-only calibration** preserved all main-network parameters. Then **3,600 frames trained both networks** using exact new ideal signals and stereo inputs. The saved checkpoint now contains:

- Frames/reward updates: **21,120**.
- Main updates: **16,800**.
- Miss examples: **98 cumulative**, exploration credits: **43**, stationary frames: **1,997**.
- Policy-training mean: **16.28 ms/frame**, recent p95 **17.14 ms**, Intel Arc MTL.

The final viewer passed Vulkan validation for 90 play frames with continuous stereo audio routed to a silent device: mean learning time 15.38 ms/frame, total viewer time 2.31 seconds. No test updates were saved. Checksums of the trained files remained unchanged.

## Prediction check and limitations

Held-out scripted trajectories on separate generated layouts supplied a fixed **249-sample sensory-evidence dataset**. It contains 149 neutral/revisit samples, 79 stationary samples, nine misses and twelve exploration events. Diagnostic teacher labels were used only to measure predictions, without learning on those samples or feeding them to ordinary play. Before/after predictions use identical cached evidence to isolate translator changes. The dataset uses the original migrated policy's probe features; after policy learning, representations can drift.

| Held-out group | Ideal mean | Before prediction | Final prediction |
| --- | ---: | ---: | ---: |
| Miss pain, including any simultaneous staying pain | 0.670 | 0.046 | 0.254 |
| New-cell pleasure | 0.300 | 0.004 | 0.061 |
| Stationary pain | 0.029 | 0.008 | 0.063 |
| Neutral/revisit pleasure | 0 | 0.007 | 0.039 |
| Neutral/revisit pain | 0 | 0.004 | 0.050 |

Miss and exploration predictions increased and their respective mean-squared errors decreased, but the translator remains inaccurate: it underestimates these positive/event targets and produces false rewards/pain on neutral examples. Stationary prediction error also remains above the old near-zero baseline. These results do not establish reliable novelty recognition, absence of reward hacking or improved maze performance. Exact ideal rules are guaranteed by the training path; learned play-time rewards remain approximations under partial observation.

## Verification

Release build and **10,184 assertions passed** with hardware Vulkan validation. Checks cover no penalty on revisits, one-time exploration credit, sustained/ramping stationary pain, blocking/turning behavior, exact ideal policy rewards, stereo channel routing, facing-relative sound, interleaved/split waveform equality, legacy weight preservation, checkpoint resumes and frozen-head realtime play learning.

Silent-driver and actual audio-device tests reported continuous stereo playback with zero OpenAL errors/underruns across a deliberate gap in game submissions.

Evidence: `maze-stereo-exploration-validation.log`, `maze-stereo-audio-validation.log`, `maze-stereo-audio-device-validation.log`, `maze-behavior-calibration.json`, `maze-behavior-after-policy-training.json`, `maze-stereo-exploration-training.log`, `maze-stereo-viewer-validation.log`, and `maze-stereo-exploration-saved.sha256`.
