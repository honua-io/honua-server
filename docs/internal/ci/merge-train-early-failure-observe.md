# Merge-train early-failure observation

Tracking: [#3224](https://github.com/honua-io/honua-server/issues/3224), under
the CI program [#3213](https://github.com/honua-io/honua-server/issues/3213).

Smart CI can expose a blocking shard failure long before its slowest sibling
finishes. The controller currently and correctly waits for a terminal workflow
before classification, but that wait can spend tens of runner-minutes on a
batch that cannot land.

`early-failure-observe.sh` measures this interval without changing authority.
Every two minutes it replaces one ordinary status request with a hosted job
snapshot, selects only router-declared server shards, and records the first
completed failure. The recorded hosted completion timestamp—not detection
time—anchors the interval, so throttling does not distort the measurement. Its
conservative categories are `deterministic-candidate`,
`known-flake`, `timeout`, `capacity`, and `unknown`. When the run becomes
terminal it records the interval from that failure to workflow completion.

The observation is embedded in the existing merge-train metrics artifact. It
does not cancel jobs, change train state, classify a batch, drop a PR, or
authorize landing. API/log errors are observational misses and leave the
existing terminal classifier untouched.

## Promotion boundary

Cancellation remains forbidden until at least 20 representative live train
runs show that every `deterministic-candidate` remained a real blocking failure
at terminal classification. Any later enforcement change requires a separate
reviewed PR, a recoverable persisted state, and fail-closed behavior for stale
runs, pagination, missing logs, retries, cancellation races, and resume.

Even in enforcement, early evidence may only stop a doomed run. A canceled or
otherwise incomplete workflow can never become merge evidence and can never
authorize landing.
