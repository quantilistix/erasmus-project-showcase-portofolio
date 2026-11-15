#!/usr/bin/env python3
"""Portable build/launch tooling. Application sources are never rewritten."""
import argparse
import ctypes.util
import functools
import http.server
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
LOVE = {
    "hand-coded/" + name: "hand-coded/" + name
    for name in ("2D_cross-section", "Chess", "NeuralNetwork", "Physics", "Strategy",
                 "cookieclicker", "fractal", "lua_neural_network", "raycast",
                 "rectcast", "wave_sim", "worldgen")
}
LOVE["vibe-coded/LuaTracker"] = "vibe-coded/LuaTracker/src"
LOVE["hand-coded/voxel-based-game/Assets"] = "hand-coded/voxel-based-game/Assets"
WEB = {"vibe-coded/pixelArt": "index.html", "vibe-coded/wave_sim": "wave_sim.html"}
NATIVE = "hand-coded/voxel-based-game"
DOTNET = "vibe-coded/big-project"


def fail(message):
    raise RuntimeError(message)


def tool(name):
    result = shutil.which(name)
    if not result:
        fail(f"Missing {name}. See {ROOT / 'BUILDNRUN.md'} for installation instructions.")
    return result


def love_binary():
    override = os.environ.get("LOVE_BIN")
    candidates = [override] if override else [
        "love", "/Applications/love.app/Contents/MacOS/love",
        str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "LOVE/love.exe"),
    ]
    for candidate in candidates:
        found = shutil.which(candidate)
        if found:
            return found
    fail("LÖVE missing. Install LÖVE 11.5 or set LOVE_BIN to its executable (see BUILDNRUN.md).")


def execute(command, cwd):
    print("Running:", subprocess.list2cmdline([str(x) for x in command]), flush=True)
    subprocess.run([str(x) for x in command], cwd=cwd, check=True)


def dotnet_projects():
    return {p.stem: p for base in ("src", "tests")
            for p in (ROOT / DOTNET / base).glob("*/*.csproj")}


def dotnet_sdk():
    binary = tool("dotnet")
    sdks = subprocess.check_output([binary, "--list-sdks"], text=True)
    if not any(line.startswith("10.0.") for line in sdks.splitlines()):
        fail("Install the .NET 10 SDK, not only the runtime. See big-project/BUILDNRUN.md.")
    return binary


def native_build():
    source = ROOT / NATIVE
    if not (source / "main.cpp").is_file():
        fail("C++ source is missing from this gitlink checkout. See BUILDNRUN.md#checkout-limitations.")
    build = source / "build"
    execute([tool("cmake"), "-S", ROOT / "tools/voxel", "-B", build, "-DCMAKE_BUILD_TYPE=Release"], ROOT)
    execute([tool("cmake"), "--build", build, "--config", "Release", "--parallel", "2"], ROOT)


def love_package(project):
    source = ROOT / LOVE[project]
    if not (source / "main.lua").is_file():
        fail(f"Missing {source / 'main.lua'}. This checkout may lack the nested repository; see BUILDNRUN.md.")
    target = ROOT / "dist" / (project.replace("/", "-") + ".love")
    target.parent.mkdir(parents=True, exist_ok=True)
    # Keep all runtime resources, while excluding editor metadata and build outputs.
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(source.rglob("*")):
            rel = path.relative_to(source)
            if any(p.startswith(".") or p in {"build", "dist", "node_modules", "scripts", "__pycache__"}
                   for p in rel.parts):
                continue
            if path.is_file() and path.suffix.lower() not in {".md", ".gch", ".pyc", ".sh", ".ps1"}:
                archive.write(path, rel.as_posix())
    print(target)


def serve(project, port):
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(ROOT / project))
    # Local-only: do not expose source files to the network.
    with http.server.ThreadingHTTPServer(("127.0.0.1", port), handler) as server:
        print(f"Open http://127.0.0.1:{server.server_port}/{WEB[project]} (Ctrl+C to stop)", flush=True)
        server.serve_forever()


