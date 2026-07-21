# Lean merge-queue runbook (honua-server)

Target architecture for re-enabling GitHub's native merge queue on `honua-server`
**without** the runner-starvation spiral that forced it off on **2026-06-18**. This
runbook covers: the lean `merge_group` gate (landed in `ci.yml` by this PR), the
exact ruleset re-enable steps (a follow-up Ã¢â‚¬â€ **do NOT flip the ruleset in this
PR**), recommended queue sizing, and how the queue interacts with strict branch
protection and `pr-merge-train.yml`.

Companion docs:
- `docs/internal/ci/merge-queue.md` Ã¢â‚¬â€ the original CI-throughput operator runbook
  (shard routing, suite-speed, CI-minutes review). Its Ã‚Â§1 "full lane on
  merge_group" design is **superseded** by this runbook's lean gate.
- `docs/internal/contributor/merge-coordination-runbook.md` Ã¢â‚¬â€ the webhook-driven
  train + flaky-rerun + AI-triage workflows that drain the queue today.

## 1. Why the queue was disabled (root cause)

When the queue was on, the `merge_group` event re-ran the **FULL ~45-job CI
matrix** on the batched commit Ã¢â‚¬â€ because a `merge_group` event is a
non-`pull_request` event, the `changes`/`targeted-shards` jobs took their
`else` (full-CI) branch and escalated to `run_all` (every Server Tests shard +
AOT + docker + Postgres + browser/MCP lanes). With `max_entries_to_build = 5`,
a single batch fanned out to **~225 concurrent jobs**. That starved the runner
pool; the runner-starved `AOT Build Verification` and the long shard matrix
couldn't finish inside `check_response_timeout_minutes`; GitHub **ejected the
front PR**, **reformed the group**, and spawned a **NEW** run **without
cancelling the old** Ã¢â‚¬â€ zombie runs piling up Ã¢â€ â€™ spiral. Ruleset **17808547
"Merge queue (trunk)"** was set to **enforcement: disabled**.

## 2. The fix Ã¢â‚¬â€ a LEAN `merge_group` gate (this PR)

The full matrix already gated each PR on its own `pull_request` run **before**
the PR was approved and entered the queue. Re-running it on the batch is pure
waste. The queue only needs to catch **semantic conflicts from batching** (two
PRs that each compile alone but not together; a format/architecture regression a
merge reintroduces). So `merge_group` now runs **one lean job**:

`Merge Queue Gate` (`merge-queue-gate` in `ci.yml`):
- `if: github.event_name == 'merge_group'`
- Full-solution **build** (warnings-as-errors) + **`dotnet format --verify-no-changes`**
- **Server Fast tier** unit smoke (`Tier=Fast`, no Postgres/integration) +
  **Architecture** enforcement tests
- **NO** AOT, **NO** docker, **NO** Server Tests shard matrix, **NO**
  Postgres/browser/MCP/Python lanes.

Every heavy job and the PR-time aggregator `CI Gate` are `if:`-gated to skip on
`merge_group` (see Ã‚Â§5 for the exact graph). A batch of 5 therefore runs **one
runner job**, not ~225 Ã¢â‚¬â€ it finishes inside the timeout, so the front PR is
never ejected and the group never reforms into a zombie.

### Jobs on `merge_group` vs `pull_request`

| Lane | Trigger | Jobs that run | Scope |
|------|---------|---------------|-------|
| **PR** | `pull_request` | the full graph: `pr-template-check`, `pr-readiness`, `changes`, `targeted-shards`, `ci-router-validation`, `build`, `dotnet-foundation-tests`, `server-tests` (selective shard subset), `python-integration-tests`, `js-*`, `esri-leaflet-browser-tests`, `maplibre-compat`, `mcp-*`, `postgres-compat`, `core-package-compatibility`, `aot-build`, `docker-build`, `test-all`, **`CI Gate`** | selective (affected shards) per ADR-0037 |
| **Merge queue** | `merge_group` | **`Merge Queue Gate` ONLY** Ã¢â‚¬â€ every other job cascade-skips | build + format + fast/arch unit smoke |

`CI Gate` is the **PR-time** required check. `Merge Queue Gate` is the
**queue-time** required check. They are distinct jobs by design.

## 3. Re-enabling ruleset 17808547 (FOLLOW-UP Ã¢â‚¬â€ after this lands on trunk)

Do this only **after** this PR is merged so the lean gate exists on `trunk`.
Enabling the queue while the old "full lane on merge_group" design is live on
trunk would reproduce the spiral.

1. **Make `Merge Queue Gate` the queue's required check.**
   - Branches Ã¢â€ â€™ ruleset **17808547 "Merge queue (trunk)"** Ã¢â€ â€™ **Merge queue**
     rule's **required status checks** Ã¢â€ â€™ set to **`Merge Queue Gate`**.
   - **Remove `CI Gate`** from the *merge-queue* required checks (it is the
     PR-time gate; it does not run on `merge_group` and would hang the queue
     waiting on a check that never reports).
   - Keep `CI Gate` as the required check on the **branch-protection / PR**
     ruleset (the up-front gate before a PR can be queued) Ã¢â‚¬â€ unchanged.
