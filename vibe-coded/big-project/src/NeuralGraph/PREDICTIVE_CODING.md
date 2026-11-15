# Paired-error predictive coding: agreed model and implementation

## Status

Implemented in `PredictiveCodingModel`, `CpuPredictiveCodingBackend` and `VulkanPredictiveCodingBackend`, and used by the active maze. The previous actor/value/replay learner is retired to a test fixture. Old training may be discarded, as authorized by the user; the new default checkpoint is `artifacts/predictive-maze` and old checkpoint formats are rejected rather than silently reinterpreted.

## Connections

Each logical unit has a normal neuron $x_i$ and its paired signed error $e_i$. The fixed local connections are

$$
 x_i \xrightarrow{+1} e_i,\qquad e_i\xrightarrow{-1}x_i.
$$

For every declared `Edge(i,j)`, one stored trainable parameter supplies both variable connections:

$$
 e_i\xrightarrow{w_{ij}}x_j,\qquad x_j\xrightarrow{-w_{ij}}e_i.
$$

The reciprocal weight is always the negative of the same parameter. It is not independently stored or learned. Fixed local connections are implicit and never plastic. Skips, cycles and self-connections use the same equations.

## Accumulating activity

The selected interpretation is accumulation over time, rather than assigning error activity to an instantaneous discrepancy. With unit activity time constants and gain 1, the incoming drives are

$$
 \dot{x}_i=-e_i+\sum_{k:e_k\to x_i}w_{ki}e_k,
$$

$$
 \dot{e}_i=x_i-\sum_{j:e_i\to x_j}w_{ij}x_j.
$$

In the active maze, each main motor output has signed error equal to its current normal activity (`e+ = x`, `e− = 0`) (`MatchOutputErrorsToNormal=true`), including when old state is restored. Free motor normals integrate `x_next = clamp(x + h * (incomingDrive - x), 0, 1)` and provide the actual control readout. This restores proportional inhibition without integrating motor error over time. Reward-network output errors retain the equations above so clamped-label teaching continues normally. The reusable backend defaults to unclamped output errors.

The maze sets `ActivationLimit=1` for both models. Every normal activity is projected onto [0, 1], including encoded sensory inputs, manually clamped motors, reward teaching labels and loaded normal states. Free activities are capped after each integration tick, before they feed subsequent errors or learning. The reusable library retains its unbounded nonnegative default unless `SetActivationLimit` is called. Signed error dynamics remain unchanged; main motor errors track capped normals.

The library supports an optional shared gain $g$, multiplying both variable directions symmetrically. The active maze now keeps $g=1$ in both networks: pleasure/pain reach dedicated input drives and modulate main plasticity instead of changing propagation. Fixed local $+1/-1$ connections stay unchanged.

There are no biases, tanh activations, damping terms, hidden activation resets, actor/value targets or backpropagation in these dynamics. The algebraic alternative

$$
 e_i=x_i-\sum_jw_{ij}x_j
$$

is not substituted for the selected differential equation.

## Numerical order and nonnegative outputs

The CPU and Vulkan kernels use the same projected symplectic Euler tick:

1. Apply current sensory clamps and, only during a teaching pass, output clamps.
2. Simultaneously update every error from the preceding normal state:

   $$
   e_i^{k+1}=e_i^k+h_a\left(x_i^k-g\sum_j w_{ij}x_j^k\right).
   $$

3. Update every free normal from the new errors:

   $$
   x_i^{k+1}=\max\left(0,x_i^k+h_a\left[-e_i^{k+1}+g\sum_k w_{ki}e_k^{k+1}\right]\right).
   $$

In the library default, clamped normal activities retain their boundary values in step 3. With an activation limit, both boundary and integrated normals are projected onto [0, limit]. Error updates apply to all nodes except designated outputs when `MatchOutputErrorsToNormal` is enabled; their signed errors track their normal activities. Activities persist between calls and through world resets. The normal and error arrays are initialized to zero once for a new backend, or restored explicitly from a checkpoint.

One normal neuron is one nonnegative activity; **normal outputs are not signed logical values encoded through subtraction**. Signed external data such as PCM is represented by separate positive/negative input normal channels. A signed internal error is represented by two nonnegative physical error channels:

$$
 e_i=e_i^+-e_i^-,\qquad e_i^+=\max(e_i,0),\qquad e_i^-=\max(-e_i,0).
$$

There are therefore three physical activity values per normal/error unit: $x_i,e_i^+,e_i^-$. Encoding a signed error is not a replacement for its normal/error pairing. The viewer shows the normal and its two error channels separately. The eight motor outputs are the actual normal activities, each independently thresholded at 0.5.

## Local learning and fixed plasticity

Each variable connection has a fixed multiplier $\rho_{ij}$. The maze fixes it to 1 in both models, including on resume; it has no update rule. The reusable library still permits other fixed multipliers. After all activity ticks in a call, one local update is applied:

