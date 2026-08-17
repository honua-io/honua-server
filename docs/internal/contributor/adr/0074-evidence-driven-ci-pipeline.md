# ADR-0074: Evidence-driven CI pipeline

## Status

Proposed (2026-08). This decision is the architecture contract for
honua-server#3213 and its cross-repository children. Existing gates remain
authoritative until a shadowed replacement satisfies the promotion criteria in
this ADR.

One slice has left shadow: producer-free attempt-1 reuse of the shard-local
exact-head server-test payload is live in `ci.yml` (see section 5). Every other
build-reuse topology in this ADR — shared producer jobs and PR Gate build
evidence — remains report-only and unpromoted.

## Context

Honua's CI has accumulated useful local optimizations without changing the
end-to-end feedback loop. A pull-request push currently starts expensive work
before exact-head review settles. Governed generated drift can be discovered
after build and tests, forcing a derived-only commit that invalidates review and
all exact-head checks. Merge-train normalization runs before the batch CI is
visible. Server shards may compile the same project independently. A known
deterministic batch failure does not stop unrelated long-running shards.
Native-image and SDK browser jobs can spend tens of minutes on a head that a
review fix supersedes.

Manual cancellation and rerun behavior has reduced some waste, but it is not a
reliable control plane. Different operators can race to cancel or restart a
run, and GitHub records the resulting cancellations against an otherwise valid
head. CI needs one explicit state machine rather than a collection of trigger
side effects.

Prior evidence also prevents a simplistic answer. Issue #2708 proposed one
shared producer for server-test shards. The hosted #2722 benchmark rejected the
then-current design: 144-158 MiB project artifacts increased initial
time-to-first-test for every representative profile, and the mixed two-project
profile also increased aggregate runner time. The selected replacement on
trunk is the #2735 shard-local exact-head cache, which preserves parallel cold
builds and makes failed-only reruns proportional. Any new producer design must
beat that measured baseline; “build once” is not sufficient if every consumer
waits longer or costs more.

The pipeline must preserve these existing guarantees:

- Codex review and unresolved-thread evaluation are exact-head decisions.
- `PR Gate` remains the required branch-protection context and is published by
  the existing GitHub Actions application.
- Generated catalogs, OpenAPI, capability evidence, CodeQL, native-image
  boundaries, and merge-tree tests remain fail closed.
- Untrusted pull-request code never executes with a repository write token or
  production secret.
- A merge train lands only the exact merge tree that its authoritative evidence
  covered.

## Decision

### 1. One pipeline state machine

Every pull-request head moves through four ordered states:

| State | Purpose | Allowed expensive work | Exit condition |
|---|---|---|---|
| `normalize` | Produce and compare governed derived outputs | Only declared generators and their minimal build prerequisites | Head is derivation-clean, or a safe generated-only update creates a new head |
| `review` | Run cheap admission and collect exact-head Codex evidence | No full PR Gate, CodeQL, native images, or browser matrix | Exact head has clean review evidence and no unresolved Codex thread |
| `verify` | Produce all required exact-head evidence once | Impact-selected build, test, analysis, image, and browser jobs | Required evidence manifest is complete and successful |
| `train` | Validate the exact cumulative merge tree and land by compare-and-swap | Only merge-tree evidence not safely reusable from member heads | Batch evidence is complete and trunk still equals the assembled base |

State transitions are monotonic for one head. A push creates a new subject and
returns to `normalize`; it never inherits exact-head review. A workflow retry
continues the same state and attempt contract. It does not create a second
logical verification request.

One trusted orchestrator owns transitions. Individual expensive workflows do
not independently infer that a head is ready.

### 2. Separate decisions from reusable evidence

Exact-head decisions and reusable execution evidence are different artifacts:

- **Review evidence is not reusable.** It binds to one full commit SHA, Codex
  identity, review/comment timestamp, observed check state, and complete thread
  pagination.
- **Execution evidence may be reusable** only when the complete input
  fingerprint is identical. A trusted attestor may associate that evidence
  with a newer subject SHA after independently proving the fingerprint match.
- **Merge evidence binds to the exact batch tree.** Member-head evidence can
  satisfy an unchanged input slice, but it cannot replace integration tests
  whose inputs include the cumulative merge tree.

Every reusable evidence object has an immutable manifest containing at least:

