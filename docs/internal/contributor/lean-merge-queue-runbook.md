# Lean merge-queue runbook (honua-server)

Target architecture for re-enabling GitHub's native merge queue on `honua-server`
**without** the runner-starvation spiral that forced it off on **2026-06-18**. This
runbook covers: the lean `merge_group` gate (landed in `ci.yml` by this PR), the
exact ruleset re-enable steps (a follow-up — **do NOT flip the ruleset in this
PR**), recommended queue sizing, and how the queue interacts with strict branch
protection and `pr-merge-train.yml`.

Companion docs:
- `docs/internal/ci/merge-queue.md` — the original CI-throughput operator runbook
  (shard routing, suite-speed, CI-minutes review). Its §1 "full lane on
  merge_group" design is **superseded** by this runbook's lean gate.
- `docs/internal/contributor/merge-coordination-runbook.md` — the webhook-driven
  train + flaky-rerun + AI-triage workflows that drain the queue today.

## 1. Why the queue was disabled (root cause)

When the queue was on, the `merge_group` event re-ran the **FULL ~45-job CI
matrix** on the batched commit — because a `merge_group` event is a
non-`pull_request` event, the `changes`/`targeted-shards` jobs took their
`else` (full-CI) branch and escalated to `run_all` (every Server Tests shard +
AOT + docker + Postgres + browser/MCP lanes). With `max_entries_to_build = 5`,
a single batch fanned out to **~225 concurrent jobs**. That starved the runner
pool; the runner-starved `AOT Build Verification` and the long shard matrix
couldn't finish inside `check_response_timeout_minutes`; GitHub **ejected the
front PR**, **reformed the group**, and spawned a **NEW** run **without
cancelling the old** — zombie runs piling up → spiral. Ruleset **17808547
"Merge queue (trunk)"** was set to **enforcement: disabled**.

## 2. The fix — a LEAN `merge_group` gate (this PR)

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
`merge_group` (see §5 for the exact graph). A batch of 5 therefore runs **one
runner job**, not ~225 — it finishes inside the timeout, so the front PR is
never ejected and the group never reforms into a zombie.

### Jobs on `merge_group` vs `pull_request`

| Lane | Trigger | Jobs that run | Scope |
|------|---------|---------------|-------|
| **PR** | `pull_request` | the full graph: `pr-template-check`, `pr-readiness`, `changes`, `targeted-shards`, `ci-router-validation`, `build`, `dotnet-foundation-tests`, `server-tests` (selective shard subset), `python-integration-tests`, `js-*`, `esri-leaflet-browser-tests`, `maplibre-compat`, `mcp-*`, `postgres-compat`, `core-package-compatibility`, `aot-build`, `docker-build`, `test-all`, **`CI Gate`** | selective (affected shards) per ADR-0037 |
| **Merge queue** | `merge_group` | **`Merge Queue Gate` ONLY** — every other job cascade-skips | build + format + fast/arch unit smoke |

`CI Gate` is the **PR-time** required check. `Merge Queue Gate` is the
**queue-time** required check. They are distinct jobs by design.

## 3. Re-enabling ruleset 17808547 (FOLLOW-UP — after this lands on trunk)

Do this only **after** this PR is merged so the lean gate exists on `trunk`.
Enabling the queue while the old "full lane on merge_group" design is live on
trunk would reproduce the spiral.

1. **Make `Merge Queue Gate` the queue's required check.**
   - Branches → ruleset **17808547 "Merge queue (trunk)"** → **Merge queue**
     rule's **required status checks** → set to **`Merge Queue Gate`**.
   - **Remove `CI Gate`** from the *merge-queue* required checks (it is the
     PR-time gate; it does not run on `merge_group` and would hang the queue
     waiting on a check that never reports).
   - Keep `CI Gate` as the required check on the **branch-protection / PR**
     ruleset (the up-front gate before a PR can be queued) — unchanged.
