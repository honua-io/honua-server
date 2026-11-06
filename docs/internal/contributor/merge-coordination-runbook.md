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

## Gate model: what runs on a PR

There is no per-PR CI matrix; the optimistic merge-train model has been in place since
2026-06-18. A PR's own events run:

- the cheap `PR Gate` (`.github/workflows/pr-gate.yml`): build, format, Fast unit tests and an
  architecture smoke, on one runner;
- CodeQL;
- GitBook previews;
- the drift and OpenAPI governance checks.

The full CI matrix runs only on batch CI, as trailing trunk runs, and on the nightly schedule.
That matrix is every server-test shard in `.github/ci-shards.json` plus the AOT, Docker,
browser, MCP and provider lanes.

**The required branch-protection checks are `PR Gate` and `Review Gate`.**

- `PR Gate` (app id `15368`) is the unprivileged verification context.
- `Review Gate` is the trusted, exact-head admission context, published only by default-branch
  policy.

Both are unfiltered and required. `CI Gate` remains the batch-CI aggregator and is intentionally
not a required per-PR context.

**Nothing in CI validates the PR template.** The `Validate PR Template Compliance` job lives in
`ci.yml`, which has no `pull_request` trigger. Its one real step is also gated on
`github.event_name == 'pull_request'`, so it never fires on a PR, and it was only advisory even
when it did. The template is a human contract:

- Fill every section, including the `Closes #N` or `Refs #N (<what remains>)` disposition.
- Tick the `scripts/ci/pre-pr-check.sh` box only after a real run. If you ran an equivalent
  instead, say what you ran under **Testing**.

`PR Gate` does read the commits and the PR body for one thing: the AI-attribution check,
`scripts/ci/check-commit-attribution.py`.

## Normal landing path: the per-PR lander

Keep a ready PR non-draft and let `PR Gate` complete. The routine merge authority is the fleet's
serialized per-PR lander. It continuously lands any open PR that meets all of these conditions:

- MERGEABLE and non-draft;
- not labelled `hold`, `train:hold` or `train:escalated`;
- currently based on `trunk`;
- `PR Gate` and `Review Gate` both green at its exact current head;
- zero unresolved review threads.

The lander rechecks every condition immediately before merging. You do not dispatch anything:

- Resolve review findings with evidence, never with bare resolutions.
- Keep the head green.

The full CI matrix trails on trunk after landings. Trunk runs are serialized and never cancelled
by a newer trailing dispatch. A deterministic trunk red pauses the lander for every branch except
`fix/`, `revert/` and `hotfix/` ones until a fix-forward PR lands. Never weaken tests to clear it.

**Batch verification (shadow, CI velocity plan v2).** The same lander also verifies candidate
batches of landable PRs:

1. It reproduces GitHub's sequential squash merges on a private `land/batch/<base7>/<id>` ref
   (never `train/batch/*`).
2. It dispatches `ci.yml` there with `selective_base` set to the last verified trunk head.
3. It judges the run by a verification receipt, never by a job count. See
   `docs/internal/ci/gate-model.md`, "Verification receipts and batch landing".

While batch verification runs in shadow, it only records would-land and would-quarantine
outcomes. Per-PR landing is unchanged, and the batch path never merges, labels or comments on a
PR. `land/batch/*` branches and `CI lander-batch-*` runs belong to the lander:

- do not push to, rerun or delete them;
- never read one as a verdict on trunk or on your PR.

Batch landing going live is a separate operator decision. Once it is live, a red batch
quarantines its members until a verified green subset containing them exists. A quarantine is
keyed to the head it was judged at, so pushing a fix releases it.

## Base every PR on `trunk`; do not stack (#3248)

Stacking is how reviewed, tested, nominally merged work disappears:

1. The stacked PR merges into its base branch.
2. GitHub marks it MERGED, and the linked issue often auto-closes. CI was green.
3. The base branch itself never lands.

