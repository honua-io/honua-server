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
`serving_functions`, `worker`) over that class's exact build-input file set, so
two heads that would consume byte-identical inputs are recognisable without
rebuilding either of them. Neither expensive
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

Exact-input reuse is the mechanism that can recover those minutes *while keeping
the evidence on the pull request*. It remains the answer for the GDAL worker
lane, whose Trivy verdict depends on the vulnerability database at scan time and
is never reusable across days. The v3 receipt carries the per-image content
digests required to measure it, and the ledger counts a head as reuse-eligible
only when an attestation for byte-identical inputs already existed when that
head's own image work started.

### The serving trigger was narrowed instead (2026-08-25, #3204)

The serving lane took the other exit. Rather than reuse per-push evidence,
`serving-image-boundary.yml` stopped asking for it: its `pull_request` trigger
now carries only image-DEFINING inputs, and managed source moved to the batch
train's `aot-build` (compile risk, pre-merge), `pr-gate.yml`'s detector fixtures
(per push), and the nightly/release/deploy lanes (final rootfs). See
`workflow-inventory.md` for the placement table.

Two consequences for this observer:

- **The legacy replay policy changed.** `.github/native-image-impact.json`'s
  `legacy.serving_*` patterns were narrowed in lockstep with the workflow, as
  the fail-closed case-arm cross-check requires. Receipts emitted before that
  change replayed a different legacy policy and are bound to a different policy
  blob SHA; they are not comparable to later ones and must not be pooled with
  them in a promotion count.
- **The cohorts invert.** Managed-source heads now report
  `serving_candidate_only`, not `serving_legacy_only`: the graph-derived
  candidate would re-add exactly the per-push image builds the narrowing
  removed. The candidate router as written is therefore no longer a narrowing of
  the authoritative trigger and cannot be promoted on a "narrows nothing, so it
  is safe" argument. Its promotion criteria need restating against the new
  baseline before any enforcement decision.

#### What the digest addresses, exactly

- **The merge tree, not the head tree.** Both authoritative workflows check out
  the `pull_request` merge ref (`actions/checkout` with no `ref:`, and
  `HEAD_SHA: ${{ github.sha }}`), so two heads with equal *head* trees can still
  be built from different *merge* trees whenever trunk moved a shared input
  (`Directory.Packages.props`, `eng/*.props`) between them. The observer fetches
  `refs/pull/<N>/merge` and accepts it only when its parents are exactly the
  observed base and head commits; the receipt records
  `image_input_tree: merge` and the merge commit id. This is not hypothetical:
  8 of the 77 trunk commits landed in a 3-day window changed a serving image
  input, so the merge tree under an open pull request moves roughly every tenth
  landing. When no merge commit can be
  verified (conflicted, closed, or a ref that moved after the gate run) the
  receipt records `image_input_tree: head` and the ledger refuses to use that
  head as either a reuse source or a reuse consumer. This was chosen over a
  `base_sha`-equality side condition because the merge tree *is* the built
  input; base equality is only a proxy for it.
