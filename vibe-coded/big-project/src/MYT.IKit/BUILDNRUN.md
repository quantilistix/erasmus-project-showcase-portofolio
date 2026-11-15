# Build and run: MYT.IKit

Install the **.NET 10 SDK** for your OS and CPU, following the
[Windows, Linux and macOS prerequisites](../../BUILDNRUN.md#install-the-sdk).
The [native runtime instructions](../../BUILDNRUN.md#native-runtime-prerequisites)
cover Vulkan, GLFW, shaderc, validation layers and audio where needed. A managed
build alone does not verify those libraries or GPU support.

From this project directory:

```sh
dotnet build MYT.IKit.csproj -c Release -m:1
```

This is a **class library**, so it has no standalone run command. Build it
and run a consuming application from the [suite guide](../../BUILDNRUN.md#run-the-programs-directly).

The complete [suite guide](../../BUILDNRUN.md) describes build outputs, first-time
NuGet restore, platform limitations and troubleshooting. Existing source warnings
and application behavior are unchanged.
