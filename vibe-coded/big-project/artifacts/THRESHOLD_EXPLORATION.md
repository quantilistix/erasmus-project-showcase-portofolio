# Independent controls and exploration rewards

The eight physical buttons press independently when their score is strictly above the fixed threshold of 0.5. Move, turn and fire can run together. Opposing directions cancel, diagonal movement is normalized, and no button is forced if every score is below threshold. The explicit Wait output has been removed from action selection. Its checkpoint port now holds the state-value baseline; the former combined ForwardFire port is reserved. Old checkpoint shapes remain loadable.

The user explicitly chose to keep the staying penalty. Physical displacement still resets the stationary timer; turning, firing and pushing into a wall do not. Revisits carry no direct penalty.

Exploration earns 3 pleasure per new cell (ten times the former 0.3), plus 1 for entering a new 5×5 region. Spawn cells/regions are excluded. Credit is paid from a finite balance, 20% of the remaining balance per frame; the terminal frame flushes the balance, subject to the combined pleasure cap of 4. Revisiting or waiting earns no additional credit. Smoothing makes evidence less dependent on a single unseen grid-boundary crossing.

During `--train`, these ideal rewards teach the main network and supervise the reward translator. During `--play`, the translator is frozen and supplies learned rewards, while the main network keeps learning; primitive sensations remain separate. No visited-cell table or ideal novelty labels enter the translator. Exploration replay examples receive full weight; neutral replay examples help limit false pleasure. Calibration uses play-like predicted feedback and verifies that main weights remain unchanged.

The main learner now uses independent button targets and a separate state-value baseline. Positive advantage reinforces held buttons; negative advantage suppresses them. Replay targets use fresh current predictions. Stateless training scratch remains isolated from persistent live activations. Training exploration samples button combinations.

## Saved training and verification

Training resumed existing weights. Original checkpoints are archived in `maze/before-threshold-controls/`. There were 6,500 normal policy/reward training frames plus reward-only calibration passes. `maze/` contains the final saved weights, counters and live neural state.

Release build and all **10,828 assertions** passed with hardware Vulkan validation, including threshold edges, simultaneous controls/physics, reward totals, revisits, staying penalty, persistence, frozen play-time reward weights and reward-dependent button-learning direction. The GUI completed a 90-frame Vulkan-validation run without reported errors. Normal training measured 23.91 ms/frame mean and 24.98 ms recent p95 on Intel Arc MTL; the viewer measured 17.58 ms/frame mean over 90 frames. These are short-run measurements, not a guaranteed frame rate.

Evidence: `threshold-exploration-build.log`, `threshold-exploration-validation.log`, `threshold-exploration-final-policy.log`, `threshold-exploration-weighted-calibration.log`, `threshold-exploration-viewer-validation.log`, `threshold-exploration-fresh-evaluation.json`, and `threshold-exploration-saved.sha256`.

## Final fresh evaluation

Saved session: 40,580 frames, 23,300 main updates and 40,580 reward updates. Six scripted trajectories used the frozen head and current live main activations, with learning disabled and teacher labels restricted to diagnostic comparison. These scripts measure translation, not autonomous game skill.

| Situation | Samples | Ideal pleasure | Predicted pleasure | Ideal pain | Predicted pain |
|---|---:|---:|---:|---:|---:|
| neutral or revisit | 119 | 0.000 | 0.048 | 0.000 | 0.013 |
| stationary | 103 | 0.000 | 0.027 | 0.032 | 0.040 |
| miss | 9 | 0.000 | 0.035 | 0.675 | 0.379 |
| exploration | 38 | 0.268 | 0.096 | 0.000 | 0.001 |

The translator still underestimates exploration and misses and can produce false rewards on neutral frames. Stronger ideal rewards and sampling are implemented, but this does not establish reliable novelty recognition, improved autonomous navigation or freedom from reward hacking. Continue `--train` for both networks; `--play` freezes the translator while continuing main-network learning.

The threshold is fixed in code at 0.5. There is no command-line or saved configuration setting; legacy checkpoint threshold fields are ignored.
