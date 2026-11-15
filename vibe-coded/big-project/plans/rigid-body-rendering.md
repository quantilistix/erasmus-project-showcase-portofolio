# Plan: Rigid Body Physics Visible in Rendering

## Context

The scene has a cube (`boxBody`) that should fall under gravity and a static map (`mapBody`). Currently the cube is rendered at the origin with `mat4.Identity` and never moves. The rigid body's transform mat4 lives on the GPU so the vertex shader can read it as an SSBO — no CPU round-trip needed. Collision with the map is out of scope for now; the cube just needs to fall visibly.

## Root Causes

1. `boxBody.UploadToGPU()` is never called (Game.cs:60 calls `mapBody.UploadToGPU` a second time)
2. `RigidBody.UploadToGPU` creates a staging buffer but never issues `vkCmdCopyBuffer` to device-local
3. No rigid body data buffer on GPU (position/velocity/mass)
4. `PhysicsSystem.Update()` only dispatches soft body compute — no rigid body integration
5. No compute shader for rigid body integration
6. The cube renders with the phong material at identity — no rigid body pipeline is wired up

## Architecture Change: Drop Materials, Add ShaderLoader

The `MaterialLoader`, `PhongMaterial`, and `RigidBodyMaterial` concepts are being retired. Instead:

- **ShaderLoader** — new utility class that compiles GLSL source to `ShaderModule`. Lives in `MYT.Kit/Vulkan/`.
- Pipeline creation, descriptor sets, and UBOs are managed directly where needed (in `Game.cs`/`Render.cs` for now).
- The existing phong pipeline stays functional during this change (refactor later) but the rigid body pipeline is built directly using `ShaderLoader` + inline Vulkan calls.

### Future plan (not this change)
- Image loader (textures)
- Pipeline loader (data-driven `pipeline.json` parsing to `VkPipeline`)
- Descriptor set layout loader
- Remove PhongMaterial/RigidBodyMaterial files entirely

## Implementation Steps

### 1. Create `ShaderLoader` utility

**New file**: `src/MYT.Kit/Vulkan/ShaderLoader.cs`

Simple class wrapping Shaderc:
- `ShaderModule LoadVertex(Vk vk, Device device, string glslPath)`
- `ShaderModule LoadFragment(Vk vk, Device device, string glslPath)`
- `ShaderModule LoadCompute(Vk vk, Device device, string glslPath)`
- Internally reads the file, compiles to SPIRV via `ShadercCompiler`, creates `VkShaderModule`
- Implements `IDisposable`, tracks created modules for cleanup

### 2. Create rigid body integration compute shader

**New file**: `assets/shaders/physics/rigid_body_integrate.comp`

Single-invocation compute shader per rigid body:
- Set 0: global physics params UBO (gravity, deltaTime) — existing layout
- Set 1, binding 0: rigid body state SSBO (uses `MassPoint` struct — position, velocity, force, mass, 128 bytes)
- Set 1, binding 1: transform mat4 SSBO (64 bytes output)

Logic:
```
velocity += dvec3(gravity) * double(deltaTime)
position += velocity * double(deltaTime)
write mat4 translation to transform buffer
```

### 3. Fix `RigidBody.UploadToGPU`

**File**: `src/MYT.Kit/Vulkan/RigidBody.cs`

- Create `_dataBuffer` (device-local, 128 bytes) holding the `MassPoint` struct
- Upload via staging + `vkCmdCopyBuffer` using one-shot command pattern
- Fix transform buffer: actually issue `vkCmdCopyBuffer` from staging to device-local (currently skipped)
- Expose `DataBuffer` property

### 4. Add rigid body support to `PhysicsSystem`

**File**: `src/MYT.Kit/Vulkan/Physics.cs`

- Add `_rigidBodyDescriptorSetLayout` (2 storage buffer bindings: state + transform)
- Load `rigid_body_integrate.comp` pipeline with layouts `[_globalDescriptorSetLayout, _rigidBodyDescriptorSetLayout]`
- Add `Dictionary<RigidBody, DescriptorSet[]> _rigidBodyDescriptorSets`
- Add `RegisterRigidBody(RigidBody body)` — allocates descriptor sets, binds `body.DataBuffer` and `body.TransformBuffer`
- Add rigid body dispatch in `Update()` — after soft body passes, bind rigid body pipeline, dispatch 1 workgroup per body

### 5. Build rigid body graphics pipeline directly

**File**: `src/Tester/Game.cs` and `src/Tester/Render.cs`

Instead of going through a material loader, create the rigid body graphics pipeline inline:
- Use `ShaderLoader` to compile `assets/materials/rigid_body/vertex.glsl` and `fragment.glsl`
- Create descriptor set layout matching the shader: binding 0 = camera UBO, binding 1 = transform SSBO, binding 2 = lighting UBO
- Create pipeline layout and graphics pipeline (same config as `pipeline.json` but built directly)
- Create per-frame camera UBO (128 bytes, persistently mapped) and lighting UBO (64 bytes, persistently mapped)
- For each rigid body: allocate a descriptor set binding camera + transform + lighting
- Store all this in static fields on `Program`

### 6. Wire up init and rendering

**File**: `src/Tester/Game.cs`

- Fix line 60: call `boxBody.UploadToGPU(...)`
- Set initial position: `boxBody.MassPoint.Position1 = new dvec3(0, 0, 5)` (above the map)
- Set mass: `boxBody.MassPoint.Mass = 1.0f`
- Call `physicsSystem.RegisterRigidBody(boxBody)` after physics init
- Create rigid body graphics pipeline + resources

**File**: `src/Tester/Render.cs`

- In `UpdateUniforms`: write camera (view + projection) and lighting to the rigid body UBOs
- In `RecordCommandBuffer`: after drawing phong objects, bind rigid body pipeline, bind descriptor set for box's transform, draw `cubeMesh`

### 7. Synchronization

`PhysicsSystem.Update()` calls `QueueWaitIdle` after compute submission. This guarantees the transform buffer is written before the graphics pass reads it. No additional barrier needed.

## Files Modified

| File | Change |
|------|--------|
| `src/MYT.Kit/Vulkan/ShaderLoader.cs` | **New** — GLSL-to-ShaderModule utility |
| `assets/shaders/physics/rigid_body_integrate.comp` | **New** — rigid body compute shader |
| `src/MYT.Kit/Vulkan/RigidBody.cs` | Fix UploadToGPU, add data buffer + copy |
| `src/MYT.Kit/Vulkan/Physics.cs` | Rigid body descriptor set, pipeline, registration, dispatch |
| `src/Tester/Game.cs` | Fix boxBody init, create rigid body graphics pipeline, register |
| `src/Tester/Render.cs` | Render cube with rigid body pipeline |

## Verification

Run the application. The cube should appear above the map and fall downward under gravity (Z-down in the physics, Z-up in the coordinate system). It will fall through the map and eventually disappear below (no collision yet). The map and soft body sphere should remain visible and functional.
