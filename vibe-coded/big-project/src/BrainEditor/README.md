# Brain Editor

Native editor for the predictive-coding engine integrated with MYT. Buttons, text fields and typography use `MYT.Kit.Gui`, with a shared theme also used by the maze. Neural graphs use Vulkan line primitives and a depth buffer through `MYT.Kit.Graph`. The window scales its layout above a 1100 × 740 minimum. The maze remains a separate executable.

```bash
dotnet run --project src/BrainEditor -c Release -- --project artifacts/editor/my-brain.brain.json
```

A missing project opens a connected sensory/cortex/motor example. **Save Project** writes editable JSON; **Save As** chooses its path. **Open Project** restores it. Changes are saved explicitly. Escape clears the current selection, or cancels an active field edit. Close the window to exit; save first.

The tabs and number keys select:

1. **Link** — choose a module type from the Type dropdown, then Add. Modules display generated neurons and depth-tested weights inside named wire boxes. Colored curved connectors and arrows identify logical links; only the selected link gets a canvas name label. Create Link, then click source and destination modules (self-links are allowed). Click a connector/weight to select its link, then **Edit Link** (or the Edit tab) to configure it. Click empty canvas to deselect. Select a module, choose **Move** for three axis arrows or **Rotate** for three rings, and drag a handle. Rotation is around the module's center; exact XYZ position and Euler angles are also editable. In Move mode, dragging the module itself moves in the camera plane; Shift adds depth movement, and X/Y/Z constrain an axis.
2. **Edit** — with a module selected, edit its properties or open **Neuron Groups** to create and change named groups. **LayerSizes** is an inline comma-separated list with +/− buttons to add/remove layers. Boolean and enum properties open dropdowns. Arrays of objects have add/remove controls and individual fields. Next Module cycles modules. Add/Remove Array changes the modifier stack. Custom modules support Add Pair, pair selection, Connect, and dragging base pairs in 3D; array copies remain procedural. With a **link** selected, Edit instead displays its two networks and **source/destination group lists**, like reward membership lists. Click rows to toggle one or several groups, or All Neurons. Gold marks source neurons, cyan marks destination neurons. The **Wiring** tab controls pattern, selection percentages, weights and plasticity. Group details stay in Edit mode rather than covering the Link canvas.
3. **Rewards** — Add Group (creates a reward module if needed), toggle module membership, cycle reward source, and set its generated pair index. Modules belong to at most one group. Reward neurons cap at **8**, others at **1**. Reward factor is `(rewardNow - rewardPrevious) / dt`. The first sample initializes derivative history.
4. **Final Network** — Bake Project compiles every module, modifier and link. Save Baked writes a new directory containing `network.pcn` and `manifest.json`. Open Baked inspects a saved artifact. Edit Source as Project restores its embedded source without changing its saved files. Editing a project invalidates its current bake.

Right mouse drag pans, middle mouse drag orbits, and the wheel zooms around the pointer within the viewport. Arrow keys and +/- remain available outside text fields. F resets and fits the camera. Page Up/Down or Fields buttons page through properties. Ctrl+S saves; Ctrl+Z/Y undo/redo. Click an inline field to type; Left/Right, Home/End, Backspace and Delete edit its value. Ctrl+A clears the draft, Enter or clicking another control commits it, and Escape cancels. Invalid values retain the draft and show the validation error in the status bar. Text input suppresses canvas shortcuts. File paths and project rename use a compact entry bar along the bottom, leaving the canvas visible.

### Neuron groups and routing

Each module starts with **input**, **hidden** and **output** groups. Custom groups have a stable ID, name, kind (Input/Hidden/Output/Other), and a criterion: default role, layer (for layered modules), generated index range, local XYZ box, explicit neuron IDs, or all neurons. Start is zero-based within the matching set, Count 0 means all remaining matches, and Step selects every Nth match. Layer −1 means last, −2 penultimate, etc. Array copies retain layer metadata and are traversed in deterministic generation order. Groups may overlap; a link's selected groups form a deduplicated union. Movement/rotation never changes local group membership.

