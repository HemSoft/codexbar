---
# SFL V6.0.0
description: |
  This workflow runs a repository audit to detect documentation drift,
  stale artifacts, configuration hygiene issues, and cross-reference mismatches.
  It creates exactly one agent-fixable issue containing all findings and
  detailed fix instructions for the SFL pipeline to process.

# MOTHERREPO: Automatic triggers disabled. Consumer repos get full triggers via installer.
# Original trigger:
#   schedule:
#     - cron: "0 8 * * *"  # 4:00 AM EDT
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
    title-prefix: "[repo-audit] "
    labels: [sfl-issue]
    max: 1
  update-issue:
    target: "*"
    max: 5
  add-comment:
    target: "*"
    max: 1
---

# Repo Audit

Run a high-signal repository audit. Produce **exactly one issue**
containing all findings with detailed, step-by-step fix instructions. This
single issue enters the SFL pipeline and must give the Issue Processor
everything it needs to implement all fixes in one pass.

## Step 0 — Close previous repo-audit issues

Before creating today's audit, search for all **open** issues whose title
starts with `[repo-audit]`. For each one found, determine whether it has
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

- Detect documentation vs implementation drift
- Surface stale/dead artifacts and outdated references
- Identify configuration or dependency hygiene risks
- Detect stale agent-fix branches from merged or orphaned PRs
- Recommend small, prioritized next actions

## Audit Scope

1. Documentation Drift
   - README/docs structure and claims vs actual files and behavior
   - Broken or outdated internal references

2. Configuration Hygiene
   - Config/env keys that appear unused or mismatched
   - Potentially stale scripts, settings, or dependency declarations

3. Artifact Staleness
   - Deprecated, orphaned, or no-longer-relevant files/folders

4. Cross-Reference Accuracy
   - Mismatches between type/config docs and real usage patterns

5. React Health (react-doctor)
   - Run `npx -y react-doctor@latest . --yes` if a `package.json` with React
     dependencies exists at repo root
   - Parse the terminal output for errors and warnings
   - Group findings by category (Accessibility, Dead Code, State & Effects,
     Performance, Security, Architecture)
   - **False positive exclusion**: Files under `electron/` are Electron main
     process files — they are NOT part of the React import graph. Ignore any
     "Unused file" findings for `electron/**` paths.
   - **False positive exclusion**: Hook files consumed indirectly through
     wrapper hooks are NOT dead code. `src/hooks/useConvex.ts` exports are
     consumed by `src/hooks/useConfig.ts` which re-exports them as
     higher-level hooks. Hooks imported only by `App.tsx` are entry-point
     hooks by design. Verify the full import chain before flagging any
     `src/hooks/` file as unused.

6. Workflow Scheduling Hygiene
   - **Duplicate names**: Two or more `.yml` files in `.github/workflows/` that
     share the same `name:` value — they will appear identical in the Actions UI
     and may indicate redundant work
   - **Overlapping cron schedules**: Workflows whose cron expressions produce
     runs at the same minute, suggesting accidental double-execution (compare
     expanded cron minute/hour sets for collisions)
   - **Overlapping concerns**: A `.lock.yml` (gh-aw compiled) AND a standalone
     `.yml` that perform the same logical task — one should be removed
   - **Orphaned gh-aw prompt files**: A `.md` file in `.github/workflows/`
     whose corresponding `.lock.yml` no longer exists
   - **Stale `.disabled` files**: A `.lock.yml.disabled` sitting alongside an
     active `.lock.yml` or `.yml` of the same base name — the disabled copy
     should be deleted to avoid confusion

