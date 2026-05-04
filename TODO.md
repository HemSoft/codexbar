# CodexBar TODO

| Status | Priority | Task | Notes |
|--------|----------|------|-------|
| 🚧 | Critical | [Backfill test coverage to 80%](#backfill-test-coverage) | Currently 13% line / 12% branch. Need tests for all providers, services, config |
| 📋 | High | [Add CRAP score tooling](#add-crap-score-tooling) | ReportGenerator + coverlet.runsettings for complexity × coverage analysis |
| 📋 | High | [Add Stryker.NET mutation testing](#add-mutation-testing) | Target ≥ 80% mutation score |
| 📋 | Medium | [Create scorecard skill](#create-scorecard-skill) | Model after hs-buddy and relias-assistant scorecard skills |
| 📋 | Medium | [Add husky + lint-staged pre-commit hooks](#add-husky-hooks) | Auto-run dotnet format and dotnet test on commit |
| 📋 | Low | [Add markdownlint-cli](#add-markdownlint) | Lint README.md and other docs |
| 📋 | Low | [Expand .gitignore patterns](#expand-gitignore) | Add .agents/, coverage/, TestResults/ exclusions |
| ✅ | High | Fix 233 code formatting violations | Resolved with `dotnet format` (2026-05-04) |
| ✅ | High | Add .editorconfig | Created with .NET C# conventions (2026-05-04) |
| ✅ | High | Add static analysis (Roslyn + StyleCop) | Added to Core, App, and Tests projects (2026-05-04) |
| ✅ | High | Create GitHub Actions CI workflow | `.github/workflows/ci.yml` — build, format, test, security (2026-05-04) |
| ✅ | Medium | Create AGENTS.md | Coding standards and quality gates documented (2026-05-04) |
| ✅ | Medium | Create perfection skill | `.agents/skills/perfection/` with SKILL.md and Invoke-PerfectionCheck.ps1 (2026-05-04) |
| ✅ | Medium | Create CodexBar.App.Tests | xUnit project with initial Converters tests (2026-05-04) |
| ✅ | Low | Security audit clean | `dotnet list package --vulnerable` shows 0 issues (2026-05-04) |
| ✅ | Low | Build zero warnings | `dotnet build` passes with 0 warnings (2026-05-04) |

## Progress

**Completed: 8 / 16** (50%)

---

## Remaining Items

### Backfill test coverage

**Problem:** Coverage is at ~13% line / 12% branch. Only 55 Core tests exist; the entire WPF App layer was untested until the new App.Tests project was created.

**What to test:**
- `ClaudeProvider` — full HTTP mocking for auth, quota, and error paths
- `CopilotProvider` — multi-account discovery, token resolution, API responses
- `OpenRouterProvider` — credits, usage parsing
- `OpenCodeGoProvider` — minimal but present
- `SettingsService` / `FileSecurityHelper` — file I/O with temp directories
- `UsageRefreshService` — timer behavior, event firing, provider failure handling
- `MainViewModel` — property changes, command execution
- `MainWindow` — position persistence, zoom, drag behavior (UI automation or extracted logic)

**Target:** 80% line / 70% branch as first milestone, then ratchet up.

---

### Add CRAP score tooling

**Problem:** No automated CRAP (Change Risk Anti-Patterns) score analysis exists.

**Solution:**
1. Add `coverlet.runsettings` with thresholds
2. Install `dotnet-reportgenerator-globaltool`
3. Configure CI to generate JSON + HTML reports
4. Flag any method with CRAP > 30 and fail CI if found

---

### Add mutation testing

**Problem:** Tests may have weak assertions. Mutation testing verifies test quality by mutating code and checking if tests catch the change.

**Solution:** Install `Stryker.NET` (`dotnet tool install dotnet-stryker`) and run per-project.

**Target:** ≥ 80% mutation score.

---

### Create scorecard skill

**Problem:** No scorecard skill exists to track org-metrics maturity.

**Solution:** Model after `hs-buddy/.agents/skills/scorecard/` and `relias-assistant/.agents/skills/scorecard/`. Create `.agents/skills/scorecard/SKILL.md` with status + improve commands.

---

### Add husky hooks

**Problem:** No pre-commit validation. Formatting or test breakages can be committed accidentally.

**Solution:** Add `husky` + `lint-staged` (or equivalent .NET git hooks) to run `dotnet format --verify-no-changes` and `dotnet test` before each commit.

---

### Add markdownlint

**Problem:** No markdown linting configured.

**Solution:** Add `markdownlint-cli` or `markdownlint-cli2` to package.json (or as a global tool) and enforce in CI.

---

### Expand .gitignore

**Problem:** `.gitignore` is missing patterns for new tooling artifacts.

**Solution:** Add `.agents/`, `coverage/`, `TestResults/`, `.stryker/`, and other tool outputs.