For example, in **Edit module → Neuron Groups**, create `output0` with Criterion **Layer**, Layer **−1**, Start **0**, Count **10**. Create `output1` with Start **10**, Count **10**. Select the first link, open **Edit → Groups**, and check `output0` on its source; select the second link and check `output1`. The two routes use distinct neurons. Group creation belongs to the module's Edit screen; group assignment belongs to the link's Edit screen. Deleting a group referenced by a link is blocked until that link's selection is changed. Unchecking every group produces an empty link, not an all-neuron connection.

Link patterns are **AllToAll**, **OneToOne** (equal selected counts, stable generation order), **Nearest** (nearest selected destinations in world space), and **FanOut** (seeded random destinations). FanOut sets the maximum destinations per source for Nearest/FanOut. Endpoint percentages and degree selection choose candidates; CrossPercent controls the resulting cross connectivity. Weights may be seeded random ±WeightScale or a signed constant Weight, with configurable Plasticity. Invalid links remain inspectable but block baking. Nearest links update after a placement gesture ends.

Group kinds describe routing intent. Output groups also contribute free neurons to the baked readouts; membership in an Input group does not clamp a neuron. External boundaries still come only from input panels and clocks. Existing documents without groups receive defaults and preserve their legacy role-based endpoint selection.

Hub connectivity uses a **finite degree profile based on a truncated power law**, `P(k) ∝ k^(-Exponent)`. In Edit → **Distribution**, set the neuron count, minimum/maximum outgoing degrees, and exponent alongside a live histogram of actual generated degrees. The same settings remain in Properties. Defaults are 1, 32 and 2. Larger exponents concentrate neurons at the minimum; 0 gives a uniform profile. Degrees are assigned using evenly spaced percentiles, including minimum and maximum, then shuffled among neurons by the seed. Both endpoints are therefore represented whenever the network permits it, rather than depending on a rare random tail draw. This is a finite approximation to the power law with explicitly represented endpoints, not independent random sampling. The seed changes assignment and wiring without changing the outgoing degree histogram.

The editor rejects maximum-degree edits beyond `neuron count − 1`, and reducing the neuron count adjusts the limits in the same undo step. Loaded older configurations still cap at available neighbors; the histogram shows the realized range. A single neuron has no edges. Equal limits give a fixed degree, with the exponent field inactive. Very high exponents can put every interior percentile at the minimum; the UI reports the concentration and suggests lowering the exponent. Small parameter edits can legitimately leave integer degree counts unchanged. The histogram shows range, mean, total edges, concentration at the minimum, and per-bin counts on hover. Profiles exceeding the edge budget are rejected before wiring begins.

Target selection still favors already-connected neurons. Incoming and inter-module connections can increase total connectivity beyond the assigned outgoing degree. Existing hubs without PowerLaw settings use the defaults; old DegreeDistribution/fraction entries are retained in JSON for compatibility but no longer used or shown in the editor.

Layered modules use aligned YZ planes separated along X; changing the seed does not rotate the layers. Hubs occupy a 3D lattice volume. Hubs expose NeuronCount. Lattices expose Dimensions and an individual Size field for each axis; their neuron count is the product of those positive sizes. New lattices start at 6 × 6. Older count-only lattices infer balanced integer axis sizes that preserve the total count. Explicit sizes take precedence over the legacy neuron count. Routing partitions are edited as groups. Rotation is an explicit saved module transform, applied to every neuron and array copy before linking and baking.

For a headless bake:

```bash
dotnet run --project src/BrainEditor -c Release -- --project artifacts/editor/my-brain.brain.json --bake artifacts/editor/baked-v1
```

