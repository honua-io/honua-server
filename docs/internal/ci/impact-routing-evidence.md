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

Observers wake on every completed source run, including the cancelled and
superseded ones an ordinary force-push produces, whose head no longer matches
the pull request. `resolveTrustedPullRequestWorkflowRun` stays fail-closed for
those. Observation-only consumers go through `resolveForObservation`, which
passes `unresolved: 'skip'` and downgrades **only** the superseded-source class
(`UnresolvedTrustedWorkflowRunError`: cancelled source with no terminal job, no
check-run association, a run head outside the association, a moved or closed
pull request) to a recorded skip. Misconfiguration and API drift - wrong
workflow path/name/event, a fork head, a dispatch-input `run_attempt` or
conclusion mismatch, an ambiguous job or association, an inconsistent check-run
shape, a cross-repository identity - still throw under every behavior, so a
broken observer stays loud instead of masquerading as a superseded push. The
trusted default (`'throw'`) is unchanged and remains mandatory for anything that
attests, promotes, or gates. The two impact observe jobs additionally decline to
schedule at all when the source run's conclusion is `cancelled`.

A skip is recorded once, not inferred: `core.notice`, the `skip`/`skip_code`/
`skip_reason` step outputs, and a marker artifact named
`<observer>-skipped-<code>-attempt-<n>` holding an
`honua.ci.observation-skipped/v1` document. The ledger recognises that name from
the artifact catalog alone - it is never downloaded - and classifies the run as
`observation-skipped:<code>`. Without the marker every skip would land in
`observation-receipt-not-emitted`, which is the only signal for a real
receipt-emission regression. Two different skip codes on one attempt is producer
ambiguity and is an integrity failure. The ledger summary prints the skip total
with its per-code breakdown alongside the count of successful observers that
emitted neither a receipt nor a marker.

Collection budgets are hard API-cost bounds and stay fail-closed: a truncated
catalog would be counted as missing evidence and reported as a fabricated
integrity or authoritative-outcome failure rather than as the collection gap it
is. Exhausting one prints an error naming the collection, the count consumed,
the declared total, and the policy key to change. The three bounds are distinct
resources and are sized independently:

- `maximum_pages_per_query` (8, i.e. 800 runs per paged query) bounds each run
  catalog. On 2026-08-17 the widest collection, `serving-image-boundary.yml`
  `pull_request` runs, was 430 over a trailing 7-day window (worker-gdal-image
  388, native observer 188, PR Gate observer 151), and the image window is the
  receipt window plus `image_outcome_lookback_hours`.
- `maximum_producer_run_catalogs` (900) bounds the per-run artifact listings.
  Only **successful** observer runs are catalogued, because only they can have
  uploaded a receipt or a skip marker; non-success runs stay in the run catalog
  so `discover` keeps reporting them as `observer-run-<conclusion>`. This bound
  used to be `maximum_receipt_downloads`, which made a 500-request download cap
  the binding limit on how many observer runs the window could contain. At the
  2026-08-17 rate of roughly 50 completed runs per day per observer (~100 a day
  combined, 339 completed and 296 successful over the trailing 7 days) a full
  7-day window needs about 700 catalog listings, so the old cap would have
  tripped around 2026-08-19.
- `maximum_receipt_downloads` (420, validator-bounded at 500) bounds actual
  archive transfers, which is the expensive resource it was always meant to
  describe.

Both streams bind the Git blobs of the authoritative PR Gate workflow and
`scripts/ci/trusted-pr-workflow-run.js`, which resolved the canonical PR, base,
head, workflow, run, and attempt. A gate or resolver change therefore starts a
new policy cohort instead of mixing identities established under superseded
logic. Receipts collected under the previous blobs are then reported as
`policy inputs are not current` INTEGRITY FAILURES, not as exclusions, so the
ledger stays red for the whole retention window unless `observation_started_at`
is advanced in the same change. Any pull request that edits
`scripts/ci/trusted-pr-workflow-run.js` or either observer workflow must
therefore bump `observation_started_at` to its own merge time; the
2026-08-17 entry restarts the cohort for exactly that reason (the concrete
failures are `PR Gate receipt policy inputs are not current` and
`native-image receipt policy inputs are not current`). The receipt records the
workflow blobs from the observed PR head, while the ledger accepts them only
when they equal the current default-branch policy.
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
