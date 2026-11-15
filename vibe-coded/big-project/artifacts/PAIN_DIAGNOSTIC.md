# Random pain diagnostic — 25 October 2026

Read-only measurement of the saved main network. No main/reward optimization or checkpoint save was performed. This diagnostic was run on the **CPU layered reference** because automatic permission review for hardware GPU access timed out; that timeout does not establish that the GPU action was unsafe.

Used 24 game states (seed 55001), gain one, three pain-signal draws per state and condition. Compared each perturbation against an all-zero-pain baseline for the same state. Visual, PCM, FFT and previous-control inputs were copied unchanged. Only learned-pain, primitive-pain or both pain banks were varied.

All variants used the **same fixed game-derived Q/reward/next-image targets**, with the clean next-state prediction supplying the bootstrap. The weights match the live learner's output loss weights. These are counterfactual diagnostic losses, not a recording of the live policy's learned-reward training loss.

| Pain banks | Level | Mean loss | Fraction of draws above baseline |
| --- | ---: | ---: | ---: |
| Neither | 0 | 0.11317 | — |
| Learned only | 0.5 | 0.15917 | 81.9% |
| Primitive only | 0.5 | 0.28332 | 95.8% |
| Both | 0.5 | 0.36119 | 94.4% |
| Both | 1 | 0.75133 | 95.8% |

Both banks at 0.5 increased the mean by about 3.2×; level one increased it by about 6.6×. Mean absolute activation change in directly connected first-layer neurons was 0.0988 and 0.1380 respectively. These measurements cover a short trajectory, not a general significance test, a global maximum or an avoidance-behavior evaluation. Some perturbations reduced rather than increased loss.

The current pain inputs use Bernoulli firing: each input is one with probability p, otherwise zero. Variance is p(1−p), highest at p=0.5. At p=1 the bank is constantly on, so its randomness disappears even though its activation shift may be large.

Prediction loss, hidden activation change and policy reward are different quantities. Increasing prediction error does not by itself guarantee that the action policy will avoid pain. Weight learning can compensate or reduce dependence on pain inputs; the action policy is trained on its reward target. No new adversarial pain mechanism or stronger pain cost has been applied pending clarification of the desired objective.

Data: [pain-random-signal-evaluation.json](pain-random-signal-evaluation.json).

Reproduce without GPU permissions:

```bash
dotnet run --project tests/NeuralGraph.Checks -c Release -- --evaluate-pain --backend cpu --frames 24
```

For a longer hardware measurement, use `--backend vulkan --frames 96` with access to a Vulkan device.


The subsequent user request selected slower sample-and-hold signals and a sine clock instead of an adversarial pain mechanism. See [the timing change](SLOW_PAIN_CLOCK.md). This artifact measures the original per-evaluation random signal, not the new held-sample policy.
