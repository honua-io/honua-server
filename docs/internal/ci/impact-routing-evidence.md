# Impact-routing evidence ledger

Status: report-only. Tracking: #3204, #3235, and umbrella #3213.

Workflow-run counts are not promotion evidence. A trusted observer can complete
successfully while producing no relevant candidate, while using a superseded
policy, or without retaining a uniquely discoverable receipt. The Impact Routing
Evidence Ledger makes the denominator explicit for the docs-only PR Gate and
native-image routing experiments.

## Stable receipt boundary

The trusted default-branch observers publish one constant artifact name per run:

- `pr-gate-impact-docs-only-v3` for the countable candidate cohort and
  `pr-gate-impact-full-v3` for noncandidate diagnostics;
- `native-image-impact-observation-v2`.

Identity belongs inside the validated receipt, not in an artifact-name prefix.
This allows GitHub's exact artifact-name filter to bound discovery even though
the repository has tens of thousands of retained artifacts. The ledger rejects
missing, duplicate, expired, oversized, unsafe-archive, wrong-workflow,
wrong-policy, and cross-head evidence. It deduplicates retries by full head SHA
and never counts a head associated with more than one pull request.

Observer receipts use a seven-day rolling retention window. At current activity
that keeps exact-name discovery and downloads below the repository token's
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
