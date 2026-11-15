# Build and run: Physics

## Install

Install **LÖVE 11.5** using the [Windows, macOS and Linux installation steps](../../BUILDNRUN.md#love-installation-all-lua-projects).
No compiler, LuaRocks packages, or standalone Lua installation is needed.
Windows/Linux/macOS desktop use requires LÖVE graphics support and a graphical session.
Keyboard/mouse controls and existing bugs are unchanged; mobile/touch operation is unverified.

## Run directly

Open a terminal in this project directory, keeping all included files together.

Linux (or any OS with `love` on PATH):

```sh
love .
```

macOS:

```sh
/Applications/love.app/Contents/MacOS/love .
```

Windows PowerShell (adjust the installation path if necessary):

```powershell
& 'C:\Program Files\LOVE\love.exe' .
```

Run from this directory so any ordinary relative file paths also resolve correctly.

## Portable launcher and packaging

From this project directory, with [Python 3](../../BUILDNRUN.md#start-here):

```sh
python3 ../../tools/buildnrun.py check hand-coded/Physics
python3 ../../tools/buildnrun.py run hand-coded/Physics
python3 ../../tools/buildnrun.py build hand-coded/Physics
```

On Windows replace `python3` with `py -3`. For a custom LÖVE installation set
`LOVE_BIN` as described in the root guide. The launcher sets the working directory
and finds LÖVE without relying on VS Code or a personal installation path.
Packaging needs only Python: the result is
`../../dist/hand-coded-Physics.love`, containing `main.lua` at the archive root
and the project's resources. Open it with LÖVE on a compatible desktop.
It still requires LÖVE; packaging does not compile Lua or create a standalone executable.

The existing configuration requests a 1920 × 1080 window. A smaller display may
clip the window; changing that configuration would change application code and was left to the author.

## Troubleshooting

- `love` not found: install LÖVE and use its full executable path or `LOVE_BIN`.
- Missing image/module: restore the full project and preserve filename capitalization.
- Blank/error window: read the terminal or LÖVE error screen. Verify graphics drivers
  and a LÖVE 11.x runtime; packaging cannot fix an existing application error.
- For audio errors on a machine with no sound device, try a normal desktop audio
  session. Linux diagnostic runs can use `ALSOFT_DRIVERS=null`, which disables sound output.