7. Branch Hygiene
   - List all remote branches matching the pattern `agent-fix/*`
   - For each branch, determine its status by searching for a pull request
     whose `head.ref` matches the branch name:
     - **Merged**: A merged (closed) PR exists for the branch → the branch is
       safe to delete. Include PR number and merge date.
     - **Orphaned**: No pull request (open or closed) references the branch →
       the branch has no associated work. Include branch creation date if
       available.
     - **Active draft**: An open draft PR exists for the branch → skip it,
       this branch is in use by the SFL pipeline.
     - **Open non-draft**: An open non-draft PR exists → skip it, this branch
       is under review.
   - Only report branches in the **Merged** or **Orphaned** categories.
     Active branches are healthy and should be excluded from findings.
   - gh-aw safe-outputs cannot delete branches, so fix instructions should
     state: "Delete branch `<name>` via the GitHub UI or CLI
     (`git push origin --delete <branch>`)."
   - Group all stale branches into a single finding with a table of branch
     names, statuses, evidence (PR link or "no PR found"), and recommended
     action.
   - Mark this finding as **Category: Manual** — the Issue Processor cannot
     delete branches via safe-outputs. The finding is informational and
     should be listed after all actionable findings in the issue body.

## Output — Single Consolidated Issue

Create **exactly one issue** using `create_issue`. The safe-output config
automatically adds the `[repo-audit]` title prefix and `sfl-issue` label.

If the audit found zero actionable findings, do NOT create an issue — skip
straight to the activity log entry (Process step 6) and report `0 findings`.

### Title

`Repo Audit — YYYY-MM-DD`

(The `[repo-audit]` prefix is added automatically.)

### Risk in body

Each finding already includes a severity and risk assessment in its body text.
Do NOT apply any risk labels (`risk:low`, `risk:medium`, etc.) — the only
label applied is `sfl-issue` (enforced by safe-outputs).

For Medium or High risk findings, include a **Risk Acknowledgment** line in the
issue body stating what could go wrong and why the fixes are still worth pursuing.

### Issue body structure

The issue body must give the Issue Processor a complete, self-contained
implementation guide. Use this exact structure:

```markdown
## Summary

<Executive summary — overall repo health, total findings count,
estimated total lines changed>

## Findings

<For EACH finding, create a numbered section:>

### Finding 1: <short description>

- **File(s)**: `<file path>` (lines X–Y)
- **Category**: <e.g., Documentation Drift, Dead Code, Configuration, Accessibility>
- **Severity**: <low / medium / high>
- **Risk**: low/medium/high — <one-line justification>

**Problem**: <What is wrong and why it should be fixed>

**Fix**: <Precise, unambiguous instructions — what to delete, rename, update,
or rewrite. Include exact code when the change is non-obvious.>

<Repeat for each finding>

## Implementation Order

Apply changes in this order to avoid conflicts:

1. <file:line — brief description>
2. <file:line — brief description>
...

## Acceptance Criteria

- [ ] <Criterion 1 — e.g., "no TypeScript errors after changes">
- [ ] <Criterion 2 — e.g., "removed file is not referenced anywhere">
- [ ] <Criterion 3>
...
```

Formatting requirements for the issue body:

- Leave a blank line after every heading.
- Leave a blank line before and after every list.
- Leave a blank line before and after every fenced code block.
- Do NOT collapse headings, paragraphs, and lists onto the same line.
- Prefer plain Markdown over decorative formatting.

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

## Process

1. Inspect repository file structure and key docs
2. Cross-check docs/config claims against implementation
3. Compile findings with severity, confidence, and agent-fixability assessment
4. Filter out findings that require human judgment or broad refactors
5. Close any previous open `[repo-audit]` issues
6. Create the single consolidated issue (or skip if zero findings)
7. Rely on the `sfl-issue` label (applied automatically by safe-outputs)
   to trigger the SFL Dispatcher via the `issues: labeled` event. Do NOT dispatch
   the processor explicitly from this workflow.
8. Post activity log entry to **Discussion #95** using `add_comment` with `issue_number`: `95` and `body`: `YYYY-MM-DD h:mm AM/PM EDT | Repo Audit | Audit | ✅ N findings` or `⏭️ 0 findings — no issue created`; use `EST` instead of `EDT` only when standard time is actually in effect

Timestamp rule for Discussion #95 entries:

- Convert the current workflow time to `America/New_York` before writing the log line.
- Use the converted local **date and time**, not the UTC date.
- Use `EDT` when daylight saving time is in effect and `EST` otherwise.
- Valid: `2026-03-08 10:56 PM EDT | ...`
- Invalid: `2026-03-09 2:56 AM EST | ...` when the workflow ran at `2026-03-09T02:56:00Z`
