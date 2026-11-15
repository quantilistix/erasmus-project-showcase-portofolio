# Predictive coding implementation and validation

The active maze now runs the user-specified normal/error dynamics on CPU or Vulkan. Each graph node has one nonnegative normal activity and positive/negative nonnegative channels for its signed error. There are no biases, tanh functions, actor/value targets, replay, backpropagation or least-squares visual reward readout in the active path. The retired maze learner and analytic visual readout are test fixtures only. Generic legacy graph APIs remain separate library features.

## Equations and implementation

Every normal feeds its own error through fixed +1, and that error feeds the normal through fixed −1. Edge(i,j) has one stored weight for error_i → normal_j and the exactly negated reciprocal prediction normal_j → error_i. A derived outgoing-order GPU cache is refreshed from that shared parameter, never independently learned. CPU and GPU use the same projected symplectic Euler order: errors first from the preceding normals, then normals from the new errors, with sensory/teaching boundary clamps and nonnegative projection for free normals. All activity persists between frames and world resets.

Main defaults are eight activity ticks at h=0.04, followed by a local update with weight step 0.02 and base eta 0.00001. Each connection changes by weightStep × effectiveEta × fixedPlasticity × error_source × normal_destination. The reward network uses the same mechanism. Its training pass starts from free inference state, clamps its two outputs to ideal normalized labels, applies local learning, and restores the free state without undoing the learned parameters. Play skips this pass and freezes reward parameters while its activities continue. Main local plasticity remains active in both modes.

The only total-error readout is sum(error_i²) over ALL represented errors, including input and output units, with no half factor or averaging. It is statistics-only and does not participate in any update. No variational-free-energy claim is made. The viewer shows explicit normal/error pairs, exactly eight actual motor outputs, and a purple sum(E²) bar on a fixed logarithmic 0–10¹² scale.

## Confirmed pleasure/pain proposal

The user selected normalized learned signals plus primitives. Main propagation gain stays 1. Let P be applied learned/ideal pleasure divided by 256 plus the primitive pleasure trace, and N be applied learned/ideal pain plus primitive pain. Totals use the preceding feedback: ideal learned-system values in training, free inferred values in play. Both totals range from 0 to 2.

The main effective rate is baseEta × (1 + P − N), recomputed per frame without compounding into the base. The factor ranges from −1 to 3. Zero stops weight increments without stopping inference; a negative factor reverses the local product rule. Reward teaching retains its own configured base rate and is frozen in play.

The 32 drive neurons receive P + N × U_i, with U_i uniform in [0,1] and held for twelve frames. Pleasure is a constant baseline, not randomly sampled activation. Pain scales the random amplitude. No added noise enters vision, sound or other sensory inputs. The graph explicitly includes two DRIVE inputs, and the UI/console show totals, factor and effective eta. Drive sample/refresh state is saved.

## Checkpoints and measurements

The default checkpoint is artifacts/predictive-maze, independent of working directory. main.pcn and reward.pcn store topology, one weight and fixed plasticity per edge, and freeze state; maze.json stores both complete activity populations, counters, sensations, progression and drive state. The final marker is paired-error-pc-v2. V1 predictive checkpoints migrate while preserving weights and activity; retired RL checkpoints are rejected. Old maze progress was authorized to be discarded and remains isolated from this model.

The saved checkpoint has 600 active frames: an initial 500-frame PC smoke test, followed by 100 frames using the final confirmed reward mechanism. It has 600 main local-update attempts, 600 reward-teaching updates, 4,800 main free activity ticks, 4,800 reward free ticks and 4,800 teaching ticks. The main has 7,200 normal units, 14,400 error channels and 4,214,784 shared weights. The reward model has 3,402 normal units and 419,072 shared weights.

On Intel Arc MTL, the final 100-frame learning continuation averaged 42.31 ms per compute frame (~23.6 compute fps), recent p95 46.88 ms. A subsequent 90-frame play/viewer check averaged 34.52 ms (~29.0 compute fps), recent p95 41.01 ms. Startup, drawing and pacing add to these compute measurements; this is not a 60-fps guarantee.

The final training sample logged P=0.04629, N=0.12745, factor=0.91884 and effective eta=0.00000919, matching the rule. The main sum(E²) was approximately 3.02 million. Error sums grew in the smoke tests, and free reward predictions sometimes saturated their readout bounds. These runs do not establish accurate reward recognition, convergence, improved exploration or competent maze play. The selected undamped accumulating-error equations can accumulate discrepancy under persistent forcing. No damping or alternate algebraic-error model was silently substituted.

## Verification

The complete verification suite passed 13,044 assertions with Vulkan validation. This includes 938 focused predictive-library checks, 914 active-maze integration assertions and seven drive-signal assertions; the remainder cover retained generic/legacy mechanisms and game/audio/vision behavior. Focused coverage includes hand-derived signs/increments, tied weights, signed and zero effective rates, frozen/fixed plasticity, nonnegative motor outputs, recurrence/skips, CPU/GPU agreement, statistics independence, normalized totals, tonic pleasure and held pain noise, free/teaching state separation, terminal feedback, migration and exact activity checkpoint roundtrips.

The GLFW/Vulkan window was checked using a captured composed frame, including the error bar and labeled drive inputs. The read-only play run left saved model/state checksums unchanged.

Evidence: predictive-coding-build.log, predictive-coding-validation.log, predictive-coding-viewer-validation.log, predictive-coding-saved.sha256, predictive-maze-training.log, predictive-reward-modulation-training.log and predictive-reward-modulation-benchmark.log. The initial training log's obsolete save-message filenames were corrected in code; the actual predictive files are the .pcn files listed above.
