# AGENTS.md

This file provides coding‑specific guidance for agents working in this repository.

## Non‑Obvious Code Conventions
- **Import alias** `@` resolves to `src` (see `vite.config.ts`). Use it for all internal imports.
- **Class name merging**: always use the `cn()` helper from `src/lib/utils.ts` (wraps `clsx` + `tailwind‑merge`). Direct `clsx` or `twMerge` usage is discouraged.
- **Tailwind custom palette**: colors `discord-bg-chat`, `discord-bg-sidebar`, `discord-bg-navigation` are defined in the Tailwind config; they are not standard Tailwind colors.
- **Backend CORS** is locked to `http://localhost:5173`. Any fetch from a different origin will fail silently.
- **In‑memory repositories**: all API data lives in memory and resets on each server restart; persisting data across runs is not supported.
- **Mock data**: during frontend development the UI loads JSON from `public/mocks/` (e.g., `channels.json`). The API is not called for these resources.
- **Dynamic import** in `src/services/data.service.ts` is required to avoid circular dependencies when loading DM data.
- **No import‑order lint rule**: rely on the `@` alias for clean paths; ESLint does not enforce a specific order.
- **Testing**: there is no dedicated test script; backend tests run via `dotnet test` in `backend/Vinculum.Api.Tests`, and frontend tests are not configured (Vitest not set up).

# Code Mode Rules

## Build / Run Commands (non‑standard)
- `npm run dev` – starts the Vite dev server (frontend) on **http://localhost:5173**.
- `dotnet run --project backend/Vinculum.Api/Vinculum.Api.csproj` – launches the ASP.NET Core API (CORS allows the Vite dev server).
- `npm run build` – builds the frontend for production.
- `npm run lint` – runs ESLint on all **.ts/.tsx** files.
- No dedicated test script is defined; unit tests are absent.

## Code Style (project‑specific)
- Import alias `@` maps to `./src` (see **vite.config.ts**).
- Use the `cn()` utility from **src/lib/utils.ts** for class name merging instead of raw `clsx`/`twMerge`.
- ESLint uses the **eslint.config.js** preset: React Hooks, React Refresh, TypeScript‑recommended.
- Tailwind config adds custom colors `discord-bg-chat`, `discord-bg-sidebar`, `discord-bg-navigation`.

## Non‑Obvious Patterns & Gotchas
- Backend CORS is locked to `http://localhost:5173`; any other origin will be blocked.
- All repositories in the API are in‑memory; data resets on each restart.
- The frontend fetches mock JSON files from `/mocks/...` during development.
- Dynamic imports in **src/services/data.service.ts** avoid circular dependencies for DM data.
- No linting rule for import order; rely on the alias `@` for clean paths.

## CRITICAL INSTRUCTIONS FOR FILE CREATION

1. **NO POWERSHELL FOR FILES:** Do NOT use `powershell`, `pwsh`, `cmd`, or any shell command (like `echo`, `type`, `Set-Content`, `Out-File`) to write or modify files.
2. **USE THE TOOL:** You MUST use the `write_to_file` tool for all file creation and editing. This is not a suggestion; it is a constraint.
3. **Handle Newlines:** When using `write_to_file`, ensure all newlines are literal real newlines in the `content` parameter, not escaped `\n` characters, unless the file format specifically requires it (e.g., JSON strings).
4. **Fallback:** If you absolutely must generate a file via a shell command (e.g., for a setup script), you MUST write a temporary Python script to generate the file and execute that Python script.
   - ❌ BAD: `echo "import os..." > script.py`
   - ✅ GOOD: `python -c "open('script.py', 'w').write('import os\n...')"`

## WINDOWS & FILESYSTEM COMPATIBILITY RULES

1. **STRICT FILE IO:**
   - **NEVER** use shell commands (`echo`, `type`, `Get-Content`, `Set-Content`, `printf`) to read or write files.
   - **ALWAYS** use the provided tools: `read_file` to read, `write_to_file` to write/edit.
   - **REASON:** Windows terminals corrupt text encoding and newlines when piping content.

2. **PATH HANDLING:**
   - **ALWAYS** use forward slashes (`/`) for file paths, even on Windows (e.g., `src/components/App.tsx`).
   - **NEVER** use unescaped backslashes.

