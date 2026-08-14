# Repeated-project server-test reuse benchmark

Issue #3222 is the evidence gate for the build-reuse stage of #3213. It does
not reverse the decision in #2722: that benchmark correctly rejected a shared
producer that serialized every consumer behind every selected project. PR
#2750's shard-local exact-head cache remains the production authority for
failed-only reruns.

This benchmark tests a narrower hybrid using the current bounded payload:

- a project gets a producer only when at least two selected shards require the
  exact project/input fingerprint;
- project-unique shards keep the current independent restore/build path and
  start immediately;
- reused consumers check out and set up .NET in parallel with the producer,
  then poll for one exact same-run artifact instead of waiting on a job-level
  `needs` edge;
- every consumer validates the inner archive manifest and a separate receipt
  binding the full source/tree SHA, project, Release configuration, SDK,
  runner OS/architecture, workflow/registry/scripts, artifact digests, run,
  and 24-hour validity window;
- baseline and reused paths run the exact same filter and publish stable TRX
  evidence that excludes duration/order but includes every test identity and
  outcome.

The workflow is branch-scoped/manual, read-only, non-required, and outside PR,
nightly, release, and merge-train orchestration. Its default `core` mode runs
the predeclared two-same-project, two-mixed-project, and five-project profiles.
The deliberately expensive `observed-full` mode is manual only and derives the
current 63-shard distribution directly from `.github/ci-shards.json`; today it
selects producers for 36 Server, 10 GeoServices, and four each OData, OGC API,
and OGC Classic shards while leaving five project-unique shards independent.

## Promotion threshold

For every profile with repeated projects, hybrid reuse must have:

1. exact filter/result/outcome parity;
2. lower p90 test-command start time;
3. fewer rounded Linux runner minutes; and
4. no more than 5% wall-clock regression.

A no-reuse profile must remain exactly on the baseline projection. A tie in a
required improvement is a no-go. Passing one hosted run makes the design only
eligible for a separate 20-exact-head observe phase; it does not authorize a
production dependency or branch-protection change. The old path remains the
rollback authority throughout observation.

## Hosted procedure

Pushes to `ci/compact-server-test-reuse-benchmark` run the bounded core profile.
Use `workflow_dispatch` for `observed-full`. To prove failed-only reuse without
making every benchmark intentionally red, dispatch `core` with
`prove_failed_rerun=true`, wait for the post-evidence `server-b` failure, then
rerun failed jobs only. The producer and all green jobs must retain attempt-one
timestamps; the failed consumer must restore the same exact artifact with
`build_ms=0` and the same TRX evidence digest.

Raw metrics, TRX summaries, GitHub job/step timestamps, the immutable plan, and
the decision summary are retained together for 14 days. Final run IDs and the
promotion/no-go decision are published on #3222 and ADR-0074 before any observe
mode production change.
