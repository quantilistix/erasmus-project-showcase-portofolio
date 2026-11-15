# Reward-driven predictive coding

The maze now keeps paired-error predictive coding for sensory representation and calculated pleasure/pain, and adds a small local motor actor/critic. This is an explicit hybrid. Reward gradients do not pass through the predictive graphs. Their tied w/−w connections, fixed +1/−1 coupling, nonnegative activities, local error × activity plasticity, persistent state and CPU/Vulkan implementations are unchanged.

## Why the previous coupling was insufficient

Multiplying every predictive update by pleasure minus pain does not specify which action should become more likely. Negative multiplication reverses sensory prediction learning; a zero signal stops it. Noise in sensory drive inputs provides variation but no action-specific credit assignment. Long sensory pleasure traces also repeatedly pay a single event if used directly as rewards.

## Research and interpretation

- [Legenstein, Pecevski & Maass, 2008](https://journals.plos.org/ploscompbiol/article?id=10.1371/journal.pcbi.1000180) analyze conditions under which reward-modulated local plasticity improves behavior. Correlation with an informative reward signal matters; merely multiplying arbitrary plasticity by reward is insufficient.
- [Gerstner et al., 2018](https://arxiv.org/abs/1801.05219) review evidence for eligibility traces: pre/post activity leaves a temporary synaptic flag, and later modulatory input determines plasticity.
- [Bellec et al., 2020](https://www.nature.com/articles/s41467-020-17236-y) combine local eligibility with learning signals, including reward prediction errors, for recurrent spiking networks. This implementation uses the three-factor principle; it does **not** implement or claim e-prop's recurrent gradient approximation.
- [Peters, 2010](https://www.scholarpedia.org/article/Policy_gradient_methods) describes policy gradients and the role of baselines. Here the small linear motor readout has an explicit stochastic policy, so its score derivative can be calculated locally.
- [Millidge, Tschantz & Buckley, 2022](https://pubmed.ncbi.nlm.nih.gov/35534010/) establish predictive-coding/backpropagation connections for specific inference constructions. That result is not a convergence proof for this project's undamped accumulating-error equations.

The design choice is to use a mathematically defined reinforcement rule at the motor readout while preserving the existing PC mechanism. Biological plausibility is limited to local activity products, eligibility and a shared modulatory signal. The normalized features, linear critic, numerical bounds and button sampling are engineering choices.

## Implemented rule

For each of eight buttons, `q = sigmoid(theta_b · phi)` and `p = .02 + .96*q`. The 4% uniform mixture keeps probabilities between .02 and .98. All eight buttons are independently sampled and held for 12 physics frames; opposite buttons still cancel through normal physics. Actual PC motor normal activities are clamped to these executed buttons throughout inference and sensory learning, just as for manual control. The motor outputs are therefore genuine graph activities, but no longer free-inference action proposals.

`phi` contains a constant, 24 first-person RGB sector averages (72 values), 32 bounded samples of the preceding actual PC hidden state, three vitals and eight preceding buttons. The nonconstant part is normalized to norm at most one. There are no coordinates, maps, exit bearings, teacher paths or event counters in the policy input. Stereo audio still reaches the predictive graph. The graph viewer displays the PC graph; the separate motor readout has 928 actor and 116 critic weights, reported through its TD/update readout.

A decision's feedback is accumulated with frame discount .995. For a decision lasting n frames:

```
R = sum(k=0..n-1) .995^k * r[k]
Gamma = .995^n
V(s) = critic · phi(s)
delta = clamp(R + Gamma * V(next) - V(previous), -5, 5)
```

Terminal transitions use `V(next)=0` and learn before resetting. The exact local actor score derivative, including the exploration mixture, is:

```
g_bi = (a_b - p_b) * .96*q_b*(1-q_b)/(p_b*(1-p_b)) * phi_i
z_actor = Gamma * .9 * z_actor + g
z_critic = Gamma * .9 * z_critic + phi(previous)
theta += .025 * delta * z_actor
critic += .03 * delta * z_critic
```

Motor/critic weights are bounded to ±20. No eligibility arrays or gradient operations are added to the PC backend. Actor and critic are linear readouts; this is not end-to-end policy optimization of the predictive body. Positive delta raises the likelihood of the sampled joint choice, including its released buttons; negative delta lowers it. A critic baseline allows a painful outcome that is better than expected to reinforce a successful escape instead of discouraging all activity in a painful state.

## Feedback and boundaries

Per-frame motor reward is:

```
.1 * (calculated pleasure / 256) - calculated pain
+ instantaneous primitive healing - primitive damage/wall/low-health/death cost
+ 4 on completion
+ .02 per newly visited cell (maximum .1/frame)
```

The .1 pleasure weight limits the previous large tonic darkness incentive. Existing calculated teaching labels remain unchanged, including hits, kills, pickups, exit, darkness, missed-shot pain and inactivity pain. Training uses those ideal calculated labels; play substitutes the frozen reward network's free predictions. Primitive costs, actual completion and novelty are available in both modes. These are explicit task feedback, not hidden teacher directions. Primitive death retains its −8 cost; TD clipping controls the magnitude of each motor update.

The peak-preserving sensory traces still reach drive inputs and bars, but are never used as repeated event payments. Main PC learning always uses positive configured base eta. Motor learning uses its own rates and remains active in play; `--play` is not a frozen-policy evaluation.

Manual/bot controls clear pending on-policy transitions and eligibility; they still teach PC action associations but do not train the motor actor by reinforcement or imitation. Switching mode and resetting a world also clear policy credit. Saving preserves actor/critic weights, update count and its independent sampling RNG. Loading clears pending transitions/traces because physics restarts in a newly generated world. V1/V2 PC checkpoints migrate with learned PC weights, normal activities and progression preserved, and a fresh motor readout. Pre-fix V1/V2/V3 checkpoints clear accumulated error activities once; those integrals were collected under incompatible motor boundaries/plasticity and caused the Release training crash on a long-trained checkpoint. `ErrorStateVersion: 1` marks completed migration, so subsequent resumes preserve both populations exactly. Existing saved experiments are not overwritten during validation.

## Reproduce

```bash
dotnet build ArtificialBrain.sln -c Release --no-restore -m:1
dotnet tests/NeuralGraph.Checks/bin/Release/net10.0/NeuralGraph.Checks.dll --reward-motor-only
dotnet tests/NeuralGraph.Checks/bin/Release/net10.0/NeuralGraph.Checks.dll --predictive-maze-only
dotnet tests/NeuralGraph.Checks/bin/Release/net10.0/NeuralGraph.Checks.dll --evaluate-reward-motor-maze
```

The motor checks validate the exact score update, opposite directions for reward and pain, reset boundaries, persisted RNG, learning opposite actions in different contexts and delayed credit. Across five seeds, initial controlled checks reached at least .955 rewarded-action probability, at most .045 punished-action probability, .976 delayed rewarded-action probability and .969 contextual accuracy.

The maze experiment uses actual game physics and rewards in a small straight corridor, three fixed training seeds, 30,000 autonomous training frames per seed, and 30 frozen-policy evaluation episodes per condition. Both evaluations use the same trained PC weights, fresh PC activity, the same policy sampling seed and identical starts. Actor/critic weights never update during evaluation; the reward predictor is frozen and no calculated teaching targets are supplied. A 240-frame reset limits training/evaluation episodes. Results are saved in `reward-motor-maze-evaluation.json`; the first unsuccessful 12,000-frame experiment is retained in `reward-motor-maze-initial.json`.

A small corridor is a test of reinforcement reaching actual movement. It is not evidence that the default 41×41 procedural maze, long routes, combat or held-out layouts are solved. The existing PC equations remain undamped and do not guarantee long-run stability. The frozen calculated-reward network can be inaccurate, and the motor critic must track a changing predictive representation. Reward shaping can introduce competing incentives; reliable full-maze competence requires broader evaluation.

Validation on the final implementation: the complete verification suite passed 17,652 assertions. The dedicated paired-error CPU/Vulkan checks passed 4,343 assertions; Vulkan here is llvmpipe software rendering on CPU, not a hardware GPU performance result. A separate Vulkan smoke run trained 240 frames, saved to a temporary checkpoint and resumed play with the learned motor parameters.

Final corridor results (30 frozen-policy episodes per seed):

| Seed | Untrained completions | Trained completions | Untrained wall pain | Trained wall pain |
| --- | ---: | ---: | ---: | ---: |
| 1 | 17/30 | 19/30 | 214.69 | 239.49 |
| 7 | 13/30 | 29/30 | 208.17 | 49.04 |
| 42 | 9/30 | 17/30 | 227.14 | 275.14 |

Aggregate completion improved from **39/90 (43.3%) to 65/90 (72.2%)**. Each seed improved completion, but two seeds had higher total wall pain after training. This is evidence of action learning in the tested corridor, not reliable collision avoidance or general maze solving. The initial and final experiments also differ in training duration, pleasure weighting and evaluation controls, so they are not a controlled ablation of the pleasure weight. The diagnostic motor probabilities in the raw run log are end-of-training samples, despite their original “Initial-state” label; the evaluator's label has been corrected.
