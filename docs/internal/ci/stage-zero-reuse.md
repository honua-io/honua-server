# Stage 0 evidence and cross-phase build reuse

Stage 0 currently provides local feedback. It does not authorize PR Gate to
skip tests or import compiled outputs. This document records the evidence and
requirements for a future implementation; it changes no gate decisions.

## Live Stage 0 measurements

Four live runs on 10 November 2026 used two pinned diffs: six files (+46/-7)
and 35 files (+1,012/-147). Both selected 29 unit suites because their changes
included broad architectural inputs. These are not examples of a narrow
three-test change. All four runs passed. Times below are minutes.

| Diff / run | Queue | Readiness + sync | Restore | Build | Unit tests | Total wall |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Small / cold | 0.00 | 6.15 | 0.08 | 3.80 | 1.99 | 12.38 |
| Medium / cold | 0.00 | 5.93 | 0.08 | 4.12 | 1.96 | 12.53 |
| Small / warm | 5.05 | 0.87 | 0.06 | 3.18 | 1.92 | 11.42 |
| Medium / warm | 5.12 | 1.02 | 0.06 | 2.73 | 2.02 | 11.27 |

Restore, build and tests are components of execution, not additional wall time.
Snapshot, orchestration and copy-back account for the remainder. DNS/discovery
trouble affected cold synchronization. These observations do not isolate a
causal speedup or establish a sustained p50 or builds-per-hour optimum.
Per-worktree ignored outputs survive synchronization, but a changing snapshot
revision can still invalidate generated assembly metadata. Output retention
alone is not proof that compilation was reused.

The local transport now uses HEAD directly for clean trees and a deterministic
snapshot revision for unchanged dirty trees. This removes timestamp-only
revision churn from generated assembly metadata. A changed source tree still
creates a different revision. Live repeats must verify unchanged intermediate
and output timestamps before claiming compilation reuse.

A subsequent clean-HEAD repeat removed snapshot churn as a variable. The small
prime built in 3.21 minutes; its repeat built in 3.24 minutes after 12.12 minutes
in the queue (15 seconds readiness, 27 seconds sync, 4.44 seconds restore,
5.42 minutes total execution and 11 seconds copy-back). The repeat regenerated
all inspected Core intermediate timestamps. The medium prime built in 3.63
minutes after 13.85 minutes queued (16 seconds readiness, 28 seconds sync,
4.48 seconds restore, 5.65 minutes execution and 11 seconds copy-back). All
three runs passed. The medium repeat also passed: queue 16.48 minutes,
readiness 0.25, synchronization 0.43, restore 0.07, build 2.97, unit test
invocations 2.01, execution 5.25 and copy-back 0.18 minutes; total wall was
22.62 minutes. Its outputs were absent before execution. These are observed
rebuilds, not demonstrated warm hits.

During these runs available host disk fell from 86 GiB to 28 GiB, then recovered
to 80 GiB. Transport cleanup starts below 60 GiB and removes least-recently-used
checkout outputs until 90 GiB is free, excluding the current and recently active
checkouts. Queued checkouts have no active marker, so retention while waiting
is not guaranteed. A follow-up snapshot at 03:00:34 UTC found both measured
worktrees missing their Core output assembly and generated global-usings file
after successful runs; output retention had failed. No per-eviction log was
present to attribute the deletion to a specific cleanup invocation. Some active
markers outlived their local processes; a dead local PID does not establish that its
remote child has exited. Marker cleanup must verify remote execution liveness
before permitting eviction. Do not claim incremental reuse from output paths
alone or remove marker protection based only on local process exit.

The local transport now prefers idle checkout caches for eviction before
queued checkout caches. Queued caches remain eligible if idle eviction cannot
recover the existing 90 GiB target; current and recently active checkouts retain
their protection. Five isolated cleanup scenarios passed, including the disk
pressure fallback. Live cache-hit and cycle-time effects remain unmeasured.

Six lane slots, each bounded to five .NET processors and five MSBuild workers,
limit nominal lane demand to 30 processors. PR runner services share the host
and bypass lane admission. This is not an operating-system CPU reservation or
proof of no oversubscription. Measure simultaneous lane and PR workloads before
raising concurrency. A global admission budget covering both classes is needed
to make a total-host bound enforceable.