2. **Sizing** (recommended):
   - `max_entries_to_build`: **2** (down from the prior 5). The lean gate is
     cheap, but a smaller batch means a single bad PR invalidates fewer
     entries under `ALLGREEN` grouping, and keeps blast radius small while we
     re-validate the queue. Raise to 3–5 once a week of clean runs proves the
     lean gate holds.
   - `minimum_group_size`: **1** (don't wait to batch; merge singletons fast).
   - `check_response_timeout_minutes`: **15** (down from 60). The lean gate's
     job timeout is 30 min worst case but typically completes in <10; 15 gives
     headroom without letting a hung gate hold the group for an hour. Ensure no
     step in `merge-queue-gate` can exceed this (build 20 / fast 15 / arch 10
     are step caps but run sequentially — the job timeout is 30, so if you set
     the response timeout to 15 also trim the job `timeout-minutes` to ~20 or
     keep 15 and accept a rare timeout-eject; 15 is recommended with the job at
     30 and the understanding that a genuinely slow batch ejects safely).
   - `grouping_strategy`: keep **`ALLGREEN`**.
   - Merge method: **squash** (matches the train's convention and repo history).
3. **Flip enforcement to `active`.** Set ruleset 17808547 enforcement from
   **disabled** → **active** (Active). Optionally stage via **Evaluate** first
   to dry-run without enforcing.
4. **Verify live** (post-enable — the queue cannot be exercised until it's on
   the default branch):
   - Open a throwaway PR, approve it, click **"Merge when ready"**.
   - Watch the `CI` run triggered by the **`merge_group`** event. Confirm
     **only** `Merge Queue Gate` runs (no AOT/docker/shard jobs appear), it
     reports success, and the PR fast-forwards `trunk`.
   - Confirm a batch of ≥2 queued PRs produces **one** `merge-queue-gate` job
     per batch ref, not a full matrix.

### Rollback

If the queue misbehaves, set ruleset 17808547 enforcement back to **disabled**
(one toggle). PRs immediately fall back to direct merge under classic branch
protection + `pr-merge-train.yml`, exactly as today. No code revert needed —
the lean gate simply stops being invoked when `merge_group` events stop firing.

## 4. Strict branch protection — keep it OFF

`strict` (require-branch-up-to-date-before-merge) was turned **off** so PRs
merge in parallel when green. **Keep it off once the queue is on.** The merge
queue **enforces up-to-date-ness itself**: it builds each entry on top of the
combined batch (the latest trunk + the PRs ahead of it), so a stale PR is
re-based by the queue before its gate runs. Turning `strict` back on would
re-introduce the serial re-stale storm (every merge re-stales every other PR)
that the queue exists to eliminate — redundant and harmful. **Recommendation:
strict stays OFF.**

## 5. Interaction with `pr-merge-train.yml`

`pr-merge-train.yml` currently does GitHub's job manually: it squash-merges the
oldest CLEAN PR and freshens BEHIND PRs (see `merge-coordination-runbook.md`).
**With a real merge queue, the queue does the merging and the up-to-date-ing,
so the train's merge/freshen role is superseded.**

**Recommendation: keep the train but neuter its merge/freshen actions while the
queue is the primary drainer** — i.e. treat the queue as primary and the train
as a *disabled-by-default fallback*, NOT run both merge mechanisms at once
(double-merge races, the train fighting the queue's rebases). Two safe options:

- **Preferred:** when ruleset 17808547 goes `active`, **disable
  `pr-merge-train.yml`** (comment its triggers or add a guard
  `if: vars.MERGE_QUEUE_ENABLED != 'true'`). The rollback in §3 (queue back to
  disabled) then re-arms the train by flipping that variable — one toggle,
  symmetric with the ruleset.
- **Minimum:** at least stop the train from **merging** while the queue is on
  (leave its freshen path off too — the queue rebases). Do not leave both
  merge paths live.

The flaky-rerun (`auto-rerun-flaky.yml`) and AI-triage (`ci-failure-triage.yml`)
workflows are **orthogonal** and stay on regardless — they react to CI
conclusions, they don't merge.

## 6. Static verification done in this PR

The queue/`merge_group` event cannot be fully exercised until the ruleset is
`active` on the default branch, so this PR verifies the gate **statically**:

- **YAML validity:** `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"` passes.
- **Job-graph trace (parsed `if:`/`needs:`):** on `merge_group`, the **only**
  job that runs is `Merge Queue Gate`. All 21 other jobs — including
  `aot-build`, `docker-build`, the `server-tests` shard matrix, the
  Postgres/browser/MCP/Python lanes, and `CI Gate` — cascade-skip (`changes`,
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
  is not cancelled-by / cannot-cancel the prior batch — the lean gate makes the
  per-batch run cheap, so `cancel-in-progress: true` only collapses duplicate
  `checks_requested` events for the SAME batch ref.

**Live validation is post-merge + post-ruleset-enable** (§3 step 4), with the
one-toggle rollback in §3.

## 7. Open items / things guessed

- **Exact prior ruleset field values** (`max_entries_to_build=5`,
  `grouping_strategy=ALLGREEN`, `check_response_timeout_minutes=60`) are taken
  from `docs/internal/ci/merge-queue.md` §5, not re-read live from the GitHub
  API (the ruleset is currently disabled). Confirm against
  `gh api repos/honua-io/honua-server/rulesets/17808547` before flipping.
- The recommended `max_entries_to_build=2` and `check_response_timeout=15` are
  conservative re-entry values, not measured — tune up after a week of clean
  queue runs (the lean gate's real wall-time will tell you the safe batch size
  and timeout).
