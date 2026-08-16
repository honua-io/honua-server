# Merge-train early-failure observation

Tracking: [#3224](https://github.com/honua-io/honua-server/issues/3224), under
the CI program [#3213](https://github.com/honua-io/honua-server/issues/3213).

Smart CI can expose a blocking shard failure long before its slowest sibling
finishes. The controller currently and correctly waits for a terminal workflow
before classification, but that wait can spend tens of runner-minutes on a
batch that cannot land.

`early-failure-observe.sh` measures this interval without changing authority.
The controller retains its authoritative workflow-status read every 30 seconds;
in observe mode, that same read also carries the run id, attempt, workflow,
event, immutable head SHA, and batch branch. Every two minutes it makes one
additional, explicitly paginated jobs request, selects only router-declared
server shards, and records the first completed failure. The recorded hosted
completion timestamp—not detection time—anchors the interval, so throttling
does not distort the measurement. Its active-run request per 120 seconds is a
25% increase over the four ordinary status requests in the same interval.
When the workflow becomes terminal before the next interval, the observer may
make one bounded final jobs request so it can record GitHub's exact terminal
timestamp and the complete selected-shard result set. That exception can make
a very short run's observed overhead exceed 25%, but adds at most one final
jobs request per run; observation never replaces authority.

Snapshots are accepted only for the exact `CI` `workflow_dispatch` run whose
`train/batch/*` branch and 40-character head SHA match the assembled batch.
Jobs are read through GitHub's paginated Actions endpoint rather than the
first-page `gh run view` projection, and every job must identify the same run
attempt as its enclosing snapshot. A controller restored after interruption
reconstructs the expected branch, SHA, and shard descriptor before observing
the journaled run. Attribution probes explicitly disable the primary observer,
so their deliberately different merge trees cannot contaminate the controller's
single primary-batch sample. If later attribution rebuilds the primary batch,
the first complete classified sample remains the controller's retained record.
Completed timeout/capacity jobs are classified first from their small,
paginated exact-check annotations. Other failures use an exact-job log reader:
the Actions job-log REST endpoint is primary, with the ordinary
`gh run view --job` surface retained as fallback. This preserves immutable job
identity, avoids downloading a 20 MB log for an annotated capacity marker, and
avoids a several-minute aggregate-log race.

Conservative early categories are `deterministic-candidate`, `known-flake`,
`timeout`, `capacity`, and `infra-or-unknown`. When the initial attempt becomes
terminal, the record contains the interval from the first failure to workflow
completion and requires exactly one terminal result for every selected shard.
It also retains every job window that overlaps the post-failure interval and
sums two runner-time measures:

- `avoidable_runner_seconds` is all observed runner time after the candidate
  failed; and
- `actionable_runner_seconds` is the subset after the observer actually saw
  the candidate, accounting for polling delay.

Both fields are zero unless the early category is `deterministic-candidate`.
They sum overlapping jobs independently because parallel hosted runners are
separately consumed. The ordinary train classifier later appends its outcome
against the same run, branch, and head. A failed-job retry may advance the
attempt but cannot change that identity.

The observation is embedded in the existing merge-train metrics artifact. It
does not cancel jobs, change train state, classify a batch, drop a PR, or
authorize landing. The classifier only writes a copy of the decision it already
made; no decision reads the observation. API, pagination, identity, or log
errors are observational misses and leave the existing terminal classifier
untouched. Independently, the terminal classifier fails closed when both exact
job-log surfaces are unavailable: it escalates the batch as an evidence/control-
plane failure and cannot attribute a member from the matrix job name alone. The
raw record is retained inside the existing `merge-train-metrics` artifact for
30 days.

## Promotion boundary

Cancellation remains forbidden until at least 20 representative live train
runs are countable and show zero contradictions: every
`deterministic-candidate` must remain a real blocking failure at terminal
classification. A sample is countable only when exact run identity, a complete
terminal selected-shard set, and a conclusive classifier outcome are all
present. A candidate that becomes pre-existing, non-blocking, a known flake,
capacity exhaustion, or passes its controlled retry is a contradiction rather
than evidence for cancellation. Missing or control-plane evidence is
inconclusive and cannot increase the sample count.

Any later enforcement change requires a separate reviewed PR, a recoverable
persisted state, and fail-closed behavior for stale runs, pagination, missing
logs, retries, cancellation races, and resume.

Even in enforcement, early evidence may only stop a doomed run. A canceled or
otherwise incomplete workflow can never become merge evidence and can never
authorize landing.
