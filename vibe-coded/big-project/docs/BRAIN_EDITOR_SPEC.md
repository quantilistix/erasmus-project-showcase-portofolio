# Brain editor specification and implementation plan

## Scope

Add a native GLFW/Vulkan editor as a separate BrainEditor project (`--project PATH`). Keep the existing maze and reward experiments intact. Project JSON is the editable source of truth; baking creates a predictive-coding `.pcn` plus a JSON manifest of positions, module membership, boundary channels, modifiers and reward groups. Seeds make generation and baking deterministic. Display one selectable entity per normal/error pair (the engine represents signed errors with two channels).

## Four views

1. **Link**: add, select, move and delete modules; create directed links, including self-module links. Edit both endpoint roles, percentages and selection criteria independently. Tabs and keys 1/2/3/4 switch views unless typing.
2. **Edit**: inspect the selected module's generated pairs and connections in an XYZ projection. Edit all generation settings, add/remove Array modifiers, and edit custom pairs and directed edges. Rotation, zoom and pan support spatial inspection. Fields commit on Enter and cancel on Escape. Save/load, undo/redo and explicit validation are available.
3. **Reward groups**: create groups, toggle module membership, choose a reward source module and pair index. Show the derivative-over-dt and primitive reward conventions.
4. **Final network**: explicitly bake all modules and links into one graph, inspect pair/edge totals and spatial topology, then export. Changes invalidate the bake. Export must not silently use stale results.

## Module types and parameters

All modules have stable IDs, names, seed, XYZ origin, input/hidden/output roles, default weight scale and plasticity. Roles describe module ports, not global clamped inputs.

- **Hub**: input/hidden/output counts; weighted degree distribution (degree and fraction). Sample directed neighbors with preferential attachment to existing degree, excluding self edges.
- **Lattice**: role counts; arbitrary positive dimension count and optional axis sizes; neighbors per pair; flat, spherical, hyperbolic or custom geometry; imperfection probability replacing a neighbor by a random pair. XYZ is a seeded orthogonal projection of N-dimensional coordinates. Custom shape uses explicit coordinates. Display clustering coefficient and mean shortest path (undirected connectivity; unreachable pairs excluded and reachability reported). Neighbors are nearest in intrinsic geometry; 2D degrees 3/4/6 are explicit honeycomb/square/triangular grids.
- **Layered**: layer sizes; per-cross source and destination percentages; layer dimensions/axis sizes; residual step, density and type. Residual step is the layer-index distance (3 means A to D); density is the number of consecutive origins followed by one origin without a skip, repeating (2 means origins A, B, D, E). Confirmed by the user. Fixed residuals have weight 1 and plasticity 0. Weighted residuals use ordinary initialization.
- **Input/output panels**: visual width/height, center-to-edge resolution decay; audio min/max frequency, bands and sample rate; controls and other channel count. Input panels become clamped boundaries; output panels are readouts. Visual coordinates are center-dense according to decay. Audio uses logarithmic bands with frequency-dependent windows and running sliding DFT, updating sums rather than reintegrating windows.
- **Pain**: count and random-signal frequency (Hz); receives network activation and emits random values scaled by it.
- **Clock**: frequency Hz, amplitude, phase radians; one pair, clamped to a nonnegative sine wave (offset sine, because normal activities are nonnegative).
- **Reward**: count; ordinary free neurons used as reward-group sources, with activation capped at 8. All other neurons are capped at 1.
- **Custom**: explicitly editable pairs (stable local IDs, role, XYZ) and edges (source, destination, weight, plasticity).

## Links

Source/destination module IDs and independent role masks; independent selected percentages [0,100]; criteria uniform, highest internal degree, or degree-weighted with a positive distribution exponent. Cross is a source percentage [0,100] per selected destination. Round nonzero fractional selections up. No pair-to-itself edges; deduplicate directed edges deterministically. Directed cycles and module self-links are legal. Array replicas participate in the module's selected population.

## Array modifier

An ordered modifier stack repeats its input on an N-dimensional lattice with axis sizes, spacing and shape. Each copy retains internal wiring and roles, with a unique instance identity. Copies do not gain inter-copy connections automatically; module self-links can supply those. Multiple arrays compose in order. Shape/projection rules match lattice modules.

## Reward groups and runtime

Each group identifies member module IDs and an actual reward pair (module ID and local generated pair index). Groups are disjoint to avoid ambiguous multipliers. Per simulated second, perceived reward is `(current - previous) / dt`. First observation initializes history without a derivative spike. Learning multiplier is perceived reward + **4 × primitive reward**. Primitive reward is supplied externally per group and bypasses differentiation. Apply this multiplier to outgoing edge updates belonging to a member source module; ungrouped edges use multiplier 1. Fixed residual plasticity remains zero. Editor runtime support is separate from the maze's existing reward code.

## Constraints and safety

Engine requires at least one external input and one non-input output; expose validation errors instead of inventing neurons. External input-panel and clock boundaries cannot receive graph edges. Internal module input roles remain free and can receive links. Pain neurons are free and inject stimulation during runtime. Enforce finite coordinates/settings, unique IDs, valid references, positive dimensions, bounded percentages, nonnegative plasticity and explicit generation limits. No silent truncation of baked topology; viewport sampling must be labeled. JSON saves use temporary-file replacement. Export writes matching model and manifest into a new export directory. The existing maze is not automatically rewired to arbitrary exported sensors/motors.

## Execution plan

1. Implement versioned document models, atomic persistence, validation and deterministic generators.
2. Implement modifiers, endpoint selection, bake mapping and predictive-model export.
3. Implement signal/reward runtime helpers without modifying existing learning equations.
4. Add native four-view UI, property editing, custom editing, link operations, history and file controls.
5. Add headless checks for generation, residual locking, recurrence, mapping, persistence, export and reward math. Build the existing app and run relevant regression checks. Document launch and limitations.

## Decisions to verify

Native editor follows the existing app. Coordinates are editor metadata. Frequency units are Hz; phase is radians. Residual density convention above is confirmed. Baking is a topology compilation operation, not training. Live sensor attachment is a host API, not a new maze integration.

## Implementation and verification

Implemented in `src/BrainEditor` as a separate executable, registered in the solution. `ArcadeLab.Viewer` exposes a shared native canvas; a software canvas renders the same drawing code for headless layout checks. The four views, nested properties, custom pairs/edges, arrays, reward membership, undo/redo, project save/load and baked save/load are implemented. Export directories contain the predictive `.pcn` and a manifest with XYZ identities, per-pair activation caps, runtime settings and embedded source. The CPU host adapter supplies signal and reward semantics; existing maze learning remains unchanged.

Validation completed:

- Editor project builds with zero warnings/errors; 60 focused editor checks pass.
- Headless CLI bake of the example succeeds: 56 pairs and 528 connections.
- All four canvas views render offscreen and were visually inspected.
- Existing predictive checks pass with 4,343 assertions, including CPU/software-Vulkan agreement; maze feedback checks pass with 6,198 assertions.
- Full-solution build remains blocked by 36 errors in legacy `tests/NeuralGraph.Checks`, referencing absent `RewardMotorPolicy` and retired `MazeSession` APIs. Those tests/APIs were not modified by this work.
- An interactive window launch was attempted, but GLFW could not initialize the available display. Desktop mouse/key behavior therefore remains unverified in this environment.

The [usage guide](../src/BrainEditor/README.md) documents launch commands, file formats, geometry conventions, integration boundaries and generation limits. Physical sensor capture and automatic maze rewiring are host integrations rather than editor actions. The editor has no live training preview; baking saves topology and initialized parameters.
