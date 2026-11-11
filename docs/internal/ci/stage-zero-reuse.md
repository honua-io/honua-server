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

Six lane slots, each bounded to five .NET processors and five MSBuild workers,
limit nominal lane demand to 30 processors. PR runner services share the host
and bypass lane admission. This is not an operating-system CPU reservation or
proof of no oversubscription. Measure simultaneous lane and PR workloads before
raising concurrency. A global admission budget covering both classes is needed
to make a total-host bound enforceable.

## Post-merge queue baseline

[Affected run 42929935484](https://github.com/honua-io/honua-server/actions/runs/42929935484)
completed in 131.45 minutes. Admission before job creation took 36.65 minutes;
job queue time, measured from job creation to its first step starting, had a
36.03-minute median and 61.15-minute maximum. The run created 167 jobs and
failed, so its elapsed time is not a successful affected-run latency target.
The longest execution included AOT (34.80 minutes), the managed producer
(26.50 minutes), and the Capabilities shard (26.63 minutes).

This run predates the completed-run base selection and six-job affected cap.
Those changes need a completed subsequent affected run before an after value
can be reported. Full fallback retains complete coverage under the affected
cap; scheduled full runs retain their separate concurrency budget. Existing
selector guards determine expensive-suite eligibility, while descending shard
rank starts estimated slow work first. Queue reduction must be assessed beside
PR Gate queue times because moving the wait between workflows is not a gain.

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
