# CodexBar TODO

| Status | Priority | Task | Notes |
|--------|----------|------|-------|
| 📋 | High | [#6 Add Stryker.NET mutation testing](#add-mutation-testing) | Target ≥ 80% mutation score |
| 📋 | Medium | [#11 Create scorecard skill](#create-scorecard-skill) | Model after hs-buddy and relias-assistant scorecard skills |
| 📋 | Low | [#8 Expand .gitignore patterns](#expand-gitignore) | Add .agents/, coverage/, TestResults/ exclusions |
| ✅ | High | #9 Backfill test coverage to 80% | Coverage target met (2026-05-05) |
| ✅ | High | #7 Add CRAP score tooling | ReportGenerator + coverlet configured (2026-05-05) |
| ✅ | Medium | #5 Add husky + lint-staged pre-commit hooks | Pre-commit hooks active (2026-05-06) |
| ✅ | High | Fix popup restart position regression | RestoreState before Show(); SaveWindowState uses savedLeft/savedTop; defer EnsureOnScreen to Loaded (2026-05-05) |
| ✅ | Low | #10 Add markdownlint-cli | markdownlint-cli added, CI step enforces, all docs pass (2026-05-04) |
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

**Completed: 13 / 17** (76%)

---

## Remaining Items

### Add mutation testing

**Problem:** Tests may have weak assertions. Mutation testing verifies test quality by mutating code and checking if tests catch the change.

**Solution:** Install `Stryker.NET` (`dotnet tool install dotnet-stryker`) and run per-project.

**Target:** ≥ 80% mutation score.

---

### Create scorecard skill

**Problem:** No scorecard skill exists to track org-metrics maturity.

**Solution:** Model after `hs-buddy/.agents/skills/scorecard/` and `relias-assistant/.agents/skills/scorecard/`. Create `.agents/skills/scorecard/SKILL.md` with status + improve commands.

---

### Expand .gitignore

**Problem:** `.gitignore` is missing patterns for new tooling artifacts.

**Solution:** Add `.agents/`, `coverage/`, `TestResults/`, `.stryker/`, and other tool outputs.