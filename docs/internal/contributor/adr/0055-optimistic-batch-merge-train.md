# ADR-0055: Optimistic batch merge train (Phase 1)

## Status

Proposed. Phase 1 (deterministic git assembly, smart-CI, attribution, FF-CAS
land, dry-run-by-default workflow) lands with this ADR. It ships DISABLED for
live action: every automatic trigger runs in dry-run, and only an explicit
`workflow_dispatch` with `train_apply=true` (and a `HONUA_MERGE_TOKEN`) lands a
batch. A human flips it live later. Phases 2/3 are tracked as roadmap below.

## Context

honua-server merges one PR at a time. GitHub's native merge queue was disabled
(2026-06-18, ruleset 17808547) after batch sizes of ~5 caused runner
starvation, ejected the front PR, reformed the group, and spiralled into zombie
runs (see `docs/internal/contributor/merge-queue-runbook.md`). The lean
`Merge Queue Gate` job replaced the full matrix on `merge_group`, but the queue
itself is off, so throughput is gated by serial human merges.

The single required PR check is **CI Gate** (`ci.yml`), an aggregator over the
full heavy matrix that already validated each PR on its own `pull_request` run
BEFORE it would ever land. Re-running that matrix per batch is what melted the
runners. The opportunity: assemble several already-green PRs into one batch
branch, run only the **smart-CI shard subset** that the batch's cumulative diff
actually touches (via the existing ADR-0037 `honua-server-targeted-tests.sh` +
`.github/ci-shards.json`), and fast-forward trunk once — catching only the
semantic conflicts that BATCHING introduces, not re-proving each PR.

The prior bar for any such automation: it must be deterministic, unit-tested
offline, dry-run by default, and incapable of acting merely because its PR
merged.

## Decision

Add an **optimistic batch merge train** as POSIX-bash steps under
`scripts/ci/merge-train/` (each a sourceable, independently testable function;
`train.sh` orchestrates) plus a `merge-train.yml` workflow that defaults to
dry-run.

### Steps

1. **select** — `gh pr list --base trunk --state open`. Ready = non-draft,
   labels exclude `train:hold`/`train:escalated` (and the pre-existing `hold`
   opt-out), `mergeable==MERGEABLE`, and CI Gate `SUCCESS` or a flake-only
   failure. Oldest-`createdAt` first, capped at `MAX_BATCH` (default 3).
2. **assemble** (the git heart) — branch `train/batch/<trunkSha7>/<epoch>` off
   `origin/trunk`; `git merge --no-ff` each PR head. On conflict
   `git merge --abort` (transactional, ZERO residue), mark `SKIPPED_CONFLICT`,
   continue. Records INCLUDED[] + each pre-merge head SHA.
3. **smart-ci** — the targeted-tests script on the batch's cumulative diff picks
   the shard subset; (live) push the branch + `gh workflow run ci.yml`, poll,
   read the **CI Gate** job conclusion. The batch is a BRANCH, so its CI keys on
   `ci-<ref>` — a DISTINCT concurrency group from each member's `ci-<pr#>`, so
   the batch run can never cancel-in-progress a member's PR run.
4. **forward-fix** — ONLY when the sole failure is the format-verify step:
   `dotnet format Honua.sln` → commit `style: dotnet format (train forward-fix)`
   → re-run. Cap 2. Everything else (proof-ledger / OpenAPI / feature-catalog
   drift, compile/test failures) ESCALATES, never auto-patched.
5. **classify-flake** (BEFORE attribute) — regex
   `40P01|deadlock detected|ryuk|Testcontainers.*(timed out|connection refused)`
   over failing-job logs. Match → a single `gh run rerun --failed` (cap 1),
   never bisection. Reproduce twice → treat as real.
6. **attribute** — REVERSE of smart-CI routing: failing shard →
   `.paths[]` from ci-shards.json → which INCLUDED PR's diff touches those
   prefixes. 1 suspect → drop it; ≥2 → drop all; 0 → escalate the whole batch.
   Dropped PRs get `train:escalated` + a comment; rebuild minus culprits, re-CI.
