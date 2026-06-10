# Weekly Backlog Review Cadence

A repeatable weekly operating rhythm that keeps the honua-server backlog triaged, scope intentional, and completed work closed promptly. The owner (or designated reviewer) works through this checklist once per week and posts the outcomes as a dated GitHub comment.

## Backlog Review

- [ ] New issues triaged (`area/*`, `priority/*`, `effort/*`, `phase/*`, assignee, milestone).
- [ ] Next 2 weeks have enough `ready-to-start` work.
- [ ] Blocked issues have explicit dependency notes.

## Scope Gate

- [ ] New scope has an explicit tradeoff (what was deferred/removed).
- [ ] MVP/Beta/GA mix is still intentional for current goals.
- [ ] Oversized tickets (`effort/XL`) are split or explicitly accepted.

## Done/Close Hygiene

- [ ] Completed work is closed within 24 hours.
- [ ] Partially complete work has a comment with exact remaining tasks.
- [ ] Stale items are rephased or closed.

## Cadence

- **Frequency**: weekly (recommended). Adjust to project pace as needed.
- **Owner**: posts a dated weekly comment with outcomes and decisions.
- **Escalation**: unresolved cross-repo blockers are escalated to `honua-server` or `honua-devops` as appropriate.

## Weekly Comment Template

Copy the block below into a new issue comment each week:

```markdown
## Backlog Review — YYYY-MM-DD

### Backlog Review
- [ ] New issues triaged (`area/*`, `priority/*`, `effort/*`, `phase/*`, assignee, milestone).
- [ ] Next 2 weeks have enough `ready-to-start` work.
- [ ] Blocked issues have explicit dependency notes.

### Scope Gate
- [ ] New scope has an explicit tradeoff (what was deferred/removed).
- [ ] MVP/Beta/GA mix is still intentional for current goals.
- [ ] Oversized tickets (`effort/XL`) are split or explicitly accepted.

### Done/Close Hygiene
- [ ] Completed work is closed within 24 hours.
- [ ] Partially complete work has a comment with exact remaining tasks.
- [ ] Stale items are rephased or closed.

### Outcomes & Decisions
<!-- Summarize what changed, what was escalated, and any decisions made. -->

### Cross-Repo Blockers
<!-- List any blockers escalated to honua-server or honua-devops. -->
- None
```
