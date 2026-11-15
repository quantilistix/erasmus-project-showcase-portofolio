# MYT Physics

MYT simulates three kinds of bodies on the GPU:

- **Fixed bodies**: static geometry such as levels and terrain. They never move.
- **Rigid bodies**: objects that move and rotate but never bend, such as crates, rocks and props.
- **Soft bodies**: point masses joined by springs, such as jelly, cushions and ropes.

Every pair of bodies collides (except two fixed bodies). The solver is **Augmented Vertex Block Descent** (AVBD), which keeps stacks stable and contacts hard with few iterations. Collision detection is both **discrete** (DCD: what is touching or about to touch at the start of a step) and **continuous** (CCD: what the motion during the step would sweep through). Fast bodies therefore don't tunnel through thin walls.

The engine does no rendering. It hands you GPU buffers (a model matrix per rigid body, a vertex and index buffer per soft body) that you draw however you like.

This guide covers everything needed to use it. [PhysicsInternals.md](PhysicsInternals.md) explains how it works inside.

---

## Contents

1. [Requirements](#requirements)
2. [Quick start](#quick-start)
3. [Units and conventions](#units-and-conventions)
4. [Fixed bodies](#fixed-bodies)
5. [Rigid bodies](#rigid-bodies)
6. [Soft bodies](#soft-bodies)
7. [Running the simulation](#running-the-simulation)
8. [Moving and pushing bodies](#moving-and-pushing-bodies)
9. [Rendering](#rendering)
10. [Materials: friction and bounce](#materials-friction-and-bounce)
11. [Contact reports](#contact-reports)
12. [Settings reference](#settings-reference)
13. [Statistics](#statistics)
14. [Troubleshooting](#troubleshooting)
15. [Limitations](#limitations)
16. [Tests](#tests)

---

## Requirements

- A Vulkan device with the **`shaderFloat64`** feature **enabled** when you create the logical device. Positions are stored in double precision so large worlds stay accurate; desktop NVIDIA, AMD and Intel Arc GPUs support it.

  ```csharp
  PhysicalDeviceFeatures features = new() { ShaderFloat64 = true };
  ```

- A **queue with compute and transfer support**, normally the one you render with (see [Rendering](#rendering)), and a **command pool** for that queue's family, created with `CommandPoolCreateFlags.ResetCommandBufferBit`.
- The **physics shaders**, in `assets/shaders/physics`. They are compiled when the `PhysicsSystem` is created, with the same shaderc library the rest of MYT uses.

## Quick start

```csharp
using GlmSharp;
using MYT.Kit.Vulkan;

// Once, after creating your Vulkan device.
PhysicsSystem physics = new(vk, device, physicalDevice, commandPool, graphicsQueue, "assets/shaders/physics");

// A level that never moves.
Mesh3D levelMesh = Mesh3D.Loader.Load("assets/3d/testMap.ply");
physics.Add(new FixedBody(levelMesh));

// A 1 kg crate dropped from 5 m, slightly tilted.
Mesh3D crateMesh = Mesh3D.Loader.Load("assets/3d/cube.ply");
RigidBody crate = new(crateMesh, mass: 1.0)
{
    Position = new dvec3(0, 0, 5),
    Orientation = dquat.FromAxisAngle(0.5, new dvec3(1, 0, 0))
};
physics.Add(crate);

// A wobbly 2 m jelly cube.
SoftBody jelly = SoftBody.CreateLatticeBox
(
    center: new dvec3(3, 0, 6),
    size: new dvec3(2, 2, 2),
    resolution: new ivec3(6, 6, 6),
    totalMass: 1.0,
    stiffness: 100.0,
    damping: 0.005
);
physics.Add(jelly);

// Every frame.
physics.Update(1.0 / 60.0);
Console.WriteLine($"The crate is at {crate.Position}");
// ...then draw crate.TransformBuffer and jelly.VertexBuffer / IndexBuffer (see Rendering).

// At shutdown.
physics.Dispose();
```

`src/Tester` is a complete windowed example: `Game.cs` builds the scene and `RigidBodyPipeline.cs` draws it.

## Units and conventions

| Quantity | Unit |
|---|---|
| Length | metres |
| Mass | kilograms |
| Time | seconds |
| Force | newtons (impulses in newton-seconds) |
| Angles | radians |

- There is no fixed "up". Gravity defaults to `(0, 0, -9.81)` (z up); set `physics.Settings.Gravity` to `(0, -9.81, 0)` for a y-up world.
- Meshes are triangle lists (`Mesh3D.Faces` holds three indices per triangle). **Triangles must be wound counter-clockwise when seen from outside** the body, so that their normals point outward. A surface only collides on the side its normal points to. Most exporters, Blender included, already do this.

## Fixed bodies

```csharp
FixedBody floor = new(floorMesh);
FixedBody ramp = new(rampMesh, transform: dmat4.Translate(10, 0, 0) * dquat.FromAxisAngle(0.3, dvec3.UnitY).ToMat4);
physics.Add(floor);
physics.Add(ramp);
```

- The mesh may be open (a single ground plane is fine) and as detailed as you like: fixed geometry sits in a bounding volume hierarchy, so a 100,000-triangle level costs little more than a small one.
- Fixed bodies cannot move. To move one, `Remove` it and `Add` a new one (see [Adding and removing bodies](#adding-and-removing-bodies)).
- Walls should be thicker than `Settings.PenetrationTolerance` (1 mm by default).
- A level split into pieces, such as a terrain in chunks, should set `OpenEdgesContinue = true` on each piece. Its open edges are then seams inside one surface rather than the rim of a sheet, and bodies sliding across them do not catch on them:

  ```csharp
  FixedBody chunk = new(chunkMesh) { OpenEdgesContinue = true };
  ```

## Rigid bodies

```csharp
RigidBody rock = new(rockMesh, mass: 25.0)
{
    Position = new dvec3(0, 0, 10),       // where the mesh origin goes
    Orientation = dquat.Identity,
    LinearVelocity = new dvec3(2, 0, 0),  // m/s, of the center of mass
    AngularVelocity = dvec3.Zero,         // rad/s, world axes
    Friction = 0.6,
    Restitution = 0.2
};
physics.Add(rock);
```

- The mesh must be **closed**. Mass is spread uniformly through its volume, and the center of mass (`CenterOfMass`, in mesh space) and inertia (`LocalInertia`) are computed from it. The constructor throws if the mesh is open.
- A body whose density is not uniform, such as one built from parts of different materials, takes its mass properties directly; its mesh is then used only for collision:

  ```csharp
  RigidBody mixed = new(mesh, mass: 1200, centerOfMass: new dvec3(0.4, 0, 0.5), inertia: myInertiaTensor);
  ```
- `Position` is the world position of the **mesh origin**, not of the center of mass; `WorldCenterOfMass` gives the latter.
- `FixedRotation = true` makes a body that only translates, such as a character's upright collision box: contacts and impulses push it but never turn it, and its angular velocity stays zero. Setting it on an added body rebuilds the simulation on the next update.
- `GravityScale` (default 1) multiplies the gravity the body feels, so a character can fall more briskly than the objects around it; it rests on what holds it up with its weight scaled alike. The solver integrates it as gravity, so a resting body starts each step where gravity takes it rather than inside the ground, which an impulse added every step to the same effect does not do: such a body hops off the ground by a hair every few steps. Setting it on an added body rebuilds the simulation on the next update.
- Bodies made from the same `Mesh3D` share one bounding volume hierarchy, so many copies of a detailed mesh stay cheap to build.
- Changing `Mass` after adding the body works, but rebuilds the simulation on the next update.

## Soft bodies

A soft body is a set of **nodes** (point masses) joined by **springs**. Its **surface triangles** are what it collides with and what you render. Nodes that no surface triangle uses are interior: they hold the shape but do not collide.

### Ready-made shapes

```csharp
// A solid box: nodes on a grid, springs to all 26 neighbours so it resists stretching and shearing.
SoftBody cushion = SoftBody.CreateLatticeBox
(
    center: new dvec3(0, 0, 2),
    size: new dvec3(1, 1, 0.3),
    resolution: new ivec3(6, 6, 3),   // nodes per axis, at least 2
    totalMass: 0.5,
    stiffness: 400,                   // N/m per spring
    damping: 0.005                    // seconds; 0.001 - 0.01 is typical
);

// Any closed, roughly convex mesh: springs along its edges, across neighbouring triangles,
// and to a hidden node at its center.
SoftBody ball = SoftBody.CreateFromMesh(sphereMesh, totalMass: 1.0, stiffness: 300, damping: 0.005);
```

**Choosing stiffness.** Each spring has a stiffness in N/m, so the right value depends on how many springs share the load. Start around `100 * totalMass * resolution` for a lattice box and adjust by eye. More nodes or higher stiffness make a firmer body. Too soft a body can fold inside out under a hard impact.

### Building your own

```csharp
dvec3[] positions = ...;        // initial node positions
double[] masses = ...;          // kg per node; double.PositiveInfinity pins a node in place
Spring[] springs = ...;         // see below
uvec3[] surface = ...;          // outward-wound triangles over the nodes, may be empty
SoftBody custom = new(positions, masses, springs, surface);
```

```csharp
Spring spring = new()
{
    Node1 = 0,
    Node2 = 1,
    RestLength = 0.1,                       // m
    Stiffness = 500,                        // N/m, or double.PositiveInfinity for a rigid link
    Damping = 0.005,                        // s
    Durability = double.MaxValue            // N; the spring breaks for good above this force
};
```

- **Hard springs.** `Stiffness = double.PositiveInfinity` turns a spring into an exact distance constraint. AVBD solves these without the instability infinite stiffness would normally cause. Use them for chains, ropes and inextensible cloth.
- **Breakable springs.** A finite `Durability` lets the body tear when a spring is pulled or pushed harder than that.
- **Pinning.** `Pin(node)`, or a node mass of `double.PositiveInfinity`, fixes a node in space. A pinned node still pushes other bodies away. This is how you hang a rope from the ceiling:

```csharp
SoftBody rope = new(positions, masses, links, []);   // no surface: it does not collide
rope.Pin(0);
physics.Add(rope);
```

## Running the simulation

```csharp
physics.Update(deltaTime);
```

`Update` advances the world by `deltaTime` seconds and returns once the GPU is done. After it returns:

- every body's properties (`RigidBody.Position`, `SoftBody.Positions`, ...) hold the new state;
- every render buffer has been rewritten.

**Use a constant time step.** The simulation behaves most consistently when every step is the same length. A common pattern:

```csharp
const double Step = 1.0 / 60.0;
accumulator += frameTime;
while (accumulator >= Step)
{
    physics.Update(Step);
    accumulator -= Step;
}
```

To make one `Update` more accurate, use `physics.Settings.Substeps` (more, shorter steps) or `physics.Settings.Iterations` (more solver passes per step). See [Settings reference](#settings-reference).

### Adding and removing bodies

`Add` and `Remove` can be called at any time between updates. The next `Update` then rebuilds the GPU data. That costs a few milliseconds to tens of milliseconds depending on the scene, so batch your changes: add twenty crates, then update once. Bodies already simulated carry on exactly where they were. A large level that changes often (a voxel world) is best split into several fixed bodies, so an edit replaces only the piece it touches: each body's collision data is prepared once and reused by later rebuilds.

`Remove` waits for the GPU to be idle, then frees the body's render buffers, so stop drawing it first. A removed body keeps its last state and can be added again, to this or another `PhysicsSystem`.

## Moving and pushing bodies

Setting a property of a body that is in a system takes effect at the start of the next `Update`:

```csharp
crate.Position = new dvec3(0, 0, 10);             // teleport; it keeps its velocity
crate.Orientation = dquat.Identity;
crate.LinearVelocity = new dvec3(0, 0, 5);        // launch it upward
crate.AngularVelocity = new dvec3(0, 0, 3);       // spin it
crate.ApplyImpulse(new dvec3(10, 0, 0));          // N·s through the center of mass
crate.ApplyImpulse(new dvec3(10, 0, 0), crate.WorldCenterOfMass + new dvec3(0, 0, 0.5));  // off-center: also spins it

jelly.SetVelocity(new dvec3(0, 0, 5));            // every free node
jelly.ApplyImpulse(new dvec3(0, 20, 0));          // spread over the body by mass
jelly.Translate(new dvec3(0, 0, 3));              // move without adding velocity
```

Several changes before one update are applied in the order you made them.

### Reading state

After each `Update`, `RigidBody.Position`, `Orientation`, `LinearVelocity`, `AngularVelocity` and `WorldCenterOfMass` hold the simulated state. So do `SoftBody.Positions`, `Velocities` and `Center`.

Copying soft body nodes back costs bandwidth. For a large soft body you only draw, set `jelly.SyncToCpu = false`; its `Positions` then stop updating, but its render buffers still do. Rigid bodies always sync, which costs little.

## Rendering

The engine writes render data on the GPU and leaves drawing to you.

| Body | Buffer | Contents | Valid |
|---|---|---|---|
| `RigidBody` | `TransformBuffer` | one `mat4` (float, column-major): mesh space to world space | from `Add` until `Remove` |
| `SoftBody` | `VertexBuffer` | one vertex per node: float3 position, float3 normal (24 bytes), in world space | from `Add` until `Remove` |
| `SoftBody` | `IndexBuffer`, `IndexCount` | the surface triangles as uint32 indices | from `Add` until `Remove` |

Rigid body: draw the body's own mesh (`rigid.Geometry`) with the model matrix from `TransformBuffer`, bound as a uniform or storage buffer:

```glsl
layout(set = 0, binding = 1) readonly buffer Transform { mat4 model; };
...
gl_Position = projection * view * model * vec4(inPosition, 1.0);
```

Soft body: bind `VertexBuffer` (stride 24: position at offset 0, normal at offset 12) and `IndexBuffer` (`IndexType.Uint32`), then `vkCmdDrawIndexed(cmd, softBody.IndexCount, 1, 0, 0, 0)` with an identity model matrix. Normals are smooth, averaged over the triangles around each vertex.

**Synchronization.** Submit your draws **on the same queue you gave the `PhysicsSystem`**, after `Update` returns. `Update` puts a barrier between its writes and later vertex and shader reads on that queue, and waits for earlier work on the queue (such as last frame's draws) before overwriting anything. If you render on a different queue, synchronize yourself, for example with `vkQueueWaitIdle` on the render queue before calling `Update`.

## Materials: friction and bounce

Every body has two material properties, which you can change at any time and which take effect on the next update:

- `Friction`: Coulomb friction coefficient, default 0.5. A contact uses the **geometric mean** of both sides' values, so a 0-friction body slides on everything. As a guide, 0.1 is ice, 0.5 wood on wood, 1.0 rubber.
- `Restitution`: bounciness from 0 to 1, default 0. A contact mixes both sides' values as `Settings.RestitutionMixing` says: by default the **larger** (`Maximum`, a bouncy body bounces off anything), or the **geometric mean** (`GeometricMean`: two surfaces of one material bounce with its restitution, and a surface of restitution 0, such as soil, stops any bounce). Only rigid bodies bounce, and impacts slower than `Settings.MinimumBounceSpeed` (0.5 m/s) never bounce, so resting objects stay at rest. Between two rigid bodies the bounce is shared by their masses, so momentum is kept and a heavy body hardly bounces off a light one. Soft bodies get their bounce from their springs.

A body made of several materials, such as a level or a voxel object, gives each triangle of its collision mesh its own with `SurfaceMaterials`, one `SurfaceMaterial(Friction, Restitution)` per triangle in mesh order (a soft body's: per `SurfaceTriangles`), which take the place of `Friction` and `Restitution` there. A vertex or edge touches with the material of the first triangle that uses it. Setting the list rebuilds the simulation on the next update; set a new list to change it.

```csharp
SurfaceMaterial[] surfaces = new SurfaceMaterial[mesh.Faces.Length / 3];
for (int t = 0; t < surfaces.Length; t++)
    surfaces[t] = IsIce(t) ? new SurfaceMaterial(0.05, 0.1) : new SurfaceMaterial(0.8, 0.0);
physics.Add(new FixedBody(mesh) { SurfaceMaterials = surfaces });
```

## Contact reports

With `physics.Settings.ReportContacts = true`, every update lists the contacts that pushed in `physics.ContactReports`, one entry per contact per substep. Gameplay uses them for impact damage, sounds, or the loads a body carries:

```csharp
physics.Settings.ReportContacts = true;
physics.Update(step);
foreach (ContactReport contact in physics.ContactReports)
{
    dvec3 forceOnA = (contact.NormalImpulse * contact.Normal + contact.FrictionImpulse) / contact.Duration;
    if (contact.ApproachSpeed > 5.0)
        Console.WriteLine($"{contact.BodyA} hit {contact.BodyB} at {contact.Point}");
}
```

| Field | Meaning |
|---|---|
| `BodyA`, `BodyB` | The two bodies. |
| `Point` | World point of contact at the start of the substep, on the surface of a side that moves. |
| `Normal` | Unit normal from `BodyB` to `BodyA`. |
| `NormalImpulse` | Impulse pushing `BodyA` along the normal during the substep, N·s; `BodyB` gets the opposite. |
| `FrictionImpulse` | Friction impulse on `BodyA`, N·s. |
| `ApproachSpeed` | How fast the sides were closing along the normal when the substep began, m/s; large in the substep of an impact, about 0 at rest. |
| `Duration` | The substep's length, s. |

At rest the impulses of a body's contacts add up to its weight times the step. Reports beyond `Settings.MaxContactReports` (16384 by default) are dropped and counted in `Statistics.DroppedContactReports`. Reporting costs one extra pass per substep and a copy back of the reports.

## Settings reference

All settings live in `physics.Settings` and take effect on the next `Update`, except `MaxContacts` and `MaxContactReports`, which take effect after the next add or remove.

| Setting | Default | What it does |
|---|---|---|
| `Gravity` | `(0, 0, -9.81)` | Acceleration applied to everything, m/s². |
| `Iterations` | 10 | Solver passes per step. More gives firmer contacts and less stretchy soft bodies. AVBD stays stable with as few as 2-4. |
| `Substeps` | 1 | Splits each `Update` into this many shorter steps. The best fix for fast motion, very stiff soft bodies and tall stacks; costs proportionally more. |
| `ContinuousCollisionPasses` | 1 | Sweeps for missed collisions during each step. Raise it for very fast projectiles; 0 turns CCD off. |
| `ContactMargin` | 0.01 | Contacts are created this many metres before bodies actually touch (plus how far they move in the step). |
| `PenetrationTolerance` | 0.001 | A vertex this far behind a surface still counts as in front of it. Keep it below the thinnest wall. |
| `MaxPenetrationDepth` | 0.25 | Deepest an existing contact keeps pushing out from, in m. Stops a body that went through something from snagging on the far side. |
| `MinimumBounceSpeed` | 0.5 | Impacts slower than this (m/s) never bounce. |
| `Alpha` | 0.95 | Of an overlap already present when a step starts, the fraction left for later steps: 1 − α of it is resolved per step, gently, rather than with a violent push-out (AVBD α). |
| `Beta` | 1e5 | How fast constraint stiffness ramps up while a constraint is violated (AVBD β). |
| `Gamma` | 0.99 | How much of last step's contact forces and stiffness the next step starts from (warm starting, AVBD γ). |
| `StartStiffness` | 1e4 | Lowest stiffness a new contact or hard spring starts with, N/m. New contacts also start at least as stiff as their heavier body's mass / step². |
| `MaxStiffness` | 1e10 | Upper bound on any stiffness, for numerical safety. |
| `MaxForce` | 1e12 | Upper bound on any constraint force, for numerical safety. |
| `MaxContacts` | 32768 | Most contacts in one step. Watch `Statistics.DroppedContacts`. |
| `ReportContacts` | false | Lists the contacts of each update in `ContactReports` (see [Contact reports](#contact-reports)). |
| `MaxContactReports` | 16384 | Most contact reports one update keeps. Read when the system rebuilds, like `MaxContacts`. |

You rarely need to touch anything below `MinimumBounceSpeed`.

## Statistics

`physics.Statistics` describes the last `Update`:

- `ContactCount`: contacts in the last substep.
- `DroppedContacts`: contacts, or candidate triangle pairs that could have made some, lost because `MaxContacts` was reached. Raise `MaxContacts` if this is not 0.
- `DroppedContactReferences`: internal; should always be 0.
- `DroppedContactReports`: contacts left out of `ContactReports` because `MaxContactReports` was reached.
- `GpuMilliseconds`: how long the GPU spent on the update. 0 if the device cannot time compute work.
- `CandidatePairCount`: candidate triangle pairs tested across detection passes in the last substep.
- `DynamicTriangleCount`: moving collision triangles processed by each detection pass.
- `WarmStartedContactCount`: contacts restored from the previous substep through stable surface lifetime and local-feature identities, including across unrelated topology changes.
- `SerialRigidBodyCount`: rigid bodies assigned to the ordered serial overflow queue after color conflict removal, in the last substep.

`physics.Timings` separates CPU wall time into rebuilding, preparation, command recording,
submission, fence waiting and readback, plus the whole update. Fence waiting includes GPU
execution and earlier queued work, so it must not be added to GPU time as another cost.
Empty updates clear the previous statistics.

## Troubleshooting

**Something falls through the floor or a wall**

- Check the winding: the surface must face the body hitting it. Flip the triangle order if needed.
- A fixed mesh only collides on its front side. A floor seen from below is not solid.
- Walls thinner than `PenetrationTolerance` cannot be resolved; make them thicker.
- For extremely fast bodies, raise `ContinuousCollisionPasses` or `Substeps`.

**A soft body folds inside out or passes through things**

- Only its surface nodes collide. If its springs are too soft, interior nodes can overtake the surface on a hard impact. Raise `stiffness` or the resolution, use `Substeps = 2-4`, or make key springs hard (`double.PositiveInfinity`).

**A soft body is too stretchy, or sags**

- Raise `Iterations` or `Substeps`, or use hard springs where the shape must hold.

**Stacks wobble or creep**

- Raise `Iterations` (10-20) or `Substeps`. Very different masses in one stack (a 100 kg crate on a 0.1 kg one) need more.

**A body bounces less than its restitution suggests**

- The bounce uses the speed at the start of the impact step, so large steps under-bounce slightly. Use smaller steps or substeps.

**`Update` is slow**

- `Statistics.GpuMilliseconds` shows the GPU cost. It grows with the number of soft body nodes × iterations × substeps, with the number of surface triangles of moving bodies, and with the number of contacts.
- Contacts are made per vertex and per edge, so a dense mesh resting on something makes thousands of them. A 327k-triangle sphere with a soft box on it steps in about 4 ms on average and 12 ms at worst on an RTX 3060. A coarser mesh for physics, drawn with the dense one at the body's transform, is much cheaper.
- Adding or removing a body rebuilds the GPU buffers. What depends only on a body's shape (welding, edges, its BVH) is computed the first time the body is added and kept, and every fixed body has its own BVH, so a rebuild where most bodies are unchanged costs about 20 ms for a 64×64 voxel level split into 18 fixed bodies. A body seen for the first time is welded and gets its BVH then; a 327k-triangle mesh takes about a second.
- Set `SyncToCpu = false` on large soft bodies you only draw.
- Consumer GPUs run double precision slowly. The engine uses it only for absolute positions, but it still adds up.

## Limitations

- **No self-collision for soft bodies.** A soft body never collides with itself; folding cloth onto itself would pass through.
- **Only surface nodes of soft bodies collide.** Interior nodes are held in place by springs alone.
- **No joints between rigid bodies yet** (hinges, ball sockets). Hard springs between soft nodes cover ropes and chains.
- **Fixed bodies cannot move**, and there are no kinematic (animated, infinite-mass moving) bodies yet.
- **No scene queries** (ray casts, overlap tests) yet.
- **Bounces between two moving rigid bodies are approximate**: each body bounces as if the other were static.
- **Rigid bodies have uniform density** unless their mass properties are given explicitly.
- **Adding or removing bodies rebuilds the GPU data** on the next update.
- Every body's relative quantities use float precision. Absolute positions are double, so worlds tens of kilometres wide are fine.

## Tests

`src/PhysicsTests` runs the engine headless (no window) through 36 scenarios: resting contact, stacks, edge-on-edge contact, fast projectiles against thin walls, friction on slopes, friction and bounce that differ between the triangles of one body, a bounce between a heavy and a light box that keeps momentum, soft bodies on ridges, ropes, a 64-box pile, the test map, Blender's cone, a 327k-triangle sphere with a soft box on it, which also checks the step stays real-time, contact reports on a box given explicit mass properties, an upright box with fixed rotation that is hit and pushed without tipping, and a box under four times the world's gravity that falls four times as far and rests as still. Scaling, candidate overflow, statistics lifecycle, work-list completeness and motion across topology changes are also checked. Each prints its measurements against pass/fail ranges:

```sh
dotnet run --project src/PhysicsTests                      # all scenarios
dotnet run --project src/PhysicsTests -- soft-ridge pile   # only some
MYT_VALIDATION=0 dotnet run --project src/PhysicsTests     # without the Vulkan validation layer (faster)
```

Run them after changing the engine or its shaders.