An eleven-sample, five-minute live window on 11 November measured 36 visible
and allowed logical CPUs. CPU usage ranged from 11.91 to 23.82 busy processors
(median 18.76), with one-minute load from 14.79 to 27.31. This window did not
show CPU oversubscription. It does not establish the optimal slot/worker split
or completed builds per hour, and cannot bound demand outside that window.
These historical measurements do not recommend a current slot/worker split.
The operating limits have since changed; use the current build-host admission
policy. Comparable live completion samples must cover alternative splits and
PR runner demand before claiming a throughput optimum.

A later controlled six-file run on 11 November verified actual incremental
reuse. In one admission, cold compile took 2.71 minutes and immediate warm
compile took 0.12; feedback fell from 4.77 to 2.28 minutes. A separate admission
then retained the sampled Core DLL, reference and generated-output timestamps
across synchronization and execution: compile 0.096 minutes, feedback 2.05,
with 28 unit invocations passing. Its queue was 12.22 minutes, synchronization
0.47, restore 0.057 and tests 1.80. This proves reuse for one pinned worktree
on the same host, not a fleet cache hit rate. Disk-pressure eviction remains
possible, and queue time still dominates that warm sample.

## Post-merge queue baseline

[Affected run 42929935484](https://github.com/honua-io/honua-server/actions/runs/42929935484)
completed in 131.45 minutes. Admission before job creation took 36.65 minutes;
job queue time, measured from job creation to its first step starting, had a
36.03-minute median and 61.15-minute maximum. The run created 167 jobs and
failed, so its elapsed time is not a successful affected-run latency target.
The longest execution included AOT (34.80 minutes), the managed producer
(26.50 minutes), and the Capabilities shard (26.63 minutes).

This run predates the completed-run base selection and six-job affected cap.
The subsequent runtime-base refresh and newest-merge selection cap merged in
PR #7332. Completed runs now exist, but their selected work differs. The table
records observations, not minutes saved by that change. Times are minutes.

| Affected run | Result | Elapsed | Jobs created / executed | Job queue median / maximum | Summed job execution |
| --- | --- | ---: | ---: | ---: | ---: |
| [42929935484](https://github.com/honua-io/honua-server/actions/runs/42929935484) | Failure | 131.45 | 167 / 166 | 36.03 / 61.15 | 1,529.33 |
| [43440336773](https://github.com/honua-io/honua-server/actions/runs/43440336773) | Failure | 60.10 | 42 / 31 | 0.07 / 21.18 | 154.25 |
| [43391539717](https://github.com/honua-io/honua-server/actions/runs/43391539717) | Success; .NET skipped | 24.50 | 30 / 8 | 0.05 / 0.07 | 4.48 |
| [43367716551](https://github.com/honua-io/honua-server/actions/runs/43367716551) | Success; .NET skipped | 5.47 | 30 / 8 | 0.05 / 0.05 | 5.85 |

The 13 November snapshot uses every page of the jobs API for each completed
run. Executed jobs have steps and are not skipped. Queue is job creation to
first-step start; execution is first-step start to job completion. Elapsed is
run creation to the latest job completion, excluding later metadata updates.
Summed execution includes parallel jobs and is an occupancy proxy, not billed
runner minutes or a count of distinct runners. Jobs can share runners.

The newer failure actually executed managed tests. Its producer took 31.98
minutes, runtime proofs 12.27 and WFS 9 shard 8.88. Its 42 job records include
31 executed jobs; the baseline created 167 and executed 166. These counts
show different selected workloads, so neither the shorter wall time nor the
lower occupancy establishes a causal speedup. The successful samples ran
routing and summary jobs but skipped the managed producer, foundation tests,
server tests and runtime proofs. They do not demonstrate faster test CI.
The 24.50-minute sample also illustrates that low per-job queue time does not
exclude delay before job creation; it had only 4.48 minutes of summed execution.

Full fallback retains complete coverage under the affected cap; scheduled full
runs retain their separate concurrency budget. Existing selector guards determine
expensive-suite eligibility, while descending shard rank starts estimated slow
work first. Queue reduction must be assessed beside PR Gate queue times because
moving the wait between workflows is not a gain.

### Remaining evidence for #7268

The controlled Stage 0 repeat above demonstrates incremental compilation for
one pinned six-file worktree. It does not supply an old-code baseline for both
small and medium diffs, a fleet hit rate, or a supported concurrency optimum.
The completed affected runs resolve the missing-run question, but do not yet
provide a successful managed-test run over a comparable baseline workload.
Retain those distinctions when reporting the issue; do not infer speedup from
skipped tests, failures, changed input ranges, or queue movement. A comparison
must pin inputs and toolchain, record admission and execution separately, and
include concurrent PR demand. No new full run or runner-setting change was
needed to collect this snapshot.

## Why the local receipt cannot skip hosted tests

The local receipt is writable by the contributor and runs code from the PR.
A matching HEAD or a digest inside that same file proves consistency, not who
executed the tests. A contributor could construct both the file and its digest.
A signature from a key available to PR-controlled code has the same problem.
PR head equality also does not prove the tested merge tree matches the tree
that CI will validate after trunk advances.

A sound implementation needs an execution authority outside PR-controlled
code. Its protected issuer must observe the actual checkout, command and test
results, then attest at least:

- repository, PR head, base and tested merge-tree identity;
- trusted workflow, selector and verification-policy digests;
- SDK, OS/architecture, dependencies, build configuration and diagnostic flags;
- every selected project, exact test filter, test identity and passing outcome;
- complete TRX and output-artifact digests, issuer identity, issuance and expiry.

The verifier must come from a protected revision and validate issuer policy,
freshness, revocation, artifact integrity and exact required-suite coverage.
Missing or ambiguous evidence must execute the existing gate path. Failed,
partial or cancelled tests cannot produce a reusable success. A changed merge
tree or test policy invalidates the receipt even if PR HEAD is unchanged.
Credentials that authorize attestations must never be exposed to PR execution.

Before enabling skips, shadow verification must reject forged, expired,
wrong-tree, changed-policy, missing-TRX and failed-test receipts while producing
identical test coverage to the current path. Current same-run reuse receipts
are useful implementation references, but their trust boundary does not by
itself authorize importing evidence from a contributor-controlled host. See
[the existing benchmark](server-test-reuse-benchmark.md) for artifact binding
and parity requirements. No hosted skip is enabled by this proposal.

## Five recent PR observations

The five most recent successful completed PR Gate runs, one per branch, were
sampled on 11 November 2026. Producer logs explicitly reported
`enable-build-cache: false` in every sample; NuGet package restoration succeeded
from cache in all five.

| PR Gate run | Compiled-output cache enabled | NuGet restored | Published project outputs |
| --- | --- | --- | ---: |
| [42936088567](https://github.com/honua-io/honua-server/actions/runs/42936088567) | No | Yes | 0 |
| [42934119807](https://github.com/honua-io/honua-server/actions/runs/42934119807) | No | Yes | 12 |
| [42934007974](https://github.com/honua-io/honua-server/actions/runs/42934007974) | No | Yes | 14 |
| [42932247060](https://github.com/honua-io/honua-server/actions/runs/42932247060) | No | Yes | 0 |
| [42931709729](https://github.com/honua-io/honua-server/actions/runs/42931709729) | No | Yes | 0 |

Compiled-output cache hit rate is undefined: there were zero enabled attempts,
not five misses. Minutes saved are unmeasured, not zero. Published same-run
artifacts do not demonstrate cross-phase cache hits. This sample cannot justify
enabling `PR_GATE_BUILD_CACHE` or estimate its benefit.

## Required experiment before cross-phase promotion

Keep the existing build-cache opt-in disabled by default. Compare baseline and
candidate paths for the same five representative input sets, measuring restore,
compile, cache transfer and total critical-path minutes separately. Include
cold, warm, changed-dependency and no-reuse cases. Record enabled attempts,
exact hits, fallback hits, misses and invalidations explicitly; report savings
net of restore/save costs. Retain the existing week-long p90 observation
requirement before changing the default.

A content key must cover project and transitive dependency inputs, imported
MSBuild files, source generators, dependency locks, SDK/toolchain, platform,
configuration, compiler/analyzer flags and relevant environment. Unknown or
unmodelled inputs must rebuild. Reuse must retain warnings-as-errors and test
selection. Protect cache writes and verify payload provenance so untrusted PR
outputs cannot poison later trusted builds. Avoid restoring stale absolute
paths or credentials from `obj`.

Promotion requires exact diagnostics and test-result parity, lower critical-path
p90 and lower total runner minutes including transfers. Retain the uncached
path as rollback. Previous same-run fan-out experiments increased latency and
runner minutes despite correct results; build reuse must earn its own measured
benefit rather than assume that retained outputs are faster.
