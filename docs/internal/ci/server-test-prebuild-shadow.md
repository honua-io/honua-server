# Server-test build evidence shadow

Issue #3226 owns the bounded server-test build-reuse experiment under ADR-0074.
Production server-test shards remain independent and authoritative. No artifact
described here can satisfy review, `PR Gate`, `CI Gate`, CodeQL, generated-data,
native-image, release, or merge authority.

## What is live vs. still shadow (2026-08-16)

**Live in `ci.yml`:** producer-free attempt-1 reuse of the shard-local
exact-head payload. Contract, kill switch and fail-open behaviour:
[`server-test-binary-artifacts.md`](server-test-binary-artifacts.md). It adds no
producer, `needs:` edge, poll or wait, so it cannot reproduce the run
31768277005 fan-out regression.

**Still shadow, unpromoted:** everything else in this document — the PR Gate
build-evidence producer, the trusted observer receipt, the train handoff, and
the Smart CI consumer proof. Ledger status as of the 2026-08-16 scheduled run
(`server-test-prebuild-evidence-ledger.yml` run 31950724055): 3 of the required
20 countable exact heads, 1 of 2 required profiles, 65.23% rounded
runner-minute savings, p90 test start 380,464 ms -> 94,640 ms, zero integrity
failures, 33 successful shells excluded for missing/invalid evidence.
Recommendation `insufficient-evidence`; every promotion gate except
`integrity_clean` is still `false`.

`server-test-prebuild-parity.yml` has produced **no** parity artifacts to date:
sampled runs resolve no subject and skip the candidate/baseline matrix, so there
is currently no direct proof at the workflow level that a prebuilt payload
yields identical test results. The equivalent evidence that does exist is the
`prove-server-test-binary-artifacts.sh` contract proof (clean detached worktree,
empty NuGet cache, full discovery plus representative execution with
`--no-build --no-restore`) and the observe lane's no-build proof test.

## Current decision (2026-08-15)

Two earlier producer topologies were measured and not promoted:

- the original 144–158 MiB shared producer delayed time-to-first-test; and
- the later opportunistic pre-review producer preserved parity but improved the
  two hosted profiles by only 14.3% and 6.3% rounded runner-minutes, below the
  ADR's 60% promotion threshold.

The current shadow does not add another build to the causal path. `PR Gate`
already performs the required full-solution Release build. After that build is
green, it may package at most two registered test projects that the trusted
shard plan says are each used by at least two selected shards. Packaging and
upload are best-effort. Failure or absence is a cache miss and cannot change the
required `PR Gate` conclusion.

A trusted default-branch observer validates the small producer metadata before
issuing a receipt. A one-member merge train may pass that exact successful PR
Gate run identity to Smart CI. Smart CI then performs a report-only proof:

```text
PR Gate Release build
  -> bounded payload + metadata (untrusted evidence)
  -> trusted default-branch metadata validation + receipt
  -> exact one-PR train handoff
  -> exact-tree payload validation + safe extraction + no-build proof test
  -> retained shadow result

authoritative server shards
  -> independent restore + build + test (unchanged)
```

Multi-PR batches, trunk-advanced batches, non-identical trees, forks, missing or
ambiguous artifacts, policy changes, expired evidence, toolchain changes, and
every validation error take the existing independent-build path immediately.
The shadow never makes a consumer wait for an artifact.

## Kill switch and rollback

The repository variable `HONUA_PR_GATE_BUILD_REUSE_SHADOW` is the only switch
for this experiment. It must equal the lowercase string `true` to enable work.
When it is false or absent:

- `PR Gate` does not package or upload build evidence;
- merge-train selection does not query the source PR Gate run;
- the train passes no reuse identity to `ci.yml`; and
- the Smart CI shadow job is not instantiated.

Unset the variable for immediate rollback. This changes no required context,
branch-protection rule, shard filter, or merge authority. A later enforcement
proposal must use a separately reviewed mode/contract; changing the meaning of
the shadow switch is prohibited.

## Trust and identity boundary

### Producer

The producer runs in the ordinary unprivileged `pull_request` PR Gate after the
Release build. Its payload is never trusted merely because GitHub stored it.
The producer records the repository, PR, event-time base, source head, PR merge
commit and tree, run/attempt, Release configuration, .NET SDK, runner
OS/architecture/image, selected-shard descriptor, plan, manifests, sizes,
digests, and TTL. Each project is allowlisted by
`.github/server-test-artifact-projects.json` and must serve at least two selected
shards.

### Trusted observer

`pr-gate-impact-observe.yml` executes the default-branch workflow definition
with read-only Actions, checks, contents, and pull-request permissions. It:

- resolves the unique GitHub-managed `PR Gate` check/run association;
- requires a completed successful same-repository `pull_request` run at the
  exact run id and attempt;
- rechecks the open, non-draft PR's current base and head;
- recomputes the merge tree and repeated-project plan from trusted policy;
- requires every execution-relevant policy blob to match the producer tree;
- checks the exact GitHub artifact id, name, size, and SHA-256 digest; and
- emits only a bounded, seven-day receipt.

It downloads metadata, not the large payload. It has no status, dispatch,
branch-write, label, train, or merge permission.