```text
contract and schema version
producer workflow/action/script digests
relevant source-tree input digest
project, dependency, lock, and generator input digests
toolchain and SDK versions
runner OS, architecture, and image identity
artifact file digest and bounded size
producing repository, run, attempt, and subject SHA
creation and expiry timestamps
result and covered evidence class
```

The fingerprint is an allowlisted input graph, not merely a commit SHA and not a
mutable branch cache. Any unmodelled input is a cache miss.

Evidence stores use exact keys only. Prefix fallback, mutable tags, and
best-effort acceptance are prohibited. A missing, expired, oversized,
incompatible, or digest-invalid object is deleted or ignored and the consumer
rebuilds. Forks and untrusted jobs can read permitted public evidence but cannot
publish authoritative reusable evidence.

### 3. Normalize before review without crossing the trust boundary

Normalization uses two workflows:

1. An unprivileged `pull_request` workflow checks out and executes the pull
   request. A generation job runs each declared generator twice from the same
   committed-output baseline and uploads only byte-identical, bounded JSON
   projections. A separate fresh runner packages those data files
   against a clean exact-head checkout, so generator/setup mutations cannot
   alter provenance inputs or the envelope builder. The envelope contains the
   source SHA and Git tree, contract version, allowlisted derived path names,
   exact output blobs/digests, and generator evidence. It contains no executable
   path, symlink, submodule, workflow, or arbitrary archive layout.
2. A trusted default-branch `workflow_run` consumer does not execute pull-request
   scripts. It validates the completed producer identity, repository, event,
   current PR head, envelope schema, path allowlist, size bounds, and every
   digest using default-branch validator code. For a same-repository branch, it
   may commit only the validated derived blobs if the head has not moved. Forks
   receive the envelope and a failing drift check but no write operation.

The trusted consumer refuses executable paths, workflow/action files, symbolic
links, submodules, path traversal, binary patches outside the declared derived
formats, and any output not named by the default-branch allowlist. It compares
the resulting tree before pushing; an empty result succeeds without a commit.
The commit includes a loop-prevention marker, while idempotent tree comparison
is the primary loop guard.

In observe mode the consumer publishes no status or branch mutation. Its output
is explicitly a candidate and is excluded from accuracy evidence until an
independent exact-head PR Gate corroborates a no-op, or authoritative drift
checks pass on the subsequent normalized head. Reproducibility and provenance
are necessary admission signals, not mutation authority.

The 2026-08-16 shadow audit closed that evidence requirement: 61 validated
envelopes over 16 pull requests produced 29 independently corroborated heads,
two byte-exact would-change candidates, and zero false positives
(`docs/internal/ci/derived-artifact-normalization.md`). The Git-object
compare-and-swap transition is therefore implemented and covered by fixtures,
gated behind `NORMALIZATION_MODE: enforce` plus a per-run GitHub App token
minted immediately before the mutation. The workflow `GITHUB_TOKEN` keeps
`contents: read` in every mode; the repository token cannot be the write
credential because a push made with it produces no `pull_request` events and
would strand the normalized head without its required contexts. The swap is the
GraphQL `updateRefs` mutation with `beforeOid` — REST `updateRef` with
`force: false` only guarantees a fast-forward from the ref's value at update
time and would silently reinstate a commit dropped by a concurrent backward
force-push. Because the commit voids exact-head review evidence, the consumer
re-requests review from both attesting lanes as part of the mutation.

Review is requested only after normalization reaches a stable head. Generated
drift remains independently checked in `verify` and `train`; early
normalization changes when the error is found, not whether it is enforced.

### 4. Automate exact-head review-first verification

The first `PR Gate` attempt for a new head performs bounded admission only. If
exact-head `Review Gate` is absent, it terminates before restore/build/test and
records a review-pending outcome. Branch protection requires both `PR Gate` and
`Review Gate`, so PR-authored verification code cannot substitute for trusted
exact-head admission.

Review events may pass through a read-only, no-checkout bridge because GitHub
executes those events from the PR merge branch. Its completion is a best-effort
latency hint that wakes
`review-gate.yml`; trusted default-branch logic then publishes clean evidence,
finds the unique admission run for that head, and reruns it exactly once. The
resumed PR workflow remains an
unprivileged `pull_request` execution and performs expensive verification only
after rechecking the exact current head and successful Review Gate status.

