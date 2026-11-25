# CI Workflow Inventory

The prior-reader compatibility job was removed on 22 November 2026. Migrations are forward-only, idempotent and tested on an installed stack; CI does not certify rolling rollback to an older reader. Required check names and the remaining PR Gate dependencies are unchanged.

Shared OGC capabilities XML tests live in the Wms namespace and run in Maps Supporting. The WMS class/complement partition and its legacy budgets stay unchanged; Supporting also retains catalog metadata and cross-protocol XML source triggers.

Server Features Streaming Snapshot and Conformance selects the snapshot and conformance classes. The deleted cursor-durability class is omitted from that shard, its parent and the miscellaneous residual filters.

The latest full-run capacity audit partitions eleven additional parents into 26
children: Scene I3S Resources, OData Advanced and Filters, MCP and Sessions,
Admin Authorization, Admin Release Control, Collaboration Mobile and Identity,
OGC API Features, GeoServices GPServer and NAServer, STAC Protocol, Operator
Eval Harness, and Migration. The first ten crossed 80% of their test budgets in
the last three full runs.
Whole-class selections are retained except the GP/NAS routing class, whose
methods are partitioned with a residual fallback for future tests. Method-union,
future-method and mutation checks enforce complete, disjoint selection and
unchanged projects, source triggers and budgets. Historical TRX interval weights
guide the cuts; they are not measured child elapsed runtimes. The 17 Nov rank
refresh measured all 26 children (gate-model.md, "Measured ranks").

OGC Classic WMTS exhausted its 20-minute test budget in full run `43612492209`
(job 132511815046) while producing output three seconds before termination; its
passing samples took 14.9 to 18.4 job minutes. Two children replace it, cut by the
serial class spans of run `43580101834` (12.2 minutes): child 1 owns
`OgcClassicWmtsTests` (7.7 minutes) and child 2 is the exact complement of the
parent filter (`OgcClassicWmtsTemporalTests` 3.1 minutes, six small classes and any
future Wmts class). Both inherit the parent's paths and the shared
`WmtsTileMatrixFormatting.cs` override. Initial ranks 12.8 and 9.6 are 5.1 minutes of
job overhead plus each child's spans (`estimatedRanks`). Budgets and source
triggers are unchanged.

Server Features Packages and Temporal measured 12.7 minutes (full runs
`43612492209` and `43580101834`), above the 10-minute PR shard budget, so PR Gate
would have deferred the executing owner of temporal, form and package sources to
trunk. Two children replace it, cut by the serial class spans of run
`43580101834` (9.0 of 12.8 job minutes): child 1 owns the two temporal history
classes, `PackageReviewEndpointTests`, `FormCompatibilityEndpointTests` and
`WorkflowPackagePublishDegradedTests` (4.8 minutes), and child 2 is the exact
complement of the parent filter (4.2 minutes). Both inherit the parent's paths,
so a temporal change runs both. Their initial ranks (8.6 and 8.0) are the 3.8-minute
job overhead plus each child's spans, recorded as `estimatedRanks` in the contract
until a full run measures them. Budgets and source triggers are unchanged.

Server Features Admin Layer Management exhausted its 35-minute test budget in
full run `43580101834` while producing output one second before termination.
Two children replace it: child 1 owns `AdminLayerDisplaySpatialTests`,
`AdminLayerAuthoringEndpointsTests` and `LayerStyleEndpointsTests` (10.4 of 20.1
minutes in the 13 Nov passing TRX), and child 2 is the exact complement of the
parent filter, including its `~Layer` method-name matches in other Admin classes
and any future ones. Host teardown in this test process grows from 0.4 to 16
seconds across 133 in-process hosts, so shard wall time rises with the square of
hosts per process. The split is capacity relief; the teardown leak is tracked
separately. Budgets, source triggers and the inherited dispatch rank are unchanged.


Migration exhausted its 29-minute test budget in full run `43564575677` while
producing output two seconds before termination. Three children separate migration
evidence/toolbox endpoints, scanner/run endpoints, and the residual migration
selection. Historical passing TRX class intervals weigh these groups at about
6.2, 5.7, and 5.2 minutes; these are sizing evidence, not measured child runtimes.
STAC qualification exhausted child 1 at 15.04 minutes and measured child 2 at
12.70 minutes. Four children now isolate search, spatial/temporal and queryables,
paging, and the residual selection. Original budgets, source triggers and
future-class fallback remain enforced by the conservation contracts.
Focused one-worker qualification passed all seven revised children: Migration
1/2/3 took 7.35/5.35/5.51 minutes and STAC 1/2/3/4 took
11.94/4.01/5.93/4.68 minutes. The largest Migration child finishes
21.65 minutes before the exhausted parent deadline; this is capacity headroom,
not a measured end-to-end workflow saving. STAC Protocol 1 is the single
class `StacSearchTests` at 11.94 of 15 minutes (80%); it cannot be split further by
class and is the next method-level partition candidate.

FeatureServer Replica now uses three whole-class shards: `FeatureServer Replica
Management` (conflict review, ownership and management), `FeatureServer Replica
Sync` (the sync class), and the residual `FeatureServer Replica` catch-all.
The original project, path triggers, exclusions, tier rules and 28-minute test /
38-minute job budgets are retained. The coverage validator enforces the legacy
parent inventory exactly once. This addresses repeated 28-minute capacity
exhaustion with active output. The children started from the conservative
18.3-minute parent dispatch rank; each now carries its measured rank ("Measured ranks").


The live-output capacity partitions replace File and Raster Import with three
numbered children (import orchestration, direct CRS/authorization contracts, and
the residual import/raster selection), Sharing with two (REST tokens and the
residual selection), and Analytics Studio Export and Reporting with three
(spatial analytics, export, and the residual feature-store/reporting selection).
Scheduled full run `43540829683` exhausted the respective 28-, 22-, and 22-minute
test budgets while producing output within eight seconds of termination. These
are incomplete baselines, not passing durations. Every child retains its parent's
project, routing paths, tier filters and test/job time limits; targeted overrides
select all siblings. The children started from their parent's rank and now carry measured ranks.
`test-live-output-capacity-partitions.py` conserves the original methods exactly
once, keeps each class intact for future methods, and rejects lost, duplicated,
foreign, budget-expanded or path-narrowed selections.
GeoJSON preview validation belongs to File and Raster Import 3; the router pins
its executable owner and selects every import sibling when its test file changes.


OData Core is partitioned into `OData Core Endpoints` (the whole
`ODataEndpointTests` class) and the residual `OData Core` catch-all. Both retain
the original project, path triggers, tier filters and 29-minute test / 39-minute
job budgets. Targeted overrides select both children; the coverage validator and
`test-odata-core-capacity-partition.py` enforce the original inventory exactly
once, including future methods. This addresses a 29-minute pre-PR capacity
exhaustion with active output at termination; it does not extend timeouts.

<!-- Generated by scripts/ci/generate-workflow-inventory.py. Edit the source template
scripts/ci/workflow-inventory.template.md for narrative and per-workflow notes. -->

> Canonical inventory of every workflow in `.github/workflows/`. File, name and
> trigger columns are derived from YAML. New workflows appear automatically.
> Feature PRs edit workflow YAML and the source template; submitted projections
> require canonical verification. Trunk automation also publishes this page.
> Run `python3 scripts/ci/generate-workflow-inventory.py`
> to preview it locally before validation.

**Branch protection requires `PR Gate` and `Review Gate` together**: unprivileged
verification plus trusted exact-head admission. `CI Gate` trails on trunk and
is deliberately not a per-PR required context. Repository hygiene is also required. Other advisory workflows are not required
contexts — the fleet lander selects on `mergeable`, not on
`mergeStateStatus`, so a red advisory check does not by itself block landing.
PR-time CodeQL security analysis comes from `codeql.yml`'s own
`pull_request` lane: code-scanning **default setup is `not-configured`** for this
repository, and every entry in `/code-scanning/analyses` carries
`analysis_key: .github/workflows/codeql.yml:analyze`. GitHub **Code Quality**, a
different product that publishes no security analysis, is switched off for this
repository.

`ci.yml` and `pr-gate.yml` PostgreSQL services use assigned host ports rather
than fixed 5432 bindings. Full-CI jobs export the assigned port to database tests,
SQL clients, server setup and Docker smoke; concurrent jobs retain separate
service containers. Workflow/job counts and required gates are unchanged.

## Workflow syntax and expression validation

The architecture member of PR Gate's fast matrix runs actionlint 1.7.12 before
.NET setup, including for docs-only and generated-only changes. It checks all
`.github/workflows/*.yml`, adds no jobs, and fails the existing Fast result on
invalid syntax or expression contexts (including job-level `runner.temp`). The
binary is pinned by version and SHA-256; shellcheck and pyflakes remain separate
checks. `.github/actionlint.yaml` permits GitHub's `concurrency.queue` key and
does not suppress property-reference or expression-context errors.
The obsolete `optimize-disk` local action input is removed from Portal discovery.
No runner-count or runtime saving is claimed; the new check uses an existing job.

## Draft PR admission and superseded work

CodeQL, Compose Release Lifecycle, GeoArrow/GeoParquet interop, OGC API
building-block conformance, and serving/GDAL image verification defer heavy jobs
until the PR is ready. They subscribe to `ready_for_review` and ready-head
`synchronize` events; conversion back to draft cancels the active verification run.
Drafts run Format, Clean code, and Repository hygiene. The aggregate accepts
intentionally skipped heavy dependencies only for a draft event, requires Format
success, and rejects failures, cancellations or missing results. A draft run
reports it as `PR Gate (draft)`, and the skipped shard verdict is named
`PR Gate / Affected shards (draft)`; neither uses a required context. GitHub
creates a ready run's `PR Gate` check only when its needs finish (11 to 20
minutes), so a draft success under the required name let a PR marked ready merge
in that window without its heavy jobs. A head that has only draft runs now shows
both required contexts as expected, and blocks merging until the ready run
reports them. Ready PRs require every heavy dependency to succeed and run the
complete gate.

Verification workflows cancel superseded admission per PR. Review Event Bridge
and both Review Gate Attestation jobs queue older events by PR identity
across head updates instead of cancelling them. Completion payloads without
a PR number stay isolated by run ID until the trusted resolver identifies
the PR, so an unresolvable old completion cannot delay or cancel a resolver
for the current head. They skip ordinary draft PR payloads and re-read live
draft state before attestation, including comment
and workflow-completion events. CodeQL already cancels by PR number and skips
draft PR analysis; its scheduled/manual analysis remains enabled.
Review event ordering is not guaranteed. A late old-head event queues behind newer ones and reconciles the current live PR head. Draft conversion still reaches the shared attestation group, queues, and sheds the live draft before checkout or status publication. Other draft pull-request payloads skip resolution.

Trusted observers resolve the current PR before expensive jobs; unrelated
comments cannot interrupt paid work. New validation concurrency uses a unique run ID
outside PR events, keeping manual qualification independent of trunk pushes.
Existing release cancellation policy is retained.

## Neutral automation helpers

Derived Artifact Normalization (`Generate bounded normalization projections`,
`Produce data-only normalization envelope`) cancels repeated eligible runs for
one PR head. Different heads use separate groups so delayed old events cannot
cancel current work and then shed without replacement. Draft and title/body-only
edits use isolated groups because they cannot generate a replacement envelope.
Trusted consumer admission resolves the live PR and cancels only older producer
generations before per-attempt validation. Observe mode retains envelope
validation and drift reporting. Source PRs commit regenerated projections, and
the existing architecture job verifies submitted bytes and modes after regeneration.
The MCP roster retains its existing valid-provenance contract; its generated body
and file mode must still match exactly.
Trusted ownership admission requires the current validator and required invocation;
older branches must adopt the repair. Generated-only publications retain canonical
trunk-producer proof. No runner job or required aggregate dependency changes.
Live-head checks and compare-and-swap remain required before branch mutation.

Auto-merge keeps `cancel-in-progress: false` and `queue: max` so live eligibility
is applied in event order. Review helpers (resolver, attestation, bridge) queue superseded events
with `cancel-in-progress: false` and `queue: max`, because a cancelled
helper run is a red mark on the PR page. Each queued run re-reads live
review evidence and admission state. A run that starts on a closed or draft
subject finishes neutrally before publication. An attestation whose live
review or admission state changes between its first snapshot and final
revalidation fails red; the queued event behind that change re-attests an open, ready
head. Bursts are not collapsed: each event runs its resolver and
attestation in turn; up to 100 wait per pull request, further arrivals are
cancelled, and a waiting run still re-reads live state after them.
Missing admission prerequisites finish the
attestation helper neutrally while the separate required `Review Gate` status
remains failing for blocking reviews. Skipping a draft publishes no success;
`ready_for_review` re-enters trusted attestation on the current head. API and
attestation errors still fail; helpers cannot override required admission policy.

CI Doctor, PR Review Doctor and PR Sync Doctor were retired after being
disabled in repository settings. Their generated lock workflows, authored
Markdown sources and doctor-only tooling have been removed.

