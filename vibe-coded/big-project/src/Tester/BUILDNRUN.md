# Build and run: Tester

Install the **.NET 10 SDK** for your OS and CPU, following the
[Windows, Linux and macOS prerequisites](../../BUILDNRUN.md#install-the-sdk).
The [native runtime instructions](../../BUILDNRUN.md#native-runtime-prerequisites)
cover Vulkan, GLFW, shaderc, validation layers and audio where needed. A managed
build alone does not verify those libraries or GPU support.

From this project directory:

```sh
dotnet build Tester.csproj -c Release -m:1
```

This is an executable. The common launcher selects the correct working directory
and passes arguments without editing application files. From this directory,
with [Python 3](../../../../BUILDNRUN.md#start-here):

```sh
python3 ../../../../tools/buildnrun.py run vibe-coded/big-project --app Tester
```

On Windows use `py -3` in place of `python3`. For direct `dotnet run` commands,
see the [suite run table](../../BUILDNRUN.md#run-the-programs-directly).

Direct alternative **from this directory**: `dotnet run -c Release`. Keep this
working directory: its source resolves assets via `../../assets`. Vulkan 1.3,
Khronos validation layers, shaderc shared library and a desktop session are required.

The complete [suite guide](../../BUILDNRUN.md) describes build outputs, first-time
NuGet restore, platform limitations and troubleshooting. Existing source warnings
and application behavior are unchanged.
