---
name: scorecard
description: >
  V1.0 - Commands: status, improve. Self-evaluated repository health scorecard
  with Bronze/Silver/Gold classification. Tracks build, format, coverage, CRAP,
  security, mutation testing, and CI/CD quality gates.
version: "1.0.0"
lastModified: "2026-05-14"
---

# Scorecard — Repository Health Skill

## Overview

The scorecard measures CodexBar repository health across Bronze, Silver, and
Gold tiers. Each tier has rules worth points; passing all rules in a tier earns
that classification. The score is the sum of all passed rule points out of 100.

Unlike org-metrics scorecards that fetch from an external service, this scorecard
is self-evaluated by running quality gates locally.

## Commands

### `scorecard status`

Default command when no arguments are provided. Runs all quality gates and
displays the current scorecard.

#### Steps

1. Run each quality gate (see Rules section below) and collect pass/fail.
2. Calculate points per tier and overall score.
3. Determine classification (must pass ALL rules in a tier to achieve it).
4. Present the report (see template below).
5. Log the score to `score-history.log` in this skill's directory.

#### Score logging rules

- Append one line per evaluation in this format:

  ```text
  YYYY-MM-DD HH:MM ET | Score: X/100 | Classification: Level | Passed: X/Y | Bronze: X/Y | Silver: X/Y | Gold: X/Y
  ```

- Timestamps must be US Eastern Time (use `Get-Date` with timezone conversion).
- Only append if the score or classification changed from the last entry.
- Create the file with a header if it does not exist.

#### Report template

```markdown
## Scorecard Status — CodexBar

**Score**: X / 100 (Classification: None|Bronze|Silver|Gold)

### Tier Breakdown
| Tier   | Passed | Total | Points | Max |
|--------|--------|-------|--------|-----|
| Bronze | X      | Y     | X      | 30  |
| Silver | X      | Y     | X      | 35  |
| Gold   | X      | Y     | X      | 35  |

### Failing Rules
| Tier | Rule | Points | Detail |
|------|------|--------|--------|
| ...  | ...  | ...    | ...    |

### Passing Rules
| Tier | Rule | Points |
|------|------|--------|
| ...  | ...  | ...    |

_Report generated YYYY-MM-DD HH:MM ET_
```

### `scorecard improve`

Analyzes failing rules and recommends the single highest-impact improvement.

#### Steps

1. Run `scorecard status` to get current state.
2. Collect all failing rules.
3. Select the highest-impact fix using this priority:
   - Bronze failures first (required for any classification)
   - Highest points among same tier
   - Lowest complexity as tiebreaker
4. Present the recommendation with specific fix strategy.
5. Ask the user if they want to implement the fix now.

#### Report template

```markdown
## Scorecard Improvement Recommendation

**Current Score**: X / 100 (Classification: None|Bronze|Silver|Gold)

### Recommended Fix

- **Rule**: <Rule Title>
- **Tier**: Bronze|Silver|Gold
- **Points**: X pts
- **Current Detail**: <what the check found>
- **Projected Score**: ~X / 100 after fix

### Fix Strategy

<Specific actionable steps to fix this rule>
```

## Rules Definition

### Bronze Tier (30 points) — Fundamentals

| Rule | Points | Check Command | Pass Criteria |
|------|--------|---------------|---------------|
| Build with zero warnings | 5 | `dotnet build` | Exit code 0, 0 warnings |
| Code format clean | 5 | `dotnet format --verify-no-changes` | Exit code 0 |
| All tests pass | 5 | `dotnet test` | Exit code 0, 0 failures |
| CI/CD workflow exists | 5 | Check `.github/workflows/ci.yml` | File exists and contains build+test steps |
| README exists and non-trivial | 5 | Check `README.md` | File exists, > 50 lines |
| License defined | 5 | Check `LICENSE` | File exists |

### Silver Tier (35 points) — Quality

| Rule | Points | Check Command | Pass Criteria |
|------|--------|---------------|---------------|
| Line coverage ≥ 80% | 10 | `dotnet test --collect:"XPlat Code Coverage"` + parse cobertura | line-rate ≥ 0.80 |
| Branch coverage ≥ 80% | 5 | Same as above | branch-rate ≥ 0.80 |
| Security audit clean | 5 | `dotnet list package --vulnerable` | No vulnerable packages |
| Markdown lint clean | 5 | `npx markdownlint-cli2 "**/*.md" "#node_modules"` | Exit code 0 |
| CRAP score: 0 methods > 30 | 10 | ReportGenerator JSON + parse | No method with CRAP > 30 |

### Gold Tier (35 points) — Excellence

| Rule | Points | Check Command | Pass Criteria |
|------|--------|---------------|---------------|
| Mutation score ≥ 60% | 10 | `dotnet stryker` + parse JSON report | Score ≥ 60% |
| Mutation score ≥ 80% | 10 | Same as above | Score ≥ 80% |
| Line coverage 100% | 5 | Same as Silver coverage | line-rate = 1.00 |
| Branch coverage 100% | 5 | Same as Silver coverage | branch-rate = 1.00 |
| Zero TODO/HACK comments in source | 5 | `grep -r "TODO\|HACK" src/ --include="*.cs"` | 0 matches (excludes test files) |

### Classification logic

- **Gold**: All Bronze + Silver + Gold rules pass
- **Silver**: All Bronze + Silver rules pass
- **Bronze**: All Bronze rules pass
- **None**: Any Bronze rule fails

## Integration with Other Skills

- **perfection**: The `perfection audit` command runs overlapping quality gates.
  The scorecard adds scoring, classification, and improvement recommendations on
  top. Use `perfection fix` to auto-fix failing gates when possible.
- **crap**: Use the `crap` skill for detailed CRAP score analysis if available.

## Notes

- Coverage data requires running tests with `--collect:"XPlat Code Coverage"`.
- CRAP score analysis requires `dotnet-reportgenerator-globaltool`.
- Mutation testing requires `dotnet-stryker` (already configured as local tool).
- The Gold mutation thresholds are intentionally progressive (60% then 80%) to
  reward incremental improvement.
- Score history is tracked locally — there is no external scorecard service for
  this personal repository.
