# Server-test hosted transfer benchmark

Issue #2722 is the decision gate between the bounded project payload contract
in #2721 and any production orchestration change in #2708. The benchmark is an
isolated workflow; it does not run for pull requests, merge-train batches,
schedules, or releases, and it does not change `.github/workflows/ci.yml`.

## Decision threshold (declared before measurement)

A shared producer is eligible only when its measured result improves both:

1. aggregate runner time; and
2. initial time from job execution to no-build test discovery

for all three representative profiles: two shards sharing one project, two
shards using mixed projects, and five shards using mixed projects. A tie or an
improvement in only one dimension is a no-go. The threshold is encoded in
`.github/server-test-transfer-benchmark.json` and evaluated by
`summarize-server-test-transfer-benchmark.py`.

The baseline restores and builds independently in each shard. Shared artifact
and cache projections build once per unique project, then add the measured
pack/save or upload and download/restore/integrity/discovery durations. Setup
time is measured from the first runner step and therefore includes checkout and
.NET setup for every producer and consumer. Raw hosted job and step timestamps
are retained alongside the command-level millisecond measurements.

## Transfer and rerun contracts

The immutable artifact name contains the full commit SHA and project identity.
The cache key contains the archive contract version, runner OS, full commit
SHA, resolved .NET SDK, project identity, and artifact-registry hash. Restore
fallback keys are forbidden. Each consumer downloads only its own project
payload, including for the five-shard profile, so the benchmark does not imply
that scheduled or manually forced full matrices should fetch unrelated payloads.

Both transports fail closed on missing evidence. The payload manifest is valid
for at most 24 hours and restore rejects future, expired, overlong, mismatched,
oversized, unsafe, or digest-invalid evidence. Hosted negative probes exercise
missing artifact/cache behavior; the fixture exercises the remaining contract
failures.

One artifact consumer, one cache consumer, and a standalone producer probe fail
only on workflow attempt 1. Rerunning failed jobs must leave the green baseline,
producer, and consumer jobs on attempt 1 while the failed consumers reuse the
same-run artifact or exact-head cache on attempt 2 without a build. Job names
remain per-shard so attribution is not collapsed.

## Running the hosted proof

The temporary push trigger is restricted to branch
`ci/2722-hosted-transfer-benchmark` and benchmark implementation paths. Once the
initial run reaches its deliberate failures, rerun only failed jobs:

```bash
gh run rerun RUN_ID --failed
```

The `Benchmark evidence summary` job uploads `summary.json`, `summary.md`, every
raw command metric, and GitHub's raw job/step timing response for each attempt.
Artifacts are retained for seven days; project payloads are retained for one
day. The final run IDs, sizes, timings, and decision are published on #2708.

## Hosted result

Pending the first opt-in hosted run. No production change is eligible until the
attempt-2 evidence is recorded here and on #2708.
