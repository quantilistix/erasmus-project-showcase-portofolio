# MYT Physics: how it works

This is the maintainer's companion to [Physics.md](Physics.md). It explains what happens inside `PhysicsSystem.Update`, why things are done the way they are, and where each piece lives. The two papers it builds on are in this folder:

- **VBD**: *Vertex Block Descent*, Chen et al. 2024 (`2403.06321v4.pdf`).
- **AVBD**: *Augmented Vertex Block Descent*, Giles, Diaz and Yuksel, SIGGRAPH 2025 (`Augmented_VBD-SIGGRAPH25.pdf`).

Equation numbers below refer to these papers.

---

## The big picture

Each step solves one implicit Euler time step as an optimization (VBD Eq. 1): find positions close to where inertia would carry every body, `y = x + h·v + h²·g`, while satisfying springs and contacts. VBD does this by **block coordinate descent**: it moves one *block* at a time, holding the others fixed, with one Newton step per block (VBD Eq. 3-6). A block is either

- a **soft body node**: 3 degrees of freedom, a 3×3 system; or
- a **rigid body**: 6 degrees of freedom (position and rotation), a 6×6 system (AVBD §4).

Blocks that share no spring can be solved in parallel. Nodes are therefore graph-colored, and each color is one GPU dispatch.

AVBD adds an **augmented Lagrangian** on top of this. Hard constraints (contacts, infinitely stiff springs) carry a dual variable λ and a stiffness k that ramps up while the constraint is violated. Contacts end up exactly satisfied without huge stiffness, which is what keeps stacks standing with few iterations.

Everything runs on the GPU. One `Update` records every pass of every substep into a single command buffer, submits it, and waits on a fence. The CPU never reads state back during a step; it only copies the final state back afterwards.

## Precision

Absolute positions and orientations (`Node.position`, `Rigid.position`, `Rigid.orientation`, collision vertex positions) are **double**, so bodies far from the origin keep millimetre detail. Everything *relative* is **float**: displacements within a step, a rigid vertex's rotated offset from the center of mass, contact geometry and dual variables, spring forces, the 3×3 and 6×6 Newton systems, and narrowphase geometry, which each thread computes around its own local origin. World-space bounds (triangles, bodies, BVH nodes) are float **rounded outward**, so the broad phase compares them without doubles and never misses a pair. Consumer GPUs run fp64 at 1/32-1/64 of the fp32 rate, and moving the inner loops to float made the solver 5-10× faster. The `far-away` test (a scene 20 km from the origin) checks that nothing degrades.

## One step, pass by pass

`PhysicsSystem.Update` records, for each substep:

