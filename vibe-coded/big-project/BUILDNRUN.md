# Build and run: big-project

## Install the SDK

Install the **.NET 10 SDK**, matching your OS/architecture, from
[Microsoft's Windows, macOS and Linux instructions](https://learn.microsoft.com/en-us/dotnet/core/install/).
A runtime alone cannot build these `net10.0` projects. No Visual Studio is required.

- **Windows:** use the .NET 10 SDK installer, or
  `winget install --id Microsoft.DotNet.SDK.10 --exact` in PowerShell.
- **macOS:** use Microsoft's .NET 10 SDK installer for Apple Silicon or Intel.
- **Linux:** follow Microsoft's page for your exact distribution/version; enable
  its documented feed if necessary, then install `dotnet-sdk-10.0` with apt/dnf.
  Do not assume the default distribution feed contains it.

Reopen the terminal and run `dotnet --list-sdks`. It must list a stable `10.0.*` SDK.
`global.json` selects the installed .NET 10 SDK and allows newer 10.0 feature bands.
NuGet restore requires internet on first build. Existing GlmSharp compatibility and
nullable warnings are inherited from the source/dependencies and have not been suppressed.

## Build

Open a terminal in this `big-project` directory:

```sh
dotnet restore Showcase.slnx --disable-parallel
dotnet build Showcase.slnx -c Release --no-restore -m:1
```

`Showcase.slnx` includes all 12 `.csproj` files actually present, including the checks
project. The old `Tester.sln` references a missing `src/StoryMovie/StoryMovie.csproj`;
use the new solution. Movie artifacts are not a buildable program in this checkout.
Each project's references and packages remain unchanged. Outputs stay in each
project's `bin/Release/net10.0/`. This is a source-tree build: keep `assets/` and
embedded shader resources in place, and do not move built executables outside the tree.
A successful managed build does not verify native libraries or GPU features.

With the optional [Python launcher](../../BUILDNRUN.md#start-here), from here:

```sh
python3 ../../tools/buildnrun.py check vibe-coded/big-project
python3 ../../tools/buildnrun.py build vibe-coded/big-project
python3 ../../tools/buildnrun.py build vibe-coded/big-project --app VoxelGame
python3 ../../tools/buildnrun.py run vibe-coded/big-project --app BrainEditor
```

On Windows replace `python3` with `py -3`. Run defaults to `Tester`; `--app` selects
any actual executable project. Libraries can be built with `--app`, but cannot be run.
Arguments go after `--args` as the last launcher option.

## Native runtime prerequisites

These are separate from the .NET SDK and the NuGet packages:

- A Vulkan loader and GPU driver providing the required API/features. `Tester`
  requests Vulkan 1.3. `VoxelGame` requires Vulkan 1.3, double precision shaders
  (`shaderFloat64`) and a queue supporting graphics, compute and presentation;
  it also enables other device features in `GraphicsContext.cs`. Not all GPUs qualify.
- **GLFW 3** for graphical windows.
- The **shaderc shared library** for runtime GLSL compilation in physics/voxel rendering:
  `shaderc_shared.dll`, `libshaderc_shared.so` or `libshaderc_shared.dylib`.
  Merely having `glslc` or `glslangValidator` on PATH is insufficient.
- **Khronos validation layers** for `Tester`, which enables them unconditionally.
  Other applications/tests may also request them. Use a Vulkan SDK installation
  and its documented environment setup to make layer manifests visible.
- **OpenAL** for ArcadeLab audio. Use `--mute` for a headless experiment that does
  not need sound. **FFmpeg** is optional, only for workflows that explicitly export movies.

### Ubuntu / Debian

```sh
sudo apt update
sudo apt install libvulkan1 vulkan-tools vulkan-validationlayers libglfw3 libopenal1 libshaderc-dev
vulkaninfo --summary
```

Install the matching graphics driver (for example Mesa's Vulkan driver package for
supported Intel/AMD GPUs, or NVIDIA's vendor driver). Package names/availability can
vary by release. Check that `libshaderc_shared.so` is actually installed: a shaderc
static archive or `libshaderc_combined.a` is not enough. The `-dev` package commonly
supplies the unversioned library name imported by this code.

### Fedora

```sh
sudo dnf install vulkan-loader vulkan-tools vulkan-validation-layers glfw openal-soft libshaderc-devel
vulkaninfo --summary
```

### Windows

Install the GPU vendor's Vulkan-capable driver and the
[LunarG Vulkan SDK](https://vulkan.lunarg.com/sdk/home). Reopen the terminal and run
`vulkaninfo --summary`. The SDK's `Bin` directory must be available in the native
DLL search path, with `shaderc_shared.dll` from a matching architecture/build.
If your SDK distribution does not include that shared library, obtain/build shaderc
as described by [the shaderc project](https://github.com/google/shaderc); a compiler
executable is not a substitute. Install/download the matching
[GLFW Windows binaries](https://www.glfw.org/download.html) (`glfw3.dll`) and
[OpenAL Soft](https://openal-soft.org/) (`OpenAL32.dll`).

Place matching native DLLs beside the relevant application's built `.dll` in
`src/<App>/bin/Release/net10.0/` (or the tests project's equivalent), or configure
the native library search path. Include any dependent DLLs as supplied by those
libraries. NuGet restore alone does not install graphics drivers, shaderc or every
system library. Avoid mixing x64, x86 and ARM64 files.

### macOS

Install the .NET SDK for your CPU and the macOS Vulkan SDK/MoltenVK using LunarG's
instructions. In a terminal source the SDK's `setup-env.sh` at its actual installed
path before launching, so its loader and ICD manifests are available. Install GLFW
and shaderc through your package manager or their official build instructions;
with Homebrew already installed, `brew install glfw shaderc` is a starting point.
Verify that shared libraries (not just command-line tools) are discoverable by the
.NET process. For example, `DYLD_LIBRARY_PATH` can include your GLFW/shaderc library
directories in the launch terminal. ArcadeLab uses the system OpenAL framework.

**macOS execution is not guaranteed:** Vulkan translation does not implement every
feature this code requests. In particular Apple GPUs/MoltenVK should not be assumed
to supply `VoxelGame`'s required double precision shaders. Installing libraries
cannot remove this source-level restriction. CPU-only modes below are a useful
alternative for some neural-network experiments, not for the Vulkan voxel game.

## Run the programs directly

All commands below start in `big-project` unless noted.

| Project | Command / purpose |
| --- | --- |
| Tester | First `cd src/Tester`, then `dotnet run -c Release`. Its literal `../../assets` paths require this working directory. |
| VoxelGame | `dotnet run --project src/VoxelGame -c Release` |
| BrainEditor | `dotnet run --project src/BrainEditor -c Release -- --project artifacts/editor/my-brain.brain.json` |
| ArcadeLab | `dotnet run --project src/ArcadeLab -c Release -- --help` to inspect modes before starting a training job. |
| PhysicsTests | `dotnet run --project src/PhysicsTests -c Release` (custom executable checks, many need Vulkan). |
| VoxelTests | `dotnet run --project src/VoxelTests -c Release` (custom executable checks, many need Vulkan). |
| BrainEditor.Checks | `dotnet run --project tests/BrainEditor.Checks -c Release` (custom executable checks). |
| MYT.Kit, MYT.IKit, NeuralGraph, BrainRuntime, VoxelLighting | Class libraries; build the solution or their `.csproj`, and run a consuming executable. |

For a bounded CPU neural experiment without a desktop/Vulkan device:

```sh
dotnet run --project src/ArcadeLab -c Release -- --train --backend cpu --frames 10 --headless --mute --no-save
```

This does not exercise GPU rendering. To pass it through the launcher use
`--app ArcadeLab --args --train --backend cpu --frames 10 --headless --mute --no-save`.
See the existing BrainEditor/NeuralGraph READMEs and `ArcadeLab --help` for other modes.
No long training jobs run automatically during build.

## Troubleshooting

- `NETSDK1045` / SDK not found: install the stable .NET 10 **SDK**; check architecture
  and `dotnet --list-sdks` from this directory.
- Restore fails: check NuGet connectivity/proxy settings and SDK availability.
  If packages are already cached, `dotnet build Showcase.slnx -c Release --no-restore -m:1`
  can validate compilation offline. A fresh checkout needs a successful restore first.
- `DllNotFoundException`: the named native shared library and its own dependencies
  must be visible to the process. Check the exact filename and CPU architecture.
- Validation layer unavailable: install/activate Vulkan SDK validation layers;
  Release configuration does not disable Tester's hard-coded request.
- No suitable GPU/device: inspect `vulkaninfo` and the application's reported feature
  requirements. Software Vulkan implementations can be very slow and may lack features.
- Missing assets: keep outputs within the source tree; launch Tester from `src/Tester`.
- Headless server: use supported CPU/headless modes. Graphical applications require
  a desktop/session and a present-capable GPU; building them is not a runtime test.
