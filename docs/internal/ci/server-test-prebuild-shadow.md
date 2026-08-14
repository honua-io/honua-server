# Opportunistic server-test prebuild shadow

Issue #3226 is the bounded follow-up to the same-run reuse result recorded by
#3222. The same-run producer preserved correctness, but it did not improve the
two required outcomes: p90 test start was slightly slower and rounded runner
minutes increased from 13 to 21. Production server-test shards therefore remain
independent and authoritative.

This experiment changes the timing, not the trust model. A read-only observer
may restore and build at most two projects while an exact PR head is already
waiting for review. A completed trusted `Review Gate Attestation` identity run
wakes the observer; manual backfill accepts that run's exact id, attempt, and
conclusion. Automatic production ships disabled: the initial A/B remains the
first authorization step, and only a successful A/B authorizes setting the repository
variable `HONUA_SERVER_TEST_PREBUILD_SHADOW=true` for the 20-head shadow. A later
manual verifier makes one artifact download attempt
and never polls. A missing, late, expired, duplicate, stale-policy, wrong-runner,
wrong-toolchain, wrong-head, malformed, oversized, path-unsafe, or digest-invalid
artifact is a cache miss: partial `bin/Release` and `obj` output is removed and
the verifier immediately performs the existing local restore/build.

## Security and identity boundary

The observer and benchmark are shadow workflows. They have read-only Actions,
contents, package, and pull-request permissions; they publish no commit status,
are not required checks, and cannot dispatch or join the merge train. The
observer executes from the default-branch `workflow_run` definition, resolves
the source Review Gate through its unique GitHub-managed identity-job check,
and rejects forks, moved heads/bases, and draft/closed PRs before executing the
raw head. Candidate code never executes in `pull_request_target`; the raw head
runs only with the observer's exact read-only permission allowlist. The
benchmark is manual and rejects any producer unless all of these are true:

- the current PR is open, ready, and still points to the receipt's full head
  SHA in the same repository;
- the producer is a completed successful run of the trusted-default
  `.github/workflows/server-test-prebuild-observe.yml` workflow, and its policy
  checkout is pinned to the immutable default-branch `github.sha` that GitHub
  executed rather than a moving branch name;
- the plan binds the exact source Review Gate path, event, run/attempt,
  conclusion, successful identity job, and GitHub-managed check association;
- the trusted observer's plan artifact binds repository, PR, event-time base,
  source head,
  producer run/attempt, and its actual policy SHA; that policy SHA is an
  ancestor of current trusted trunk, and every execution-relevant policy input
  has the same digest at both commits;
- every exact artifact name is unique and unexpired;
- the outer receipt binds repository, PR, source SHA/tree, producer run and
  attempt, runner OS/architecture/image, trusted policy commit/tree and every
  policy input digest;
- the inner receipt binds the project, Release configuration, exact .NET SDK,
  build inputs, manifest, archive size/digest, and a maximum 24-hour lifetime;
  and
- the existing restore boundary rechecks bounded archive size, digest, TTL,
  safe relative paths, unpacked size, project assets, DLLs, and PDBs before a
  no-build test command can run.

The receipt is evidence, not authorization. It cannot satisfy review, security,
generated-artifact, image, branch-protection, or merge-tree requirements. Any
production consumer must preserve this fail-closed validation plus local-build
fallback.

## Cost and latency decision

The manual A/B workflow supports two profiles: two shards sharing Server.Tests,
and a five-project hybrid with only the repeated Server.Tests project reused.
Each baseline and candidate runs the same trusted proof filter and publishes a
stable TRX identity/outcome digest. The summary uses complete GitHub-hosted job
intervals, not partial shell timers.

Promotion to a 20-exact-head shadow requires, for every profile:

1. exact filter, test identity, count, and outcome parity;
2. every repeated-project candidate accepting the exact prebuild rather than
   falling back;
3. the producer completing before candidate verification starts;
4. lower p90 candidate test-command start;
5. fewer rounded runner minutes after charging the candidate for the complete
   observer plan and every producer job, including unused bounded work; and
6. no more than the existing 5% wall-clock regression budget.

A tie is a rejection. One passing A/B only authorizes the 20-head shadow. After
that, production routing remains guarded and reversible until 30 post-enforcement
observations confirm the latency and minute reductions. The current independent
restore/build path remains the rollback authority throughout.

Automatic producer observations are availability evidence, not parity. After a
successful exact-head PR Gate, the read-only
`Server Test Prebuild Parity Observation` workflow may make one lookup for the
already-completed observer artifact. For each bounded repeated project it runs
the registered proof selection once from an independent restore/build and once
from the validated prebuild, then compares stable filter, test identity, count,
and outcome digests. It also records complete hosted producer, baseline, and
candidate intervals. A missing or rejected artifact falls back locally and is
reported as non-countable; the workflow never polls, publishes a status,
dispatches another workflow, or changes the authoritative PR Gate result.

The 20-run promotion input counts only distinct representative exact heads whose
parity artifact says `countable: true`. Producer-only, skipped, fallback,
incomplete, duplicate, or contradictory observations do not advance the count.

## Hosted procedure

After these workflows reach the default branch, leave
`HONUA_SERVER_TEST_PREBUILD_SHADOW` unset during the initial A/B:

1. choose an open same-repository PR whose targeted descriptor selects at least
   two shards for a registered project;
2. manually dispatch `Server Test Prebuild Observation` with the exact completed
   `Review Gate Attestation` run id, attempt, and conclusion for that head, then
   use the completed observer run ID;
3. dispatch `Server Test Prebuild Benchmark` once for `two-same-project` and
   once for `five-hybrid-project` before the receipt expires or policy moves;
4. retain the plan, raw transfer/consumer/producer metrics, complete hosted job
   intervals, TRX summaries, and decision report; and
5. publish both run IDs and the go/no-go result on #3226 and ADR-0074; and
6. set the enable variable only if both profiles are eligible, then collect the
   predeclared 20 exact-head shadow observations.

Do not rerun only a subset and combine attempts. A changed head, policy, runner
image, toolchain, or expired receipt requires a new observer run and a new whole
A/B observation.