### Train handoff and consumer

Selection accepts reuse metadata only from one canonical successful `PR Gate`
CheckRun. The train carries it only when the admitted batch has exactly that one
PR and valid full identities. Multi-member, attribution, malformed, or resumed
state without complete metadata clears the optional inputs.

The Smart CI shadow locates one trusted observer receipt and binds its observer
run/attempt to the actual artifact-producing workflow. It then resolves one
exact source payload by artifact id and rechecks its name, size, digest, source
run, source attempt, repository, and head through the Actions API. Acceptance
requires the consumer commit's complete Git tree to equal the producer merge
tree. Different commit IDs are permitted only when their full trees are
identical.

Before extraction, the trusted contract rejects:

- non-canonical JSON, duplicate keys, unknown/missing fields, or invalid names;
- more than two projects or a project not repeated across selected shards;
- receipts older than 24 hours or from a different SDK/runner/configuration;
- changed workflow, action, router, registry, packager, validator, restore, or
  train policy blobs;
- archives above 256 MiB, total payload above 512 MiB, expansion above 512 MiB,
  or more than 100,000 entries;
- absolute paths, parent traversal, backslashes, controls, duplicate normalized
  paths, symlinks, hardlinks, devices, and FIFOs; and
- manifest, archive, tree, or GitHub artifact digest mismatches.

Only after those checks does the shadow restore with owner/permission
preservation disabled and run the registered proof filter with
`--no-build --no-restore`. The result is retained as
`honua.pr-gate-server-test-shadow/v1` with `mode: observe`, `mutation: none`,
and `promotion_authority: none`. `CI Gate` does not depend on this job.

## Measurement and promotion

The new topology must be evaluated as a complete system. Charge all additional
work, including:

- PR Gate packaging, hashing, and upload for every qualifying head;
- trusted observer metadata validation and receipt upload;
- payload download, validation, extraction, toolchain setup, and proof tests;
- work wasted on superseded or never-trained heads; and
- storage and transfer failures that fall back.

Compare that total with the shard-local restore/build work that enforcement
would actually remove. Queue time and wall-clock are reported separately from
rounded runner-minutes; neither may be hidden inside shell timers.

Promotion remains forbidden until the current policy cohort has:

1. at least 20 distinct representative exact-tree shadow observations with
   unchanged discovery, filter, test identity, count, and outcome;
2. at least 30 cost observations spanning the declared two- and five-shard
   profiles;
3. lower p90 post-review time-to-first-test and no more than 5% p90 workflow
   wall-clock regression;
4. at least 60% fewer rounded PR/train runner-minutes for the representative
   workload after charging every producer and consumer cost;
5. zero unsafe acceptance in fork, moved-head, attempt, artifact ambiguity,
   policy, tree, toolchain, expiry, archive, and fallback fixtures; and
6. an independently reviewed enforcement change that keeps exact-head review,
   required contexts, generated-data checks, image evidence, and exact
   merge-tree validation unchanged.

A tie or incomplete sample is a rejection. Evidence from different policy
digests is not combined. A repeated exact head counts once, using the highest
valid run/attempt/artifact identity; a later observation may invalidate an
earlier count but cannot manufacture another sample.

## Prior hosted evidence

The historical opportunistic producer remains useful negative evidence, not an
enforcement authorization:

- [Producer 31798013710](https://github.com/honua-io/honua-server/actions/runs/31798013710)
  emitted one 173,196,743-byte `Honua.Server.Tests` payload.
- [Two-shard A/B 31798575655](https://github.com/honua-io/honua-server/actions/runs/31798575655)
  preserved parity and changed 14 to 12 rounded runner-minutes; p90
  time-to-first-test changed from 383,382 ms to 108,622 ms.
- [Five-project A/B 31799159098](https://github.com/honua-io/honua-server/actions/runs/31799159098)
  preserved parity and changed 32 to 30 rounded runner-minutes; p90
  time-to-first-test changed from 387,773 ms to 344,429 ms.

Those 14.3% and 6.3% savings failed the 60% gate. The standalone observer may
remain an availability measurement, but its output cannot be counted as current
PR Gate reuse evidence.

## Shadow rollout procedure

1. Merge the reviewed report-only implementation while leaving
   `HONUA_PR_GATE_BUILD_REUSE_SHADOW` unset.
2. Confirm the hosted PR itself follows the ordinary independent path and that
   required PR Gate, Review Gate, CodeQL, generated-data, and train checks pass.
3. Set the variable to `true` and select same-repository PRs whose trusted plan
   contains one or two repeated registered projects.
4. Retain the source PR Gate payload/metadata, trusted observer receipt, exact
   one-member train run, Smart CI shadow result/TRX, and authoritative shard
   outcomes for each observation.
5. Audit misses as data. Never rerun until acceptance or wait for a producer;
   fix the contract or keep the independent path.
6. Publish run IDs and complete cost intervals on #3226 and ADR-0074.
7. Propose enforcement only after every promotion gate passes. Otherwise unset
   the variable and record the no-go decision.

Do not combine partial workflow attempts. A changed head, base, tree, policy,
runner image, toolchain, or expired receipt starts a new observation.
