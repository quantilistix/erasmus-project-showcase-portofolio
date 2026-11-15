# Build and run: voxel-based-game

## Supported machines

The unchanged C++ source requests an **OpenGL 4.6 core** context. Use a desktop
Windows/Linux machine with a supporting GPU and current vendor graphics drivers.
macOS's native OpenGL stops at 4.1, so this game cannot run natively there without
source changes. This follows from the context requested in `src/glinit.cpp` and
[GLFW's macOS OpenGL support](https://www.glfw.org/docs/latest/compat_guide.html).
Virtual machines, remote desktops, mobile systems and old GPUs may not supply 4.6.
The build files can be configured on macOS for compilation, but that does not remove
the runtime limit. No application source or shaders have been changed.

## Install prerequisites

You need **CMake 3.20+**, a C++23 compiler (GCC 13+, recent Clang, or current Visual
Studio C++ tools), **GLFW 3.3+**, **oneTBB**, OpenGL development libraries and threads.
GLAD, GLM and stb_image are already included. The source includes a TBB header even
though its current world maps use standard containers; the build still needs that header.
Use dependencies matching your compiler and CPU architecture.

### Ubuntu / Debian

```sh
sudo apt update
sudo apt install build-essential cmake libglfw3-dev libtbb-dev libgl1-mesa-dev
```

Check `g++ --version` and `cmake --version`. On older distributions install a newer
compiler/CMake from the distribution's documented packages. Select another compiler
on the **first** configure with `-DCMAKE_CXX_COMPILER=g++-13` (or its installed path).
Install graphics drivers appropriate to your GPU; development packages alone do
not provide hardware support.

### Fedora

```sh
sudo dnf install gcc-c++ cmake glfw-devel tbb-devel mesa-libGL-devel
```

### Windows

Install [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/)
with **Desktop development with C++**, the Windows SDK and CMake tools (or install
[CMake](https://cmake.org/download/) separately). Reopen a Developer PowerShell.
Install Git if needed and set up [vcpkg](https://learn.microsoft.com/en-us/vcpkg/get_started/get-started):

```powershell
git clone https://github.com/microsoft/vcpkg.git C:\dev\vcpkg
C:\dev\vcpkg\bootstrap-vcpkg.bat
C:\dev\vcpkg\vcpkg.exe install glfw3:x64-windows tbb:x64-windows
```

From this project directory, configure with that toolchain **before** using the
common launcher (change paths/triplet for your installation/architecture):

```powershell
cmake -S ../../tools/voxel -B build -A x64 -DCMAKE_TOOLCHAIN_FILE=C:/dev/vcpkg/scripts/buildsystems/vcpkg.cmake -DVCPKG_TARGET_TRIPLET=x64-windows
cmake --build build --config Release --parallel 2
cmake --build build --config Release --target run
```

vcpkg normally copies required DLLs beside the executable. If a DLL is missing,
make the matching vcpkg `installed/x64-windows/bin` directory available on `PATH`.
Do not mix MinGW libraries with MSVC libraries. An ARM build also needs a matching
triplet/compiler and a GPU driver that actually supports the requested OpenGL API.

### macOS (compile only)

With [Homebrew](https://brew.sh/) installed: `xcode-select --install`, then
`brew install cmake glfw tbb`. Build with CMake as below. Native running remains
blocked by OpenGL 4.6; installing GLFW does not add a newer OpenGL implementation.
Use a supported Windows/Linux machine for running this unchanged game.

## Build and run

From this project directory on Linux, or another configured CMake environment:

```sh
cmake -S ../../tools/voxel -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release --parallel 2
cmake --build build --config Release --target run
```

The executable is `build/bin/Application` (`Application.exe` on Windows).
The `run` target sets the working directory to the project root so relative
`shaders/` and `Assets/` paths resolve. If starting the executable yourself, do so
from this directory. Keep the source asset folders with the build; this is not a
standalone distributable executable.

Alternatively, with [Python 3](../../BUILDNRUN.md#start-here):

```sh
python3 ../../tools/buildnrun.py check hand-coded/voxel-based-game
python3 ../../tools/buildnrun.py build hand-coded/voxel-based-game
python3 ../../tools/buildnrun.py run hand-coded/voxel-based-game
```

On Windows replace `python3` with `py -3`. The launcher reuses `build/CMakeCache.txt`,
including an already configured vcpkg toolchain. For dependencies in custom locations,
pass `-DCMAKE_PREFIX_PATH=/path/to/prefix` on the first manual configure.

CMake discovers actual source files, links platform dependencies via imported targets,
and stages unchanged headers in `build/headers` so the checked-in compiler-specific
`include/pch.hpp.gch` is never used. The old VS Code g++ task and precompile script
are legacy alternatives; use CMake for the portable build. Do not run the old
precompile script: it writes a binary into the source tree.

## Troubleshooting

- Missing `glfw3Config.cmake`/`TBBConfig.cmake`: install development packages or
  configure the correct vcpkg toolchain / CMake prefix.
- Changed generator, compiler, architecture or toolchain: choose a fresh build
  directory, or remove only the generated `build` directory and reconfigure.
- Context creation fails or startup crashes: verify OpenGL 4.6 support in the
  active graphics driver. The original source does not safely recover from all
  initialization errors; CMake cannot fix that behavior.
- Compile errors after dependency discovery can reflect original source/library
  compatibility. Report them with compiler versions; application code is intentionally unchanged.
- `Assets/` also contains a separate LÖVE conversion utility; see its
  [own guide](Assets/BUILDNRUN.md). It is not required to compile the C++ program.
