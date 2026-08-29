# Merge coordination runbook

Routine merging on `honua-server` is performed by the fleet's serialized per-PR
lander. It rechecks that the PR is open, non-draft, MERGEABLE, based on `trunk`,
not held or escalated, has exact-head successful `PR Gate` and `Review Gate`
contexts, and has no unresolved review threads immediately before merging. No
live Claude/Codex session needs to babysit a queue.

| Workflow | Trigger | Job |
|---|---|---|
| Fleet serialized per-PR lander | External serialized service | Routine merge authority: rechecks trunk base, exact-head `PR Gate` + `Review Gate`, review-thread, mergeability, draft, and hold/escalation admission immediately before landing. A deterministic trailing trunk CI failure pauses routine landings except fix-forward branches. |
| `merge-train.yml` | 15-minute schedule (dry-run) or an explicit `train_apply=true` dispatch | Manual/release-candidate batch authority: exact-head `PR Gate` + `Review Gate` admission, batch assembly, batch CI dispatch, failure attribution, and compare-and-swap landing. Not the routine landing path. |
| `merge-train-rerun-recovery.yml` | `workflow_run` (CI) completed successfully on a `train/batch/*` branch | Resumes the active immutable batch when a failed batch CI is rerun green: clears stale `train:escalated`/`train:landing` labels, lands or re-queues the recorded batch, then dispatches one live continuation run of `merge-train.yml`. |

Flake reruns, timeout classification, and failure attribution live **inside**
the train, not in separate observer workflows:
`scripts/ci/merge-train/classify-flake.sh` (bounded single rerun on a known
environmental signature), `scripts/ci/merge-train/classify-timeout.sh` (generic
timeout / exit-124 / terminal-cancellation retry, strict precedence over flake
matching), and `scripts/ci/merge-train/attribute.sh` (which batch member caused
the failure). The former standalone `auto-rerun-flaky.yml` and
`ci-failure-triage.yml` workflows were deleted: both keyed on
`github.event.workflow_run.event == 'pull_request'`, and `ci.yml` has had no
`pull_request` trigger since #2865, so every run of either was `skipped`.

## Recovery concurrency

`merge-train.yml` holds `concurrency: {group: merge-train,
cancel-in-progress: false}` so one batch is in flight at a time.
`merge-train-rerun-recovery.yml` used to share that exact group. GitHub keeps
only **one pending run per group**, so the 15-minute train schedule repeatedly
evicted queued recovery runs — 12 of 15 consecutive recovery runs were
`cancelled` before their job ever evaluated.

Recovery now uses its own per-source-run group
(`merge-train-recovery-<workflow_run id>`), so distinct recoveries queue
independently instead of cancelling each other.

### What makes concurrent execution safe

The shared Actions concurrency group was never the safety mechanism, and the
idle-wait that replaced it is **best-effort contention avoidance only**. Recovery
lands directly (`recovery.sh` calls `train_land`), so it is worth being precise
about what actually holds:

| Guarantee | Where |
|---|---|
| A scheduled or `workflow_run` train is hard-coded `TRAIN_APPLY=0`, and every train mutation goes through `train_side_effect`, which in dry-run logs and returns without executing. `train_state_write` uses it for both the edit and the create path. **A dry-run train writes nothing at all** — no state issue, no labels, no push. | `resolve-mode.sh:20-22`, `lib.sh:268-305`, `state.sh:162-177` |
| The plain (never forced) fast-forward push is the atomic landing boundary, enforced by the git server rather than by Actions. Two actors pushing the same batch SHA make the second a no-op; different SHAs make the loser non-fast-forward, which becomes `rc=10` and a re-assemble. **Double-landing is structurally impossible.** | `land.sh:177-206` |
| Every member's mutable admission is re-attested immediately before the CAS, and trunk must still equal the assembly base. | `land.sh:154-174` |
| Recovery's PR closes are gated on `gh pr merge --match-head-commit`, so a head that moved after validation can never be merged. | `recovery.sh:52-61` |
| Recovery refuses to act unless the state issue still names this exact batch branch, this exact CI run id, and a recoverable phase (`ci-incomplete`, `land`, `requeue`). A train that has moved on fails it closed. | `recovery.sh:211-214` |
| Post-land reconciliation is driven by trunk ancestry, not by the journal, and terminal recovery refuses to overwrite a land-family phase rather than clearing it. | `land.sh:119-139`, `train.sh:464,470` |

