# Impact-routing evidence ledger

Status: report-only. Tracking: #3204 and umbrella #3213. The docs-only PR
Gate experiment (#3235) was closed as not planned on 2026-08-17 UTC; its
cohort thresholds below are retained only as the standard the native-image
stream is still measured against. See the audit at the end of this document.

Workflow-run counts are not promotion evidence. A trusted observer can complete
successfully while producing no relevant candidate, while using a superseded
policy, or without retaining a uniquely discoverable receipt. The Impact Routing
Evidence Ledger makes the denominator explicit for the docs-only PR Gate and
native-image routing experiments.

## Attempt-bound receipt boundary

The trusted default-branch observers publish one attempt-qualified artifact per
run:

- `pr-gate-impact-docs-only-v3-attempt-<n>` for the countable candidate cohort
  and `pr-gate-impact-full-v3-attempt-<n>` for noncandidate diagnostics;
- `native-image-impact-observation-v2-attempt-<n>`.

The ledger first discovers a bounded set of trusted producer runs, then reads
each run's artifact catalog and selects only the name whose attempt equals the
run's current `run_attempt`. Retained artifacts from earlier rerun attempts can
therefore neither poison nor satisfy the cohort. The catalog count shares the
same hard budget as receipt downloads, keeping the workflow below the
repository token limit even though the repository has tens of thousands of
artifacts. The ledger rejects missing, duplicate, expired, oversized,
unsafe-archive, wrong-workflow, wrong-policy, and cross-head evidence. It
deduplicates successful observations by full head SHA and never counts a head
associated with more than one pull request.

Both streams bind the Git blobs of the authoritative PR Gate workflow and
`scripts/ci/trusted-pr-workflow-run.js`, which resolved the canonical PR, base,
head, workflow, run, and attempt. A gate or resolver change therefore starts a
new policy cohort instead of mixing identities established under superseded
logic. The receipt records the workflow blobs from the observed PR head, while
the ledger accepts them only when they equal the current default-branch policy.
Native receipts additionally bind both authoritative image workflows
and record the Serving Image Boundary workflow's replayed per-variant decision.
Serving narrowing is the strict difference between that legacy variant count
and the candidate variant count; reproducing an existing
Lambda-only, Functions-only, or generic-only selection does not count. An image
outcome is authoritative only when its GitHub-managed workflow association
matches the receipt's PR number, base SHA, and head SHA; an earlier run for a
reopened same-head PR cannot satisfy a later-base observation.

Observer receipts use a seven-day rolling retention window. At current activity
that keeps per-run catalog discovery and downloads below the repository token's
bounded request budget while requiring the positive/narrowed cohorts to reflect
the current workload rather than stale historical examples.

The workflow runs only from the default branch with read-only Actions and
contents permissions. It is `report-only`, has no status or routing authority,
and cannot dispatch, cancel, label, normalize, merge, or publish an image.

## Promotion cohorts

The policy is `.github/impact-routing-promotion.json`. Promotion remains a
separate reviewed change. The current ledger requires all of the following
before it can emit `eligible-for-human-promotion-review`:

- at least 20 distinct docs-only candidate heads whose authoritative full
  `PR Gate` succeeded, with zero docs-only full-gate failures;
- at least 20 distinct native-image heads;
- positive serving and worker impact cohorts;
- serving narrowing and worker avoidance cohorts that demonstrate actual work
  removal rather than a selector that merely reproduces the legacy filters;
- successful exact-head Serving Image Boundary or GDAL Worker Image outcomes
  whenever either the legacy or candidate decision says that image evidence is
  required; and
- zero receipt-integrity failures.

The initial pre-ledger audit on 2026-08-15 demonstrates why these cohorts matter:
35 trusted PR Gate receipts represented 27 heads, but all 35 selected the full
gate and zero were docs-only candidates. Thirty-two trusted native receipts
represented 25 heads, but only one head impacted either image workflow and no
head demonstrated narrower routing. Neither experiment was promotion-ready.

## Rollback and incident handling

The observers and ledger can be disabled independently without changing the
authoritative PR Gate or image workflows. If receipt integrity fails, keep the
legacy routes authoritative, preserve the ledger artifact, and fix or version
the producer contract. Never waive a missing image outcome or combine receipts
from different policy cohorts to reach a threshold.

## Docs-only promotion audit, 2026-08-16/17 UTC (#3235)

All dates in this section are UTC.

**Outcome: #3235 was closed as not planned on 2026-08-17.** The docs-only route
is not implemented and is not being pursued. What survives is the classifier's
soundness: `scripts/ci/classify-pr-gate-impact.py` still runs in the trusted
observer, still reports `authoritative_gate: full`, and must not describe a
class of change as harmless when it is not. This section records the numbers
that produced the decision and the defect that the numbers alone would never
have shown.

### The shadow cohort was an order of magnitude short

Receipts harvested from the 100 most recent `PR Gate Impact Observation` runs
(2026-08-13 through 2026-08-17 UTC): 82 runs completed successfully and 80
retained a current-contract receipt.

| Classification | Receipts | Distinct heads |
|---|---:|---:|
| `full` / `path-requires-full-gate` | 78 | 70 |
| `docs-only` / `internal-markdown-only` | 2 | 2 |

Both docs-only receipts belong to pull request #3245 (heads `72c94f13` and
`3b923c3f`), which changed `docs/internal/contributor/adr/0075-...md` and
`docs/internal/contributor/adr/README.md`. Each head's authoritative `PR Gate`
succeeded, so no missed failure is attributable to the classifier's own
decisions. The trusted ledger
([run 31951506155](https://github.com/honua-io/honua-server/actions/runs/31951506155))
counted `1` of the required `20` docs-only heads inside its seven-day
head-deduplicated window, with `0` docs-only gate failures and `5`
receipt-integrity failures (four native, one superseded PR Gate policy).

The observed candidate rate was 2 of 80 receipts. At roughly twenty PR Gate runs
per day the cohort needed about six more weeks, for a route that would have
avoided about 2.5% of gate runs.

### The docs-only class was unsound as written

The audit also asked the inverse question: for a head the classifier calls
docs-only, could the skipped gate have failed? For seven documents the answer is
yes, because a gate step asserts their *content*:

| Document | Asserted by | Gate step |
|---|---|---|
| `docs/internal/operator/audit-coverage-matrix.md` | `AuditCoverageMatrixDriftTests` | Architecture tests |
| `docs/internal/contributor/adr/0047-module-dependency-policy.md` | `ModuleDependencyPolicyTests.MatrixAndAdr_ShouldCrossReference_EachOther` | Architecture tests |
| `docs/internal/contributor/release-bundle.md` | `ServingImageBoundaryTests` | Architecture tests |
| `docs/internal/contributor/public-interface-quality-model.md` | `PublicInterfaceProofLedgerTests` | Architecture tests |
| `docs/internal/spikes/geocode-server-matrix.md` | `DocumentationMatrixDriftTests.GeocodeServerMatrix_RoutesAndImplementedParametersMatchCode` | Architecture tests |
| `docs/internal/ci/merge-train-early-failure-observe.md` | `validate-early-failure-observe.sh` | Merge-train timeout policy |
| `docs/internal/security/code-scanning-2026-Q2-remediation.md` | `base-image-mirrors.sh --verify-inventory-doc` | Base-image security inventory |

None of the observed docs-only heads touched one of these, so the shadow sample
could never have exposed the gap; only reading the gate's own inputs could.
`scripts/ci/classify-pr-gate-impact.py` routes all seven to the full gate with
reason `lean-gate-governed-doc`.

Two documents are deliberately *not* on that list.
`docs/internal/ci/gate-model.md` and `docs/internal/ci/workflow-inventory.md`
are parsed by `validate-review-first-dispatch.py`, which runs in the always-on
`Verify review-first admission contract` step, before any routing decision.
`scripts/ci/fixtures/validate-pr-gate-always-on-steps.py` asserts structurally
that this step and `Verify .NET base-image security inventory` never acquire an
`if` or `continue-on-error`, that the `pr-gate` job itself is never conditional,
and that each step still runs its script; it proves each of those rejections
fires before reporting success.

One further constraint is worth stating plainly rather than leaving in prose:
`scripts/ci/check-markdown-command-policy.ps1` content-asserts **every** `*.md`
file in the repository, including every file the docs-only class would claim. It
is a step of the `lean-gate` composite action. Any future docs-only route must
therefore either re-run that step on the docs-only path or split it out of the
composite; it cannot be skipped along with the .NET work.

### Keeping the exclusion list honest

`scripts/ci/classify-pr-gate-impact.test.py` rescans the gate's own inputs and
fails when a `docs/internal/**.md` reference is neither governed nor listed as
reference-only, naming the referencing file. It reads both spellings used here:
a whole-path literal, and a path assembled segment-by-segment through
`ArchitectureTestHelpers.CombinePath`, `Path.Combine`, or `Path.Join` -- the
prevailing style in the architecture tests, and how two of the seven governed
documents are referenced. A path whose segments are not adjacent string literals
is out of reach of any scan; the governed list, not the scan, is the contract.
An undecodable gate input fails the guard rather than being skipped, because the
unreadable file could be the one introducing a new assertion. The reverse
direction -- a governed document no longer referenced by any gate input -- is
advisory only: it is over-conservative rather than unsound, and failing on it
would push maintainers to prune correct entries.

The reference-only allowlist and the scanned globs live in the test file, not in
the classifier, so curating them never changes the classifier blob that
observation receipts bind.

Placement: this guard runs in batch CI through
`scripts/ci/validate-ci-router.sh` (the `CI Router Validation` job) and not in
`PR Gate`, so a new content assertion is caught on the merge train rather than
on the pull request that introduces it. The whole of
`scripts/ci/validate-pr-gate-impact.sh` measures about 1.9 s locally, of which
the classifier test scanning 940 gate inputs is about 0.35 s, so moving it into
the `lean-gate` composite would be affordable. It is deliberately not moved
here: with #3235 closed, the classifier has no routing authority, a
one-merge-train delay on a list that only matters to a report-only observer is
acceptable, and the required gate should not grow steps for a shelved
experiment.
