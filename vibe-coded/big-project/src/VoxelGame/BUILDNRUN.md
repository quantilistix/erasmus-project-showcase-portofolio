# Build and run: VoxelGame

Install the **.NET 10 SDK** for your OS and CPU, following the
[Windows, Linux and macOS prerequisites](../../BUILDNRUN.md#install-the-sdk).
The [native runtime instructions](../../BUILDNRUN.md#native-runtime-prerequisites)
cover Vulkan, GLFW, shaderc, validation layers and audio where needed. A managed
build alone does not verify those libraries or GPU support.

From this project directory:

```sh
dotnet build VoxelGame.csproj -c Release -m:1
```

This is an executable. The common launcher selects the correct working directory
and passes arguments without editing application files. From this directory,
with [Python 3](../../../../BUILDNRUN.md#start-here):

```sh
python3 ../../../../tools/buildnrun.py run vibe-coded/big-project --app VoxelGame
```

On Windows use `py -3` in place of `python3`. For direct `dotnet run` commands,
see the [suite run table](../../BUILDNRUN.md#run-the-programs-directly).

Requires a present-capable Vulkan 1.3 GPU with double precision shaders and other
requested features. macOS/Apple GPUs are not a guaranteed target. Keep build output
inside this tree so its asset-root search succeeds. See the suite guide for details.

The complete [suite guide](../../BUILDNRUN.md) describes build outputs, first-time
NuGet restore, platform limitations and troubleshooting. Existing source warnings
and application behavior are unchanged.