The payload is then not in the product, and nothing signals it. This happened three times in five
weeks (#3116, #3113, #2835).

- **Retarget when the base merges.** If you must stack while a base is in review, retarget the
  stack member at `trunk` the moment its base merges (`gh pr edit <N> --base trunk`), then
  rebase. Never merge a PR into a base branch that has already landed or been deleted.
- **Neither the MERGED badge nor `git merge-base --is-ancestor <mergeCommitSha> origin/trunk`
  is evidence on its own.** The ancestry test checks commit identity, but a squash, a
  cherry-pick or an independent re-land puts the content on `trunk` under a different SHA. Use
  it to find candidates, then check the content. The first #3248 sweep filed three false
  positives out of four this way.
- **The weekly stranded-merge detector is the backstop.**
  `scripts/ci/detect-stranded-merges.py` runs from
  `.github/workflows/stranded-merge-detector.yml`.
  - It adjudicates each candidate by patch identity first, then blob equality, then added-line
    presence.
  - It classifies each candidate as one of:
    - `stranded` (files absent);
    - `edits-missing` (files present, the PR's lines absent);
    - `superseded`;
    - `landed`;
    - `indeterminate` (usually a force-pushed or deleted stack base; treated as actionable,
      never as a pass).
  - It also warns about open PRs whose base has already merged or been deleted.

  It is a backstop, not the control: retarget the stack member yourself.
- **Write the decision down in `scripts/ci/stranded-merge-dispositions.json`.** Each entry needs
  a `reason` and the exact `paths` it covers.
  - `abandoned` or `landed-elsewhere` moves the finding to the Adjudicated table.
  - `recovering` (with the recovery PR) keeps the finding actionable until that PR lands.

  A decision cannot absorb a path it never adjudicated, so a file later deleted from `trunk`
  re-opens the finding.

## Live batch train (manual and release-candidate use)

`.github/workflows/merge-train.yml` is no longer the routine landing path. Use it only when you
intentionally want cumulative batch CI and batch landing:

```bash
gh pr edit <NOT-READY-N> --add-label hold   # exclude any PR that is not ready to land
gh workflow run merge-train.yml -f train_apply=true
```

- **A bare dispatch is a dry run.** `train_apply` defaults to `false`, so a bare
  `gh workflow run merge-train.yml` reports its decisions and merges nothing. Scheduled
  invocations are also dry runs.
- **The train lands the whole batch**, not just the PR you are looking at. It excludes drafts and
  PRs labelled `train:hold`, `hold` or `train:escalated`.
- **How a batch is built.** The train assembles eligible PRs oldest-first on a
  `train/batch/<trunk-sha>/<id>` branch, selects the cumulative smart-CI shard subset, dispatches
  `ci.yml` and attributes failures. It lands a successful batch only during an explicit live
  dispatch.
- **For an isolated PR**, dispatch with `train_apply=true` and `max_batch=1`. Never admin-merge
  around the train.
- **Batch failure means `train:escalated`.** The train labels the batch members
  `train:escalated`, which excludes them from future batches.
  - If a rerun of that same `train/batch/*` CI run turns green, `merge-train-rerun-recovery.yml`
    clears the stale escalation labels and stamps `CI Gate` on the member PR heads.
  - If the failure is real, fix the root cause, remove the label, and re-dispatch.
- **Batch CI is stricter than local builds.** See
  [known flakes](../ci/known-flakes.md), which also covers the `40P01` deadlock flake that
  affects batch shards.

## Recovery concurrency

Every controller that can write the durable train state uses
`concurrency: merge-train-state-writers` with `cancel-in-progress: false`.
This makes live train dispatch and rerun recovery single-flight before either
can read the issue body. Scheduled and bare-dispatch trains are provably
read-only and use a unique `merge-train-read-only-<run id>` group instead.

The split matters because GitHub keeps only **one pending run per group**. When
the 15-minute read-only schedule shared the writer group, it evicted queued
recoveries: 12 of 15 consecutive recovery runs were cancelled before their job
evaluated. Unique observer groups remove that starvation source. GitHub can
still replace one queued writer with a newer queued writer; an operator may
have to rerun a superseded recovery. The running writer is never cancelled,
and preserving state integrity takes precedence over retaining every queued
invocation.

### What makes execution safe

The shared writer group is the state journal's atomic admission boundary: two
controllers cannot interleave read→edit sequences, so the issue body's
non-atomic API cannot lose an update. Recovery lands directly (`recovery.sh`
calls `train_land`), and the landing path retains independent safeguards:

| Guarantee | Where |
|---|---|
| A scheduled or `workflow_run` train is hard-coded `TRAIN_APPLY=0`, and every train mutation goes through `train_side_effect`, which in dry-run logs and returns without executing. `train_state_write` uses it for both the edit and the create path. **A dry-run train writes nothing at all** — no state issue, no labels, no push. | `resolve-mode.sh:20-22`, `lib.sh:268-305`, `state.sh:162-177` |
| The plain (never forced) fast-forward push is the atomic landing boundary, enforced by the git server rather than by Actions. Two actors pushing the same batch SHA make the second a no-op; different SHAs make the loser non-fast-forward, which becomes `rc=10` and a re-assemble. **Double-landing is structurally impossible.** | `land.sh:177-206` |
| Every member's mutable admission is re-attested immediately before the CAS, and trunk must still equal the assembly base. | `land.sh:154-174` |
| Recovery's PR closes are gated on `gh pr merge --match-head-commit`, so a head that moved after validation can never be merged. | `recovery.sh:52-61` |
| Recovery refuses to act unless the state issue still names this exact batch branch, this exact CI run id, and a recoverable phase (`ci-incomplete`, `land`, `requeue`). A train that has moved on fails it closed. | `recovery.sh:211-214` |
| Post-land reconciliation is driven by trunk ancestry, not by the journal, and terminal recovery refuses to overwrite a land-family phase rather than clearing it. | `land.sh:119-139`, `train.sh:464,470` |

### State issue compare check

The state issue remains a plain `gh issue edit`, whose API has no atomic version
precondition. `state.sh` still compares the exact prior and verifies the result
as defense in depth, but correctness no longer relies on that shell check:
single-flight admission prevents a second live controller from entering the
read→edit window.

**Why not fence the train out with an early `land` phase?** Considered and
rejected. `land` is durable land intent: `train_state_salvage` refuses to reset
it (`state.sh:232`) and `train_recover_terminal_batch` refuses to clear it
(`train.sh:464,470`). Writing it for a batch recovery has not yet decided to land
— and might abandon at the wait cap — trades a rare lost rescue for exactly the
repo-wide merge deadlock #3045 exists to prevent. Recovery-side land intent
therefore stays where it is: written immediately before `train_land`, with
immutable member heads and a batch SHA.

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