| # | Pass | Shader | Threads | What it does |
|---|---|---|---|---|
| 1 | Edits | `apply_edits.comp` | per block | Applies teleports, velocity changes and impulses queued on the CPU (first substep only). |
| 2 | Step begin | `step_begin.comp` | per block | Records x^t, the inertial target y, and the adaptive initial guess (VBD Eq. 17), a rigid body's under gravity times its `GravityScale` (`inertia0.w`). |
| 3 | Spring warm start | `spring_update.comp` (mode 0) | per spring | Decays spring stiffness and λ from the last step (AVBD Eq. 19). |
| 4 | Surface update | `surface_update.comp` | per dynamic collision vertex | Start positions (x^t) and predicted end positions (from y). |
|   | Vertex directions | `vertex_cones.comp` | per vertex | Decodes and rotates contact cones at x^t once per substep; fixed vertices initialize once per geometry rebuild. Later detection passes reuse them for the start manifold. |
| 5 | Triangle bounds | `triangle_bounds.comp` | per dynamic triangle | Swept world AABB of each triangle, merged per workgroup into its body's running bounds. |
| 6 | Body bounds | `body_bounds.comp` | per dynamic body | Turns the running bounds into the body's AABB and resets them. |
| 7 | BVH refit | `bvh_refit.comp`, `body_bvh_refit.comp` | per BVH node | Refits soft-body triangle bounds and, for at least 32 bodies, the top-level body hierarchy, bottom up. |
|   | Body candidates | `body_candidates.comp` | per dynamic body | Queries nearby bodies once for all the body's triangles; a full list falls back to per-triangle hierarchy traversal. |
| 8 | Broad phase | `contact_pairs.comp` | per dynamic triangle | Lists the triangle pairs whose swept boxes overlap. |
| 9 | Narrow phase | `contact_args` (pairs), `contact_detect.comp` | per candidate pair (indirect) | DCD + CCD; emits contacts and looks up their warm start. |
| 10 | Contact lists | `contact_args`, `contact_lists` (count), `contact_scan`, `contact_lists` (fill) | | Files every contact under the blocks it touches, as compressed rows. |
| 11 | Initial guess limit | `guess_limit.comp` (3 modes) | per body / per contact (indirect) / per block | Conservative advancement: pulls each body's guess back to where its first contact closes. |
| 12 | Rigid coloring and work lists | `rigid_color.comp` ×4 + conflict pass, `rigid_work.comp` | workgroup per rigid body / per work bin | Snapshot-based greedy coloring, conflict removal, then stable parallel compaction by color and contact count. |
| 13 | Iterations | | | For each iteration: |
|   | Node solve | `node_solve.comp` | workgroup per node, per color | AVBD primal step for each soft body node. |
|   | Rigid solve | `rigid_solve.comp`, `rigid_solve_dense.comp`, `rigid_solve_serial.comp` | indirect, per color | AVBD primal step, 6×6 LDLᵀ; small bodies share a workgroup, dense bodies get their own. |
|   | Contact dual | `dual_update.comp` | per contact (indirect) | λ and k update (AVBD Algorithm 1, lines 26-31). |
|   | Spring dual | `spring_update.comp` (mode 1) | per spring | Stiffness ramp (Eq. 16), hard-spring λ, breaking. |
|   | Continuous detection | passes 4-10 in continuous mode | | After evenly spaced iterations: sweep x^t → current iterate and add missed contacts. |
| 14 | Step end | `step_end.comp` | per block | Velocities v = (x − x^t)/h. |
| 15 | Restitution | `restitution.comp` | workgroup per rigid body | Bounce impulse. |
| 16 | Contact store | `contact_store.comp` | per contact (indirect) | Saves λ, k and normal under the contact's key for the next step. |
| 17 | Contact reports | `contact_report.comp` | per contact (indirect) | Only with `ReportContacts`: appends every contact that pushed (λ_n < 0) to the report buffer, with its impulse λ·h, point and approach speed. |

After the last substep come the outputs: with `UpdateRigidTransforms` enabled (the default), `rigid_transforms.comp` writes model matrices, which are copied into each `RigidBody.TransformBuffer`. The voxel game disables these unused outputs because its renderer interpolates CPU poses. `soft_render_vertices.comp` still writes each soft body's vertices and normals. With reporting enabled, `contact_args.comp` generates a dispatch size from the live report count and `report_readback.comp` copies those reports to cached host memory. Empty updates still refresh the header. Then the state readback copies and a final barrier for the renderer and CPU.

Every dispatch is followed by a compute→compute (and indirect-argument) memory barrier (`ComputeCommands`).

**No pass loops over a whole body on one thread.** A dense mesh (the `map-sphere` test drops a 327k-triangle sphere) has hundreds of thousands of vertices, and resting on something gives it thousands of contacts, so per-body passes use a workgroup per body whose threads split the contacts and sum in shared memory, and per-body bounds are reductions. When one thread per body walked them, that scene took ~350 ms per step; it now takes ~4.

## GPU data

### Descriptor set 0: solver state (`solver_common.glsl`, `AvbdSolver.cs`)

| Binding | Buffer | Notes |
|---|---|---|
| 0 | `SolverParams` (uniform) | Written every update from `PhysicsSettings`. |
| 1 | `nodes` | All soft body nodes of all bodies, concatenated. `position.w` = mass (∞ = pinned). |
| 2 | `rigids` | One `Rigid` per body: pose about the center of mass, velocities, inertial and previous pose, inertia tensor. |
| 3-4 | `springs`, `springStates` | Float spring constants; state = (current stiffness, λ, broken). |
| 5-6 | `nodeSpringOffsets/Indices` | Compressed rows: the springs of each node. |
| 7 | `colorNodes` | Nodes sorted by color; `AvbdSolver` keeps each color's range. |
| 8 | `rigidTransforms` | Model matrices, copied out to each body's buffer. |
| 9 | `edits` | Host-visible, grows on demand. |
| 10 | `rigidColors` | Two snapshots of rigid colors; color 8 denotes serial overflow. |
| 11-12 | `nodeBodies`, `guessFractions` | For per-body conservative advancement: one fraction per soft body, then per rigid body. |
| 13-14 | `rigidWork`, `rigidArguments` | Compact body indices and indirect dispatch arguments, in eight colors × two contact-count bins, plus one ordered overflow queue. |

