#!/usr/bin/env bash
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
# MCP stdout is reserved for JSON-RPC, including on the first build.
dotnet build "$workspace/src/BrainEditor/BrainEditor.csproj" -c Release --no-restore -m:1 -v:quiet >&2
exec dotnet "$workspace/src/BrainEditor/bin/Release/net10.0/BrainEditor.dll" --mcp --workspace "$workspace"
