---
# SFL V6.0.0
description: |
  SFL Processor — Processes an issue by creating or updating a PR
  with the implementation. Triggered by the SFL dispatcher when the
  sfl-issue label is applied to an issue, or by the reactor when a
  fix-cycle is needed.

on:
  workflow_call:
    inputs:
      issue-number:
        description: Target issue number
        required: true
        type: string
      pr-number:
        description: Existing PR number (fix-cycle mode)
        required: false
        type: string
  workflow_dispatch:
    inputs:
      issue-number:
        description: Target issue number
        required: true
        type: string
      pr-number:
        description: Existing PR number (fix-cycle mode)
        required: false
        type: string

permissions:
  checks: read
  contents: read
  issues: read
  pull-requests: read

checkout:
  fetch-depth: 0
  fetch: ["*"]

steps:
  - name: Fetch all remote branches and configure git auth
    env:
      FETCH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      SERVER_URL: ${{ github.server_url }}
    run: |
      header=$(printf "x-access-token:%s" "${FETCH_TOKEN}" | base64 -w 0)
      git -c "http.extraheader=Authorization: Basic ${header}" fetch origin '+refs/heads/*:refs/remotes/origin/*'
      echo "GIT_CONFIG_COUNT=1" >> "$GITHUB_ENV"
      echo "GIT_CONFIG_KEY_0=http.${SERVER_URL}/.extraheader" >> "$GITHUB_ENV"
      echo "GIT_CONFIG_VALUE_0=Authorization: Basic ${header}" >> "$GITHUB_ENV"

  - name: Precompute review context
    env:
      GH_TOKEN: ${{ github.token }}
      PR_NUMBER: ${{ inputs.pr-number }}
      ISSUE_NUMBER: ${{ inputs.issue-number || github.event.inputs.issue-number }}
    run: |
      set -euo pipefail
      mkdir -p /tmp/gh-aw/agent

      # Validate issue-number is digits only (defense-in-depth against
      # jq injection — the value is interpolated into a jq test() regex).
      if ! [[ "$ISSUE_NUMBER" =~ ^[0-9]+$ ]]; then
        echo "::error::Invalid issue-number: must be digits only"
        exit 1
      fi

      # Validate pr-number input is digits only when explicitly provided
      # (defense-in-depth — mirrors issue-number validation above).
      if [ -n "$PR_NUMBER" ] && ! [[ "$PR_NUMBER" =~ ^[0-9]+$ ]]; then
        echo "::error::Invalid pr-number: must be digits only"
        exit 1
      fi

      # If no PR number provided, try to discover an existing agent:pr PR
      # for this issue so the precomputed context is available for both
      # fix-cycle entry paths (explicit pr-number and auto-discovered PR).
      # Uses the SFL issue-resolution order: Closes/Fixes/Resolves #N,
      # **Linked Issue**: #N, then branch name agent-fix/issue-N-*.
      if [ -z "$PR_NUMBER" ]; then
        MATCHES=$(gh pr list --repo "${{ github.repository }}" \
          --label "agent:pr" --state open --json number,body,headRefName \
          --jq "[.[] | select(
            (.body | test(\"(?i)(closes|fixes|resolves)\\\\s+#${ISSUE_NUMBER}\\\\b\"))
            or (.body | test(\"\\\\*\\\\*Linked Issue\\\\*\\\\*:\\\\s*#${ISSUE_NUMBER}\\\\b\"))
            or (.headRefName | test(\"^agent-fix/issue-${ISSUE_NUMBER}-\"))
          )]")
        MATCH_COUNT=$(echo "$MATCHES" | jq 'length')
        if [ "$MATCH_COUNT" -eq 0 ]; then
          echo "No existing PR found — new implementation mode"
          exit 0
        elif [ "$MATCH_COUNT" -gt 1 ]; then
          echo "::error::Multiple agent:pr PRs found for issue #${ISSUE_NUMBER} — cannot auto-select"
          exit 1
        fi
        PR_NUMBER=$(echo "$MATCHES" | jq -r '.[0].number')
        echo "Discovered existing PR #${PR_NUMBER} for issue #${ISSUE_NUMBER}"
      fi

      # Validate pr-number is digits only when provided or discovered
      # (defense-in-depth against injection via GraphQL variables and REST paths).
      if [ -n "$PR_NUMBER" ] && ! [[ "$PR_NUMBER" =~ ^[0-9]+$ ]]; then
        echo "::error::Invalid pr-number: must be digits only"
        exit 1
      fi

      # Fetch unresolved, non-outdated review threads (paginated)
      CURSOR=""
      THREADS_JSON="[]"
      while true; do
        AFTER_ARG=""
        if [ -n "$CURSOR" ]; then
          AFTER_ARG="-f after=${CURSOR}"
        fi
        PAGE=$(gh api graphql -f query='
          query($owner: String!, $repo: String!, $pr: Int!, $after: String) {
            repository(owner: $owner, name: $repo) {
              pullRequest(number: $pr) {
                headRefName
                reviewThreads(first: 100, after: $after) {
                  totalCount
                  pageInfo { hasNextPage endCursor }
                  nodes {
                    isResolved
                    isOutdated
                    id
                    comments(first: 50) {
                      totalCount
                      nodes {
                        id
                        databaseId
                        author { login }
                        body
                        path
                        line
                      }
                    }
                  }
                }
              }
            }
          }' -f owner="${{ github.repository_owner }}" \
             -f repo="${{ github.event.repository.name }}" \
             -F pr="${PR_NUMBER}" \
             ${AFTER_ARG})

        # On first page, capture branch name and save raw for later
        if [ -z "$CURSOR" ]; then
          echo "$PAGE" > /tmp/review_raw.json
        fi

        # Append nodes to accumulated array
        PAGE_NODES=$(echo "$PAGE" | jq '[.data.repository.pullRequest.reviewThreads.nodes[]]')
        THREADS_JSON=$(echo "$THREADS_JSON" "$PAGE_NODES" | jq -s '.[0] + .[1]')

        HAS_NEXT=$(echo "$PAGE" | jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.hasNextPage')
        if [ "$HAS_NEXT" != "true" ]; then
          break
        fi
        CURSOR=$(echo "$PAGE" | jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.endCursor')
      done

      # Extract branch name
      BRANCH=$(jq -r '.data.repository.pullRequest.headRefName' /tmp/review_raw.json)
      echo "PR branch: ${BRANCH}"

      # Build structured review context from accumulated threads
      echo "$THREADS_JSON" | jq --arg pr_number "$PR_NUMBER" --arg branch "$BRANCH" '{
        pr_number: ($pr_number | tonumber),
        branch: $branch,
        unresolved_threads: [
          .[]
          | select(.isResolved == false and .isOutdated == false)
          | {
              thread_id: .id,
              comments: [.comments.nodes[] | {
                comment_id: .databaseId,
                author: .author.login,
                body: .body,
                path: .path,
                line: .line
              }]
            }
        ]
      }' > /tmp/gh-aw/agent/review-context.json

      THREAD_COUNT=$(jq '.unresolved_threads | length' /tmp/gh-aw/agent/review-context.json)
      echo "Fix-cycle mode: PR #${PR_NUMBER}, branch=${BRANCH}, unresolved_threads=${THREAD_COUNT}"

      # Fetch CHANGES_REQUESTED review bodies for the current head commit.
      # When a reviewer submits CHANGES_REQUESTED with no inline threads,
      # the review body is the only actionable context for the implementer.
      # Filter to opinionated states (APPROVED/CHANGES_REQUESTED) before
      # grouping so a follow-up COMMENTED review cannot mask an earlier
      # change request.
      HEAD_SHA=$(gh api "repos/${{ github.repository }}/pulls/${PR_NUMBER}" --jq '.head.sha')
      gh api "repos/${{ github.repository }}/pulls/${PR_NUMBER}/reviews" \
        --paginate --jq '.[]' \
        | jq -s --arg sha "$HEAD_SHA" '
          [.[] | select(.commit_id == $sha and (.state == "APPROVED" or .state == "CHANGES_REQUESTED"))]
          | group_by(.user.login)
          | map(sort_by(.submitted_at) | last)
          | [.[] | select(.state == "CHANGES_REQUESTED" and .body != "")]
          | map({author: .user.login, body: .body, submitted_at: .submitted_at})' \
        > /tmp/gh-aw/agent/changes-requested-reviews.json

      # Merge review bodies into review-context.json so the agent has
      # complete context even when there are no inline threads.
      REVIEW_BODIES=$(cat /tmp/gh-aw/agent/changes-requested-reviews.json)
      jq --argjson reviews "$REVIEW_BODIES" '. + {changes_requested_reviews: $reviews}' \
        /tmp/gh-aw/agent/review-context.json > /tmp/gh-aw/agent/review-context.tmp.json \
        && mv /tmp/gh-aw/agent/review-context.tmp.json /tmp/gh-aw/agent/review-context.json

      CR_COUNT=$(jq '.changes_requested_reviews | length' /tmp/gh-aw/agent/review-context.json)
      echo "CHANGES_REQUESTED review bodies: ${CR_COUNT}"

      # Fetch failed check run logs (summary only, paginated, includes all
      # non-success conclusions that trigger a fix cycle).
      # Exclude the reactor's own "react" check run to mirror the decision
      # logic in sfl-review-reactor.yml — a prior reactor failure is
      # self-referential and should not appear as an actionable failure
      # for the implementer agent.
      gh api "repos/${{ github.repository }}/commits/${HEAD_SHA}/check-runs" \
        --paginate --jq '.check_runs[]' \
        | jq -s '[.[] | select(.name != "react") | select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out" or .conclusion == "action_required" or .conclusion == "startup_failure" or .conclusion == "stale") | {name, conclusion, output_title: .output.title, output_summary: .output.summary}]' \
        > /tmp/gh-aw/agent/failed-checks.json

      FAIL_COUNT=$(jq 'length' /tmp/gh-aw/agent/failed-checks.json)
      echo "Failed checks: ${FAIL_COUNT}"
      cat /tmp/gh-aw/agent/review-context.json

timeout-minutes: 120

engine:
  id: copilot
  model: claude-opus-4.6

network:
  allowed:
    - defaults
    - dotnet

tools:
  github:
    lockdown: false

safe-outputs:
  github-app:
    client-id: ${{ vars.SFL_APP_CLIENT_ID }}
    private-key: ${{ secrets.SFL_APP_PRIVATE_KEY }}
  create-pull-request:
    title-prefix: "[agent-fix] "
    labels: [agent:pr, sfl-ready-for-review]
    max: 1
    protected-files: fallback-to-issue
  push-to-pull-request-branch:
    target: "*"
    title-prefix: "[agent-fix] "
    labels: [agent:pr]
    max: 1
    protected-files: fallback-to-issue
  add-comment:
    target: "${{ inputs.issue-number }}"
    max: 3
  reply-to-pull-request-review-comment:
    target: "*"
    max: 100
  resolve-pull-request-review-thread:
    max: 100
---

# SFL Processor

Process exactly one issue per run. Read the issue, implement the fix, and
create or update a PR.

## Step 1 — Read the issue

Issue number: `${{ inputs.issue-number || github.event.inputs.issue-number }}`

1. Read the issue body and title.
2. Verify the issue is open.
3. Validate the body contains actionable information:
   - A problem statement or finding
   - Implementation direction or fix description
   - Acceptance criteria or verifiable outcomes
4. Accept alternative headings (Goal, Summary, Implementation Plan) when they
   provide equivalent detail. Only exit with a failure comment when the body
   is truly non-actionable.

## Step 2 — Determine mode

Check whether `/tmp/gh-aw/agent/review-context.json` exists (the precompute
step creates it when a PR number is provided as input OR when an existing
`agent:pr` PR is discovered for the issue).

- **If `review-context.json` exists**: this is a **fix-cycle**. Read
  `/tmp/gh-aw/agent/review-context.json` for unresolved review threads and
  CHANGES_REQUESTED review bodies (in the `changes_requested_reviews` array),
  and `/tmp/gh-aw/agent/failed-checks.json` for CI failures. Check out the PR
  branch listed in the review context JSON.
- **If `review-context.json` does not exist**: this is a **new
  implementation**. Continue to Step 3.

## Step 3 — Create the branch (new implementations only)

Determine the repository's default branch from repository metadata, then
create branch `agent-fix/issue-<issue-number>` from that default branch.

For new issues, confirm the described problem actually exists. If already
fixed and no PR exists, post a comment "Already resolved — no changes
needed" and exit without making changes.

## Step 4 — Implement

For **new implementations**: implement the fix based on the issue body.

For **follow-up passes**: address blocking analyzer findings, unresolved
review comments, and CHANGES_REQUESTED review bodies.

Implementation priorities:

1. Issue acceptance criteria and explicit scope
2. Blocking analyzer findings
3. CHANGES_REQUESTED review bodies (top-level reviewer feedback)
4. Unresolved review comments (inline thread feedback)
5. Non-blocking suggestions (only after blocking items are addressed)

Rules:

- Apply the minimal change that satisfies acceptance criteria
- Do not refactor surrounding code
- Do not rename symbols unless that is the stated fix
- Do not add comments, docs, or type annotations to unchanged lines
- Before committing, format changed files with tooling already configured
  in the repo; do not install new formatting tooling

**IMPORTANT**: Do NOT run `git push`. Use the safe outputs in Step 5.

### 4a — Classify and address ALL review threads (follow-up passes only)

Address **every** unresolved review thread from `review-context.json`.
Classify each thread into one of two categories:

**Category A — Correctness** (bugs, security vulnerabilities, resource leaks,
missing error handling, race conditions, null dereferences, async/cancellation
bugs, test failures):
→ Fix the issue in code. Reply with a structured classification:

```text
**Classification**: Correctness
**Fix**: <brief description of what changed and why>
```

Then resolve the thread.

**Category B — Design suggestion** (naming preferences, refactoring proposals,
alternative approaches that are equally valid, style choices, "consider doing X
instead of Y" when both are correct):
→ Reply with a structured classification:

```text
**Classification**: Design suggestion — out of scope
**Rationale**: <why the current implementation is acceptable>
```

Then resolve the thread.

**Classification rules:**

- If a thread alleges runtime failure, data corruption, security risk,
  resource leak, async/cancellation bug, nullability issue, or test failure,
  classify as **Category A** unless you can prove from the code that the
  concern is inapplicable.
- When uncertain, default to Category A and fix the issue.
- Never leave a thread unresolved. Every thread must be either fixed or
  acknowledged with a clear rationale.

For each thread:

1. Reply using `reply_to_pull_request_review_comment` with:
   - `comment_id`: the ID from `review-context.json`
   - `body`: the structured classification reply above

2. Resolve using `resolve_pull_request_review_thread` with:
   - `thread_id`: the `thread_id` from `review-context.json`

### 4b — Self-review diff before push (follow-up passes only)

Before pushing, review your own code changes (diff from the PR base branch)
for patterns that Copilot commonly flags. Check only lines you changed or
added — do not scan unmodified code.

Patterns to check:

- Methods accepting `CancellationToken` but not passing it to async calls
- New `IDisposable` objects not wrapped in `using` or `Dispose`
- Exception handlers that swallow exceptions without logging
- Boolean state flags set but never reset in error/cancellation paths
- Missing null checks on nullable reference types introduced by your changes
- Race conditions in async state management (e.g., shared mutable state
  without synchronization)

Fix any concrete correctness issues found. Keep fixes minimal — do not
refactor, rename, or redesign. The goal is to reduce new Copilot comments
on re-review.

## Step 5 — Create or update the PR

| Scenario | Action |
| --- | --- |
| New implementation (`review-context.json` absent) | `create_pull_request` |
| Fix-cycle (`review-context.json` present) | `push_to_pull_request_branch` with `pull_request_number` = the `pr_number` from `review-context.json` |

For new PRs, include `Closes #<issue-number>` in the PR body.

Before creating a new PR, re-check that no open `agent:pr` PR exists for
the issue. If one exists, stop and report the conflict.

## Step 6 — Post implementation comment

Post a comment on the PR summarizing what was changed and why.

## Guardrails

- Process exactly ONE issue per run
- Never create a second PR for an issue that already has an open `agent:pr` PR
- Never modify the PR body after creation
- All safe-output tools are deferred and execute after this run completes