Each export uses a **new directory**, protecting older networks and keeping model/manifest pairs together. Project saves use temporary-file replacement. `.pcn` stores the engine topology and parameters; the manifest stores positions, stable pair/module identities, per-pair caps, signal configuration, reward groups and source. Use both files when loading through `BakedBrain.Load` and `BrainRuntime`. Loading `.pcn` directly does not restore editor metadata or runtime adapters.

`BrainRuntime` provides a predictive-coding adapter (CPU by default, with an optional host backend) for host applications. Supply nonnegative external panel channels in manifest boundary order, optionally overriding named group factors (the maze disables audio/video reward modulation). It supplies clock inputs automatically, stimulates pain-emitter destinations in proportion to the emitter activity, applies activation caps each integration tick, and multiplies outgoing source-module weight updates by its reward-group factor. Fixed residual weights keep plasticity zero. `TeachRewards` clamps reward targets in a separate inference phase, updates only reward-path weights, and restores free activities and derivative history. Reward-path weights stay frozen during free inference. The optional `learningBias` adds an implicit baseline to non-reward weight updates without altering reward activations, derivatives or teaching labels. Positive effective factors select the local free-energy descent axis and negative factors reverse it; ungrouped non-reward edges use +1. See [maze rewards and touch](../../docs/Neural.md#per-cortex-rewards-and-touch) for event labels, training and the sixteen-channel tactile ring. `SlidingAudioDft` accepts PCM samples and exposes logarithmic-band magnitudes for audio panels; hosts attach these to input channels. Visual panel geometry provides center-dense sampling coordinates; hosts sample their image at those coordinates.

For flat lattices, **Neighbors** describes the physical coordination: 1D/2 is a chain; 2D/3 honeycomb, 2D/4 square, 2D/6 triangular, and 2D/8 square with diagonals; 3D/4 diamond, 3D/6 cubic, 3D/8 body-centered cubic, and 3D/12 face-centered cubic. Each requested axis size counts cells along a lattice coordinate (a primitive direction for BCC/FCC, and alternating sublattice sites for diamond). Standard layouts connect only physically adjacent sites, so open boundaries have fewer neighbors. Imperfection deliberately rewires some of those connections. Other neighbor counts use a grid and the requested number of nearest neighbors. Flat layouts up to 3D are aligned; use the module rotation to orient them.

Spherical geometry normalizes coordinates to a sphere; hyperbolic geometry uses a hyperboloid-distance neighborhood and a spatial display warp. These curved shapes and projections above 3D retain their intrinsic nearest-neighbor rules. Custom geometry requires one XYZ coordinate per site in the axis-size product. Array modifiers still use their own sizes, spacing and shape. The 20,000-neuron limit is checked on the axis product before allocating geometry.

Residual step is the layer-index difference: 3 gives A→D. Density 2 gives consecutive origins A, B, then omits C, and repeats D, E, omits F. Source/destination/cross percentages round nonzero counts up. Cross percentages select source and destination subsets per interlayer cross. Clustering and path metrics use an undirected view, up to 128 deterministic source samples and 256 neighbors per sampled clustering calculation; unreachable pairs are omitted from path means and reported as reachability.

Generation limits are 20,000 neuron pairs and 2,000,000 directed edges per brain. Oversized bakes fail rather than truncate. Viewport clipping is reflected in the displayed counts. Engine boundaries must include an input panel or clock and at least one free output. Links cannot target external clamped inputs; module input ports remain free and accept links.

Checks:

```bash
dotnet run --project tests/BrainEditor.Checks -c Release
```

The checks cover MYT button and text input, raster clipping, the shared CPU neural backend, baking/runtime integration, all four editor views at two sizes, and the maze dashboard without changing its sensors. Preview PPMs are written to a temporary directory, or a directory passed as the first argument, so they can run without a desktop. Headless interaction checks exercise inline edits, dropdowns, layer controls, deselection and link group toggles; native GLFW presentation still requires a desktop session, GLFW 3 and Vulkan. The full [specification and plan](../../docs/BRAIN_EDITOR_SPEC.md) describes the design.

Weights render as Vulkan `LineList` primitives, with exactly two vertices per displayed edge and an interpolated depth value. A D32 depth attachment tests and writes line depth. Neurons render afterward as billboard triangles with depth testing and writes enabled, so nearer neurons cover weights and farther neurons are correctly occluded. Text and manipulation handles use a separate foreground overlay. Every generated edge is eligible to render; there is no edge sampling or display budget. GUI backgrounds and text remain in the composed canvas; solid GUI fills now emit row spans without scanning every pixel. Generated/baked topology is cached between edits.

For GPU checks without a desktop:

```bash
dotnet run --project tests/BrainEditor.Checks -c Release -- /tmp/myt-neural-previews --gpu
```

This additionally checks actual Vulkan line depth in both draw orders, neuron/weight occlusion in both directions, neuron overlap, text overlay and scissor clipping. The CPU checks include camera inverse/drag/zoom math and a dense graph timing sample (excluding generation, metrics and image export). It reports CPU offscreen frame cost, not desktop FPS.

The Edit and Final Network views show translucent XY/XZ/YZ reference grids around the network, with red X, green Y and blue Z axes. Grid spacing adapts to zoom and is shown in world units. Edge-on planes fade out to prevent overlapping lines. A fixed-size labeled axis triad in the Link, Edit and Final Network viewports follows the orbit rotation and stays visible while panning or zooming. These guides render as a separate blended line batch before weights, without writing depth.

The editor camera uses **Z-up perspective projection**: X/Y form the ground plane, positive Z points upward, and distant geometry appears smaller. The initial view and F reset use an elevated oblique angle. Middle-drag uses the original orbit direction. Weight and grid segments are clipped against near/far planes before perspective division; picking and camera-plane dragging use the same projection and the dragged object's depth. Orientation axes use the same Z-up basis. Project coordinates and saved layouts are not rewritten.

Camera orbit, zoom, world-space pan and framing are retained separately for Link, Final Network, each edited module and each edited link. **Save Project** / Ctrl+S persists these settings in the project's optional `EditorViews` metadata; Open restores them. Older projects use the defaults. F resets only the active view, and model undo/redo keeps the current navigation settings. Right-drag translates both the camera and its orbit target sideways in world space, keeping the lens centered on the viewport and producing perspective parallax.

The outlined canvas extends from just below the active toolbar to just above the status bar. Counts and graph metrics use a compact readout strip at its lower edge. Module bounds, neuron markers and crossing links are clipped to their visible portions; moving an object's center beyond the outline does not hide its remaining visible part. Pointer selection is limited to the visible canvas.

Link preview and baking share `Baking.Compile`, including generated modifiers, endpoint selection, weights and plasticity. Moving or rotating a module transforms cached local neuron/box geometry without regenerating connections during the gesture. Nearest-neighbor links rebuild once on release. All generated neurons and edges are rendered, including internal module connections and inter-module links. Link, Edit and Final Network views have no edge display cap or sampling; viewport clipping and depth testing still apply. Final Network names clock modules in blue and reports the number of clock input pairs in its inspector; array copies remain individual clock inputs.

For a project-specific preview/timing check, append `--scene PROJECT_JSON` to the check command. It reads that project and writes an offscreen Link preview to the chosen output directory. Reported frame time excludes generation and file export.

### MCP editor and maze tools

Run `scripts/brain-editor-mcp.sh` from an MCP client's stdio server configuration. The launcher builds with diagnostics on stderr and reserves stdout for newline-delimited JSON-RPC. Restore the solution's dependencies first on a fresh checkout. Alternatively, after building, run:

```bash
dotnet src/BrainEditor/bin/Release/net10.0/BrainEditor.dll --mcp --workspace /absolute/path/to/MYT
```

For clients using `mcpServers`, the server entry is:

```json
{
  "mcpServers": {
    "brain-editor": {
      "command": "/absolute/path/to/MYT/scripts/brain-editor-mcp.sh",
      "args": []
    }
  }
}
```

This is a headless editor/file interface and a separate CPU maze session. Reopen edited files to see changes in an existing GUI window. It does not attach to a running ArcadeLab window. The transport implements the [MCP stdio specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports), initialization, ping, tool discovery and tool calls; it requires no network service or additional package.

| Tool | Purpose |
| --- | --- |
| `brain_create_maze` | Create a connected maze-ready template in a new project file |
| `brain_read` | Return full editable project JSON and its revision hash |
| `brain_write` | Validate and atomically replace a project using its expected revision |
| `brain_validate` | Compile and check neural topology and maze signal bindings |
| `brain_bake` | Export to a new directory |
| `maze_start` | Load a bake or checkpoint, with optional training/adaptive flags |
| `maze_step` | Advance up to 600 frames, stopping after a two-second compute slice |
| `maze_status` | Inspect completion, exploration, errors, rewards and learner updates |
| `maze_save` | Save to a new checkpoint directory |
| `maze_stop` | Release the session; save beforehand to retain training |

All tool paths must stay inside `--workspace`; symlink paths are rejected. Project writes reject stale revisions. Bakes and checkpoints require new directories. One maze session runs per server, and simulation advances only when `maze_step` is called. Repeat calls for sustained training and check `advanced` rather than assuming every requested frame ran. GUI play continuously advances the same learning code.

A complete MCP workflow is `brain_create_maze {"path":"artifacts/online/brain.json"}`, `brain_bake {"path":"artifacts/online/brain.json","directory":"artifacts/online/baked"}`, `maze_start {"directory":"artifacts/online/baked"}`, repeated `maze_step {"frames":600}`, then `maze_save {"directory":"artifacts/online/checkpoint-1"}`. `brain_read` / `brain_write` support changing modules, groups, links, reward membership and sensory bindings before another bake. The template includes spatially separated RGB samples, sixteen touch channels, vitals, stereo audio, recurrent visual/spatial lattices, temporal-memory/central/motor hubs, eight motor buttons and separate reward heads. Additional bindings are `video.rgb.N`, `body.ammo` and `body.reserve`.

### Online adaptive maze learning

An unqualified CLI launch now builds the sparse recurrent modular brain on CPU and saves under `artifacts/modular-maze`. MCP sessions from bakes and new CLI runs enable adaptive control. Explicit checkpoint paths resume that checkpoint; architecture flags such as `--layers`/`--width` retain the legacy dense-network path. Existing checkpoints retain their saved setting; add `--adaptive` to enable it when resuming. `--no-adaptive` retains the original threshold controls. A separate small neural action head learns online alongside the predictive cortex, and chosen buttons clamp the actual motor neurons. The dashboard identifies this controller as **ADAPTIVE**. Manual controls and `--bot` take priority and do not train the adaptive action head.

```bash
dotnet run --project src/ArcadeLab -c Release -- --train --brain artifacts/online/baked --checkpoint artifacts/online/checkpoint --adaptive
```

The head receives RGB sectors, touch, stereo sound, vitals, previous buttons and sampled hidden cortical activities from baked brains (motor readouts for the legacy dense runtime). Previous observations and slow sensory traces provide temporal context. It uses a 32-unit nonlinear hidden layer, fourteen compatible button combinations, discounted TD learning with eligibility traces, bounded experience replay, and short lookahead through an empirical graph of observed views and transitions. It receives no player coordinates, depth buffer, full map, enemy coordinates, teacher routes or distance-to-exit. The observed-view graph is a partial memory of experience, not a reconstructed metric map; similar-looking places can alias.

Rewards prioritize exit completion (+8) over death (−4), first visits (+0.04/cell, +0.12/region, capped at +2 per episode), hits (+0.05), kills (+0.25), useful ammo (+0.08), healing (+0.005/HP), damage (−0.04/HP), misses (−0.03), elapsed time, inactivity and wall pressure. Darkness pays nothing. Active baked reward heads learn the same bounded outcome traces; audio/video reward heads retain their existing disabled modulation. Unbaked cortical reward teaching uses its existing normalized teacher pathway with revised exploration/exit labels; the adaptive action head uses the same new outcome reward in both runtimes.

Completion, death, or 180 simulated seconds ends an adaptive episode and starts the next maze. Neural weights and target weights persist in checkpoints; sensory history, eligibility, replay and observed-view graphs reset on load because the world is regenerated. In `--play`, action-head and reward-head training freeze; the existing predictive cortex still has local plasticity. `--train` is the continuously adapting mode.

The implementation provides online reinforcement learning, not a demonstrated general-purpose meta-learner. Enemy-pattern learning and general maze solving remain evaluation goals. The reproducible benchmark below uses two fixed, small, enemy-free mazes and three seeds; it reports training completions separately from frozen-policy evaluation. It does not measure generalization to unseen geometry or combat proficiency.

```bash
dotnet run --project tests/BrainEditor.Checks -c Release -- --adaptive-benchmark
```

Initial action-head-only CPU benchmark, with zero cortical context (80 training episodes per seed, 600-frame episode cap): 213/240 online training episodes completed; frozen evaluation completed 2/6, compared with 0/6 before training. The two frozen successes were straight corridors; all three turn-maze evaluations failed. These results demonstrate learning and expose weak policy generalization; they do not establish reliable navigation or enemy tactics.

### Sparse recurrent default and performance

Run the modular brain with `dotnet run --project src/ArcadeLab -c Release -- --train`, or select it explicitly with `--brain modular`. Its default backend is CPU with four integration ticks per frame, avoiding software-Vulkan submission overhead on small sparse graphs. Explicit `--backend` and `--propagation-steps` still override these choices. This uses its own checkpoint directory, preserving the previous dense and editor-export checkpoints. The saved source project and bake are in `artifacts/modular-brain/`; open `brain.brain.json` in BrainEditor to edit its modules and routing.

The visual lattice has 48 neurons, the spatial lattice 64, temporal memory 64, central integration 96, and motor cortex 32. Local grid connections and bounded power-law hub degrees keep the graph sparse. Spatial ↔ memory, memory ↔ central, and central ↔ motor routes provide actual recurrent feedback; executed motor buttons feed temporal memory. Baked hidden activities are inputs to the action learner, so recurrent cortical state contributes to decisions. Visual reward modulation is isolated from spatial reward teaching.

The runtime caches boundary-module mappings, reward indices, pain-emitter routes and edge memberships, reuses boundary/weight buffers, and avoids recreating unchanged random generators during reward teaching. Topology and metadata are fixed for the lifetime of a runtime; editing a project requires a new bake/runtime.

Measured on this machine: the existing 7,200-neuron / 4,214,784-edge dense checkpoint averaged 262.78 ms per CPU training frame over 12 frames. The final 412-neuron / 2,038-edge recurrent brain averaged 1.19 ms over 600 frames with eight integration ticks, matching the old checkpoint’s tick count. These are end-to-end simulation/learning timings excluding rendering, with eight integration ticks in both timed CLI runs; the compact default uses four. They are not a desktop FPS or a learning-quality comparison. `ModularBrainChecks` reports the current topology and CPU timing on every check run.

VS Code configuration is provided in `.vscode/mcp.json`. Open that file and start the `brain-editor` server, or use **MCP: List Servers**. Its format follows the [VS Code MCP configuration reference](https://code.visualstudio.com/docs/agents/reference/mcp-configuration). The server can edit the source modules, validate/bake the sparse graph, and run it headlessly; `maze_status` reports neuron/connection counts and frame time.
