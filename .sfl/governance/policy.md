# Set it Free — Governance Policy

**Version**: 6.0.0
**Updated**: 2026-05-17
**Status**: Active

This document defines the operational boundaries and decision authority for the
**Set it Free Loop** — a label-driven automation system that implements fixes,
validates them through CI and Copilot review, and promotes PRs for human merge.

---

## 1. Label Taxonomy

### Pipeline labels (V6 state machine)

| Label | On | Color | Purpose |
|-------|-----|-------|---------|
| `sfl-issue` | Issue | `#1a7f37` green | Trigger: kicks off processor |
| `sfl-ready-for-review` | PR | `#0075ca` blue | Awaiting Copilot review |
| `sfl-needs-work` | PR | `#e99695` pink | PR needs fixes (CI, threads, CR) |
| `sfl-processing` | PR | `#FBCA04` yellow | Processor running (in-flight lock) |
| `sfl-done` | PR | `#6f42c1` purple | Automation done — human merge |

### Identity labels

| Label | On | Color | Purpose |
|-------|-----|-------|---------|
| `agent:pr` | PR | `#1a7f37` green | Identifies SFL-managed PRs |
| `agent:pause` | Issue/PR | `#e3771a` orange | Halt automation |
| `agent:human-required` | Issue/PR | `#d73a4a` red | Exceeds automation boundary |

### Risk labels (optional, co-applied)

| Label | Color | Purpose |
|-------|-------|---------|
| `risk:trivial` | `#cfd3d7` gray | Formatting, typos, doc updates |
| `risk:low` | `#0e8a16` green | Dep bumps, safe refactors |
| `risk:medium` | `#fbca04` yellow | Logic changes, new features |
| `risk:high` | `#e11d48` red | Auth, payments, data migrations |

### Utility labels

| Label | Purpose |
|-------|---------|
| `no-agent` | Opt out of all SFL automation |
| `report` | Informational only — automation ignores |

---

## 2. State Machine

```text
Issue + sfl-issue
  → Dispatcher validates → dispatches processor → consumes label

Processor completes (workflow_run)
  → Reactor TRANSITION: success → sfl-ready-for-review + request Copilot review
                         failure → sfl-needs-work

Copilot reviews PR (branch ruleset auto-trigger)

Reactor EVALUATE on pull_request_review / CI workflow_run
  ├─ CI green + 0 unresolved threads + review OK → sfl-done  ✅
  ├─ Clean Copilot review + stale threads → sfl-done          ✅
  ├─ Issues found → sfl-needs-work                            🔄
  └─ Cycle ≥ 20 → agent:human-required                        ⚠️

Reactor DISPATCH on sfl-needs-work labeled
  → Sets sfl-processing lock
  → Dispatches processor fix-cycle

Human reviews sfl-done PR and merges
```

---

## 3. Retry Policy

| Max fix cycles | 20 |
|---------------|---|
| **Cycle 1–19** | Reactor dispatches processor automatically |
| **Cycle 20** | Final attempt — if still failing, escalate |
| **Escalation** | `agent:human-required` label applied, no further automation |

---

## 4. Merge Authority

| Condition | Who merges |
|-----------|-----------|
| `sfl-done` label present | Human reviewer |
| `risk:high` or above | Human with domain knowledge |
| All other cases | Any team member |

Auto-merge is not enabled. All PRs require human approval.

---

## 5. Safe Write Boundaries

The following paths are **never** modified by automation without human approval:

| Category | Paths |
|----------|-------|
| Auth & secrets | `**/auth/**`, `**/oauth/**`, `**/.env*` |
| Payment flows | `**/payment/**`, `**/billing/**` |
| Data migrations | `**/migrations/**`, `**/seeds/**` |
| CI/CD config | `.github/workflows/**` |
| Lock files | `package-lock.json`, `yarn.lock`, `bun.lockb` |

If the processor touches a prohibited path, `agent:human-required` is applied.

---

## 6. Opt-Out

- **Per issue**: Apply `no-agent` label
- **Per repo**: Add `.buddy-no-agent` file to repo root
- **Per directory**: Add `.buddy-no-agent` file to any directory

---

## 7. Governance Review Cadence

| Frequency | Activity |
|-----------|----------|
| Weekly | Review `agent:human-required` issues |
| Monthly | Review cost per resolved issue, false positive rate |
| Quarterly | Update retry thresholds and merge authority |

---

## References

- [README.md](../../README.md) — Project overview
- [CATALOG.md](../../CATALOG.md) — Workflow registry
