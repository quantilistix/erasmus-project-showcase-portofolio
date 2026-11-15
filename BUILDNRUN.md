# Build and run the showcase

Application source and the author's READMEs are unchanged. The tooling supports
Windows, Linux and macOS where the original program's runtime and GPU requirements
permit it. This does **not** promise support for every machine: graphics APIs,
fixed window sizes, keyboard/mouse controls and existing application bugs remain.
Mobile devices, consoles, and headless servers are not universal substitutes for a desktop.

## Start here

Download/clone the whole repository and keep each project's assets with its source.
Install only the prerequisites for the program you want. No IDE is required.
The optional common launcher needs Python 3.9 or newer and uses only its standard library.

- **Windows:** install [Python 3](https://www.python.org/downloads/windows/), reopen
  PowerShell, and check `py -3 --version`. Replace `python3` in the examples with `py -3`.
- **macOS:** install [Python 3](https://www.python.org/downloads/macos/) or run
  `brew install python` if [Homebrew](https://brew.sh/) is already installed.
- **Ubuntu/Debian:** `sudo apt update` then `sudo apt install python3`.
- **Fedora:** `sudo dnf install python3`.

From the repository root:

```sh
python3 tools/buildnrun.py list
python3 tools/buildnrun.py check hand-coded/Chess
python3 tools/buildnrun.py run hand-coded/Chess
python3 tools/buildnrun.py build hand-coded/Chess
python3 tools/buildnrun.py run vibe-coded/pixelArt --port 8080
python3 tools/buildnrun.py build vibe-coded/big-project
python3 tools/buildnrun.py run vibe-coded/big-project --app BrainEditor
```

`check` reports prerequisites, not application correctness or GPU compatibility.
`build` packages LÖVE games into `dist/*.love`, compiles native/.NET projects in
Release, or reports that browser programs need no compilation. `run` starts LÖVE,
builds/starts native or .NET programs, or serves browser files on localhost.
Use `--args` last to pass application arguments, for example:

```sh
python3 tools/buildnrun.py run vibe-coded/big-project --app ArcadeLab --args --help
```

Commands resolve project paths relative to the repository, so the launcher also
works when invoked by an absolute path from another directory. Tool failures return
a nonzero exit status. Nothing automatically installs software or rewrites application sources.

## Projects

| Program | Instructions | Runtime/build requirements |
| --- | --- | --- |
| hand-coded/2D_cross-section | [BUILDNRUN.md](hand-coded/2D_cross-section/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/Chess | [BUILDNRUN.md](hand-coded/Chess/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/NeuralNetwork | [BUILDNRUN.md](hand-coded/NeuralNetwork/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/Physics | [BUILDNRUN.md](hand-coded/Physics/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/Strategy | [BUILDNRUN.md](hand-coded/Strategy/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/cookieclicker | [BUILDNRUN.md](hand-coded/cookieclicker/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/fractal | [BUILDNRUN.md](hand-coded/fractal/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/lua_neural_network | [BUILDNRUN.md](hand-coded/lua_neural_network.BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/raycast | [BUILDNRUN.md](hand-coded/raycast/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/rectcast | [BUILDNRUN.md](hand-coded/rectcast/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/voxel-based-game | [BUILDNRUN.md](hand-coded/voxel-based-game.BUILDNRUN.md) | CMake, C++23, GLFW, TBB; OpenGL 4.6 |
| hand-coded/voxel-based-game/Assets | [BUILDNRUN.md](hand-coded/voxel-based-game.Assets.BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/wave_sim | [BUILDNRUN.md](hand-coded/wave_sim/BUILDNRUN.md) | LÖVE 11.5 |
| hand-coded/worldgen | [BUILDNRUN.md](hand-coded/worldgen/BUILDNRUN.md) | LÖVE 11.5 |
| vibe-coded/LuaTracker | [BUILDNRUN.md](vibe-coded/LuaTracker/BUILDNRUN.md) | LÖVE 11.5 |
| vibe-coded/big-project | [BUILDNRUN.md](vibe-coded/big-project/BUILDNRUN.md) | .NET 10 SDK; native graphics libraries |
| vibe-coded/pixelArt | [BUILDNRUN.md](vibe-coded/pixelArt/BUILDNRUN.md) | Modern browser; Python for local server |
| vibe-coded/wave_sim | [BUILDNRUN.md](vibe-coded/wave_sim/BUILDNRUN.md) | Modern browser; Python for local server |

## LÖVE installation (all Lua projects)

Install [LÖVE 11.5](https://love2d.org/) for your OS/CPU. A standalone Lua interpreter
is not enough; LÖVE includes Lua and the graphics/audio runtime.

- **Windows:** use the official installer or unzip the official Windows download.
  The launcher searches `PATH` and `C:\Program Files\LOVE\love.exe`.
  For another location set `$env:LOVE_BIN = 'D:\Apps\LOVE\love.exe'` in PowerShell.
- **macOS:** download LÖVE, unpack it and move `love.app` to `/Applications`.
  The launcher detects `/Applications/love.app/Contents/MacOS/love`.
- **Ubuntu/Debian:** `sudo apt update` then `sudo apt install love`.
  Check `love --version`; use the official download if the distribution ships an old release.
- **Fedora:** `sudo dnf install love`.
- **Other Linux:** install your distribution's LÖVE package, or use the official
  AppImage on a matching architecture. Make it executable and set
  `export LOVE_BIN='/absolute/path/to/love.AppImage'`.

LÖVE needs a graphical session, working graphics drivers, and normally an audio
device. Set `LOVE_BIN` to an executable path, with no extra flags. Do not expect
11.x compatibility with every future major LÖVE release. See each guide for direct
commands that do not need Python. A `.love` file still needs LÖVE installed; it is
not a native executable. Keep resource filename capitalization intact on Linux.

## Other prerequisites

The [C++ guide](hand-coded/voxel-based-game.BUILDNRUN.md) explains compiler/CMake,
GLFW and TBB installation. The [.NET guide](vibe-coded/big-project/BUILDNRUN.md)
explains the .NET 10 SDK, Vulkan drivers, validation layers, shaderc, GLFW and OpenAL.
The two browser programs run without Node.js or npm; pixelArt's optional existing
tests use Node.js and Playwright.

## Checkout limitations

Three paths are recorded as Git **gitlinks**, with no `.gitmodules` URLs:

- `hand-coded/lua_neural_network`
- `hand-coded/voxel-based-game`
- `vibe-coded/big-project/src/ArcadeLab`

Their source files exist in this working copy, but a fresh clone may leave those
folders empty. `git submodule update --init` cannot recover them without the original
repository URLs. Obtain complete source folders from the author before trying to
build those programs or the .NET projects that depend on ArcadeLab. The launch/build
tooling does not invent remote URLs, convert gitlinks, or change existing source.

Guides are provided inside each populated project. Distributable copies also live
beside the gitlinks: [Lua neural network](hand-coded/lua_neural_network.BUILDNRUN.md),
[C++ voxel game](hand-coded/voxel-based-game.BUILDNRUN.md),
[LÖVE asset utility](hand-coded/voxel-based-game.Assets.BUILDNRUN.md), and
[ArcadeLab](vibe-coded/big-project/src/ArcadeLab.BUILDNRUN.md). Commands in these
fallback guides assume you have entered the project directory named by the title;
relative links are adjusted for the guide's location. The canonical CMake definition
is in `tools/voxel`, so the build definition itself remains in the parent repository.
A guide inside a gitlink requires a commit in its original repository to travel
with that repository. Existing nested edits were left intact.

## Verification limits

Build/launch tooling cannot remove source-level platform or hardware dependencies.
In particular the C++ game's requested OpenGL 4.6 context prevents a native macOS
run (macOS exposes up to OpenGL 4.1). The .NET VoxelGame requires Vulkan 1.3 with
64-bit shader floating point; do not assume MoltenVK/Apple GPUs meet those requirements.
See [GLFW's compatibility guide](https://www.glfw.org/docs/latest/compat_guide.html).
Windows/macOS builds and interactive behavior need testing on those actual systems.

Verified here on Linux: all 12 .NET projects compile in Release; the C++ CMake target
compiles and links with GCC 13 and TBB; all 14 LÖVE archives have valid entry points;
both browser servers serve their entry files; launcher checks cover paths/arguments
with spaces, a different caller directory, LuaTracker's working directory and a missing
runtime. ArcadeLab help and a bounded CPU/headless training smoke run also succeed.
These are build/tooling checks, not interactive gameplay or Windows/macOS certification.