**Block numbering:** node `i` is block `i`; rigid body `r` is block `nodeCount + r`.

### Descriptor set 1: collision (`contact_common.glsl`, `GPUCollisionSystem.cs`, `CollisionGeometry.cs`)

Collision *bodies* are ordered **rigid, soft, then fixed**, one collision body per `FixedBody`, each with its own BVH and static bounds. Their vertices and triangles follow the same order, so dynamic ones come first (`DynamicVertexCount`, `DynamicTriangleCount`).

- **Vertices**: local position (fixed: world; rigid: relative to the center of mass; soft: unused) with, in `w`, the triangle of the flat surface the vertex lies inside or −1, info (body, kind, block, the body's own material id), and three world positions: `vertexStart` (x^t), `vertexEnd` (prediction or current iterate) and `vertexLast` (the previous step's x^t).
- **Bodies**: kind, block, triangle and vertex ranges, BVH root, swept AABB, and running bounds that `triangle_bounds` merges with `atomicMin`/`atomicMax` on order-preserving integer keys of the floats.
- **Triangles**: three vertices, three edge ids, body, and **ownership bits**. Every vertex and every edge of a body is owned by exactly one triangle (the first one using it). A triangle pair is then tested as "owned vertices against the other triangle, owned edges against owned edges", which visits every vertex-triangle and edge-edge pair exactly once. Above the ownership bits (from bit 8, `MaterialShift`) the word holds the triangle's material id; a contact side touches with the material of the triangle that owns its vertex or edge.
- **Triangle bounds**: each triangle's swept world AABB for the current pass.
- **Edges**: two vertices and the two adjacent triangles (for outward normals).
- **BVHs**: built on the CPU by `BvhBuilder` (binned SAH, float bounds rounded outward). One per fixed body, one per distinct rigid mesh in that mesh's center-of-mass frame, and one per soft body over its rest shape. Soft BVHs are kept together and refit every pass (`bvh_refit.comp`): each leaf bounds its triangles, then climbs while it is the second child of its parent to finish, using a per-node arrival counter (`bvhRefit`, which also holds parent links).
- **Cooking**: what depends only on a body's shape (the welded vertices, edges with their adjacent triangles, ownership bits and the BVH, all with body-local indices, and which of the body's triangles each one comes from, as welding drops slivers) is computed once per `FixedBody` and per rigid mesh and center of mass, kept in `ConditionalWeakTable`s, and only copied and offset by later rebuilds. Fixed surfaces also retain their outward-rounded triangle and body bounds. The concatenated geometry arrays retain their capacity across rebuilds. Soft bodies change shape and are cooked afresh each time. A body's `SurfaceMaterials` are cooked the same way, per body: its distinct materials and which of them each cooked triangle has.
- **Assembly**: a sequential pass assigns each body's vertex, edge and triangle ranges, material offsets and shared BVH root. Retained jobs store transforms and palette indices as values. After list counts and backing arrays are frozen, workers fill disjoint ranges through spans; the same body-local calculations run serially for small rebuilds. No worker changes list metadata or performs a global reduction. `MYT_PARALLEL_GEOMETRY=0` selects serial assembly. `geometry-assembly` compares every output buffer byte for byte after growth, removal, reordering, material changes, soft motion and shrinkage. This still reassembles dense geometry; it does not provide stable geometry slots.
- **Candidate pairs**: up to 16 × `MaxContacts` triangle pairs per pass from the broad phase. Overflow is counted in `DroppedContacts`.
- **Contacts**: up to `MaxContacts`, 176 bytes each, all float. `key.zw` holds the bodies' own material ids of both sides, which name the bodies for the contact reports. `weightsA.w` and `weightsB.w` hold the friction and restitution the contact mixed from its sides' materials when it was made.
- **Contact reports** (binding 22): a header (written, dropped) then up to `MaxContactReports` reports of 96 bytes, filled over all substeps of an update. Binding 23 holds their mapped readback buffer; only the written reports are copied, preserving the full configured capacity and overflow counts.
- **Body hierarchy** (bindings 24-26): top-level BVH nodes, body indices, and parent/arrival/escape links. Built from the bodies' initial world bounds and refit to their swept bounds every detection pass. Escape links let broad-phase queries skip subtrees without a second traversal stack. The topology stays until the next geometry rebuild; widely rearranged bodies can loosen its boxes and reduce its efficiency.
- **Body candidates** (bindings 27-28): up to 64 nearby bodies per dynamic body and their counts, refreshed each detection pass. A count above 64 selects the full hierarchy traversal, so overflow cannot drop collisions.
- **Vertex directions** (binding 29): decoded world-space cone axis and cosine at x^t. Static vertices initialize after a rebuild and dynamic vertices refresh each substep, after edits and step begin. These directions stay fixed during that substep's continuous detection passes, so caching preserves the original geometric tests.
- **Two hash tables** for warm starting, swapped every step: two descriptor sets differ only in which table is "current" and which is "previous".
- **Materials**: host-visible, friction and restitution: one per body (rigid, soft, fixed order), written from `Friction` and `Restitution` every update, then the distinct `SurfaceMaterials` of each body that has them, written as they were at the last rebuild.