### Residual risk (larger than a lost journal entry)

The idle wait narrows a window; it does not close one. A live train can still be
dispatched between the wait passing and recovery finishing. **Nothing can
double-land or merge an unreviewed head** — that is settled by the fast-forward
push and `--match-head-commit` above — but the green rerun this workflow exists
to rescue *can still be discarded*, because a concurrently starting train runs
its own startup recovery over the same state:

- a `ci-incomplete` batch is classified `escalate` (`train.sh:380`), which labels
  every member `train:escalated` and clears the batch; or
- a land-family phase is taken over by `train_restore_post_land`
  (`land.sh:91-140`), which reconciles against trunk and reselects.

Either outcome loses the rescue and needs an operator to clear the escalation
and re-dispatch. Separately, the state issue is a plain `gh issue edit` with **no
compare-and-swap** (`state.sh:168`), so interleaved writers are last-writer-wins.

**Why not fence the train out with an early `land` phase?** Considered and
rejected. `land` is durable land intent: `train_state_salvage` refuses to reset
it (`state.sh:232`) and `train_recover_terminal_batch` refuses to clear it
(`train.sh:464,470`). Writing it for a batch recovery has not yet decided to land
— and might abandon at the wait cap — trades a rare lost rescue for exactly the
repo-wide merge deadlock #3045 exists to prevent. Recovery-side land intent
therefore stays where it is: written immediately before `train_land`, with
immutable member heads and a batch SHA.

**What the wait does buy.** It waits only on `workflow_dispatch` runs, because a
scheduled train is provably dry-run and blocking on the 15-minute cadence would
be pure delay. A failed API read counts as "busy, keep polling" rather than
aborting the job, and on expiry the job fails loudly with the state untouched —
so a dropped recovery is visible and rerunnable rather than silently cancelled.

**The relevance precheck comes first.** This workflow fires on *every* green
`train/batch/*` CI run, and a live drain self-chains, so most invocations concern
a batch the train has already moved past. The job therefore runs the read-only
`train_recovery_active_state` guard (`recovery.sh`) before it waits at all, and
exits with a notice when this run is not the active recoverable batch. Without
that ordering a drain produced a string of recoveries that each burned the full
30-minute wait and then went red.

### Why recovery does not simply re-dispatch the train

It is the obvious design, and it is not currently available: the train's own startup recovery
classifies `ci-incomplete` as `escalate` (`train.sh:380`), which labels every
member `train:escalated` and clears the batch — the opposite of landing it. The
fact that makes landing correct ("that same CI run has since been rerun green")
arrives only in this workflow's `workflow_run` payload; `merge-train.yml` has no
`workflow_run` trigger and its `recovery_key` input is a bare idempotency
string, not a batch identity. Handing off would therefore need a new train input
carrying the green run identity plus a new recovery phase and class, with drift-
guard coverage in `fixtures/validate-timeout-retry.sh`. That is a merge-train
design change, tracked separately from this workflow's concurrency fix.

## Recovering a stuck merge train (never hand-edit the state issue)

