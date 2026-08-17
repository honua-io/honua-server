# Impact-routing evidence ledger

Status: report-only. Tracking: #3204, #3235, and umbrella #3213.

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

## Docs-only promotion audit, 2026-08-16 (#3235)

The docs-only PR Gate route is **not promoted**. Two independent problems block
it, and only one of them is a matter of waiting.

### The shadow cohort is an order of magnitude short

Receipts harvested from the 100 most recent `PR Gate Impact Observation` runs
(2026-08-13 through 2026-08-17): 82 runs completed successfully and 80 retained a
current-contract receipt.

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

The observed candidate rate is 2 of 80 receipts. At the observed workload
(roughly twenty PR Gate runs per day) the cohort needs about six more weeks of
observation, and any change to the classifier restarts it because the ledger
binds the classifier blob.

### The docs-only class was unsound as written

The audit also asked the inverse question: for a head the classifier calls
docs-only, could the skipped lean gate have failed? For four documents the
answer was yes, because a lean-gate step asserts their *content*:

| Document | Asserted by | Lean-gate step |
|---|---|---|
| `docs/internal/operator/audit-coverage-matrix.md` | `AuditCoverageMatrixDriftTests` | Architecture tests |
| `docs/internal/contributor/release-bundle.md` | `ServingImageBoundaryTests` | Architecture tests |
| `docs/internal/contributor/public-interface-quality-model.md` | `PublicInterfaceProofLedgerTests` | Architecture tests |
| `docs/internal/ci/merge-train-early-failure-observe.md` | `validate-early-failure-observe.sh` | Merge-train timeout policy |

None of the observed docs-only heads touched one of these, so the shadow sample
could never have exposed the gap; only reading the gate's own inputs could.
`scripts/ci/classify-pr-gate-impact.py` now routes them to the full gate with
reason `lean-gate-governed-doc`, and `classify-pr-gate-impact.test.py` rescans
every lean-gate source so a new content assertion cannot silently widen the
class again: an unclassified `docs/internal/**.md` literal fails the guard with
the referencing file named.

Two further documents, `docs/internal/ci/gate-model.md` and
`docs/internal/ci/workflow-inventory.md`, are read by
`validate-review-first-dispatch.py`. They stay eligible only because that step
runs unconditionally, before any routing decision;
`scripts/ci/validate-pr-gate-impact.sh` now fails if that step ever acquires a
condition.

### What remains before enforcement

1. Twenty distinct docs-only candidate heads under the current classifier blob,
   with zero docs-only full-gate failures and an integrity-clean ledger.
2. Fixture coverage for rename, deletion, fork, moved head, truncated file
   lists, and policy-input changes. Rename/deletion/status, truncation,
   duplicate records, unsafe paths, generated data assets, and the governed-doc
   class are covered by `classify-pr-gate-impact.test.py`; fork and moved-head
   admission remain enforced by the trusted resolver rather than by fixtures.
3. The in-gate route itself: an early classifier step inside the existing
   `PR Gate` job, the heavy steps conditioned on its output, a docs-only path
   that still runs admission, the base-image inventory check, the Markdown
   command policy, and exact-head review revalidation, plus a labelled escape
   hatch and a one-line rollback switch. None of that is implemented; the
   required gate is unchanged and fully authoritative.
