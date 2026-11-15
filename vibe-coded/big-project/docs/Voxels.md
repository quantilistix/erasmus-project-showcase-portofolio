# MYT Voxels

MYT has two general voxel building blocks, and a game built on them and on the [physics engine](Physics.md):

- **`Grid3D<T>`** (`MYT.Kit.Vulkan`): a sparse, unbounded 3D grid of cells.
- **`StressLattice`** (`MYT.Kit.Structural`): linear elastic stress analysis of anything built from equal cubes.
- **`GpuStressLattice`** (`MYT.Kit.Structural`): the same analysis on the GPU, running continuously alongside a game.
- **`src/VoxelGame`**: a sandbox where the level is a fixed body, loose objects are rigid voxel bodies, and both crack and break under impacts and under their own weight.

---

## Repeating world and terrain LOD

The world repeats by translation every 12,000 blocks on X and Y. Its canonical bounds are
`[-6000, 6000)` on both axes: crossing +6000 continues at -6000 in the same direction.
Terrain, caves, ores and trees repeat; terrain noise blends across the join without reflection.
Simulation coordinates remain continuous to avoid teleporting the camera or breaking contacts.
Edited columns use canonical save keys; loose objects, dropped items and furnaces follow the
same translated world.

**LOD Distance** in Settings is the base radius in blocks. A value of 192 means full
resolution to 192 blocks, 2-block voxels to 384 blocks, 4-block voxels to 768 blocks,
8-block voxels to 1,536 blocks, and so on. Both voxel size and spherical distance double
at each level. LOD chunks contain 16³ coarse cells, so their world dimensions also double.
Distances include X, Y and Z; flying upwards reduces ground detail. Chunk boundaries round
the exact spherical transitions. The terminal level collapses height detail smaller than one
coarse voxel to textured squares projecting onto the infinite horizon.

Coarse geometry consists of axis-aligned cube faces with explicit outward normals. It
uses the same packed vertex format, opaque pipeline, sun shadows and water pipeline as
full-detail voxels. Each coarse voxel face carries one authored block texture, so patterns
scale with voxel size instead of collapsing immediately to averaged colors. Existing texture
mipmaps filter distant faces. Water retains its original transparency, volume shading and
back-to-front rendering. Top-surface materials remain separate so grass stays
grass and craters expose stone or dirt. Features below the current voxel resolution eventually
disappear.

LOD workers read immutable column snapshots and cached occupancy/material mip levels.
Unvisited distant terrain uses bounded stratified sampling. Live edits replace the snapshots
and invalidate affected cached meshes, including translated world copies. Edited snapshots
remain available after unloading and are restored from saved columns, so craters, caves and
map constructions no longer revert to generated terrain at a distance. Already drawn meshes
remain until replacements upload, and results made before an edit are rejected.
Physics, destruction, inventory and simulation distance keep their existing behavior.

GPU uploads have per-frame budgets, and indirect draws batch terrain by arena page.
Generation visits the selected volume from nearest chunks to farthest in every direction,
including behind the camera. Camera direction only culls drawing. Generated GPU meshes stay
cached until the world is closed; looking away, waiting or walking away does not evict them.
Edits still invalidate affected meshes. Stationary cameras reuse the volume hierarchy and
cached voxel mip levels.

Run `dotnet run --project src/VoxelTests -c Release -- world-periodic terrain-lod terrain-lod-edits world-wrapped-edits`
for topology, spherical selection, face normals, crater and overhang checks. `terrain-lod-render`
needs a display/GPU, compares direct and indirect rendering pixel for pixel, and checks that
a crater changes distant pixels and persists through unloading and restored saved columns.

## Slabs and stairs

Bedrock, stone, cobblestone, dirt, sand, oak planks, acacia planks and glass have slab,
straight stair, corner stair and missing-corner stair items. Corner stairs have one raised
quarter; missing-corner stairs have three raised quarters. These remain separate block types
and do not change shape when neighbours change. Water, ores, leaves and blocks with different
textures on their faces have no variants.

Place a slab on a top/bottom face for a horizontal half. Each side face is divided by its
two diagonals into four triangular regions: the top/bottom triangles select the upper/lower
horizontal slab, while the left/right triangles select the corresponding vertical slab
along the face. Ties on a diagonal select a horizontal slab; the exact center selects bottom.
Straight stairs rise away from the player. Placing against an underside or the upper half
of a side makes upside-down stairs. On top/bottom faces, corner stairs raise the aimed
quadrant and missing-corner stairs omit the aimed quadrant. On side faces, corner stairs
raise the quarter against that face on the aimed side; missing-corner stairs leave their
notch facing out on the aimed side. This stays stable across diagonal viewing angles.
Textures keep their original grid scale, phase and face axes for every orientation.
Dropped slabs and stairs use the same shape as their item model, including its empty space.
Shape items are in the creative inventory; at a workbench, one base
block crafts one shape item (bedrock variants are creative only).

`BlockShapes` describes occupied half cells for rendering, collision and ray picking.
Shape orientation is saved in the existing voxel orientation byte; old block IDs remain
unchanged. Rigid-body mass, center of mass and inertia use the occupied volume. Structural
connectivity and elastic analysis still operate on the original whole-cell lattice.

## TNT

The default path now uses a shared GPU pressure grid, GPU material-stress solvers and GPU
damage evaluation. See [GPU voxel pipeline](GpuVoxelPipeline.md) for ownership, the acoustic
model, checks and comparison switches. The wavefront behavior below describes the retained
CPU reference selected with `MYT_GPU_BLASTS=0`; it is not an exact description of the new
acoustic field's attenuation or reflection behavior.

Punch or right-click a TNT block to detonate it immediately. Crouch-right-click places
blocks against TNT. The consumed TNT emits a traveling pressure pulse; nearby TNT
ignites when that pulse reaches it, rather than all blocks firing in one synchronous loop.

A sparse air-grid wavefront travels at 64 m/s with a 50 ms compression pulse. Pressure
stays nonnegative. Loads from every arriving wave add before damage and motion are
calculated. Opposing directions can cancel momentum while still adding damaging
compression. A single TNT has a nominal effective reach of 16 blocks in open air;
pressure attenuates as `(1 + travelDistance / 4)^-3`, and the combined-pressure threshold
extends that reach for overlapping blasts. Source positions and obstacles determine the
shape. Every source, including TNT on moving/rotated objects, injects once into its
containing global voxel (`floor(worldPosition / cellSize)`, including negative positions).
Receivers still use trilinear sampling of that shared global pressure field.

The solver approximates positive compression waves using shortest travel times through
26 neighboring air cells. Intact solid cells absorb the wave, and the front can travel
around barriers. A block that breaks while its pulse is present transmits the remaining
pulse through the opening. This is an absorbing wavefront model, not full acoustics:
there are no reflected waves or negative-pressure/suction phases. Partial blocks and
moving objects use voxel-scale occupancy for pressure transport.

Blocks use the mining elastic threshold/wear model under the combined pressure load,
with a 96 MPa source stress so nearby intact stone can break.
Surviving detached blocks receive pressure impulses at their individual positions, so
objects can both translate and rotate. Anchored terrain stays fixed until disconnected.
The same pressure field pushes the player, dropped items, chips, dust and blast puffs;
player invulnerability prevents damage but does not prevent pressure impulses. Damage,
TNT ignition and topology updates happen before particle forces, so newly spawned chips,
drops and explosion effects receive the pressure still passing through their position.

Every TNT emits 720 dust particles and 16 blast puffs using `dust0–4.png` and
`explosion0–6.png`. Both effects start at rest: their outward motion comes entirely from
pressure, including pressure from other TNT. Dust has a continuous cloud-mass distribution
of 0.1–1.15 kg (for the one-block sprite), weighted toward heavier clouds with a sparse
light tail. The same impulse gives lighter particles greater acceleration. Porous clouds
couple to pressure less strongly than solid chips. Dust couples four times more strongly
than blast puffs and spawns 0.35–0.80 blocks from the center to catch the outward front.
Spawn rays stop short of solid geometry, so dust and puffs stay on the source side of
cannon walls, including shaped and rotated blocks. Movement sweeps the entire travel
segment; particles engulfed by geometry are removed even if stationary. Blast chips
originate on the face exposed to pressure. Dust keeps a random dark gray shade
(15–50% brightness) and an independently random lifetime of 1–5 seconds. Blast puffs live
0.25–0.45 seconds. Both sprite types are one block wide (1/16 block per pixel).

All particles experience mass-dependent linear air drag and displaced-air buoyancy. Chips
use their material density; dust and puffs are lighter than air (1.225 kg/m³), so they float
after the pressure passes. Air motion is integrated analytically for stable timesteps.
Sprites render as opaque cutouts and sample local world illumination, sunlight, point
lights and shadows. A warm point light (1200 peak intensity) and local ambient boost
(120 peak intensity) flash for two rendered frames (100%, then 25%, then off). Effects
share a 32,768-particle limit; disabling particles still allows pressure and light flashes.

## Grid3D

```csharp
Grid3D<Voxel> grid = new();                 // every cell starts as default(Voxel), "empty"
grid[new ivec3(3, -2, 7)] = new Voxel(BlockType.Stone, 0);
bool solid = grid.IsOccupied(new ivec3(3, -2, 7));
```

- Cells live in 16³ chunks (`Grid3D<T>.ChunkSize`) that exist only where something is not `Empty`, so coordinates can be anything, negative included. Cell (x, y, z) covers [x, x + 1) × [y, y + 1) × [z, z + 1).
- **Change tracking.** `Set` marks the cell's chunk as changed, and the chunks across any chunk face the cell touches, since their exposed faces may change too. `TakeChangedChunks()` returns and forgets them, ready for remeshing. `Update` changes a cell's value without marking anything (for per-cell state such as damage); it refuses to change whether the cell is occupied.
- **Regions.** `CopyRegion(min, size, destination)` copies a box chunk by chunk. Meshers copy a chunk with a one-cell border and read neighbours from the copy rather than looking cells up one by one.
- **Connectivity.** `FloodFill(start, visited, include, stop)` is a face-connected breadth-first search that can stop early (for instance on reaching an anchor). `ComponentsOf(seeds)` gives the distinct groups containing the seeds, largest first; after removing a cell, its six neighbours as seeds tell whether the rest split.
- **Ray casts.** `Raycast(origin, direction, maxDistance, out Grid3DHit hit)` walks cell by cell (Amanatides and Woo) and returns the first occupied cell, the face it entered through and the distance.
- **Files.** `Save(stream)` / `Grid3D<T>.Load(stream)`, or `Grid3DLoader<T>` as an `IAssetLoader`/`IAssetSaver`.

`T` must be an unmanaged `IEquatable<T>`, such as a record struct. A grid is single-threaded.

## StressLattice: how much load each block carries

`StressLattice` answers "which blocks of this structure are overloaded?" with real units: forces in newtons, stresses in pascals.

### The model