`merge-train.yml` keeps its resume point in the machine-managed **Merge Train State** issue
(label `train:state`). On startup the train reads that state and recovers before selecting:
`land`/`pre-land-cleanup`/`post-land-finalize` reconcile against trunk, the `*-retry-*` phases
resume their in-flight failed-job rerun, and every other phase is a terminal cleanup that
releases the batch. **Every phase the read schema accepts has a recovery owner** — a drift guard
in `scripts/ci/merge-train/fixtures/validate-timeout-retry.sh` fails the build if a phase is ever
added to `TRAIN_STATE_PHASES` (`state.sh`) without a `TRAIN_PHASE_RECOVERY` class (`train.sh`).
Before that guard existed, a run that ended during `attribute` deadlocked all merging repo-wide
(#3045).

If the train still refuses to start ("active merge-train state is unknown, malformed, or
incompletely recovered", or "durable state lookup failed"), use the sanctioned reset rather than
editing the issue body:

```bash
gh workflow run merge-train.yml -f train_apply=true -f reset_state=true
```

It clears `active_batch` to the exact cleared shape the train itself writes (`branch: ""`,
`included: []`, `phase: "select"`, null run/batch fields), preserves `config` and
`last_landed_trunk`, removes `train:landing` from the recorded members, and stops without
selecting or landing. It **refuses** while a batch carries durable land intent (phase `land`,
`pre-land-cleanup`, or `post-land-finalize`) — that state has to reconcile against trunk first.
A reset-only run never self-chains, so nothing lands until you dispatch an ordinary live run
yourself.

The reset also repairs a body the read schema *rejects* (an already-hand-edited issue, or one
whose JSON block no longer parses): it falls back to a best-effort salvage that keeps whatever
members, trunk base and telemetry are still legible, and applies the land-intent refusal to the
raw text — across newlines, so a damaged block that puts the value on its own line
(`"phase":` ⏎ `"land"`) still refuses instead of erasing the durable land journal. The refusal
keys on the *phase token* only: a `batch_sha` is recorded for every assembled batch
(`smart-ci`, `attribute`, the retry phases), and a branch name or `last_landed_trunk` can
contain the same letters, so matching any of those would refuse the ordinary stuck batch this
exists for.

Do not repair the state issue by hand. `active_batch: null` in particular is **invalid**: the
read schema requires `active_batch` to be an object, so that edit swaps a recovery deadlock for
a "durable state lookup failed" one.

## Reading a shard-terminal escalation

Three merge-train outcomes mean *a shard never finished executing its tests*, so nothing in the
batch is implicated and no member diff is attributed. Each escalation comment **names the
offending jobs** and the batch is cleared (`active_batch` reset) so the queue can progress.

| Outcome | What happened | What to do |
|---|---|---|
| `ci-shard-capacity-exhausted` | The shard used its whole configured budget while still producing test output. | Raise `test_timeout_minutes`/`timeout_minutes` or split the shard in `.github/ci-shards.json` (see [shard-timeout-budgets](../ci/shard-timeout-budgets.md)). Do **not** rerun — a rerun reproduces the exhaustion at full runner cost. |
| `ci-shard-killed` | The shard's test host was SIGKILLed before the runner's own kill deadline. Not a timeout. | Suspect an out-of-memory kill or an external cancellation; check runner size and the shard split. |
| `ci-failure-evidence-unavailable` | A terminal failed job's log stayed unreadable across the controller's bounded retries. | Restore Actions evidence access, then re-dispatch. |

In all three cases: fix the cause, remove `train:escalated` from the members, and re-dispatch. A
**stalled** shard (`HONUA_SHARD_HANG_SUSPECTED`) is deliberately different — it still gets the
historical single failed-job rerun, and is only treated as real if it reproduces.

Every one of these is classified **before** the pre-existing-failure filter can subtract it, and
before the bounded retry, autofix, and attribution can act on it. That ordering is the safety
property: the filter compares signatures sampled from a bounded window of each job log, and a
shard marker is the shard's very last line, so filtering first could have cancelled the shard
against trunk's equally noisy red shard and landed the batch on tests that never ran.
`scripts/ci/merge-train/fixtures/validate-capacity-ordering.sh` locks the ordering in.

## Testing & validation

- `scripts/ci/merge-train/fixtures/validate-timeout-retry.sh` proves every accepted state phase
  has a recovery owner and that the bounded timeout retry is idempotent across cancellation.
- `scripts/ci/validate-single-merge-authority.sh` proves no second workflow can merge.
- `workflow_run`-triggered workflows only ever execute the default branch's definition, so a
  change to `merge-train-rerun-recovery.yml` is validated post-merge: on the next green rerun
  of a `train/batch/*` CI run, confirm the recovery run is no longer `cancelled` and that its
  decision log names the active batch.