7. **land** — `git fetch origin trunk`; compare-and-swap: only if
   `origin/trunk` still equals the assembled-onto SHA, `git push origin
   <batch>:trunk` FF-only (a non-FF rejection ⇒ trunk moved ⇒ re-assemble; the
   train NEVER lands un-CI'd bytes via force). Then `gh pr merge <n> --merge`
   per INCLUDED PR.
8. **state** — a `Merge Train State` issue (label `train:state`) with a fenced
   JSON block, written BEFORE each side-effecting step so a crash is resumable;
   per-PR labels `train:landing`/`train:escalated`/`train:hold` carry transient
   state.

### Dry-run contract (the safety bar)

`TRAIN_APPLY` (default 0) gates every state-mutating action through a single
`train_side_effect` chokepoint. In dry-run, real LOCAL git assembly and real
READ-ONLY GitHub reads execute (so conflict detection and CI-status reads are
genuine), but `git push`, `gh pr merge/edit/comment`, `gh workflow run`,
`gh run rerun`, and issue writes are LOGGED, not executed. The `merge-train.yml`
workflow hard-codes dry-run for `schedule` and `workflow_run` and defaults the
`workflow_dispatch` `train_apply` input to `false`, so merging the PR that adds
the train cannot make it act.

### Workflow

Triggers `schedule` (*/15), `workflow_run:{workflows:[CI]}`, and
`workflow_dispatch` (`train_apply` boolean default false). `concurrency:
{group: merge-train, cancel-in-progress: false}`. Permissions
contents/pull-requests/actions/issues write. Uses `secrets.HONUA_MERGE_TOKEN`
if present, else `GITHUB_TOKEN` (a `GITHUB_TOKEN` push does NOT retrigger
downstream workflows, so live batch-branch CI needs the PAT). No `matrix.*`
appears in any job-level `if:`. After the train runs, `if: always()` steps
guarantee the Step Summary is never empty and upload `merge-train-metrics.json`
as the `merge-train-metrics` artifact.

### Instrumentation / observability (first-class)

Lack of visibility was half the prior CI nightmare, so the train makes every
decision obvious at a glance. This is ADDITIVE — it reads only what the decision
steps already produced and changes no decision logic; dry-run stays read-only.

- **Structured, grouped run log.** Every step emits a greppable, leveled line via
  one formatter: `[train][<step>][<LEVEL>] <msg>` where `<LEVEL>` is
  `INFO`/`WARN`/`DECISION` (the founder greps `DECISION`). Each pipeline step
  wraps its output in GitHub Actions `::group::`/`::endgroup::` (only when
  `$GITHUB_ACTIONS`) so the workflow log is collapsible per step; off-Actions it
  prints a plain banner so a local dry-run reads cleanly. Landings and
  escalations also emit `::notice::`/`::warning::` so they surface in the run's
  annotations.
- **Step Summary dashboard (headline).** Each run appends a Markdown report to
  `$GITHUB_STEP_SUMMARY` (mirrored to stdout for local dry-runs): run mode
  (DRY-RUN vs LIVE), trunk SHA, batch branch, a **candidates table** (PR# |
  author | CI-Gate | decision: included / skipped+reason / escalated), the
  **batch** (branch + included/skipped PRs), the **smart-CI shard set**
  (targeted shards + `run_all` reason), a **run-actions** counter table
  (forward-fixes / flake reruns / attribution drops / escalated / landed), and
  **per-phase timings**. `_dashboard` is rendered on EVERY exit path — including
  "nothing selected" — so a run is never a silent Summary tab.
- **Machine-readable metrics.** `merge-train-metrics.json`
  (`schema: honua.merge-train.metrics/v1`) is written to `$TRAIN_METRICS_OUT`
  and uploaded as the `merge-train-metrics` workflow artifact (`if: always()`).
  The run timestamp is injected by the workflow (`TRAIN_RUN_TIMESTAMP`), not
  generated mid-script, so the Summary, metrics, and aggregate agree on one
  instant. Schema:

  ```json
  {
    "schema": "honua.merge-train.metrics/v1",
    "run_timestamp": "<ISO-8601>",
    "mode": "DRY-RUN|LIVE",
    "outcome": "nothing-ready|dry-run|landed|trunk-moved-reassemble|escalated-batch|all-dropped|land-error",
    "trunk_sha": "<sha|''>",
    "last_landed_sha": "<sha|null>",
    "counts": {
      "candidates": 0, "selected": 0,
      "skipped_by_reason": { "select_ineligible": 0, "assemble_conflict": 0 },
      "included": 0, "landed": 0, "escalated": 0,
      "flake_reruns": 0, "forward_fixes": 0, "attribution_drops": 0
    },
    "smart_ci": { "shard_count": 0, "run_all": false },
    "phase_durations_seconds": { "select": 0, "assemble": 0, "smart-ci": 0, "ci-gate": 0, "land": 0 }
  }
  ```

- **Persistent over-time dashboard.** Aggregate metrics accumulate across LIVE
  runs in a second fenced block (` ```json aggregate `,
  `schema: honua.merge-train.aggregate/v1`) inside the SAME **Merge Train State**
  issue, with a human-readable Markdown dashboard rendered above it — total
  batches, PRs landed, runs, median time-to-land, flake-rerun rate (reruns per
  batch), escalation count, current trunk SHA, last-landed SHA, and the live
  flake-signature list. The state issue was chosen over a committed
  `docs/ci/merge-train-status.md` because it needs no commit/push per run (lower
  friction, and the train stays FF-CAS-clean about what it pushes to trunk). In
  dry-run the aggregate dashboard renders to the Step Summary but is NOT
  persisted to the issue.

### No bot attribution (runtime requirement)

The train's own commits (`style: dotnet format (train forward-fix)`) and PR/
issue comments carry NO `Co-Authored-By` / "Generated with" / emoji lines, the
same rule the repo enforces on humans. This is asserted in the fixture suite.

## Validation

`scripts/ci/merge-train/fixtures/validate-merge-train.sh` builds a throwaway
local git repo + a bare "upstream" and asserts every decision path offline (no
network, no gh, no dotnet): clean-merge, trunk-conflict (skip + zero residue),
inter-PR-conflict, format-drift forward-fix (+ no-bot-attribution), real-test
attribution (1 / ≥2 / 0 suspects), flake (single rerun, no bisection), and the
FF-CAS race (stale-base + non-FF push both ⇒ re-assemble, never land). Offline
gates: YAML parse, `bash -n`, `jq` validity, and a no-`matrix.*`-in-job-`if:`
grep. A read-only shadow run against the real open PRs exercises real assembly
and real shard computation with no writes.

## Consequences

- Throughput rises without re-melting runners: one smart-CI run lands several
  already-green PRs.
- Trunk integrity is preserved by FF-CAS: only the exact bytes CI validated land.
- Phase 1 is inert until a human dispatches live with a `HONUA_MERGE_TOKEN`.

## Roadmap (deferred)

- **Phase 2** — live enablement with `HONUA_MERGE_TOKEN`, real CI poll/land,
  and the resume-from-state-issue path exercised end to end on a live batch.
- **Phase 3** — bisection-free finer attribution when ≥2 suspects share a
  failing shard, batch-size auto-tuning, and starvation-aware scheduling.

## Alternatives considered

- **Re-enable native merge queue** — rejected: it is what spiralled; it re-runs
  the full matrix per batch and cannot run a diff-targeted subset.
- **Auto-patch non-format drift (proof-ledger/OpenAPI)** — rejected: those are
  author-intent changes; the train escalates instead of guessing.