## Collision detection

### Broad phase (`contact_pairs.comp`)

One thread per triangle of a rigid or soft body. It takes the triangle's swept box (start to end positions, from `triangle_bounds`) plus the margin. Scenes with at least 32 collision bodies query the refit top-level hierarchy once per dynamic body and reuse its candidates across the body's triangles. The body's swept box encloses every triangle query. More than 64 candidates falls back to querying the hierarchy per triangle; smaller scenes walk the body list directly. For each candidate body:

- Skip the body itself (no self-collision). A dynamic pair is enumerated from **one** side only: the side with **more triangles**, so the work spreads over more threads and each small triangle queries the coarser body's BVH; on a tie, the lower body index. Fixed geometry never enumerates.
- Test the body's bounds.
- Traverse the body's BVH. A rigid body's BVH is in its own frame, so the query box is transformed into that frame at the start and end pose, and padded by rotation angle × radius to cover the curved path in between.
- Append every leaf triangle whose box overlaps, in batches of 8 per atomic.

The phases are split so that a large triangle overlapping thousands of small ones (a soft box lying on a dense sphere) costs its thread only the BVH walk, while the pair tests spread over the whole GPU.

The hierarchy avoids an unconditional all-pairs body scan. With useful spatial pruning and bounded local overlap, body queries typically cost roughly O(N log N + K), where K is the number of nearby body pairs. This is not a worst-case guarantee: overlapping bounds, dense piles or a poorly pruned refit hierarchy can still make the work quadratic. Triangle complexity and the number of generated contacts also matter; the solver visits those contacts every iteration.

Physics uses batched buffers and dispatches per pass and solver color, with one submission per update. It does not launch a separate simulation or wait for each rigid object. The voxel renderer currently records a draw per visible chunk, including an object's chunks, into the frame's command buffer.

### Narrow phase (`contact_detect.comp`)

One thread per candidate pair (indirect dispatch). The owned features are tested:

Contact validity comes from the surface geometry. Previous contacts supply warm-start forces and penetration history; they cannot choose a rigid edge's outward direction. A warm start is discarded when its normal reverses.

**Vertex-triangle (VT).** With n the triangle's normal at x^t and d0 the vertex's signed distance:

