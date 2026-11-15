# AGENTS.md

This file provides guidance to agents when working with code in this repository.

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

## Architecture Overview
- **Frontend**: Vite + React (TSX) compiled with Tailwind.
- **Backend**: .NET 10 Web API serving OpenAPI docs and in‑memory endpoints.
- Communication: HTTP fetches to API (development uses mock JSON files).
