# GPU voxel simulation and geometry

The game defaults to GPU air pressure, material stress, impact response, damage evaluation,
and generated world-render geometry. CPU voxel grids remain available for streaming, saves,
queries and fragmentation. `MYT_CPU_REFERENCE=1` restores the CPU reference implementations
for these changed paths; physics and lighting continue using their existing GPU backends.

## Ownership and execution

| Work or data | Implementation |
|---|---|
| Loaded world occupancy/materials | CPU grids and the existing GPU lighting scene |
| Blast-participating voxel type/health | Persistent GPU chunk cache, synchronized with independent CPU chunk revisions |
| Air pressure and staggered face velocities | Shared sparse GPU tiles; no field readback |
| Map, object and player material stress | GPU RBSM/CG/multigrid; immutable kernels shared across bodies |
| Impact stress, strain energy and failure-energy allocation | GPU; the damage pass reads solver buffers directly |
| Voxel/player health, tool wear and sustained-load damage | GPU material laws; double precision preserves the existing stored damage values |
| Connectivity, fragment membership and contact advancement through broken cells | CPU |
| Generated chunk, shaped-block and terrain LOD render vertices | GPU count/emission passes and device-local arena storage |
| Inputs, streaming, body commands, effects and controller logic | CPU |

GPU blast changes are returned as compact voxel events. The CPU updates its voxel mirror,
emits effects and performs connectivity checks; fragment/body changes become subsequent GPU
commands. Object/player stress snapshots also support inspection and failure-site selection.
Impact solvers disable bulk stress readback: their per-cell pressure and energy feed GPU
damage allocation directly, with only damage/topology results returned to the CPU.
The player's three-cell stress solve completes in the current step because its health
affects impact loading immediately; scenery/object stress advances asynchronously.

Air occupancy queries use the GPU voxel cache and current inverse body transforms. A
per-tile candidate list avoids checking every object at every air cell. The CPU builds this
small broad-phase input; the GPU performs the voxel queries. Moving or rotated bodies do not
require rebuilding their voxel geometry just to update pressure occupancy.

This is not a fully GPU-resident game loop. In particular, collider mesh preparation,
physics rebuild coordination, particles, gameplay, and fragmentation still have CPU work.
The GPU voxel cache covers the active blast neighborhood, rather than replacing all existing
scene storage with one unified allocator. Individual simulation buffers currently respect
the device's `maxStorageBufferRange`; an oversized request reports a capacity error.

## Air-pressure model

`GpuBlastSimulation` uses a finite-volume acoustic stencil. Each substep reads the current
pressure/velocity field, writes updated face velocities to a second device buffer, then
updates pressure from their divergence. Substeps keep `waveSpeed * dt <= 0.55` for unit
cells. Wave speed is 64 m/s, sources inject for 50 ms, and simultaneous sources in the same
cell are combined before dispatch. Overlapping sources use the same state arrays.

The acoustic state is signed; only positive compression contributes to damage and outward
pressure loads. Closed solid faces prevent air flux and receive incident pressure. Material
stress is handled separately by the structural solver. Broken/moved obstacles are reflected
in occupancy at the next simulation step. The air field includes damping and a sponge near
its resident boundary, and releases its active work after the bounded blast interval.

This replaces the CPU solver's per-source shortest-arrival traversal. It changes attenuation,
reflections and blast shape; performance comparisons do not imply identical destruction.
The old nonnegative wavefront model remains available for comparison.

## Geometry

The render thread packs voxel inputs, with a time budget for initial snapshots. Interactive chunks count
and emit geometry in one GPU dispatch into a modest reserved arena range. If that range is
too small, the shader reports the required count without writing any vertices, and the
renderer retries with the correct size. Initial distant chunks and LOD retain exact-size
count/emission passes to bound memory use. Existing meshes remain visible until replacement
geometry completes. Once a bounded emission finishes, unused reserved space is returned to
the arena so small fragments do not retain large speculative allocations.
Production reads counts and flags, not generated vertices. Full vertex captures are test-only.

Edits have priority through snapshot scheduling and both GPU mesh passes. A chunk edited
while its previous snapshot is queued replaces that snapshot with current voxel inputs.
Changes made during a GPU submission are captured as soon as that submission completes.
Completed frame fences are polled without waiting for their slots to be reused. Initial chunks and terrain
LOD retain reserved dispatch slots, so edits remain responsive during streaming without
stopping background generation.
Empty-chunk commits wait for replacement surfaces in previously displayed neighbours only;
neighbours that have never rendered and are waiting for unloaded columns cannot retain a
deleted chunk's old mesh.
Empty-chunk commits preserve dependencies within connected groups, while unrelated ready
deletions commit independently instead of waiting for every pending deletion in the world.