The structure is a **rigid-body-spring lattice** (Kawai's RBSM, used for fracture of concrete and masonry):

- every cell is a rigid block with three translations and three rotations;
- every face two cells share is an elastic interface between their centers, with axial, shear, bending and torsional stiffness. Two half cells act in series: kn = A / (h/2E₁ + h/2E₂), likewise ks with the shear modulus, kb = kn·I/A with I = h⁴/12, and kt = ks·J/A with J = 0.1406·h⁴.

A row of cells then bends and twists like a Timoshenko beam with the same cross-section. After solving K·x = f (loads on every cell: gravity, contacts, inertia), each interface's force and moment give the stresses on that face:

- tension and compression: axial force / area ± bending moment / section modulus (h³/6), at the worst corner;
- shear: 1.5 × shear force / area plus torsion / (0.208·h³).

Each cell keeps the worst values over its faces (`CellStress`).

The answers match engineering statics and beam theory (`src/VoxelTests`):

| Case | Expected | Lattice |
|---|---|---|
| 12 cells hanging under an anchor: tension at the top | 12·ρg·A | exact (1e-4) |
| Cantilever of L cells: root bending stress | 3ρgL² | exact (1e-3) |
| Beam fixed at both ends: end and midspan bending | wL²/12, wL²/24 (Euler-Bernoulli) | within 2% and 1% |
| Free 2×2 bar pulled by balanced end loads | F/A | exact |
| Stone block hanging from a dirt cell | weight / A | exact |

### Anchors and free bodies

Anchored cells are held by stiff springs to the ground (1000 times their stiffest interface). A free structure must be loaded in balance, as **inertia relief** guarantees: each cell carries −m·(a + α × r + ω × (ω × r)) for the rigid-body acceleration its loads produce. The middle cell of any group of cells without an anchor is anchored automatically to remove the rigid-body motions, and then carries nothing. This is why a falling object feels no stress, while one landing on its corner does.

### The solver

Conjugate gradients preconditioned by a symmetric multigrid V-cycle:

- aggregates of 2×2×2 cells move rigidly (unsmoothed aggregation on the rigid-body modes) and form the next coarser level;
- the coarse operators are Galerkin products Pᵀ·K·P;
- smoothing is red-black block Gauss-Seidel;
- the coarsest level is solved densely.

Plain iterative solvers need about L² iterations for a slender beam of L cells (its condition number grows as L⁴); this one needs 1 for L = 16, 11 for L = 64 and 24 for L = 128. A 64×64×6 slab on four pillars solves from rest in 61 iterations, about 0.5 s on six cores. After a small load change, the previous solution restarts it within a few iterations.

The convergence check uses the true residual b − K·x: the updated residual of a long run drifts and would report convergence early.

```csharp
StressLattice lattice = new(cellSize: 1.0, cells, materials, anchored);   // CellMaterial = (E, G) per cell
lattice.Forces[i] = mass * gravity;                                        // per cell, N; Moments likewise, N·m
LatticeSolveResult result = lattice.Solve(maxIterations: 30, tolerance: 1e-5);
lattice.ComputeStresses(stresses);
lattice.ComputeStrainEnergies(energies);                                   // J per cell: half of each face's, ½·(F·δ + M·φ)
```

`SetMaterial` softens a cell (damage) without a rebuild; the multigrid hierarchy goes stale and is refreshed on request (`refreshPreconditioner`). `CopySolution` starts a rebuilt lattice from an old one's solution where cells coincide.

## GpuStressLattice: the same analysis, continuously, on the GPU

A large structure that keeps changing, such as a map being built on, is too slow for the CPU lattice: about 8 ms an iteration for 33 000 cells on twelve threads, and a few hundred iterations from rest. `GpuStressLattice` (shaders in `assets/shaders/structural`) runs the same model, conjugate gradients and multigrid on the GPU, without ever waiting:

```csharp
GpuStressLattice lattice = new(vk, device, physicalDevice, commandPool, queue, "assets/shaders/structural");
lattice.SetCells(cells, materials, masses, anchored);     // or SetMaterial(index, material) to soften one
lattice.SetLoads(gravity, contactLoads);                  // gravity on the masses, plus (cell, force, moment)
// every step:
if (lattice.Update() && lattice.HasResults)               // reads what the GPU finished, submits the next iterations
    lattice.CopyStresses(stresses);                       // with RelativeResidual telling how settled they are
```

- **Continuous.** Each `Update` uploads what changed, rebuilds what that invalidates (the multigrid hierarchy takes a handful of dispatches) and runs `IterationsPerUpdate` iterations on from the current solution. One submission is in flight at a time and is polled for with a fence; a converged lattice with nothing changed submits nothing.
- **A box.** Cells are nodes of a box around the structure with a margin, so cells that stay keep their solution as others come and go; a cell outside it grows the box. Coarser levels halve the box down to one node, which is solved exactly. The levels of 512 nodes or fewer run in a single workgroup.
- **Precision.** Stone is so stiff that rounding a displacement to float makes a force error the size of a block's weight, so the conjugate gradient's vectors and operator are double. The multigrid cycle is float, except that the coarse operators are summed in double: they are large terms that nearly cancel, and summed in float they stop being positive definite.
- **Agreement.** Its stresses match the CPU lattice's within a few parts per million. On the game's map it settles an edit in 15-25 iterations of 1-2 ms on an RTX 3060.

## The voxel game

```sh
dotnet run --project src/VoxelGame -c Release
```

The game opens on a title screen (see [Menus and saves](#menus-and-saves)). A world is played in survival or creative, chosen when it is made (see [Game modes, items and crafting](#game-modes-items-and-crafting)). In a world:

| Control | Action |
|---|---|
| WASD, mouse | move, look |
| Space / Shift | jump (a press, kept 0.1 s for the ground) / hold to crouch; flying, up / down |
| Ctrl | sprint, forward only (with W): half as fast again straight ahead, no faster to the sides |
| Enter | respawn, once dead (or the Respawn button) |
| Left mouse (hold) | strike the targeted block 6 times a second with what you hold; damage stays when you let go |
| Right mouse | place the held block on the targeted face, on the map or on an object |
| 1-9, wheel | choose a hotbar slot |
| E | inventory and crafting (E or Esc closes it) |
| Q / Ctrl+Q | drop one of the held item / the whole stack |
| T | stress view: blue idle, green, yellow, red at the limit, white past it |
| H | hide the help |
| Esc | game menu: the world holds still and the mouse is free; Back to Game (or Esc again), Settings, or Save and Quit to Title |
| F (creative) | fly or walk |
| I (creative) | invulnerable or not |
| B (creative) | break blocks in one hit or not |
| G (creative) | drop a 3×3×3 cube of the held block in front |

Environment variables:

- `MYT_VALIDATION=1`: Vulkan validation layer.
- `MYT_LOG=1`: prints every impact, every block that breaks and every hurt the player takes, and why.
- `MYT_STRESS=1`: starts in the stress view.
- `MYT_CAMERA="x,y,z,yaw,pitch"`: places the camera.
- `MYT_SCREENSHOT=file.ppm` with `MYT_FRAMES=n`: renders n frames without input, saves the last one and quits. It starts straight in a world that isn't saved, unless `MYT_MENU` names a screen.
- `MYT_MENU=title|worlds|create|servers|settings|pause|inventory`: opens on that screen. The multiplayer screen, whose button is disabled, can be seen this way.
- `MYT_GAMEMODE=survival`: plays the unsaved world `MYT_SCREENSHOT` starts in in survival rather than creative.
- `MYT_SWING_PHASE=f`: holds the held item still a fraction f (0 to 1) of the way through its swing, for screenshots of the animation.
- `MYT_SPAWN_ITEMS=n`: drops n items, every kind in turn, in a grid before the spawn point of a new world.
- `MYT_HOLD=name`: puts an item in the player's hand, by its name with underscores (`Diamond_Pickaxe`, `Oak_Planks`), or `none` for the bare arm; for screenshots of the held item.
- `MYT_WORLD=folder`: plays that saved world (its folder under `saves/`) straight away; with `MYT_SCREENSHOT`, it is saved on quitting as usual.
- `MYT_DATA=dir`: where the settings, the server list and the saved worlds are kept. The default is `MYTVoxels` in the user's local application data (`~/.local/share/MYTVoxels` on Linux).
- `MYT_SEED=n`: generates the unsaved world that `MYT_SCREENSHOT` starts in from that seed, rather than a random one.
- `MYT_SPAWN_OBJECTS=n`: adds 0–10,000 airborne objects during world generation, without waiting for shots. Their poses and velocities use the world seed.
- `MYT_SPAWN_MODE=mixed`: single stone blocks with every sixth object a 3×3×3 plank cube (default); `blocks` uses single stone blocks in a more compact arrangement. Objects start spaced apart over loaded terrain, moving and rotating in different directions.
- `MYT_RENDER_DISTANCE=n`, `MYT_SIMULATION_DISTANCE=n`: overrides those settings, in chunk columns.
- `MYT_PROF=1`: prints frame timings and a summary on exit, including completed-frame GPU render timestamps and physics CPU cost. The screenshot capture frame is excluded from the summary.
- `MYT_PRESENT_MODE=immediate`: requests uncapped presentation for performance measurements, when supported; otherwise falls back to FIFO. Normal play retains FIFO/vsync.
- `MYT_INCREMENTAL_MESH=0`: restores whole-chunk greedy render meshing for comparisons.
- `MYT_RENDER_MERGE=0`: with incremental meshing disabled, uses the original unit-face reference mesher. Collision meshing is independent.
- `MYT_INDIRECT_DRAWS=0`: submits voxel chunks with individual indexed draws for comparison with the default batched submission.

Object-count performance can be checked without a window:

```sh
MYT_VALIDATION=0 MYT_PROFILE=1 dotnet run --project src/PhysicsTests -c Release -- many-rigid
MYT_VALIDATION=0 MYT_BENCH_STEPS=500 dotnet run --project src/PhysicsTests -c Release -- crowded-rigid
MYT_VALIDATION=0 dotnet run --project src/VoxelTests -c Release -- world-many
```

`many-rigid` measures 64, 144 and 256 separated cubes on chunk-sized colliders, with the game's contact-report capacities. Set `MYT_BENCH_COUNTS` and `MYT_BENCH_STEPS` to change that workload. `crowded-rigid` measures 414 touching cubes; `MYT_BENCH_COUNT` and `MYT_BENCH_LAYERS` change its size and stacking. `world-many` measures 144 voxel objects and a player with contact loads and stress analysis running; set `MYT_WORLD_COUNT=414` for the larger workload. These report simulation cost, rather than rendered FPS. Shape complexity, dense piles, streaming and fracture rebuilds also affect frame time.

The physics copies only live contact reports back to the CPU, reuses its per-step arrays, groups small rigid bodies into smaller solver clusters and skips distant bodies through a refit body hierarchy. Nearby-body candidates are shared across each body's triangles; full candidate lists fall back to hierarchy traversal. Gameplay rays reject objects outside their bounding spheres before walking their voxel grids, and contact-load smoothing reuses scratch storage. The `world-raycast` test compares this culling with a full grid walk for rotated objects and for edits outside an object's previous bounds. `body-candidates` checks overflow with 80 overlapping colliders and refresh after teleporting away.

Further optimizations specialize the rigid solver's fixed-size arithmetic, cache vertex directions through each substep, defer unnecessary warm-start lookups, and skip GPU model-matrix copies that the voxel renderer does not use. Stress-damage checks reuse collections and skip empty watch lists; object membership and incoming stress results use an ID index. Materials, timestep, iteration count, continuous collision passes and contact limits are preserved.

The opt-in terrain collision path merges coplanar exposed faces of the same block type into rectangles.
This preserves the occupied boundary, cavities, face orientation and materials. Rectangle
edges are split at shared corners, including across chunks of one collider, before
triangulation, so the collision cooker's edge adjacency and vertex cones remain valid.
Constrained ear clipping retains those boundary points without degenerate triangles or
extra face-center vertices. Each streamed chunk remains a separate fixed collider.

Moving voxel objects retain their original cell-scale tessellation. Removing their face
samples changed local impact loads and raft momentum in regression tests, even though the
geometric boundary was unchanged. Simplifying them requires persistent contact patches
and a validated mapping from patch impulses to cell forces and moments. The other planned
architecture changes—sleeping islands with cached structural support loads, fully stable
GPU geometry slots and compact asynchronous reporting—remain subsequent stages.

Set `MYT_MERGED_COLLISION=1` to benchmark terrain merging. It remains disabled by default:
the `player-crushed` regression changed the crush outcome with sparse terrain contacts,
despite preserving the exact geometric boundary. `MYT_UNMERGED_COLLISION=1` explicitly
selects original terrain tessellation, taking precedence over the experimental flag.
`collision-meshing` checks surface coverage, material boundaries, cavities, unmatched edges,
volume, center of mass and inertia on structured and seeded random shapes. The standalone
`collision-meshing-benchmark` alternates original and merged build-and-cook paths after
warmup. For its 64×64 stepped slab, triangles fell from 20,352 to 540, median construction
and cooking from 57.3 to 6.1 ms, and managed allocations from about 26.5 MB to 0.76 MB.
These are geometry preparation measurements, not rendered frame times.

The rigid solver also uses the exact equal-tangent-stiffness Hessian identity and a 3×3
solve for fixed rotation, retaining geometric stabilization and exact lever-arm rotation.
Stable parallel work compaction now uses conflict-free color snapshots, a serial overflow
queue and sorted small contact lists, as described below. Geometry merging remains
experimental: geometrical equivalence alone does not preserve local damage and support.

Repeated small uniform voxel boxes share a bounded cache of their original cell-resolution
collision meshes and materials. This also shares the physics cooker's per-mesh BVH and feature
cache across shots and drops. Geometry checks distinguish position, size and material; holes
and heterogeneous objects use the normal mesher, and health-dependent mass and inertia are
still computed per body. Contact processing resolves each body's start-of-step pose and its
inverse once, reusing them for surface-cell lookup, facing, load rotation and impact points.
Topology rebuilds retain the large CPU collision arrays and cache fixed terrain's outward-rounded
triangle bounds, rather than reallocating and recalculating unchanged geometry. They reuse
the completed rigid-state readback instead of downloading that same state again. Soft bodies
which disable CPU synchronization retain their required download.

Final isolated measurements on an RTX 3060, Release build, validation off, with 300 measured
steps and gameplay contact reporting:

| Separated rigid objects | Mean physics step | p99 physics step | Mean GPU time | Managed allocation per step |
|---|---:|---:|---:|---:|
| 400 | 2.362 ms | 3.746 ms | 1.753 ms | 0 B |
| 1,000 | 3.395 ms | 5.074 ms | 2.777 ms | 0 B |
| 5,000 | 10.013 ms | 11.286 ms | 8.352 ms | 0 B |

Support impulse remained within 0.01% of weight, resting speeds passed and no contact,
reference or report overflow occurred. This measures separated active simulation, not
rendered frame time during shooting or fracture.

The rotating barrage comparison used the same inputs before and after storage/shape reuse,
with the optimized solver already enabled in both runs. Frames 3,000–3,599 exclude startup
and the final screenshot. The scenes vary because physics timesteps and background damage
analysis follow wall time:

| Barrage metric | Before reuse | Final |
|---|---:|---:|
| Mean moving objects | 393 | 399 |
| Mean contacts | 7,686 | 8,516 |
| Mean frame | 28.16 ms | 27.08 ms |
| Median frame | 25.6 ms | 19.2 ms |
| p99 frame | 84.6 ms | 78.5 ms |
| Median rebuild at 350–430 rigid bodies | 10.35 ms | 6.90 ms |
| Full collections over all 3,600 frames | 517 | 162 |

The subsequent resident-buffer, persistent-identity, scheduling and parallel-contact changes
were measured with the same rotating barrage and frame window:

| Barrage metric | Previous reuse pass | Current architecture pass |
|---|---:|---:|
| Mean moving objects | 399 | 388 |
| Mean contacts | 8,516 | 7,888 |
| Mean frame | 27.08 ms | 21.77 ms |
| Median frame | 19.2 ms | 17.8 ms |
| p99 frame | 78.5 ms | 66.9 ms |
| Median rebuild at 350–430 bodies | 6.90 ms | 5.50 ms |
| Median contact gathering per fixed step | 5.2 ms | 2.1 ms |
| Mean fixed steps per frame | 1.50 | 1.28 |
| Full collections over all 3,600 frames | 162 | 152 |

These runs have different body/contact counts because they advance on wall time. The
mean-frame improvement is about 20%, but cannot be attributed solely to the code changes.
Frame-time spikes and collection frequency remain too high; smooth gameplay with thousands
of interacting objects is not achieved. Separated-body timing alone does not establish it.

Validation: the solution builds in Release with existing warnings; all 38 physics scenarios
pass with zero Vulkan validation errors. The earlier full 74-scenario voxel suite has three failures
also observed with baseline geometry: `health-model` expects fresh stone to break at 50 HP
while the configured material requires 500 HP; `world-cantilever` breaks at about 3.3 s against
a 2.5 s limit; `world-through-glass` is below its expected exit-speed range. Test tolerances
and material constants were preserved. The default crushing, raft, impact, shape-cache and
meshing checks pass. Experimental terrain merging still fails the crush outcome and stays
disabled by default.

Physics benchmarks print p50/p95/p99, GPU time, candidate pairs, moving triangles and CPU
recording/wait/readback times. Game profiling reports these counters and the number of
fixed steps per frame, allowing catch-up work to be distinguished from one-step cost.

```sh
dotnet run --project src/VoxelTests -c Release -- collision-meshing collision-meshing-bedrock collision-meshing-benchmark
MYT_VALIDATION=0 MYT_BENCH_COUNTS=400,1000,5000 MYT_BENCH_STEPS=500 dotnet run --project src/PhysicsTests -c Release -- many-rigid
MYT_VALIDATION=0 MYT_WORLD_COUNT=414 dotnet run --project src/VoxelTests -c Release -- world-many
MYT_VALIDATION=0 MYT_MERGED_COLLISION=1 MYT_WORLD_COUNT=414 dotnet run --project src/VoxelTests -c Release -- world-many
MYT_PROF=1 MYT_PROFILE=1 dotnet run --project src/VoxelGame -c Release
MYT_PROF=1 MYT_PROFILE=1 MYT_AUTOPILOT=spin MYT_AUTOTHROW=1 MYT_SEED=1 MYT_CAMERA=32,32,25,0,-0.3 MYT_FRAMES=3600 MYT_SCREENSHOT=/tmp/barrage.ppm dotnet run --project src/VoxelGame -c Release
MYT_PROF=1 MYT_PROFILE=1 MYT_SPAWN_OBJECTS=1000 MYT_SPAWN_MODE=mixed MYT_SEED=1 MYT_CAMERA=32,32,25,0,-0.3 MYT_AUTOPILOT=spin MYT_FRAMES=600 MYT_SCREENSHOT=/tmp/bulk.ppm dotnet run --project src/VoxelGame -c Release
```

`spin` hovers at the selected camera position and rotates through all directions. With
`MYT_AUTOTHROW=1`, it shoots stone every 10 frames and drops a 3×3×3 plank object every
60 frames after warmup. Screenshot mode releases the mouse and terminates at the chosen
frame. This workload exercises topology changes, impacts, fracture, terrain collider coverage
and CPU structural loads. Frame-indexed inputs are repeatable, but wall-clock timesteps and
background analysis make the resulting scene nondeterministic. Compare body/contact counts
and fixed steps as well as frame time; exclude startup and the final screenshot capture.

The execution follow-up adds resident solver/collision buffers with capacity growth and
changed-range uploads for immutable geometry. A same-size rebuild regression allocates
no collision buffers and uploads about 41% fewer bytes; add/remove/grow/shrink tests verify
state after index compaction. Contact-cache identities now use fresh surface lifetime handles and body-local features,
so unrelated add/remove operations preserve warm-start forces even though dense detection
indices change. Reattachment, replacement and material or simulation-property edits receive
fresh handles. Full CPU geometry assembly and full mutable-state initialization remain.

Contact processing now uses a parallel map followed by ordered reduction. Workers read
immutable start poses and occupancy and compute cell selection, local force and face
moment into disjoint staging slots. The main thread reduces in report order, preserving
floating-point accumulation, impact grouping and player support. The retained staging
array contains values and current-step body indices, so it cannot retain detached bodies.
Small batches stay serial; use
`MYT_PARALLEL_CONTACTS=0` to run the reference path. `contact-mapping` compares both
paths exactly across rotated surfaces, stacked bodies and player pushes.

Sustained loads use a stable counting scatter into per-body contact-side slices. Each body
has one worker, so its cells receive additions in precisely the original report order,
after the player's pushes. This permits parallel force/moment reduction without atomics,
floating-point reassociation or concurrent dictionary writers. Impacts and player support
still fold on the main thread in global report order. Small workloads stay serial;
`MYT_PARALLEL_LOADS=0` selects the serial reference. A per-step owner index and a pose array
replace repeated owner/pose dictionary walks. Staging records store body indices instead
of references and two complete transforms per report, and need no per-report reference clearing.
`contact-mapping` additionally
compares forces, face moments and dictionary insertion order against the serial fold.

GPU coloring now reads snapshots and removes remaining conflicts into an ordered serial
overflow queue. Stable tiled parallel work compaction is enabled. Continuous collision
passes prune new conflicts and rebuild work lists before the next solve, including
contact-count bin changes. Normal color dispatches contain no touching rigid bodies;
overflow work has one workgroup with barriers between bodies. This removes the unsafe
in-place scheduling prerequisite, although overflow can still limit throughput. Rigid
adjacency lists up to 256 contacts are sorted by feature key before force accumulation
to reduce sensitivity to atomic contact enumeration order. The bitonic network uses the
next power of two of the actual list size, avoiding a 256-entry sort for small lists.

Color refinement now starts from each body's previous scheduling hint across substeps and
topology rebuilds. The hint travels in an unused rigid-state padding component through the
existing upload/readback, without another GPU transfer or a changed rigid-state stride.
It does not certify the new graph: snapshot refinement and conflict pruning still validate
every dispatch. Four rounds initialize both color snapshots, including a single-body scene.
`work-lists` compares retained hints with a CPU refinement reference after unrelated spawns.
The profiler's `serial` counter reports how many bodies require the serial overflow queue.

Bulk generation exposed a further cost: each of up to 2,048 texture-chip particles walked
the complete object list for its short collision ray. `ParticleRaycaster` now builds a
conservative hierarchy of object bounds once for the batch and refits it in linear time
while membership stays the same. Dirty objects bypass stale bounds; map collisions still
use the exact grid walk. Leaves use the same voxel raycast and ties retain map/object
order. `world-raycast` compares against a full grid walk before cooking, after edits and
after motion/refitting. `particle-raycast` checks 2,048 rays across 1,000 rotated objects:
the reference scan took 192.2 ms, refit 0.22 ms and indexed queries 4.21 ms, with no
query allocations and no differing hits. This is an isolated CPU ray workload.

The 600-frame seeded bulk scene with 1,000 mixed objects completed with zero Vulkan
validation errors. Before/after the debris hierarchy (before the subsequent refit change),
frames 61–599 averaged 49.0/28.9 ms, with p99 286.5/132.9 ms. This excludes startup
and screenshot capture; resulting scenes differ slightly, and these are not 60 FPS guarantees.

The 300-frame, validation-off, 5,000-block falling pile was repeated before and after
hierarchy refitting. Frames 61–299 excluded startup and capture:

| Metric | Rebuild hierarchy each step | Refit unchanged membership |
|---|---:|---:|
| Mean moving objects | 5,015 | 5,023 |
| Mean contacts | 49,197 | 48,939 |
| Mean frame | 173.2 ms | 141.1 ms |
| Median frame | 141.5 ms | 120.4 ms |
| p99 frame | 393.9 ms | 404.1 ms |
| Median debris update in late frames (three steps) | 20.75 ms | 3.30 ms |

The dense pile remains expensive and takes three catch-up steps per frame. GPU contact
detection/solving and CPU contact-load reduction dominate the late profile. Use this scene
to assess the remaining architecture, rather than treating separated-body timing as gameplay FPS.

The subsequent body-owned load reduction, compact contact staging and retained color hints
were measured with the same seeded 300-frame, 5,000-block workload. A further pass deduplicates
terrain requests. Frames 61–299 exclude startup and screenshot capture:

| Metric | Before load/scheduling pass | After load/scheduling pass | With terrain range deduplication |
|---|---:|---:|---:|
| Mean moving objects | 5,023 | 5,026 | 5,024 |
| Mean contacts | 48,939 | 49,644 | 48,721 |
| Mean frame | 141.1 ms | 92.8 ms | 90.9 ms |
| Median frame | 120.4 ms | 83.4 ms | 82.0 ms |
| p99 frame | 404.1 ms | 147.3 ms | 145.5 ms |
| Median late contact reduction per step | 3.83 ms | 1.50 ms | — |
| Median late contact gathering per step | 9.50 ms | 6.93 ms | — |
| Median late terrain following per step | 1.13 ms | 1.07 ms | 0.60 ms |

These are individual gameplay runs, with some trajectory and timing variation. The first
pass reduced mean frame time about 34% despite a slightly higher mean contact count. The
terrain pass's smaller frame improvement also includes slightly fewer contacts. The scene
continues to take three fixed steps per frame and remains well above the smooth-frame target.

Terrain requests keep the same speed-dependent reach and chunk quantization. Union is
idempotent: expanding an identical integer range twice adds no chunks. `ChunkCoverage`
expands each distinct range at its first occurrence, preserving both exact coverage and
first-insertion order. Work scales with the number of bodies plus the expanded volume of
distinct ranges, rather than expanding every body's range separately. Both range and chunk
sets retain capacity and are cleared every step, so motion and support edits remain current.
`collider-coverage` compares 5,003 requests against the original expansion, including negative
coordinates, boundaries, height clipping, repetition and clearing. It measured 3.82 ms for
the original expansion and 0.73 ms with deduplication, with zero steady-step allocations.
The streaming, contact mapping, drop and player crushing regressions pass.

Collision topology assembly now assigns deterministic disjoint body ranges before filling
them in parallel. Jobs retain transform and palette values instead of allocating callbacks
per body. Cooking, palette assignment and shared BVH allocation remain sequential; workers
apply the original transforms, index offsets and outward-rounded bounds independently.
`MYT_PARALLEL_GEOMETRY=0` selects serial assembly; small rebuilds also stay serial.
The warmed 5,000-body mixed-shape benchmark measured 3.49 ms serially and 1.21 ms in
parallel. Cached serial assembly allocates zero managed bytes; parallel scheduling has
bounded task overhead. All geometry buffers match serial assembly byte for byte, and a
separate comparison with the committed original implementation also matched after motion
and removal. Vertex-owner checks independently validate report body IDs. This reduces
rebuild overhead while the stable-slot architecture remains planned.

The object stress worker now schedules only dirty bodies in a priority queue, ordered by
their waiting time. New inputs to an already queued body retain its place; unfinished solves
rejoin at their new waiting time. Removed lifetimes are invalidated even if an old queue entry
remains, so reusing an ID cannot revive removed work. This changes scheduling from repeated
full-body scans (quadratic for a dirty batch) to heap operations, `O(N log N)` per batch.
The convergence tolerance, iteration budget, inertia relief and stress equations stay the
same. A separate 5,000-body comparison processed the original scan in 452 ms and the queue
in 123 ms, with no differing stresses. `stress-scheduling` checks 1,000 bodies against direct
lattice solutions and repeats after removing queued lifetimes and reusing their IDs.

Single-cell objects use an exact structural shortcut. `ComputeStresses` takes maxima over
cell-to-cell interfaces; with no neighbour every maximum is zero, regardless of gravity,
contact force/moment, material or angular velocity. The worker publishes a settled zero
snapshot with the current topology version without constructing a lattice, and the world
skips repeated structural load messages for these objects. Contact reports, loads on other
bodies and impact damage still run. Growth to multiple cells restores the full solve;
shrinkage discards the old lattice. `stress-single-cell` checks arbitrary loads and both
transitions. This identity applies to this model's internal structural stress, not to impact
strength or the stress inside a larger body.

The final collision assembly passes all 39 physics scenarios with zero Vulkan validation
errors. The broad voxel run after the scheduling change passed 85 of 88 scenarios; only the
three baseline failures described above remained. After adding the singleton shortcut, all
10 targeted structural/gameplay checks passed, including building, splitting, detaching,
impacts, many objects, player crushing and drops. Release builds retain existing warnings;
material constants and test tolerances were not changed.

The final 5,000-block run, including all three changes, measured the following against
the earlier load/scheduling build. Frame statistics use frames 61–299; geometry statistics
include every rebuild in each 300-frame run:

| Metric | Before this pass | Final build |
|---|---:|---:|
| Mean moving objects | 5,026 | 5,017 |
| Mean contacts | 49,644 | 49,155 |
| Mean frame | 92.8 ms | 90.6 ms |
| Median frame | 83.4 ms | 82.2 ms |
| p99 frame | 147.3 ms | 132.0 ms |
| Median CPU geometry rebuild | 5.77 ms | 2.34 ms |
| p95 CPU geometry rebuild | 34.55 ms | 10.70 ms |
| Median late structural load submission per step | 0.67 ms | 0.13 ms |

The profiler's measured-window GC pause dropped from 1,645 to 562 ms. These individual
runs differ slightly in resulting contact counts and asynchronous damage. The intermediate
parallel-rebuild run averaged 98.6 ms despite faster rebuilds, so isolated CPU wins should
not be presented as guaranteed frame improvements. The final build still spends about
14 ms on GPU physics per step and takes three catch-up steps per dense frame. It remains
well above the 16.7 ms frame target.

The final 600-frame mixed scene generated 1,000 objects, including 3×3×3 blocks. Frames
61–599 averaged 1,036 moving objects and 18,723 contacts, with mean 22.59 ms, median
13.2 ms and p99 120.9 ms. This has much faster typical frames than the 5,000-body pile,
but streaming, impacts and topology changes still cause stalls; the median alone does not
establish smooth gameplay. After the initial streaming and impact surge, frames 201–599
averaged 13.16 ms with p99 19.4 ms and maximum 27.8 ms. This establishes the steady
mixed-pile result for this run, not the cost of a continuing barrage or fresh streaming.

Persistent patches, island sleep/wake, GPU load mapping and stable feature slots remain
planned. The current dense profile puts repeated narrow-phase feature tests (about 6 ms per
step) and CPU contact gathering (about 7 ms per step) ahead of further assembly tuning.
The mixed scene also exposes streaming and impact/topology stalls. Stable slots remain
necessary for those tails, even though parallel assembly reduces collision rebuild cost.

| Stage | Mathematics and architecture | Accuracy gate |
|---|---|---|
| Incremental topology | Separate immutable shape geometry from mutable body slots. Use stable handles with generations, free lists and capacity growth; upload only added/edited ranges. Maintain separate static terrain and dynamic body hierarchies. Changing body indices must not invalidate every triangle, material or cached contact. | Add/remove/fracture/teleport preserves motion and material identity; stale handles cannot address reused slots; steady spawn cost scales with changed geometry. |
| Persistent contact patches | Group connected coplanar contacts with compatible materials. Select extremal support points and retain a persistent patch frame. Reduced points must preserve net impulse `ΣJ` and moment `Σ(r×J)`, friction torque and unilateral feasibility. Map the patch footprint back to occupied surface cells for structural forces. | Player crushing, raft momentum, stack stability, unequal-mass impacts and local fracture outcomes remain within baseline tolerances; exact surface coverage alone is insufficient. |
| Island sleep and wake | Build connected contact islands. Sleep only after low kinetic energy, small constraint residual and stable support for a sustained interval. Cache the island's balanced cell loads and continue structural damage analysis while it sleeps. Wake the whole island on impact, support loss, fracture, edits or applied force. | Sleeping objects continue loading and damaging supports; edited supports collapse and fast projectiles wake targets before solving. Never replace support forces with zero. |
| Conflict-free GPU scheduling (implemented for rigid contacts) | Snapshot coloring and conflict pruning make ordinary rigid colors independent. Overflow uses ordered single-workgroup Gauss-Seidel. Stable tiled work compaction runs in parallel; continuous detection refreshes this contract before further solves. | Dense piles and reordered body creation preserve stability, support and momentum; no two same-color writers can read each other's changing pose. |
| GPU cell-load reduction | Rotate/map reports using start-of-step poses, then sum forces and face moments by body/cell. Keep impact energy, pair lifetime and player support data. Copy compact aggregates with topology generation tags; delay only consumers whose time semantics allow it. | Compare per-cell forces/moments and impact energy against current CPU gathering before replacing it. Player contact response and topology edits remain current; delayed results cannot affect a changed body. |

For a patch with a common penalty matrix `K` and unchanged active constraints, the
linearized rigid Hessian is `Σ Bᵢᵀ K Bᵢ`, where `Bᵢ = [I, −[rᵢ]×]`. Its assembly can use
sample count, `Σrᵢ` and `Σ(rᵢrᵢᵀ)` instead of repeating every 6×6 outer product. Net force
and torque are `Σfᵢ` and `Σ(rᵢ×fᵢ)`. Split patches when normal, material, penalty stiffness
or stick/slip state differs; rebuild their moments when membership changes. This identity
is conditional on that common active set. Individual unilateral constraints, curved-contact
stabilization and impact CCD cannot simply be discarded because their surface is planar.

Use the rotating barrage and separate 400/1,000/5,000-body benchmarks after each stage.
Track median and tail frame time, rebuild time, managed allocation, contacts and candidate
pairs, report overflow, penetration, momentum and structural outcomes. A 16.7 ms median
frame budget and 33 ms tail budget are targets; the separated-body benchmark does not
establish either for dense gameplay.

The first optimization pass measured on an RTX 3060 with 500 measured steps per workload, validation off, 10 solver iterations and one continuous collision pass:

| Rigid objects | Original mean step | Optimized mean step | Reduction |
|---|---:|---:|---:|
| 64 | 3.00 ms | 1.88 ms | 37% |
| 144 | 3.33 ms | 2.07 ms | 38% |
| 256 | 3.88 ms | 2.21 ms | 43% |
| 512 | 5.25 ms | 2.64 ms | 50% |
| 1024 | 7.94 ms | 3.64 ms | 54% |

The optimized physics benchmark allocated 0 managed bytes per steady-state step at every count; the original allocated about 22 KB at 144 objects and 166 KB at 1024. These timings use separated resting cubes, with full gameplay contact reporting; dense piles, impacts, complex shapes and rendered frames have different costs.

The follow-up touching-cube workload kept 11,698 contacts and 1,656 reports with 414 objects. Mean physics step time fell from 5.87 ms after the first pass to 5.12 ms after the follow-up (about 13%, 500 measured steps). The 414-object voxel-world workload reduced main-thread allocations from about 111 KB to about 1 KB per step by removing temporary stress-damage collections (120 measured steps). These controlled scenes do not reproduce every crowded gameplay pile or predict its rendered FPS.

New worlds start on raised terrain without buildings or loose demonstration objects. The hand-made physics fixture, available through `TerrainGenerator.Generate`, retains these structures for tests:

- a stone cantilever at 62% of stone's tensile elastic limit;
- two stone blocks hanging by one dirt block at 83%;
- a plank bridge;
- a glass wall;
- trees, with roots and branches;
- two loose objects.

Around it the world goes on wherever the player goes ([below](#the-endless-world)).

### GPU particle billboards and terrain handoffs (November 2026)

Particles use a single compact GPU stream and draw call. Each visible particle uploads a
36-byte center/size, texture crop/layer and dust shade record. A geometry shader turns each
point into a four-vertex triangle strip. Devices without geometry shaders use the same
stream with four instanced GPU vertices; `MYT_PARTICLE_GEOMETRY=0` selects that path for
comparison. The CPU no longer sorts opaque cutouts or expands six vertices per particle.
The old `BuildVertices` method remains a lazy CPU reference for physics/geometry tests.

The fragment shader samples RGB block irradiance at the fragment's world position without
a voxel-face offset. Direct sunlight and local point lights use the billboard's actual
camera-facing normal. Its brightness therefore changes with camera orientation, as intended.
Sprite alpha cutouts, depth writes, animation, texture crops and simulation behavior remain.
At the 32,768-particle limit, the upload is 1,179,648 bytes instead of 6,291,456 bytes (81.25%
less). One local CPU stream benchmark measured about 1 ms with zero allocations per build.
This does not measure simulation cost or total TNT frame time.

Terrain selection retains a nonoverlapping cached cover while new LOD meshes generate.
It requests the selected fidelity directly, nearest first. Ready detailed chunks and finer
LOD children remain visible; cached coarse meshes are clipped to the missing sibling
regions in both color and shadow passes. Previously rendered children also remain while
a coarser requested tile builds. The renderer does not generate a coarse-to-fine ladder
or replace ready nearby detail with a parent mesh. Unchanged coverage and tile-version
checks are cached. Dirty chunks invalidate old background jobs immediately, including
while waiting for neighbouring columns to load.

Checks: `particle-stream`, `particle-rendering`, `terrain-cover`, and `terrain-lod-render`.
The particle GPU check compares both expansion paths and verifies normal-dependent sunlight
and local furnace lighting. Particle collision, containment and cannon tests still exercise
the CPU simulation independently of rendering.

### Deferred meshing and bounded lighting (November 8, 2026)

Render meshes now update on background workers by default, including nearby terrain and
moving debris. The main thread captures immutable padded snapshots under a 1 ms soft
budget, with at most 32 jobs outstanding. Each chunk has one mesh-state owner and one
coalesced pending edit. A finished intermediate snapshot may be displayed while a newer
edit waits, allowing ongoing explosions to make visible progress. Visible nearby chunks
get priority; work older than 250 ms gets a turn to prevent starvation.

The previous mesh remains until a replacement is ready. Empty chunk removals wait for
adjacent replacement surfaces to reach the GPU; dependent empty chunks commit together.
Body removal, unloading and world clearing invalidate old record identities. Uploads have
separate completed/displayed versions, so a new CPU edit cannot repeatedly cancel an
otherwise useful finished upload. Transfers use a 2 MiB/frame byte budget and roughly
1 ms of CPU bookkeeping. A single mesh larger than the byte budget may proceed alone
within the physical staging capacity. Snapshot and upload budgets yield between jobs,
not inside an individual copy. Singleton debris retains its six-face fast path.

Indirect daylight accepts small changes at a lower cadence: at least 200 ms between
updates, a 1/512 change threshold, and a one-second maximum hold for nonzero smaller
changes. Major changes and lighting toggles apply immediately. Direct sun, sky rendering
and sun shadows still use the current frame's parameters. Block edits and emitters wake
local light tiles independently of the daylight cadence.

Bounded GPU diffusion preserves a pending frontier instead of launching every active
8-cubed tile. Nearby newly invalidated regions receive priority, with remaining capacity
available to a reserved, contiguous rotating fair window, followed by an opportunistic
fill pass. The fair window prevents continuous urgent work from starving distant tiles.
A two-step batch processes the same selected tiles:
the second step reads B for selected neighbours and current A for unselected neighbours.
It does not read stale ping-pong values from deferred regions. Changes requeue the tile
and its neighbours; unselected work remains pending. GPU timings adjust the tile cap
between 128 and 4096 toward a 2 ms diffusion target. Small asynchronous dispatch-count
readbacks provide diagnostics without waiting on the GPU. The requested cap remains a
work bound, not a guarantee against driver stalls or other GPU workloads.

The asynchronous image test exercises rapid edits, deletion, clearing, and unloading a
column while a worker is in flight. Its final pixels match the synchronous reference.
`lighting-budget` forces an eight-tile limit and checks distant-source progress,
convergence, extinguishing sources, growth preserving existing light, a sealed room
opening and closing, and progress despite continuous urgent edits in another region. The original exhaustive compute comparison remains available.

Fresh paired captures used identical copies of `better trees`, particles disabled, an
off-screen TNT cell at (-564,268,79), fixed 1/60 simulation dt, and 480 frames. No Blender
process was observed during these runs. The control and optimized captures used the same
build; the control enabled synchronous meshing and disabled both lighting changes.
After the first 60 frames:

| Measurement | Control | Deferred/budgeted |
| --- | ---: | ---: |
| Median frame-loop interval | 19.0 ms | 17.9 ms |
| Mean frame-loop interval | 20.84 ms | 20.95 ms |
| Mean completed GPU frame | 9.86 ms | 4.62 ms |
| Median light diffusion | 7.8 ms | 0.8 ms |
| Peak detonation frame | 83.6 ms | 82.0 ms |

Changed-mesh request-to-upload latency was about 31 ms median and 46 ms maximum in that
capture. During frames 301–330, main-thread mesh snapshots took at most 0.05 ms/frame.
These results show a large lighting reduction, but average frame time was effectively
unchanged. The off-screen detonation still has a substantial CPU blast/topology/physics
hitch; the small peak difference is not evidence that hitch is solved.
Earlier captures reported alongside Blender activity are not controlled A/B evidence.

Comparison flags: `MYT_SYNC_MESH=1`, `MYT_LIGHT_BUDGET=0`, and `MYT_DAYLIGHT_DEFER=0`.
For reproducible screenshot runs, `MYT_FIXED_DT=0.016666666666666666` fixes simulation dt;
`MYT_AUTOPILOT=tnt`, `MYT_AUTOTNT=300`, and `MYT_AUTOTNT_CELL=-564,268,79` fix the blast.
`MYT_PROF_EVERY_FRAME=1` writes all frame samples when `MYT_PROF=1` is enabled. Use save
copies, since normal autosaving remains active. Rendering latency is separate from the
simulation: collision geometry and pressure still update at their existing fixed steps.

### TNT frame-time follow-up (November 2026)

Pressure transport now keeps pooled, flat arrays of visited travel times in each live
source's bounded neighborhood. Only queued local neighbors propagate; there is no loaded
world or chunk sweep. The 26-neighbor stencil retains diagonal transport and wall corner
blocking, but shares six solid-face probes across all neighbors. Precomputed array strides
replace per-neighbor coordinate hashing. The queue groups arrivals into face-travel time
bins (1/64 second); no edge can connect arrivals within one bin, so only bins need heap
ordering. Exact arrival times still determine pulse overlap, including steps smaller than
a bin and catch-up steps. Visited arrays grow when overlapping sources extend support,
then return to the pool when the wave finishes. Simultaneous sources in the same global
cell share one traversal and add strength. Pressure and momentum remain additive.

Off-grid TNT no longer injects up to eight independent trilinear wave searches. It uses
one containing global cell; this intentionally quantizes the pressure source by at most
one voxel. Particle emission positions remain at the actual TNT center. Damage/impulse
target bounds now use current-step field support rather than the blast's eventual radius,
so objects ahead of the front avoid premature occupied-cell scans. Transport still runs
on the CPU every fixed simulation step, including when off-screen or particles are off.

`pressure-grid-benchmark` isolates transport in empty air over 80 steps, with five measured
runs after warm-up. Before this change, local medians were 32.66 ms for one centered TNT
and 3424.33 ms for eight neighboring TNT; after the change they were 7.44 ms and 876.87 ms.
These totals cover the full simulated blast, not one frame, and exclude source injection,
damage, collision rebuilds, and rendering. Integrated sampled pressure remained identical
(0.375 and 3.2252761192298229). `pressure-waves` checks timestep invariance, wall release,
positive compression, momentum cancellation, and grid snapping at negative coordinates;
`tnt-global-grid` checks a rotated object's actual detonation against the global-grid field.

Profiling a detonation in a copy of the saved `better trees` map separated blast work from
particle rendering. Blast processing occupied roughly 50–70 ms in the slowest captured
frames, including collision/topology rebuilds and enumerating distant objects. Particle
updates were around 2–4 ms. Render-only diagonal AO dependencies had also been reaching
the collision rebuild path.

Blast collision queries and target enumeration now conservatively reject objects outside
the wave's possible support, including global source snapping, overlapping sources and the
current outgoing pulse. Pressure still advances at each simulation step. Cell-center
samples use one field lookup; interpolation retains the original weights. Unchanged
geometry reuses pressure-solid samples through the remainder of a step. Separate render
and face-only collision dirty sets prevent AO-only neighbours from recooking colliders.

`tnt-spatial-benchmark` compares the same 40-step blast with 256 distant objects: a local
run measured 113.8 ms for full scans and 35.8 ms with support bounds (3.2x), with identical
field values. This isolates blast processing, not total frame time; collision cooking and
physics submissions remain separate costs. Independent pulses restart their internal time
origin once no wave is live, preventing floating-point drift between repeated blasts.
`pressure-support`, `pressure-waves`, `tnt-chain`, `tnt-superposition` and
`mesh-collision-invalidation` cover the optimized dependencies and pressure behavior.

For an end-to-end diagnostic on a temporary save copy, use `MYT_PROF=1`,
`MYT_AUTOPILOT=tnt`, and `MYT_AUTOTNT=120` to detonate at frame 120. The profile separates
`blast.solids`, `blast.propagate`, `blast.damage`, `blast.topology`, `blast.particles`,
`lod.requests`, `lod.cover`, and `lod.total`.

A particles-off, off-screen `better trees` capture after the local-grid change still had
a 78.7 ms detonation frame, with 11.8 ms in `blast.propagate` on that first invocation,
9.9 ms in solid preparation and 9.2 ms in damage. Later reported propagation samples were
0.3–2.0 ms/frame. This single run does not establish an end-to-end speedup over
the earlier 82 ms capture; the full detonation hitch remains, despite faster warmed-up
transport. Logs: `/tmp/myt-pressure-map-optimized.log`,
`/tmp/myt-pressure-final-cpu.log`, and `/tmp/myt-pressure-world-tests.log`.

### GPU culled rendering (November 2026)

The default asynchronous renderer generates edited cube chunks with `culled_mesh.comp`.
CPU workers pack immutable 18³ padded snapshots (one material/orientation/lit tag per
voxel); the compute shader determines face visibility, corner AO, diagonal splits, texture
layers and rotations, then writes compact packed vertices directly into device-local arena
pages. Each chunk uses a 256-thread workgroup and a prefix sum; each lane owns sixteen
consecutive cells. This preserves the exact CPU cell/face order, including blended water,
without a per-face atomic allocation or a fixed array of invisible quads.

Eight chunks at most dispatch per frame and share the mesh upload byte budget. Each result
owns a new maximum-capacity arena range (768 KiB), trading VRAM for allocation simplicity;
old ranges retire after frames in flight finish. The renderer reads only face count and
water presence (eight bytes per chunk) when the normal frame fence completes. There is no
blocking mesh readback; previous geometry remains visible until its replacement is ready.
Empty chunks retain the existing neighbor-version dependencies. Clear/unload invalidates
record identity so late GPU completions cannot restore old-world terrain.

Untouched chunks retain their initially compressed CPU greedy meshes. Identical streaming
invalidations reuse those meshes, so a no-op does not promote them to larger unit-face
meshes. Initial generation, singleton debris, partial blocks (including shaped padded
neighbors), LOD generation and collision meshes retain CPU paths. Set `MYT_GPU_MESH=0` to
compare against the incremental CPU culled mesher. `MYT_SYNC_MESH=1` also uses CPU meshing.
The optional profiling line includes dispatched/completed GPU meshes and the last batch's
GPU timestamp duration. Render GPU timing now includes uploads and GPU meshing.

`gpu-meshing` checks exact packed vertex order against the CPU reference across negative
chunks, borders, AO, oriented logs, lit furnaces, water, transparent leaves, rapid edits,
shape transitions, and a worst-case 24,576-face chunk, then compares rendered pixels.
`async-meshing` exercises ongoing edits, no-op compressed-mesh retention, empty-chunk
dependencies, Clear and unloading. Full vertex readbacks are enabled only in these tests.

On the local RTX 3060, that geometry test completed 63 GPU meshes with exact packed vertex
agreement and zero pixel differences. Measured kernel batches had a 0.206 ms median and
3.916 ms maximum, including the fully exposed leaf stress case. A serial particles-off,
off-screen TNT smoke test on two identical `better trees` copies dispatched six edited
chunks in three batches taking 0.188, 0.252 and 0.127 ms. Mean frame-loop times were
20.34 ms (CPU culled) and 20.29 ms (GPU culled); this is not evidence of an overall frame-rate
gain. GPU result publication adds frame-fence latency: maximum observed mesh edit latency
was 156 ms versus 48 ms for CPU results in this capture. Old meshes remained visible.
Both runs loaded the version 5 fixture and wrote version 6 saves. Diagnostic logs are
`/tmp/myt-gpu-mesh-final-test.log`, `/tmp/myt-gpu-mesh-final-async.log`,
`/tmp/myt-save-final-tests.log` and `/tmp/myt-mesher-{cpu-control,gpu-optimized}.log`.

### Incremental CPU culled fallback (November 2026)

The CPU fallback uses persistent culled unit faces (`IncrementalVoxelMesh`). Each
chunk retains a padded cell snapshot, a cell/face-to-slot table and a dense face array.
New chunks also get a compressed greedy render mesh once, so untouched terrain keeps its
lower draw cost. No-op invalidations reuse that compressed mesh. The first actual geometry
edit promotes the chunk to its already-built unit-face state; subsequent edits stay local
and do not run greedy meshing again. `incremental-render-cache` checks this transition.
Removing a face moves the last face into its slot; adding one appends. Edits recalculate
visibility, texture orientation and corner AO only within the changed cells' one-cell
neighbourhood, deduplicated across a batch. Chunk edge/corner edits invalidate diagonal
neighbours as well as face neighbours. Damage alone does not change mesh geometry.

GPU arena ranges retain spare capacity. Uploads compare against the last mesh actually
uploaded, coalesce changed quads into contiguous transfers, and reuse the allocation when
it fits. A vertex-read-to-transfer barrier protects ranges still used by earlier frames;
the usual transfer-to-vertex barrier exposes the changes to subsequent draws. Queued CPU
results are immutable and versioned, so skipped uploads cannot lose edits.

This trades RAM, VRAM and additional rendered quads for quicker edits. It still copies and
compares the 18-cubed snapshot to discover edits, copies dense vertices when geometry changes,
and compares upload snapshots; it does not promise constant total CPU time per edit.
Chunks containing shaped blocks, including in their one-cell border, retain the complete
shape mesher. Singleton debris retains its existing six-face fast path without persistent
chunk tables. Collision cooking, pressure propagation and lighting remain separate costs.

For A/B profiling set `MYT_INCREMENTAL_MESH=0`. The CPU benchmark
`incremental-meshing-benchmark` includes padded copies: one local run measured 1,000
single-block edits at 65 ms incremental versus 190 ms whole-chunk greedy (2.9x),
and 1,000 batches of 64 edits at 68 ms versus 181 ms (2.7x).
These are mesher timings, not TNT frame-time measurements. `incremental-meshing` compares
random edit batches, transparency, AO, rotations, seams and shape transitions against the
reference; `render-equivalence` also compares partial GPU edits with fresh reference meshes.

### Rendering and singleton contact performance (October 2026)

The investigation separates presentation pacing, GPU rendering, fixed-step simulation and
CPU contact processing. At 75 Hz, FIFO/vsync accounts for a roughly 13.3 ms frame interval;
time waiting in `BeginFrame` alone does not establish a rendering bottleneck. GPU rendering
in the empty seeded scene measured around 0.2 ms on the RTX 3060. Use the optional immediate
present mode and GPU timestamps to distinguish these costs on other hardware.

The implementation follows this order: measure empty and dense scenes; reduce render
geometry under strict shading equivalence; specialize single-block mesh/contact work;
avoid settled streaming scans; validate against the original algorithms; measure complete
frames again. Collision topology, contact-admission rules, CCD, solver iterations, material
strengths, structural loads and the fixed physics timestep are retained.

Render meshing greedily joins adjacent faces only when the texture layer and all four AO
values match a constant. Faces with AO gradients retain their original vertices and diagonal,
since matching boundary values alone would not preserve the piecewise affine interpolation.
The existing packed vertex gains a rectangle flag in bit 22. The shader repeats the original
oriented texture per cell, using continuous derivatives with tile-local clamp sampling.
It locates each fragment's cell in chunk-local coordinates half a cell inside its face,
so independent cracks and stress colors still update without remeshing, including on rotated
objects. Each meshing thread retains its own scratch masks. `MYT_RENDER_MERGE=0` and
`render-meshing` retain the original unit-face implementation as a comparison.

A single occupied voxel has six exposed faces with AO=3. Its render path bypasses the padded
18-cubed copy and full chunk traversal. Damage/stress uploads stop after the chunk's occupied
cells have been processed. Whole-object render culling uses the cooked occupied bounds before
building matrices or traversing storage chunks; the interpolated pose is used for both tests
and drawing. Settled streaming returns immediately until the player changes columns, the
render radius changes, a column loads/unloads, or kept content invalidates it; pending
background results always continue to be drained.

Contact mapping retains the original probe and fallback search. When a verified object has
exactly one occupied cell, membership in the same eight-cell search box replaces repeated
occupancy hashes and depth ranking. Failed lookups also retain the original probe cell.
The candidate from cooked bounds is checked against live occupancy before use, so stale
bounds cannot select a wrong cell after edits. Each side's inverse-rotated contact normal is
computed once and reused for cell selection, facing and face moments. Tests compare singleton
lookups with the generic search, and `contact-mapping` compares resulting cell selections,
forces, moments and ordered reductions against generic serial mapping.

Isolated CPU measurements on this host (not frame times):

| Workload | Original | Optimized |
|---|---:|---:|
| Solid 16-cubed chunk geometry | 1,536 quads | 6 quads |
| Seeded terrain column geometry | 594 quads | 175 quads |
| Terrain column meshing | 0.293 ms | 0.337 ms |
| Meshing 5,000 single-block objects | 101.42 ms | 2.21 ms |
| 744,060 singleton surface queries | 215.97 ms | 4.45 ms |

The terrain path trades a little additional CPU meshing work for less resident/uploaded
geometry and GPU work. This is useful for terrain scale; it is not a claim that rendering
was limiting the empty desktop scene.

The seeded 300-frame, 5,000-block falling pile, with validation off and original collision
shaders, measured frames 61–299 (startup and screenshot capture excluded):

| Metric | Before this pass | After this pass |
|---|---:|---:|
| Mean frame | 126.51 ms | 122.15 ms |
| Median frame | 121.5 ms | 117.3 ms |
| p99 frame | 173.7 ms | 179.1 ms |
| Median contact mapping per frame | 9.7 ms | 6.7 ms |
| Median contact gathering per frame | 21.3 ms | 18.4 ms |

These are individual wall-time-driven runs with different trajectories, collider counts and
asynchronous damage timing. The tail did not improve; the modest mean improvement must not
be generalized into a guaranteed speedup. The final dense run's mean GPU rendering was
0.240 ms. Repeated GPU collision/solving remains the dominant cost, with three catch-up
physics steps per frame. Smooth 5,000-body piles at 75 Hz remain unresolved. The island
sleep/wake, persistent-patch and stable-topology work described above remains the next
architectural work, with support-load and impact/CCD accuracy gates.

Validation: the full headless voxel suite passed 110 of 114 scenarios with no Vulkan
validation messages. The four failures (`health-model`, `world-cantilever`,
`world-through-glass`, `terrain-stands`) were each reproduced with the original game
assembly. Test thresholds and material constants were retained. The separate windowed
`render-equivalence` check compares cracks and stress colors on terrain and rotated objects,
then changes damage without remeshing. All four image comparisons passed: mean absolute
channel differences were 0.0016–0.0065 on the 0–255 scale, with 15–52 pixels differing by
more than 8 out of 307,200 pixels, and no Vulkan validation messages. Small differences
are at raster/texture boundaries. The pixel check requires a display and runs only when
explicitly named, so the normal suite remains headless.

Reproduce the new checks with:

```sh
dotnet run --project src/VoxelTests -c Release -- render-meshing render-meshing-benchmark contact-single-cell contact-mapping streaming-cache
dotnet run --project src/VoxelTests -c Release -- render-equivalence # needs a display
MYT_PROF=1 MYT_PRESENT_MODE=immediate MYT_SEED=1 MYT_FRAMES=6000 MYT_SCREENSHOT=/tmp/empty.ppm dotnet run --project src/VoxelGame -c Release
MYT_PROF=1 MYT_SEED=1 MYT_SPAWN_OBJECTS=5000 MYT_SPAWN_MODE=blocks MYT_FRAMES=300 MYT_SCREENSHOT=/tmp/pile.ppm dotnet run --project src/VoxelGame -c Release
```

### Worst-frame latency

The HUD now uses the real frame-loop interval. Previously `Program` capped that interval
at 100 ms for simulation catch-up and passed the capped value into the HUD too, hiding
longer stalls. Simulation still receives the same capped time; `FrameTiming` independently
reports frames per real elapsed second and shows the largest recent interval immediately.
`MYT_PROF=1` additionally summarizes complete loop intervals, including event polling,
input and diagnostic output. Those intervals describe what the player sees; Update/Draw
scope times locate the work behind them. Screenshot capture is excluded from both series.

The stutter profile identified synchronous collider/topology rebuilds, terrain structural
preparation and first-use compilation. Empty-scene GPU rendering was not the limiting work.
This pass changes the following:

- Terrain analysis visits only each chunk's intersection with the current analysis window.
  Only cells on a window boundary test neighbours outside it. Cell order, voxel values and
  anchor flags match the original six-direction implementation exactly, including negative
  chunk coordinates and non-solid water. A 70,675-cell comparison measured 15.22 ms for the
  reference and 6.16 ms for the boundary-only traversal.
- Map topology preparation retains its scratch lists. Published cell/stress snapshots remain
  independent, so old snapshot comparisons and topology generation checks keep working.
- `GpuStressLattice` retains cell, node-index, result and component-work capacity with an
  explicit active count. Shrinking does not expose unused capacity or read old cell indices.
  Tests compare reused/fresh solutions through shrinkage, reordering, disconnected groups
  and regrowth, and check copying into a destination with an untouched sentinel tail.
- Both structural solvers select the first minimum-distance anchor with a loop. The old
  `MinBy` lambda captured a variable in the seed loop and created a closure even for seeds
  skipped as already visited. Warm GPU topology preparation now allocates zero managed
  bytes in the 9,646-cell repeat test; the mathematical anchor and tie order are unchanged.
- The streamer integrates at most four completed terrain jobs per update. Results remain
  pending and generation continues normally; there is no limit on world extent or loaded
  render distance. This bounds completion bursts reaching the main thread.
- Release disables tiered compilation, compiling optimized methods on first use rather than
  promoting code during play. First-use compilation still exists. A local ReadyToRun build
  was also measured, but had more moderately slow frames than the selected configuration;
  it is not the default. Debug retains its runtime defaults.

On this RTX 3060 host, the same seeded 1,800-frame spinning/throwing workload, FIFO/vsync,
validation off, produced the following Update/Draw/present timings for frames 61–1,799:

| Tail metric | Before this pass | Final changes with Release runtime setting |
|---|---:|---:|
| p99 frame | 22.73 ms | 19.03 ms |
| Worst frame | 56.49 ms | 42.66 ms |
| Frames over 20 ms | 28 | 11 |
| Frames over 33 ms | 4 | 2 |

A separate 1,200-frame walking/streaming comparison reduced the worst scope time from
29.14 ms to 17.12 ms and frames over 20 ms from four to zero (frames 61–1,199).
The final complete-loop maximum was 17.31 ms. The walks differ slightly in player path.

Mean scope time remained about 13.2 ms because presentation is capped at 75 Hz. The new
complete-loop measurements for the final run were p99 19.21 ms and maximum 42.78 ms.
These are individual wall-time-driven runs with different asynchronous damage timing, so
neither the tail percentages nor the maximum are guarantees. First object/impact use and
synchronous physics topology rebuilds still hitch. A 75 Hz frame budget is 13.33 ms; this
pass reduces stuttering but does not establish hitch-free play.

Validation includes the existing ten GPU structural scenarios, CPU statics/convergence,
new frame timing and topology reuse checks, exact analysis-window equivalence, streaming
budgets, impacts, splitting, detaching, crushing and sliding support loads. Test tolerances,
materials, collision shaders, solver iterations and physics timestep are retained. The final
15 targeted structural/gameplay checks passed with the Release compilation setting and no
Vulkan validation errors; the solution builds in Release with existing warnings. Run the
new targeted checks with:

```sh
DOTNET_TieredCompilation=0 MYT_VALIDATION=1 dotnet run --project src/VoxelTests -c Release -- frame-timing analysis-window gpu-topology-reuse streaming-cache
MYT_PROF=1 MYT_PROFILE=1 MYT_AUTOPILOT=spin MYT_AUTOTHROW=1 MYT_SEED=1 MYT_FRAMES=1800 MYT_SCREENSHOT=/tmp/stutter.ppm dotnet run --project src/VoxelGame -c Release
```

#### Overlap recovery and chunk submission

A short startup profile missed the 2 FPS regression. A longer replay of a copy of a saved
world, using its render and simulation distances of 32, reproduced it as terrain loaded:
170 simulated voxel objects (5,000 cells), 3,409 loaded columns, 5,836 visible chunks and
1,019,088 visible quads. The new CPU overlap recovery consumed about 126 ms per step,
with three catch-up steps in each approximately 436 ms frame. Render GPU time was about
1.5 ms. The save was copied into a temporary data directory for all measurements.

The implementation was refined in stages, with retained references of the original code:

1. Replace all-body scans with a conservative body hierarchy. Refit after motion and update
   corrected bodies immediately, so later corrections in the same pass see the new poses.
2. Cache local cell centers and cell hierarchies by object lifetime and topology version.
   Query the source cell hierarchy against each target before visiting cells, rather than
   repeatedly scanning whole sparse chunks and entire large objects for small contacts.
3. Cache rotated box axes, then cache the fifteen separating axes and projection radii once
   per body pair. Keep normalized axes, degeneracy thresholds, traversal order, equal-depth
   winners, four recovery passes and inverse-mass correction arithmetic. Single-cell targets
   use the same separating-axis test directly. Chunk lookups and target transforms are reused.
4. Clip walkable faces in stack buffers instead of allocating polygon lists. Crouch support
   uses the existing ground-probe range and retains the solver's contact band at an edge;
   otherwise roundoff can stop flat walking or let a tilted ledge lose support. The slope
   movement check lets the player stop before reversing, preserving its acceleration law.
5. Build per-frame voxel draw records and indexed indirect commands. Consecutive chunks on
   an arena page are submitted together, without reordering surfaces. Model matrices, vertex
   data, per-cell cracks/stress, fog and culling are unchanged. Unsupported devices and
   `MYT_INDIRECT_DRAWS=0` use direct indexed draws. Each frame slot owns its buffers and
   grows them only after its fence completes; batches respect the device's draw-count limit.

The CPU comparison used the same saved poses and materials, including overlap recovery,
and measured twenty warmed steps with tiered compilation disabled:

| Recovery metric | Original exhaustive traversal | Indexed traversal |
|---|---:|---:|
| Mean step | 127.4 ms | 3.24 ms |
| Managed allocation per step | 7,509,736 bytes | 0 bytes |
| Final body poses | Reference | Within 1e-9 m of reference |

The rendered comparison on an RTX 3060 used the same initial save and settings, with FIFO
presentation and Vulkan validation off. The original replay was stopped at frame 943 after
reproducing the sustained slowdown; the optimized replay completed 6,000 frames. The table
uses frames after generation and meshing finished (102 original frames and 5,158 optimized
frames), so the sample durations differ:

| Settled-scene metric | Before | After |
|---|---:|---:|
| Mean Update/Draw/present scope | 436.1 ms | 15.3 ms |
| Median scope | 436.1 ms | 14.3 ms |
| p99 scope | 503.6 ms | 34.9 ms |
| Recovery per simulation step | 126.4 ms | 3.82 ms |
| Median GPU rendering | 1.50 ms | 0.62 ms |
| Voxel draw calls | 5,836 | 7 |

The complete optimized run's loop intervals, excluding the first sixty frames, averaged
16.43 ms (about 61 FPS), with p99 40.27 ms and maximum 81.98 ms. This includes terrain
loading. Steady scope time corresponds to about 65 FPS, versus 2.3 FPS before. Physics and
structural work still produce occasional slow frames; this does not establish constant
75 Hz or remove the existing need for GPU collision/solver optimization in dense scenes.
Asynchronous damage timing and wall-clock catch-up differ between replays. Solver iteration
counts, contact tolerances, materials, structural equations and the physics timestep were
not reduced for performance.

An additional 1,200-frame replay disabled only indirect submission. Comparing frames
900–1,199 after loading, mean CPU Draw scope was 0.875 ms with direct calls and 0.771 ms
with batching (about 12% lower). GPU rendering was essentially unchanged, 0.628 versus
0.622 ms, with identical visible chunk/quad counts. The larger GPU-time difference in the
combined before/after table cannot be attributed solely to batching; GPU duty cycle also
changes when the CPU bottleneck is removed. Collision recovery accounts for the large FPS
recovery, while batching reduces submission overhead.

Accuracy checks compare 20,000 rotated and aligned contacts and clipped slope faces with
the original implementation (separation tolerance 1e-12). Ordered recovery is compared
through edits, movement, spawning, parking and removal. The render check confirms the
existing merged/unit-face comparisons and pixel-identical direct/indirect submission with
cracks, stress colors, rotated objects and growth beyond 256 draw records in both frame
slots; its comparison scene reduces 329 calls to one, with Vulkan validation enabled.
The full voxel suite completed 133 scenarios: 128 passed, and five failed (`health-model`,
`world-cantilever`, `world-through-glass`, `world-terrain`, `terrain-stands`). All five also
failed in the preserved pre-optimization build. Neither run emitted Vulkan validation
messages. The Release solution build succeeds with the existing warnings.

```sh
DOTNET_TieredCompilation=0 dotnet run --project src/VoxelTests -c Release -- collision-numerical-equivalence collision-spatial-index voxel-recovery-equivalence voxel-recovery-benchmark
# Optional: benchmark a saved snapshot without running or modifying that world.
MYT_COLLISION_SAVE=/path/to/world.dat DOTNET_TieredCompilation=0 dotnet run --project src/VoxelTests -c Release -- voxel-recovery-benchmark
MYT_VALIDATION=1 dotnet run --project src/VoxelTests -c Release -- render-equivalence
MYT_VALIDATION=1 dotnet run --project src/VoxelTests -c Release -- player-crouch-ledges player-crouch-clearance player-crouch-slope discrete-overlap-recovery
```

#### Empty-world walking and turning follow-up

Profiling stationary camera rotation reproduced roughly 66 ms frames after streaming had
settled. The split `swap.present` scope measured about 53 ms inside `vkQueuePresentKHR`,
with rendered GPU work around 0.16 ms and no garbage collection in those frames. This
locates that stall in presentation; it does not distinguish the driver, compositor and
scheduling causes. An uncapped `MYT_PRESENT_MODE=immediate` diagnostic did not reproduce
it, but covered less wall-clock time. FIFO/vsync remains the default. Profiling now separates
`swap.fence`, `swap.acquire` and `swap.present`, and `pt.index` from `pt.setCells`.

Walking also exposed a separate synchronous wait when relocating the map's stress lattice.
CPU topology preparation now retains the previous GPU resources while their submission
finishes. `Update` polls that submission's fence, rejects results for outdated topology,
and only then recycles resources for the newest box. Buffer clears and the hierarchy table
upload are recorded in the next solver command buffer with transfer ordering, avoiding a
separate blocking initialization submission. Moving an existing analysis window schedules
its GPU update on the following physics update, separating CPU window preparation from
resource replacement. This adds one update of structural-analysis latency on window moves;
the physics timestep, structural equations, tolerances, iterations and boundary anchors are
unchanged. Catch-up updates may still run in one rendered frame.

The topology-reuse regression now submits old-box work immediately before each topology
change, including movement to distant positive/negative coordinates and back. It compares
fresh and retained solver stresses, invalidation, active spans and destination sentinels.
All 14 targeted GPU structural, timing and streaming checks passed with Vulkan validation.

Eight additional structural-gameplay/streaming checks also passed with validation (two
overlap the previous group). The final 1,200-frame walking run measured complete-loop
p99 14.17 ms and maximum 40.21 ms, excluding the initial 60 frames and screenshot. Moving
window frames were about 13.5 ms; `s.follow` preparation peaked at about 9.4 ms compared
with 15.3 ms in the earlier Release walk. Resource replacement on the following update
can still produce a roughly 15 ms frame. The worst frame was instead a terrain-damage
and detachment event: 35 ms in world update, including 18.1 ms in topology work,
10.7 ms processing impacts, 6.8 ms rebuilding terrain colliders and 4.8 ms in particles
(scopes overlap). No 66 ms presentation stall occurred in this particular walk, but those
stalls reproduced in other captures. The total worst frame has therefore **not** been
eliminated; these samples do not establish an improvement in overall worst-frame latency.

Capture the individual waits during manual walking/turning with:

```sh
MYT_PROF=1 dotnet run --project src/VoxelGame -c Release > /tmp/myt-manual-frame-times.log 2>&1
```

### The endless world

The map is generated as the player explores (`TerrainGenerator`, `ChunkStreamer`), a chunk column at a time: 16 × 16 m, from the bedrock up.

- **Terrain.** Sea level is Z=63. Raised continental terrain, hills and ridged mountains meet river channels, lake basins and submerged seas. Mountain stone starts at a noise-varied altitude rather than a flat cutoff; soil thickness also varies. The spawn's hills are raised by 64 blocks and blend into the seeded terrain over 40 m.
- **Underground.** Bedrock fills Z=0, with decreasing random coverage through Z=4. Intersections of two smooth 3D noise fields carve winding tunnels, and larger noise pockets create underground chambers. Ore veins follow triangular probability gradients around preferred depths: coal 68, iron 48, bronze 44, emerald 42, lapis 30, gold 28, uranium 20, ruby 18, diamond 12 and soul 8. All coordinates are Z levels.
- **Water and trees.** Water fills surface basins up to Z=63. It renders as a transparent block, contributes no collision or structural support, and stays static. Trees grow on dry land with roots and branches that match across columns.
- **Seams.** Generating a column is a pure function of the seed and the column, so columns generated apart, in any order, meet: a tree rooted near a border is grown by every column it reaches into, in the same order.
- **Seed.** A new world gets the seed typed when it is created (a number as it is, other text hashed), or a random one; the HUD shows it.
- **Streaming.** Columns are generated on background threads (including caves and ore placement), nearest first, out to one column past the render distance, so that every column drawn has its neighbours to mesh against, and unloaded two columns further out, so that walking back and forth over a border does not load the same columns again and again. A column the player changed (a block placed, mined, damaged or fallen away) is kept in memory as it was left and comes back so; the others are simply generated again. If the player outruns the generator, the world holds still until the ground under it is there.

A map that size cannot be simulated whole, so the simulation follows the player (`World.FollowPlayer`):

- **Colliders** exist only in a square of 4 × 4 chunk columns centred on the chunk corner nearest the player, which moves on once the player strays 12 m from its middle, so the player is always at least 20 m from its edge. Colliders for where it goes next are baked ahead on a background thread (`ColliderBaker`, with the engine's `FixedBody.Cook`), and the ones it leaves are kept for a while, so a move costs one physics rebuild, about 15 ms, rather than 35-45.
- **Objects** beyond the region are parked: taken out of the physics where they are, with their motion, until the region reaches them again, so nothing falls through ground without colliders. A parked object stays where it is, even in mid-air.
- **The map's stress analysis** covers a box 64 m across and 64 m high (24 m below the player, 40 above) that follows the player in the same way, though never on the same step as the region. Blocks beside its sides are held by the map beyond it; those within 3 m of a side take no stress damage, since that hold is the box's, not the map's. Columns streaming in and out far away leave it alone.

Rendering keeps up with up to 32 columns all round:

- Chunk meshes live in device memory, in 64 MB pages handed out in ranges (`VertexArena`), and reach it through a staging buffer, up to 8 MB a frame. A chunk whose cells show no damage or stress shares one empty slot of the per-cell data buffer.
- The map's chunks within two chunks of the eye are meshed on the spot, so an edit shows on the frame it is made; the rest on background threads, nearest first, once the columns around theirs are loaded.
- Chunks out of view or past the fog are not drawn, and depth is reversed without a far plane, so far terrain neither clips nor flickers. The fog closes in at the render distance.
- At a render distance of 32, an RTX 3060 draws about 2,600 chunks (70 MB of meshes) at about 100 frames a second.

### Menus and saves

The interface is built with the engine's GUI library (`MYT.Kit.Gui`, see [Gui.md](Gui.md)). It is laid out as Minecraft's is, in units of 2 pixels at 720p and 3 at 1080p, with the scale set in Settings. The menus are drawn over tiled dirt, or over the dimmed world while one is played.

- **Title screen**: the title, then Singleplayer, then Multiplayer (disabled until there is multiplayer), and after a gap, Settings and Quit Game side by side.
- **Select World**: the saved worlds, the one played last first, each with its folder, when it was last played, its game mode and its seed. Play Selected World (or double-click, or Enter), Create New World (a name, a seed or blank for a random one, and Game Mode: Survival or Creative), Rename, and Delete after a confirmation.
- **Play Multiplayer**: a server list that can be added to, edited and deleted (`servers.json`). Join Server is disabled.
- **Settings**: render distance, simulation distance, field of view, mouse sensitivity and GUI scale, kept in `settings.json`. They are reached from the title screen and from the game menu.
- **Game menu** (Esc): Back to Game, Settings, Save and Quit to Title. **Death screen**: Respawn, Title Screen.

A saved world is a folder under `saves/` in the data directory. Its `world.json` holds the name, seed, dates and game mode, and keeps the folder name when the world is renamed; one written before game modes plays in creative. Its `world.dat` holds what generating from the seed would not give back (`WorldFile`, version 6; versions 1–5 are still read):

- every chunk column changed since it was generated, loaded or kept by the streamer, block health included;
- every object: its blocks in its own frame with their health, its pose and its motion, parked or not;
- every furnace: its queued input, completed output, remaining smelting time and fuel credit;
- the RGB light grid for terrain and air, including archived chunks that streamed out;
- the day/moon clock, preserving the full 31-day orbital cycle rather than only the solar phase;
- live chips, dust and blast puffs: position, velocity, age/remaining lifetime, voxel appearance, texture pixel, size, mass and dust shade, plus remaining explosion-flash frames;
- every dropped item: what it is, its pose and motion, how long it has lain and how long until it can be picked up;
- the player: where it stands, its velocity, view, health, flying, invulnerability, one-hit breaking, spawn point, the hotbar slot held and its 36-slot inventory. A stack carried on the inventory screen when the world is saved goes back into the inventory, or lies at the player's feet if there is no room.

Passing state is left out: impacts being analysed, stresses, contact loads and in-flight pressure waves. A world played again streams its changed columns back in as the player comes near. Its objects wait, parked, until the ground under them is loaded, then go on as they were. Particle snapshots own their data for background saves and restore their existing motion and remaining lifetime; disabling particles in settings keeps them disabled on load. Worlds saved before version 6 start at noon without saved particles. Invalid clocks, excessive counts and invalid particle physics are rejected. `save-particles-daytime` checks restoration, snapshot ownership, malformed data and version 5 compatibility; `save-round-trip` checks the remaining world state.

The world is saved on Save and Quit, when the window is closed, and every 5 minutes on a background thread (a failed autosave shows in the HUD). The file is gzip-compressed binary, written whole beside the old one and then moved over it, so a save cut short leaves the last one intact. Lighting is copied from the GPU at snapshot time; compression and disk writing run on the save thread. Uniform light chunks store a single value, and other chunks store all 4,096 RGB values. Each chunk includes a geometry signature: incompatible cached lighting is discarded when its terrain loads. Saves without a lighting section initialize from sources and start diffusing immediately.

### Game modes, items and crafting

**Survival**: the player starts with nothing, walks (no flying), and is always vulnerable. A block mined out drops as an item; placing a block takes one from the held stack. **Creative**: items never run out (placing and dropping take nothing), mined blocks drop nothing, and the player flies (F), is invulnerable until I turns that off, and breaks any block in one hit, bedrock too, until B turns that off, when it mines as in survival, where bedrock does not break.

**Mining with tools.** A strike crushes the block under the edge of what is held at its *strike* stress σ, which also shears it at σ/2, the most shear a one-way compression makes. `BlockMaterial.StrikeRatio` gives ρ, how far past the block's elastic limit at its health that goes. At ρ ≤ 1 the strike bounces off and does nothing. Past it, a strike deals the block's mining rate × the tool's speed × (1 − 1/ρ²), the share of the strike's energy past the limit, for a sixth of a second (`Tool.Damage`). A tool far stronger than the block mines it at the full rate; one barely stronger, slowly. As the block wears its limit falls, so mining speeds up toward the end, and a block worn by something else can give way to a weaker tool.

| Held | Strike | Speed | Gets through (intact) | Stone takes |
|---|---|---|---|---|
| Hand, or anything not a tool | 12 MPa | 1 | soil, sand, leaves, glass, wood, planks, cobblestone; not stone (16 MPa) | — |
| Wooden Pickaxe | 24 MPa | 1.5 | stone; not gold ore (32 MPa) | 11 strikes, 1.8 s |
| Stone Pickaxe | 40 MPa | 2 | gold ore; not diamond ore (52 MPa) | 7 strikes |
| Iron Pickaxe | 80 MPa | 2.8 | all ores | faster than stone |
| Emerald Pickaxe | 140 MPa | 4.5 | everything breakable | faster than diamond |
| Golden Pickaxe | 64 MPa | 3 | diamond ore | 5 strikes |
| Diamond Pickaxe | 120 MPa | 4 | everything breakable | 4 strikes |

An oak log takes a hand about 13 strikes (2.2 s), dirt 4. The HUD says when what you hold cannot get past the targeted block's limit.

**Drops.** A block the player mines in survival drops an item where it was, tossed up a little: itself, but grass drops dirt, coal ore drops coal, and diamond/emerald ores drop gems. Blocks broken by impacts, overload or collapse shatter into particles as before, so a falling tower does not fill the world with bodies. Q drops one of the held item (Ctrl+Q the stack) along the view; the inventory screen drops what is let go of outside it.

**Dropped items** (`DroppedItem`, `WorldItems.cs`) are rigid bodies among the objects: a block as a 0.5 m cube of it, as big as a dropped tool, with its density (a stone one weighs 325 kg), friction and bounce; another item as its 16 × 16 image a pixel thick, 0.5 m across (`ItemMesher.SpriteShape`, the pixels that show, joined where two touch only at a corner so every edge has two faces). They collide, fall, slide and rest like objects, but the damage model leaves their contacts out: they neither break nor break anything, and they do not hurt the player. A contact that pushes on the player picks one up into the inventory, as much as fits, once its pickup delay is over (0.25 s for a mined block, 1.5 s for one the player dropped); what does not fit stays. Items are parked beyond the simulation distance like objects, preserving their state. Physical objects and dropped items never automatically despawn: there is no age limit, count eviction or below-world deletion. They remain saved while parked; pickup and actual destruction still work normally.

**The inventory** (`Inventory`) has 36 slots, the first 9 the hotbar, stacks of 64 (tools 1). E opens the survival inventory. In creative, the unlimited item catalog appears on the left and the player inventory on the right, both visible without tabs. Left and right clicks, Shift transfer, hotbar number keys and Q drops keep their existing behavior. Clicking the catalog takes a stack (right-click: one), Shift adds directly to the inventory, and clicking with a carried stack discards it.

**Crafting** (`Crafting.Recipes`) shows each recipe's result and ingredients. Hand recipes are available from E; right-click a workbench within reach to open its larger recipe list. Nearby workbenches no longer grant crafting access. Shift-click crafts as many as materials and space allow. Wood and stone pickaxes use planks/cobblestone and sticks; iron and golden pickaxes use ingots, and diamond and emerald pickaxes use gems. A furnace costs eight cobblestone at a workbench.

**Smelting** (`Smelting.cs`) opens by right-clicking a furnace, using a recipe list above the player inventory like the workbench. Click a recipe to queue one item, or Shift-click to queue more. Iron/gold ore smelt into ingots, sand into glass, and cobblestone into stone. Each item takes ten seconds during play; one coal fuels eight items and one log or plank fuels one. A progress bar and queue count show the work. Click the output slot to collect results (Shift transfers directly). Smelting continues when the UI closes, pauses with the game, and saves its progress. Breaking a furnace drops its completed output and queued inputs. Shift-right-click places blocks against a station.

**The held item** is drawn at the lower right of the view, over the world (the depth is cleared first, so it never goes into a wall), with a 70° field of view of its own: a block as a turned cube, a tool or a stick cut from its image as the dropped one is, culled to the pixels that show, and with nothing in hand the bare arm. It is lowered for a moment when another is chosen. Every mining hit (one per click, six a second held) starts a swing (`HeldSwing`), turned about where the item is gripped, and the hit lands as the swing strikes: the item winds up back and up, thrusts forward and in toward the middle of the view while turning, about 0.4 m, and strikes there 70 ms in; then it drops down over the next 80 ms, about 0.2 m, pitching over as it follows through, and settles back to rest, which is most of its 0.42 s. A swing lasts longer than the time between hits, so while mining the next hit catches it settling, still low, and it swings again from there, blended over 60 ms. A hit that starts a swing before the last one struck lands that one's hit at once, so every hit lands. A tool chops, its head coming over and down to the middle of the view; a block or another item punches, more forward than down; the bare arm jabs from the shoulder, dipping less as it drives in. Placing a block swings it too, striking nothing. Each style is three poses (wound up, thrust forward, dropped down), passed through on a cubic Hermite curve. Items are drawn with `assets/shaders/voxel/item.vert` and `item.frag`.

### Bodies

- **The map** is a `Grid3D<Voxel>` anchored by its bedrock. Every 16³ chunk with faces is one `FixedBody`, so an edit replaces only the chunks it touches; bedrock undersides are left out of the collision mesh.
- **Objects** are `Grid3D<Voxel>`s with a pose. Big ones simply span several chunks: they render per chunk and collide as one `RigidBody`. Its mass properties come from each block's density, through the engine's explicit-mass constructor.
- **Placing** a block on an object rebuilds its rigid body and keeps its motion: the new center of mass moves with the velocity the old rigid motion gives it.
- **Removing** a block runs a connectivity check:
  - map blocks that can no longer reach bedrock fall away as a new object;
  - an object cut apart splits, each piece moving as the whole did at its center (v + ω × r).
- **The player** is a body too (`Player`, see [below](#the-player)).

### Block properties

Every block type has (`BlockMaterial` in `Blocks.cs`):

| Property | What it is |
|---|---|
| **Density** (mass) | kg/m³: the block's mass, so the inertia and momentum of the body it is part of and the energy of its impacts. |
| **Elasticity** | The stresses the block endures before it starts breaking, in tension, compression and shear (Pa). |
| **Stiffness** | How hard the block is to break: a factor on its elasticity and its durability. |
| **Durability** | Its health, HP. |
| Young's modulus, Poisson's ratio | How much it deforms: how loads spread through a structure and how hard an impact pushes. |
| Mining time | Time to mine it by hand from intact. |
| **Friction** | Coulomb friction coefficient of its faces: how it grips what touches it, and how well the player's legs push and brake on it. |
| **Damping** | How much of a bounce it soaks up, 0 to 1: two blocks of it part at 1 − Damping of the speed they met at. |

A block's health h goes from 1 (intact) to 0 (broken) and never heals. Its six crack stages show the share of the damage that breaks it intact it has taken, 1 − h², which grows evenly with damage, so each hit of mining cracks it further. Its effective stiffness is s = Stiffness × h, and it scales both what the block endures and how much damage it soaks up:

- its elastic limit is Elasticity × s, so a worn block starts breaking under less;
- damage d (HP) costs it d / s of its durability: h² falls by 2d / (Durability × Stiffness). A fresh block breaks after Durability × Stiffness / 2 HP, and the second half of its health takes a third of the damage the first half did;
- its Young's modulus falls with its health too, to a fifth, so worn blocks shed load onto their neighbours.

| Block | Density | Elasticity: tension, compression, shear | Stiffness | Durability | Friction | Damping |
|---|---|---|---|---|---|---|
| Stone | 2600 | 6, 100, 8 MPa | 1.0 | 100 | 0.7 | 0.6 |
| Cobblestone | 2300 | 1.6, 45, 2.2 MPa | 0.9 | 80 | 0.8 | 0.65 |
| Dirt | 1600 | 100 kPa, 1.9 MPa, 125 kPa | 0.8 | 200 | 0.65 | 0.95 |
| Grass | 1600 | 125 kPa, 1.9 MPa, 150 kPa | 0.8 | 200 | 0.55 | 0.95 |
| Sand | 1700 | 30 kPa, 1.4 MPa, 43 kPa | 0.7 | 150 | 0.5 | 0.98 |
| Oak log | 750 | 6, 40, 4 MPa | 1.0 | 150 | 0.6 | 0.5 |
| Oak planks | 700 | 4.5, 33, 3.3 MPa | 0.9 | 120 | 0.4 | 0.5 |
| Glass | 2500 | 1.5, 15, 1.5 MPa | 1.0 | 8 | 0.4 | 0.4 |
| Leaves | 200 | 40, 200, 40 kPa | 0.5 | 40 | 0.3 | 0.9 |

So a one-block dirt overhang holds and a two-block one breaks; sand cannot overhang at all. Structures in the starting world sit on stone foundations because grass and dirt shear under a heavy tower.

Friction and damping are real-world values for two blocks of the same kind. Every face of a collision mesh carries its block's (`SurfaceMaterials` on the engine's bodies), so an object of planks slides on glass and grips cobblestone. Two different blocks grip with the geometric mean of their frictions and part at the geometric mean of their 1 − Damping, so stone bounces off stone at 0.4 of the speed it hit with but lands dead on soil and sand, which soak up nearly everything (stone on grass: 0.14). Between two objects a bounce is shared as an impulse between them would be: a falling 70 t cube that hits the 17 t piece of a bridge deck it broke off goes on down through it rather than bouncing off it.

Dirt, grass and sand are **granular**: cracking a block of soil does not get it out of the way, so an impact must crush it to break it, which takes far more energy than snapping it. Soil takes impacts: a 70 t cube dropped 2 m onto grass dents it, and a glass block thrown at it bounces off. But it fails only where an impact strikes it, not under what is struck: an impact is over long before the load that would crush the footing of a wall gets there, so a glass wall standing on grass shatters where a thrown block hits it, rather than the grass under it soaking up the blow. Under its own weight soil still cracks and falls like anything else.

### How blocks get damaged

1. **Mining** strikes six times a second with what the player holds. A strike that gets past the block's elastic limit deals a sixth second of its mining rate, less the less it gets past it and more for a better tool (see [Mining with tools](#game-modes-items-and-crafting)); one that does not, nothing. Each hit also applies 20 N·s along the viewing ray to a loose object at the hit point and an equal, opposite impulse to the player (the fixed map transfers its share to the ground). Hits away from the object's center spin it. As damage costs a worn block more, its cracks spread slowly at first and fast at the end.
2. **Impacts** are analysed as a whole, on background threads of their own (`ImpactAnalysis`, `ImpactWorker`), and answered a few steps later:
   - A contact closing faster than 1 m/s is an impact. Its contacts are gathered over the steps the two bodies close fast (CCD often splits one impact over two). `ImpactKinematics` computes the energy available at peak compression from their resultant normal impulses and moments. For generalized impulse p, incoming velocity v and inverse mass M⁻¹, let C = −p·v and D = p·M⁻¹·p, summed over moving participants. Then W = C²/(2D) when C and D are positive, capped at the available kinetic energy. This includes rotational effective mass, ignores free tangential motion, and removes common center-of-mass motion between two free bodies. Scaling all impulses cancels out. The former ½·Σ J·v estimate counted only the position solver's partial stopping impulse: otherwise identical hits at 10%, 50% and 90% of a timestep could report 90%, 50% and 10% of the incoming energy. The `impact-step-phase` regression now checks equal energy at all three timings. W is the peak elastic energy, including the portion subsequently returned by bounce, rather than just the final dissipated kinetic energy.
   - The impact is an equivalent static load (the energy method of impact loading): its contact forces are scaled until the elastic energy both sides store equals W. Each side's stiffness comes from a CPU `StressLattice` of its cells around the contact: all of an object (with inertia relief), or up to 1024 map cells, the edge held by the rest of the map. So the peak force grows with the impactor's mass and speed and with how stiff the structure is where it is hit, and every cell's stress follows, added to what it already carries.
   - How a solver shares an impulse between contacts is arbitrary, so the force on each cell follows how fast the bodies were closing there before the impact: even for a flat landing, more at the far end of a spinning body. A contact on a corner touches several cells; it presses the one facing the other body.
   - Each side's force weights are normalized to the same total before the common energy scaling. Different numbers of touched cells or closing speeds must not independently amplify one body's force or shield the other. Both bodies can take damage, according to their materials and structural stresses.
   - Contacts have a finite footprint shared by both bodies. The normal in each body's grid estimates whether it meets a face, edge or corner; the sharper side sets the patch. The area varies continuously from a full cell face to a quarter for a diagonal edge and a ninth for a diagonal corner. Pressure, shear and half-cell compliance use that area, so sharp hits concentrate damage locally while broad contacts transfer more load into the structure. This bounded voxel-scale approximation does not resolve indentation, exact overlapping faces or subvoxel chips.
   - Cells fail weakest first. Past the load where the first cells reach their elastic limit (with any within 5% of it), the energy left breaks them, shared by the energy each holds past its limit, so a punched block shears out while its neighbours only crack. A cell that absorbs k times its elastic capacity in the failing mode (lim² / 2E per volume) takes 2k HP: a brittle block snaps in tension for little energy, crushing it takes a lot, and a granular one always counts as crushed.
   - If cells broke, the analysis goes on without them on the energy left: the impact presses on the cells behind those it broke, until the struck part comes loose or the impact breaks through into open space. Energy that goes through a hole breaks nothing else. A moving object that breaks flies apart, which ends it.
   - An object that breaks through the map gets back the share of its momentum breaking did not use: a falling cube carries on through the bridge deck it snapped, a thrown block through a window.

   For example: a 3×3×3 stone cube (70 t) dropped 8 m onto a plank bridge snaps the deck at both piers 0.1 s after landing and falls on through. Dropped 0.5 m onto the tip of a stone cantilever, a plank block leaves its root at 92% health while the cube snaps it. A glass block dropped 6 m onto stone (130 kJ) shatters, and the stone is unharmed; dropped 0.1 m, nothing happens.
3. **Stress** (anywhere, sustained loads):
   - The loads are gravity, the forces of contacts closing slower than 1 m/s smoothed over 0.1 s, and for objects their inertia. They are analysed again when they change by 2% or their centroid moves half a block.
   - The map is analysed continuously on the GPU (`GpuStressLattice`), two iterations a step while it has not settled: after an edit its stresses are shown within a step or two (relative residual 10⁻³) and settled (10⁻⁴) in about a tenth of a second, while the player keeps building. Objects are analysed on a background thread (`StressWorker`), longest-waiting first, each lattice warm-started.
   - A block stressed to ρ times its elastic limit takes 100 × (ρ² − 1) HP per second: the energy past its limit, in units of what it holds at the limit. As it wears its limit falls and ρ grows, so a small overload creaks for a moment and a large one breaks at once. The dirt link under a third stone block (25% over) tears in about a second, the 10-block cantilever (28% over) drops in under one.
   - Failure localizes, as in real structures: of each face-connected group of overloaded blocks only the most overloaded takes damage, so a crushed leg gives at one block rather than all along, and a crack between two blocks breaks one of them.
   - Only a settled analysis of the body as it is now deals damage. Once a block breaks the loads take new paths, and the old analysis says nothing about them: a leg that lost its foot hangs from the table top rather than being crushed by loads it no longer carries.

Damage sheds camera-facing chips cut from random 2×2-pixel regions of the damaged face's texture. The count is proportional to the fraction of the block's total breaking damage absorbed: every intact breakable block sheds 32 particles through destruction, regardless of its material or how many hits it takes. Fractional counts carry between hits through the block's accumulated wear, and the final hit emits only the remaining budget, even if it deals excess damage. Mining and impact chips start around the contact point; stress failures use an exposed face of the failing cell. Chips inherit the object's motion, fall and bounce, then disappear after a random 1–2 seconds. They pause with the world, and at most 32,768 are kept at once.

### The player

The player is a physical body like the objects: an upright 0.6 × 0.6 × 1.8 m box of 80 kg, its bottom edges beveled 6 cm at 45°, a `RigidBody` with `FixedRotation`, so contacts push it but never tip or turn it. The view turns freely, with a vertical field of view of 110° that eases out to 120° while sprinting and back when it stops, over about a quarter of a second; the box does not. Objects collide with it, push it and land on it, and it pushes them. Gravity pulls it three times as hard as the rest of the world (`GravityScale`, integrated by the solver as gravity), so it falls and lands briskly while blocks and loads keep the real one.

- **Walking.** The feet have no sliding friction; they grip by pushing. While the bottom face touches something (a contact pushing it up at 45° or steeper, or when the solver's final contact forces leave that out, as they can on a slope, short rays cast down from the bottom), each step the legs push the player toward the speed asked for, 4.5 m/s (sprinting, only forward, 6.75 straight ahead; going diagonally only the part straight ahead speeds up), relative to the surface it stands on and along it, so slopes are walked up and down rather than into. The legs push the way asked at 160 m/s² times the ground's friction, and the ground's friction drags on the player's speed over it, in proportion to that speed and to the friction; the two balance at the speed asked for, which is therefore the most the player walks at, on any ground. Grippy ground gets the player going and stops it fast, slippery ground slowly: the speed closes in on the one asked for with a time constant of 4.5 / (160 × friction) s, 0.04 s on stone and 0.09 s on leaves, where the player also slides 2.3 times as far when it lets go. They also hold the player against the pull of gravity down a slope, so it stands still on one, and keep its feet on the ground: what a lip it rides over gives it upward is taken back, unless it jumped. With no key held they push toward standing still on that surface, so the player rides a moving object. Whatever the legs give the player they take from what it stands on, at the contact points, shared by how hard each pushes back: Newton's third law. Walking across a raft of leaves sliding on ice pushes the raft back with as much momentum, and the push loads the cells underfoot for the stress analysis.
- **Jumping** is an impulse of 9.9 m/s (1.6 m high under the threefold gravity, clearing a block), taken from what the player stands on in the same way. It takes a press: a press in the air waits 0.1 s for the feet to touch down, and holding the key does not jump again.
- **In the air** the player steers a little, at up to 3 m/s², pushing on nothing, and the air drags on it in proportion to its speed squared, as on a person of its mass (0.0053 /m: ½ × 1.2 kg/m³ × 0.7 m² / 80 kg). That slows a run by a fraction of a percent per second, a long fall more: the player falls at most about 75 m/s.
- **Pushing.** The hands push the objects the player walks into, found by rays cast ahead from its ankles to its head: toward its walking speed, with up to 30 kN, at its middle and as high as the object's center of mass, pushing harder with one hand than the other to keep the object from turning away. The feet take the push back into the ground. A block of planks goes along at walking speed; one of stone (2.6 t, 18 kN of friction on stone) follows at walking speed after half a second; a 3×3×3 stone cube (70 t) does not budge.
- **Stepping up.** Walking into something from 1 cm to 0.6 m high, like a slab, a stair or the low edge of a tilted object, with room above it, puts the player on top of it, 3 cm above so it walks on over the edge as it settles; the view catches up over 0.08 s. Anything lower, even an edge level with its feet, it rides over on its beveled bottom: cutting the inside corner of an L of blocks, half over the air, it passes onto the other arm. A whole block takes a jump; jumping at one from too close, the feet hit its side and stop without being thrown back, and steering carries the player on over its top.
- **Landing** never bounces: the player's restitution is zero, and the impact goes into the analysis below.
- **Crouching.** Hold either Shift while walking to use a 1.35 m collision box (75% of standing height) and walk at half speed, without sprinting. On ground the feet stay fixed and the top and view lower; in the air the top and view stay fixed while the bottom rises. Releasing Shift restores the height only when the entire standing box has room, including around tilted objects. Grounded crouching stops the foot area at a ledge while allowing motion along it and jumping away. Support comes from upward faces under the foot area, with the same 45° slope limit as walking, so inclined surfaces remain walkable.
- **Discrete overlap recovery.** Before movement, voxel bodies and the player are translated out of existing overlaps deeper than 1 cm. Box separation includes all fifteen axes for rotated cells. Two moving bodies share the correction by inverse mass; fixed terrain takes none. This positional correction preserves velocities; normal contact solving and continuous detection still handle motion during the step.

- **Flying** (F) pushes on nothing: it holds the player up and steers it at up to 30 m/s² toward 10 m/s (sprinting, 15 straight ahead). The box still collides.

To impacts and loads the player is three stacked 0.6 m cells of a block material of its own (`Player.Material`), which share one health. It is analysed like any block:

- **Impacts** between the player and the map or an object go to the same impact analysis, the player a free side of three cells. A body is soft and weak next to stone (E = 0.43 MPa, limits 150/200/100 kPa), so it is usually the side that gives: unhurt it soaks up about 11 kJ (a fall of 4.5 m under its threefold gravity) and presses with up to 70 kN, and about 55 kJ (a fall of about 35 m, which the air's drag slows) kills it, on stone or grass alike. A fall of 6 m leaves it at 97%, 12 m at 87%. Weaker structures give first: a fall of 7 m onto a roof of leaves tears it open where the walls hold it, and the player goes on through with its middle.
- **Sustained loads**: gravity, the smoothed contact loads and the push of its legs are solved every step on the player's three-cell lattice, with inertia relief. A stone block resting on its head is a third of what it endures; a heavier load crushes it at 100 × (ρ² − 1) HP/s, as it would a block.
- **Health** goes from 1 to 0 as a block's does: damage d costs h² 2d / (Durability × Stiffness), so the first hurts cost little health and the last a lot, and a hurt player has a lower elastic limit, so it is hurt by less. Health never heals; at 0 the player dies, and Enter respawns it, unhurt, where it started. Falling out of the world kills it too.
- **Invulnerable** (I): impacts and loads are analysed as before, so the world reacts the same, but the player takes no damage.
- Between two moving bodies an impact can take no more than their kinetic energy in their center-of-mass frame, ½·μ·v² for the reduced mass μ: a 0.7 t block thrown at the player at 22 m/s takes 17 kJ, not the block's 170 kJ, and hurts it a little.

### Code map

| File | What |
|---|---|
| `World.cs` | Bodies, physics, contacts → impacts and loads, damage, splitting and detaching, rebuilding bodies. |
| `ImpactWorker.cs` | The impact analysis and the background threads that run it. |
| `ImpactKinematics.cs` | Peak compression energy from incoming motion and contact impulse distribution. |
| `StressWorker.cs` | The background structural analysis of objects' sustained loads, fed and read through messages only. |
| `MYT.Kit/Structural/GpuStressLattice.cs`, `assets/shaders/structural` | The map's analysis on the GPU. |
| `RigidMass.cs` | Mass properties of a set of cells and inertia relief, for both analyses. |
| `VoxelBody.cs` | Map and object bodies: grids, poses, stress results, mass properties. |
| `Blocks.cs` | Block types, materials and their health model, what a strike does to them, what they drop, the `Voxel` cell. |
| `Items.cs`, `Inventory.cs`, `Crafting.cs`, `GameMode.cs` | Items and tools, the 36-slot inventory and how clicks move stacks, the crafting list, survival and creative. |
| `DroppedItem.cs`, `WorldItems.cs` | Dropped items: their bodies, persistent parking and pickup (part of `World`). |
| `ItemSprites.cs`, `ItemMesher.cs` | The items' images, and their models: cubes for blocks, images cut a pixel thick, drawn and collided with. |
| `InventoryScreen.cs` | The inventory screen: the creative grid, the crafting list, the slots and their clicks. |
| `HeldSwing.cs` | The held item's swing, which lands the mining hits: keyframed chop, punch and jab poses about the grip. |
| `VoxelMesher.cs` | Render meshes (visible faces, ambient occlusion) and collision meshes. |
| `Renderer.cs`, `assets/shaders/voxel`, `assets/shaders/gui` | Texture array (blocks, cracks, items, the arm's skin), the interface's atlas, chunk buffers, per-cell damage and stress data, dropped and held items, particles, lines and the interface. |
| `Program.cs`, `App.cs` | The window and frame loop; the title screen and every menu, and going between them and worlds. |
| `Game.cs` | A world being played: the game mode, controls, mining and drops, the held item, streaming, the HUD, saving and autosaving. |
| `InterfaceArt.cs` | The interface's atlas: the font cut from `bitmapfont.png`, dirt, the title, the inventory's art, cube icons for blocks and images for items. |
| `WorldStore.cs`, `WorldFile.cs`, `Settings.cs` | Saved worlds' folders and records, `world.dat` snapshots, the settings and the server list. |
| `MYT.Kit/Gui` | The immediate-mode GUI library ([Gui.md](Gui.md)). |
| `BlockParticles.cs` | Camera-facing 2×2-pixel texture chips proportional to block damage, with random 1–2 s lifetimes, gravity and collision bounce. |
| `ParticleRaycaster.cs` | Conservative object hierarchy for debris collision rays; refits motion, handles pending edits and preserves exact voxel-grid hits. |
| `GraphicsContext.cs` | Window, device, swapchain, frames. |
| `Player.cs` | Input, and the player: its body, material, health and view. How it walks, jumps and gets hurt is in `World.cs`. |
| `TerrainGenerator.cs`, `Noise.cs` | The world: the spawn area and its structures, and the terrain, biomes and trees generated around it from a seed, a column at a time. |
| `ChunkStreamer.cs`, `BackgroundWorkers.cs` | Generating, loading and unloading columns around the player, keeping changed ones. |
| `ColliderBaker.cs` | Map colliders baked ahead on a background thread for the simulated region. |
| `VertexArena.cs` | Device memory for the chunk meshes. |

### Tests

```sh
dotnet run --project src/VoxelTests -c Release
```

`src/VoxelTests` covers the grid, the solver against the cases above, and the game world on a headless GPU:

- the health model: elastic limit and damage scaled by health;
- mining damage that stays, six reduced hits per second, equal thrust and recoil including through a breaking split;
- particle texture crops, emission positions, billboards, lifetimes and capacity; equal damage-based totals across materials, tiny hits, excess damage and block replacement;
- the dirt link tearing under a third stone block, within a second;
- the cantilever breaking at 10 blocks, within a second;
- a table whose legs give at one block each, not whole;
- splitting with the right velocities;
- detaching from the map;
- glass shattering on a 6 m fall and not on a 0.1 m one;
- a falling cube snapping a bridge as it lands, and carrying on through;
- the same drop leaving a cantilever standing under a plank and snapping it under a 70 t cube;
- a thrown block breaking a window and flying on through, and shattering a glass wall standing on grass without harming the grass;
- soil taking a heavy cube's fall and a thrown glass block without breaking;
- building on a moving object;
- an object spanning chunks;
- the generated world standing on its own, its trees well within their limits, with the cost of an edit;
- the GPU lattice against the same statics and beam theory, against the CPU lattice on terrain, and settling edits;
- the player: standing, walking, stopping and jumping; getting going and stopping 7/3 as slowly on leaves as on stone, at the same walking speed, and slowed by the air's drag when thrown; pushing a raft back with as much momentum as it gains; falls that hurt from about 4 m and kill from about 35 m, on stone and grass, and none when invulnerable; carrying a block on its head and crushed by a cube; knocked along and hurt by a thrown block; breaking through a roof of leaves; walking along and back onto a block's top edge, sliding along a step it walks into, and sprinting into one unhurt; standing still on a 10° slope and walking up, down and across it at full speed; stepping onto a ledge 0.4 m up and stopped by one 0.8 m up; pushing a block of planks and one of stone along, but not a 70 t cube; cutting the inside corner of an L of blocks; walking onto lips of 2, 5 and 30 cm; jumping at a block from too close without being thrown back; walking the generated world in sixteen directions without ever being caught on nothing;
- objects of many shapes dropped tilted and spinning onto the generated world and onto each other, none sinking into the map or another object or falling through the world;
- the generator: heights in range and every biome present over 8 × 8 km, the spawn area the same whatever the seed, the same columns from the same seed, under 5 ms a column; trees whole across column borders; a block of every biome standing under the stress model with nothing past 0.6 of its limit;
- the GUI library: atlas packing, fonts, clipping, and buttons, sliders, text fields and lists driven by a made-up pointer and keys;
- saves: worlds' folders, renaming, listing and deleting, game modes kept and old worlds in creative; settings and servers kept, out-of-range settings brought in range, damaged files refused, version 1 files read; and a world saved near and far from its changes, read back and played again, with the player and its inventory (a carried stack put back), the edits, a worn object, the objects' resting places and a dropped item as they were;
- the inventory: stacks filling before new ones start, the hotbar first; Minecraft's clicks, Shift moves and number keys; crafting from logs to pickaxes, mixed planks, unlimited resources, and nothing crafted without the cost or the room;
- tools: a hand never through stone or ore but through a log in 13 strikes; each pickaxe through what the one before could not, and through stone in fewer strikes; worn stone giving way to a hand; one-hit breaking, never of bedrock;
- dropped items: tools' bodies closed with two faces at every edge, a pixel thick and the size of their image; a cube and a pickaxe coming to rest on the floor; picked up after their delay, by falling on the player or the player walking into them, and left where the inventory is full; a 325 kg stone item dropped hard on glass and on the player harming neither;
- ore: gold and diamond veins at their depths and rates, the same every time, and none in the spawn area;
- the held item's swing: every style starting and ending at rest, smooth, thrusting farthest forward as it strikes and dropping farthest down after, and lasting longer than the time between hits; a click hitting once, as its swing strikes; six hits a second held, each 70 ms after it starts its swing, which it catches below rest; clicks faster than a swing strikes each hitting once; no jump in the pose, a millisecond at a time;
- streaming: the player flying 600 m out and back and skimming the ground, never falling through, with a bounded number of columns and colliders loaded, a parked object waiting where it was, a block placed before leaving still there after its column was unloaded, and no frame stalled past 33 ms by the simulation moving along.

`MYT_VALIDATION=0` turns the validation layer off.

### Limitations

- The solver's 10 iterations do not always pass a heavy body's momentum fully to a light one within a step: a 0.7 t block thrown at the 80 kg player at 22 m/s knocks it along at about 14 m/s instead of 19.7, and a block that lands right behind the player can drag it to a stop with it.
- The player steps up only 0.6 m without jumping: a whole block takes a jump. Riding over an edge level with its feet, it bumps up about a centimetre for a step.
- The ground under the player is found from the solver's contact forces, which at 10 iterations sometimes leave out one that held it up; short rays cast down cover that, but only for the map and objects under its middle and corners.
- Objects never sleep: every one in the simulated region stays a rigid body there; beyond it they are parked, frozen where they are.
- Changed columns are kept in memory only: a new world, or quitting, forgets them. The map's columns reach 512 m up.
- Structures reaching beyond the stress analysis' box are analysed only in it, and those beyond it not at all, until the player comes near.
- An edit costs a physics rebuild of about 20 ms, and so does the simulated region moving on, every 16 m or so the player travels; the stress analysis' box moving on, every 12 m, restarts the map's analysis. The map's stress analysis then settles in about a tenth of a second, at 3-6 ms of GPU time a step while it does; an object's gets a fresh lattice on the worker thread.
- The GPU lattice spends a thread on every air node of its box, and long slender structures take it a few times more iterations than the CPU lattice.
- An impact's damage comes 2-5 steps after it, the time to gather and analyse it; an object breaking through rests against what it broke for that long. Each round of the analysis costs 10-25 ms for a map region of 1024 cells.
- A broken block emits texture chips and vanishes; there is no physical rubble. Only blocks the player mines drop items.
- Tools do not wear out, and only pickaxes exist: there are no shovels or axes, so they mine every block alike.
- Fluids and explosions are not modelled.

### Fully generated distance-32 follow-up

A second investigation used the later saved position and both stationary and turning
replays. The plan was to separate GPU rendering, draw submission, presentation waits and
simulation after all 3,409 columns had loaded, then optimize the dominant measured cost
without changing the timestep or collision tolerances.

Repeated terrain recovery still consumed several milliseconds per simulation step. A
cached terrain result now proves when another full query cannot produce a correction
greater than the existing 1 cm threshold. For every candidate cell pair, the minimum
overlap on the three fixed terrain axes bounds the full separating-axis penetration from
above. The largest of these bounds, including SAT-rejected candidates, supplies the
remaining motion allowance. Unvisited cells have no positive overlap on at least one
terrain axis. Translation plus a conservative quaternion-displacement bound over the
shape's bounding radius consumes that allowance. The certificate is measured from the
shape's bounding-box center, which also handles objects whose mesh origin is far from
their center of mass. Moving bodies still undergo ordinary GPU physics and structural
analysis; only redundant CPU terrain recovery is omitted.

Certificates never skip a potentially deep correction. Corrections against other bodies
still run in their original order, and a push by another body consumes the same motion
allowance before the next recovery pass. Body topology changes and every terrain grid
mutation invalidate reuse. Grid revision tracking includes unmarked cell updates and
chunk streaming, because map topology versions alone do not cover edits outside the
structural simulation window. `MYT_TERRAIN_REUSE=0` disables this optimization for comparison.

`voxel-terrain-certificates` compares 240 successive recoveries against exhaustive SAT,
including small drifts, rotations, larger movements, water-to-solid updates, chunk removal,
chunk replacement, clearing and regrowth. Existing numerical, hierarchy, ordered recovery
and grid checks also pass. A warmed 170-body/5,000-cell snapshot benchmark measured 0.767 ms
per step with zero managed allocation and positions matching exhaustive recovery within
1e-9 m; the previous indexed implementation measured 3.24 ms on this workload.

For repeatable saved-world runs, use a **fresh copy** of the original save for each run:
loading an existing world saves it on shutdown even in screenshot mode. Keep `MYT_DATA`
pointed at that temporary directory. `MYT_MAXIMIZED=1` profiles at the actual display size;
the startup message reports the build configuration and framebuffer dimensions. Use `dotnet run -c Release` (or
`--configuration Release`); `--release` does not select the build configuration.

The fresh-snapshot stationary comparison used Release, FIFO, 1280×720, render and
simulation distances 32, and 6,000 frames on the RTX 3060. Complete loop intervals after
the first sixty frames (including loading) averaged 23.88 ms before this follow-up and
14.40 ms afterward, approximately 42 versus 69 FPS. The p99 decreased from 57.78 to
38.95 ms. GPU rendering was essentially unchanged, 0.422 versus 0.423 ms; the change
reduces simulation work. The final scene still drew 4,601 chunks / 662,858 quads in four
voxel calls. These are replay measurements rather than a constant-FPS guarantee.

A separate 6,000-frame turning replay at the maximized 2560×1324 framebuffer averaged
14.14 ms (about 71 FPS), with GPU rendering averaging 0.651 ms. Its initial temporary
save had already been advanced by an earlier replay, so it is a display-size endurance
check, not a controlled before/after comparison. Neither replay reproduced sustained
6 FPS in Release. The user subsequently confirmed that the proper Release command
runs acceptably; previous runs used `--release` rather than `-c Release`.

Final verification: Release solution build succeeded; ten grid/collision checks and five
GPU crouch/overlap scenarios passed. The GPU scenarios ran with Vulkan validation enabled
and emitted no validation messages. Renderer geometry and submission were unchanged in
this follow-up; the earlier pixel-equivalence results still cover that implementation.

### Player controls and video settings

Settings now has General, Video and Controls tabs. Controls accepts keyboard keys and
three mouse buttons for movement, mining, placing, inventory, hotbar slots and creative
actions. Selecting an occupied binding swaps the two actions. Escape cancels key capture;
Reset restores defaults. Escape remains the menu key and F3 remains reserved for creative
diagnostics. Settings are saved when leaving the settings screen; older JSON files pick
up defaults for the added options.

FPS and slowest-frame timing always appear. Creative players can toggle the rest of the
diagnostics with F3; survival has no diagnostics toggle. Gameplay elements such as the
hotbar, status bars and held-item name remain visible. R launches the held solid block as
a full-size physics object in creative, using the existing test launch speed, without
consuming inventory. G still spawns a 3×3×3 cube.

The sky follows a complete **32-minute day/night cycle**, starting at noon when
entering a world and pausing with the game. `DaylightCycle.DurationSeconds` controls the
period, currently 1,920 seconds. A procedural sky draws the moving
sun, a cratered moon orbiting at 32/31 of the sun’s speed, and rotating stars that fade in
at night and remain visible below the horizon. Wide sunrise/sunset glows follow the sun
above and below the horizon, leaving their
upper tip visible after sunset against a dark blue opposite horizon. Dawn shifts from
orange to yellow; dusk shifts from yellow to orange. The existing orbital tilt is shared
by both bodies, and the moon's position advances across solar days. At noon, the sky
has a broad, soft cyan-blue region around the sun, blending into a bright blue horizon.
Deep blue is strongest opposite the sun during twilight. Fog shares the sky gradient. Sun direction, shadow maps, sunlight color and ambient sky illumination follow the cycle;
furnace lights retain their own brightness.
The cycle clock restarts when a world is reopened.

Video options include transparent leaves, ambient occlusion, directional sunlight,
specular highlights, dynamic sun shadows, block lighting and ambient brightness.
Transparent leaves use the cutout texture and retain faces against adjacent blocks,
including other leaves; raster back-face culling still applies. The transparent leaf artwork
uses opaque neutral gray as a mask, which the importer converts to alpha zero; all other
block textures keep their authored alpha. Fragment shaders round interpolated texture-layer
numbers before selecting leaf variants, avoiding opaque/cutout alternation within one face.
Opaque leaves use the filled texture and normal neighbor culling. Changing this option remeshes loaded terrain
and objects, with the option captured separately for each background job. AO can be
switched without remeshing.

Lighting runs on the GPU in `ComputeLighting`, `light_diffuse.comp` and
`light_points.comp`. Each loaded terrain column has explicit 16³ bricks from its lowest
chunk (or zero) to one chunk above its highest chunk, including absent all-air chunks.
Completely empty loaded columns also receive air bricks up to the loaded terrain ceiling.
Every cell owns RGB light, including solid, transparent and air blocks. Cells outside the
loaded volume have only the roof-column sky boundary condition; the game does not
simulate an infinite world.

Two complete Jacobi diffusion steps run every rendered frame, across all loaded bricks,
independent of the camera, visibility, simulation radius or CPU propagation queues.
Persistent device-local buffers alternate input and output, with barriers between steps
and before rendering. One step moves information across one cell face. Bricks share
neighbor values through workgroup memory. Fully opaque interiors update to zero without
transport work; their outer cells retain reflected incoming irradiance. Air surrounded by
air uses a simpler diffusion path. GPU storage is RGB9E5: three floating components with
a shared exponent, four bytes per block per buffer (eight bytes with both buffers).
Transport arithmetic uses ordinary float RGB. Buffers grow geometrically and preserve
existing light; the overlay reports allocated grid memory and loaded cell count.

Heat-style six-neighbor diffusion averages incident RGB with damping, with sky and lit
furnaces held as sources. Removing a source or closing an opening diffuses away old light.
Transparent materials transmit, and slabs/stairs require matching open half-cell faces.
Terrain returns incoming intensity multiplied by average linear texture RGB and average
alpha. Reflection returns to the same air side of an opaque surface, preventing a scalar
light value in a wall from leaking into the opposite room. This is approximate diffuse
transport. Objects sample the world grid and cast direct shadows; they neither reflect
ambient light nor obstruct its transport.

Direct illumination adds to the ambient RGB at a visible surface. Sunlight uses a GPU
2048² orthographic depth pass over terrain and object meshes, including alpha cutouts,
covering up to 128 blocks around the viewer and fading to roof-column visibility at its
border. Point lights use six 16² depth maps, rebuilt in a compute dispatch each frame.
Rays walk half cells and store linear cube-face depth. Complete maps become available to
the fragment shader together, after a barrier. Point-light object occlusion is voxelized;
sun shadows use the meshes. Lit furnaces on objects seed the world light grid too.

Manual saves and autosaves snapshot the current GPU light values into the version 6 world
file. They do not wait for convergence. Unloaded light bricks are archived and restored
when they stream back. Loading restores matching light bricks before the first compute
step; old worlds without lighting remain readable. GPU readback at snapshot/unload time
and capacity growth can briefly stall the render queue. Simulation, rendering and save
memory scale with loaded volume; a large render distance is substantially more expensive
than the former camera-prioritized CPU frontier.

Keyboard holds are tracked by press/repeat/release callbacks, independent of per-frame
polling. Repeat keeps a key down without creating another press edge. Focus loss clears
held and pending keys; short taps between frames retain their press edge.

Regression checks: `lighting-compute` runs the actual GPU kernels with Vulkan validation.
It checks simultaneous updates in distant regions, RGB in air, glass transmission into a
sealed room, opaque wall closure, source removal, exact save-file round trips, GPU restore
and preserving existing light when new chunks stream in. `render-equivalence` checks
immediate direct shadows/furnaces, ambient in shadows, long-distance lighting, leaf cutouts
and draw submission equivalence. Its optional warmup performs full GPU steps for pixel
comparisons; gameplay always runs two steps per frame. The earlier CPU lighting scenarios
remain as transport/reference tests, rather than a measurement of gameplay propagation.

Surface sampling excludes fully opaque cells from the trilinear light footprint and
renormalizes the remaining weights. Opaque cells retain reflected irradiance for the
transport solver, but that value can include illumination from the outside of a wall;
blending it into room air caused bright seams along one-block-thick interior corners.
The sampler retains cached chunk lookups and uses chunk occupancy to avoid decoding
materials in uniform air or solid chunks. `enclosed-room` checks an unlit GPU air grid
and rendered interior surfaces from all eight corners, in both mesh modes with sun on
and off, plus a glass-skylight control that must illuminate the room.

GPU diffusion tracks exact changes to packed RGB values with two banks of 8³-tile flags,
mirroring the light buffers. A tile skips an iteration only when neither it nor any of
its six neighbors changed in the preceding iteration. Its two light copies are then
identical and all inputs to its next result are unchanged. This preserves the original
two Jacobi steps per frame, including propagation speed, with no convergence tolerance
or distance-based update budget. Terrain edits and streamed/restored bricks wake their
columns and adjoining columns, including every height affected by a changed roof. Moving
point sources, global lighting settings and buffer growth conservatively wake the grid.
Flag-bank offsets stay fixed within capacity as slots are added. Flags cost 64 bytes per
allocated chunk. Transfer/compute barriers protect flag clears and the next iteration's
reads, including across frames in flight.

Metadata construction uses the existing emitter-chunk index to avoid searching every
terrain cell for furnaces whenever streaming rebuilds the topology. Terrain removal
clears cached occupancy as well as materials, so an emptied brick uses the air path.
`MYT_LIGHT_REUSE=0` forces exhaustive diffusion for comparison. `lighting-compute`
compares every archived RGB word against that mode after multiple iteration counts,
settings changes, source removal, opaque chunk removal, negative coordinates, slabs,
moving object emitters, save/restore and storage growth.

A Release replay on an RTX 3060 used fresh copies of the same saved world, render and
simulation distances 32, the saved video settings (including dynamic shadows), 1280×720,
immediate presentation and 1,800 frames. After the first 60 frames, mean complete loop
intervals including streaming fell from 94.55 to 66.36 ms (10.6 to 15.1 FPS). In the loaded
scene, frames 1,000–1,799 averaged 68.32 ms before and 46.18 ms afterward (14.6 to 21.7 FPS);
GPU diffusion averaged 64.87 versus 17.88 ms. The loaded light domain remained 94,212,096
cells. Physics and streaming still limit frame time. These replays advance physics by
elapsed time, so they do not hold the simulation state identical between runs; the exact
GPU regression comparison separately verifies unchanged diffusion results. The original
save was left byte-for-byte unchanged.
