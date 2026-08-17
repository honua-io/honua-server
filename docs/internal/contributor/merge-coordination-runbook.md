# Merge coordination runbook

Merging on `honua-server` has exactly one authority: `merge-train.yml`. Nothing
else merges, reruns, or triages on a PR's behalf, and no live Claude/Codex
session needs to babysit a queue.

| Workflow | Trigger | Job |
|---|---|---|
| `merge-train.yml` | 15-minute schedule (dry-run) or an explicit `train_apply=true` dispatch | Sole merge authority: exact-head `PR Gate` + `Review Gate` admission, batch assembly, batch CI dispatch, failure attribution, and compare-and-swap landing. |
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
independently instead of cancelling each other, and it takes the train's
exclusion through the durable **Merge Train State** issue rather than through
the Actions concurrency group: it refuses to act unless the state issue's
`active_batch` still names the same batch branch, the same CI run id, and a
recoverable phase (`ci-incomplete`, `land`, or `requeue`). Landing itself is an
FF-CAS against current trunk (`train_land` returns 10 and re-queues when
admission or the fast-forward target moved), and PR closes are gated on
`gh pr merge --match-head-commit`. A recovery that cannot prove it is still the
active batch writes the `select` phase and dispatches one live train run instead
of acting itself.

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
