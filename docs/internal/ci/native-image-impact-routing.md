# Native-image impact routing

Status: observation only. Path-based narrowing has now been measured at zero
savings and is not promoted; exact-input reuse is the measured candidate
mechanism. Tracking: #3204. Architecture: ADR-0074.

The legacy pull-request workflows use broad handwritten path filters. A normal
server edit starts three serial native-AOT builds, while several solution,
sample, fixture, and test-only edits start image work even when they cannot
change a published root filesystem. The worker workflow has the inverse risk:
its handwritten list can miss a vulnerability-policy input.

`scripts/ci/native-image-impact.py` derives the managed input sets from the
transitive `ProjectReference` closures rooted at `Honua.Server.csproj` and
`Honua.Worker.Gdal.csproj`. Conditional references are intentionally included.
Explicit policy covers build-wide inputs, embedded files, Docker/rootfs inputs,
verification policy, and these independently measured risk classes:

- server AOT compilation;
- generic, Lambda, and Azure Functions final root filesystems;
- worker managed graph and native root filesystem;
- worker vulnerability policy.

Every completed canonical PR Gate triggers a trusted default-branch observer.
It checks out the exact PR tree only as inert data, executes selector code from
the observer policy SHA, and emits a
`honua.ci.native-image-impact-observation/v3` receipt under the attempt-bound
artifact name `native-image-impact-observation-v3-attempt-<n>`, comparing the candidate
decision with the current workflow triggers. The resolver obtains the immutable
gate-time PR base/head association from the exact GitHub Actions job's
GitHub-managed check run; it never reconstructs the base from the mutable
current PR. Fork heads are explicitly excluded from the observation denominator
and retain the full authoritative image workflows. Missing or ambiguous
same-repository associations fail closed and do not count. The artifact binds
the canonical gate run, trusted policy commit, individual Git blob IDs for the
selector, routing policy, resolver, observer workflow, PR Gate, Serving Image
Boundary, and GDAL Worker Image workflows, a digest over that fixed input
manifest, base/head SHAs, exact project lists, graph fingerprints, normalized
changed-path digest, matched reasons, and `mutation: none`. It also binds one
content digest per image class (`serving_generic`, `serving_lambda`,
`serving_functions`, `worker`) taken over that class's exact build-input file
set at the observed head, so two heads that would consume byte-identical inputs
are recognisable without rebuilding either of them. Neither expensive
image workflow reads it. The authoritative workflow blobs come from the exact
observed PR head and must equal the current default-branch blobs before the
ledger counts them. Validation parses the serving workflow's actual Bash case
arms and rejects any common/variant mapping that differs from the replay policy.

## Promotion and rollback

### Measured outcome: path routing narrows nothing here

The observation ran and was audited. Across 74 distinct trusted heads
(2026-08-13 to 2026-08-17) the candidate selector reproduced the legacy decision
exactly on every head: 40 serving-impacted and 35 worker-impacted under both
policies, **0 narrowed, 0 avoided, and 0 candidate-only**. The trusted ledger's
own countable subset agrees (17 countable heads, `serving_narrowed_heads: 0`,
`worker_avoided_heads: 0`).

That is a structural result, not a small sample. `{variant}_final_rootfs`
subsumes `server_aot_compile`, so any managed change selects all three serving
variants exactly as `src/**` does, and the legacy workflow already routes
variant-only Dockerfile edits per variant. Avoidance is only possible for the
6 of 33 `src/**` projects outside the serving closure (`Honua.AppHost`,
`Honua.ControlPlane.Lambda`, `Honua.Geoprocessing.Cli`,
`Honua.Geoprocessing.Testing`, `Honua.LicenseMint`, `Honua.Worker.Gdal`), and no
observed head touched only those.

Enforcing this router today would therefore change nothing except the risk
surface. It is not promoted.

### Where the cost actually is

Superseded runs are already cheap: `cancel-in-progress` stops them at a median
of 0.7 minutes. The spend is completed builds - about 2670 serving minutes and
405 worker minutes in a 3.4-day window (median 140 min and 18 min per
successful run). Grouping impacted heads by their selected input set shows
**60% of serving-impacted heads and 71% of worker-impacted heads repeat an
input set already built on the same pull request**; one review-heavy pull
request produced 21 serving builds across 3 distinct input sets.

Exact-input reuse, not path narrowing, is the mechanism that can recover those
minutes. The v3 receipt now carries the per-image content digests required to
measure it, and the ledger counts a head as reuse-eligible only when a strictly
earlier observation produced a **successful authoritative image run for
byte-identical inputs**.

### Promotion gates

Routing authority stays with the existing workflows until at least 20 distinct
PR observations satisfy the machine-readable cohort policy in
`.github/impact-routing-promotion.json`. The report-only Impact Routing Evidence
Ledger selects only the producer's current attempt and rejects missing or
ambiguous artifacts, policy-input drift, duplicate head identities, and missing
receipts. It queries the authoritative Serving Image Boundary and GDAL Worker
Image histories and counts an impacted head only when every required image run
for the receipt's exact PR, base SHA, and head SHA completed successfully.
Successful observer shells are never evidence by themselves.

The ledger still requires positive impacted cohorts and a demonstrated savings
cohort; twenty unrelated negative observations cannot authorize promotion. The
savings gate is now satisfied by **either** mechanism:

- `serving_savings_sample_ready`: `serving_narrowed_heads >=
  minimum_serving_narrowed_heads` **or** `serving_reuse_eligible_heads >=
  minimum_serving_reuse_heads`;
- `worker_savings_sample_ready`: `worker_avoided_heads >=
  minimum_worker_avoided_heads` **or** `worker_reuse_eligible_heads >=
  minimum_worker_reuse_heads`.

A serving head is narrowed only when the candidate selects fewer variants than
the receipt's replayed legacy per-variant decision, so a Lambda-only change that
selects Lambda in both policies does not count as savings.

Before the narrowing mechanism is enforced, failure injection must still prove
that project-graph changes, conditional providers, external embedded resources,
Dockerfiles, `.dockerignore`, and `.trivyignore` all fail safe. Before the reuse
mechanism is enforced, the reuse key must be shown to cover every input the
image build reads; the digest is derived from the same closure and pattern
predicates as the routing decision (`image_input_selection`), so an input can
only leave the digest by also leaving the decision.

Changing the selector changes `policy_blob_sha`, which invalidates every
outstanding receipt by design. The v3 contract deliberately resets the observed
sample: the v2 sample could never satisfy a narrowing-only savings gate, so it
carried no promotion value.

### Enforcement proposal (unchanged in shape, redirected in mechanism)

The enforcement proposal is separate. It will keep the lightweight router on
every PR, split serving variants into independent matrix jobs, and retain the
same boundary verification, worker smoke, Trivy enforcement, SARIF, release,
and deployment guarantees. A missing or invalid decision selects all variants,
and a missing or invalid reuse attestation builds. Rollback restores
all-variant execution without changing any security policy. The existing `ci/full` label (already
"run everything" in `ci.yml`; `full-ci` is accepted as an alias) is the escape
hatch: a labelled pull request builds every image variant and never reuses an
attestation. `workflow_dispatch` already forces all variants and stays that
way, as do the tag/release publication lanes
(`deploy-platform-images.yml`, `nightly-container-build.yml`), which this work
does not touch.

After enforcement, compare 30 runs with the ADR-0074 baseline. Promotion still
requires the program-level latency and billed-minute thresholds; otherwise the
router returns to observation mode.