- **The file mode.** Each record is `[mode, path, blob id]`. A `chmod +x` or a
  symlink/regular swap keeps the blob id but changes the built root filesystem,
  and `git diff --diff-filter=...MT` already routes such a change as a rebuild
  (see the #1641 exec-bit regression).
- **Only the inputs of the image it addresses.** The digest and the routing
  decision are both projections of one `routing_reasons` definition, so an input
  cannot leave the content address without also leaving the decision. An
  equivalence test asserts this.
- **Selected paths only, fail-closed.** An unusual filename anywhere in the
  repository is tolerated by the tree parser and simply excluded; if such a path
  is ever *selected* as an image input the observation fails closed rather than
  digesting it.

#### What the reuse cohort will and will not credit

- Only the variants the authoritative workflow **actually builds**. The Serving
  Image Boundary workflow gates each variant on its own legacy case arm, so a
  variant the candidate selected but legacy did not is never built and never
  becomes evidence. Attestations are keyed by `(image class, digest)` so a
  collapsed variant pattern can never let one variant's build satisfy another's.
- Only when the earlier build **had finished**. Heads are ordered by when their
  own image work started, not by observer run id, so a `workflow_dispatch`
  re-observation or a PR Gate rerun of an older head cannot reorder the cohort
  or change the count. Two overlapping builds of identical inputs are two real
  builds and are counted as such.
- **Build reuse only — never scan reuse.** The GDAL Worker image runs an
  enforcing Trivy scan whose verdict depends on the vulnerability database at
  scan time. Identical inputs do not imply an identical verdict, so the scan is
  re-run on every head under any future enforcement, and the worker savings
  estimate covers build and entrypoint-smoke time only.

### Promotion gates

Routing authority stays with the existing workflows until at least 20 distinct
PR observations satisfy the machine-readable cohort policy in
`.github/impact-routing-promotion.json`. The report-only Impact Routing Evidence
Ledger selects only the producer's current attempt and rejects missing or
ambiguous artifacts, duplicate head identities, and missing receipts; receipts
pinning a superseded policy generation are excluded from the cohort rather than
failed (#3343). It queries the authoritative Serving Image Boundary and GDAL
Worker Image histories and counts an impacted head only when every required
image run at that exact head SHA, on that exact workflow, from a
`pull_request` event, completed successfully. A run's own `head_sha` is the
run-invariant binding; the `pull_requests` array on a workflow run is a LIVE
projection of the pull request and can only be trusted to say that a run belongs
to a *different* PR. Successful observer shells are never evidence by
themselves.

The ledger still requires positive impacted cohorts and a demonstrated savings
cohort; twenty unrelated negative observations cannot authorize promotion. The
savings gate is satisfied by **either** mechanism, and the ledger always says
which one:

- gates `serving_savings_sample_ready` / `worker_savings_sample_ready` are the
  OR that promotion reads;
- `signals` reports each mechanism separately
  (`serving_narrowing_ready`, `serving_reuse_ready`, `worker_avoidance_ready`,
  `worker_reuse_ready`);
- `savings_mechanism` names what the sample actually substantiated
  (`narrowing`, `avoidance`, `exact-input-build-reuse`, or nothing).

A reuse-only sample authorizes reviewing build-evidence reuse and **not** the
path router, which this document has already graded at zero savings. A reviewer
must read `savings_mechanism` before promoting anything.

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

Changing either classifier or `.github/native-image-impact.json` changes the
semantic `policy_generation_sha256`, which invalidates every outstanding receipt
by design. The generation manifest contains the PR Gate classifier, native-image
classifier, and native routing-policy blobs: the inputs that can change a
routing decision.

That reset is **cohort drift, not receipt loss**, and the ledger says so (#3343).
Workflow, observer, and trusted-resolver blob SHAs remain pinned in each receipt
as provenance. The PR Gate workflow, observers, and resolver define the generation
because they can alter routing or collection semantics. Serving/worker workflow
action-version, timeout, step-name, and comment edits do not discard an otherwise
valid cohort. This is fail-closed: the native classifier parses the authoritative
serving/worker workflow trigger paths and serving variant case arms and validates
them against `.github/native-image-impact.json` before emitting evidence. A
routing-relevant workflow edit requires a matching routing-policy edit, which
changes the generation; drift fails the observer instead of silently retaining
the old generation. Receipts pinning a previous semantic generation stop being
countable and are reported under
`policy_generation_superseded_receipts`. Reporting them as *integrity* failures
made the ledger red on routine repository maintenance and gave every
`observe`→`enforce` promotion gate an unusable signal; see
`impact-routing-evidence.md`, "What the ledger calls a failure".

The retained-ledger trend reports generation resets, resets per week, and the
largest docs-only/native sample reached within one generation. Those numbers
make the configured 20/20 cohort's reachability explicit.

The promotion policy and ledger contracts are
`honua.impact-routing-promotion-policy/v3` and
`honua.impact-routing-evidence-ledger/v4`: v4 resets retained promotion samples
after candidate-only routes stopped being countable without execution evidence;
v2 added required reuse minimums and
renamed the savings gates, and v3 added the receipt-loss budget, the indexing
grace window, and the consecutive-green promotion streak.

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

After enforcement, compare 30 runs with the ADR-0074 baseline. When narrowing
suppresses the workflow itself, this may use 30 same-period candidate heads
replayed against the old trigger under the counterfactual rules in
`evidence-driven-pipeline-baseline.md`; another workflow's runtime is not a
substitute for Serving runner consumption. Promotion still requires the
program-level latency and billed-minute thresholds; otherwise the router returns
to observation mode.
