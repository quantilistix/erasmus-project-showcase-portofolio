# Neural networks in MYT

`MYT.Kit.csproj` references the standalone `NeuralGraph` project. Hosts referencing MYT can use `NeuralGraph` model, state, serialization and training APIs directly. The neural project keeps its existing namespace and can still be referenced independently, without game or GUI dependencies.

`MYT.Kit.Neural.PredictiveNetworks.Create` centralizes backend selection. The returned backend belongs to the caller and must be disposed:

```csharp
using MYT.Kit.Neural;
using NeuralGraph;

var topology = new GraphTopology(1, 2, steps: 1, outputs: [1], edges: [new(0, 1)]);
var model = new PredictiveCodingModel(topology, seed: 42);
using var network = PredictiveNetworks.Create(model, NeuralBackend.Auto,
    diagnostic: Console.WriteLine);
network.Step([0.5f], steps: 8, activityStep: .04f,
    weightStep: .02f, learningRate: .00001f, learn: true);
var state = network.CaptureState();
float output = state.Normal[1];
```

Select `Cpu` for deterministic reference execution, `Vulkan` to require GPU execution, or `Auto` to try Vulkan and fall back to CPU for an unavailable runtime/device. Model and learning behavior are described in [NeuralGraph's documentation](../src/NeuralGraph/README.md).

## Tools

From the repository root:

```bash
dotnet run --project src/BrainEditor -c Release -- --project artifacts/editor/my-brain.brain.json
dotnet run --project src/ArcadeLab -c Release -- --train --new --checkpoint /tmp/myt-maze --backend auto
```

Use a fresh checkpoint directory for a new experiment. Resume by removing `--new`, or use `--play --checkpoint PATH` for a trained checkpoint. Editor JSON and baked exports retain their existing formats. The maze can also run an editor export directly using `--brain`.

### Running a baked brain in the maze

```bash
dotnet run --project src/ArcadeLab -c Release -- --play --brain latest --backend auto
# Or select an exact export and teach controls with the roaming bot:
dotnet run --project src/ArcadeLab -c Release -- --train --brain src/BrainEditor/artifacts/editor/baked-20261107-043111 --bot --backend cpu
```

`latest` selects the newest complete `baked-*` directory in `src/BrainEditor/artifacts/editor`. Baked sessions save and resume in `artifacts/baked-maze` (override with `--checkpoint`). Use `--train --new --brain PATH|latest` to start again from the export. Source exports stay separate from maze checkpoints. A baked network can play immediately; it does not require a legacy reward checkpoint.

Panel modules now expose `ChannelBindings` in the editor inspector. These are explicit per-channel host bindings in local neuron ID order. For the motor panel, use each key exactly once: `["W", "S", "A", "D", "Q", "E", "F", "X"]`; reorder this list to change which neuron presses each key. The same key/action table drives manual keyboard controls and autonomous motor actions. Duplicate or unsupported keys fail on load. Existing exports without bindings receive that default key map, which is retained when saving the maze checkpoint.

Input bindings support `tactile.0` through `tactile.15`, `video.red`, `video.green`, `video.blue` (mean camera/HUD channels), `video.pixel.N` (luminance at pixel N), `audio.left.rms`, `audio.right.rms`, `body.pain`, `body.pleasure` and `body.health`. Existing named video/audio panels receive the corresponding defaults; the supplied single `INPUTPANEL` migrates to a sixteen-neuron tactile ring. Other unnamed inputs require explicit bindings. The startup log prints every resolved node/channel binding; the live graph lists boundary values, and the motor indicators show local channel IDs, activations and keys. Hover a neuron to inspect its module, binding, normal activity and error pair.

The maze graph now uses the editor's shared graph primitives, perspective projection, original baked positions and role colors, with module wire boxes and labels. It draws every edge and visible neuron. Right-drag pans, middle-drag orbits and the wheel zooms. **Hide Graph / Show Graph** in the top toolbar switches rendering off/on without changing neural inference. Legacy maze graphs use this same renderer with layer positions.

The host loads the original graph, weights, plasticity, per-neuron activation caps, module clocks and reward groups through the shared `BrainRuntime` project. Exactly one eight-neuron output panel maps, in order, to forward, backward, turn left, turn right, strafe left, strafe right, fire and reload. Other output groups remain internal readouts. Tab manual controls and `--bot` clamp those actual motor neurons during inference.

Input panels bind by panel type or descriptive name. The supplied bake uses `Other` panels named `Audio Input` (two stereo RMS levels) and `Video Input` (mean red, green and blue from the current camera/HUD line). Visual pixel panels instead receive the luminance line resampled to their width and repeated vertically. A named legacy scalar touch panel is expanded to sixteen body-relative tactile inputs. Explicit `body.pain` bindings on other panels remain scalar. Unrecognized multi-channel inputs fail with a binding error. The three-channel summary discards spatial detail; more detailed vision requires a visual pixel panel and rebaking. Audio RMS likewise carries level rather than frequency detail.

### Per-cortex rewards and touch

Predictive coding defines the local update axis: the direction that improves the model's predictions. Each cortex's reward factor selects a direction along that axis. Perceived reward is **the time derivative of its own reward neuron's free activation**, `g = (R_now - R_previous) / simulation_dt`. The implicit thinking bias is applied outside the reward system: the local weight-update factor is `g + thinkingBias`. Its default is 0.1 for every cortex in both train and play; use `--thinking-bias VALUE` (0 disables it). An effective factor above zero selects learning; below zero selects unlearning. Thus a falling reward activation must exceed the positive bias in magnitude to reverse learning. Zero perceived reward still permits thinking-driven plasticity. The first observation initializes derivative history. Group members are matched by module ID; group/module names identify the maze teaching policy (Motory/Motor, Central, Spatial, Audio, Video and Reward System).

With the engine's convention `prediction_i = sum_j(w_ij * x_j)` and signed predictive error `e_i`, the baked runtime applies `delta_w_ij = weightStep * rate * plasticity_ij * e_i * x_j * (g + thinkingBias)`. The positive factor follows the local free-energy descent axis; punishment reverses it. Fixed plasticity zero keeps an edge unchanged. This reward sign controls **weight plasticity**; inference continues with the same predictive dynamics. It is not a global objective or a guaranteed improvement in game performance.

| Cortex | Positive teaching events | Negative teaching events |
| --- | --- | --- |
| Motory | Hit an enemy that was visible when that projectile was fired | Every missed projectile, including firing without a visible enemy |
| Central | Kill an enemy; complete a maze | Die; a projectile misses after being fired without a visible enemy; primitive pain |
| Spatial | Sum of motor and central positive events | Sum of motor and central negative events |
| Audio / Video | None; reward modulation and reward-head teaching disabled | None |
| Reward System | Spatial aggregate, used to teach its own reward predictor | Spatial aggregate |

Shot-time visibility uses the actual camera rays (field of view, range and occlusion), and each projectile retains the identities of the enemies visible at firing. Turning afterwards does not change its credit. A miss while some enemy was visible punishes motor only; a miss fired with no visible enemy also punishes central. Enemy coordinates and visibility labels are available to the teacher, not supplied as sensory ports.

**Training now teaches the reward system.** After every game step, per-cortex events update separate bounded teaching traces. In legacy threshold control, motor hit adds 0.05; miss subtracts 0.03; central kill adds 0.25 and exit adds 8; death subtracts 4; unseen-shot miss subtracts 0.03. Central additionally receives the current nonnegative `Game.PrimitivePain` as punishment (wall pressure, damage, low health and the primitive death signal), added to its event penalties; spatial and the Reward System inherit it through their aggregates. Spatial sums these positive and negative amounts separately. Traces start at neutral activation 4, decay toward 4 with a one-second simulation half-life, and clamp to [0, 8]. Audio/video have no assigned reward or punishment and no reward teaching targets. Their local plasticity uses only the shared implicit thinking bias; their existing reward heads remain in saved graphs for compatibility and do not modulate their learning. Old audio/video teaching traces reset to neutral on load.

The **Assigned Rewards** table displays each cortex's designated positive reward, punishment and signed net for the current frame. The central primitive-pain contribution is shown separately below the table. The lower readout shows active reward neurons' perceived derivatives, effective learning factors including the implicit bias, and teaching targets. Audio/video are labeled Reward Off / Bias, and the shared bias is displayed separately from assigned rewards. Assigned signals remain visible when the graph is hidden. In play they are observer diagnostics only: teaching targets and reward-path weights stay frozen, and event assignments are not passed to the network.

A separate clamped inference phase teaches only connections in/through reward modules and the Reward System. The normal, error and clock state from free inference is restored afterwards. The label never directly replaces the perceived derivative. `--reward-rate` controls this teaching phase; counters, teaching error, per-group derivatives, factors and targets are shown in the game. In **play**, reward-path parameters are frozen and no event labels are supplied; cortical learning still uses each inferred derivative, plus the implicit bias for every cortex; audio/video use that bias alone. A sufficiently negative derivative overcomes the bias and produces unlearning. The old generic `derivative + 4 * primitiveReward` multiplier has been removed from baked learning.

**Touch is a ring of 16 neurons**, bound by `tactile.0` ... `tactile.15`. Directions are relative to the body: 0 front, 4 right, 8 back, 12 left. Near wall contact supplies 0.05 in the corresponding directions; pushing into a blocked direction supplies 0.25. Projectile damage generates a random ring location and random loud amplitude in [0.75, 1], fading with a 0.35-second simulation half-life. Mere passive health drain does not trigger a damage scream. Wall contact is refreshed from current geometry, pressure disappears on release, and resetting the episode clears damage pulses. The game shows a live tactile ring.

Old bakes/checkpoints with the named scalar touch port migrate in memory: fifteen boundary neurons are inserted, output/state indices are remapped, and each old outgoing scalar connection is replicated across the ring with its weight divided by sixteen. Original exports stay intact; the migrated topology is saved in the maze checkpoint. Fresh panels can use `Panel=Tactile`, `Channels=16`, and explicit tactile bindings. Saved reward targets, teaching counts, weights and derivative history resume across loads; loading still creates a new maze episode rather than restoring the exact world.


Both native interfaces use `MYT.Kit.Gui` with a shared atlas, theme and rendering bridge. Link view displays generated module networks inside labeled wire boxes with highlighted inter-module connections; neuron previews distinguish input, hidden, output and reward pairs. The maze has clickable AI/manual, pause/resume and restart controls, readable GUI labels and a shaded observer map. Its sensor inputs remain the actual observation strips.

## Validation

```bash
dotnet build Tester.sln -c Release -m:1
dotnet run --project tests/BrainEditor.Checks -c Release -- /tmp/myt-neural-previews
```

The checks use CPU inference and offscreen rendering; GLFW input and Vulkan presentation need a desktop. They cover buttons, caret editing, raster clipping, backend creation, baked network execution, four editor views at 1100 × 740 and 1600 × 1000, and maze rendering without advancing the simulation or modifying observations.

## Graph rendering and navigation

The native editor and maze overview batch weights into `MYT.Kit.Graph.GraphDrawList`. `GraphGpuRenderer` draws one Vulkan line-list batch with depth test/write (`LessOrEqual`) and then a neuron triangle batch sharing the depth test/write. Labels and handles have a separate depth-free overlay. The overlay loads the composed canvas color and clears a D32 depth attachment each frame. There are no per-pixel GUI rectangles for neural weight lines. `GraphRasterizer` is the CPU reference used for offscreen previews. The renderer uses one frame fence to protect its shared depth and mapped vertex buffer; swapchain resize rebuilds its attachments and pipelines.

In the editor, right-drag pans, middle-drag orbits and the wheel zooms at the pointer. Drag module boxes or base custom neurons in the current camera plane; Shift-drag moves along camera depth, and holding X/Y/Z constrains movement to that world axis. One drag creates one undo checkpoint. Generated neuron layouts and array copies remain procedural. F refits the graph after moving neurons.

Run `tests/BrainEditor.Checks` with `--gpu` after the preview directory to test real Vulkan line depth, overlays and clipping offscreen. The dense CPU preview timing excludes generation/metrics and image export, and does not predict a desktop frame rate.

The editor uses a Z-up perspective camera with an elevated default orbit view. X/Y are the ground plane and +Z projects upward; the grid and axis triad share that basis. Dragging uses the object's camera depth, and line segments crossing the near plane are clipped before projection. The projected depth is the Vulkan [0,1] perspective depth used by the existing GPU weight pipeline. This changes the view, leaving stored neuron/module coordinates intact.

Navigation is project metadata (`EditorViews`), with independent states for Link, Final Network and individual edited modules and links. Save/Open preserves orbit, zoom, world-space camera translation and framing; undoing model changes preserves navigation, and F resets the active view. Panning translates the camera and target in their right/up plane instead of shifting the projection origin. The taller canvas fills the available area between its toolbar and the status bar, with a visible border and compact readouts. Renderers scissor partial cards/markers/links rather than culling them by their center points.

Link view and baking share compilation before boundary reordering, so their neuron identities, geometry, connections and parameters agree. Link selection follows the logical link behind the weights. Module names render above weights in a GUI-font overlay, and a selected link has a separate native line highlight pass. Moving and rotating modules transforms cached local geometry without recompiling topology during a gesture; position-dependent nearest links rebuild on release. Empty/invalid conceptual links remain dashed and selectable; the strict bake still validates them.

Neurons are no longer sampled by a global stride: every generated neuron is eligible to render, with viewport clipping. Every generated edge is also eligible to render in Link, Edit and Final Network views, including all internal module connections; there is no drawing budget or edge sampling. Baked clocks are blue and named; the inspector reports their actual input-pair count. The checks cover preview/bake parity, clock arrays and signals above 2,000 neurons, artifact round trips, runtime clock clamps and actual clock rendering.

Named neuron groups and module transforms are stored in editor JSON and the baked source manifest. Group criteria support stable layer/range selections, local spatial boxes, roles and explicit IDs. Links select unions of endpoint groups and choose all-to-all, ordered one-to-one, nearest or seeded fan-out wiring. Output groups contribute baked readouts; input groups do not alter external boundary clamping. The module Edit screen defines groups, and the link Edit screen assigns them using membership lists; Link view remains a sparse overview. See [Brain Editor](../src/BrainEditor/README.md#neuron-groups-and-routing) for an example splitting a last layer into two routes. Regression checks cover disjoint routes, group unions, bounds, transforms, persistence, legacy projects and GUI interaction.

### Adaptive action learning

New maze runs use an additional online neural action head; existing checkpoints opt in with `--adaptive`. It adds TD credit assignment, sensory history, replay and observed-transition lookahead alongside the predictive cortex. In adaptive mode, active baked cortex reward heads share the outcome reward: exit +8, death −4, bounded first-visit exploration, modest combat/supply credit, and costs for damage, misses, time and inactivity. Audio/video modulation stays disabled. Completion and 180-second episode limits now advance the world in adaptive mode. Darkness no longer earns a teacher reward in either control mode. See [BrainEditor online learning and MCP](../src/BrainEditor/README.md#online-adaptive-maze-learning) for the algorithm, exact feedback scale, checkpoint semantics and evaluation limits.
