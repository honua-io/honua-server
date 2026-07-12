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

Final run: [29164316261](https://github.com/honua-io/honua-server/actions/runs/29164316261),
commit `7c083830c60407bb6709f3e1a284cf1c9cb80fa9`, .NET SDK 10.0.301 on
`ubuntu-24.04`. Attempt 1 completed with 21 successful jobs and exactly three
deliberate failures. `gh run rerun 29164316261 --failed` completed attempt 2
successfully; only the producer probe and two failed `server-b` consumers have
new execution timestamps. Producers, baselines, and the eight green consumers
retain their attempt-1 timestamps.

| Profile | Baseline runner min | Artifact runner min | Cache runner min | Baseline TTF s | Artifact TTF s | Cache TTF s |
|---|---:|---:|---:|---:|---:|---:|
| two same-project shards | 10.65 | 6.29 | 6.09 | 328.18 | 352.37 | 342.28 |
| two mixed-project shards | 9.40 | 10.94 | 10.85 | 304.86 | 343.14 | 342.28 |
| five mixed-project shards | 24.21 | 21.66 | 21.30 | 328.18 | 352.37 | 342.28 |

The shared artifact path reduced runner time by 41.0% for the same-project pair
and 10.6% for five mixed shards, but increased initial time-to-first-test by
7.4% in both. It increased both dimensions for the two-project mixed profile
(runner time +16.4%, TTF +12.6%). The shared cache path showed the same failure:
runner time improved 42.8% and 12.0% in the same-project and five-project cases,
but TTF regressed 4.3%; the two-project mixed case regressed runner time 15.5%
and TTF 12.3%.

Project archives were 143.9–158.2 MiB and exact-head cache entries were
143.2–157.3 MiB. Across projects, pack took 6.4–7.0 seconds, artifact upload
1.6–3.3 seconds, and cache save 1.8–3.2 seconds. Attempt-1 consumers measured
artifact download at 1.8–3.7 seconds and cache restore at 1.2–2.8 seconds;
integrity verification took 2.8–8.3 seconds, unpack 2.6–3.2 seconds, and no-build
discovery 3.0–10.5 seconds.

Decision: **no shared producer**. Neither transport passes the predeclared dual
threshold in every profile. The selected follow-on is a shard-local exact-head
cache saved after that shard's own build and before its tests: it leaves initial
parallel time-to-first-test unchanged while making a failed rerun proportional.
On attempt 2 the cache consumer restored, verified, unpacked, discovered, and
ran its proof selection without a build in 23.2 seconds (1.9-second transfer),
versus 29.4 seconds for the same-run artifact (3.3-second download). Production
implementation remains outside this PR and must preserve current shard names,
filters, scheduled/manual full-matrix routing, and fail-closed exact-head keys.
