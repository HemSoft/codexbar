---
# SFL V6.0.0
description: |
  This workflow runs a daily code simplification audit to identify unnecessary
  complexity, over-engineering, dead code, and opportunities for targeted
  simplification. It creates exactly one agent-fixable issue containing all
  findings and detailed fix instructions for the SFL pipeline to process.

# MOTHERREPO: Automatic triggers disabled. Consumer repos get full triggers via installer.
# Original trigger:
#   schedule: "0 6 * * *"   # 2:00 AM EDT
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

safe-outputs:
  create-issue:
    title-prefix: "[simplisticate] "
    labels: [sfl-issue]
    max: 1
  update-issue:
    target: "*"
    max: 5
  add-comment:
    target: "*"
    max: 1
---

# Simplisticate Audit

> *"Perfection is achieved not when there is nothing more to add, but when there is nothing left to take away."* — Antoine de Saint-Exupéry

Run a high-signal daily code simplification audit. Produce **exactly one issue**
containing all findings with detailed, step-by-step fix instructions. This
single issue enters the SFL pipeline and must give the Issue Processor
everything it needs to implement all fixes in one pass.

## Step 0 — Close previous simplisticate issues

Before creating today's audit, search for all **open** issues whose title
starts with `[simplisticate]`. For each one found, determine whether it has
**active implementation state** before deciding to close it.

Fetch **all** open issues and **all** open pull requests using pagination
(do not assume fewer than 100 total).

An issue has active implementation state if **any** of the following are true:

- The issue carries the `sfl-processing` or `agent:pause` label.
- There is an **open** pull request whose body contains
  `Closes #<issue_number>` or `**Linked Issue**: #<issue_number>`, or whose
  head branch name starts with `agent-fix/issue-<issue_number>-` or equals
  `agent-fix/issue-<issue_number>` exactly.

For each issue:

- **If active implementation state is detected** → skip it. Do NOT close it.
- **If no active state is detected** → close it using `update_issue` with:
  - `issue_number`: the issue number
  - `status`: `"closed"`
- **If uncertain** (e.g., API errors checking PRs) → leave the issue open.
  False negatives (leaving a stale issue open) are harmless; false positives
  (closing an in-progress issue) disrupt the SFL pipeline.

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

## Output — Single Consolidated Issue

Create **exactly one issue** using `create_issue`. The safe-output config
automatically adds the `[simplisticate]` title prefix and `sfl-issue` label.

If the audit found zero actionable findings, do NOT create an issue — skip
straight to the activity log entry (Step 8) and report `0 findings`.

### Title

`Simplification Audit — YYYY-MM-DD`

(The `[simplisticate]` prefix is added automatically.)

### Risk in body

Each finding already includes a severity and risk assessment in its body text.
Do NOT apply any risk labels (`risk:low`, `risk:medium`, etc.) — the only
label applied is `sfl-issue` (enforced by safe-outputs).

For 🟡 Medium or 🔴 High risk findings, include a **Risk Acknowledgment** line in the
issue body stating what could go wrong and why the simplifications are still
worth pursuing.

### Issue body structure

The issue body must give the Issue Processor a complete, self-contained
implementation guide. Use this exact structure:

```markdown
## Summary

<Executive summary — overall code simplicity health, total findings count,
estimated total lines changed>

## Findings

<For EACH finding, create a numbered section:>

### Finding 1: <short description>

- **File(s)**: `<file path>` (lines X–Y)
- **Signal**: <complexity signal from the table above>
- **Severity**: <low / medium / high>
- **Risk**: 🟢/🟡/🔴 <risk level> — <one-line justification>

**Problem**: <What is wrong and why it should be simplified>

**Fix**: <Precise, unambiguous instructions — what to delete, rename, inline,
or rewrite. Include exact code when the change is non-obvious.>

<Repeat for each finding>

## Implementation Order

Apply changes in this order to avoid conflicts:

1. <file:line — brief description>
2. <file:line — brief description>
...

## Acceptance Criteria

- [ ] <Criterion 1 — e.g., "no TypeScript errors after changes">
- [ ] <Criterion 2 — e.g., "removed function is not referenced anywhere">
- [ ] <Criterion 3>
...
```

Formatting requirements for the issue body:

- Leave a blank line after every heading.
- Leave a blank line before and after every list.
- Leave a blank line before and after every fenced code block.
- Do NOT collapse headings, paragraphs, and lists onto the same line.
- Prefer plain Markdown over decorative formatting.

Valid example:

```markdown
## Summary

Overall code simplicity health is fair.

## Findings

### Finding 1: Duplicate helper

- **File(s)**: `src/example.ts`
- **Signal**: Duplicated logic

**Problem**: Two helpers implement the same logic.

**Fix**: Delete one helper and update the call sites.
```

Invalid example:

```markdown
## SummaryOverall code simplicity health is fair.## Findings### Finding 1: Duplicate helper- **File(s)**: `src/example.ts`**Problem**: Two helpers implement the same logic.
```

### Writing effective fix instructions

The Issue Processor is an AI agent that will read this issue and implement
every fix in a single PR. To maximize its success:

- **Be exact**: Specify file paths, line numbers, function names. Say
  "delete lines 45–52 of `src/utils/helpers.ts`" not "remove the helper."
- **Show code**: For non-trivial changes, include before/after code snippets.
- **Order matters**: List an implementation order that avoids merge conflicts
  (e.g., delete from bottom-of-file upward, rename before removing imports).
- **One direction**: Don't offer alternatives. State the single correct change.
- **Scope guard**: Every finding must be scoped to 1–3 files. If a finding
  would touch more than 3 files, split it or exclude it.

### What to exclude

Do NOT include findings that:

- Require architectural decisions with multiple valid approaches
- Would alter external/user-facing behavior
- Need human judgment to resolve (flag these in the summary as informational)

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
5. Filter out findings that require human judgment or broad refactors
6. Compile all actionable findings into one issue with detailed fix instructions
7. Order the fixes to minimize conflicts (bottom-up deletions, etc.)
8. Create the single consolidated issue (or skip if zero findings)
9. Rely on the `sfl-issue` label (applied automatically by safe-outputs)
   to trigger the SFL Dispatcher via the `issues: labeled` event. Do NOT dispatch
   the processor explicitly from this workflow.
10. Post activity log entry to **Discussion #95** using `add_comment` with `issue_number`: `95` and `body`: `YYYY-MM-DD h:mm AM/PM EDT | Simplisticate | Audit | ✅ N findings` or `⏭️ 0 findings — no issue created`; use `EST` instead of `EDT` only when standard time is actually in effect

Timestamp rule for Discussion #95 entries:

- Convert the current workflow time to `America/New_York` before writing the log line.
- Use the converted local **date and time**, not the UTC date.
- Use `EDT` when daylight saving time is in effect and `EST` otherwise.
- Valid: `2026-03-08 10:56 PM EDT | ...`
- Invalid: `2026-03-09 2:56 AM EST | ...` when the workflow ran at `2026-03-09T02:56:00Z`