- *DCD*: in front (d0 ≥ −tolerance), within reach (margin + both sides' motion), and its projection inside the triangle's tolerant footprint. Convert the world-space penetration tolerance to barycentric allowances using each edge's altitude. Clamp and normalize the accepted weights to keep the contact on the triangle.
- *CCD*: if DCD did not accept a contact, solve the coplanarity cubic on [0, 1] by derivative splitting and bisection. Test the triangle overlap and the vertex's incident triangle fan at the candidate time, not just its initial orientation. Rejecting the initial normal must not reject a collision made possible by rotation later in the step.
- *Behind* the triangle (d0 < −tolerance): on a non-convex mesh a vertex is behind most faces, so this only counts as penetration when the contact existed last step (and is shallower than `MaxPenetrationDepth`), or when the vertex was in front at the previous step.
- *Shared boundaries*: ownership deduplicates contacts between coplanar triangles. Faces at a sharp crease have different supporting planes, so both remain eligible. Evaluate coplanarity at the candidate time for CCD.
- *Resting manifold*: cooked vertex cones retain their conservative 25° allowance for DCD. A slightly tilted face can have several shallowly penetrating corners; testing only its exact supporting vertex removes the rest of the manifold and destabilizes piles. A vertex inside a planar fan gets the stricter adjacency test, with the penetration tolerance as distance slack. This applies to fixed and freely rotating bodies as well as characters.
- *Vertex fan at impact*: walk incident triangles through the edge adjacency. An outward supporting normal must leave their edge vectors behind its plane. The fan walk is bounded to 64 triangles; open or incomplete fans are handled conservatively. Deformable vertices retain primitive contacts as their surface bends.

**Edge-edge (EE).** Both discrete and continuous edge tests remain. They are essential when long crossed edges collide while all vertices miss the opposite faces.

- *DCD*: closest segment points are interior and within reach, and the edges are not parallel. The candidate normal is their cross product. Contacts within the penetration tolerance of an endpoint belong to neighboring VT tests. Those tests expand their footprints by the same world distance, so the handoff leaves no gap that would remove stack support.
- *Supporting cones*: for each rigid/fixed edge, project each incident triangle's opposite vertex into the plane perpendicular to the edge. An outward normal must point away from both resulting vectors and toward the surface exterior. This gives the convex edge's normal cone; a coplanar seam supplies only its surface normal. A concave edge is not an exposed supporting feature. Both DCD and CCD allow angular roundoff only: adding penetration slack to an edge cone permits spurious tilted forces.
- *Both sides must agree*: a normal from B to A must be outward for B and inward for A. Choose its sign from these two cones, or reject it when neither sign works. There are no fixed-rotation, world-up or 30° seam exceptions. Open edges marked `OpenEdgesContinue` supply their face normal; real sheet rims retain their outward half-plane.
- *CCD*: solve the coplanarity cubic and verify segment overlap. Evaluate **both** incident surface cones and endpoint distances at that root. VT footprints also include the world-space endpoint allowance. A rejected DCD normal still proceeds to CCD, and rejecting one root still allows later roots. Start-of-step cone rejection would miss rotating collisions.
- *Deformable surfaces*: soft edges retain conservative primitive crossing contacts because a surface may already wrap around a ridge. A rigid/fixed participant still constrains the normal through its solid cone. When both sides leave the sign ambiguous, use their starting separation.

These are local normal-cone and internal-edge tests, the same family of techniques used for [Box2D ghost collisions](https://box2d.org/posts/2020/06/ghost-collisions/) and [Jolt active edges](https://jrouwe.github.io/JoltPhysics/). They distinguish a triangulation crossing from a surface that can actually oppose the motion; disabling EE CCD does not make that distinction.

Regression scenarios: `edge-sliding` covers fixed and moving platforms, locked and free rotation, and three scene orientations; `concave-sliding` covers rotated L channels; `edge-ccd` uses fast crossed rods with no vertex-face coverage, including a rod spinning about its long axis. `endpoint-ccd` checks the EE/VT handoff at three mesh scales. `rigid-stack`, `pile` and the soft-contact scenarios check that filtering preserves support and penetration recovery.

Each new contact gets a **key** (vertex + triangle, or edge + edge):

1. It is looked up in the previous table to warm start λ and k (AVBD Eq. 19: λ ← αγλ, k ← max(k_start, γk)).
2. It is inserted into the current table with a compare-and-swap. In a continuous pass, a key already present means the step already has this contact, so it is skipped.

New contacts start at least as stiff as the heavier participant's m/h², so convergence does not depend on mass scale.

### Contact lists

Each block's solve only visits its own contacts, so contacts are filed by block:

1. Count contacts per block (atomics).
2. Exclusive scan (one workgroup).
3. Fill.

A contact touches at most four blocks (a vertex against a triangle's three nodes, or two edges' two nodes each), so the reference buffer holds 4 × `MaxContacts` entries and cannot overflow.

## The solver

### Contact constraints (AVBD §3.2-3.3, §4)

Each contact has three rows (tangent, bitangent, normal), linearized at x^t: `C = C0 + J·Δ`, with C0 the signed distance for the normal row and 0 for the friction rows. The tangent frame comes deterministically from the normal, so warm-started friction keeps its meaning.

- **Normal force**: `F = min(0, k·C + λ)`; it only pushes.
- **Friction**: `k·C_tb + λ_tb`, clamped to the Coulomb cone μ·|F_n|.
- **Hessian**:
  - An active normal row uses k.
  - A clamped row clear of the surface uses **stiffness rescaling** (AVBD Eq. 14), which is 0 for a speculative contact that never pushed, so bodies approaching it are not slowed.
  - A clamped row that is **touching** (C < margin) keeps k. Without that, a fresh contact starting at C = 0 is invisible to the Newton step, which then jumps straight through the surface and oscillates back.
- **Pre-existing penetration** is regularized (AVBD Eq. 18): only (1 − α) of it is corrected per step.
- **Dual update** after each iteration: λ ← clamp(k·C + λ); k ← k + β|C| while the force stays inside its bounds (AVBD Eq. 11-12).

### Soft body nodes (`node_solve.comp`)

A workgroup of 64 threads shares a node. Under a dense mesh a node has hundreds of contacts; 8 threads per node made that ~5× slower, while 64 costs a large, lightly loaded soft body ~25% more. The threads split its springs and contacts, accumulate the force and the 6 unique Hessian entries, and sum them in shared memory (VBD §3.9's block-level parallelism). One thread then solves the 3×3 system and writes the position **in place**. Springs never join same-colored nodes, so only a contact between same-colored nodes of two different soft bodies can read a neighbour mid-update, which makes that pair Jacobi-like for one iteration.

- **Springs** are `½·k·(len − rest)²` with k ramped up to the real stiffness (AVBD Eq. 16). Hard springs (`Stiffness = ∞`) are augmented Lagrangian constraints with their own λ.
- The **geometric stiffness** term is clamped to positive, so the Hessian stays positive definite.
- **Damping** acts on the displacement *relative* to each neighbour, so it resists stretching without dragging free motion.

### Rigid bodies (`rigid_solve.comp`)

The 6-DOF block uses position offset `p − y_p` and rotation offset `2·(q·q_y⁻¹)_v` (AVBD Eq. 20), and the update `q ← normalize(q + ½(0, Δw)·q)` (AVBD Eq. 21). Two deviations from the paper's contact model matter a lot in practice:

1. **Contact arms rotate exactly.** The displacement of a contact point is `Δp + (R_rel − I)·r`, not the linearized `Δp + Δθ × r`. With the linear form, a light body under a hard impact can "satisfy" its contacts with a huge fake rotation that the quaternion update never actually performs. It tunnels or spins up.
2. **Geometric stiffness.** With exact arms, each contact row curves in the rotation, with second derivative `F·(sym(d·rᵀ) − (d·r)·I)`. It can dwarf a light body's rotational inertia. Its diagonal of column norms is added to the rotational block, as AVBD §3.5 suggests.

Rigid adjacency lists with at most 256 contacts are bitonic-sorted by feature identity after enumeration. The sort pads only to the next power of two of the actual contact count. This makes small-body force accumulation independent of the atomic append order; larger lists retain their existing order.

The first color-refinement round reads the previous color hint in `Rigid.inertia1.w`;
subsequent rounds read alternating color snapshots. Step end writes the final active color
back into that component. CPU state synchronization and topology uploads retain it per
`RigidBody`, avoiding a global reset after adding an unrelated body. Only the inertia
column's xyz components enter the physical tensor, so this metadata changes neither mass
nor inertia. Every hint is revalidated by refinement and conflict pruning before solving.

After coloring, `rigid_work.comp` uses one workgroup per bin and tiled 64-body prefix scans to compact bodies in ascending index order and write indirect dispatch arguments. Each group owns its output range; no cross-workgroup counter or CPU readback is needed. Bodies with at most 128 initial contacts use clusters of 16 threads, two bodies per 32-thread workgroup; dense bodies keep a 256-thread workgroup each. Each cluster splits the contacts, reduces the force and the 21 unique Hessian entries in shared memory, and solves on one thread. Incomplete groups keep all threads at the barriers and mask their unused clusters. Empty bins issue zero workgroups. After continuous detection adds contacts, another snapshot conflict pass and work-list rebuild precede the next solve; newly dense bodies can change bins.

The fixed 6×6 LDLᵀ factorization, matrix assembly and shared reductions use constant indices and scalar factors, avoiding dynamically indexed private arrays and their GPU scratch-memory traffic. The small and dense kernels share `rigid_solve.glsl`.

Equal tangent stiffnesses allow exact algebraic simplification of the contact penalty Hessian. With `B = [I, −[r]×]` and normal Jacobian `Jn`, it is `(kn−kt) Jn Jnᵀ + kt BᵀB`, since `ttᵀ + bbᵀ = I−nnᵀ`. The blocks of `BᵀB` are `I`, `−[r]×`, and `|r|²I−rrᵀ`; they need no tangent Jacobian outer products. When `kt > kn`, the shader retains positive outer-product assembly to avoid subtractive cancellation in the weaker normal direction. Geometric stiffness remains evaluated for the original normal and tangent rows, and point displacement still uses exact rotation.

A body with `FixedRotation` (`Rigid.centerOfMass.w` = 1) accumulates only the translation force and six translation Hessian entries, then solves a scalar 3×3 LDLᵀ system. It skips contact torque, rotational geometric stiffness, world inertia and the 6×6 factorization. Its inverse inertia counts as zero in impulses (`apply_edits.comp`) and bounces (`restitution.comp`), and angular velocity edits leave it alone.

Rigid bodies receive up to eight colors through four GPU greedy refinement rounds. Each round reads one snapshot and writes the other, avoiding reads racing with color updates. A final pass reads the same snapshot for all bodies and moves the higher-indexed endpoint of every remaining same-color edge into color 8. Thus no ordinary color contains a contact edge: for any conflicting pair, its higher endpoint is absent from that color after pruning. Overflow bodies run in ascending order in a single 256-thread workgroup, with buffer memory visibility and a workgroup barrier between bodies. This preserves in-place Gauss-Seidel updates without concurrent neighbouring writers. The overflow path can serialize dense graphs; it is a correctness fallback, not a guarantee of constant solve cost. Continuous detection repeats conflict pruning before later iterations. Soft-body cross-body contact coloring is unchanged.

### Conservative advancement (`guess_limit.comp`)

The initial guess carries every body along its inertial path. On an impact that ends deep inside whatever it hits: a bullet crossing two metres per step starts a metre inside the wall, every contact begins badly violated, and the first iterations push with large, uneven forces.

After detection, each body's guess is therefore pulled back along its path to where its first contact closes (linearized), so iterations start feasible. A contact that closes over the step lowers, with an atomic minimum (0.32 fixed point), the fraction of each side that moves into it: to where the two meet if both approach, or to where the other side will be if it moves away. Then each block applies its body's fraction. A side moving away keeps its motion: a block pushed ahead of a walking character would otherwise be stopped with the character every step, and could never get away from it. A soft body is pulled back **as a whole**: stopping only the touching nodes would leave the rest to overtake them, an inverted guess the springs cannot undo. Violations below the margin, as in resting or sliding contact, are left alone so the guess keeps its tangential motion.

### Restitution (`restitution.comp`)

Implicit Euler contacts are perfectly inelastic, so bounce is added after the velocities are known. For each rigid body, the contacts that pushed this step and were approaching faster than `MinimumBounceSpeed` are averaged, **weighted by their normal force** (the center of pressure), into one impact point and normal. An impulse there makes up what the sides' parting speed lacks of restitution × approach speed. Averaging unweighted arms lets an uneven set of contacts turn the bounce into spin.

Against another rigid body a body makes up only its **share** of the missing speed: its inverse effective mass at the contact (1/m + (r × n)·I⁻¹(r × n)) over both sides'. Both bodies of a contact compute the same split, so the bounce acts as one impulse between them and keeps momentum: a heavy body hardly bounces off a light one. Treated as a wall, the other body made a 10 kg box bounce straight back off a 1 kg one. Fixed and soft sides take no share. The parting speed comes from the sides' displacements over the step (velocities are displacements over the step), which this pass does not change, so no body reads a velocity that another workgroup is writing.

## CPU side

- **`PhysicsSystem`**:
  - the public facade: body lists, edits queue, `Update` recording, fence wait, readback, statistics, GPU timestamps;
  - `Rebuild()` runs when bodies change. It restores rigid state from the completed per-step readback, downloads soft state (which may have CPU synchronization disabled), retains resident solver and collision allocations, growing only buffers whose capacity is insufficient, and refreshes their logical ranges and descriptors. Warm-start tables survive rebuilds at unchanged capacity: their keys are surface lifetime handles and body-local feature indices rather than dense global indices. Immutable collision buffers retain CPU upload shadows; cache-line comparisons skip unchanged prefixes and suffixes and upload the changed interval at its destination offset. Shader-written positions, bounds and refitted BVHs always receive fresh initialization from the bodies' CPU state and cooked surfaces. CPU geometry, rigid upload state and material arrays retain their capacity, while descriptors and uploaded spans use only live counts. All its uploads go through one staging arena in one submission (`GpuBufferAllocator.BeginBatch`).
- **`AvbdSolver`**: set 0; solver passes; outputs.
- **`GPUCollisionSystem`**: set 1; detection, lists and store passes.
- **`CollisionGeometry`**: builds the set 1 arrays on the CPU: welding, ownership, edge adjacency, BVHs.
- **`BvhBuilder`**: binned SAH BVH flattened depth-first (left child is the next node).
- **Bodies**: hold their initial and synced state and own their render buffers (created on `Add`, freed on `Remove`/`Dispose`). Setters on an added body queue edits through `PhysicsSystem`.

## Debugging and profiling

- `src/PhysicsTests`: headless scenarios with pass/fail ranges. Environment variables:
  - `MYT_VALIDATION=0`: no validation layer;
  - `MYT_ITERATIONS`, `MYT_CCD`: override settings;
  - `MYT_PROFILE`: some scenarios print GPU time per phase;
  - `MYT_TRACE`: some scenarios print per-step traces and dump contacts.
  - `MYT_BENCH_COUNTS=64,144,256`, `MYT_BENCH_STEPS=120`: workloads for `many-rigid`, which prints CPU/GPU time, percentiles and managed allocations with gameplay-sized contact-report buffers. `report-readback` checks empty updates, overflow, toggled reporting and multiple substeps.
  - `crowded-rigid`: touching cubes, default 414 objects, with support, resting-speed and contact-overflow checks. `MYT_BENCH_COUNT`, `MYT_BENCH_LAYERS` and `MYT_BENCH_STEPS` configure it. Stacks check support averaged over the measured interval, since instantaneous forces fluctuate with finite solver iterations.
- `PhysicsSystem.Profile`: timestamps each phase into the read-only `LastProfile` list. In the game, `MYT_PROF=1 MYT_PROFILE=1` prints a phase sample every 120 physics steps. `PhysicsSystem.DownloadContacts()` (internal) returns the last step's contacts, `DownloadPairCount()` the last broad phase's candidate pairs. `MYT.Kit` exposes internals to `PhysicsTests` only.
- `PhysicsStatistics.GpuMilliseconds` is always measured.

## Where to go next

- **Joints** (ball, hinge) between rigid bodies as AVBD hard constraints: a new constraint type with its own dual update, filed under both bodies' blocks like contacts.
- **Soft body self-collision**: the enumeration rule currently skips `q == p`. Allowing it needs to exclude adjacent primitives and a recoloring that considers contacts (AVBD §4).
- **Kinematic bodies**: rigid bodies with infinite mass and a scripted pose, for moving platforms.
- **Scene queries**: ray casts against the same BVHs.
- **Fewer dispatches**: each dispatch plus barrier costs a few microseconds, and a soft body adds 8 colors × iterations of them. Merging bodies' colors already happens; persistent-thread solves could go further.
- **Hierarchy quality during large motion**: the top-level body BVH is refit between geometry rebuilds. Rebuilding its topology on the GPU when bodies rearrange would maintain spatial pruning in scenes that run a long time without adding or removing bodies.
- **Contact reduction for dense meshes**: a soft box lying on the 327k-triangle sphere gets ~9000 contacts, hundreds per node, and the node solve dominates the step (~5 ms). Merging nearby contacts of one feature pair of bodies would cut that at the source.
- **Rebuild cost**: resident buffers, changed-range immutable uploads and persistent contact identities avoid allocation and global warm-start invalidation. CPU geometry still assembles densely; shader-written initial state and BVHs still upload in full. Stable geometry slots and separate static/dynamic hierarchies remain necessary to make work proportional to edited shapes.


Contact cache identities are four uints: `(lifetimeA, localFeatureA, lifetimeB, localFeatureB)`.
Vertex features retain the high-bit type tag; triangle and edge indices subtract each
body's uploaded first-feature offsets. Each body attachment receives a fresh lifetime
handle. Material or simulation-property changes invalidate that body's handle; unaffected
pairs retain theirs. Handles are monotonically allocated and never reused; exhaustion
throws rather than aliasing a previous lifetime. The cache hashes all four words and
compares all four on a hit, so hash collisions cannot identify a different feature pair.
`CacheEntry` is 80 bytes, including the original header, lambda, penalty and normal.
Changing contact-buffer capacity recreates and clears both tables. Ordinary topology edits
retain them; the next substep clears only its current table, as before.
