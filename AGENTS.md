# AGENTS.md — CodexBar

Coding standards and agent behavior rules for the CodexBar repository.

## Quality Gates

All changes must pass these gates before merge:

1. **Build** — `dotnet build` with zero warnings
2. **Format** — `dotnet format --verify-no-changes` clean
3. **Tests** — `dotnet test` all green
4. **Coverage** — line ≥ current threshold (ratchet up over time)
5. **Security** — `dotnet list package --vulnerable` clean

## Conventions

- **C# 12/13** with primary constructors where appropriate
- **File-scoped namespaces**
- **Implicit usings** enabled
- **Nullable reference types** enabled
- `using` directives **inside** the file-scoped namespace (immediately after `namespace …;`)
- Async methods suffixed with `Async`
- Private fields prefixed with `_`

## Project Structure

```text
src/
  CodexBar.Core/        # Provider abstractions, models, services
  CodexBar.App/         # WPF system tray app
  CodexBar.Core.Tests/  # Unit tests for Core
```

## Testing

- xUnit + NSubstitute
- Name tests `[Method]_[Condition]_[ExpectedResult]`
- Cover both success and failure paths for providers
- Mock `IHttpClientFactory` and `ISettingsService`

## Git

- Conventional commits: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`
- One logical change per commit
- No merge conflict markers committed

## CRAP Score Exclusions

Source-generated code is excluded from CRAP analysis. Specifically:

- **`[GeneratedRegex]` methods** — The regex source generator produces `TryMatchAtCurrentPosition`
  runners with CC ≈ 56. These are optimized state machines with unreachable backtracking branches.
  They cannot be refactored (they're auto-generated) and cannot be fully branch-covered.
- **Any `*.g.cs` file** — All source-generator output is excluded from both coverage collection
  and CRAP scoring.

When running coverage or CRAP analysis, always use:

```powershell
dotnet test --collect:"XPlat Code Coverage" --settings src/CodexBar.Core.Tests/coverage.runsettings
reportgenerator -reports:"**/coverage.cobertura.xml" -targetdir:CoverageReport -reporttypes:JsonSummary -filefilters:"-**/*.g.cs"
```