2. **Sizing** (recommended):
   - `max_entries_to_build`: **2** (down from the prior 5). The lean gate is
     cheap, but a smaller batch means a single bad PR invalidates fewer
     entries under `ALLGREEN` grouping, and keeps blast radius small while we
     re-validate the queue. Raise to 3Ã¢â‚¬â€œ5 once a week of clean runs proves the
     lean gate holds.
   - `minimum_group_size`: **1** (don't wait to batch; merge singletons fast).
   - `check_response_timeout_minutes`: **15** (down from 60). The lean gate's
     job timeout is 30 min worst case but typically completes in <10; 15 gives
     headroom without letting a hung gate hold the group for an hour. Ensure no
     step in `merge-queue-gate` can exceed this (build 20 / fast 15 / arch 10
     are step caps but run sequentially Ã¢â‚¬â€ the job timeout is 30, so if you set
     the response timeout to 15 also trim the job `timeout-minutes` to ~20 or
     keep 15 and accept a rare timeout-eject; 15 is recommended with the job at
     30 and the understanding that a genuinely slow batch ejects safely).
   - `grouping_strategy`: keep **`ALLGREEN`**.
   - Merge method: **squash** (matches the train's convention and repo history).
3. **Flip enforcement to `active`.** Set ruleset 17808547 enforcement from
   **disabled** Ã¢â€ â€™ **active** (Active). Optionally stage via **Evaluate** first
   to dry-run without enforcing.
4. **Verify live** (post-enable Ã¢â‚¬â€ the queue cannot be exercised until it's on
   the default branch):
   - Open a throwaway PR, approve it, click **"Merge when ready"**.
   - Watch the `CI` run triggered by the **`merge_group`** event. Confirm
     **only** `Merge Queue Gate` runs (no AOT/docker/shard jobs appear), it
     reports success, and the PR fast-forwards `trunk`.
   - Confirm a batch of Ã¢â€°Â¥2 queued PRs produces **one** `merge-queue-gate` job
     per batch ref, not a full matrix.

### Rollback

If the queue misbehaves, set ruleset 17808547 enforcement back to **disabled**
(one toggle), then explicitly dispatch the sole `merge-train.yml` authority in
live mode. Do not restore or run the deleted legacy merger. The lean queue gate
simply stops being invoked when `merge_group` events stop firing; exact-head
admission and batch CI remain enforced by the current merge train.

## 4. Strict branch protection Ã¢â‚¬â€ keep it OFF

`strict` (require-branch-up-to-date-before-merge) was turned **off** so PRs
merge in parallel when green. **Keep it off once the queue is on.** The merge
queue **enforces up-to-date-ness itself**: it builds each entry on top of the
combined batch (the latest trunk + the PRs ahead of it), so a stale PR is
re-based by the queue before its gate runs. Turning `strict` back on would
re-introduce the serial re-stale storm (every merge re-stales every other PR)
that the queue exists to eliminate Ã¢â‚¬â€ redundant and harmful. **Recommendation:
strict stays OFF.**

## 5. Interaction with `merge-train.yml`

`merge-train.yml` is the repository's sole merge authority. Do not enable a
second native or workflow-based merger concurrently. If GitHub's native merge
queue is activated, disable the workflow authority first and make that authority
transfer an explicit operational change. The flaky-rerun and AI-triage workflows
remain orthogonal because neither can merge.

## 6. Static verification done in this PR

The queue/`merge_group` event cannot be fully exercised until the ruleset is
`active` on the default branch, so this PR verifies the gate **statically**:

- **YAML validity:** `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"` passes.
- **Job-graph trace (parsed `if:`/`needs:`):** on `merge_group`, the **only**
  job that runs is `Merge Queue Gate`. All 21 other jobs Ã¢â‚¬â€ including
  `aot-build`, `docker-build`, the `server-tests` shard matrix, the
  Postgres/browser/MCP/Python lanes, and `CI Gate` Ã¢â‚¬â€ cascade-skip (`changes`,
  `pr-readiness`, `pr-template-check`, `test-all`, and `CI Gate` carry explicit
  `github.event_name != 'merge_group'` guards; everything else skips because it
  `needs:` a skipped upstream job).
- **PR-path unchanged:** every guard added is `github.event_name != 'merge_group'
  && (existing)`, which is a no-op on `pull_request`/`schedule`/
  `workflow_dispatch`, so the existing selective PR lane and nightly full lane
  are untouched.
- **Concurrency:** `merge_group` keys on `github.ref`, which is the unique
  per-batch queue ref (`refs/heads/gh-readonly-queue/trunk/pr-...`). Each batch
  gets its own concurrency group, so a reformed group runs under a NEW ref and
  is not cancelled-by / cannot-cancel the prior batch Ã¢â‚¬â€ the lean gate makes the
  per-batch run cheap, so `cancel-in-progress: true` only collapses duplicate
  `checks_requested` events for the SAME batch ref.

**Live validation is post-merge + post-ruleset-enable** (Ã‚Â§3 step 4), with the
one-toggle rollback in Ã‚Â§3.

## 7. Open items / things guessed

- **Exact prior ruleset field values** (`max_entries_to_build=5`,
  `grouping_strategy=ALLGREEN`, `check_response_timeout_minutes=60`) are taken
  from `docs/internal/ci/merge-queue.md` Ã‚Â§5, not re-read live from the GitHub
  API (the ruleset is currently disabled). Confirm against
  `gh api repos/honua-io/honua-server/rulesets/17808547` before flipping.
- The recommended `max_entries_to_build=2` and `check_response_timeout=15` are
  conservative re-entry values, not measured Ã¢â‚¬â€ tune up after a week of clean
  queue runs (the lean gate's real wall-time will tell you the safe batch size
  and timeout).
