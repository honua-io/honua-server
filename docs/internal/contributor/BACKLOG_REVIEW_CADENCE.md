# Weekly Backlog Review Cadence

A repeatable weekly operating rhythm that keeps the honua-server backlog triaged, scope intentional, and completed work closed promptly. The owner (or designated reviewer) works through this checklist once per week and posts the outcomes as a dated GitHub comment.

## Backlog Review

- [ ] New issues triaged (`area/*`, `cap/*`, `priority/*`, `effort/*`, `phase/*`, assignee, milestone).
- [ ] Next 2 weeks have enough `ready-to-start` work.
- [ ] Blocked issues have explicit dependency notes.

## Capability Labels (`cap/*`)

Child of #2892/#2896. Issues carry `area/*` (code area) and `edition/*` (entitlement tier) labels, but neither ties a ticket to the customer-facing *capability* it affects. The `cap/<category>` namespace closes that gap so the backlog can be filtered per-capability and the evidence graph can show honest per-capability "known gaps & roadmap" data.

- **Namespace**: one label per capability category, e.g. `cap/editing`, `cap/geocoding`, `cap/serve-ogc`. A category groups related capability keys (see below); it is coarser than an exact FeatureCatalog entitlement key.
- **Source of truth (today)**: [`docs/gis/data/capability-categories.seed.json`](../../gis/data/capability-categories.seed.json), a small hand-committed seed listing the 20 categories from `FeatureCatalog.Categories` (`src/Honua.Core/Features/Licensing/Domain/FeatureCatalog.cs`) plus 5 Community protocol-serving families that have no entitlement key (`serve-geoservices`, `serve-ogc`, `serve-tiles`, `serve-odata`, `serve-stac`). This is a **loosely-coupled placeholder**: once honua-server#2893 publishes the canonical `capability-keys.v1.json` artifact, the sync workflow's `seed_path` moves to point at that artifact instead, with category slugs held stable across the swap.
- **Sync workflow**: [`.github/workflows/label-sync.yml`](../../../.github/workflows/label-sync.yml) reads the seed list and ensures a `cap/<category>` label exists for every entry — creating missing labels and updating the description/color of existing ones. It **never deletes or renames** a label, and labels are **never hand-created**. `workflow_dispatch` defaults to `apply=false` (dry run — prints the create/update diff only); a push that changes the seed file also always runs dry-run. Only an explicit `apply=true` dispatch mutates labels.
- **Issue-form field**: `bug.yml` and `feature.yml` carry an optional "Capability Key(s)" text input (comma-separated, e.g. `editing.feature-edits, geocoding.single-line`); `tech-debt.yml` carries the same field but it's clearly optional there. When a bug/feature issue is opened without a parseable, recognized key, [`.github/workflows/issue-capability-check.yml`](../../../.github/workflows/issue-capability-check.yml) posts a one-time advisory comment with guidance — it never labels, closes, or blocks the issue.
- **Applying the label**: the sync workflow only manages label *existence*, not which issues carry them. Apply `cap/<category>` to an issue by hand (or via the one-time backfill triage pass, honua-server#2896 acceptance criterion 3, tracked separately) based on the "Capability Key(s)" field's category prefix.

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
- [ ] New issues triaged (`area/*`, `cap/*`, `priority/*`, `effort/*`, `phase/*`, assignee, milestone).
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
