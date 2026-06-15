# Andrej Karpathy Coding Skills & Guidelines

## Core Principles
1. Think before coding. Clarify ambiguity, ask questions, list tradeoffs before writing code. Never assume requirements.
2. Simplicity first. Prefer simple, readable solutions over over-engineered abstractions. Keep code minimal.
3. Surgical edits. Only modify code directly related to the current task. Do not reformat, refactor, or change unrelated logic, comments, or style.
4. Goal-driven work. Define clear acceptance criteria first. Verify results after implementation. Ensure functionality works as expected.

## Code Style Rules
- Write clean, concise, idiomatic code
- Avoid unnecessary dependencies and complexity
- Prioritize maintainability and readability
- Keep changes small and focused
- Do not rewrite existing working code without explicit permission

## Behavior Rules
- Point out potential risks and edge cases actively
- Suggest multiple solutions when appropriate
- Stop and confirm before large structural changes

## Build & Run
```powershell
# Build + restart WinAssistant (run from repo root):
powershell -Command "Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3; dotnet build WinAssistant\WinAssistant.csproj -c Debug --verbosity quiet; Start-Process -FilePath 'WinAssistant\bin\Debug\net9.0-windows10.0.26100.0\win-x64\WinAssistant.exe'"

# Kill + restart without rebuild:
powershell -Command "Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 3; Start-Process -FilePath 'WinAssistant\bin\Debug\net9.0-windows10.0.26100.0\win-x64\WinAssistant.exe'"

# Debug log:
powershell -Command 'cat "$env:TEMP\WinAssistant_dbg.txt" -Tail 50'
```