def check(project):
    if project in LOVE:
        if not (ROOT / LOVE[project] / "main.lua").is_file():
            fail("Missing LÖVE entry point; see this project's BUILDNRUN.md.")
        execute([love_binary(), "--version"], ROOT)
    elif project in WEB:
        print("Python HTTP server available; open the URL in a modern browser.")
    elif project == NATIVE:
        execute([tool("cmake"), "--version"], ROOT)
        print("CMake configure checks the compiler, OpenGL, GLFW and TBB. GPU support needs a runtime check.")
    else:
        binary = dotnet_sdk()
        execute([binary, "--version"], ROOT / DOTNET)
        for name in ("vulkan", "glfw", "shaderc_shared", "openal"):
            print(f"Native library hint {name}: {ctypes.util.find_library(name) or 'not found by system lookup'}")
        print("Hints are not GPU validation. See big-project/BUILDNRUN.md for native libraries and hardware limits.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("list", "check", "build", "run"))
    parser.add_argument("project", nargs="?", choices=sorted(set(LOVE) | set(WEB) | {NATIVE, DOTNET}))
    parser.add_argument("--app", help="big-project .csproj name; run defaults to Tester")
    parser.add_argument("--port", type=int, default=8080, help="local HTTP port (0 chooses a free port)")
    parser.add_argument("--args", nargs=argparse.REMAINDER, default=[], help="application arguments, last option")
    args = parser.parse_args()
    if args.action == "list":
        for project in sorted(set(LOVE) | set(WEB) | {NATIVE, DOTNET}):
            print(project)
        print(".NET apps/libraries:", ", ".join(sorted(dotnet_projects())))
        return
    if not args.project:
        parser.error("project is required")
    project = args.project
    if args.app and project != DOTNET:
        parser.error("--app applies only to vibe-coded/big-project")
    if args.action == "check":
        check(project)
    elif project in LOVE:
        if args.action == "build":
            love_package(project)
        else:
            source = ROOT / LOVE[project]
            if not (source / "main.lua").is_file():
                fail(f"Missing {source / 'main.lua'}; see BUILDNRUN.md.")
            execute([love_binary(), source, *args.args], source)
    elif project in WEB:
        if args.action == "build":
            print("No compilation required. Keep the HTML, JavaScript and CSS files together.")
        else:
            serve(project, args.port)
    elif project == NATIVE:
        native_build()
        if args.action == "run":
            executable = ROOT / NATIVE / "build/bin" / ("Application.exe" if os.name == "nt" else "Application")
            execute([executable, *args.args], ROOT / NATIVE)
    else:
        binary = dotnet_sdk()
        projects = dotnet_projects()
        if not (ROOT / DOTNET / "src/ArcadeLab/ArcadeLab.csproj").is_file():
            fail("ArcadeLab source is missing from this gitlink checkout. See BUILDNRUN.md#checkout-limitations.")
        app = args.app or ("Tester" if args.action == "run" else None)
        if app and app not in projects:
            fail("Unknown .NET project. Choices: " + ", ".join(sorted(projects)))
        if args.action == "build":
            # The legacy solution also names a missing StoryMovie project.
            target = projects[app] if app else ROOT / DOTNET / "Showcase.slnx"
            execute([binary, "build", target, "-c", "Release", "--nologo", "-m:1"], ROOT / DOTNET)
        else:
            import xml.etree.ElementTree as ET
            if ET.parse(projects[app]).findtext(".//OutputType") != "Exe":
                fail(f"{app} is a library; choose an executable with --app.")
            cwd = projects[app].parent if app == "Tester" else ROOT / DOTNET
            execute([binary, "build", projects[app], "-c", "Release", "--nologo", "-m:1"], ROOT / DOTNET)
            execute([binary, "run", "--no-build", "--no-restore", "--project", projects[app], "-c", "Release", "--", *args.args], cwd)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except (RuntimeError, OSError, subprocess.CalledProcessError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