Idempotency is enforced by the head SHA, workflow id, original run id, attempt,
and a single transition receipt. Repeated review events cannot start another
verification. A newer head cancels an older head's work through a per-PR
concurrency group keyed only by the resolved PR number. No privileged event
checks out or executes the PR, and there is no PR-ref manual dispatch path.
The bridge is not an authority because PR code can suppress it: live train
selection and pre-land both independently fetch current review evidence, refresh
the status, and fail closed. A trusted `repository_dispatch` provides explicit
default-branch re-attestation after a thread is resolved without an event.

The same transition receipt gates optional exact-head CodeQL, native-image, and
SDK browser work. Workflows do not need an operator to cancel every review-fix
push.

### 5. Reuse server builds only where current evidence wins

Server verification chooses one of two measured modes per selected test
project:

- **Repeated project:** when two or more selected shards consume the same
  project and input fingerprint, one producer may build and package the current
  bounded project payload. Shards verify the immutable manifest and run with
  `--no-build --no-restore`.
- **Project-unique or producer-ineligible:** retain the current parallel
  shard-local restore/build path and exact-head failed-rerun cache.

The producer is not enforced until a hosted shadow A/B using the current compact
payload improves both aggregate billed runner time and p90
time-to-first-test for representative two- and five-shard profiles. It must
also prove failed-only reruns, exact-key rejection, producer-failure fanout,
independent check names, and unchanged filters/results. If the threshold does
not pass, the pipeline keeps shard-local execution and the “build once” design
returns to optimization rather than becoming policy by assertion.

This is one build per repeated project fingerprint, not one oversized solution
artifact and not one build per logical shard.

#### Producer-free attempt-1 reuse (live, 2026-08-16)

One narrow slice of "build once" is live in `ci.yml`, because it needs no
producer and adds no wait. The single designated writer shard for each project
already packaged and saved its exact-head payload on attempt 1; only the *read*
was gated on `run_attempt > 1`, so the same project was rebuilt in every sibling
shard while a valid identical-tree payload sat unread in the run's own cache
scope. Shards now perform one single-shot, fail-open lookup on attempt 1 as
well. Nothing polls or waits, so the run 31768277005 fan-out regression cannot
recur, and cache write volume is unchanged.

The full contract — run-scoped keys and the same-SHA TTL poisoning they prevent,
writer selection, the single kill-switch rule, the three fail-open levels, the
trust boundary, and the accepted limitations — lives in
[`docs/internal/ci/server-test-binary-artifacts.md`](../../ci/server-test-binary-artifacts.md).
Rollback is `HONUA_SERVER_TEST_ATTEMPT1_REUSE=false`.

This promotion is deliberately narrower than the shadowed producer designs. It
does not authorize a shared producer job, PR Gate payload consumption, or any
cross-head/content-addressed key; those remain gated on the promotion criteria
above and on the #3226 ledger.

The current shadow candidate reuses a sunk build rather than adding a producer
dependency. The required `PR Gate` already performs the full Release solution
build after exact-head review. Once that gate is green, a best-effort step may
package at most two registered projects that are each consumed by two or more
trusted selected shards. Packaging and upload failure are cache misses and
cannot change the required gate result.

A read-only default-branch `workflow_run` observer validates only the small
metadata artifact. It re-resolves the canonical PR Gate run/check/attempt and
current PR identity, recomputes the merge tree and repeated-project plan, binds
every execution policy blob, and confirms the large payload's GitHub artifact
id, name, size, and digest before issuing a small trusted receipt. It never
executes the payload.

