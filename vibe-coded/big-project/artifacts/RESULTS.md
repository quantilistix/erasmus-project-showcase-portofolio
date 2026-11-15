# Verified results — 25 October 2026

Hardware execution used **Intel(R) Arc(tm) Graphics (MTL)**, reported by Vulkan as an integrated GPU. Inside the restricted filesystem sandbox only Mesa llvmpipe was visible; GPU measurements and desktop smoke tests were run with hardware/display access.

## Backend benchmark

Release build, .NET 10, 963 inputs, 32 hidden nodes, 16 linear outputs, 46,736 edges, two synchronous steps, batch size 32. Five warm-up calls, then 100 measured calls. Timing includes synchronous submission, fence wait, host inputs and prediction/loss readback. Training retains model weights on the GPU. CPU uses the allocation-free reference implementation; timings are illustrative laptop measurements, not guarantees.

| Backend | Prediction ms/batch | Training ms/batch | Training samples/s |
| --- | ---: | ---: | ---: |
| CPU reference | 4.326 | 10.099 | 3,169 |
| Intel Arc Vulkan | 1.854 | 1.448 | 22,100 |

Vulkan was approximately **2.3× faster for prediction and 7.0× faster for training** in this workload. This compares the supplied backends, not an optimized BLAS or commercial machine-learning framework. See [benchmark.log](benchmark.log). Larger topologies, sparse connectivity, single-sample latency and discrete-GPU memory placement can change the comparison.

## Training and autonomous play

Final checkpoints were trained from seed 42 for **300 episodes per game**, 96 ticks per episode, on Intel Arc. Each model uses the raw sensory contract described in the root README, with fixed plasticity one on every edge. Individual custom plasticity values are covered by numerical and CPU/GPU parity checks. The training process never modifies plasticity.

A separate validation seed (17001, ten episodes every ten training episodes) selects the saved policy. Final evaluation below uses **50 episodes, seed 9001**, with exploration disabled. Random uses the same environment seed and samples uniformly from the three raw button actions. Higher mean episode reward is better; these are shaped rewards, not win percentages.

| Game | Saved neural policy | Random controls |
| --- | ---: | ---: |
| Catch | -0.225 | -2.095 |
| Dodge | 3.356 | 2.095 |
| Rally | -1.671 | -1.571 |

Catch and Dodge improved over random controls. Rally remains weaker and did not outperform random on this evaluation. These checkpoints demonstrate actual learning and inference, with no claim that all games are mastered. The source supports longer training, other topologies and reward changes. Validation selection also has sampling noise; training longer does not guarantee a stronger final policy.

Final training took approximately 67 s for Catch, 67 s for Dodge and 71 s for Rally, including periodic validation. See [training-final.log](training-final.log) and [evaluation.log](evaluation.log). Earlier `training.log` and `training-intel.log` record exploratory runs before the final three-action-head policy; their older 14-output models were replaced by the final 16-output checkpoints.

## Correctness and rendering

- Release solution build succeeded with warnings treated as errors.
- **5,215 assertions passed**: finite-difference BPTT on cycles/self-edges/skips/output feedback; CPU/Vulkan prediction, loss and updates; batches 1, 17 and 65; zero/fractional/amplified fixed plasticity; checkpoint round trips; clipping/bias semantics; known-tone FFT; deterministic games; raw vision and PCM contracts.
- The complete checks passed on Intel Arc with `VK_LAYER_KHRONOS_validation` and on llvmpipe. [Validation log](validation.log).
- A 120-frame GLFW/Vulkan viewer smoke test ran with Vulkan validation enabled and the optional OpenAL audio path enabled. No Vulkan validation errors appeared. [Viewer log](viewer-validation.log). Interactive resize/minimization controls are implemented but were not exhaustively automated.

Reproduce:

```bash
dotnet build -c Release -m:1
VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation dotnet run --project tests/NeuralGraph.Checks -c Release --no-build -- --require-vulkan
dotnet run --project src/ArcadeLab -c Release --no-build -- benchmark --batch 32 --iterations 100
dotnet run --project src/ArcadeLab -c Release --no-build -- evaluate --game all --episodes 50 --seed 9001 --backend vulkan
dotnet run --project src/ArcadeLab -c Release --no-build -- play --game catch --backend vulkan
```
