---
# SFL V6.0.0
description: |
  This workflow identifies code simplification opportunities, implements
  the changes, and creates a pull request with targeted improvements.

on:
  workflow_dispatch:

permissions:
  contents: read
  issues: read
  pull-requests: read

engine:
  id: copilot
  model: claude-opus-4.6

network: defaults

tools:
  github:
    lockdown: false
  edit:
  bash: true

safe-outputs:
  github-app:
    client-id: ${{ vars.SFL_APP_CLIENT_ID }}
    private-key: ${{ secrets.SFL_APP_PRIVATE_KEY }}
  create-pull-request:
    allow-workflows: true
    protected-files: allowed
  update-pull-request:
  push-to-pull-request-branch:
    allow-workflows: true
    protected-files: allowed
  add-labels:
source: relias-engineering/ai-workflows/.github/workflows/simplisticate-pr.md@6ec7e6b6f60e3bae9de55ff1101cad13c17a1aec
---

# Simplisticate PR

> *"Perfection is achieved not when there is nothing more to add, but when there is nothing left to take away."* — Antoine de Saint-Exupéry

## Required Configuration

This workflow uses a GitHub App for safe-outputs (creating PRs, pushing
branches, adding labels). The following must be configured in the target
repository or organization:

| Kind | Name | Description |
| ---- | ---- | ----------- |
| Variable | `SFL_APP_CLIENT_ID` | Client ID of the GitHub App used for safe-outputs authentication |
| Secret | `SFL_APP_PRIVATE_KEY` | Private key (PEM) of the GitHub App used to mint installation tokens |

Set these under **Settings → Secrets and variables → Actions** in the
repository (or organization) where the workflow runs. If they are missing or
misconfigured, the token minting step will fail gracefully
(`continue-on-error`) and subsequent steps will fall back to
`GH_AW_GITHUB_TOKEN` / `GITHUB_TOKEN` where available.

Scan the codebase for code simplification opportunities. Pick the **single
best candidate** — the highest-signal, lowest-risk simplification — implement
the fix directly, and create a pull request with the changes.

## Goals

- Identify unnecessary complexity that can be safely reduced
- Surface dead code, unused abstractions, and over-engineering
- Find duplicated logic that can be consolidated
- Recommend small, targeted simplifications with risk assessment

## Complexity Signals to Detect

| Signal | Description |
| ------ | ----------- |
| Deep nesting | >3 levels of indentation |
| Long methods | Functions >30 lines |
| Too many parameters | 4+ parameters |
| Excessive abstractions | Interfaces with single implementations |
| Duplicated logic | Similar code in multiple places |
| Complex conditionals | Nested if/else, long switch statements |
| Over-engineering | Patterns where simpler solutions exist |
| Dead code | Unused variables, methods, imports, files |
| Tangled dependencies | Circular or convoluted dependency chains |
| Magic values | Hardcoded numbers/strings without explanation |

## Audit Scope

1. **Dead Code & Unused Exports**
   - Unused variables, functions, imports, and type exports
   - Files that are never referenced from any other file
   - Config keys or env vars that appear unused

2. **Over-Engineering**
   - Abstractions with only one implementation
   - Wrapper functions that add no value
   - Unnecessary indirection layers
   - Premature generalization

3. **Duplication**
   - Repeated logic blocks across files
   - Copy-paste patterns that should be consolidated
   - Near-identical utility functions

4. **Excessive Complexity**
   - Deeply nested control flow (>3 levels)
   - Functions with too many responsibilities
   - Complex boolean expressions that could be simplified
   - Long parameter lists

5. **Stale Patterns**
   - Legacy patterns when modern alternatives exist
   - Verbose code that can leverage newer language/framework features
   - Unnecessary defensive coding against impossible scenarios

## Output — Pull Request

After identifying and implementing the best simplification candidate, create
a pull request using `create_pull_request` and push the changes with
`push_to_pull_request_branch`.

### Branch naming

`simplisticate/<short-description>` — e.g., `simplisticate/inline-unused-wrapper`

### PR title

`[simplisticate] <short description of the change>`

### PR body structure

```markdown
## Summary

<What was simplified and why>

## Changes

- <File and what changed>
- <File and what changed>

## Complexity Signals Addressed

| Signal | Location | Before | After |
| ------ | -------- | ------ | ----- |
| <signal> | `<file:line>` | <description> | <description> |

## Risk Assessment

🟢/🟡/🔴 <risk level> — <justification>

## Verification

- [ ] No build/lint errors introduced
- [ ] No behavioral changes to public APIs
- [ ] All existing tests still pass
```

### What NOT to include in a PR

- Changes that alter external/user-facing behavior
- Refactors spanning more than 3 files
- Architectural changes with multiple valid approaches
- Changes that require human judgment to validate correctness
- **Removing or weakening CI/CD quality gates** — linters, formatters, test
  runners, security scanners, and other CI jobs must never be removed, even
  when they currently find nothing to act on (e.g., a PowerShell linter in a
  repo with no `.ps1` files). These are guardrails that protect against future
  regressions and their presence is intentional.

### Dead code false-positive guardrails

Before flagging any export as dead code, verify it has **zero consumers
across the entire `src/` directory**, including indirect consumption through
other hooks, wrapper modules, or re-export chains.

Known patterns that cause false positives:

- **Hook-to-hook chains**: `src/hooks/useConvex.ts` exports are consumed by
  `src/hooks/useConfig.ts`, which wraps them into higher-level hooks.
  A grep for direct component imports will miss this. Always check whether
  another hook file imports the export before flagging it as dead.
- **Entry-point hooks**: Hooks imported only by `App.tsx` (e.g., `usePrefetch`,
  `useBackgroundStatus`, `useAppAppearance`) are application lifecycle hooks —
  a single consumer does not make them dead.
- **Convex wrapper layer**: All exports from `src/hooks/useConvex.ts` are thin
  wrappers around Convex `useQuery`/`useMutation` and are consumed either
  directly by components or indirectly via `useConfig.ts`. Never flag
  `useConvex.ts` exports as dead without verifying the full import chain.
- **Type-only exports**: For `export type` or `export interface` declarations
  that are used only within their own file, do not flag as a finding —
  removing `export` from a file-private type is too low-signal.

## Process

1. Inspect repository file structure and identify key source directories
2. Scan source files for complexity signals
3. Cross-reference findings with test coverage and usage patterns
4. Assess risk of each potential simplification
5. Select the **single best candidate** — highest signal, lowest risk, ≤3 files
6. Implement the fix using `edit` and/or `bash`
7. Verify the change doesn't break anything (run existing linters/tests if available)
8. Create a pull request with a clear description of what changed and why
