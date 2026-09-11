# Impact-routing evidence ledger

Status: report-only. Contracts: promotion policy `/v4`, ledger `/v4`.
Tracking: #3204 and umbrella #3213. The docs-only PR Gate experiment (#3235)
was closed as not planned on 2026-08-17 UTC; its cohort thresholds below are
retained only as the standard the native-image stream is still measured
against. See the audit at the end of this document.

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
- `native-image-impact-observation-v3-attempt-<n>`.

The ledger first discovers a bounded set of trusted producer runs, then reads
each run's artifact catalog and selects only the name whose attempt equals the
run's current `run_attempt`. Retained artifacts from earlier rerun attempts can
therefore neither poison nor satisfy the cohort. Catalog listings and receipt downloads have separate hard budgets, even
though the repository has tens of thousands of artifacts. The ledger rejects missing, duplicate, expired, oversized,
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

- `maximum_runs_per_query` (1,000) bounds each time slice, matching
  [GitHub's filtered workflow-run listing ceiling](https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow).
  The collector binds one closed seven-day window (plus the existing producer
  and image lookbacks), recursively partitions it into disjoint inclusive
  creation-time ranges, and checks each range's page totals and unique IDs.
  It retries an inconsistent slice from page one. Status is not a query filter:
  a run completing during pagination cannot shift page membership. Incomplete
  observers remain visible exclusions and owe no receipt yet.
- `maximum_producer_run_catalogs` (3,000) bounds the successful observers that
  need an artifact catalog. Failed and incomplete observers remain in the run
  catalogs.
- `maximum_receipt_downloads` (2,500) bounds the indexed receipts per audit.

The collector fails on a truncated catalog, persistent pagination race, or a
single second exceeding the query ceiling. These are collection errors, not
fabricated receipt-integrity findings. Per-query slicing replaces the old
800-run page budget without shortening retention or changing the 5% loss gate.

### GITHUB_TOKEN request budget

The audit job uses only `github.token`. GitHub meters that token at 1,000
requests per repository per hour, and other workflows draw on the same pool.
`github_token_request_limit` (1,000) less `github_token_request_reserve` (200)
is the job's allowance. The allowance shrinks further when the unmetered
`/rate_limit` endpoint reports less remaining this hour. Every run-catalog
page, artifact listing, download, retry, and expiry lookup is charged to
`evidence/request-budget.json` before it is sent. The trend step reserves its
41 history requests up front, or reports the current run alone. The
per-phase spend is printed in the step summary and retained with the ledger.

At the 2026-09-03..09 volume (1,307 runs per observer, 876 + 866 successful,
801 + 775 receipts), the previous shape made one catalog request per
successful observer and one download per receipt. With run catalogs and trend
history, that was about 3,390 requests, more than four hours of the token
pool. The current shape stays inside the allowance:

| Phase | Requests |
|---|---:|
| Run catalogs (two sliced observer windows, serving, worker) | ~33 |
| Name-filtered receipt listings, 100 per page, newest first | ~21 |
| Per-run catalogs for runs that list no current-attempt receipt (skips, losses) | ~166 |
| Receipt downloads not already in the cache (one day of new receipts) | ~225 |
| Trend history (reserved) | 41 |
| **Steady-state total** | **~486 of 800** |

Receipt archives are immutable per artifact ID. The job restores them from a
default-branch-scoped Actions cache. Pull request runs can neither restore nor
write that cache. A cached archive is reused only while its SHA-256 matches
the artifact catalog's `digest`. Archives outside the current index are
pruned, so the cache tracks the retention window. When the cache is cold,
receipt transfers run until the allowance is spent. The step then fails with
the spend. The archives it verified are saved anyway, so the next audit
resumes from them. A fully cold seven-day window completes on the third audit.
Dispatching those three runs an hour apart recovers the ledger the same day.
`scripts/ci/collect-impact-routing-runs.test.py` replays a slightly hotter
volume (1,804 successful observers, 1,610 receipts) through the real
collector, discovery, and downloader. It measures 3,455 requests for the old
shape and 33 + 216 + 230 + 41 = 520 for a warm audit. A cold cache takes
800, 800, then 763 requests across three audits. The replay also asserts that
synthesized and per-run catalogs produce identical indexes.

### Receipt store identity and concurrency

The receipt store is Actions artifacts, not an issue body. Each immutable upload
belongs to a producer run and has an attempt-bound name. Discovery keys it by
stream, run ID and current attempt, rejects duplicate artifacts, and summary
counts each validated head once. Concurrent producers append independent
artifacts; they never replace a shared receipt list. Repeated audits are
idempotent. There is no issue-body CAS operation to add here; the separate
merge-train state-issue gap is tracked in #3519 and is outside this ledger.

A native observation discarded at the final identity recheck now uploads the
same skip marker as a collect-time skip. It preserves the reason for discarding
a moved PR without pretending that the discarded bytes are a retained receipt.
The seven-day replay covers independent producers, repeated reads, full-mode
PR Gate artifacts, successful native outcomes with moved live PR pointers,
empty diffs, and post-observation skips. Empty diffs are valid only when both
recorded policies select no image work; contradictory routing still fails.

Both streams bind the authoritative PR Gate workflow and trusted resolver blobs.
Native receipts also bind image workflows and content digests over the exact
merge-tree build inputs. Changed policy generations remain visible cohort
exclusions, and contradictions against a receipt's own policy head remain
integrity failures. Authoritative image matching uses the immutable run head
and workflow identity; mutable PR tip pointers cannot invalidate historical
runs. The detailed failure classification and supersession rules below apply.

Observer receipts use a seven-day rolling retention window. The positive and
narrowed cohorts therefore reflect the current workload rather than stale
historical examples. The request budget above, not the window, bounds API cost.

The live audit runs only from the default branch with read-only Actions and
contents permissions. Pull requests touching this ledger run its seven-day
fixture replay in a separate job; fixture results never seed the live streak. It is `report-only`, has no status or routing authority,
and cannot dispatch, cancel, label, normalize, merge, or publish an image.

## Promotion cohorts

The policy is `.github/impact-routing-promotion.json`. Promotion remains a
separate reviewed change. The current ledger requires all of the following
before it can emit `eligible-for-human-promotion-review`:

- at least 20 distinct docs-only candidate heads whose authoritative full
  `PR Gate` succeeded, with zero docs-only full-gate failures;
- at least 20 distinct native-image heads;
- positive serving and worker impact cohorts;
- a serving savings cohort and a worker savings cohort that demonstrate actual
  work removal rather than a selector that merely reproduces the legacy filters.
  Each is satisfied by narrowing/avoidance **or** by exact-input build reuse. A
  head counts as reuse-eligible only when an attestation for byte-identical
  inputs already existed when that head's own image work started, only for the
  variants the authoritative workflow actually built, and only under a
  `(image class, digest)` key so one variant's build cannot satisfy another's.
  `signals` and `savings_mechanism` record which mechanism the sample proved,
  because a reuse-only sample does not authorize promoting the path router;
- successful exact-head Serving Image Boundary or GDAL Worker Image outcomes
  whenever either the legacy or candidate decision says that image evidence is
  required; and
- zero receipt-integrity failures.

The 2026-08-16 audit demonstrates why these cohorts matter. Across 74 distinct
trusted native heads the candidate reproduced the legacy decision on every head
(40 serving-impacted, 35 worker-impacted, 0 narrowed, 0 avoided, 0
candidate-only), while 60% of serving-impacted heads and 71% of worker-impacted
heads repeated an input set already built on the same pull request. A
narrowing-only savings gate can never pass here; the reuse cohort is what the
observation must now substantiate.

The initial pre-ledger audit on 2026-08-15 demonstrates the same point:
35 trusted PR Gate receipts represented 27 heads, but all 35 selected the full
gate and zero were docs-only candidates. Thirty-two trusted native receipts
represented 25 heads, but only one head impacted either image workflow and no
head demonstrated narrower routing. Neither experiment was promotion-ready.

Reuse is **build** reuse. The GDAL Worker image's Trivy scan is enforcing and
its verdict depends on the vulnerability database at scan time, so identical
inputs never imply an identical verdict; scanning is re-run on every head and is
excluded from every savings estimate.

## What the ledger calls a failure (#3343)

The ledger fails closed on **integrity** and on **measured receipt loss**, and
on nothing else. Three other outcomes used to be reported as integrity failures,
which is how the check became permanently red and therefore ignorable. They are
now separate, counted, named facts:

| Bucket | Meaning | Red? |
|---|---|---|
| `integrity_failures` | The receipt is malformed, violates its trust boundary, is not attributable to its producer, or contradicts its own declared policy head. | Yes |
| `receipt_loss_regression` | A successful observer that OWED a receipt did not leave one, above `maximum_receipt_loss_ratio`. | Yes |
| `policy_generation_superseded_receipts` | Cohort drift: the receipt is intact but pins the previous semantic classifier/routing-policy generation. | No |
| `image_outcome_superseded_heads` | The head's authoritative image run was cancelled by a **witnessed** later push on the same PR. It can never acquire a successful outcome. | No |
| `image_outcome_pending_heads` | The head's image run is still building. Undetermined, not absent — the next audit sees how it ended. | No |
| `quarantined` | Named in `.github/impact-routing-tombstones.json` with an owning issue and an expiry. | No, until the expiry |

Cohort drift is the important distinction. A classifier, native routing-policy,
PR Gate workflow, observer, or trusted-resolver change starts a semantic
generation because each can alter routing or evidence collection. Serving and
worker workflow action-version, timeout, name, and comment edits retain it; their
whole-file SHAs remain receipt provenance. Authoritative serving/worker routing
fragments are parsed and checked against the generation-pinned native policy, so
an actual routing change cannot escape the digest. A semantic reset is by design (see "Changing the selector…"
in `native-image-impact-routing.md`); it is not evidence loss and must never
redden the integrity check. `policy_generation_sha256` names the current
generation.

The one case where a stale policy input IS an integrity failure: the receipt's
own `policy_sha` equals the commit the ledger checked out. The two are then
directly comparable, and a mismatch means the receipt claims blobs its declared
head does not contain.

### Supersession must be witnessed

A `cancelled` conclusion does not say **who** cancelled the run: a later push,
an operator, and an infrastructure abort all look identical. So a head is
excluded as superseded only when a later push on the same pull request is
positively witnessed, by either:

- a later run of the same workflow, at a different head, whose live
  `pull_requests` association names this pull request
  (`superseded_by_run_ids`); or
- another validated receipt for the same pull request with a strictly higher
  `gate_run_id`, which is push-monotone and comes from signed evidence rather
  than a live API field (`superseded_by_later_receipt`).

The second witness is not redundant: GitHub leaves `pull_requests` empty on a
large share of these runs, and on live `trunk` evidence it is the only witness
available for 14 of 36 superseded heads. An all-cancelled head with **no**
witness of either kind stays a failure.

### Receipt loss

`receipt_emission` reports, per stream and overall:

- `receipts_indexed` — a receipt was found and downloaded;
- `receipts_skipped` — the observer recorded a deliberate skip, so owed nothing;
- `receipts_pending_index` — the observer finished inside
  `receipt_index_grace_minutes` and its artifact catalog may not be final. Not
  loss, and removed from the denominator rather than counted as delivered;
- `receipts_missing` — owed and absent. This is loss;
- `receipts_owed`, `loss_ratio`, and `measured`.

`measured: false` (nothing was owed) blocks promotion but is not a regression:
an idle window is not evidence of health, and it is not a reason to go red.

### Tombstones

`.github/impact-routing-tombstones.json` quarantines evidence that no future run
could ever verify — a producer run whose artifacts are gone, a head whose image
work was destroyed rather than superseded. Every entry needs a reason, an owning
issue, and an expiry, and **the auditor fails closed on an expired tombstone**,
so a quarantine cannot become permanent by neglect. A tombstone that matches
nothing is reported as stale so it can be removed. Widening a threshold or
deleting a failing receipt class is never an alternative to an entry here.

### Promotion streak

`audit-impact-routing-evidence.py trend` reads the ledger artifacts this
workflow has already retained and reports `consecutive_green_days` against
`promotion_green_days`, plus the worst `loss_ratio` inside the streak. A day is
green when it had zero integrity failures and measured loss inside budget; a day
with no ledger breaks the streak, because a missing measurement is not a passing
one. No shadow optimisation may be promoted while `promotion_gate_ready` is
false.

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