## Required PR lane

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `heavy-shutdown-retry.yml` | Heavy Runner Shutdown Retry | `workflow_run` (completed CI) | One bounded retry of proven full-trunk heavy runner shutdowns; no PR jobs. |
| `pr-gate.yml` | PR Gate | `pull_request` (base `trunk`), `workflow_dispatch` | Required verification context. Admission checks run first. `pr-gate` builds the affected graph plus planned managed test roots and the server host, publishes pristine per-project outputs and runs the required boot/migration smoke against its canonical host output. The four Fast and architecture check names stay alongside it: `fast-tests` runs `architecture` and `unit-fast`, and `fast-server-tests` runs `server-fast` and `server-governance`, selecting each build/test body by the shared affected-project reverse closure and its existing test roots (empty closures skip only with positive governed-docs proof; unknown selection retains all). Every nonempty managed closure also retains architecture and Server governance checks. `architecture` and `unit-fast` compile only their own test roots. `fast-server-tests` restores the producer Server.Tests output for `server-fast` and `server-governance` and compiles that project only when the restore is empty. The groups are: `architecture` (projection regeneration, source and collector architecture, MCP roster drift), `unit-fast` (isolated unit Fast tier), `server-fast` (Server Fast tier) and `server-governance` (Server governance/drift with PostGIS); both server jobs first refresh the admin parity exports Server.Tests compares. The `format` matrix runs `dotnet format` and the analyzer build (affected closure, analyzers, code style and documentation diagnostics as errors) as four jobs: Format, Analyzers (source projects), Analyzers (tests), and Analyzers (server tests, including the architecture collector so other tests do not recompile Server.Tests). Drafts instantiate only Format; ready PRs instantiate all four rows. The source job always retains all solution source roots, including dependencies of affected test roots, and evaluates ownership after SDK/NuGet imports and removes test-only compilation roots (multi-targeted roots remain conservative); all analyzer jobs compile forward dependencies; `HonuaAnalyzerPartition` assigns each project to exactly one diagnostics pass using `IsTestProject`. Runtime execution proofs and fidelity run in full CI, followed by the default PostGIS profile and connection-write policy checks. The independent format and CI-router jobs remain required; `required.needs` is `[pr-gate, fast-tests, fast-server-tests, format, ci-router-validation, affected-shards, server-test-producer]`. Selected shard jobs, bounded by the `pr_time_budget` in `.github/ci-shards.json` (over-budget shards are deferred to the next trunk run and named in a green `deferred` verdict), consume the same outputs; their aggregate enforces measured timing/TRX receipts and fails the required context on failed or missing execution. Shard artifacts are named per run attempt. The aggregate downloads only the newest artifact id for each selected shard (`scripts/ci/download-affected-receipts.py`), keeps an earlier attempt when a failed-jobs rerun did not repeat that shard, and does not fall back from a missing or expired newest receipt. Full CI still downloads every attempt directory without merging them. Review-first enforcement and docs-only exits remain inside the unfiltered workflow. See the [artifact contract](server-test-binary-artifacts.md) for identity, transport bounds, fallback and rollback. `runner-select` reserves the gate label for `pr-gate` and `server-test-producer` and plans `start_heavy`: half of the idle heavy runners beyond a three-runner reserve (shards, advisory), at most two per gate, take both producers when the gate pool is busy, then `analyzers-server`, `architecture`, `server-fast` and `analyzers-other` through the fork-guarded `HONUA_HEAVY_RUNNER` selector; PR Gate's runner select, Format and source Analyzers, shard selection, the shard aggregate, CI router validation and the required aggregate stay on `ubuntu-latest`. `HONUA_LIGHT_RUNNER` does not place them, so a busy light pool cannot hold the selector while `honua-build` runners sit idle. Unplanned test-analyzer rows and Fast/architecture groups stay on `ubuntu-latest`, as do drafts, forks and draft Format. Manual dispatch accepts a default-false `qualify_light` boolean: only `workflow_dispatch` on `refs/heads/trunk` may pin runner select, Format and source Analyzers, CI router validation and the required aggregate to the `honua-light` group and label. Shard selection and the aggregate run only on pull requests, and both test-analyzer rows stay hosted. PR/fork routing, producer admission and aggregate dependencies remain unchanged. No early light job inherits the two producer slots; existing even-index affected-shard routing is unchanged ([gate-model.md → PR Gate compute runner selection](gate-model.md#pr-gate-compute-runner-selection)). |
| `review-gate.yml` | Review Gate Attestation | `pull_request_target`, `issue_comment`, trusted `repository_dispatch`, `workflow_run` [PR Gate, Review Event Bridge] | Required admission context. Publishes `Review Gate` on the exact current head only when Codex has exact-head evidence and no unresolved Codex threads. Queues superseded events by resolved PR number and skips drafts, posts no red status on a pull request that merged or closed during attestation, pins the exact trusted workflow-policy SHA, and is the only authority allowed to release expensive verification. In observe mode it retains an immutable decision receipt. |
| `repo-hygiene.yml` | Repository hygiene | `pull_request` (`opened`, `synchronize`, `reopened`, `edited`, `labeled`, `unlabeled`) | Required additions-only context `repo-hygiene / Repository hygiene`. Label changes rerun every job, so the shared bundle policy judges the current `bundle-exception` label rather than the label set at open. Only `synchronize` cancels a running run; other events queue behind it (#9615). Validates unique shard names and per-project filters against the PR merge tree in a five-minute job required by the existing aggregate; disjoint grouped projects may share a filter. Runs the execution-evidence checker and its tests from the trusted base commit against the explicit PR head, including older heads without those scripts. Calls the shared SHA-pinned workflow with read-only permissions; reads the repository-owned root allowlist and exact gate-input exceptions from the trusted base branch, rejecting new scratch, run output, dated history, samples and unexpected root entries. |
| `cancelled-check-rerun.yml` | Cancelled required check rerun | `workflow_run` (completed `Repository hygiene`, `Clean code`) | Starts a job only for a cancelled first-attempt `pull_request` run of either required light check. Reruns it once when it is the newest check suite of its workflow for a head that is still an open PR's head, because same-second events (opened + labeled, opened + edited) can make concurrency cancel the newest suite, whose cancelled required context then outranks the success (#9610, #9611). Older-suite cancellations and superseded heads are left alone. Light runner, no checkout, `actions: write` and `pull-requests: read` only. Tests: `scripts/ci/cancelled-check-rerun.test.js` in `validate-ci-router.sh`. |
| `review-event-bridge.yml` | Review Event Bridge | `pull_request_review`, `pull_request_review_comment` | Best-effort latency hint only; queues superseded events per PR and skips drafts. GitHub runs these event workflows from the PR merge branch, so the bridge is credential-free/no-checkout and is never trusted for invalidation or landing. |

The analyzer jobs enforce the [review prevention rules](analyzer-baseline.md) as
build errors. Existing findings use an explicit per-finding baseline; normal CI
never regenerates it. Format excludes only the eight build-enforced IDs because
its current SDK does not load diagnostic suppressors; required analyzer builds
retain their full enforcement. The baseline records both supported SDK spellings
of eight existing CA2000 findings.

### Docs-only exit (`PR Gate`)

Operator ruling A (2026-09-11, #3213): a documentation-only pull request does
not pay for the lean-gate build and unit smoke. The exit lives **inside** the
required jobs. `PR Gate / Build and tests`, the four `PR Gate / Fast and
architecture (<group>)` jobs, `PR Gate / Format`, `PR Gate / Analyzers`, `PR Gate / Analyzers (tests)` and
`PR Gate / Affected shards (select)` still run and conclude success themselves,
and so do the `PR Gate` and `PR Gate / Affected shards` aggregates. There is no
`paths` filter, because a skipped required check blocks merge.

**What counts as docs-only** is defined once, in `scripts/ci/docs-only-diff.sh`
(its header is the full contract). To see why a documentation change ran the
full gate, run `DOCS_ONLY_EXPLAIN=1 scripts/ci/docs-only-diff.sh --paths` with
the changed paths on stdin.

- Every changed path must be a top-level Markdown file or a prose/image file
  under `docs/` (`.md .png .jpg .jpeg .gif .svg .webp`) outside
  `docs/gis/data/`. A rename counts as both its old and its new path. Deleting
  a top-level Markdown file, or turning a file into a symlink, is never
  docs-only.
- No changed path may be referenced by a gate input. At gate time the
  classifier scans everything the skipped steps compile, execute or read:
  - all of `src/` and `tests/dotnet/`, and the root build files;
  - this workflow, its composite actions, and the scripts its skipped steps run;
  - `docker/worker-gdal/`, `certification/`, and every non-prose file under
    `docs/`.

  It looks for path literals, `CombinePath(root, "docs", ...)` segment paths,
  quoted top-level Markdown names and MSBuild item paths. A document that an
  architecture or Server test reads, such as `docs/cite-status.md`, therefore
  always runs the full gate. A test that starts reading a document governs it
  from that commit on, with no list to update. On 2026-09-11, 368 of the 425
  prose documents qualified.
- Any doubt means "not docs-only": a non-`pull_request` event, a checkout that
  is not the two-parent `refs/pull/N/merge` commit, an empty diff, more than
  400 changed paths, or any classifier failure. A failure costs a full gate
  run, never a skipped one.

**What a docs-only diff still runs:**

| Job | Still runs | Skipped |
|---|---|---|
| `PR Gate / Build and tests` | The UTF-8 tracked-file guard (#3320); the terminal-error receipt, client-certification roster, review-first admission and base-image inventory checks; the admission receipt; the classifier's own fixtures; the Markdown command policy; installation-reference validation; and a public-image Docker proof only when artifact versions or executable installation procedures/harness change. | Disk cleanup, .NET setup, managed producer planning, compilation, managed artifact publication and source-built boot/migration smoke. |
| `PR Gate / Fast and architecture (<group>)` | Checkout and classification. | Disk cleanup, .NET setup, the group's test-root build and its tests. |
| `PR Gate / Format`, `PR Gate / Analyzers`, `PR Gate / Analyzers (tests)` | Checkout and classification. | .NET setup, restore, and `dotnet format` or the analyzer build. |
| `PR Gate / Affected shards (select)` | Classification. The job publishes `skip=true` with `reason=docs_only`, so the aggregate reports `skipped`. | Selector validation and shard selection. |

The analyzer build step disables persistent MSBuild servers and node reuse;
all compiler, analyzer and timeout failures still fail the check.
Test-analyzer rows never use the light pool, including manual light-pool
qualification; they stay on `ubuntu-latest` unless runner-select planned them
onto the heavy pool. Format and source Analyzers stay on `ubuntu-latest` as well,
except when a trunk dispatch sets `qualify_light`.
Only the formatter matrix row performs the preliminary whole-solution restore.
Analyzer rows restore their selected Release graph inside the shared format
action before source ownership evaluation and compilation.

Outside `PR Gate`, `docs-link-gate.yml` keeps firing on `docs/**/*.md`, the
data files its checkers read, and `README.md`: links, anchors, and the gate that rejects
added raw HTTP shell examples. It validates a pull request as its merge into
trunk, so the checkers and the files they read match the workflow definition
whenever the head was cut. The OKF agent graph it uploads is the exception:
it is generated from the exact head in a separate tree by that head's own
generator. A documentation defect on trunk fails every pull request the gate
runs on, and a rerun reuses the same merge, so recovery after the trunk fix
needs a new event on the pull request.
Review-first enforcement is unchanged, because an enforced attempt 1
fails at `Await exact-head review` before the classifier runs. Each docs-only
job records `docs_only=true` as a step output and the summary line
`PR Gate: docs-only diff, build and tests skipped (<reason>)`.

The installation artifact manifest, `docs/llms.txt`, and the derived OKF domain
graph qualify for the docs-only path. The runtime-reference scan still applies:
adding a compiled consumer of any of these files restores runtime validation.

## Merge and landing

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| Fleet serialized per-PR lander | External service | **Routine merge authority.** Immediately before merge, requires an open, non-draft, MERGEABLE PR based on `trunk`, no hold/escalation label, exact-head successful `PR Gate` + `Review Gate`, and zero unresolved review threads. A deterministic trailing trunk CI failure pauses routine landings except fix-forward branches. |
| `auto-merge-ready.yml` | auto-merge ready pull requests | `pull_request_target` (opened, ready, reopened, synchronize, labels, draft conversion, edited) | Repository-scoped bot App requests ruleset-governed auto-merge for eligible same-repository trunk PRs; new requests default to squash for single commits and merge for bundles. Eligible existing requests retain their merge method, except that a bot-armed squash switches to merge once the PR has several commits; only hold-label and base-change label/edit events start a job; live hold, draft and base checks revoke ineligible requests. |
| `ci.yml` | CI | `schedule` (07:23 and 13:23 UTC), `workflow_dispatch` | No `pull_request` trigger. Its `CI Gate` context validates scheduled or manually dispatched integration runs. Runtime proofs and import/export fidelity require the managed producer; native GDAL/PDAL and raster receipts run in the existing Worker job. Managed build producer, independent required full-solution format job, test and both architecture suites, CI-router validation, JS typecheck, and Postgres compatibility. Foundation families and selected server shards need the producer, restore verified outputs with MVC and static-asset runtime content roots relocated to the consumer checkout, and compile only their missing project union. Source architecture runs after projection regeneration; the full collector remains in the foundation server family. See the [artifact contract](server-test-binary-artifacts.md). Per ADR-0037 the `targeted-shards` job runs `scripts/ci/honua-server-targeted-tests.sh` and emits a JSON `matrix_include` drawn from `.github/ci-shards.json`; `server-tests` consumes it via `strategy.matrix.include: fromJson(...)`, so unselected shards never instantiate a runner. The full shard matrix runs on scheduled/manual full integration runs and PRs labeled `ci/full`. On a managed-artifact miss, the existing legacy cache path remains available: `server-tests` shards can opportunistically materialize their own run-scoped exact-head binary payload on attempt 1 (one designated writer per project publishes; siblings do a single fail-open lookup and never wait) when `HONUA_SERVER_TEST_PREBUILD_CONSUME=true`; absent/false is off and rollback is one variable update — contract in `docs/internal/ci/server-test-binary-artifacts.md`. `scripts/ci/run-server-test-shard.sh` composes each shard filter as `(matrix.filter)&Tier!=Slow&Tier!=Fast`, emits heartbeat/tail diagnostics, writes `.timing.json`, and enforces the inner `test_timeout_minutes` cap before the job-level `timeout_minutes` cancels the runner. The shared quarantine filter runner records `HONUA_QUARANTINE_FILTER` in retained test logs with the effective filter, excluded methods, owning issues and expiry times. `pr-template-check` and `pr-readiness` short-circuit to success on every non-`pull_request` event, so today they are no-op roll-ups into `CI Gate`. |
| `trunk-affected.yml` | Trunk affected run | `push` (trunk, no path filter), `workflow_dispatch` | Stage 3 of the [gate model](gate-model.md#verification-stages). Resolves the head of the most recently completed `CI` run on trunk (success only, excluding failures and cancellations; newest 100 runs ordered by completion update time) that is an ancestor of the pushed head and dispatches `ci.yml` with `selective_base` set to it, titled `CI affected <base>..<head>`, so only the shards and lanes the new merges touch run. Runs queue in one concurrency group without cancellation; a run whose push is no longer the trunk tip exits, because the newer push's run covers it. Writes the separate culprit range since the last full green ancestor (successful affected runs cannot clear earlier failures) (commits and pull requests) to its summary; passes that ancestor separately as `investigation_base` so the dispatched run's `changes` job writes the same culprit range to its log and summary for the lane investigating the failure. Missing green history is reported as unknown. Affected runs never certify a sha for the nightly image (`select-certified-nightly-sha.py` ignores their title). Not a required check. Needs only `actions: write` and `contents: read`. Admission and execution recheck a conservative 40-job total bound (33 side jobs plus selected shards); full routing or unknown history defer coverage to scheduled full CI without a successful CI baseline; the `Record coverage deferred to scheduled full CI` step marks that head, and later pushes start from the newest marked ancestor so a deferred range does not hold back later merges. Affected server-shard concurrency is two. An active PR Gate neither defers nor cancels an affected run. |
| `trunk-signal-watchdog.yml` | Trunk signal watchdog | Half-hourly `schedule`, `workflow_run` (`CI`, `Full CI dispatch` on `trunk`), `workflow_dispatch` | Opens or updates a `trunk-signal` issue and fails when a running run has not produced a result within 4 h of its first job starting on a runner, the last result is older than 22 h, a run or child job queued over 45 min, at least 6 of the last 10 runs created in the last 6 h were cancelled, or no scheduled full CI run started in 20 h. Queries scheduled runs separately so selective traffic cannot hide missed delivery. Reads jobs in pages of 25 with bounded retries for transient server errors, and fails visibly if a page cannot be read. A queued workflow counts as started only when job runner assignment proves execution. Child queues remain monitored, and the issue closes only when all signals recover. |
| `scheduled-failure-issues.yml` | Scheduled failure issues | Hourly `schedule`, `workflow_run` (scheduled or dispatched runs of the server nightlies and `CI`), `workflow_dispatch` | Sweeps every non-archived honua-io repository with an organisation bot App token (`actions: read`, `issues: write`). Each failed scheduled run, plus the bot dispatches named in `scripts/ci/scheduled-failure-owners.json` (`CI full` runs of `ci.yml`, candidate-tag runs of the nightly container build), opens or comments on one `bug` + area + `scheduled-failure` issue per workflow and failing job, with the owner lane from that file, a 24-hour due time and a `claimed:` comment; gate and summary jobs count only when nothing else failed, and a run already recorded on a closed issue is not filed again. A later successful run closes the workflow's issues (for the container build, only a run whose manifest publish succeeded). A success between failures closes the issues it fixed. A failure followed by a success before any sweep is not filed, and a nightly container build that publishes nothing (its candidate already imaged) neither closes nor files. Logs and the summary never name private repositories. Plain REST; no model. Tests: `scripts/ci/scheduled-failure-issues.test.js` in `validate-ci-router.sh`. |
| `full-ci-dispatch.yml` | Full CI dispatch | `push` (trunk), hourly `schedule` 05:41-10:41 UTC, `workflow_dispatch` | Dispatches one full `ci.yml` run on trunk between 22:00 and 03:00 Pacific (PST or PDT) when none is queued or running, trunk head has no full result and the night has had fewer than two full runs that were not cancelled. GitHub delivers this repository's cron events hours late, so the `ci.yml` crons alone often land in the daytime window. A night with no trunk push before 03:00 Pacific relies on those crons. `HONUA_FULL_CI_DISPATCH=off` disables it. |
| `native-tools-publish.yml` | Publish trusted native tools | `push` (native inputs on `trunk`), weekly `schedule`, `workflow_dispatch` | Trusted trunk-only production `native-tools` image publisher; dedicated image/cache, exact input identity and immutable digest attestation. Consumers verify provenance and retain the bounded production build on any miss. No merge authority; existing native/AOT execution proofs remain required. See [native tools reuse](native-tools-reuse.md). |
| `gp-devkit-publish.yml` | Publish GP Devkit | `workflow_dispatch` only | Packs `Honua.Geoprocessing.Cli` at the repository version and pushes it to GitHub Packages only for an explicit canonical-trunk dispatch. The publication identity is retained for honua-release#615. Pull requests and installed-authoring candidates do not publish. |

## PR-triggered (advisory, not required)

The scoped `GP Alias Contract` check runs the real GeoServices alias tests and OGC HTTP process-list regression when their contract, judgment, tests, or workflow changes. It checks TRX execution counts and canonical method identities, including each routing alias case.

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `telemetry-selector.yml` | Telemetry selector | path-scoped `pull_request` (ready heads only), `push` to `trunk` | Runs the six canonical platform-fault selector cases with checksum-pinned Prometheus promtool 3.5.0 on a GitHub-hosted runner. Missing tools and selector regressions fail the job; draft conversion cancels admission. |
| `compose-release-lifecycle.yml` | Compose Release Lifecycle | `pull_request` (base `trunk`, relevant executor, control-plane, and test paths) | Builds the Native AOT candidate (reads registry cache `buildcache:honua-aot-amd64` on same-repo PRs) and exercises the published rc.1 to approved MCP convergence and verified PostGIS restore journey, including negative controls. Manual candidate upgrade and return run migrate-only with Gate and an exact reviewed contract-script hash allowlist before startup; approval retry must finish with a verified schema. Manual readiness failures record bounded, secret-redacted service state and server errors before cleanup; diagnostic redaction is unit-tested before image builds when its test module exists in the checked-out PR. Older heads skip only the diagnostic and contract-approval unit steps whose test modules they lack. |
| `close-external-prs.yml` | Close external pull requests | `pull_request_target` (`opened`, `reopened`) | Closes pull requests whose author association is not `OWNER`, `MEMBER` or `COLLABORATOR` (bots exempt) with a comment pointing to issues and `SECURITY.md`; this repository does not accept external contributions. Never checks out PR code; `pull-requests: write` only. Durable complement to the six-month repository interaction limits set 2026-10-23. |
| `clean-code.yml` | Clean code | `pull_request` (`opened`, `synchronize`, `reopened`, `edited`) | Calls the shared honua-io/.github clean-code check, pinned by SHA: the Clean Code rules judged only on what the PR makes worse (comment, size, function, parameter and duplication ratchets), each finding titled with its smell code. `edited` re-runs it when a `quality-exception:` line is added to the PR body. Only `synchronize` cancels a running run; other events queue behind it (#9615). Read-only `contents` permission. |
| `runner-label-lint.yml` | Runner label lint | `push` (`trunk`), `pull_request_target` (`opened`, `synchronize`, `reopened`) | Runs only when `.github/workflows/**` changes. Calls the shared SHA-pinned honua-io/.github runner-label lint with read-only permissions. Fails when a job reachable from a pull request or `workflow_run` event could resolve `runs-on` to a self-hosted runner without the fork guard; it detects such changes, while runner-group settings and fork-workflow approval are what block fork jobs. Runs from the base branch's caller; checks out only the PR's `.github/workflows` (merge ref, else head) without credentials and parses it as YAML, never executing it. Advisory until made required. |
| `gp-installed-authoring.yml` | GP Installed Authoring | path-filtered `pull_request`, `workflow_dispatch` | Packs the candidate devkit, then exercises Python and .NET authoring on Linux, Windows and macOS using only the installed package (no source checkout). Requires positive and negative goldens, four direct tests per language, exact-version payload restoration and missing-image-evidence release refusal. Uploads a three-OS host-debug handoff for honua-release#615. Does not publish packages or qualify a worker image. |
| `gp-alias-contract.yml` | GP Alias Contract | `pull_request` (base `trunk`, relevant contract/test/judgment paths) | Runs the GeoServices alias contract and the OGC API process-list HTTP regression in their actual protocol assemblies. Uses the existing .NET setup and pinned PostGIS service conventions; requires all selected cases to execute and pass. |
| `routing-utility-contract.yml` | Routing Utility Contract | `pull_request` (base `trunk`, GPServer/NAServer discovery and test paths) | Runs the provider-level routing descriptor, catalog collision/access guards, and existing routing utility, metadata, anonymous, SOAP and FindRoutes contracts in the GeoServices component assembly. Requires every new source method and every selected result to execute and pass; retains TRX proof. |
| `featureserver-relationship-contract.yml` | FeatureServer Relationship Contract | `pull_request` (base `trunk`, relationship projection/test/workflow paths) | Runs service and layer relationship HTTP regressions in the actual GeoServices assembly using the existing .NET setup and pinned PostGIS fixture. Requires every source-derived case to execute and pass, including reciprocal directional names, identity collisions, and authorization visibility; retains TRX proof. |
| `gp-customcode-runtime.yml` | GP Custom Code Runtime | path-filtered `pull_request`, `workflow_dispatch` | Executes Python and .NET standalone custom-code harness regression suites, including real loopback broker uploads; retains JUnit/TRX evidence. Also builds both custom-code worker images (hash-locked packages, digest-pinned .NET SDK). No live cloud qualification is implied. |
| `gp-scoped-key-contract.yml` | GP Scoped Key Contract | `pull_request` (base `trunk`, GP execution/authentication/test paths) | Runs shared job authorization and terminal regressions plus GP REST/SOAP endpoint and durable-runtime tests in their actual assemblies with PostGIS and cache fixtures. Requires every new source-derived scoped key case to execute and pass; retains TRX proof. One job per test project builds it once and runs each case with `--no-build` and its own TRX (`scripts/ci/gp-scoped-key-contract.py`): 6 jobs instead of 13. |
| `ai-admission-contract.yml` | AI Admission Contract | `pull_request` (base `trunk`, AI admission/reporting/control and relevant project/fixture paths), `workflow_dispatch` | Bounded feature evidence on one Docker-capable runner: sequential Release tests for Server reporting and canonical admission controls/toolset, Core reporting, accepted Studio prompt budgets, Studio certification endpoints, and PostgreSQL admission scopes/lifecycle. Requires positive execution of every named class, at least two prompt-budget, three certification endpoint and sixteen PostgreSQL cases, and zero failures/skips; retains raw TRX and tested commit identity. Distributed-cache backend/coordinator cases remain in Hosting Components. Adds no required context, routing-cap increase or installed/rollback qualification. |
| `portal-group-discovery-contract.yml` | Portal Group Discovery Contract | `pull_request` (base `trunk`, Portal read/store/test paths) | Runs actual Portal user/group HTTP integration and existing sharing/read regressions with PostGIS. Requires each new source-derived paging and denial case to execute and pass; controlled test-store state is not native production content evidence. |

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `alerting-enabled-candidate.yml` | Alerting Enabled Candidate | path-filtered `pull_request`, `workflow_dispatch` | Exact-candidate qualification lane for the opt-in (`Alerts:Enabled=true`) Postgres webhook delivery path. On pull requests, validates the receipt schema only. On manual dispatch, validates an existing hosted-evidence artifact for the supplied source SHA and retains the receipt for 90 days; it does not produce that artifact or execute .NET tests. The ordinary Console and Alerts CI shard executes the bounded webhook fixture. Required PR Gate executes the retained Preview audit/isolation floor tests. |
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | `pull_request`, `workflow_dispatch` | Path-scoped to the API surface; enforces the breaking-change policy (`OPENAPI_ALLOW_BREAKING_CHANGES` is the deliberate escape). |
| `openapi-drift.yml` | OpenAPI Drift Check | `pull_request`, `workflow_dispatch` | Regenerates the OpenAPI document and fails on drift from the committed contract. |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | `pull_request`, `workflow_dispatch`, `release` | PR governance for the control-plane SDK surface, separate from release publishing. |
| `import-fidelity-scorecard-governance.yml` | Import Fidelity Scorecard Governance | `pull_request`, `workflow_dispatch` | Path-scoped to parity/baseline/perf-budget assets; smoke-tests the perf-parity gate (#1249) with pass/fail fixtures. |
| `capability-matrix-aggregation.yml` | Capability Matrix Aggregation | `pull_request`, weekly `schedule`, `workflow_dispatch` | Joins `feature-catalog.json`, `docs/cite-status.md`, the GeoServices REST parity index, capability crosswalks, and committed client-compat envelopes into `docs/gis/data/capability-matrix.v1.json` (#2892/#2893). Generator/input validation is strict; committed-file drift is advisory. PR inputs include the licensing constants in `FeatureCatalog.cs` used to resolve manifest entitlements. PR operation-capability completeness, registry resolution and authored artifact checks run in the existing PR Gate architecture job after canonical catalog regeneration; scheduled/manual aggregation runs audit the committed catalog. |
| `install-images.yml` | Install Images | `pull_request` (base `trunk`, install image paths), `push` to `trunk` (same paths), weekly `schedule`, `workflow_dispatch` | Builds the small images installs run beside the server: `docker/postgres-client` (psql for the Launch Stack start-up containers) and `docker/console-web` (the Compose quickstart Console web server) and `docker/compose-postgres` (the Compose installs' PostgreSQL 17 / PostGIS / pgRouting database). Pull requests build, smoke and block fixable HIGH/CRITICAL findings on linux/amd64 and linux/arm64, and `database-upgrade` runs `scripts/docker/test-compose-postgres-upgrade.sh` (a volume created on the previous base starts with refreshed collation versions). Trunk, schedule and dispatch runs pass `publish-authorization.yml`, push one multi-platform digest per image to the public `honua-server` package, run `scripts/ci/trivy-release-gate.sh` on each platform of that digest, then assign `<image>` tags, attest and verify anonymous pull. |
| `serving-image-boundary.yml` | Serving Image Boundary | `pull_request` (base `trunk`, **image-defining paths only**), `workflow_dispatch` | Pull requests build and boundary-verify the PostGIS serving image only. Manual dispatch still builds PostGIS and Lambda. Lambda native-AOT is not a pull-request build; `nightly-container-build.yml` and `deploy-platform-images.yml` keep it. Since #3204 the trigger carries only inputs that DEFINE the image (the serving Dockerfiles, `.dockerignore`, the in-image restore helper, the boundary verifier and its fixture harness, this workflow); managed source (`src/**`, `eng/**`, solution, build props) is deliberately not a trigger and is placed on the lanes in the table below. The trigger paths and the in-workflow variant `case` arms are parsed and cross-checked by `scripts/ci/native-image-impact.py`, which fails closed on drift from `.github/native-image-impact.json`. Exact v3 content addresses may reuse a successful per-variant verification marker only when `HONUA_SERVING_IMAGE_SKIP` is exactly `true`; absent/false is full verification, and every digest/decision is recorded in the run summary. Promotion and one-variable rollback are documented in `serving-image-content-addressed-skip.md`. Deliberately isolated from the required lean `PR Gate`. |
| `store-image-size.yml` | Store Image Size | image-input `pull_request`, `workflow_dispatch`, `workflow_call` | Builds PostGIS JIT/AOT and full JIT images, records before/after byte counts and compiler inputs, rejects excluded stores and satellites, and proves startup refusal, format import/export, label rendering and security scanning. Scheduled CI and manual `full_ci=true` runs call it on their own commit; `CI Gate` requires its success when selected. Evidence is uploaded as run artifacts. |
| `worker-gdal-image.yml` | GDAL Worker Image | `pull_request` (base `trunk`, path-filtered), weekly `schedule`, `workflow_dispatch` | Builds the GDAL worker image, proves native handoff/PDAL/public API execution, and enforces unchanged Trivy policy; publishes SARIF. Explicit canonical-trunk dispatch can publish the exact tested amd64 bytes under a unique nightly tag with verified source provenance, including a trunk-reachable `candidate_sha` whose nightly tag and image revision name that commit while the attestation stays on the trunk workflow SHA; PR/schedule runs never publish images and receipts are qualification:false. |
| `geoarrow-interop-fixture.yml` | GeoArrow Interop Fixture | GeoArrow-named `pull_request`, `workflow_dispatch`, `workflow_call` | Produces the GeoArrow 0.2 interop fixture. Pull requests run it for GeoArrow-named files, the shared formatter/CRS helpers it names and the package graph; scheduled CI and manual `full_ci=true` runs call it on their own commit and `CI Gate` requires its success there. |
| `geoparquet-interop.yml` | GeoParquet Interop Evidence | Parquet-named `pull_request`, `workflow_dispatch`, `workflow_call` | Pull requests run it for Parquet-named sources and tests, the Python readers and seed data, and the package graph; scheduled CI and manual `full_ci=true` runs call it on their own commit and `CI Gate` requires its success there. Builds the exact source head and requires 3 OData, 8 import, 10 schema and 5 independent-reader cells. Retains TRX/JUnit and JSON execution receipts; skipped or missing cells fail. Pre-cut source evidence, not exact-candidate certification (#4396). |
| `export-portolan-check.yml` | Export Portolan Check | Export-path `pull_request`, `workflow_dispatch` | Advisory, not required (#8268). The paired-publish integration test publishes an Export and copies its version files to disk; `scripts/ci/check-export-portolan.py` runs the validator pinned in `scripts/ci/portolan-pin.v1.json` over them. Specification findings are summary, warnings and a retained report and never fail it; a test failure, an empty snapshot, a validator crash or a different specification than the pin does. Drafts skip it. |
| `docs-link-gate.yml` | Docs Link Gate | `pull_request` (base `trunk`, path-filtered to `docs/**/*.md`, the data files the checkers read, and the checker), `workflow_dispatch` | Stdlib-Python `scripts/ci/check-doc-links.py`: relative links AND `#fragment` anchors across `docs/**/*.md` (GitHub slug algorithm, ported from `geospatial-mcp`'s `tools/check_links.py`), plus `scripts/ci/code-referenced-anchors.v1.json` — the absolute phase-A `https://honua.io/docs/<slug>/` URLs product code and shipped config hand to an operator or an agent at runtime (`remediationRef`, SCIM `documentationUri`, Prometheus `runbook_url`). Warns when a URL survives only via a `.gitbook.yaml` redirect; errors on a `docs.honua.io` URL (that host is dark until 2026.2) and on a phase-A URL that lacks the trailing slash or is not listed in the manifest; treats a `pendingPr` entry as warn-until-present so an anchor introduced by an open PR is enforced only once it lands. Pre-existing rot is carried in `scripts/ci/doc-link-rot-allowlist.v1.json` with a stale-entry ratchet. Not folded into `PR Gate` (which must stay unfiltered) or `ci.yml` (no `pull_request` trigger). Reachability grades tracked pages still present after generation; links to deleted targets still fail. Older PR heads without either the inventory generator or its template validate their existing documentation; partial generator/template pairs and generator failures remain errors. |
| `availability-evidence-contract.yml` | Availability evidence contract | `pull_request` (comparison-checker paths), `workflow_dispatch` | Regression tests for the exact two-replica/four-cell evidence checker; retains JUnit results. Synthetic checker fixtures do not certify an immutable candidate or provide a distributed SLI source. |
| `normalize-derived-artifacts.yml` | Derived Artifact Normalization | `pull_request` | Untrusted producer: may execute PR code but can only read the repo/packages and upload a bounded data artifact (#3219). Runs only when its recipe or a pinned generator input changes; PR Gate's architecture job regenerates the projections and reports drift on every ready PR. |
| `release-bundle-tooling.yml` | Release Bundle Tooling | `pull_request`, `push` (trunk), `workflow_dispatch` | Verifies the deterministic, locally-runnable core of the release-bundle orchestrator (manifest generator, evidence collector, dispatch helper, suite registry). |
| `issue-capability-check.yml` | Issue Capability Key Check | `issues` | Advisory comment when a bug/feature issue's capability key is missing or unrecognized (#2896). Never labels or fails. |

`codeql.yml` also runs on a path-filtered `pull_request`; it is listed under
*Nightly and scheduled test lanes* because the same file owns the weekly deep
scan.

### Native-image evidence placement and measured savings (#3204)

Measured on trunk for the 2026-08-13 -> 2026-08-17 window (100 most recent runs
per workflow, plus 74 distinct trusted observation receipts):

| Signal | Serving Image Boundary | GDAL Worker Image |
|---|---:|---:|
| Runs sampled | 100 | 100 |
| Successful | 19 | 22 |
| Cancelled by a newer push | 80 | 78 |
| Median successful wall time | 140 min | 18 min |
| Total successful wall time in window | 2667 min | 405 min |
| Median cancelled wall time | 0.7 min | 0.7 min |

Cancellation is already cheap: `concurrency: cancel-in-progress` stops a
superseded run at a median of 0.7 minutes, so the 80/78 cancelled runs are not
where the money goes. The cost is the ~2670 serving minutes and ~405 worker
minutes actually spent on completed builds, at roughly 785 and 119 runner
minutes per day.

Path routing cannot recover any of it. Across all 74 distinct observed heads
the graph-derived candidate selected exactly the same images and the same
serving variants as the legacy path filters: 40/74 serving-impacted and 35/74
worker-impacted under both policies, with zero narrowed, zero avoided, and
zero candidate-only heads. Only 6 of 33 `src/**` projects sit outside the
serving closure, so the theoretical avoidance ceiling is small and the observed
rate is zero.

Repeat pushes are where the money goes. Grouping the impacted heads by their
selected image-input set shows 24 of 40 serving-impacted heads (60%) and 25 of
35 worker-impacted heads (71%) repeat an input set already built on the same
pull request; one review-heavy pull request contributed 21 serving builds
across 3 distinct input sets. The independent run sample agrees: 19 successful
serving runs across 11 branches and 22 successful worker runs across 10
branches.

The worker figure is a build-time bound only: the GDAL worker's Trivy scan is
enforcing and its verdict depends on the vulnerability database at scan time, so
it is re-run on every head and is never reusable.

#### Serving-image evidence placement moved (2026-08-25)

The two findings above are the same fact seen from opposite ends. A
graph-derived *router* cannot narrow this trigger, because `src/**` genuinely is
in the serving closure — and that is also why 60% of serving-impacted heads
rebuild an input set already built on the same pull request: the trigger is
keyed on managed source, which changes on essentially every review-fix push and
invalidates every variant at once. Routing accuracy was never the lever;
*placement* is.

`serving-image-boundary.yml` therefore now fires only on inputs that DEFINE the
image — the production serving Dockerfiles,
`.dockerignore`, the in-image restore helper, the boundary verifier and its
fixture harness, and the workflow itself. Managed source keeps its evidence, on
lanes that already existed or were extended here:

| Risk class | Proved by | When |
|---|---|---|
| Native-AOT compile (`src/**`, `eng/**`, build props, solution) | `ci.yml` `aot-build` | pre-merge, on the batch that lands |
| Boundary detector correctness (all clean and injected-rootfs fixtures) | `pr-gate.yml` | every push on every pull request |
| Final rootfs — PostGIS image on a pull request | `serving-image-boundary.yml` | when the PR touches the image, Dockerfiles or publish paths; Lambda is not built |
| Final rootfs — generic AOT, Lambda AOT | `nightly-container-build.yml` | daily, on the exact digest, before its manifest publishes |
| Final rootfs — every published variant | `deploy.yml`, `deploy-platform-images.yml`, `release-bundle.yml` | on the exact digest, before promotion |

One of those rows only became true as part of this change.

**`aot-build` had to be ungated from `full_ci`** (history; the batch train was retired 2026-11-15). An ordinary merge-train batch
is dispatched as `workflow_dispatch` on `train/batch/*` *without* `full_ci=true`,
which the `changes` job classifies as a **selective** batch and reports
`full_ci=false`. A `full_ci`-gated AOT job therefore never ran on the ordinary
landing path, which means the only pre-merge native-AOT compile signal for a
source change was `serving-image-boundary.yml` building the images on every push.
The job is now scoped to `integration_changes` + `non_test_changes`, keeping AOT
compile risk pre-merge at **batch** granularity: one job under a 45-minute
ceiling per landing batch, alongside the shard matrix (p90 88.6m), in place of
one container build per variant per review-fix push. An architecture test pins the
condition so it cannot silently regain a `full_ci` gate.

#3204's warning that deleting the PR triggers would trade cost for
delayed defects is honoured by keeping compile risk pre-merge, detector risk
per-push, and rootfs risk nightly — not by removing a class of evidence.

Expected effect: the workflow fires only on pull requests that touch an
image-defining file. The baseline sample does not break its 40 serving-impacted
heads down by trigger class, so the ~785 completed-build runner-minutes per day
is the **ceiling** on the saving, not a prediction of it; AC#7's post-change
30-run comparison must measure the realised figure. The graph-derived candidate router was retired under #7880 after it
showed no routing savings; it would have re-added managed-source PR builds.

Exact-input verification reuse remains available for the serving-image variants,
using content digests from the authoritative workflow itself; see
`serving-image-content-addressed-skip.md`. The GDAL worker's Trivy verdict depends
on the vulnerability database at scan time and is never reusable.

#### Post-change Serving trigger measurement (2026-08-28)

The AC#7 follow-up used the Actions **per-workflow** runs endpoint, not the
repository-wide endpoint: the latter stops exposing history after 1,000 runs
even when a wider `created` range was requested. Pages were fetched until empty,
workflow elapsed times were `updated_at - run_started_at`, and rates use the
**2.371-day observed span** from #3512 landing at `2026-08-26T11:11:42Z` through the newest
post-change GDAL run at `2026-08-28T20:05:51Z`. This is the same observed-span
normalisation as `honua-flow/tools/ci-baseline.py`; dividing by a nominal window
would understate a capped or incomplete sample.

The post-change cohort contains 60 GDAL Worker Image runs. Replaying the exact
pre-#3512 Serving path filter against each run head's commit file list identifies
48 counterfactual Serving-eligible heads and 12 that do not match. This is a
conservative replay: a single head commit can omit an eligible path changed by
an earlier commit in the same PR, so it can undercount but cannot invent an old
trigger. The 48 positively classified heads satisfy the revised 30-observation
contract in `evidence-driven-pipeline-baseline.md`. Only two Serving Image
Boundary runs occurred in the same span, so the directly supported result is at
least **46 avoided workflow invocations** (95.8% of the positive
counterfactual), not a runner-minute or billed-cost saving.

| Signal | Serving baseline (2026-08-13–17) | Serving after #3512 | Observed change | GDAL current baseline for #3553 |
|---|---:|---:|---:|---:|
| Observed span | 3.40 days | 2.371 days | — | 2.371 days |
| Runs | 100 | 2 | — | 60 |
| Runs/day | 29.4 | 0.8 | **-97.1%** | 25.3 |
| Successful runs | 19 | 2 | — | 50 |
| Cancelled runs | 80 | 0 | — | 9 |
| Successful workflow elapsed minutes | 2667 | 101.8 | — | 803.9 |
| Successful workflow elapsed minutes/day | 785 | 42.9 | — | 339.1 |

The elapsed-minute rows describe workflow critical-path time only. They are not
runner consumption or cost: after #3512 the three build variants run in parallel,
so summing job `completed_at - started_at` durations would produce a different
quantity. The historical 785 elapsed-minute/day figure therefore remains a
ceiling observation, not a realised saving. The two remaining runs were the
change's own image-defining workflow executions and completed in 49.1 and 52.8
minutes; no cancelled Serving run occurred in the post-change span.

The GDAL column is deliberately a baseline, not a claimed saving: #3553's
narrowing had not landed at the cutoff. One run had another conclusion, so the
60-run cohort is 50 successful, 9 cancelled, and 1 other. Its current 339.1
successful workflow elapsed minutes/day replaces the earlier 119-minute/day snapshot for
evaluating #3553; the difference reflects the much denser current PR activity,
which is why both the run count and exact observed span are recorded.

## Trusted observers and evidence ledgers (read-only)

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `normalize-derived-artifacts-consumer.yml` | Derived Artifact Normalization Consumer | `workflow_run` [Derived Artifact Normalization] | Default-branch validator for the untrusted producer's artifact. Trusted admission cancels only older producer generations for the same PR. Observe mode validates envelopes and reports drift without writing PR branches; the existing trunk regeneration producer publishes generated documents. |
| `shard-rank-drift.yml` | Shard Rank Drift | completed `workflow_run` [CI], `workflow_dispatch` | Trusted default-branch observer of "CI full" trunk runs: compares each server shard's `dispatch_rank` with its median passing job minutes over the latest three full runs and keeps one `area/ci` tracking issue (owner s-ci-shards) open for total drift outside 0.77-1.30, shards 1.5x and 3 min over their rank, or sampled shards still on a contract estimate (#9545, #9546, #9547). Report-only; skips when under 80% of shards are measured. |
| `stranded-merge-detector.yml` | Stranded Merge Detector | weekly `schedule`, `workflow_dispatch`, `workflow_call` | Read-only sweep for payload that never reached the default branch (#3248, #3316). Merged PRs whose merge commit is not an ancestor are adjudicated by **content** (patch identity, then blob equality, then presence of the PR's added lines) and split into `stranded` / `edits-missing` / `superseded` / `landed` / `indeterminate`; open PRs whose base has already landed or been deleted are reported as `needs-retarget` with the `gh pr edit` remedy. Files or updates a single tracking issue only on actionable findings. Reusable via `workflow_call` (`default-branch`, `limit`, `open-limit`, `tooling-ref`); **no external consumer yet** — honua-sdk-js runs its own `scripts/stranded-merge-detector.mjs`, so those inputs are exercised only from this repo. JSON output is `schemaVersion: 2`. |

## Nightly and scheduled test lanes

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `docs-quickstart-nightly.yml` | Docs Quickstart Validation | daily `schedule` (10:30 UTC), `workflow_dispatch` | Advisory, non-required execution of the primary documented Docker Compose quickstart. Validation failures fail the workflow as an early-warning signal, and bounded validation evidence is uploaded for 14 days. |
| `shipped-quickstart.yml` | Archived quickstart customer journey | daily `schedule` (09:30 UTC), `workflow_dispatch`, package-path `pull_request` | Installs the official candidate archive on an empty hosted Docker daemon; PRs test a prospective archive with their exact source, candidate image lock and official Console build. Checks both profiles, HTTPS Console/Studio authentication, secret-free configuration, documented publish/query, idempotent reinitialization, teardown and fresh second-install credentials. Receipts bind official run/archive identity and distinguish prospective packages from shipped candidates; missing static Console bundles fail. Prospective install is explicitly skipped on fork/Dependabot PRs or when Console artifact credentials are unavailable. Failed starts record the Compose version and per-service state, health and exit code; output stays withheld. |
| `docs-install-gate.yml` | Docs Public Install | `workflow_dispatch` | Replays the public installation guide using an anonymous image pull, published clients, publish/query assertions and restart readback, without compiling the server. PRs run this same proof selectively inside the required PR Gate. |
| `validated-examples-nightly.yml` | Validated examples (advisory) | daily `schedule` (10:37 UTC), `workflow_dispatch` (optional `candidate_image`, `python_sdk_version`) | Executes the STAC operations, mobile/offline, and local geoprocessing shipped examples against isolated candidate stacks: locally built from the checked-out source, or, when dispatched with a digest-pinned `candidate_image`, that exact published image with no build. The geoprocessing example installs the pinned `honua-sdk` in a virtual environment, as its quickstart does. Retains per-scenario logs and a JSON evidence record (source SHA, candidate image and repo digests, SDK version) for 14 days. |
| `gp-buffer-canary.yml` | GP Candidate Canary | six-hour `schedule` (`:17`), `workflow_dispatch`, path-filtered `pull_request` | Verifies live frozen-candidate digest, numerical buffer and 4,096-polygon dissolve oracles, and seven receipt-complete scheduled intervals; retains hashed evidence archives. PR runs execute harness regressions and an explicitly non-qualifying published-image rehearsal. |
| `gp-qualification-harness-checks.yml` | GP Qualification Harness Checks | path-filtered `pull_request`, `workflow_dispatch` | Checks Bash syntax and Python failure-isolation regressions. An explicit dispatch can build the matching canonical worker into a runner-local registry and rehearse one actual staged-store outage, natural worker heartbeat recovery, or terminal-CAS result-package recovery; its receipt is qualification:false and does not qualify a release. |
| `gp-resilience-qualification.yml` | GP Resilience Qualification | `workflow_dispatch` | Exact-digest poison, crash/output, lease, backlog, TTL, retry, quota/isolation, output-cap, and sustained-concurrency qualification with finding-preserving receipts. |
| `gp-release-evidence.yml` | GP Release Evidence | `workflow_dispatch` | Verifies matching executed lifecycle/resilience run heads and receipt identities, then publishes a content-hashed immutable release asset containing both summaries and every scenario receipt. |
| `mcp-certification-focused.yml` | Focused MCP Certification | path-filtered `pull_request` / `push`, `workflow_dispatch` | Automatic installed SDK and Inspector CLI addressability controls, plus public Inspector web UI cancellation controls with a real PostGIS observer and dropped-notification negative. Authored peers are non-qualifying controls. Manual diagnostic of both live SDK backend transports requires an exact SDK source SHA and strict receipts. That job builds the SDK on Node 22.23.3 (engine minimum 22.12) with a 4 GiB V8 heap, the same pin as full CI MCP certification. This is not full-CI image admission. |
| `mcp-inspector-pagination.yml` | Installed Inspector Pagination Controls | `pull_request`, `push` (matching paths) | Runs the public installed Inspector CLI against controlled peers to verify transparent pagination observation, compressed-response decoding and fail-closed controls. Source-bound controls are separate from candidate/version qualification. |
| `mcp-client-contract.yml` | MCP Client Contract | path-filtered ready `pull_request` / `push`, daily `schedule` (11:40 UTC) | Exercises every MCP view through pinned published SDK and Inspector clients against the server, checking both content channels and documented inputs. Draft conversion cancels admission; draft PRs skip the contract job. |
| `flaky-detection.yml` | Flaky Test Detection | daily `schedule` (05:00 UTC), `workflow_dispatch` | Bounded, **incremental** flake hunt (ADR-0037). Each run takes a rotating window of `.github/ci-shards.json` shards (default 6), re-runs each shard's own filter under its own inner budget via `scripts/ci/run-server-test-shard.sh` (default 2 iterations), and reports per-shard flake candidates through `scripts/ci/summarize-flaky-detection.py`. The whole shard set is covered every `ceil(shards / shard_count)` days. Test commands run in dedicated process groups; cancellation cleans up the group and snapshotted descendants, including those that leave the group, with a bounded half-second escalation. Group escalation reaches children forked after the snapshot. Signal handlers are installed before child creation and record cancellation for cleanup outside the active child wait, checked every 0.1 seconds; this also preserves cancellation during startup without reentering the subprocess wait lock. Reports only; it never gates. Also runs the validated issue-backed quarantine in a separate non-gating matrix, without retries or exclusions. |
| `flake-quarantine.yml` | Publish Flaky Test Quarantine | completed `workflow_run` [CI, PR Gate] | Trusted same-repository publisher: validates raw first/retry TRX and candidate hashes; requires unambiguous identities within selected failed methods (including passing siblings) and all retry rows, files one bot issue per project/method, and maintains at most 25 active issue-backed exclusions expiring within 14 days. Reads one shard artifact per shard: the highest `-attempt-<n>` (unsuffixed names count as attempt 0). A coalescing publisher sweeps the CI and PR Gate run histories and reads artifacts per completed trusted source run, avoiding the repository-wide artifact listing. An overlap watermark preserves old-run reruns and deferred batches; each sweep inspects at most 25 source runs (including empty ones), reserving five slots for the oldest pending runs and twenty for the newest pending runs so historical migration cannot starve current evidence, rechecks completion/attempt identity after artifact listing, and checkpoints completed run versions so equal-timestamp backlogs advance, with twice-hourly scheduled recovery. Never checks out triggering code or executes downloaded artifacts. |
| `nightly-slow-tier.yml` | Nightly Slow Tier (Emulator) | daily `schedule` (04:00 UTC), `workflow_dispatch` | `--filter "Tier=Slow&Category=Emulator"` across `Honua.Server.Tests`, `Honua.Db.Postgres.Tests`, `Honua.Core.Tests` — `[EmulatorTest]` only. LocalStack + Azurite come from `EmulatorFixture` (Testcontainers); Postgres from a service container. Asserts `HONUA_TEST_DB_URL` before dispatch. |
| `load-soak-nightly.yml` | Load/Soak Diagnostics | daily `schedule` (11:30 UTC), `workflow_dispatch` | Nightly load/soak diagnostics against trunk source with per-run credentials. Reports regressions between candidates; release qualification stays with `capacity-soak-candidate.yml` (#5314). |
| `capacity-soak-candidate.yml` | capacity-soak-candidate | `workflow_dispatch`, `workflow_call` | Runs at the manifest candidate source and image, collects complete raw observations, and attests a bounded evidence ZIP. The release checker independently recomputes all eight signals and binds the exact producer attestation. See docs/internal/operations/capacity-soak-runbook.md. |
| `capacity-evidence-contract.yml` | Capacity evidence contract | path-filtered `pull_request`, `workflow_dispatch` | Real HTTP outcome fixtures, analytical population assertions, and positive/negative ZIP checks against the pinned independent release verifier (#5314). No qualification or signing permissions. |
| `security-nightly.yml` | Security Nightly | daily `schedule` (02:00 UTC), `workflow_dispatch`, `pull_request` on package/project changes (NuGet job only) | Consolidated NuGet vulnerability scan (annotates High/Critical and opens a tracking issue, reports Moderate; advisories never fail the job, an unreadable report does), Trivy filesystem scan, container security scan (Hadolint, Trivy with the committed OpenVEX decisions, structure tests, runtime constraints), and a by-digest rescan of every supported release image in `eng/security/supported-images.json` that uploads SARIF and files fixable High/Critical findings privately. |
| `nightly-container-build.yml` | Nightly Container Build | daily `schedule` (06:00 UTC), `workflow_dispatch` | Scheduled container build. Publishes the generic AOT, Lambda AOT, and JIT images on trunk, boundary-verifying each AOT digest before publication. Lambda requires digest-bound registry SBOM and provenance. Branch dispatch builds immutable Lambda candidates with isolated base-image mirrors and moves no nightly aliases. Builds the newest certified trunk sha only in a run AT that sha: when trunk head is not it, the trunk run tags it `nightly-candidate-<sha7>` and re-dispatches itself there, so contents, labels, annotations and provenance name one sha (see `docs/guides/operate/nightly-images.md`). Lambda-facing platform tags in `deploy-platform-images.yml` retain runnable serving child manifests, with attested parent indexes under separate evidence tags. |
| `nightly-migration-evidence.yml` | Nightly Migration Evidence Pack | daily `schedule` (07:15 UTC), `workflow_dispatch` | Drives the fixture-based GeoServer migration apply path end-to-end (#1015) and uploads the deterministic evidence pack. |
| `protocol-harness-certification.yml` | Protocol Harness Certification | daily `schedule` (10:41 UTC), `workflow_dispatch` | Executes the exact governed server integration-test roster outside PR CI. Separately checks out the producer contract and candidate source, binds a SHA-labeled immutable image identity, rejects incomplete TRX, and emits digest-bound operation receipts with exact `test_ids` for nightly/release aggregation. |
| `provider-http-smoke.yml` | Provider HTTP-Stack Smoke | daily `schedule` (06:30 UTC), `workflow_dispatch` | Interface-level smoke that boots a real host per secondary provider (DuckDB in-process; MySQL and SQL Server via Testcontainers) over FeatureServer/OGC API Features/OData/tiles, plus the gated Oracle real-database lane (#2947). |
| `cloud-integration-harness.yml` | Cloud Integration Harness | daily `schedule` (05:00 UTC), `workflow_dispatch`, `pull_request` on migration, Postgres store, schema-guard and process-pool paths (Local Substrate job only) | Docker-backed cloud-integration tests (#2163) against emulated backends (kind, LocalStack). Nightly runs `Category=CloudIntegration` and `Category=LocalSubstrate`; a matching PR runs only the Local Substrate job (migration gate, schema guard and local process pool), informational and not required. |
| `real-aws-certification.yml` | Real AWS Certification | weekly `schedule` (Mon 06:00 UTC), `workflow_dispatch` | `Category=RealAwsCertification` against a LIVE AWS account. Gated on a maintainer OIDC role variable, budgeted, teardown-guaranteed. |
| `standing-aws-cert-plan.yml` | Standing AWS certification plan only | `workflow_dispatch` on `trunk`; PR offline controls | Protected `cert` OIDC job on an ephemeral GitHub-hosted runner. Checks out public IaC at exact current `trunk`, admits the protected request, candidate, reviewed modules and deployed bootstrap ZIP, and exports only age ciphertext plus a bound public-safe receipt. The workflow does not provision the existing backend, roles, protected inputs or local decryption custody; it does not apply a plan. |
| `lambda-preview-certification.yml` | Lambda Preview Certification | `workflow_dispatch` | Deploys an exact GHCR Lambda AOT image to the certified AWS account, invokes and verifies it, guarantees teardown, and emits a fingerprint-only receipt containing the true ECR artifact digest for the release manifest handoff. |
| `cross-server-consume-nightly.yml` | Cross-Server Consume Nightly | daily `schedule` (07:00 UTC), `workflow_dispatch` | Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers; uploads the TRX and gap report as workflow artifacts under the ignored artifacts/ output root. |
| `client-roster-certification.yml` | Client Roster Certification | `workflow_dispatch` | Runs the bounded external-client roster (`certification/bounded-roster/run-roster.sh`) on a GitHub runner against a candidate image digest and cut time; release-mode and contract verdicts decide the job, and receipts, verdicts and lane logs upload as a release-tier evidence artifact. Producer authority for the roster rows in `certification/client-protocol-requirements.v1.json`. |
| `client-interop-nightly.yml` | Real-Client Interop Matrix | daily `schedule` (07:00 UTC), `workflow_dispatch` | `docker/client-compat` matrix (`gdal`, `pyqgis`, `openlayers`, `cesium`, `geoservices-rest-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` and fails strict mode on any baseline regression. Promote to PR-blocking only after 30 consecutive nightly passes (#806). Protected-style probes resolve the seeded publication ID and independently verify that numeric-layer vector tiles reject anonymous and invalid-key requests. |
| `multidim-raster-fixture.yml` | Real Multidimensional Raster Fixture | daily `schedule` (08:15 UTC), `workflow_dispatch` | Composes the pinned LocalStack/S3 object store, real NetCDF seed, native GDAL worker, and server; verifies production metadata scan, derived Zarr objects, and distinct selected-time ImageServer PNG slices. Uploads the verifier log and failure-only composition logs as nightly evidence. |
| `client-compat-smoke-nightly.yml` | Generic Client Compatibility Smoke | daily `schedule` (07:15 UTC), `workflow_dispatch` | Full CERT-\* matrix (18 cases × 4 protocol lanes) with per-protocol `.cert.json` envelopes, `overall-summary.json`, transcripts, and `pack/`. |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | daily `schedule` (07:30 UTC), `workflow_dispatch` | PyQGIS desktop compatibility using real QGIS providers against `client-compat-v1.sql`. |
| `gdal-driver-e2e.yml` | GDAL Driver End-to-End | daily `schedule` (07:45 UTC), `workflow_dispatch` | `ogrinfo` + `ogr2ogr` against honua-server via GDAL's `OAPIF:` stand-in driver (ADR-0034). |
| `routing-nightly.yml` | Routing Nightly (pgRouting) | weekly `schedule` (Sun 05:00 UTC), `workflow_dispatch` | `Category=Routing` with `HONUA_ROUTING_TEST=1`; `PgRoutingFixture` manages its own `pgrouting/pgrouting` Testcontainers image. |
| `warehouse-nightly.yml` | Warehouse Providers Nightly (Creds-Gated) | weekly `schedule` (Sun 06:00 UTC), `workflow_dispatch` | Matrix over Snowflake/Redshift/Databricks/SqlServer test projects using optional repository secrets; a missing secret reads as "not configured", not "absent from CI". |
| `sdk-server-compatibility.yml` | SDK Server Compatibility | weekly `schedule` (Mon 08:35 UTC), `workflow_dispatch` | Manifest-driven last-3-servers × last-3-SDK-sets matrix from `docs/developer/sdk-compatibility-versions.json`; uses Node 22.23.3 (SDK engine minimum 22.12) with a 4 GiB V8 heap for source compilation; runs SDK sources from an isolated `$RUNNER_TEMP` copy and publishes `sdk-compatibility-matrix-<run-id>`. |
| `codeql.yml` | CodeQL | path-filtered `pull_request` (base `trunk`), weekly `schedule` (Mon 00:00 UTC), `workflow_dispatch` | Two lanes in one workflow, and the **only** CodeQL security analysis this repository has. On `pull_request` the paths are the single list in `.github/codeql/pr-paths.txt` (auth, admin endpoints, connections and secrets, import and object storage, SQL building, operation approval, the MCP and Studio AI surface, outbound HTTP proxies, sharing, sessions, exports, workflows). Trunk `push` still scans `src/**` and the build graph. Test-only PRs are left to the weekly scan, which builds and analyzes the test projects too. It uses C# `build-mode: none` extraction with the default high-precision suite to stay on the PR critical path; on the weekly schedule it performs a full instrumented build with `security-extended` to catch lower-confidence findings and dependency churn. The shared config excludes `cs/linq/missed-where` and `cs/linq/missed-select`; this does not configure the separate managed Code Quality commenter. See [review prevention analyzers](analyzer-baseline.md). |
| `geoservices-import-fidelity-external.yml` | GeoServices Import Fidelity (External) | `workflow_dispatch` | External parity against live Esri services; deliberately on-demand because upstream data drifts. Enforces the correctness regression gate and the perf-parity latency gate (#1249). |

## OGC CITE / conformance

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `ogc-rc-conformance.yml` | OGC release-candidate conformance | RC `workflow_call` | Runs all dispatchers after the RC image build of a promotable candidate (`candidate == 'true'`); aggregate blocks on failure |
| `cite-conformance-common.yml` | CITE Conformance (reusable) | `workflow_call` | Shared checkout/build/run/parse/upload/fail skeleton for single-suite CITE runs. |
| `cite-conformance.yml` | OGC CITE Conformance Tests (Features) | `workflow_dispatch`, RC `workflow_call` | |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-ogcapi-processes-conformance.yml` | OGC API Processes 1.0 Release Gate | `workflow_dispatch`, RC `workflow_call` | Required exact-candidate release gate over the pinned upstream ETS denominator. Release cuts supply an immutable image digest and its full source SHA; provenance mismatches, incomplete or invalid results, skips/CantTell verdicts, and pass or denominator regressions fail closed. |
| `cite-wfs20-conformance.yml` | WFS 2.0 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | Standalone (not the reusable wrapper) because of suite-specific setup. |
| `cite-wms-conformance.yml` | OGC WMS CITE Conformance | `workflow_dispatch`, RC `workflow_call` | WMS 1.3. |
| `cite-wms11-conformance.yml` | OGC WMS 1.1.1 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-wmts-conformance.yml` | OGC WMTS CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-wcs20-conformance.yml` | OGC WCS 2.0 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-wps20-conformance.yml` | WPS 2.0 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-kml22-conformance.yml` | OGC KML 2.2 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-gml32-conformance.yml` | OGC GML 3.2 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `cite-gpkg12-conformance.yml` | OGC GeoPackage 1.2 CITE Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `ogc-maps-conformance.yml` | OGC API Maps Conformance | `workflow_dispatch`, RC `workflow_call` | |
| `ogc-api-building-block-conformance.yml` | OGC API Building-Block Conformance | `workflow_dispatch`, RC `workflow_call` | Builds and verifies an exact source-SHA image (reads registry cache `buildcache:honua-jit-amd64`), then runs the vendored CQL2, MVT/TMS 2.0, Maps, and Schemathesis validators; uploads the complete evidence pack and fails on any validator failure. |
| `cng-candidate.yml` | Candidate CNG qualification | `workflow_dispatch`, `workflow_call` | Resolves one frozen honua-release manifest into the exact server digest, source SHA and cut time; calls the existing conformance lane and retains the candidate identity. |
| `cng-conformance.yml` | Cloud-Native-Geospatial Conformance | weekly `schedule` (Wed 06:00 UTC), `workflow_dispatch`, `workflow_call`, scoped `pull_request`/`push` self-tests | COG/GeoParquet/PMTiles-class CNG conformance; verifies the candidate image revision and builds offline helpers from the pinned candidate source. |
| `cite-classic-conformance.yml` | Classic OGC CITE Conformance | `workflow_dispatch`, RC `workflow_call` | On-demand combined WMS 1.3 + WFS 2.0 classic lane. |
| `cite-evidence-report.yml` | CITE Evidence Report | `workflow_dispatch`, RC `workflow_call` | Runs the public CITE suite set and builds `artifacts/cite-evidence/` (summary JSON, badge SVG, static index, full TeamEngine HTML) with optional Pages deployment. Also asserts `docs/cite-status.md` freshness and blocks RC qualification when the reviewed snapshot is >14 days stale (#2944); refresh the reviewed snapshot manually before cutting an RC. |
| `cite-certification.yml` | Exact-candidate CITE protocol certification | `workflow_dispatch` | Release-only producer that runs the full CITE inventory against a canonical immutable GHCR digest, verifies its OCI source revision, and emits post-cut normalized certification observations for the governed release denominator (#3382). |

External OGC conformance runs after the immutable image is built for a candidate `rc` run, through
`ogc-rc-conformance.yml`. Weekly schedules and the Building-Block PR trigger are removed.
These suites are absent from both the affected post-merge run and the existing full `ci.yml`
run; GeoServices REST checks retain their PR admission. The RC aggregate requires every
conformance caller to succeed, even when optional integration dispatch is disabled.
Common CITE suites consume the candidate image and source SHA; standalone WFS, Maps and
Building-Block suites retain their source-build checks at the release workflow commit.
Exact-candidate certification remains a separate manual operation requiring its governed
candidate context.

## Release and deploy

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `deploy.yml` | Build & Publish Images | `push` (`v*` tags), `workflow_dispatch` | Multi-arch (and AOT) images. After `publish-manifests`, `dispatch-geobench` sends a `repository_dispatch` to `honua-io/geobench` with the tag and image ref; skips with a notice when `GEOBENCH_DISPATCH_TOKEN` is absent (#1596). Every leg pushes its candidate by digest only; on `v*` tags `scripts/ci/trivy-release-gate.sh` scans that digest before any per-architecture tag is promoted, so a finding stops that leg's tags and all multi-arch manifests (`publish-manifests`), and the geobench dispatch; other legs (`fail-fast: false`) still promote their own scanned digests. The gate blocks only HIGH/CRITICAL findings with an upstream fix (unfixed ones are ignored by design, unlike the `release-bundle.yml` container scan) and honours `.trivyignore-release`; Trivy is pinned by digest. |
| `deploy-platform-images.yml` | Build & Publish Platform Images | `push` (`v*` tags), weekly `schedule`, `workflow_dispatch` | Platform image deployment. Every publishing leg pushes its candidate by digest only; on `v*` tag pushes `scripts/ci/trivy-release-gate.sh` scans that digest before any per-architecture tag is promoted, so a finding stops that leg's tags and all multi-arch manifests (`publish-manifests`); other legs (`fail-fast: false`) still promote their own scanned digests. `scan_only` dispatches stay advisory. The gate blocks only HIGH/CRITICAL findings with an upstream fix (unfixed ones are ignored by design, unlike the `release-bundle.yml` container scan) and honours `.trivyignore-release`; Trivy is pinned by digest. |
| `nuget-publish.yml` | Publish Honua.Core Package | `push` (`honua-core-v*`, `v*.*.*`), `workflow_dispatch` | Release-only publishing. |
| `publish-authorization.yml` | Publish authorization | `workflow_call` | Called first by every publishing workflow: fails unless the run is on `refs/heads/trunk` exactly or on a tag matching the caller's literal globs (no `/`), before any registry login. `scripts/ci/check-publish-authorization.py` (CI router validation) requires every publishing job to depend on it and to name an environment; exceptions and their reasons are in `scripts/ci/publish-authorization-exceptions.json`. |
| `release-bundle.yml` | Release Bundle (Compatibility Train) | `workflow_dispatch` | Foundation-first release-train orchestrator: one Native AOT RC image, integration/conformance and Esri-SDK evidence against that exact digest, every SDK cut against the same candidate (dry-run by default), then the release-train manifest plus the honua-devops validator. |
| `release-security-assets.yml` | Release Security Assets | `workflow_dispatch` (trunk only) | Per published release image: binds the digest, writes the release OpenVEX document, SPDX SBOM, Trivy verdict with VEX applied and the attestation-free platform manifest reference as one artifact, plus the `eng/security/supported-images.json` change that registers the digest (committed by the release owner). See `docs/internal/security/security-patch-releases.md`. |
| `rc-channel-tag.yml` | Release candidate channel tag | `workflow_dispatch` (trunk only) | Moves `ghcr.io/honua-io/honua-server:2026.1-rc` to the one honua-server image pinned by the label's release record: a minted honua-release `nightly-lock/<label>` whose `platform-lock.json` verifies with cosign as honua-release's trunk `nightly-certification.yml`, or else the `rc-release.yml` pre-release whose attached `platform-manifest.yaml` digest its notes also name (owner decision 17, honua-release#376). Moves no other channel. |
| `realtime-preview-qualification.yml` | Realtime Preview Qualification | `workflow_call`, `workflow_dispatch` | Verifies immutable server-candidate and SDK producer identities, then projects the SDK's live Preview receipt into a required exact-candidate qualification ledger retained for release review. |
| `gp-lifecycle-qualification.yml` | GP Lifecycle Qualification | `pull_request` / `push` (harness controls), `workflow_dispatch` (candidate matrix) | Runs installed-MCP adapter and lifecycle negative controls separately from the manual exact-digest server/GDAL-worker lifecycle matrix. The matrix retains all existing scenarios plus native-process cancellation through the installed MCP client. |
| `release-migration-performance.yml` | Release Migration Performance Evidence | `release`, daily `schedule`, `workflow_dispatch` | Runs the migration performance harness against the fixture-driven baseline (#1033) and uploads the website-linkable evidence artifact. |
| `geobench-release-trigger.yml` | Trigger Geobench Release Benchmarks | `release`, `workflow_dispatch` | `repository_dispatch` to the geobench repo on every published release. |
| `cloud-post-apply-validation.yml` | Cloud Post-Apply Validation | `workflow_call`, `workflow_dispatch` | Post-deploy validation. Pass `base_url` for a deployed environment, or `local_target: true` to boot a real Honua.Server + PostGIS on the runner. Ends in a counts tripwire so an all-skipped `Category=Cloud` run fails instead of reporting green (#4414). |
| `cloud-post-apply-validation-nightly.yml` | Cloud Post-Apply Validation (nightly) | daily `schedule` (03:00 UTC), `workflow_dispatch` | The caller for `cloud-post-apply-validation.yml` (#4414). Nightly it validates the post-apply contract — including the staged-import row count — against a local target; dispatch with `base_url` to validate a deployed environment. Free; no cloud account. |
| `notify-evidence.yml` | Notify honua-evidence | `push` (trunk) | `repository_dispatch` (`producer-updated`) to `honua-io/honua-evidence` when the capability-matrix/keys snapshots or CITE status change, so aggregation does not wait for its daily fallback. |

## Maintenance, reusable, and manual benchmarks

| Workflow file | Name | Triggers | Notes |
|---|---|---|---|
| `generated-files-on-trunk.yml` | Regenerate Generated Files on Trunk | `schedule` (nightly 10:30 UTC), `workflow_dispatch` | Regenerates the six repository-state projections, validates authored inputs against them, then — never writing trunk directly — commits any diff as `Mike McDougall <mike@honua.io>` on a fixed automation branch and opens or refreshes a PR from it into trunk, landing through the ordinary PR Gate + Review Gate + per-PR lander path. No diff means no commit or branch push; a current validated source closes an obsolete open proposal after source and head checks, retaining its branch. This adds no jobs or runners. See [inventory and dry run](generated-files-on-trunk.md). |
| `trunk-sanity.yml` | Trunk Sanity | `push` (trunk), `workflow_dispatch` | Post-merge restore/build plus the reduced protocol profiles (gsr, ogc-api, ogc-api-classic) whenever anything other than workflows or docs prose changed since the last successful run. Each job queues the newest push instead of cancelling. Affected tests after a merge run in the selective CI that `trunk-affected.yml` dispatches. |
| `disabled-workflow-check.yml` | Disabled workflow check | weekly `schedule` (Monday 09:17 UTC), `workflow_dispatch` | `scripts/ci/check-disabled-workflows.py` fails when a workflow disabled on GitHub is still tracked. A disabled reusable workflow passes while an active workflow calls it; otherwise only an unexpired entry naming its deciding ticket keeps it, and an entry that no longer matches a disabled file fails. |
| `label-sync.yml` | Capability Label Sync | `push` (trunk), `workflow_dispatch` | Creates/updates `cap/<category>` labels from the canonical category list; never deletes or renames (#2896). |
| `server-test-reuse-benchmark.yml` | Server Test Reuse Benchmark | `workflow_dispatch` | Bounded A/B of an overlapped reuse producer against independent baselines. Manual, read-only, publishes no status. |

## Recently retired

| Workflow file | Retired | Why |
|---|---|---|
| `review-first-evidence-ledger.yml` | 2026-11-16 | #7880: observe experiment not promoted; latest daily audit used one runner for 156 seconds (2.60 runner-minutes), with only 5/20 current-policy heads. Exact-head Review Gate remains required. |
| `native-image-impact-observe.yml` | 2026-11-16 (#7880) | Disabled observation-only router with no measured narrowing benefit. Last run 42570078484 was 9 Nov; zero runs since 13 Nov before retirement (0 runner-minutes saved from an already disabled workflow). Report generation, observer fixtures and its promotion policy were removed; image-input validation and content digests remain in the authoritative serving-image workflow. No active required gate moved or removed. |
| `server-test-prebuild-observe.yml` | 2026-11-16 (#7880) | The prebuild shadow experiment, with the parity, ledger and benchmark rows below. The observer had been disabled on GitHub since 9 Nov, so parity found no ready prebuild to compare (0 of 60 runs since 10 Nov reached a comparison), the ledger had nothing to count, and the manual benchmark last ran 14 Aug. The required `PR Gate / Server test producer` does not use any of it. Its planners, receipt, audit, try/validate scripts, policies and `server-test-prebuild-shadow.md` went with it. |
| `server-test-prebuild-parity.yml` | 2026-11-16 (#7880) | Parity leg of the prebuild shadow experiment; see `server-test-prebuild-observe.yml`. |
| `server-test-prebuild-evidence-ledger.yml` | 2026-11-16 (#7880) | Ledger of the prebuild shadow experiment; see `server-test-prebuild-observe.yml`. |
| `server-test-prebuild-benchmark.yml` | 2026-11-16 (#7880) | Manual A/B benchmark of the prebuild shadow experiment; see `server-test-prebuild-observe.yml`. |
| `pr-gate-impact-observe.yml` | 2026-11-16 (#7880) | Disabled on GitHub since 9 Nov; observe-only docs-only routing classification. The docs-only fast path it measured is enforced through `scripts/ci/docs-only-diff.sh`. Its classifier (`classify-pr-gate-impact.py`) and validator (`validate-pr-gate-impact.sh`) went with it. |
| `impact-routing-evidence-ledger.yml` | 2026-11-16 (#7880) | Counted observer receipts toward a seven-day promotion bar that disabled observers could no longer reach. `audit-impact-routing-evidence.py`, the promotion and tombstone policies and `impact-routing-evidence.md` went with it. The run-catalog helpers the affected-shard audit uses stay in `collect-impact-routing-runs.py`, without its receipt download path. |
| `claude-review.yml` | 2026-11-15 (simplify A4) | Disabled on GitHub since the 11-11 review-path ruling; independent review now comes from the lanes and the Codex connector. Its helpers (`claude-review-body.js`, `claude-review-dedupe.js`, `review-catchup.js`) went with it. `review-gate-evidence.js` still grades a Claude bot review if one is posted. |
| `merge-train.yml` | 2026-11-15 (simplify A4) | Disabled on GitHub; GitHub ruleset auto-merge is the only landing path. `scripts/ci/merge-train/` and its fixtures went with it, and `scripts/ci/validate-single-merge-authority.sh` now rejects any repository merge authority, this workflow included. ADR-0055 is superseded. |
| `merge-train-rerun-recovery.yml` | 2026-11-15 (simplify A4) | Disabled companion of `merge-train.yml`; resumed a train batch after a green CI rerun. |
| `code-quality-supersession.yml` | 2026-11-15 | GitHub-managed Code Quality is switched off for this repository, so there is no managed run to cancel. `cancel-superseded-code-quality.js`, its test and `validate-code-quality-supersession.py` went with it. |
| `pr-merge-train.yml` | 2026-07-21 (`d2afeb9d5`) | Repository-workflow second merge authority. `scripts/ci/validate-single-merge-authority.sh` now rejects every repository merge authority; routine landings now use the external fleet lander described above. |
| `auto-rerun-flaky.yml` | 2026-08-16 | Job guarded on `workflow_run.event == 'pull_request'`, which `ci.yml` has not emitted since #2865 removed its `pull_request` trigger. Every run for months was `skipped`. |
| `ci-failure-triage.yml` | 2026-08-16 | Same dead guard. Its deterministic classifier (`scripts/ci/ci-failure-classifier.js`) and Bedrock helper (`scripts/ci/bedrock-triage.js`) had no other consumer and were removed with it. |
| `server-test-shard-cache-proof.yml` | 2026-08-16 | One-off hosted proof for the #2735 shard-local cache, triggered only by pushes to a merged experiment branch. The shipped cache lives in `ci.yml` and is still guarded by `scripts/ci/validate-server-test-shard-cache.sh`; the recorded proof stays in `server-test-binary-artifacts.md`. |
| `server-test-transfer-benchmark.yml` | 2026-08-16 | One-off #2722 benchmark whose design ADR-0074 rejected, triggered only by pushes to `ci/2722-hosted-transfer-benchmark`. Its config and evaluator went with it; the measured result stays in `server-test-transfer-benchmark.md`. Later producer designs are measured by `server-test-reuse-benchmark.yml`. |

## Historical change log

The sections below record why earlier workflow changes were made. They are
history, not a description of the current tree — the tables above are.

### Changes Made in This Audit (Ticket #485)

#### Conformance workflows moved off PR path

The following workflows had `pull_request` and `push` triggers removed, leaving only `schedule` and `workflow_dispatch`:

- `cite-tiles-conformance.yml` (was PR + push + schedule)
- `cite-wfs20-conformance.yml` (was PR + schedule)
- `cite-wms-conformance.yml` (was PR + push + schedule)
- `cite-wmts-conformance.yml` (was PR + push + schedule)
- `ogc-maps-conformance.yml` (was PR + push + schedule)

Additionally, `cite-conformance.yml` (already schedule-only) had dead PR comment steps and a stale `pull-requests: write` permission removed to match.

**Rationale**: External OGC conformance runs for every RC and on demand via
`workflow_dispatch`. OGC integration tests in scheduled full CI remain non-gating.

#### CodeQL moved off PR path

`codeql.yml` no longer triggers on `pull_request` or merge-to-trunk push. It runs on a weekly schedule. This avoids adding a slow, non-deterministic security scan to routine PR or merge cycles. **(Superseded: a path-filtered `pull_request` lane using `build-mode: none` was later reinstated and is now the repository's only PR-time CodeQL security analysis — see the table above.)**

#### PR template and validation redesigned

The PR template now includes explicit sections for gate impact, docs/contract impact, release/deploy impact, and breaking changes. The `pr-template-check` job at the top of `ci.yml` validates these sections directly and replaced the previous standalone `pr-validation.yml` workflow. (It no longer gates the downstream graph — `pr-readiness` was decoupled from it — and since `ci.yml` lost its `pull_request` trigger both jobs short-circuit to success on every event that still reaches them.)

#### Issue templates redesigned

All issue forms now require acceptance criteria, affected repos, gate-tier impact, and release/deploy impact. This ensures grooming inputs match the workflow contract.

#### Reusable SDK PR gate removed

`reusable-sdk-pr-gate.yml` was deleted on 18 November 2026: none of `honua-sdk-js`, `honua-sdk-dotnet` or `honua-sdk-python` called it, and each SDK repository runs its own pull request gate.

#### Composite actions extracted

Five composite actions were added to `.github/actions/` for shared CI setup and evidence handling:

- `setup-dotnet-ci` — .NET SDK, NuGet cache, opt-in host NuGet fallback folder *(active)*
- `setup-node-ci` — Node.js setup, npm cache *(future: SDK workflows)*
- `setup-python-ci` — Python setup, pip cache *(future: conformance/script workflows)*
- `upload-ci-evidence` — artifact upload with standard naming and tier-based retention *(active)*
- `run-conformance-stack` — Docker bootstrap/teardown for CITE workflows *(future: conformance workflows)*

### Changes Made in Workflow Refactor (2026-04-25)

#### Security workflows consolidated

`container-security.yml` and `trivy-nightly.yml` were folded into `security-nightly.yml`. The consolidated nightly now owns NuGet vulnerability scanning, Trivy filesystem scanning, and container security validation in one security lane.

**Rationale**: The previous split created three scheduled security workflows with overlapping vulnerability-scan responsibilities and separate artifact conventions. One workflow keeps the security lane easier to monitor while preserving separate jobs for dependency, filesystem, and container concerns.

#### CITE wrappers normalized

`cite-conformance.yml` and `cite-tiles-conformance.yml` now call `cite-conformance-common.yml`, matching the single-suite CITE wrappers for GML, GeoPackage, KML, WMS, and WMTS.

**Rationale**: Features and Tiles used the same checkout/build/run/parse/upload/fail skeleton as the reusable CITE workflow. Keeping only suite-specific inputs in the dispatcher files reduces drift in cache scopes, artifact upload behavior, and failure handling.

### Grouped unit and component shards

The server matrix combines 33 small unit/component assemblies into four jobs:
Provider and AI Units, Runtime Units, Protocol Units, and Protocol and Host
Components. Each `csprojs` list is an explicit assembly union. Routing, build
selection, resource classification, and the source coverage guard inspect every
member. Catalogued Docker and Postgres dependencies remain supported;
resourceful members retain service provisioning. Unknown resources fail closed.
The existing required check names and tier policies remain unchanged.

The shared runner executes each member with its own fresh TRX and timing record,
within one shared 20-minute test budget. Every assembly must execute tests and
pass; a passing sibling cannot conceal an empty, failed, or timed-out assembly.
It also emits a combined TRX and timing receipt for the existing aggregates.
The optional cache observer uses normal build fallback for grouped jobs.

The scheduled baseline run 42685503356 spent 75.38 runner-minutes across these
33 jobs. Reusing one setup per group estimates 22.80 runner-minutes; this is a
planning estimate, not an observed result. Post-merge elapsed minutes must come
from a full trunk run before claiming savings or refreshing child dispatch ranks.

Full CI transfers the selected shard matrix as a run-and-attempt-scoped JSON
artifact to the managed producer, cache planners and receipt verifier. Full
filters stay out of environment variables and command arguments, whose per-string
limits can prevent the shell from starting. Missing matrix artifacts fail the
selected lane. PR receipt verification receives only selected shard names; its
execution matrix and enforced test filters remain unchanged.

### Pre-PR and trailing affected execution

`pre-pr-check.sh --affected-tests` restores once, builds the affected unit-test
closure without analyzers, then runs unit tests by default. A markdown file under
`scripts/ci/` does not by itself select every isolated unit; executable files there,
anything under `.github/`, and the shared build files still do. Pass `--integration`
with `--affected-tests` to include affected integration shards. The independent
PR Format check retains analyzer coverage. Positively classified docs/CI-only
changes use the existing governance checks without starting a .NET build;
unknown or invalid routing still fails closed. A full-build fallback retains
these Stage 0 CPU limits and disables build analyzers; its validation scope is
unchanged. Stage 0 uses
`HONUA_STAGE0_MAX_CPU_COUNT` (default 5, range 1–16) and reports restore, build,
and test durations separately. After the one build, the no-build test commands
(affected unit projects and every selected shard, including integration shards
under `--integration`) run concurrently, `HONUA_STAGE0_TEST_PARALLELISM` at a
time (default 3, at most the Stage 0 CPU count, checked before the build). Every
command keeps its own log and TRX check. A failure or interrupt starts no queued
command, and the run fails once the running ones finish. Host queue/sync timings are reported by the
local lane runner; they are separate from execution time.

Trailing `selective_base` dispatches use their verified base for the managed
producer's affected project graph. An effective full-CI decision (including a
router promotion or manual override) builds the whole solution. Consumers reuse the single producer's
artifacts. Server shards retain descending measured `dispatch_rank` order and
run at most two at once for every `selective_base` dispatch. A selective
dispatch whose router reason is `no_path_match` does not schedule those default
smoke shards. Honua.Core package compatibility runs on that dispatch only when a
changed file is a Core package input: `src/Honua.Core/`, `src/Honua.Analyzers/`, a file directly under `src/`, a root-level file, `eng/`, `scripts/ci/analyzer-baseline/` or `.github/actions/setup-dotnet-ci/`. Repository metadata (`.github/codeql/`, issue templates, CODEOWNERS,
Dependabot, actionlint, hygiene and PR template files) also starts no managed
build or integration lane on any `ci.yml` run. An empty path list still runs it. A skipped Core
package does not cancel a selected AOT or MCP lane; a failed Core package or
Postgres compatibility job still does. Optional affected
runs above 40 total jobs defer to scheduled full CI; manual full-coverage overrides
retain every shard. Scheduled and non-selective full runs run at most 16 concurrent shards. Expensive AOT, Docker,
Postgres compatibility, browser, Python, MCP and quickstart lanes use
`scripts/ci/affected-ci-lanes.py` to select their own inputs. Native, container
and MCP checks conservatively include every runtime source or project change,
including transitive publish inputs and REST/gRPC backends. Shared seed files
select every compatibility lane. `skills/` (embedded in `src/Honua.Ai`) counts
as a `src/` path and `certification/` selects no lane. Shared inputs,
unknown paths, missing diff evidence, and the canonical router's `run_all`
fallback retain full coverage. AOT/MCP selection includes Postgres compatibility
as a prerequisite. PR routing, required check names, shard definitions, and the
twice-daily full matrix are preserved.

### Shard filter evaluation

The shard coverage guard and affected-shard selector reuse parsed filter syntax in a bounded, process-local cache keyed by the complete filter text. Method-level evaluation, malformed-filter rejection, ownership checks, and mutation fixtures remain in place. Workflow triggers, jobs, runner counts, and required-check dependencies are unchanged.

### Spec endpoint application lifetime

`SpecEndpointsTests` retains one application for validation, planning, and
rejected requests, with a fresh client per case. Successful applies, cache tests,
and license variants own their hosts and state. The class fixture disposes the
shared application after its last case. All cases and assertions run in their
existing PR and trunk shard selections; runner counts, required check names,
and aggregate dependencies are unchanged.

### Authorization shard coverage

The authorization matrix selects the live `AdminAuthorizationTests` class.
The remaining authorization shard owns every other method in the original parent
filter. Retired cache endpoint selectors are removed; the coverage guard still
rejects dead positive clauses, and the partition fixture verifies complete,
disjoint method ownership with unchanged source triggers and budgets.

### Layer publishing shared application fixture

The `Server Features Admin Layer Publishing` shard selects 48 named
`LayerPublishingIntegrationTests` methods through an exact-name filter, with a
27-minute test timeout. The class's remaining 19 methods run in
`Server Features Admin Runtime Operations` through its complementary filter.
Each shard process builds its own instance of the shared class fixture.

### WPS endpoint fixture lifetime

The WFS 1–9 shard family includes `Wps20EndpointsTests`. See
[WPS endpoint fixture lifetime](gate-model.md#wps-endpoint-fixture-lifetime)
for its class-scoped applications and isolated custom-host cases.

### ImageServer endpoint fixtures

See [ImageServer endpoint database lifetime](gate-model.md#imageserver-endpoint-database-lifetime)
for endpoint isolation and database reuse. The pure WMTS capability unit test runs
without a database fixture in the ImageServer Rendering and Support shard.

### Geocoding endpoint fixtures

The Geocoding shard retains every endpoint response and provider request-contract
case. Eleven default-provider capture cases reuse the existing class application,
with fresh clients and all four request captures reset before each case. Custom
configurations retain private hosts and directly owned factories. See
[Geocoding response fixture lifetime](gate-model.md#geocoding-response-fixture-lifetime)
for application ownership and isolation. All assertions, cases, PR/trunk routing,
required check names and aggregate dependencies remain unchanged.


Four complete 96-case owning-shard runs passed identical outcomes with zero skips.
Class median 32.809937 to 22.685127 seconds; fixture-inclusive shard median
59.338310 to 47.198992 seconds saves 12.139318 seconds (0.202322 runner-minutes),
with one runner unchanged. Both pairs improved. Compilation is excluded; the
linked gate model records individual runs and measurement limits.

### Governance capacity split

The former `Server Features Admin Governance and Sharing` shard timed out at
22.05 minutes in affected run 42846296623. Three numbered children partition
its original filter exactly, including method-name matches outside governance
classes. A fallback child retains future matches. The original paths, test and
job budgets, reporting, and CPU limits remain unchanged. The partition fixture
checks method-level union, disjointness, nonempty children, and inherited limits.

Successful baseline run 42685503356 took 15.28 minutes. Scaling its class timing
spans to the recent timeout estimates child durations of 7.89, 7.22, and 7.47
minutes, including setup allowance. These initialized the dispatch ranks.

Measured ranks (23 Nov 2026): each recorded `dispatch_rank` is the exact median
passing job duration, rounded half-to-even to 0.1 minute, over the five "CI full"
trunk runs whose head contains #9118 (shard disk-cleanup skip) and #9168 (test
fixture speed-ups), counting a sample only when its filter and project selection
match the current definition (gate-model.md, "Measured ranks"). Those two changes
halved server job time (826–878 runner-minutes per full run before, 430–451
after), so older samples are not reused. 201 of 202 shards have 3 to 5 matching
samples; the packed GPServer SOAP job keeps its 5.0 estimate until measured. The
scheduling rank sum is 436.8 runner-minutes, previously 827.1 (225 → 202 shards
since the 21 Nov record via re-packing); the six largest ranks sum to 23.9, inside
the 45 runner-minute PR time budget. These are scheduling estimates, not runtime
savings.
Temporal's explicit source override uses `prioritize: true` as a tie-breaker
after changed-test and path-hit counts; candidate admission and caps still apply.
Override priorities must be boolean; malformed values fail schema validation.

Normalization consumers preserve a producer’s recorded superseded admission across same-SHA draft-to-ready transitions, using exact-attempt Actions job outcomes. If both producer jobs were skipped by job-level admission (draft or non-base edit), the consumer also returns without validation or mutation. Missing envelopes from ordinary producers remain errors.

### Router validator execution

`validate-ci-router.sh` runs independent fixtures in a bounded pool (four workers by default; `HONUA_ROUTER_WORKERS=1..8`), records each fixture duration and collects every exit code before returning failure. A failed or missing result cannot produce success. The routing assertions retain their exact cases and shard ownership checks. Cancellation sends TERM to owned worker groups, escalates to KILL after a bounded grace period, and reaps workers before deleting results.

`--changed-only` is a local feedback mode: stdin routing cases skip only when their complete input closure (router, shard map, jq wrapper and validator infrastructure) is unchanged against the merge base with `origin/trunk`. Staged, unstaged, renamed and untracked inputs count. Unknown base or an ambiguous path runs everything; undeclared fixture input closures also run. CI and GitHub Actions ignore the optimization and always run every fixture.

The PR managed producer compiles `Honua.Server.Tests` in a parallel, review-admitted job. The main producer excludes that project and the full-coverage architecture collector, whose imported references pull Server.Tests into its graph. The independent architecture path and analyzer jobs retain collector compilation and verification. The shared import-aware project parser rejects any other reverse dependency on Server.Tests. The parallel producer publishes the Server.Tests output after every non-docs build, and publishes other managed projects only when a selected shard consumes them. The `fast-server-tests` job restores Server.Tests for `server-fast` and `server-governance` and compiles that project only when the restore is empty. The final required context directly requires the parallel producer on both pull-request and manual events; the PR-only affected-shards aggregate also checks it for an empty shard matrix. Honua.Server.Tests shards start when that producer and the selector finish, and read heavy-runner readiness from runner select. Every other selected project stays behind Build and tests, which publishes its managed outputs. Only the affected-shard matrix cancels sibling shards after a failure; fast-test and analyzer matrices retain every result.

Review revalidation shares `.github/scripts/revalidate-exact-head-review.cjs` across producer, fast-test and aggregate jobs. The aggregate checks out code only for enforced attempt-2 review validation. Generated-output classification shares `scripts/ci/classify-generated-output.sh`; executable skip policy still comes from the immutable PR base, and uncertainty preserves all checks.

Normalization consumers preserve a producer’s recorded superseded admission across same-SHA draft-to-ready transitions, using exact-attempt Actions job outcomes. Missing envelopes from ordinary producers remain errors.

### Standalone tooling in affected producer builds

The affected producer keeps solution members in its managed solution filter. The
Lambda deployment driver and MCP manifest generator are explicit standalone
projects: when selected, each restores and builds by its own project path before
managed outputs are published. Restore and build failures remain gating, with
the same warnings, analyzers and graph-build settings as the solution build.
Unknown projects outside the solution still fail planning; test selection is unchanged.

### Stage 0 evidence and cross-phase reuse

The local build-host transport evicts idle checkout outputs before queued
outputs under disk pressure. The existing free-space thresholds and active-run
protections remain; queued outputs may still be evicted as a fallback.

[Stage 0 reuse assessment](stage-zero-reuse.md) records live queue/build timings,
the five-run cache observation, completed affected-run job counts and queue
timings, and the protected issuer requirements for any future gate skip. The
completed samples have different selected work; green runs that skip managed
tests do not establish a test speedup or resolve the comparable-run evidence gap. Local receipts do not currently skip hosted tests. Compiled
output caching remains opt-in; no cross-phase promotion is authorized by the
observational sample.

Each affected shard selects the SDK and workflow-attempt identity exported by its own managed producer. Managed consumers also install the target framework’s .NET and ASP.NET shared runtimes into that private SDK root using the supported runtime bootstrap. The target channel comes from `HonuaTargetFramework`; non-versioned host files are preserved and exact producer SDK identity is verified afterward. This allows a newer producer SDK to execute older target artifacts without major runtime roll-forward or artifact changes. It adds no jobs and changes no required checks. The 12 November baseline aborted net10.0 tests and projection regeneration before execution when only the SDK 11 runtimes were present; recovery timing awaits a fresh eligible gate. A shard spanning both producers rebuilds its projects locally instead of combining independently resolved SDK outputs. Fast-test selection reads imported build documents through the shared project parser; unresolved imports or linked inputs retain all four groups.

### Test analyzer compilation roots

The ready-PR format matrix retains `PR Gate / Analyzers (tests)` and adds `PR Gate / Analyzers (server tests)`. The latter compiles Honua.Server.Tests; the former restores the remaining selected solution roots, then builds their evaluated test roots and conservative multi-targeted roots. Omitted single-targeted source roots remain covered by the source analyzer job. The server root is also retained when an affected root imports or transitively references it. Forward dependencies still compile. The `server-tests` and `other-tests` diagnostic partitions assign Server.Tests exclusively to the server job and all other test diagnostics to the other job, including when the collector imports Server.Tests. Warnings remain errors. A partition with no roots skips restore and build explicitly. The required format matrix waits for both jobs; drafts retain Format only. This separates the measured 14.9-minute test-analyzer path; the new timing must be measured on a hosted gate run.

Optional PR workflow filters and retained post-merge coverage are documented in
[optional PR input boundaries](gate-model.md#optional-pr-workflow-input-boundaries).

## Trailing affected CI selection

Trailing affected CI refreshes its completed-ancestor base when the selection job starts.
Dispatch and execution both require a conservative total bound of at most 40 jobs:
33 side jobs plus the complete selected server-shard set. Full routing, unavailable
selection/history or a missing baseline defer the whole range to
the next twice-daily full run. No range is narrowed to a newer merge. Execution
cancels before publishing change outputs when admission fails, so deferred work
cannot become a successful CI baseline. Admitted affected shards run two at once;
scheduled and non-selective full runs retain their 16-shard concurrency.

Two concurrent trusted shards use at most two `honua-heavy` runners, so an
affected run runs beside PR Gate instead
of waiting for an idle PR queue. The former PR Gate priority
workflow cancelled affected runs whenever a PR Gate started and admission refused
while one was active; with up to ten open PRs that admitted 0 of 32 dispatches
from 05:52 to 17:02 UTC on 14 Nov 2026. Required PR gates and shard definitions are
unchanged. The reported baseline incident (about 170 jobs, two hours queued,
seven of eight heavy runners) predates the 40-job bound and the two-shard width.

Trusted server-test matrices use the configured `HONUA_HEAVY_RUNNER` pool for
all indices. Mixing hosted and heavy runners under one `max-parallel` limit
allowed hosted jobs waiting for capacity to consume admission slots while heavy
runners sat idle. At 14:17 UTC on 15 Nov, full run 43709972683 had 13 admitted
hosted jobs waiting plus three executing shards, filling its 16 slots; another
58 heavy shards had not been admitted. Fork guards, hosted fallback, shard
coverage and the full/affected concurrency bounds remain unchanged. Live
post-deployment runner idle times remain to be measured.

Full runs (scheduled, or dispatched with `full_ci` or without `selective_base`,
the `run-name` rule) put every server-test index on the label set by
`HONUA_CI_HEAVY_RUNNER`, defaulting to standard `ubuntu-latest` runners.
Affected runs keep the heavy pool. Paired full runs on 18 Nov 2026 took a
78-minute server phase on
hosted runners (run 44271438677, all 225 shards, no larger runner size) against
193-327 minutes on the six `honua-heavy` runners. The matrix stays on one pool.

### Covered trunk runs

A full trunk run covers every merge up to its head. Its first job,
`supersede-trailing-runs` (`scripts/ci/supersede_trailing_runs.py`), cancels
queued or running `CI affected ...` runs at that head or an earlier one; a
selective run cancels itself when a started scheduled run has a head that
contains its own. Dispatched runs never cancel a selective run, because the
runs API does not expose the inputs that decide their scope.
Ancestry comes from the compare API. A full run held by the concurrency group
never cancels anything, and unreadable run history cancels nothing. On
11 Nov a 164-job affected run and a manual full run tested the same head
together, leaving 130 affected and 37 full jobs queued while every open PR
Gate had six to nine jobs waiting for hosted runners.

## Automatic merge admission

`auto-merge-ready.yml` reconciles same-repository trunk PRs using the repository-scoped
bot App token. Live drafts, forks, non-trunk bases and `hold`, `train:hold` or
`train:escalated` labels are ineligible. New requests use squash for one commit and merge for bundles. Existing eligible
requests retain their selected method across commit-count and mergeability changes.
Synchronization and label changes revoke ineligible requests. Required checks and repository rulesets decide when a PR
merges; review admission applies only where configured as a required check. The
helper checks out no contributor code and queues without cancellation. It runs on
`pull_request_target` in the `bot-app` environment: the job is always the trunk
version of the file, and the environment's trunk-only rule guards the bot App key
once the key moves there from the repository secrets.
Conflicted PRs are not armed; they are armed after the branch is updated.
The release App key (`HONUA_RELEASE_APP_ID`, `HONUA_RELEASE_APP_PRIVATE_KEY`) is held the
same way: only the trunk run of the nightly `select-candidate` job and the normalization
consumer's `validate` job enter `bot-app`.

Admission requires both the triggering event and the live PR to be non-draft and
free of `hold`, `train:hold` and `train:escalated`. A queued held/draft event cannot
re-arm admission after its live state changes. Ineligible events may only revoke
an existing request; draft-conversion and label events retain that cleanup path.
Manually disabling auto-merge alone is not a durable hold: apply a hold label or
keep the PR as a draft to prevent a later eligible event from enabling it again.

The event-driven auto-merge workflow skips Dependabot PRs before minting App credentials. Already mergeable PRs remain with the existing merge authority because GitHub cannot arm deferred auto-merge for them.

Retargeting an admitted PR away from `trunk` revokes its auto-merge request on
`edited`. Dependabot PRs skip this workflow entirely, including hold-label
revocation: a human or drain lane owns any manually armed dependency PR.
If mergeability becomes clean between the live query and enable mutation, only
GitHub's specific clean-status response ends neutrally; other API errors fail.
A queued run that starts after the PR merged or closed ends neutrally without
touching it: GitHub keeps `autoMergeRequest` on a merged PR but refuses to
disable it, which used to leave a red check on the merged head.

When the bot armed SQUASH on a single-commit PR that has since grown into a
multi-commit bundle, the next run switches it to MERGE (only while the PR is
still blocked); a method a lane chose itself is kept. Label events start a job
only for the hold labels, and `edited` only when the base branch changed;
other label and body edits are skipped without a runner.

Trusted PR-run consumers retain `source-run-draft-admission` classification for intentional draft-only runs; ready-run failures remain observable.

## Admin authentication capacity split (12 November 2026)

The former authentication/credentials shard now has four jobs: authentication
and operator bearer; OAuth and OIDC; API keys; client certificates. Each retains
the original routing paths, project and 22/32-minute budgets. Required checks
are unchanged. Children inherit the parent's conservative 21.7-minute dispatch
rank, including existing PR budget deferral, until scheduled trunk CI measures
them. The coverage guard proves exact ownership of the former selection.
See [the capacity evidence and qualification requirement](shard-timeout-budgets.md#admin-authentication-capacity-split-12-november-2026).

PR Gate heavy shard routing also requires current-instance, fresh version-1 heavy
pool availability, independently of the two-producer idle threshold. Only entries
the selector marked `heavy` take the pool (the longest shard of each producer lane
stays hosted); required dependencies remain. Missing or invalid additive
heartbeat proof selects hosted runners; deployment is MAIN-owned.

### Managed producer memory budget

The shared lean gate bounds restore and build to four MSBuild workers and sets
`DOTNET_PROCESSOR_COUNT=4` for each child runtime. These steps disable the
persistent MSBuild server and node reuse so restore workers cannot accumulate
alongside the graph build. Both full and filtered builds retain their complete
project graphs, warnings-as-errors and existing timeouts.

This corrects the 12 November PR #7457 producer cancellation: its runner shared
a 10 GiB user-slice budget with runner services; the OOM task dump contained
25 MSBuild processes and 16 compiler processes before systemd restarted the
runner. CPU quota alone did not bound those processes. No host resource limit
changes are required. The changed action takes effect on the next PR Gate;
that run supplies the after-change timing and memory evidence.

### Full-CI producer routing

Only `Build & Format Check` consumes `CI producer / Runner select`, a call of the
shared advisory selector. Trunk schedules and dispatches take an idle
`honua-heavy` runner while more than two heavy runners stay idle; missing,
stale or busy proof and setup failure select `ubuntu-latest`, without wake,
wait, polling or retries. Light jobs stay hosted. The producer uses a private writable SDK directory, skips sudo
cleanup off hosted runners and bounds all nested build workers to four.
Selective dispatches build that graph with `RunAnalyzers=false`; scheduled
and `full_ci` runs keep analyzers. Protocol-profile builds analyze Honua.Server
and, after a no-analyzer producer, reuse its referenced projects
(`BuildProjectReferences=false` after a no-analyzer `ResolveProjectReferences`).
See [producer admission and MAIN-owned preflight](gate-model.md#full-ci-producer-admission)
before enabling the source contract; merging it does not activate any pool.

Both producer and PR Gate selectors use `scripts/ci/gate-runner-heartbeat.sh`
for the read-only heartbeat proof. It requires exactly one JSON object, the
current instance, an integer timestamp no older than 90 seconds, and two idle
gate runners. Heavy-pool readiness remains independent of gate-pool admission.
The producer takes one snapshot; the existing PR selector retains its bounded
wake/wait orchestration. Both sparse checkouts include the shared validator.


### Heavy runner shutdown recovery

`heavy-shutdown-retry.yml` observes completed full trunk `CI` runs. It reads
trusted trunk code and revalidates the source run before requesting one failed-job
rerun. Only attempt 1 qualifies, and every failed non-summary job must have the
`honua-heavy` label and the canonical runner shutdown message in its logs.
Successful jobs remain completed; failed jobs and their dependents run once more.
Manual cancellations, selective runs, mixed product failures, unreadable logs and
second interruptions do not retry. Summary failures qualify only in known dependent-verdict steps; independent foundation/artifact failures remain failures. Required job names and dependency contracts
are unchanged. Manual full trunk runs use the `CI full` title prefix and selective trunk runs retain `CI affected`, so a dispatch nonce cannot change the mode; trunk-affected
dispatch identities retain their existing exact title.

Normal PRs add zero jobs. Each failed first-attempt full trunk run starts one
hosted observer; a qualifying interruption also requeues only failed jobs and
dependents. The seven interrupted jobs in run 43540829683 all stopped at
02:15:21Z on 14 November; that run was later cancelled and is ineligible for
recovery. No after-change recovery timing is claimed before a live interruption.
The companion IaC drain marker blocks fresh registration and listener restart
before stopping all runner services on a spot notice. GitHub records interrupted
jobs as failed until the observer requests the bounded rerun after completion.

### Superseded repository hygiene

The required `repo-hygiene / Repository hygiene` aggregate uses `!cancelled()`
and excludes cancelled dependency results. GitHub can cancel it when concurrency
supersedes a run; an already cancelled dependency skips the aggregate instead of
starting a runner solely to report failure. The skipped job is neutral admission
for superseded work, not a successful execution of either hygiene check. Active
runs still require both evidence-path and shared-hygiene success; failure,
skipping or missing results fail the shell check. The required name and both
`needs` dependencies stay unchanged. Normal runs add zero jobs; superseded runs
avoid the aggregate runner. Live after-change timing awaits a superseding event.
See [GitHub cancellation semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-cancellation).

A cancelled run can also be the newest check suite of a head: two events in the
same second (opened + labeled, opened + edited) start two runs in one
concurrency group, and GitHub orders that queue independently of suite order.
The cancelled required context then outranks the success in the older suite and
the armed PR stays blocked until a manual rerun (#9533, #9610). `Clean code` has
the same shape (#9611). `cancelled-check-rerun.yml` reruns such a run of either
workflow once, with its own event payload, when it is the newest suite of that
workflow for a head that is still an open PR's head. Older-suite cancellations,
superseded heads and later attempts are left alone; every job of the rerun
workflow still runs, so no check is relaxed. A newest-suite run cancelled by hand
is rerun once as well, and that rerun may cancel an older-suite run still in
progress; the attempt filter bounds both.

Both workflows cancel a running run only on `synchronize` (#9615). A new head
makes the running result obsolete, so it is still cancelled and no runner time
is added for superseded heads. Other events (opened, edited, labeled,
unlabeled, reopened) re-judge the same head: they queue behind the running run
in the same group, both finish, and the newest suite reports a real result.
Before this, 90 of 450 PR heads in 48 h carried a same-head cancelled hygiene
run. A burst of three or more events can still replace the pending run, which
the rerun workflow above repairs.

Operator Eval children keep Ryuk and PostGIS 17, and Core and Cloud Contracts children keep Ryuk, PostGIS 16 and PostGIS 18; the other split children prepare no images. `test-shard-image-selection.py` executes the workflow selector against every child of every split parent, compares it with the images the unsplit parent selected, and checks unrelated controls. Only Operator Eval Harness 1, which runs `EvalHarnessTests`, uploads `operator-eval-report`; the partition contracts require each report upload to move to exactly one child.

### Lightweight workflow runner admission

Repository hygiene, Clean code, Runner label lint, Review Gate Attestation,
Review Event Bridge and OpenAPI drift/governance use the fork-guarded
`HONUA_LIGHT_RUNNER` JSON selector with
`ubuntu-latest` fallback. The shared reusable workflows must be pinned to a
revision supporting that selector. Each local shell job establishes `TMPDIR`,
`TEMP` and `TMP` from `RUNNER_TEMP` before checkout. Clearing the variable affects
new jobs; it does not migrate queued or running jobs.

OpenAPI Contract Governance includes `scripts/ci/lib/python-resolve.sh` in its
PR paths because `validate-openapi-contracts.sh` sources that helper. This adds
no jobs for unrelated changes and retains the existing non-draft admission.

Older PR bases that lack the retired-cache policy load it from fetched trusted
trunk. The explicit base/head diff is still checked, head-supplied policy cannot
replace it, and a missing or incomplete trusted policy fails the required
hygiene job.

Quarantine publication scans retained CI/PR Gate histories twice hourly and on trusted default-branch manual dispatch, rather than once per completion, to amortize API discovery without dropping rerun evidence.

### Feature stream default application lifetime

`FeatureStreamEndpointsTests` shares one seeded Pro application through its
class fixture. Sequential default-host cases clear sessions and create a fresh admin
client. The production PostgreSQL event log and its monotonic generations remain
intact, as they do across separate application lifetimes. Cases that replace licensing,
metadata or other configuration still build and dispose their own application.
The shared host uses a one-hour heartbeat interval to avoid unsolicited frames
between cases; scheduled-heartbeat cases retain their separate configured hosts.
Credential expiry settings and every assertion are unchanged.

Paired build-host runs of the complete class passed all 126 cases: 802 seconds
before and 632 seconds after the shared-host change, saving 170 seconds
(2.83 runner-minutes) with one
test process in each run. These historical measurements precede the PostgreSQL
event-log migration and are not timings of the current implementation. The final
head retains the production database store and is validated in the PR Gate. Full-shard
elapsed savings have not yet been measured.
All cases remain in the selected streaming shards and twice-daily full trunk
runs. This changes no runner count, required check name, required aggregate
dependency, workflow or shard definition.

### Advisory heavy compute admission

| Workflow file | Trigger | Compute policy |
|---|---|---|
| `advisory-runner-select.yml` | `workflow_call` from CodeQL, Store Image Size, Serving Image Boundary and the full-CI producer | Hosted bootstrap (honua-light has only the snap AWS CLI, which cannot start in the runner sandbox); same-repository PRs read existing instance/heartbeat with trusted base scripts; heavy pool while more than two of its runners are idle (two stay free for required PR Gate jobs), else the original hosted label. |

The selector is read-only: it never wakes or resizes capacity. Its 90-second
heartbeat proof binds the current instance and available heavy worker. Forks,
dispatches from non-default refs, missing configuration/credentials, busy/stale
hosts and health read errors use hosted compute. The OIDC trust admits
same-repository PRs and the trunk ref (role assumption succeeded on trunk
dispatch 44309947920). Selection
errors also preserve hosted execution through caller fallback expressions.
Each consumer independently checks repository trust before accepting the
selected runner and uses hosted compute for forks;
Serving Image Boundary still requires successful impact detection. No CodeQL
language/build mode, image variant, test or gate is removed. Native GitHub Code
Quality uses a managed workflow and is outside these runner-routing changes.

Advisory heavy selection preserves runner-group restrictions in object selectors as well as label arrays. Store native-runtime checks execute all eight cases as container root through rootless Docker on heavy runners; hosted runners retain their root execution. Both image smoke paths select free loopback ports through the shared ownership helper before publishing them explicitly. This avoids the independent Docker allocators choosing the same default port across rootless accounts; readiness remains bound to the launched container.

CodeQL self-hosted execution explicitly budgets 8,192 MB and four extraction
threads, with `DOTNET_PROCESSOR_COUNT=4` also bounding MSBuild and extractor
parallelism. Both initialization lanes receive these inputs; analysis inherits
them from initialization. Hosted execution keeps the action defaults. The heavy
runner's existing 16 GiB user slice includes the runner and its child processes:
the remaining half is headroom, not additional provisioned capacity. The pinned
action reads root cgroup limits and misses this nested slice. In run
43722168928/job132839988502 it advertised 240,353 MB and 64 threads before the
kernel killed `Semmle.Autobuil` at the 16 GiB slice limit. On pull requests the
no-build extractor cannot authenticate to the GitHub Packages feed, so the
solution restore fails and it restores each project separately, one per
extraction thread: unbounded on the heavy host, four with this budget, as on
the four-CPU hosted runner where run 43742167308 passed in 19 minutes.
Authenticating that feed for the pull request lane is a separate follow-up.
This is a resource repair, with no query, source, build-mode, timeout or gate
reduction. A subsequent live CodeQL pass and peak-memory measurement are still
needed to verify it.

### Evaluated test analyzer build roots

After the existing scoped restore, each test analyzer job evaluates `IsTestProject` after SDK and NuGet imports and builds those test roots. All multi-targeted roots remain selected conservatively because test ownership can depend on the inner target framework. Forward project references still compile; the independent source analyzer job retains all source roots and source diagnostics. An empty evaluated root set skips compilation explicitly. Restore scope, diagnostic policy, required job names and job dependencies are unchanged. Standalone single-targeted source roots no longer compile merely because they are solution members.

Baseline: #7807's hosted general test analyzer ran 14.67 minutes, including 0.43 minutes scoped restore and 13.60 minutes compilation; the server test analyzer ran 10.32 minutes. Both passed. A completed hosted run is required to measure the resulting runtime; no saving is assumed.

### SensorThings collection host lifetime

The SensorThings `Database` collection holds a collection-scoped default
`WebAppFixture`. Its reference keeps the shared default host alive, so the
serial default-configuration classes (query options, paging, ingest, ingest
identity, navigation and review regressions) no longer rebuild that host for
every case. Each case still creates its own `WebAppFixture`, schema and seed.
Classes with their own configuration, such as core CRUD with development
authentication off and the multi-tenant streams, keep their private hosts.
The expiring portal credential in the stream authorization theory now lives 20
seconds instead of 45; connect, publish and the first observation take about 3
seconds, and the case still asserts the observation arrives before expiry and the
stream then ends with `authorization-ended`.

Paired build-host runs of the complete SensorThings test project on trunk
c06ce1031a passed the same 135 cases with no skips. VSTest execution fell from
13m08s to 7m55s, saving 313 seconds (5.2 runner-minutes); the TRX interval fell
from 789.2 to 477.2 seconds. Query options fell from 116.7 to 11.0 seconds of
fixture-inclusive time and paging from 57.8 to 6.4. The baseline shared the build
host with another lane job, so part of the difference may be load; the per-case
fixture time removed (about 5 seconds per default-host case) accounts for most of
it. Full CI job savings are unmeasured.
All cases remain in the SensorThings shard on selected PR runs and twice-daily
full trunk runs. This changes no runner count, required check name, required
aggregate dependency, workflow or shard definition.

### GeoServices MapServer collection host lifetime

The `Database.GeoServicesMapServer` collection holds a collection-scoped default
`WebAppFixture`. Its reference keeps the shared default host alive, so the
serial MapServer, export and identify classes no longer rebuild that host for
every case after the previous case releases it. Each case still creates its own
`WebAppFixture`, schema and seed, and the KML cleanup in
`MapServerEndpointTestBase` is unchanged. Cases that change the metadata graph
(service governance, protocol-duplicate publications and the KML seed) now write
their own schema partition instead of the shared baseline, so those changes end
with the case. Classes that replace services keep their private hosts.

Paired build-host runs on trunk c06ce1031a used the three CI shard filters with
one test process each and passed the same cases with no skips. TRX intervals:
GeoServices MapServer 562.3 to 33.3 seconds (56 cases), MapServer Export 804.2 to
34.0 seconds (65 cases), MapServer Identify 625.5 to 32.0 seconds (52 cases). The
build host was heavily loaded, so its per-case host rebuild (about 10 seconds)
exceeds CI's: full run 43709972683 spent 212 and 225 seconds of per-case fixture
time in the MapServer and Export classes (3.5 to 3.8 seconds per case), which is
the expected CI saving for those shards. Full CI job savings are unmeasured.
All cases remain in their existing shards on selected PR runs and twice-daily
full trunk runs. This changes no runner count, required check name, required
aggregate dependency, workflow or shard definition.

### Catalog endpoint SOAP raster host lifetime

`GeoservicesCatalogEndpointTests` lazily starts one SOAP raster application per
class for six read-only tests that use the default raster store. Only Catalog
Support 5 and 6 contain these cases; the other four shards do not start this host.
The existing database collection serializes the tests; the class fixture owns
startup and disposal.
Cases that customize raster metadata, inspect substitute call counts, mutate
catalog state or override host configuration retain their private applications.
Every request, response assertion and discovered case remains in place.

All cases remain in the existing GeoServices Catalog Support shards on selected
PR runs and twice-daily full trunk runs. Runner count, required check names,
aggregate dependencies, workflows and shard definitions are unchanged. A paired
build-host class run passed the same 59 cases without skips: VSTest execution
185 to 140 seconds; fixture-inclusive TRX interval 214.73 to 159.48 seconds.
This saves 0.75 execution runner-minutes in one unchanged test process. The six
shared cases' body total fell from 34.50 to 4.99 seconds; other cases also varied,
so the observed whole-class reduction is not entirely attributed to host reuse.
Build and admission time are excluded. The complete existing Support 6 test
invocation passed the same 24 cases without skips: VSTest 81 to 63 seconds,
saving 18 seconds (0.30 execution runner-minutes) with one process unchanged;
fixture-inclusive TRX interval 83.24 to 66.93 seconds. Full CI-job time, including
setup and build, remains unmeasured.

### GPServer default application lifetime

`GPServerEndpointTests` shares one seeded application through a class fixture.
The sequential default-host cases clear their in-memory job store before use,
so job listings and persisted-plan checks retain per-case state. Successful
synchronous execution cases share a second host with the same job-service stub,
clear its recorded plan before each case and use fresh clients. Other cases that
replace authorization, metadata or geoprocessing services own separate hosts.
All assertions and method filters remain unchanged; the GPServer/NAServer
shards still run on selected PRs and full trunk runs.

Paired build-host class runs passed the same 71/71 cases. VSTest reported
342 seconds before and 132 seconds after (210 seconds saved). The wider TRX
start-to-finish interval fell from 349.64 to 145.88 seconds, saving 203.76 seconds
(3.40 test-run runner-minutes). Each used one test process with the same filter.
No runner, workflow, required check, aggregate dependency or shard definition
changes. These are class timings; a full-shard elapsed saving remains unmeasured.

A subsequent synchronous-host optimization was measured with four interleaved
Release runs of the complete owning GPServer Endpoints shard. All four passed
the same 71 cases without skips. Class-body totals were 34.012 / 32.569 seconds
before and 27.791 / 31.771 seconds after (medians 33.291 to 29.781 seconds).
Fixture-inclusive TRX intervals were 46.941 / 53.211 seconds before and
41.400 / 45.258 seconds after (medians 50.076 to 43.329 seconds). This saves
6.747 seconds, or 0.112 test-run runner-minutes, with one runner unchanged.
Compilation, admission and full CI-job time are excluded. The earlier timings
above describe the separate default-host optimization, not this comparison.

### Admin authentication default application lifetime

`AdminAuthEndpointsTests` initializes its default `WebAppFixture` only when a
case requests its default client. Cases that build a separately configured OIDC
or client-certificate host no longer start an unused default host first. Each
case retains its own application, database schema, authentication configuration
and assertions. Of the current 47 discovered test methods (64 cases), 32
methods (49 cases) do not use the default application.

All cases remain in the Server Features Admin Authentication and Credentials
shard on selected PR runs and twice-daily full trunk runs. Runner count, required
check names, aggregate dependencies, workflows and shard definitions are unchanged.

Paired Release build-host runs passed the identical 64 cases with one test
process each. VSTest reported execution fell from 437 to 306 seconds, saving
131 seconds (2.18 execution runner-minutes). The wider TRX start-to-finish
interval fell from 446.067028 to 314.676740 seconds, saving 131.390288 seconds
(2.19 test-run runner-minutes). Queue and build time are excluded. Full-shard
job savings remain unmeasured: the shard also includes
`OperatorBearerTokenServiceTests` and CI setup.

### Admin authentication parallel case groups

xUnit runs the cases of one test class in sequence, and `AdminAuthEndpointsTests`
was also in the `Database` collection, which disables parallel execution. Its 80
cases each start their own application, so the class ran end to end in one
sequence. Each partial file now holds its cases in a nested class
(`BrowserSessions`, `CallbackErrors`, `FailedAttempts`, `GrpcBrowserSessions`,
`WebSocketOrigin`), and the outer class leaves the `Database` collection, so the
six groups run in parallel. No class uses the collection fixture. Every case still
creates its own application and isolated database schema; stub identity providers
and clocks are created per case where used, and no assertion changes. Rate-limit and failed-attempt state stays per
application, so parallel groups do not share it.

The nested names keep the `AdminAuthEndpointsTests` prefix, so the shard filter
`FullyQualifiedName~Honua.Server.Tests.Features.Admin.AdminAuthEndpointsTests`
selects the same 80 cases in the Server Features Admin Authentication and
Credentials shard on selected PR runs and twice-daily full trunk runs. Runner
count, required check names, aggregate dependencies, workflows and shard
definitions are unchanged.

Before, on hosted full trunk run 44183857476, the class summed 618.9 seconds and
the shard's TRX start-to-finish interval was 648.9 seconds for the 79 cases it
then held: the cases ran one at a time. The parallel saving is not yet measured.

### ImageServer raw pixel host lifetime

`ImageServerRawPixelExportTests` retains the default shared web host through a
class-scoped `WebAppFixture`. Each case still creates and disposes its own fixture,
client and schema, resets the raster samples, and holds the schema-mutation lock
through every request. This avoids rebuilding the default web host between cases
without sharing case data or changing assertions.

Paired full-class build-host runs passed the same 45 cases with no skips. VSTest
execution fell from 230 to 20 seconds, saving 210 seconds (3.50 execution
runner-minutes). The wider TRX start-to-finish interval fell from 235.864591 to
33.210444 seconds, saving 202.654147 seconds (3.38 test-run runner-minutes).
Each run used one test process; full CI shard job savings remain unmeasured.
The selected PR shard and twice-daily full trunk runs retain all cases and their
existing budgets.
No workflow, shard selection, runner count, required check or aggregate dependency
changes are made.

### ImageServer mosaic default host lifetime

`ImageServerMosaicIntegrationTests` retains the default web host through a
class-scoped `WebAppFixture`. Each case still creates and disposes its own fixture
and schema, and holds the schema-mutation lock through its raster seed and
requests. Explicit license configurations retain private hosts. The warm raster
query performance assertion and all response assertions remain unchanged.
All cases remain in selected PR raster rendering shards and twice-daily full
trunk runs, with no workflow, shard, runner count, required check or aggregate
dependency changes.

Paired build-host runs passed the same 20 cases with no failures or skips.
VSTest execution fell from 115 to 46 seconds (69 seconds, or 1.15 execution
runner-minutes saved). The fixture-inclusive TRX interval fell from 133.049096
to 73.394077 seconds (59.655019 seconds saved). Both runs used one test
process; complete CI shard/job savings remain unmeasured.

### Secure connection endpoint test host reuse

`SecureConnectionEndpointsTests` lazily starts one class-owned application for
its unchanged default admin authentication and database seed. Each case keeps
its own authenticated client and uses unique connection names and IDs. Cases
with substituted registries, connection drivers, audit logs, encryption failures
or host-allowlist settings retain their private application hosts. No host is
started when a shard selects only those custom cases. Assertions, test selection,
parallelism and required checks remain unchanged.

A paired build-host run passed the exact same 56 cases with zero skips in
306 seconds before and 181 seconds after: 125 seconds (2.08 runner-minutes)
saved with one test process in each run. TRX test-run duration fell from
317.974 to 193.183 seconds. The 21 default-host case bodies took 103.834 versus
8.437 seconds; the 35 private-host case bodies took 193.064 versus 172.680
seconds. Ambient variation affects both groups, so the complete duration change
is an observation rather than an attribution to fixture reuse alone. These are
class execution measurements, excluding compilation, admission and copyback.

A separate pair using the complete existing Platform and Connections shard
filter passed identical 91-case/class/method/outcome multisets with zero skips.
Its one-process test execution fell from 458 to 363 seconds, saving 95 seconds
(1.58 runner-minutes); fixture-inclusive TRX duration fell from 470.055 to
376.298 seconds. The other three classes are unchanged. This overlaps the
class measurement and is not added to it. Complete CI-job setup and build
duration remain unmeasured.

### PrintingTools endpoint application lifetime

`PrintingToolsEndpointTests` keeps one default application for the class. Each case creates its own admin client, as the per-case application did. Jobs are addressed by generated id, and every assertion stays. The class remains in Server Features Spec Printing and Static Maps 3. No runner count, required check name, required aggregate dependency, workflow, or shard definition changes.

A paired build-host run passed the same 22 cases with zero skips, one process each. VSTest case time was 1 minute 41 seconds before and 2 seconds after, because the one application start is now class-fixture time. Fixture-inclusive TRX duration fell from 110.869 to 24.444 seconds, saving 86.424 seconds (1.44 test-run runner-minutes). Compilation, admission and copyback are excluded.

The whole Spec Printing and Static Maps 3 filter passed the same 142 cases with zero skips in both runs. VSTest reported 4 minutes 29 seconds before and 2 minutes 7 seconds after. TRX wall time fell from 281.6 to 152.9 seconds, saving 128.7 seconds (2.15 runner-minutes) per shard run.

### OData endpoint read-only host lifetime

`ODataEndpointTests` builds its Pro-licensed, `odata.yaml`-seeded application once
per class through the `ReadOnlyProHost` class fixture. The read-only GET cases use
that host's client and schema. The theory that changes the metadata graph and
inserts a feature still builds, seeds and disposes a private Pro host per case.
Assertions, endpoint attributes and case counts are unchanged.

Paired build-host runs of the class on trunk 47eb3bc9ba passed the same 65 cases
with no skips. VSTest execution fell from 16m16s to 48s and the TRX interval from
977.6 to 73.5 seconds. The build host was heavily loaded, so each private Pro host
cost about 15 seconds there; full run 43709972683 spent 183 seconds of per-case
fixture time in this class (2.8 seconds per case), which is the expected CI saving.
Full CI job savings are unmeasured.
All cases remain in the OData Core Endpoints shard on selected PR runs and
twice-daily full trunk runs. This changes no runner count, required check name,
required aggregate dependency, workflow or shard definition.

### OData, OGC classic and STAC collection host lifetime

The OData, OGC classic and STAC test assemblies serialize their database-backed
classes in a `Database` collection that previously had no definition, so nothing
held the reference-counted shared default host between cases. Each assembly now
defines that collection with a collection-scoped default `WebAppFixture`, so
default-configuration cases reuse the shared host instead of rebuilding it. The
definition keeps default parallelization, so scheduling is unchanged. Existing
per-case and per-class fixtures and their schemas are unchanged; classes with
private configuration keep their hosts. The WMTS advertised-resource case now also
clears any unconsumed graph activation, which could otherwise reach a later case.
The collection fixture starts the shared host even when a filter selects only
private-host classes, which costs one startup in that case.

With this change on trunk 47eb3bc9ba, build-host runs of the three complete
projects passed every case with no skips: OGC classic 592 (19m15s VSTest), STAC
149 (5m29s) and OData 512 (36m27s). The OGC classic project without this change did not
finish within 111 minutes of execution on the same, heavily loaded host, so no
paired whole-project time is claimed. As an estimate from unpaired data, per-case
fixture time in the affected default-host classes was 2.1 to 3.5 seconds in full
run 43709972683 and 0.1 to 0.5 seconds in the build-host run, while private-host
classes stayed near 3.4 seconds; across those 18 classes that is about 639
seconds (10.7 runner-minutes) of fixture time per full run. Full CI job savings
are unmeasured.
All cases remain in their existing shards on selected PR runs and twice-daily
full trunk runs. This changes no runner count, required check name, required
aggregate dependency, workflow or shard definition.

### OData error-handling and advanced-feature host lifetime

`ODataErrorHandlingTests` and `ODataAdvancedFeaturesTests` build their Pro-licensed,
`odata.yaml`-seeded application once per class through a `ReadOnlyProHost` class
fixture, as `ODataEndpointTests` does. Read-only GET cases use that host's client.
Every case that sends a POST, PATCH or `$batch`, inserts features or reads the
database still builds, seeds and disposes a private Pro host; cases that already
built their own host are unchanged. Assertions and case counts are unchanged.

Paired build-host runs of both classes on trunk f0483ffc72 passed the same 109
cases (53 error-handling, 56 advanced) with no skips. VSTest execution fell from
30m11s to 4m22s and the TRX interval from 1812.9 to 324.0 seconds on a heavily
loaded host. Full run 43709972683 spent 336 seconds of per-case fixture time in the
two classes; about two thirds of their cases now share the host, so the expected CI
saving is roughly 220 seconds. Full CI job savings are unmeasured.
All cases remain in their existing OData shards on selected PR runs and
twice-daily full trunk runs. This changes no runner count, required check name,
required aggregate dependency, workflow or shard definition.

### I3S scene server host lifetime

`I3sSceneServerEndpointTests` builds its enterprise and community applications, the
authenticated client and the two database-backed scene registrations once per class
through the `SceneHosts` class fixture instead of for every case. Every case only
reads, and the scene registry rows the cases see are the same rows each case used
to register after its hosts seeded. Test bodies and assertions are unchanged.

Paired build-host runs of the class on trunk f52d27ce02 passed the same 35 cases with
no skips. VSTest execution fell from 18m37s to 6s and the TRX interval from 1118.1 to
77.6 seconds on a heavily loaded host. Full run 43709972683 spent 222 seconds of
per-case fixture time in this class (6.3 seconds per case), the expected CI saving.
Full CI job savings are unmeasured.
All cases remain in the Scene I3S Resources 2 shard on selected PR runs and
twice-daily full trunk runs. This changes no runner count, required check name,
required aggregate dependency, workflow or shard definition.

### Spatial analytics REST host lifetime

`SpatialAnalyticsRestTests` builds its Pro-licensed application once per class
through a `ReadOnlyProHost` class fixture. The clusters, density, buffer-aggregate
and spatial-join query cases only read, so they use that host's client; the three
cases that seed extra rows still build, seed and dispose a private Pro host.
Assertions and case counts are unchanged.

Paired build-host runs of the class on trunk 0afc7ba8ba passed the same 47 cases
with no skips. VSTest execution fell from 4m41s to 20s, saving 261 seconds (4.35
runner-minutes), and the TRX interval from 302.5 to 41.5 seconds, with one test
process each. Full run 43709972683 spent 174 seconds of per-case fixture time in
this class. Full CI job savings are unmeasured.
All cases remain in their existing Server Features Analytics Studio Export and
Reporting shard on selected PR runs and twice-daily full trunk runs. This changes no
runner count, required check name, required aggregate dependency, workflow or shard
definition.

### Scene gRPC host lifetime

`SceneGrpcIntegrationTests` builds its configured application and four temporary
scene asset roots once per class through `SharedSceneHost`. Each case still creates
and disposes its own gRPC channel, registers its extent scene and removes its
registered extent/inactive scenes afterward. Test bodies and assertions are
unchanged; the existing database collection keeps cases serialized.

The 52 cases remain in the existing gRPC Protocol and Scene 3–7 shards on
selected PR runs and twice-daily full trunk runs. Each selected shard process
creates its own fixture. No runner, required check, aggregate dependency,
workflow or shard definition changes. Paired build-host runs passed the same 52
cases without skips: VSTest execution fell from 306 to 4 seconds, and the
fixture-inclusive TRX interval fell from 318.9 to 28.9 seconds. That saves 302
execution seconds (5.0 runner-minutes), or 290.0 seconds including fixture lifetime
(4.8 runner-minutes), with zero added runners. Full CI shard wall-time savings
remain unmeasured. Historical full run 43709972683 separately recorded 185.7
seconds of per-case fixture work across this class.

### FeatureServer maintenance host lifetime

`FeatureServerMaintenanceTests` builds its Pro-licensed application, with service
editing capabilities enabled, once per class through a `ReadOnlyProHost` class
fixture. The validate-SQL, domain and relationship GET cases only read and use that
host's client. Every case that POSTs (append, calculate and POST validate-SQL) or
inserts or reads rows still builds and disposes a private Pro host. Assertions and
case counts are unchanged.

Paired build-host runs of the class on trunk 8bbd43c0ee passed the same 42 cases
with no skips. VSTest execution fell from 10m44s to 2m14s and the TRX interval from
646.7 to 151.8 seconds. Full run 43709972683 spent 139 seconds of per-case fixture
time in this class; about four sevenths of its cases now share the host. Full CI
job savings are unmeasured.
All cases remain in their existing FeatureServer shard on selected PR runs and
twice-daily full trunk runs. This changes no runner count, required check name,
required aggregate dependency, workflow or shard definition.

### Admin authorization host lifetime

`AdminAuthorizationTests` builds its application (server seed, development
authentication off, an admin password set) and its unauthenticated client once per
class through the `AnonymousHost` class fixture. Every case sends an anonymous
request with no credentials, so the authentication-failure limiter never counts it;
every write case expects 401, and the anonymous 200 cases only read. Test bodies and
assertions are unchanged.

Paired build-host runs of the class on trunk 8bbd43c0ee passed the same 58 cases with
no skips. VSTest execution fell from 6m16s to 3s, saving 373 seconds (6.2
runner-minutes), and the TRX interval from 395.2 to 21.8 seconds, with one test
process each. Full run 43709972683 spent 196 seconds of per-case fixture time in this
class. Full CI job savings are unmeasured.
All cases remain in their existing Server Features Admin Authorization shards on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow or shard definition.

### Query related records host lifetime

`QueryRelatedRecordsEndpointTests` builds its Pro-licensed application, with service
editing capabilities enabled, once per class through a `ReadOnlyProHost` class
fixture. The related-record GET cases use it. The case that inserts and deletes
features, and the POST, PUT and DELETE cases, first switch to a private Pro host of
their own. Test bodies and assertions are otherwise unchanged.

Paired build-host runs of the class on trunk 8bbd43c0ee passed the same 35 cases with
no skips. VSTest execution fell from 3m20s to 46s, saving 154 seconds (2.6
runner-minutes), and the TRX interval from 214.0 to 74.3 seconds, with one test
process each. Full run 43709972683 spent 124 seconds of per-case fixture time in this
class. Full CI job savings are unmeasured.
All cases remain in their existing shard on selected PR runs and twice-daily full
trunk runs. This changes no runner count, required check name, required aggregate
dependency, workflow or shard definition.

### Spatial correctness fixture lifetime

`SpatialCorrectnessTests` shares one seeded `WebAppFixture` through xUnit's class
fixture lifetime. Its eleven endpoint cases only query that private schema, including
the POST query case; they do not edit features, configuration or registrations.
The database collection and fixture disposal still belong to xUnit.

The thirteen geographic range and WKB cases live in
`SpatialCorrectnessUnitTests`. They retain every input and assertion and construct neither an HTTP host nor a
database fixture. The validation theories express expected validity with an enum;
the WKB theory accepts its four coordinates as a parameter array to satisfy the
strict signature rules without changing the coordinates tested. The endpoint cases retain their original methods and assertions too.
This removes repeated setup rather than dividing the suite across new runners.

The existing Core and Cloud Contracts 1 filter still owns the endpoint cases.
The existing Core and Cloud Contracts 4 catch-all owns the new geometry-only
class. All thirteen geometry cases carry Fast-tier unit attributes, so the
existing foundation Fast filter also executes them. The three moved theory
signatures are removed from the legacy untiered baseline. Both classes run
in the current selected PR and full trunk shard mechanisms;
no shard definition, workflow, required check or aggregate dependency changes.
The authored workflow inventory template carries this explanation; the canonical
inventory producer publishes its generated projection.

Paired build-host runs preserve all 24 passing cases with no skips: VSTest
102 → 21 seconds, fixture-inclusive TRX 114.646 → 29.494 seconds. Test-body
seconds fall from 25.912 to 4.126 for the endpoint class plus 0.053 for the
host-free geometry class. Both runs use one process; no runners are added.
The 81-second execution saving is 1.35 runner-minutes for this focused suite;
full CI shard wall-time savings remain unmeasured. These measurements use the
initial fixture change before the equivalent strict-signature parameter adapters.

### OGC Features enhancements host lifetime

`OgcFeaturesEnhancementsTests` shares one Pro-licensed application and its seeded
WGS84 spatial and temporal metadata through a class fixture for read-only requests.
The feature creation, null-geometry insertion and schema-field addition cases each
initialize and dispose a private application with the same seed and metadata.
The exact geometry identifier assertions therefore retain an unchanged database.
Every test input, request and assertion is preserved.

The complete class remains in OGC API Features 1 for selected PR runs and the
existing full trunk runs. No workflow, shard filter, runner count, required check
name or aggregate dependency changes. Paired build-host timings will distinguish
test execution from build time; full CI job savings remain unmeasured.

### PrintingTools GPServer service application lifetime

`PrintingToolsGpServerService5546Tests` shares one `WebAppFixture` per class
instead of creating and seeding one per case. Every case owns and disposes its
own admin client. REST and SOAP metadata requests are read-only; render and job
requests use their own generated job IDs. Database collection serialization and
all test bodies, endpoint attributes and assertions remain unchanged.

The class stays in Server Features Spec Printing and Static Maps 1 on selected
PR runs and twice-daily full trunk runs. No tests move to a later gate. This
changes no runner count, required check name, aggregate dependency, workflow or
shard definition.

### ImageServer default raster fixture lifetime

ImageServerEndpointsTests shares one class-scoped host and seeded schema for the
62 test methods that use the identical default raster substitute. These cases
query metadata, render pixels, or compute derived responses without changing
stored data or substitute behavior; POST requests are read operations too.
Custom raster stores, authorization/masking, sensor metadata, and elevation
configurations keep their private hosts and schemas. The existing database
collection still serializes the class, and xUnit disposes the shared host once.
All test attributes, request bodies and assertions are retained.

The six existing GeoServices ImageServer filters continue selecting these same
methods in selected PR and full trunk runs. No workflow, shard definition,
required check name, timeout, or aggregate dependency changes. This removes
repeated initialization rather than adding runners. The authored inventory
template is updated; its generated inventory projection remains producer-owned.

### Durable GP SOAP authentication host lifetime

`GPServerDurableRuntimeTests.AuthenticationTests` shares one authenticated
PostgreSQL job host across the anonymous, invalid API key and invalid bearer
cases. Each case still creates its own owner job and clients, checks all seven
SOAP operations for an unauthorized fault without job details, and verifies
that job count, successful status and the independently computed area survive.

The nested class keeps the existing serialized job-substrate collection and
ends the shared worker lifetime before restart, migration and private-host
cases clear the substrate. Those cases retain their private applications.
The existing GPServer Durable and Aliases filter matches the parent class prefix,
including the nested class, in selected PR and full trunk runs. No workflow,
shard filter, required check or aggregate dependency changes; runner delta zero.
Paired class build-host measurement: 25/25 identical passing cases, 498 to 470
seconds.

### Geocoding batch response fixture

`GeocodingEndpointTests` shares an Enterprise-licensed batch application across
11 response-only cases with identical provider capabilities. Provider results
are derived from the current request; these cases never inspect request capture.
The existing Community application remains separate. Each case owns its client
and response. Cases that inspect captured requests, override capabilities or
batch limits, exercise failover, or vary licensing retain private applications.
The class fixture disposes both shared applications after the class completes.

All existing case data, endpoint attributes, requests and assertions remain.
The class stays in its existing PR-selected and full-trunk partition; this
changes host lifetime without adding runners, jobs or required check names.

### Catalog service typing host lifetime

`GeoservicesCatalogServiceTypingTests.DefaultTypingFixture` initializes one
application with the default typing graph and raster store for the four read-only
service-directory cases. The lifecycle loop and duplicate-publication case retain
their private applications and custom graphs. All service-name selection and
response assertions remain in place; the class stays in its database collection.

The existing Catalog and ImageServer Support partition retains these cases on
selected PR runs and full trunk runs. No workflow, shard filter, required check,
aggregate dependency or runner count changes. Paired class and existing-partition
measurements are collected separately; test-body time excludes class-fixture
startup and disposal, and does not establish complete CI job-wall savings.

### Admin operations progress host lifetime

`OperationsProgressEndpointsTests` (including its query partial) shares one
application with in-memory durable job store and queue doubles across every
case that uses them. Each case still writes progress and jobs under its own new
operation id and deletes its progress afterwards; the shared queue is drained
before each case, so the queue-depth assertion stays exact. Cases that replace
the job store, queue or remote backend keep their private applications and no
longer start an unused default application as well.

The class stays in Server Features Admin Operations Endpoints on selected PR
runs and full trunk runs. No workflow, shard filter, required check, aggregate
dependency or runner count changes. Build-host class 39/39 identical passing
cases, 391 to 88-92 seconds; existing partition 122/122, 981 to 628 seconds.

### Cross-protocol permission matrix write hosts

`CrossProtocolPermissionMatrixTests` write cases (the FeatureServer/gRPC
ApplyEdits parity theory and the FeatureServer, OData and OGC Features write
cases) differ only in the grants that the test role holds. They now share one
application per write catalog (FeatureServer/OData/OGC and gRPC) instead of
starting one per case: each case replaces the role's grants before its request,
cases of a class run one at a time, permissions are resolved per request, and
the test feature store is created per request, so no grant or edit outlives its
case. Role names, payloads and every status assertion are unchanged; the two
layer-scoped read cases keep their own applications.

The class stays split across Security and Authorization 1 and 7 on selected PR
runs and full trunk runs. No workflow, shard filter, required check, aggregate
dependency or runner count changes. Build-host class 59/59 identical passing
cases, 349.6 to 31.7-32.5 seconds.

### Capability manifest and deployment profile host reuse

`CapabilityManifestEndpointTests` cases that need only the default manifest
configuration now use the class's default application, and cases that differ
only by the manifest source (registry) or the experimental switch share one
application per configuration, started on first use. Those cases only read the
manifest. Cases with any other configuration or replaced services keep their own
applications. `DeploymentCapabilityProfileIntegrationTests` folder-path cases
share one application per configured capability (8 cases, 4 applications) with
the test service moved into the "Public" folder once at start.

The classes stay in Server Features Capabilities on selected PR runs and full
trunk runs. No workflow, shard filter, required check, aggregate dependency or
runner count changes. Build-host namespace 100/100 identical passing cases in one
job: wall 633 to 419-438 seconds; DeploymentCapabilityProfileIntegrationTests
80.4 to 43.0-43.7 seconds and CapabilityManifestEndpointTests 305.0 to
225.7-241.8 seconds, while the unchanged classes in the same job also ran
148.5 to 102-110 seconds, so part of that difference is host load.

### Sharing REST token host lifetime

`SharingRestTokenTests` shares one application with the protected-service graph
and token settings across every case that used the default token configuration,
instead of starting one per case. A startup filter stamps the request client
address; each case now gets its own TEST-NET-3 address, so failed attempts that
the authentication failure limiter counts per source never carry over between
cases, and IP-bound tokens still bind to the address of the case that issued
them. Each case issues, exchanges or revokes only its own tokens and keys. Cases
that need other settings or a replaced credential verifier keep their own
applications.

The class stays in Server Features Sharing 1 on selected PR runs and full trunk
runs. No workflow, shard filter, required check, aggregate dependency or runner
count changes. Build-host class 45/45 identical passing cases in one job, 297.2
to 34.7-34.8 seconds (process wall 345 to 62-71 seconds).

### Sharing community and REST read host lifetime

`SharingCommunityTests` shares one application with the durable PostgreSQL
portal sharing stores; the portal sharing tables are created once, and each case
deletes all rows from `portal_item_shares` and `portal_community_groups` before
it starts, so every case still begins from empty sharing tables. The case that
needs the store binding removed keeps its own application.
`SharingRestReadTests` shares one application across the default-graph cases,
which only send anonymous GET and HEAD requests; none of the endpoints they call
(sharing info, portal URI list, portals/self, search, content items and
`/rest/info`) is output-cached. The configured-public-root and
disabled-entitlement cases keep their own applications.

Both classes stay in Server Features Sharing 2 on selected PR runs and full
trunk runs. No workflow, shard filter, required check, aggregate dependency or
runner count changes. One build-host job, all 83 cases of the three Sharing
classes identical and passing: SharingCommunityTests 114.6 to 10.3-10.9 seconds,
SharingRestReadTests 121.3 to 16.1-17.5 seconds, SharingRestTokenTests 326.4 to
36.3-38.9 seconds; process wall 584 to 106-121 seconds.

### API key token parameter host lifetime

`ApiKeyTokenParameterTests` shares one application across the class. Each case
creates only its own keys and never revokes or lists shared ones. Key names are
not unique in the store;
lookup is by key material. The in-process transport leaves the client address
unset, and the authentication failure limiter ignores a request with no client
address, so the expected invalid-token responses do not lock later cases out.
The protected layer's access metadata is set once when the host starts.
`ApiKeyTokenParameterPrincipalTests` does not use this host.

The class stays split across Security and Authorization 2 and 3 on selected PR
runs and full trunk runs. No workflow, shard filter, required check, aggregate
dependency or runner count changes. One build-host job, 24/24 identical passing
cases, zero skipped: class span 163.3 to 3.9-4.0 seconds (process wall 186 to
63 and 29 seconds). Whole-job shard savings were not measured.

### Patch concurrency host lifetime

`PatchConcurrencyTests` shares two Pro applications with the OData seed across
the class instead of one per case. One keeps the product field-mask source,
because only that source takes the combined row and field read path; the other
registers the test's mutable mask. Each case inserts and edits only its own
feature, and starts by installing a fresh write barrier and clearing the test
mask. `OgcPatch_PublicIdentifierAndMaskedField_UsesCompleteSnapshot` changes
the layer schema and keeps its own application.

The class stays in Core Mutation Concurrency on selected PR runs and full
trunk runs. No workflow, shard filter, required check, aggregate dependency or
runner count changes. One build-host job, 43/43 passing cases, zero skipped:
class span 199.8 to 15.2-15.6 seconds (process wall 212 to 38-43 seconds).

### GPServer SOAP job host lifetime

`GPServerSoapEndpointsTests` built one application per job case to inject a
fresh job-service substitute. The class fixture now holds one job-service
substitute and one terminal-service substitute, registered as singletons so
every consumer, including the real singleton terminal service, sees the same
object, and two lazily started applications: one with the job substitute and
the real terminal service, one with both substitutes. Each case clears the
substitutes it uses (returns, callbacks and received calls) and configures
them again, so every assertion is unchanged.

The class stays split across GeoServices GPServer SOAP and SOAP Progress on
selected PR runs and full trunk runs. No workflow, shard filter, required
check, aggregate dependency or runner count changes. One build-host job, 81/81
passing cases, zero skipped: class span 243.7 to 23.0-24.2 seconds (process
wall 248 to 27-28 seconds).

### Collaboration co-editing host lifetime

`CollaborationLiveCoEditingTests` started at least one database-less application per case.
The class fixture now holds two lazily started applications with the default
settings: one with the restart-durable operation log and one without. Every case
creates its own draft, and session, operation log and checkpoint state is keyed
by map, so a case never reads another case's map; the per-map participant limits
are unaffected. Cases that change settings, decorate the operation log or supply
a Studio store keep their own applications.

The class stays in Server Features Collaboration Mobile and Identity 2 on
selected PR runs and full trunk runs. No workflow, shard filter, required check,
aggregate dependency or runner count changes. One build-host job, 24/24 passing
cases, zero skipped: class span 118.1 to 57.2-58.0 seconds (process wall 123 to
63-64 seconds).

### SensorThings stream host lifetime

`SensorThingsStreamEndpointsTests` shares one multi-tenant application across
the class instead of one per case. Stream cases open their own connections and
read from the live tail or from a cursor taken in the same case, so observations
ingested by an earlier case are never expected; the tenant-collision case seeds
and drops its own schema.

The class stays in SensorThings 5 on selected PR runs and full trunk runs. No
workflow, shard filter, required check, aggregate dependency or runner count
changes. One build-host job, 25/25 passing cases, zero skipped: class span 179.7
to 67.1 seconds on both head runs (process wall 194 to 83-84 seconds).

### Alert admin host lifetime

`AlertAdminEndpointsTests` built its webhook-only application for every case,
including the seven cases that then boot eight private applications with
another channel policy or stubbed stores. The webhook-only application is now one class
fixture: its cases scope every zone and rule to a fresh service id and probe
missing ids far above the sequence, and each case still creates its own admin
client. Application boots per run drop from 29 to 9; every assertion is kept.

The class stays in its admin shard on selected PR runs and full trunk runs. No
workflow, shard filter, required check, aggregate dependency or runner count
changes. Before: 80.4 seconds summed case time in full trunk run 44013566836.

### Isolated-host classes leave the serial database collection

The `Database` xUnit collection sets `DisableParallelization = true`: its classes run
one at a time and nothing else runs beside them. Nine classes were in it although
none injects the collection fixture and every application they start is
configured, so each owns its host and isolated schema:
`ExperimentalCapabilityGatingIntegrationTests`,
`DeploymentCapabilityProfileIntegrationTests` and
`DeploymentCapabilityProfileManifestTests` (Server Features Capabilities);
`SharingOAuth2Tests`, `PortalFacadeDiscoveryContractTests`,
`SharingOAuth2IntrospectionTests` and `SharingOAuth2CallbackE2ETests` (Server
Features Sharing OAuth and Portal); `ProposalEndpointsTests` and
`InvestigationEndpointsTests` (Server Features Admin Governance and Sharing 2;
method-name matches also run some proposal cases in Server Features Admin Layer
Management 2 and Server Features Admin Authorization 2). Before the change they
were audited for shared state: no static mutable fields, environment, culture or
file-system writes, fixed ports, shared-server fixtures, production job-substrate
hosts or sub-second timing windows. They now run in parallel with the other
classes in their shards. Cases, fixtures and assertions are unchanged; only the
collection attribute is removed.

`CapabilityManifestEndpointTests` leaves the collection too. Its one case that
starts a production job-substrate host moves to the nested
`ProductionJobSubstrate` class in the serial `JobSubstrate` collection, because
those hosts share the job tables and must run one at a time; only its name and
an assertion message change wording. The rollback cases, which build their own
hosts over substitutes, become the nested `RollbackTargets` class and run in
parallel with the default-application cases. Their bodies are unchanged.

Shard filters select by class or method name, so every case stays in the same
shards on selected PR runs and twice-daily full trunk runs. No workflow, shard
definition, required check, aggregate dependency or runner count changes. Before,
on hosted full trunk run 44183857476, the serial collection classes summed 492 of
the 514-second TRX interval in Server Features Capabilities (198 of them outside
`CapabilityManifestEndpointTests`), 239 of 530 seconds in Sharing OAuth and
Portal, and 230 of 678 seconds in Admin Governance and Sharing 2. The parallel
saving is not yet measured.

### Shared-table audit for the database collection exits

Isolated `WebAppFixture` hosts get their own schema for feature and metadata data,
but every host uses the same test database, and system tables (progress, users,
SCIM groups, security tokens, token replay, audit log, alert and ops-health
history) stay in the shared `honua` schema. A fixture configured only through
`ReplaceRequestService` also counts as unconfigured and uses the process-wide
shared server. A class leaves the serial `Database` collection only when its
assertions cannot see rows that another parallel class writes to those tables.

Fourteen more classes pass that audit and leave the collection:
`ObservabilityEndpointsTests`, `ObservabilityEventEndpointsTests`,
`OpsObservabilityEndpointsTests` and `ObservabilityAlertEndpointsTests` (Server
Features Admin Operations Endpoints); `ScimProvisioningEndpointsTests`,
`IdentityEntitlementGateTests`, `SamlBridgeEndpointsTests`,
`AdminLogoutDependencyLossTests` and `ScimAuthenticationSecurityTests` (Server
Features Identity); `ServiceSettingsAttachmentAuthoringTests`,
`OpsIncidentEndpointsTests`, `AgentSettingsApprovalTests`,
`ProcessLocalAdminStoreGuardEndpointTests` and `ExpiredGeoprocessingResultsTests`
(Server Features Admin Integrations and Automation 4). Their shared-table writes
use names, ids or keys unique to the class, and their assertions are scoped to
them. The audit also rechecked the classes above and `AdminAuthEndpointsTests`
against the shared tables and found no collision.

These stay serial: `OperationsProgressEndpointsTests` (shared server, exact
Upload and Import counts over the shared progress table),
`CacheOperationsEndpointsTests` (shared server; invalidates every cache),
`AlertPreviewFloorTests` (creates and drops a trigger on the shared
`honua.audit_log`), `CommunityOpsHealthEndpointsTests`, `OidcEntitlementGateTests`
(uses the collection fixture), and every production job-substrate host.

Cases, fixtures and assertions are unchanged, and every case stays in the same
shards on selected PR runs and twice-daily full trunk runs. No workflow, shard
definition, required check, aggregate dependency or runner count changes. Before,
on hosted full trunk run 44183857476, the moved classes summed 137 seconds of the
882-second Admin Operations Endpoints TRX interval, 169 of 380 seconds in
Identity, and 97 of 515 seconds in Admin Integrations and Automation 4, each run
one class at a time. The parallel saving is not yet measured.

### More isolated-host classes leave the serial database collection

Six more classes drop `[Collection("Database")]`: `FeatureLockCapabilityManifestTests`
and `MobileAuthAndDiagnosticsEndpointsTests` (Collaboration Mobile Identity 1),
`AlertEntitlementGateTests`, `PublishedRouteEndpointsTests` and
`CatalogDiscoveryEndpointsTests` (Console Alerts 2), and `EndpointRegistryDriftTests`
(STAC API Governance 2). None injects the collection fixture. The first five build
isolated hosts and either only read, use in-memory stores, or write shared alert
rules under per-test service ids that no case counts. `EndpointRegistryDriftTests`
only reads endpoint data sources and source files from the shared host. Only the
attribute is removed; cases, fixtures and assertions are unchanged.

These stay serial: `FieldCollectionSyncEndpointsTests` (asserts the global maximum
field-collection generation and bumps the shared `sync_generation` sequence),
`ApiSurfaceComplianceTests` (5-second per-query timing assertion), the two feature
stream snapshot and conformance classes (they assert the first change and exact
sequence on layer 0 of the shared `honua.feature_changes` table), and the import
classes that run import DDL in the shared `honua_data` schema or write fixed
`honua.tile_caches` ids.

Shard filters select by class or method name, so every case stays in the same
shards on selected PR runs and twice-daily full trunk runs. No workflow, shard
definition, required check, aggregate dependency or runner count changes. Before,
on hosted full trunk run 44183857476, the moved classes summed 127 of 415 seconds
in Collaboration Mobile Identity 1, 85 of 456 seconds in Console Alerts 2, and 180
of 320 seconds in STAC API Governance 2, each run one class at a time. The
parallel saving is not yet measured.

### Feature stream cases share one host per repeated configuration

`FeatureStreamEndpointsTests` cases on the class's shared Pro host take about 0
seconds each; cases that build their own host take 9 to 12 seconds, almost all of
it host start. Cases that repeat a configuration now share one keyed variant host
on `FeatureStreamProHost`, started on first use, so a shard whose filter skips
those cases never starts it. Each acquisition drops sessions an earlier case left, and variants keep the class
host's one-hour heartbeat so no scheduled frame lands in an ordered read.
The ten subscriber-policy cases use two variants (each anchors its cursor on its
own change); dev auth off, Community edition, the service-level access catalog and
the service-only filter catalog use one each for nine cases that read only their
own requests or events; and the
eight expired-cursor executions share one host over the fixed 50 to 60
retention-window fake store, which none of them appends to. About 20 host starts
are removed; assertions are unchanged. Cases that mutate metadata, policies,
limits or their own event store keep their own host.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check, aggregate dependency or runner count changes. Before, on hosted full trunk
run 44183857476, the class took 599 seconds in Server Features Streaming
Transports and 101, 170 and 232 seconds in Streaming Endpoints 1, 2 and 4.

### Retry-failure cases check the backoff instead of sleeping through it

On hosted full trunk run 44250783964 the three
`RoutingAsyncWebToolTests.SubmitJob_ProviderException_FailsDurablyWithoutPartialResults`
cases took 91 to 102 seconds each, 284 of the 292-second class span in GeoServices
GPServer and NAServer 1. Almost all of it was the default retry policy's 30- and
60-second backoff. `RoutingPgRoutingQueuedJobTests.ProviderFailure_FailsDurablyWithoutPartialResults`
on the routing nightly waits the same 90 seconds for each of its three tasks.
Both now use `ScheduledRetryExpediter` (Honua.TestKit). After each failed attempt
it asserts that the requeued job's stored `next_retry_at` equals the policy's
`ComputeDelay(attempt + 1)`, a check these tests did not make before. It then
makes that one retry due now, the state the backoff would reach on its own. The
real worker still claims, runs and fails every attempt, and the terminal status,
attempt count, error and empty artifacts are asserted as before.

The GeoServices GPServer and NAServer 1 fixture signals the production worker
after each committed retry visibility update. The update loop still handles
the worker's two-write requeue and checks the original retry schedule.

`EndpointRegistryDriftTests.AllEndpointRegistryEntries_AreBackedByHttpRequests`
took 86 seconds in STAC API Governance 2. It built a path regex for every
(registry endpoint, test method body) pair through `Regex.IsMatch`, whose static
cache holds 15 patterns, so every pattern was parsed again for every body. Each
endpoint's pattern and each verb's call-site patterns are now built once, and
the verb result is shared per body. The matching rules are byte-identical. The
class's read-only host is now one class fixture instead of one host per case.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs, twice-daily full trunk runs and the routing nightly. No workflow, shard
definition, required check name or aggregate `needs` changed.

### Proposal endpoint cases share one host

On hosted full trunk run 44250783964 `ProposalEndpointsTests` started one
`WebAppFixture` for each of its 74 cases: 131 seconds of case time across a
324-second class span in Server Features Admin Governance and Sharing 2, the
slowest server shard of that run. One class-fixture host now serves every case.
Each case starts with the guardrail tier, executor and notifier counts and
identity-provider token that a fresh host had. Proposals, operation instances,
API keys and console content persist, and each case seeds and reads its own ids.
`ListProposals_ReturnsActiveProposals` now looks for its own seeded proposal by
id instead of accepting any non-empty list.

The admin sign-in endpoints admit five requests a minute per client address, and
the class signs in eleven times. Each browser session therefore gets its own
address from a test-only header that the class host maps in a startup filter
registered after the sign-in helper's fixed per-host address. The shared helper
itself is unchanged.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### MCP endpoint, modern-bearer and guidance cases share one host per class

On hosted full trunk run 44250783964 three MCP classes started a host for every
case. `McpEndpointIntegrationTests` took 112 seconds of case time over a
262-second span in MCP Endpoints. `McpModernBearerAuthenticationTests` took 70
over 127 seconds in MCP and Sessions 1. `McpAuthenticationGuidanceTests` took 26
over 62 seconds in MCP and Sessions 2. All three run in the serial Database
collection, so these host starts ran one after another. Each class now has one
class-fixture host and stays in its collection.
- MCP endpoint cases open their own sessions. The per-principal cap of 32 evicts
  the least-recently-used session, never one a case just opened.
- Modern-bearer cases mint their own tokens. Each gets a fresh `jti` unless a
  case pins a fresh id, so one case's replay record cannot admit or refuse
  another case's token. The zero-skew expiry case keeps its own host.
- Guidance cases only read responses on fresh clients.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Read-only toolbox and OGC Maps validation cases share one host per class

On hosted full trunk run 44250783964 these three classes started a host for every
case, inside serial collections:
- `ToolboxTranslationEndpointTests`: 34 cases, 66 seconds, Migration 1.
- `OgcMapsParameterValidationTests`: 22 cases, 46 seconds, OGC API Maps
  Rendering and Records 2.
- `OgcMapsErrorHandlingTests`: 23 cases, 34 seconds, OGC API Maps Rendering and
  Records 1.

Their cases only POST manifests to the stateless translation validator (which
reads the process catalog and stores nothing) or render and read with GET. Each
class now has one class-fixture host. The toolbox host keeps its small
edit-payload cap, and the maps classes use the default host they used before.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Versioning and middleware-header cases share one host per class

On hosted full trunk run 44250783964 these classes started a host for every case
in the serial Database collection:
- `VersionManagementServerEndpointTests`: 42 cases, 86 seconds of case time over
  a 204-second span, GeoServices Versioning 1.
- `CorrelationIdMiddlewareTests`: 12 cases, 23 seconds, Admin and
  Infrastructure 1.
- `Infrastructure.Middleware.SecurityHeadersMiddlewareTests`: 6 cases, Admin and
  Infrastructure 4.

Each class now has one class-fixture host.

Versioning cases create, lock, reconcile and post their own versions; every
write to the global versioning tables is keyed by that case's version id. Five
cases publish the managed branch-versioned layout, which renames the test
service in the metadata graph. Every case therefore first restores the graph
the host was seeded with, and the revision only moves forward.

The middleware cases send their own request headers and read the response
headers.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Admin authentication browser sessions share one host per configuration

On hosted full trunk run 44271438677 the Server Features Admin Authentication
and Credentials shard ran `AdminAuthEndpointsTests` with an application per
case:
- `AdminAuthEndpointsTests+BrowserSessions`: 27 cases, 240 seconds of case time.
- `AdminAuthEndpointsTests+GrpcBrowserSessions`: 12 cases, 133 seconds.
- `AdminAuthEndpointsTests`: 33 cases, 266 seconds; 7 of them use the default
  application.

These classes now take one class-fixture host per configuration: the default
application, the password-token application and the generic OIDC callback
application. `BrowserSessions` boots 4 applications instead of 27,
`GrpcBrowserSessions` 2 instead of 12, and the outer class 27 instead of 33.
Cases with their own configuration or stub identity provider keep their own
application.

Every case uses fresh clients, so cookies, CSRF tokens and sessions stay per
case, and each client gets its own client address from a test-only header that
the host maps in a startup filter (the same technique as
`ProposalEndpointsTests`, now shared as `PerClientAddress`). The in-process
transport otherwise leaves the address empty, which skips the per-address
limits; with an address per client, the sign-in endpoints' five requests a
minute and the failed-password block now apply, per case.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Credential-expiry cases start with a 3-second lifetime

Expiry cases in `FeatureStreamEndpointsTests` (OData poll, default-tenant OData read,
WebSocket and SSE stream, Kestrel close), `SensorThingsStreamEndpointsTests`
(`ObservationsStream` over WebSocket and SSE) and `McpModernBearerAuthenticationTests`
(zero-skew reuse) issued portal credentials or JWTs that lived 6 to 30 seconds and
waited them out on the real clock. The product checks expiry against the real
clock, so the tests still do, but the credential under test now lives 3 seconds.
Each case warms the host with a separate long-lived credential first.
`ExpiringCredentials` in `Honua.TestKit` runs the step that needs the credential
valid. If a slow runner outlives the credential before that step finishes, the
helper repeats the step with a 10-second and then a 30-second lifetime, so no case
asserts on a credential that ended before its setup. The stream cases record every
frame received during setup, including frames from discarded attempts. After setup
they check those frames again for the other tenant's data and for the marked change
or observation, so a retry cannot hide a leak. Every assertion is unchanged.

Measured on the build host (Release, the same 66 cases, 66/66 passing on every
run, head twice and base once):
- The six FeatureStream expiry cases: 127.3 to 63.8-66.4 seconds.
- The two SensorThings expiry cases: 44.3 to 10.3-10.4 seconds.
- The MCP zero-skew case: 33.9 to 9.1-10.0 seconds.
- The revocation twins are unchanged.

These cases run in Server Features Streaming Endpoints 1, 2 and 4, Server Features
Streaming Transports, SensorThings 5 and MCP and Sessions 1. Shard filters are
unchanged, so every case runs in the same shards on selected PR runs and
twice-daily full trunk runs. No workflow, shard definition, required check name or
aggregate `needs` changed.

### Isolated test host boot and teardown overhead

Three changes cut per-host boot and teardown cost across all isolated `WebAppFixture`
integration suites without reducing any assertion:
- `ResourceLeakDetector` (#9097): when `_trackedResources` is empty, `ScanForLeaksAsync`
  returns the same empty `ResourceLeakScanResult` immediately instead of running two
  forced blocking gen-2 collections and `GC.WaitForPendingFinalizers()` on every
  Test/Development host stop. Scans with tracked resources are unchanged.
- `PostgresFixture` (#9110): `GlobalSharedState` retains its shared data source and
  migrated pool across a 30-second idle grace (`ReleaseIdleNowAsync` plus `ProcessExit`
  hook) instead of tearing down and re-running extensions/migrations between sequential
  isolated cases. Private `PostgresSharedState` instances keep `TimeSpan.Zero`.
- Request Delegate Generator (#9123): enabled `EnableRequestDelegateGenerator` on
  `Honua.Server` and the 11 endpoint Web SDK libraries so Minimal API delegates are
  source-generated at compile time rather than JIT-compiled on each isolated host boot.
- `McpSessionManager.StopAsync`: made idempotent under concurrent host stops exposed by
  faster teardown.

Measured on the build host (Release, 3 interleaved base/head rounds of the
`Server Features Capabilities` shard filter, 100/100 passing on all 6 runs):
- Shard wall time: base 524.8 / 477.5 / 370.2 s (median 477.5 s, 7.96 min) to
  head 74.8 / 67.6 / 56.1 s (median 67.6 s, 1.13 min); median saving 6.83 min
  (5.24 min on the lowest-load pair).
- Per-class median TRX case time (total 1039.0 to 178.3 s):
  `CapabilityManifestEndpointTests` (40) 411.8 to 45.2 s;
  `ExperimentalCapabilityGatingIntegrationTests` (34) 263.8 to 35.2 s;
  `DeploymentCapabilityProfileIntegrationTests` (11) 151.8 to 36.6 s;
  `CapabilityManifestEndpointTests+RollbackTargets` (5) 111.9 to 31.1 s;
  `DeploymentCapabilityProfileManifestTests` (4) 80.8 to 28.4 s;
  `CapabilityManifestEndpointTests+ProductionJobSubstrate` (1) 9.7 to 1.0 s;
  `CapabilityGateEndpointFilterTests` (3) 0.2 to 0.3 s;
  `CapabilityGateOpenApiDocumentTransformerTests` (2) 0.4 to 0.3 s.

Shard filters are unchanged, so every case runs in the same shards on selected PR runs
and twice-daily full trunk runs. No workflow, shard definition, required check name or
aggregate `needs` changed.

### Measured SOAP shard packing

The SOAP and SOAP Progress shards now share one GeoServices GPServer SOAP
runner. Their exact filter union, affected-path union, project, tier selection,
CPU setting, report flags and 22-minute test / 32-minute job budgets are
preserved. The method ownership guard covers the combined shard; a frozen
packing contract checks both former filters and execution settings. MVT keeps
its separate coverage partition.

Ordinary full runs [44819335862](https://github.com/honua-io/honua-server/actions/runs/44819335862),
[44921455496](https://github.com/honua-io/honua-server/actions/runs/44921455496)
and [45000554030](https://github.com/honua-io/honua-server/actions/runs/45000554030)
passed all 81 SOAP cases with no skips. Their two shard process-wall sums were
35, 26 and 17 seconds; even the sum of the individual maxima is 35 seconds,
2.7% of the unchanged 1,320-second budget. The last full run failed elsewhere;
its two SOAP shards passed. The combined dispatch rank of 5.0 minutes is an
explicit packing estimate (the sum of the former ranks), pending a qualifying
combined-filter job measurement. This reduces the full matrix from 203 to 202 jobs,
and a GPServer-affected selection loses one runner. In the last run the two
jobs occupied 203 runner-seconds (3.38 minutes), including 19 seconds in test
steps. Combined-job runner-minute savings remain unmeasured until a live run;
these figures do not claim a PR critical-path improvement. The old 132-to-123
proposal requires fresh qualification for its other groups.

### OData client integration host lifetime

`ODataClientIntegrationTests` initializes its Pro-licensed, `odata.yaml`-seeded
application once per class instead of once per case. All 44 cases only read the
seed. Each case still creates and disposes a fresh HTTP client and creates its
own OData context; all 98 assertions and endpoint attributes are unchanged.
The existing `Database` collection still serializes these cases. The separate
`ODataClientCertificationTests` fixture retains its own schema, authentication
configuration and mutable certification data.

Every case remains in the OData Client Certification shard on selected PR runs
and twice-daily full trunk runs. Runner count, shard filters, required check names
and the required aggregate's `needs` contract are unchanged. No workflow or shard
definition changes are included.

Two paired build-host runs of the complete 56-case owning shard passed with no
failures or skips and identical test identities. Median summed class duration
fell from 14.80 to 2.95 seconds. Median shard wall time, including fixture setup
and disposal, fell from 63.57 to 21.62 seconds, saving 41.94 seconds (0.70 runner
minutes) with one runner before and after. Individual baseline/candidate shard
pairs were 68.24/18.56 and 58.89/24.69 seconds. These measurements exclude build
time and do not claim a reduction in the parallel PR Gate's critical path.

### Observability endpoint fixture initialization

`ObservabilityEndpointsTests` starts its default private host only in the five
migration/error cases that use it. The five telemetry cases retain their separately
configured private hosts and no longer start an unused default host. All 10 cases,
79 assertions, endpoint metadata and configuration isolation are retained.

Two interleaved base/head Release runs of the unchanged owning Admin Observability /
Operations shard on one build-host runner passed the same 123 cases in each run
(zero skipped). Fixture-inclusive TRX session seconds were 92.572 / 93.402 at base
and 86.363 / 88.605 at head: median 92.987 to 87.484 seconds, saving 5.503 seconds
(0.092 runner-minutes). Both pairs improved, by 6.209 and 4.797 seconds.
`ObservabilityEndpointsTests` summed TRX case seconds were 13.497 / 17.553 at base
and 44.454 / 45.573 at head (medians 15.525 to 45.013). Those class figures are
not directly comparable: moving initialization from `IAsyncLifetime.InitializeAsync`
into the five consuming test bodies makes that initialization visible in case time.
The fixture-inclusive shard measurement includes setup and teardown in both versions;
compilation is excluded. This is a small measured shard saving, not evidence of an
end-to-end CI critical-path improvement.

The shard filter, test selection, workflows, required check names and aggregate
`needs` contract are unchanged. Every case still runs in its existing selected PR
shard and full trunk runs; no assertions or gate coverage move to another schedule.

### OIDC bearer authentication host lifetime

`OidcAuthenticationTests` reuses a lazy class host for seven bearer-token read
cases with identical OIDC settings. Each case still creates and disposes its own
HTTP client and sets its own request headers and token. The existing database
collection serializes the cases. xUnit disposes the shared factory after the
class finishes. Replay, certificate, production-mode, tenant-specific and
mutating admin scenarios retain their private factories and configuration.

All 27 OIDC cases, 125 assertion calls and endpoint metadata remain unchanged.
Every case stays in the existing Admin & Infrastructure 1 shard on selected PR
runs and twice-daily full trunk runs. Runner count, shard filters, required check
names and the aggregate's `needs` contract are unchanged. No workflow or shard
definition changes are included.

Four interleaved build-host runs of the complete 39-case owning shard passed
without failures or skips and with identical case outcomes. Median summed OIDC
class duration fell from 26.39 to 22.24 seconds; both pairs improved. Median
fixture-inclusive shard wall time fell from 48.45 to 41.50 seconds, saving 6.95
seconds (0.116 runner minutes), with one runner before and after. Individual
baseline/candidate shard pairs were 55.15/41.01 and 41.76/41.99 seconds: the second
pair was slightly slower, so the shard saving is variable. Build time is excluded;
these results do not establish a reduction in the parallel PR Gate critical path.

### Streaming projected-bbox handshake host lifetime

`FeatureStreamEndpointsTests.Stream_WithProjectedBboxLayer_AllowsSseHandshake`
uses the existing class-owned Pro streaming application and a fresh admin client.
It previously started a second private Pro application for the same handshake.
The shared host resets sessions between cases, and the projected layer is seeded
before requests. The request URL, Accept header, five-second cancellation, status
and content-type assertions are unchanged. All 193 assertion lines and 134
selection/endpoint metadata lines in the changed file are retained. Cases with
specialized configuration, real credential expiry or mutable services retain
their existing hosts and timing bounds.

Four interleaved baseline/candidate runs of the complete unchanged Server
Features Streaming Endpoints 2 shard passed the same 23 cases, with zero skips.
Summed class seconds were 30.821524 / 27.345450 and 27.160965 / 26.675210
(baseline / candidate); the median was 28.991244 -> 27.010330 seconds.
Fixture-inclusive TRX shard intervals were 47.529324 / 43.602161 and
43.948600 / 43.279365 seconds. Their median was 45.738962 -> 43.440763 seconds,
a measured saving of 2.298199 seconds (0.038303 runner-minutes), with one runner
before and after. Both pairs improved, with different magnitudes; this small
sample does not establish PR critical-path savings. Compilation/process time is
excluded from the shard saving. This removes one redundant application boot
without splitting the suite or reducing its assertions.

Every case remains in the same selected PR shard and twice-daily full trunk runs.
Shard filters, workflow files, required check names and the required aggregate's
`needs` contract are unchanged.

### Streaming baseline-capability seed lifetime

The eight baseline-capability cases in `FeatureStreamEndpointsTests` borrow the
feature schema of the already initialized class fixture. Each case retains its
private application, mock feature reader, metadata provider, license configuration
and disposal. These GET requests read capabilities through the case-local mock
reader; they do not mutate the shared feature data. The Database collection still
serializes the cases, and the class fixture owns schema cleanup after every
borrower is disposed. The one-second lookup budget, cancellation behavior, all
assertions and endpoint metadata remain unchanged.

Every case remains in its existing selected PR shard and full trunk runs. No
workflow, shard filter, required check name, runner count or required aggregate
`needs` contract changes.

Two interleaved base/head Release runs of the complete unchanged Streaming
Endpoints 4 shard passed the same 28 cases in every run (zero skipped), on one
runner before and after. Summed `FeatureStreamEndpointsTests` TRX case seconds
were 33.220638 / 31.768553 at base and 28.079612 / 25.437050 at head: median
32.494596 to 26.758331 seconds. Fixture-inclusive TRX session seconds were
49.478414 / 47.762028 at base and 45.713160 / 40.702234 at head: median
48.620221 to 43.207697 seconds, saving 5.412524 seconds (0.090209 runner-minutes).
Both pairs improved, by 3.765254 and 7.059794 seconds. Compilation is excluded;
this is a measured shard saving, not evidence of an end-to-end CI critical-path
improvement.

### Streaming unsupported-owner seed lifetime

The nine `Capabilities_UnsupportedOwner_OmitsLayer` theory rows keep private Pro
application hosts and independently patched metadata providers while borrowing the
already initialized class fixture's feature schema. These requests reject the
unsupported metadata owner before reading features; they do not write features or
read the stream event log. Each borrower is disposed within its case, before the
class fixture drops its owned schema. The SSE and WebSocket rejection cases keep
their existing private fixtures and all assertions and test attributes are intact.

The shard filter, selected cases, workflows, required check names and aggregate
`needs` contract are unchanged. Every case continues to run in its selected PR
shard and full trunk runs. No test or gate coverage moves to another schedule.

Two interleaved base/head Release runs of the complete Streaming Endpoints 2 shard
passed the same 23 cases in each run, with zero failures or skips. Summed class
case medians fell from 26.418 to 22.098 seconds. Fixture-inclusive TRX session
seconds were 42.367 / 40.345 at base and 37.157 / 37.190 at head: median
41.356 to 37.173 seconds, saving 4.183 seconds (0.070 runner-minutes), with one
runner before and after. Both pairs improved, by 5.210 and 3.155 seconds.
Compilation is excluded; this small shared-host measurement does not establish
a reduction in the parallel PR Gate's critical path.

### WPS canonical rejection host lifetime

The four `Wps20EndpointsTests.CanonicalExecute_UnsupportedContract_IsRejectedBeforeSubmission`
theory cases share a lazy class-owned application with the same canonical process
catalog and job-service substitute. Every case creates a fresh client and clears
received job-service calls before sending its request. Other configured hosts
retain their isolation. All 125 assertion lines and 154 selection/endpoint
metadata lines are unchanged, including the bad-request response and assertion
that rejected contracts never submit a job.

Four interleaved baseline/candidate runs of the complete unchanged WFS 6 owning
shard passed the same 12 cases, with zero skips; 11 belong to the WPS class.
Summed class seconds were 8.783987 / 4.083868 and 7.195504 / 3.723192
(baseline / candidate); the median was 7.989745 -> 3.903530 seconds.
Fixture-inclusive TRX shard intervals were 27.592498 / 22.787546 and
25.240013 / 27.248837 seconds. Their median was 26.416256 -> 25.018192 seconds,
a measured saving of 1.398064 seconds (0.023301 runner-minutes), with one runner
before and after. Both class pairs improved, but the second full-shard pair was
slower; this variable small sample does not establish PR critical-path savings.
Compilation/process time is excluded. This removes three redundant configured
application boots without splitting cases or reducing assertions.

Every case remains in the same selected PR shard and twice-daily full trunk runs.
Shard filters, workflow files, required check names and the required aggregate's
`needs` contract are unchanged.

### WPS invalid-contract reference host lifetime

Four `Wps20EndpointsTests.ConformanceEcho_InvalidContract_DoesNotResolveReference`
theory rows share a lazy class-owned application with the unchanged reference
allowlist. Every row creates and disposes a fresh client; the Database collection
remains serial. Each request must still fail contract validation before reference
resolution, report the expected locator, and omit the DNS-resolution failure text.
The separate unresolvable-reference case retains its private application. All
124 assertion lines and 154 endpoint, operation and test metadata lines are unchanged.

Every case remains in the same selected PR shard and full trunk runs. No workflow,
shard filter, required check name or aggregate dependency changes.

Two interleaved base/head Release runs of the unchanged complete WFS1 shard each
passed the same 11 cases, with zero skipped and identical test identities/outcomes.
Summed class seconds were 8.860 / 8.573 at base and 5.586 / 5.779 at head (median
8.716712 to 5.682745 seconds). Fixture-inclusive TRX shard seconds were 21.303095 /
21.066283 at base and 18.091381 / 18.253105 at head (median 21.184689 to 18.172243).
This saves 3.012446 seconds, or 0.050207 runner-minutes, with one runner before and
after. Both pairs improved, by 3.211714 and 2.813178 seconds. Compilation is excluded;
these small local shard measurements do not establish parallel PR Gate critical-path
savings. The other WPS shard selections and their private hosts remain unchanged.

### WFS configured metadata seed ownership

Five read-only `Wfs20EndpointsTests` Pro applications borrow the existing class
schema while preserving their individual metadata providers, licenses, services,
clients and disposal. Mutation cases retain private schemas. All 440 assertion
lines and 400 metadata lines remain; the WFS Endpoints owning shard runs every
case on selected PRs and twice-daily full trunk runs. See
[WFS 2.0 endpoint fixture lifetime](gate-model.md#wfs-20-endpoint-fixture-lifetime).
No workflow, shard filter, runner count, required check name or aggregate `needs`
dependency changes.

Two interleaved base/head runs of the full 99-case WFS Endpoints shard passed
identical cases with zero skips. Class sums were 32.012600 / 31.165176 seconds
at base and 31.582156 / 29.329947 at head: median 31.588888 to 30.456051 seconds.
Fixture-inclusive TRX session pairs were 44.316083 / 44.266786 and
43.926958 / 42.751009 seconds: median 44.121521 to 43.508898 seconds, saving
0.612623 seconds (0.010210 runner-minutes), one runner before and after.
Both pairs improved, but the first by only 0.049297 seconds; this is a modest
observed saving with shared-host variability, not a demonstrated PR critical-path
improvement. Compilation is excluded. Process times were 426.106 / 56.836 seconds
at base and 57.608 / 60.113 at head; the first baseline included a cold build.

### Capability plugin discovery schema lifetime

The six `CapabilityManifestEndpointTests.GetManifest_FilePluginWriters_UsesLicensedEnabledDiscovery`
theory cases retain separate applications, service registries, license editions
and fresh clients. Each application borrows the initialized class fixture's seeded
schema rather than creating and seeding another schema. The fixture owner outlives
all serial class cases; borrowers never drop its schema. Requests only read the
capability manifest. Per-host metadata and plugin registries remain separate.
The schema-reuse change preserves every assertion. Both measured variants
include the GeoParquet manifest assertion correction that trunk now carries;
their only difference is the six plugin hosts borrowing the initialized schema.

Four interleaved base/head Release runs of the complete Server Features
Capabilities shard passed the identical 103 cases with no skips on one runner
before and after. Fixture-inclusive TRX session seconds were 88.657 / 67.712
at base and 65.149 / 74.636 at head: median 78.184 to 69.893 seconds,
a measured saving of 8.292 seconds (0.138 runner-minutes). The first pair
improved by 23.508 seconds; the second regressed by 6.925 seconds, so these
runs show substantial variability rather than a consistent per-run saving.
The target class's 43 cases summed to 56.155 / 39.371 seconds at base and
42.806 / 45.035 at head: median 47.763 to 43.920 seconds. Compilation and
process startup are excluded from the shard measurement; process durations
were 205.689 / 151.173 seconds at base and 158.666 / 164.087 at head.
No end-to-end PR Gate critical-path reduction is claimed.

### Experimental capability manifest schema lifetime

The thirteen separately configured manifest GET cases in
`ExperimentalCapabilityGatingIntegrationTests` borrow the seeded schema from the
existing eager `DisabledPostureHost` class fixture. Each case still starts its
own application with the same global/per-capability flags, registry composition,
Pro edition, tenant, services and fresh client. Per-host metadata stays separate.
The owner outlives the serial cases; borrowers do not create, seed or drop another
schema. The mTLS-enabled admin case retains its private schema and application.
All 32 assertion lines, four assertion-helper calls, 55 test metadata lines and
14 test methods are unchanged. No parallel collection or polling changes apply.

Four interleaved base/head Release runs of the unchanged Server Features
Capabilities shard passed the same 103 cases with zero skips, including 34 target
class cases, on one runner before and after. Target class summed case seconds
were 34.685 / 27.959 at base and 28.704 / 24.533 at head: median 31.322 to
26.618 seconds. Fixture-inclusive TRX session seconds were 85.201 / 60.185 at
base and 72.939 / 62.115 at head: median 72.693 to 67.527 seconds, saving
5.166 seconds (0.086 runner-minutes). The first shard pair improved by 12.262
seconds; the second regressed by 1.930 seconds, so variability remains substantial.
Both external benchmark variants include the same neighboring GeoParquet manifest
assertion correction required after trunk added the writer; that correction is
only a common measurement precondition and is not committed in this suite change.
Process durations were 646.397 / 179.941 seconds at base and 198.986 / 154.497
at head, including compilation (the first run was cold). They are separate from
the fixture-inclusive session figures. No PR Gate critical-path saving is claimed.

Every case remains in the same selected PR shard and twice-daily full trunk runs.
Shard filters, workflow files, required check names and the required aggregate's
`needs` contract are unchanged.

### Deployment profile folder schema lifetime

`DeploymentCapabilityProfileIntegrationTests.FolderApplications` retains one
private application per configured capability. The first initialized application
owns the common feature-data seed; the other three borrow its schema. Each host
still installs its own deployment profile, metadata graph and clients. Folder-path
cases only read the seed. Borrowers stop before the owner drops the schema.
The three standalone profile cases retain their private schemas and hosts.

Four interleaved complete Capabilities shard runs passed the same 103 cases,
including all 11 class cases, with zero skips. Class test-duration median changed
from 32.864138 to 30.715827 seconds. Fixture-inclusive shard median changed from
62.444438 to 60.571979 seconds, saving 1.872459 seconds / 0.031208 runner-minutes
on one runner before and after. Individual shard pairs were 63.240212/56.698306
and 61.648663/64.445651 seconds: the second pair regressed 2.796988 seconds, so
the modest median saving is subject to shared-host variability. Cold compilation
and process time are separate; no PR critical-path saving is established.

All test methods, assertions, theory rows and endpoint metadata remain unchanged.
Every case remains in the Capabilities shard on selected PRs and full trunk runs.
No workflow, shard filter, runner count, required check name or aggregate `needs`
contract changes.

### Durable GPServer feature schema lifetime

Six read-only geometry and unavailable-workspace cases in
`GPServerDurableRuntimeTests` borrow the feature schema seeded by their class
fixture. Each case still starts and disposes its own production job host,
clears the global job tables through `PostgresJobHost.StartAsync`, and asserts
that the Postgres job store, queue and execution service are registered. The
schema owner uses the default test composition without production job workers.
The job-substrate collection remains serial. Restart, migration, ownership,
authentication, scoped-key and node-local cases retain their existing lifetimes.
Every assertion, request and endpoint/operation attribute is unchanged.

All cases remain in the complete GeoServices GPServer Durable and Aliases shard
on selected PR runs and full trunk runs. No workflow, shard filter, required
check name or aggregate dependency changes.

Two interleaved base/head Release pairs of that complete shard passed the same
50 cases in every run, with zero failures or skips (17 cases in the target class).
Summed class seconds were 29.661 / 29.573 at base and 28.799 / 28.241 at head:
median 29.617 to 28.520 seconds. Fixture-inclusive TRX session seconds were
52.821 / 52.634 at base and 52.385 / 50.043 at head: median 52.727 to 51.214
seconds, saving 1.513 seconds (0.025 runner-minutes). Both pairs improved, by
0.435 and 2.591 seconds. Runner count stays one. These are small shared-host
measurements; compilation is excluded, and no parallel PR Gate critical-path
improvement is claimed.

### FeatureServer unsupported-control host lifetime

The nine `ApplyEdits_UnsupportedEsriControl_IsRejectedInsteadOfDropped` rows use
the existing class application. Validation rejects these controls before invoking
the edit handler. Each row retains its unique feature name, HTTP error and message
assertions, and a separate query proving that the rejected edit created no feature.
Successful mutation cases retain their private applications and schemas. The class
collection remains serial and all assertions and test attributes are preserved.

Shard filters, selected cases, workflows, required check names and the aggregate
`needs` contract are unchanged. Every case continues in its selected PR shard and
full trunk runs; no coverage moves to another schedule.

Four interleaved baseline/candidate runs of the complete unchanged Core Endpoint
Mutations shard passed the same 61 cases, with zero failures or skips. Median
class-body time fell from 51.067914s to 45.244433s. Median fixture-inclusive TRX
`Times` interval fell from 68.107526s to 62.059710s, saving 6.047816s
(0.100797 runner-minutes). Both pairs improved: 66.604309s to 62.258366s and
69.610743s to 61.861054s. Runner count remains one; compilation and process startup
are excluded. These are paired shard measurements, not an aggregate critical-path
claim or a sum with other independently measured candidates.

### GeoServer container idle grace

`GeoServerFixture` holds the shared GeoServer container for a two-minute idle
grace after its last release, the same pattern the shared web host uses.
`GeoServerCuratedImportIntegrationTests` (Database collection) and
`GeoServerLiveImportIntegrationTests` (job substrate collection) run in
different serial collections, so the reference count reached zero between them
and each class booted its own container (about 30 to 55 seconds each on CI and
the build host). A later class now reuses the running container; curated seed
state is kept with it. The live cases assert only positive discovery counts and
a completed dry run, so the curated resources do not change what they check.
No test file or assertion changes. When the test host exits without raising
ProcessExit, the Testcontainers reaper (enabled in every workflow) removed the
idle container within 15 seconds in the build-host measurement.

Build-host runs of the complete unchanged Migration Source Imports shard filter
(88 passed, 4 skipped per run): the curated class's fixture-inclusive span was
44.202 / 58.267 seconds on the base and 4.141 / 4.346 seconds on the candidate,
which is the removed container boot. TRX shard walls were 343.845 / 263.147
seconds (base) and 252.611 / 186.485 seconds (candidate); the first base run
also had one unrelated failure (`GeoServerImportEndpointTests`, a disposed
CancellationTokenSource in a class that does not use this fixture). The shared
build host was loaded, so the wall pairs are noisy; the dependable saving is one
GeoServer boot, about 40 seconds (0.67 runner-minutes) per run of the shard, with
one runner before and after. On scheduled full run 45038465540 the shard spent
136 of its 229 job seconds running tests.

Every case remains in the same selected PR shard and twice-daily full trunk runs.
Shard filters, workflow files, required check names and the required aggregate's
`needs` contract are unchanged.

### MCP LLM smoke credential admission

The advisory full-CI `MCP LLM Smoke Tests` job checks the configured Bedrock role
before checkout, server setup or SDK builds. Without
`HONUA_BEDROCK_SMOKE_ROLE_ARN`, PostgreSQL is not started and the job reports an
explicit skip in its log and summary. With a role, the existing authenticated
server, SDK smoke command, OIDC credentials and transcript upload remain active.
The job retains its certification dependency and advisory status.

Run 45159280398/job 137667996720 spent 572 seconds in MCP setup and 22 seconds
starting PostgreSQL before discovering the smoke script was absent. The previous
ten-minute cap cancelled post-action cleanup. The 33-minute job limit now gives
MCP setup 14 minutes, the smoke command six minutes with ten seconds to exit
before forced termination, and transcript upload two minutes. The smoke step has
a seven-minute runner limit. The configured-role path reserves four minutes
outside its step limits for runner and service startup and post-job cleanup.
With a role and an SDK ref that lacks the smoke script, the job reports the skip
after checking the SDK manifest. It skips the Python, Honua Server and SDK build
setup, role assumption and transcript upload. Certification mode still uses the
SDK certification script. A hosted after-change duration still requires a
qualifying run.

### Watchdog startup reconciliation retry

The Caching, File Storage, Styling and Infrastructure shard includes the watchdog
startup retry integration test. The test advances an injected clock through the
five-second retry delay, verifies the second suspend attempt succeeds, and
checks that the reconciliation task has no exception.

### Tile Export Durable Recovery

The Server Features Miscellaneous 7 shard includes Tile Export Durable Recovery.
Its fixture connects the PostgreSQL job queue and worker with the same in-process
wake signal used by the server host.