3. **ENVIRONMENT AWARENESS:**
   - This is a **Windows** environment.
   - **DO NOT** assume the existence of Linux utilities like `touch`, `sed`, `awk`, `grep`, `make`, or `sudo`.
   - If you need to search text, use the `search_files` tool, do not try to `grep` via the terminal.

4. **TERMINAL DISCIPLINE:**
   - When running commands, assume the shell is `cmd.exe` or `PowerShell`.
   - Do not chain commands with `&&` unless you are certain the shell supports it (PowerShell uses `;` or `&&` only in v7+).
   - If a command fails due to syntax, **STOP**. Do not blindly retry. Ask the user for the correct syntax or switch to a Python script to execute the logic.

5. **NEW FILE CREATION:**
   - When creating a new file, always output the **FULL** content in a single `write_to_file` block. Do not create an empty file and then append to it.

## PATH & FILESYSTEM STANDARDS
1. **ALWAYS** use forward slashes (`/`) for all file paths, even in terminal commands (PowerShell/CMD accept them).
   * ❌ Bad: `src\components\Button.tsx`
   * ✅ Good: `src/components/Button.tsx`
2. **RELATIVE PATHS:** Always prefer relative paths (`./utils/helpers.ts`) over absolute paths (`C:/Users/...`) to avoid confusion with drive letters.
3. **CASE SENSITIVITY:** Treat the filesystem as case-sensitive (like Linux) even though Windows isn't. This prevents "File not found" errors if you deploy to a Linux server later.

## FRONTEND RULES (React 19 / Next.js)

1. **SERVER COMPONENTS FIRST:**
   - Default to React Server Components (RSC) for all data fetching.
   - **DO NOT** use `useEffect` + `fetch` for initial data loading. Use async components.
   - Only add `'use client'` when absolutely necessary (interactivity, hooks, browser events).

2. **MODERN DATA MUTATIONS:**
   - **DO NOT** use manual `onClick` handlers with `fetch` for form submissions.
   - **MUST** use Server Actions (`action={myAction}`) and the `useActionState` hook for form state.
   - Use `useOptimistic` for immediate UI feedback.

3. **NO MORE FORWARDREF:**
   - **NEVER** use `React.forwardRef`.
   - Pass `ref` directly as a prop (React 19 standard).

4. **STRICT STYLING (Tailwind):**
   - Do not invent Tailwind classes. If you are unsure, use standard CSS in a `style` tag or verify the class exists.
   - **Mobile First:** Always write base styles for mobile, then `md:`, `lg:` modifiers.

## BACKEND RULES (.NET 10 / C# 14)

1. **VERSION HANDLING (CRITICAL):**
   - You do not natively know .NET 10.
   - **STRATEGY:** Write valid **.NET 9 / C# 13** code.
   - If I ask for C# 14 features (like the `field` keyword), use them, but otherwise stick to stable C# 13 patterns to avoid compilation errors.

2. **MINIMAL APIs OVER CONTROLLERS:**
   - **DEFAULT** to Minimal APIs (`app.MapGet`) for all endpoints.
   - **DO NOT** generate `Controllers` folders or classes deriving from `ControllerBase` unless explicitly asked.
   - Use `TypedResults` (e.g., `TypedResults.Ok()`) for strong typing.

3. **MODERN C# SYNTAX:**
   - **Global Usings:** Do not clutter files with standard `using System;` statements; assume they are in `GlobalUsings.cs`.
   - **File-Scoped Namespaces:** Always use `namespace MyApp.Api;` (no curly braces).
   - **Primary Constructors:** Use `public class Service(ILogger<Service> logger)` instead of defining manual fields and constructors.

4. **RECORDS FOR DTOs:**
   - Always use `public record LoginRequest(string Email, string Password);` for DTOs. Never use classes with getters/setters for simple data transfer.

## CODE QUALITY STANDARDS
- **No "Pseudo-code":** Never write `# ... rest of code`. Write the full file every time.
- **Defensive Coding:** Assume user input is malicious. Assume the network is down.
- **Tests First:** If the user asks for a feature, write the test case *before* the implementation (TDD).

## LOCAL ENVIRONMENT AWARENESS (Self-Hosted Specifics)
- **Tooling:** You have access to `python` scripts for complex logic. Use them!
- **Verification:** After every edit, you MUST run a build check (`npm run build` or `dotnet build`).
  - If it fails: **FIX IT**. Do not report the failure.
  - You have 3 "lives" (retry attempts) before you must report a failure to the user.