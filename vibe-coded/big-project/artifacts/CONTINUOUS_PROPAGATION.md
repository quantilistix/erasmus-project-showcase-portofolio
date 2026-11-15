# Persistent neural propagation

The live maze networks now keep their activation state across frames. Each simulation frame advances each network by two synchronous connection hops by default (`--propagation-steps`, range 1–32). Inputs are clamped to the current sensory values; non-input neurons read the preceding tick's activities. Signals therefore travel through layers over time rather than every inference starting from an empty graph. Cycles/self-connections are supported by the standalone continuous API.

The CPU backend uses two persistent rate arrays. The GPU uses a new cooperative `continuous.comp` kernel and separate GPU-resident state banks. Neither overwrites source state while calculating the same tick. Both retain nonnegative physical activation pairs. Weights remain shared with the optimizer, so weight changes affect the following ticks while current activations remain intact.

The library exposes `Advance`, `AdvanceEncoded`, `PropagationSteps`, `ReadStateActivations`, `RestoreStateActivations` and explicit `ResetState`. Existing stateless `Predict`/`Train` APIs remain for learning and batch evaluation. Their scratch activations are separate, so clearing those working buffers cannot erase or advance the live stream.

## Game integration

Only the current RGB first-person view is supplied. The previous image and external 64-value smoothed hidden trace have been removed from the input data. Their old ports stay zero to retain checkpoint topology/weights. The reward feature processor still maintains internal perceptual summaries; it does not pass a second image to either network.

Both live networks advance once per game frame. Post-action evaluation, replay sampling and weight updates do not advance them again. The activity display reads the persistent state rather than whichever training example was evaluated last. The previous-view panel is removed; the viewer labels the remaining sensor line CURRENT VIEW and identifies the retained neuron state.

Maze changes, manual maze resets and mode switches do not clear neuron activities. Pausing simulation also pauses propagation. State evolves in discrete simulation ticks rather than an unbounded background GPU loop.

Version-5 session snapshots store both live activation arrays and propagation counters. Loading resumes those values; legacy checkpoints lacking activities initialize state once at startup. Exact game physics/RNG is still not serialized, so loading starts a new maze while neural state resumes.

## Verification

Release build: zero warnings/errors. The complete suite passed **10,802 assertions** with hardware Vulkan validation, including **587 continuous-propagation assertions**:

- A one-tick input impulse reaches a three-edge output after the input has returned to zero.
- Intermediate neurons carry the impulse forward without any previous-image input.
- CPU/GPU activities agree for cyclic graphs, feedback from outputs, self-connections and changing input streams.
- Stateless prediction and weight learning leave live activations and counters unchanged.
- Explicit resets clear state; snapshot restore recovers state exactly.
- Each game frame advances each live network exactly once.
- Game resets retain activity, and checkpoint saves/restores preserve activity and propagation counters.
- Play keeps learning main weights while reward parameters remain frozen and both activation streams evolve.

The 120-frame Intel Arc MTL viewer check passed Vulkan validation with `--no-save`: mean learning-frame time **15.63 ms**, recent p95 **17.46 ms**, total viewer time **2.90 seconds**. Saved-file SHA-256 checks confirmed no trained files were modified by the test.

Evidence: `continuous-propagation-validation.log`, `continuous-propagation-viewer-validation.log`, `continuous-propagation-saved.sha256`.

## Learning limitation

This change implements persistent forward dynamics. It does not replace the existing reward-driven Q-learning and auxiliary prediction objective with predictive-coding error-neuron inference. Training uses a stateless feedforward/unrolled surrogate in isolated working buffers; temporal backpropagation through the live activation history is not implemented. The tests establish propagation/state isolation, not improved gameplay or equivalence to a biological/predictive-coding learning algorithm.
