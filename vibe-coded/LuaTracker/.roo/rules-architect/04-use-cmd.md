# Rule: Use CMD (cmd.exe) — Never PowerShell

## Shell Requirement

- **Always** use `cmd.exe` as the shell for all terminal commands.
- **Never** use PowerShell, `pwsh`, or PowerShell syntax.
- When running commands via `execute_command`, use CMD-compatible syntax only.

## Syntax Rules

- Use `&&` to chain commands, not `;` (PowerShell separator).
- Use `set` for environment variables, not `$env:`.
- Use `%VARIABLE%` for variable expansion, not `$Variable` or `${Variable}`.
- Use `dir` instead of `Get-ChildItem` or `ls`.
- Use `del` / `rmdir` instead of `Remove-Item`.
- Use `type` instead of `Get-Content` or `cat`.
- Use `copy` / `xcopy` instead of `Copy-Item`.
- Use `if`, `for`, and `call` for control flow — never PowerShell cmdlets.
- Use `^` as the line continuation character, not `` ` `` (backtick).
- Wrap multi-line scripts in `.bat` or `.cmd` files when needed.

## Common Patterns

```batch
:: Run a Node script
cmd /c npm run dev

:: Set an env var and run
cmd /c "set VITE_API_URL=http://localhost:5000 && npm run dev"

:: Chain commands
cmd /c "cd backend\Vinculum.Api && dotnet run"

:: Check if a file exists
if exist "package.json" echo Found
```

## Why

PowerShell has encoding quirks, execution policy issues, and syntax differences that cause unpredictable failures in automated tool use on Windows. CMD is simpler and more reliable for the operations this agent performs.