The merge train carries that source identity only for an exact one-member
batch. Smart CI accepts it only when the complete consumer Git tree equals the
producer merge tree, then validates and safely extracts the bounded payload and
runs registered proof filters with `--no-build --no-restore`. This is report-only:
the authoritative shards still restore and build independently, `CI Gate` does
not depend on the shadow, and multi-PR/different-tree/missing/invalid evidence
falls back without waiting. `HONUA_PR_GATE_BUILD_REUSE_SHADOW=false` or absence
removes producer, lookup, handoff, and consumer work in one rollback. The exact
contract and promotion procedure are documented in
`docs/internal/ci/server-test-prebuild-shadow.md` (honua-server#3226).

The retained ledger partitions observations by the complete measurement-policy
digest. If the same exact head is measured more than once under that policy,
the highest verifier run id (then attempt and artifact id) is authoritative and
older valid receipts are reported as superseded rather than as integrity
corruption. The newer observation may remove a previously countable head; a
repeat can never manufacture two samples from one head.

### 6. Fail fast without manufacturing success

For a single-PR train batch, the first deterministic gating failure cancels
unrelated matrix work within two minutes. Logs, timing records, test results,
and the failing evidence class are retained. The batch fails; cancellation can
only shorten the failure path.

For a multi-PR batch, the controller cancels unrelated work and continues with
only the failing shard classes required by the deterministic path-to-owner
mapping. Ambiguous attribution fails closed or runs focused probes. It does not
wait for unrelated green shards merely to classify a known failure.

Classified infrastructure flakes and timeouts retain one controlled
failed-job-only retry. GitHub's terminal `cancelled`, `timed_out`, and
`startup_failure` job conclusions are retry inputs, not evidence: they bypass
optimistic allowlists and pre-existing-failure subtraction, and only a newer
explicitly successful attempt can satisfy the gate. A repeated timeout,
unknown signature, evidence incompleteness, cancellation failure, or
attribution ambiguity is a real non-success. Recovery clears or resumes durable
phase/label state idempotently; it never dispatches duplicate batch CI.

Before cancellation authority exists, the live controller runs a read-only
shadow observer. Each retained record is bound to the exact CI run id, initial
attempt, workflow-dispatch event, batch branch, and immutable batch SHA; job
enumeration is explicitly paginated and attempt-bound. The terminal classifier
appends its already-made outcome, but no train decision reads the observation.
Promotion requires at least 20 countable deterministic candidates with complete selected
shards and zero contradictions. The record separately measures ideal
post-failure runner time and the actionable subset remaining after observation,
so polling latency is charged rather than hidden.

### 7. Route specialized evidence by inputs

Native-image evidence follows #3204. A fixture-backed router maps Native-AOT
compile risk, final-rootfs risk, worker dependency/entrypoint risk, and
vulnerability-scan inputs to the lane that must prove them. Identical image
input digests can reuse immutable attestations; publication remains blocked
until the required PR, nightly, release, or deploy evidence exists.

Observation resolved which half of that pays. Path routing does not: across 74
distinct trusted heads the graph-derived selector reproduced the legacy path
filters exactly, with zero narrowed, zero avoided, and zero candidate-only
heads, because the serving closure covers 27 of 33 `src/**` projects and every
managed change selects all three serving variants. Re-push churn does: 60% of
serving-impacted heads and 71% of worker-impacted heads repeat an input set
already built on the same pull request, against a median 140-minute serving
build. The router therefore stays in observe mode and the promotion savings gate
accepts either narrowing/avoidance or exact-input build reuse, with the
observation receipt (`.../v3`) binding one content digest per image class over
the merge tree the images are actually built from, so reuse eligibility is
measured before anything is enforced. The ledger names the substantiated
mechanism, because a reuse-only sample does not authorize promoting the router.
Vulnerability scanning is never reused. See
`docs/internal/ci/native-image-impact-routing.md`.

The JavaScript SDK follows honua-sdk-js#1286. One immutable build feeds
independent SDK, MCP, and browser jobs. Browser coverage is split by owned
failure domain so offline, realtime, examples, and heavyweight map tests can be
rerun independently. Offline-shell manifests are normalized from official
build output before Playwright, so a stale asset digest is an early named drift
failure rather than a late suite-wide symptom.

### 8. Observe before enforcing

Every decision-changing slice has `observe` and `enforce` modes. Observe mode
runs beside the current authority, publishes comparison artifacts, and cannot
land, merge, satisfy branch protection, or publish release evidence. Promotion
requires:

- at least 20 representative parity runs with no missing or contradictory
  result;
- a reproducible 30-run before/after cost and latency sample;
- p90 admission below 5 minutes;
- p90 required post-review verification below 15 minutes;
- deterministic-failure cancellation below 2 minutes;
- at least 60 percent fewer billed PR/train minutes for the measured
  representative workload; and
- security, exact-head, generated-artifact, image, and merge-tree evidence
  invariants all passing their fixtures.

The old path remains selectable through a documented rollback input for the
first enforcement period. Rollback changes routing only; it does not edit
branch protection during an incident.

### PR Gate reuse shadow checkpoint (2026-08-15)

The standalone opportunistic producer described by the following historical
checkpoint did not meet the 60% runner-minute threshold and is not being
promoted. The next candidate packages build outputs that the required PR Gate
already produced, validates them through a default-branch receipt, and proves
tree-equivalent reuse in one-member Smart CI without influencing any gate.

The implementation remains disabled until it is reviewed and merged. After
merge, enabling `HONUA_PR_GATE_BUILD_REUSE_SHADOW=true` authorizes only the
report-only observation. It does not authorize shard build removal. Hosted
results from this policy cohort must be recorded here before any enforcement
proposal.

### Historical hosted shadow checkpoint (2026-08-14)

The first opportunistic prebuild candidate passed its bounded hosted A/B and is
enabled only as a read-only producer-availability observer. It is not
verification authority and it does not change branch protection, shard
filters, test results, or the independent shard-local build fallback.

The observed evidence is:

- [Current-head producer observation 31798013710](https://github.com/honua-io/honua-server/actions/runs/31798013710)
  built one exact `Honua.Server.Tests` payload for head
  `4e2fb806983d42e2ceb6b19cd396c272ad21a3de`. The immutable artifact was
  173,196,743 bytes and carried the expected plan and receipt.
- [Two-shard A/B 31798575655](https://github.com/honua-io/honua-server/actions/runs/31798575655)
  had no parity or reuse failures. Baseline versus opportunistic measurements
  were 14 versus 12 rounded runner-minutes, 380,204 versus 80,446 ms to first
  test, 383,382 versus 108,622 ms p90 to first test, and 394,000 versus 120,000
  ms wall time. Producer cost is included in the opportunistic totals.
- [Five-project hybrid A/B 31799159098](https://github.com/honua-io/honua-server/actions/runs/31799159098)
  had no parity or reuse failures. Baseline versus opportunistic measurements
  were 32 versus 30 rounded runner-minutes, 320,199 versus 82,270 ms to first
  test, 387,773 versus 344,429 ms p90 to first test, and 397,000 versus 353,000
  ms wall time. Producer cost is included in the opportunistic totals.
- Two negative observations also failed closed as designed: a profile with no
  repeated project skipped the producer, and an older source head missing a
  required fingerprint input could not publish trusted reusable evidence.

Both representative profiles met the predeclared A/B requirement that runner
time and p90 time-to-first-test improve without parity loss. The billed-minute
improvement was only two rounded runner-minutes in each profile (about 14.3
percent and 6.3 percent), however, so this evidence is **not** sufficient for
enforcement. In particular it does not satisfy the ADR's 60 percent promotion
threshold. Repository variable `HONUA_SERVER_TEST_PREBUILD_SHADOW=true` starts
automatic exact-head producer-availability observations. Those plan, build,
package, and timing results do **not** count toward the required 20 parity
observations because the observer does not consume the artifact or compare test
outcomes. Twenty distinct representative exact heads must also pass either the
manual benchmark consumer or a separately reviewed read-only parity consumer
that executes the same selection through both paths and compares stable result
identities. Independent shard builds remain the authority until that consumer
exists and every promotion criterion passes.

The control-plane prerequisites were also exercised on hosted infrastructure:

- [Review Gate re-attestation 31799859173](https://github.com/honua-io/honua-server/actions/runs/31799859173)
  successfully published an exact-head attestation using default-branch code.
- PR #3221 then landed through a one-member synthetic train. Its
  [exact merge-tree CI 31800619798](https://github.com/honua-io/honua-server/actions/runs/31800619798)
  passed with no failing job, and controller
  [31800106923](https://github.com/honua-io/honua-server/actions/runs/31800106923)
  compare-and-swapped the covered tree to trunk.
- That live train spent about 6.5 minutes regenerating deterministic derived
  artifacts before the synthetic CI run became visible. This is recorded as a
  separate normalization-latency target; it must not be hidden inside test
  savings or treated as reusable evidence without its own fingerprint.

The complete checkpoint and decision are also recorded on
[honua-server#3226](https://github.com/honua-io/honua-server/issues/3226#issuecomment-5293201747).

## Implementation sequence

1. Completed: merge and measure bounded #3209 / PR #3210 emitter reuse.
2. Completed in observe mode: review-first transition receipts and trusted
   dispatch fixtures (#3216).
3. Completed in observe mode: data-only normalization envelope and trusted
   allowlist validator (#3219). Its 20-head shadow audit passed on 2026-08-16
   and the credential-gated enforce transition is implemented; activation waits
   only on the scoped normalization App credential.
4. Rejected the standalone producer for insufficient savings; shadow the sunk-
   cost PR Gate build-evidence topology under #3226.
4a. Completed: producer-free attempt-1 reads of the already-written shard-local
   exact-head payload in `ci.yml` (see "Producer-free attempt-1 reuse" above).
   This is the only build-reuse slice currently enforced in production.
5. Promote deterministic-failure cancellation and focused attribution only
   after #3224's retained observations pass. The 2026-08-16 audit found 0 of the
   required 20 countable samples across 149 train runs (23 live batch-CI
   dispatches, all without a failing selected server shard), so cancellation
   stays disabled and `TRAIN_EARLY_FAILURE_MODE` remains `observe`.
6. Promote native-image evidence reuse under #3204 after its impact ledger
   passes. Path-based routing narrowing measured zero savings on this
   repository and is not a promotion candidate on its own.
7. Do not pursue docs-only PR Gate routing. #3235 was closed as not planned on
   2026-08-17 UTC: the audit in `docs/internal/ci/impact-routing-evidence.md`
   found 2 docs-only heads against a required cohort of 20, for a route worth
   about 2.5% of gate runs, and found seven internal documents whose content a
   gate step asserts. The classifier excludes those documents and a drift guard
   keeps the exclusion honest, so the report-only observer stays truthful; the
   required gate is unchanged.
8. Implement SDK build/browser separation under honua-sdk-js#1286.
9. Promote each slice only after its declared parity, latency, cost, and
   security gates; keep one independent rollback per slice.

Each implementation slice has its own issue and PR. The umbrella PR does not
combine workflow rewrites.

## Consequences

### Positive

- Review-driven SHA churn no longer starts heavyweight work by default.
- Generated drift is corrected before review while remaining enforced later.
- Exact build/test/image evidence can survive a non-input documentation change
  without pretending that review survives a SHA change.
- Failed feedback becomes proportional: one failed shard does not replay or
  wait for unrelated successful work.
- Runner-minute, latency, and cancellation claims become measured promotion
  gates rather than anecdotes.
- Security boundaries are explicit and fixture-testable.

### Costs and risks

- The trusted orchestrator and normalization consumer are security-sensitive
  code and require tighter review than ordinary CI YAML.
- Input graphs must be maintained. An omitted input is unsafe, so unknowns
  deliberately reduce cache hits by forcing a rebuild.
- Build artifacts consume storage and transfer time. The measured selector can
  reject producer mode when it is not an improvement.
- Splitting jobs increases workflow graph complexity and check count even while
  it reduces rerun cost.
- Cancellation can hide secondary failures from one run. Shadow sampling,
  nightly full evidence, and focused attribution retain discovery without
  charging every known-failed PR for the full matrix.
- Cross-repository rollout requires coordinated evidence schema versions, but
  the server and SDK can promote independently behind their own switches.

## Alternatives considered

- **Continue manual review-first cancellation.** Rejected: it is race-prone,
  leaves cancelled statuses, and depends on an operator watching every push.
- **Run all workflows on every SHA and rely on concurrency cancellation.**
  Rejected: long jobs can consume substantial time before cancellation and
  identical inputs produce no reusable result.
- **Use commit SHA as the only cache identity.** Rejected: it prevents safe
  reuse across non-input changes and does not describe workflow/toolchain
  compatibility.
- **Reuse review across equivalent trees.** Rejected: exact-head review is a
  human/agent decision about a particular commit and is never inferred from a
  build fingerprint.
- **Immediately restore the #2708 shared producer.** Rejected: hosted evidence
  showed it regressed feedback. Only a compact, repeated-project selector may
  advance after a new A/B passes.
- **Remove image or browser coverage from PRs.** Rejected: the evidence is
  valuable. The decision routes and reuses it without permitting unverified
  publication.

## References

- Umbrella: honua-server#3213
- Architecture and baseline child: honua-server#3214
- Native image routing: honua-server#3204
- Merge-train emitter reuse: honua-server#3209 / PR #3210
- Prior producer decision: honua-server#2708, #2722, #2735
- SDK build/browser child: honua-sdk-js#1286
- Existing train contract: ADR-0055
