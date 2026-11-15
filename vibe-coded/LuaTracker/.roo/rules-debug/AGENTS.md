# AGENTS.md

This file provides debugging‑specific guidance for agents working in this repository.

## Non‑Obvious Debugging Gotchas
- VSCode extension logs appear only in the **Extension Host** output channel; they are not shown in the Debug Console.
- IPC messages between the webview and the extension must be wrapped in `try/catch`; uncaught errors are swallowed silently.
- The backend API enforces CORS for `http://localhost:5173` only – requests from other origins will fail without a clear error.
- All API repositories are in‑memory; restarting the API clears all data, which can appear as “missing data” during debugging.
- Frontend mock data is loaded from `public/mocks/…`; if a mock file is missing, the UI will show empty state without an explicit error.
- Console statements such as `console.log` are used sparingly (e.g., in `src/components/dm/dm-sidebar.tsx`); rely on the browser devtools for runtime inspection.

# Debug Mode Rules

## AGENTIC AUTONOMY

### ERROR HANDLING (CRITICAL)
If a shell command fails (e.g., syntax error, missing dependency), **FIX IT YOURSELF**.
- Do not stop to explain the error.
- Read the error, formulate a fix, apply it, and retry.
- Only ask for human intervention if you fail 3 consecutive times.

### TESTING PROTOCOL
After writing code, immediately run a verification command (e.g., `npm test`, `dotnet build`, or a quick python script) to prove it works. Do not wait for the user to tell you to test.

### THE 3-LIVES RULE
- You have 3 "lives" (retry attempts) before you must report a failure to the user.
- **First attempt:** Try the obvious fix.
- **Second attempt:** Read documentation or related files for context.
- **Third attempt:** Try a different approach entirely.
- **After 3 failures:** Report to user with full diagnostic information.

## WINDOWS SURVIVAL (Refined)

### TERMINAL COMPATIBILITY
- **Powershell Ban:** Use `cmd.exe` or `bash` (if WSL). Avoid `Get-Content` / `Set-Content`.
- **Pathing:** Force `/` forward slashes everywhere.
- **Line Endings:** Force `\n` in all `write_to_file` calls.

### ENVIRONMENT AWARENESS
- This is a **Windows** environment.
- **DO NOT** assume the existence of Linux utilities like `touch`, `sed`, `awk`, `grep`, `make`, or `sudo`.
- If you need to search text, use the `search_files` tool, do not try to `grep` via the terminal.

### COMMAND EXECUTION
- When running commands, assume the shell is `cmd.exe` or `PowerShell`.
- Do not chain commands with `&&` unless you are certain the shell supports it (PowerShell uses `;` or `&&` only in v7+).
- If a command fails due to syntax, **STOP**. Do not blindly retry. Ask the user for the correct syntax or switch to a Python script to execute the logic.

## DIAGNOSTIC WORKFLOW

### WHEN A BUILD/TEST FAILS:
1. **Read the error output completely** - don't skim
2. **Identify the root cause** - is it:
   - Missing dependency?
   - Syntax error?
   - Type error?
   - Configuration issue?
   - Environment issue?
3. **Check related files** - imports, configs, dependencies
4. **Apply the fix**
5. **Verify** - run the command again
6. **Document** - if this was a non-obvious fix, add a comment or update `.cline/memory.md`

### DEBUGGING TOOLS
- Use `console.log` / `Console.WriteLine` liberally during debugging
- For TypeScript: Check `tsconfig.json` for strict mode issues
- For C#: Check `.csproj` for SDK version mismatches
- For build errors: Check `package.json` / `.csproj` versions match your code

## LOCAL ENVIRONMENT VERIFICATION
After every edit, you MUST run a build check:
- Frontend: `npm run build` or `npm run lint`
- Backend: `dotnet build`
- If it fails: **FIX IT**. Do not report the failure.
- You have 3 "lives" (retry attempts) before you must report a failure to the user.

## NON-OBVIOUS PATTERNS & GOTCHAS
- Backend CORS is locked to `http://localhost:5173`; any other origin will be blocked.
- All repositories in the API are in‑memory; data resets on each restart.
- The frontend fetches mock JSON files from `/mocks/...` during development.
- Dynamic imports in **src/services/data.service.ts** avoid circular dependencies for DM data.