On the RTX 3060 streaming regression, placement, removal and explosion updates reached the
displayed mesh after two frames instead of six. Continuous per-frame edits displayed 11
versions in 12 frames, with displayed snapshots at most one frame old. These are frame
latencies under that test workload, not a guarantee of a fixed wall-clock latency during
large explosions. The regression allows four frames and also checks edits that replace an
already queued snapshot. Bounded-emission tests compare exact geometry and verify that an
undersized output range is left entirely untouched before retrying.

Cubes, water, glass, transparent leaves, slabs, all stair forms, doors with their exact
thickness, and terrain LOD use this path. LOD workers still downsample CPU world snapshots;
face selection, transition hulls and horizon vertex generation run on the GPU. Static authored
item/interface assets and CPU reference meshers remain available.

## Checks and profiling

```bash
dotnet run --project src/VoxelTests -c Release -- gpu-meshing-headless
dotnet run --project src/VoxelTests -c Release -- gpu-pressure-stencil gpu-pressure-sources gpu-pressure-objects gpu-pressure-damage gpu-pressure-mirror gpu-pressure-world gpu-damage-laws gpu-object-stress gpu-impact-analysis
dotnet run --project src/VoxelTests -c Release -- gpu-meshing
dotnet run --project src/VoxelTests -c Release -- terrain-lod-render
```

The two windowed scenarios run separately because the test entry point selects one rendering
scenario per invocation. Use `gpu-meshing` and then `terrain-lod-render`. Headless checks compare
packed geometry, buffer guards, an independent acoustic stencil, material laws, GPU/CPU stress
and impact outcomes, mirror edits/unloads, moving obstacles and chain ignition. Vulkan
validation is enabled by default in the headless tests.

For a repeatable unsaved 27-TNT experiment:

```bash
MYT_PROF=1 MYT_SEED=713 MYT_AUTOPILOT=tnt MYT_AUTOTNT=100 MYT_AUTOTNT_COUNT=27 \
MYT_FIXED_DT=0.0166666666666667 MYT_FRAMES=220 MYT_SCREENSHOT=/tmp/myt-gpu.ppm \
MYT_DATA=/tmp/myt-gpu-data MYT_RENDER_DISTANCE=4 MYT_SIMULATION_DISTANCE=4 MYT_PRESENT_MODE=immediate \
dotnet run --project src/VoxelGame -c Release
```

Repeat with `MYT_CPU_REFERENCE=1` and different temporary data/screenshot paths. Run the
experiments consecutively, with other GPU checks finished. `MYT_PROF=1` includes the pressure
kernel time, full pressure-update time, waits, uploads, fragmentation and impact work.

Individual switches are `MYT_GPU_BLASTS=0`, `MYT_GPU_STRESS=0`, `MYT_GPU_IMPACTS=0`,
`MYT_GPU_DAMAGE=0`, and `MYT_GPU_MESH=0`. The reference blast loop applies damage cell by cell;
disable GPU damage too when benchmarking that reference to avoid a GPU submission per cell.

### Measured 27-TNT comparison

On an RTX 3060, consecutive runs with the settings above produced these frame times
over the 159 measured frames after warmup:

| Frame time | CPU reference | GPU paths |
| --- | ---: | ---: |
| Mean | 123.45 ms | 27.71 ms |
| Median | 13.62 ms | 17.72 ms |
| 99th percentile | 826.15 ms | 107.21 ms |
| Maximum | 831.93 ms | 112.96 ms |

The GPU paths greatly reduced explosion stalls but increased median frame time in this run.
This is a whole-game comparison, including CPU fragmentation, collider preparation, particle
updates, uploads and synchronization. The acoustic pressure model differs from the reference
wavefront model, so destruction and subsequent player/scene trajectories also differ; this
is not a comparison of identical simulation outputs. Raw logs are `/tmp/myt-result-gpu.log`
and `/tmp/myt-result-cpu.log` for this development session.

Targeted pressure, damage, stress, impact and player checks passed on the same hardware.
GPU mesh comparisons matched CPU packed geometry and rendered pixels; terrain LOD and
lighting checks passed. A separate 27-TNT run with Vulkan synchronization validation reported
no hazards.