$$
 \boxed{\Delta w_{ij}=-h_w\,\eta\,\rho_{ij}\,e_i x_j.}
$$

The active maze sets the main effective rate to `baseRate * (TotalPleasure - TotalPain)`, so its full update is `-weightStep * baseRate * plasticity * error_source * normal_destination * (total reward - total punishment)`. There is no added baseline: positive net reward applies the ordinary local update, negative net reward reverses it, and zero net reward stops weight changes while activities continue. Total pleasure combines normalized applied pleasure and primitive pleasure; total pain combines applied pain and primitive pain. These are the preceding frame's feedback signals. Reward-network teaching uses its own positive base rate independently of this multiplier; reward weights are frozen in play.

The maze clamps the resulting weights to [−8, +8] after each update on CPU and Vulkan. The configurable magnitude is saved in `MazeConfig.WeightLimit` and can be overridden with `--weight-limit`; old maze configurations default to 8. Loaded weights are bounded before inference, and the reciprocal prediction remains the exact negative of the same weight. The reusable library remains unbounded unless `SetWeightLimit` is called before backend creation (or followed by `UploadParameters` for a live GPU backend).

This uses the current connected error and normal activities. The multiplier does not learn. Zero plasticity freezes a connection. Freezing a model, or requesting inference without learning, disables these increments while activity continues. Stored plasticity metadata is retained; it is not modified by an optimizer or by mode changes.

Default maze settings are eight activity ticks per frame, $h_a=0.04$, one weight update per learning network, $h_w=0.02$, and $\eta=10^{-5}$ for both networks. Thus the base weight-update coefficient is $-2\times10^{-7}$ before multiplying by plasticity and activities, versus a 0.04 activity coefficient repeated eight times. The total activity-time advance per free frame is 0.32. A reward-teaching pass adds its own eight temporary activity ticks and one weight update.

This library mechanism has no loss-gradient, clipped SGD, eligibility trace or batch replay path. The application motor readout has its own local eligibility traces. Arbitrary gains/topologies still require sensible numerical steps. The undamped accumulating-error equations can produce growing error under sensory forcing; neither this integration scheme nor the local rule guarantees that the reported error decreases. Nonfinite state is reported rather than silently repaired.

## Total error: statistics only

The user specified

$$
 \boxed{L_{\mathrm{stats}}=\sum_{i=0}^{N-1}e_i^2.}
$$

The implementation sums every represented error square, including input, hidden and output units. It uses no factor of $1/2$, averaging, output-only selection, reward-dependent weighting or normalization. With physical error channels,

$$
 L_{\mathrm{stats}}=\sum_i(e_i^+-e_i^-)^2.
$$

This is exclusively a read-only statistic. It does not drive activities or weights. Omitting the measurement leaves trajectories identical, which is covered by a CPU reference test. Main free-state error, reward free-state error and reward-teaching-state error are reported separately. The purple graph bar displays main $\sum e^2$, not an old TD/actor loss. A variational-free-energy interpretation has not been specified, so that name is not used.

## Game/reward boundary conditions

The main sensory boundary includes current vision, stereo audio, executed controls, health/ammunition, reward drives, primitive pleasure and clock. Main inference and signed reward-modulated local learning run in both modes. Autonomous actions read actual motor normal activities above 0.5. Manual/bot actions clamp these normals; their signed errors track those normals in either case.

The reward network first performs free inference on current evidence. Predictions are read from its two free normal outputs. During training, its free normal/error state is then saved, its outputs are clamped to ideal normalized pleasure/pain, activity is integrated and local weights update. Restoring the saved free state leaves those learned weights in place but removes teaching-state activity from the next displayed prediction. Play skips the teaching pass and freezes all reward parameters while its activities continue evolving. There is no instantaneous analytical darkness-reward branch.

The designated drive inputs retain $P_{\mathrm{total}}+N_{\mathrm{total}}U_i$, with held uniform randomness. Main plasticity additionally uses the signed total feedback multiplier described above. Inactivity teacher pain peaks at 1 after a 0.5-second grace period and a 4.5-second ramp in simulation time; meaningful displacement resets it. Control trials provide no experimentation reward. Primitive pleasure comes from healing; held pain randomness remains active.

## Validation and persistence

Checks include hand-derived local increments and connection signs, GPU/reference agreement with recurrence and output clamps, fixed/frozen plasticity, nonnegative physical state and outputs, statistics independence, free/teaching state separation, main-learning/reward-freeze behavior, and exact normal/error checkpoint roundtrips.

Predictive checkpoints use their own model header and engine marker and store both complete populations. Incompatible old models are rejected. The application sets signed motor errors to the restored normal activities on resume and fixes maze plasticity to 1 while retaining normal activities capped to [0, 1], hidden/input errors, weights and counters. Resuming the game restores neural state and progress while generating a new world; it is not a full game-physics/RNG snapshot. Numerical validation and a bounded training run establish implementation behavior, not long-term convergence or competent maze play.
