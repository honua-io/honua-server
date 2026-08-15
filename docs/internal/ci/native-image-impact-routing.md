# Native-image impact routing

Status: observation only. Tracking: #3204. Architecture: ADR-0074.

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
`honua.ci.native-image-impact-observation/v2` receipt under the attempt-bound
artifact name `native-image-impact-observation-v2-attempt-<n>`, comparing the candidate
decision with the current workflow triggers. The resolver obtains the immutable
gate-time PR base/head association from the exact GitHub Actions job's
GitHub-managed check run; it never reconstructs the base from the mutable
current PR. Fork heads are explicitly excluded from the observation denominator
and retain the full authoritative image workflows. Missing or ambiguous
same-repository associations fail closed and do not count. The artifact binds
the canonical gate run, trusted policy commit, individual Git blob IDs for the
selector, routing policy, resolver, observer workflow, and authoritative legacy
Serving Image Boundary workflow, a digest over that fixed input manifest,
base/head SHAs, exact project lists, graph fingerprints, normalized changed-path
digest, matched reasons, and `mutation: none`. Neither expensive image workflow
reads it.

## Promotion and rollback

Routing authority stays with the existing workflows until at least 20 distinct
PR observations satisfy the machine-readable cohort policy in
`.github/impact-routing-promotion.json`. The report-only Impact Routing Evidence
Ledger selects only the producer's current attempt and rejects missing or
ambiguous artifacts, policy-input drift, duplicate
head identities, and missing receipts. It queries the authoritative Serving
Image Boundary and GDAL Worker Image histories and counts an impacted or narrowed
head only when every required image run for the receipt's exact PR, base SHA,
and head SHA completed successfully.
Successful observer shells are never evidence by themselves.

The ledger also requires positive impacted cohorts and actual narrowed/avoided
cohorts; twenty unrelated negative observations cannot authorize promotion. A
serving head is narrowed only when the candidate selects fewer variants than
the receipt's replayed legacy per-variant decision. A Lambda-only change that
selects Lambda in both policies therefore does not count as savings.
Before enforcement, failure injection must prove
that project-graph changes, conditional providers, external embedded resources,
Dockerfiles, `.dockerignore`, and `.trivyignore` all fail safe.

The enforcement proposal is separate. It will keep the lightweight router on
every PR, split serving variants into independent matrix jobs, and retain the
same boundary verification, worker smoke, Trivy enforcement, SARIF, release,
and deployment guarantees. A missing/invalid decision selects all variants.
Rollback restores all-variant execution without changing any security policy.

After enforcement, compare 30 runs with the ADR-0074 baseline. Promotion still
requires the program-level latency and billed-minute thresholds; otherwise the
router returns to observation mode.
