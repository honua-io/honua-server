# CI Gate Model

The prior-reader compatibility job was removed on 22 November 2026. Migrations are forward-only, idempotent and tested on an installed stack; CI does not certify rolling rollback to an older reader. Required check names and the remaining PR Gate dependencies are unchanged.

Every source path has at least one shard owner inside the PR time budget. `test-pr-owner-coverage.py` fails when a path's owners are all over budget.

#7678 split the nine over-budget shards that were still the only owners of 61 source paths (Admin Catalog and Configuration, Admin Network and Jobs, Admin Platform and Connections, OData Mutations and Batch, Miscellaneous 1 and 2, Cloud and Streaming Import, SensorThings, gRPC Protocol and Scene) into 28 children from the TRX class spans of full run `43580101834`. Classes longer than a child's 5.5-minute share are cut at method boundaries, and classes with no shard time in that run (Tier Fast or Slow) stay in the last child, which also owns future tests. Child ranks are estimates in `full-run-capacity-contracts.json` until the next passing full run measures them. Miscellaneous children continue that family's numbering (3 to 8).

Scheduled full run `43819793722` measured four of those children above ten
minutes including setup: Miscellaneous 3 (841 seconds), Miscellaneous 4 (603),
gRPC 1 (646), and gRPC 2 (810). They are replaced by Miscellaneous 9–13 and
gRPC 3–7, bringing the matrix to 225 jobs. The cuts balance TRX start/end
intervals, including per-test lifecycle time that the reported test duration
omits. Each estimate includes the previous job's non-interval overhead (at
least four minutes) plus thirty seconds of margin. Estimated job durations are
8.3–8.7 minutes; a scheduled full run must still confirm timing, images and TRX
coverage before the issue closes. Each residual child owns future methods;
projects, source triggers, timeouts and report ownership are conserved.

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
refresh measured all 26 children (see "Measured ranks" below).

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


> Defines the five-tier quality gate model governing all CI workflows across the Honua project.
> Last updated: 2026-11-12

The shard retry supervisor captures cancellation before spawning a test process. Cancellation received during startup is replayed once the child is available, with bounded escalation for TERM-ignoring descendants. The CI router exercises both TERM and INT during child creation.

## Tier Definitions

| Tier | Purpose | Characteristics | Merge-blocking |
|---|---|---|---|
| **PR** | Deterministic pre-merge confidence | Fast, path-filtered when possible, no flaky external dependencies | Yes |
| **nightly** | Broad regression and compatibility coverage | Expensive, long-running, external-system-heavy, certification-style | No |
| **release** | Packaging and release certification | Publish/package/sign/smoke-test tied to tags or release branches | No (for routine PRs) |
| **deploy** | Environment promotion and post-apply validation | Manual or protected-branch workflows tied to environments | No (for routine PRs) |
| **maintenance** | Repo automation and housekeeping | Version automation, metadata updates, scheduled hygiene | No |

## Governing Rules

Draft pull requests run Format, Clean code, and Repository hygiene only; optional PR verification and PR Gate heavy jobs wait for `ready_for_review`. Optional verification cancels its previous run on conversion back to draft. PR Gate concurrency separates draft and ready events for each PR, so a delayed draft event cannot cancel the full ready gate; newer events in the same readiness state still cancel superseded runs. The stable `PR Gate` aggregate still runs with `always()` and accepts skipped heavy dependencies only on draft PR events after Format succeeds. Failure, cancellation and missing results still fail; ready PRs require all dependencies to succeed. A draft run names its aggregates `PR Gate (draft)` and `PR Gate / Affected shards (draft)`, so the required contexts stay expected until a ready run reports them; GitHub creates the ready run's aggregate checks only after their needs finish, and a draft success under the required name let a just-readied PR merge in that window. `ready_for_review` starts the complete verification. Observers classify a run with only the draft aggregate as `source-run-draft-admission`. Manual and trunk verification retain their existing coverage.

NuGet audit advisories (NU1901-NU1904) never fail a restore or build: `Directory.Build.props` lists them in `WarningsNotAsErrors` (projects append to that list), so a newly published advisory shows as a restore warning and `scripts/ci/dotnet-restore-retry.sh` treats any remaining restore failure as a retryable feed error. Advisories stay tracked by the NuGet vulnerability scan in `security-nightly.yml`, which annotates High/Critical findings and opens a tracking issue without failing the job (an unreadable report still fails); bumps are ordinary dependency work.

1. **PR gates must be deterministic and directly actionable by the author.** If a check fails, the author must be able to reproduce and fix it locally without external system access.

2. **External conformance suites, soak tests, and long security scans do not belong in the PR lane.** These run on schedule or manual dispatch only.

3. **Release and deploy workflows remain strict** but do not burden everyday feature merges.

4. **New checks default to nightly** unless explicitly justified as PR-blocking. Justification requires: deterministic behavior, sub-5-minute runtime, and author-actionable failure messages.

   A nightly suite may also run as an informational, path-filtered PR check when its nightly reds are PR-catchable regressions. The Cloud Integration Harness Local Substrate job runs on PRs that touch migrations, the Postgres stores the schema guard drives (raster included), the local process pool or the server host composition: 9 of its 14 nightly runs from 10 to 22 November failed on trunk changes no PR had run. It is not a required check.

   **Named PR checks — `PR Gate`, `Review Gate`, and Repository hygiene.** Together, `PR Gate` and `Review Gate` remain the required PR lane on `trunk`: unprivileged verification and trusted exact-head admission. Verification uses an affected-scope managed build producer, selected shard consumers, and independent Fast/architecture, format and CI-router jobs. The producer retains the server boot/migration smoke. Fast, architecture and governance verification remain required on PRs and build independently alongside the producer. Native GDAL/PDAL execution proofs and import/export fidelity run in `ci.yml`, whose aggregate requires the runtime-proof job whenever the managed build is selected. `Review Gate` adds no build runner. Repository hygiene publishes `repo-hygiene / Repository hygiene` through the SHA-pinned shared workflow. The execution-evidence job materializes its checker and tests from the trusted base commit, so older heads need no checker files and PR changes cannot replace the policy. It checks the explicit base/head diff; both this job and shared hygiene must succeed. Shared hygiene deterministically judges added paths against trusted-base policy, has a five-minute job timeout, and reports author-actionable path violations. Shard identity validation checks the PR merge tree, rejecting duplicate names globally and duplicate filters within a test project (including grouped-project overlap). Disjoint test projects may share a namespace filter. This deterministic check has a five-minute timeout and feeds the existing required hygiene aggregate, so concurrent additions fail before landing without adding a required check name. Any further PR-blocking check still owes the sub-5-minute justification above and should first be considered inside an existing required workflow.

5. **AOT verification is full-CI only until trim debt is retired.** The `AOT Build Verification` job in `ci.yml` runs in the full scheduled/manual integration lane or on PRs explicitly labeled `ci/full`, not on routine PRs or merge-to-trunk pushes, because its current failures are not consistently fast or author-actionable within the default PR lane.

6. **The `Tier=Fast|Integration|Slow` test trait is a sub-tier inside this gate model, not a replacement for it.** ADR-0037 splits the .NET test suite by execution cost so that PRs run the `Tier=Fast` foundation tests plus a targeted subset of `server-tests` shards selected by `scripts/ci/honua-server-targeted-tests.sh`. The `targeted-shards` job emits a JSON `matrix_include` drawn from `.github/ci-shards.json` and `server-tests` consumes it via `strategy.matrix.include: fromJson(...)`, so unselected shards never instantiate a runner. The shared shard runner composes `&Tier!=Slow&Tier!=Fast` onto the matrix filter so Slow-tagged tests inside a shard's namespace are skipped and Fast tests run only once in the foundation lane; it also emits heartbeat/tail diagnostics over normal-verbosity test logs, writes timing artifacts, and enforces the inner `test_timeout_minutes` cap before the job-level timeout cancels the runner. `Tier=Slow&Category=Emulator` runs nightly via `nightly-slow-tier.yml`, and `flaky-detection.yml` re-runs a bounded rotating window of the same `.github/ci-shards.json` shards several times a night for flake reporting (the whole shard set is covered every ceil(shards / shard_count) days). The Scale/Cloud/External slow subfamilies need dedicated workflows once their fixtures are wired up. The five-tier PR/nightly/release/deploy/maintenance gate model above still defines *where* a workflow lives; the trait defines *which subset of tests* runs inside the workflow.

Full CI runs twice daily at `07:23` and `13:23` UTC (23:23 and 05:23 Pacific standard time). It retains protocol-profile compilation, remote-source and server execution proofs, and import/export fidelity. The existing Worker GDAL job owns native GDAL/PDAL coverage and the raster execution receipt checks.

## Neutral automation helpers

See [the workflow inventory](workflow-inventory.md#neutral-automation-helpers)
for helper check names and queue policy. Helper completion does not grant
admission: draft helpers skip without publishing success; `ready_for_review`
re-enters attestation. Blocking reviews retain failure, and real attestation/API
errors fail. Review helpers queue superseded events per PR (`queue: max`)
instead of cancelling them, so a superseded resolver or bridge run ends green
rather than as a cancelled red mark on the PR page. Each attestation re-reads
live review/admission state before acting. An attestation whose live review
or admission state changes between its first snapshot and final revalidation fails red;
the queued event behind that change re-attests an open, ready head. Bursts
are not collapsed: each event runs in turn, with up to 100 waiting per PR.
Normalization cancels repeated eligible
producers for the same PR head. Different heads use separate groups so a delayed
old event cannot cancel current work and then shed without replacement. Draft
and title/body-edit events use isolated groups because they cannot generate a
replacement envelope. Trusted consumer admission resolves the live PR, rejects
stale completions and cancels only older producer generations before per-attempt
validation. Live-head checks and compare-and-swap remain required before branch
mutation. Auto-merge eligibility events retain FIFO serialization because each
event can change merge eligibility.

Completions without a PR number use an isolated resolver group per run; once resolved, publication queues behind older work by PR number. An unresolvable completion cannot delay or cancel a resolver for another run.

Review event ordering is not guaranteed. A late old-head event queues behind newer ones and reconciles the current live PR head. Draft conversion still reaches the shared attestation group, queues, and sheds the live draft before checkout or status publication. Other draft pull-request payloads skip resolution.

## Verification stages

Every stage reacts to a repository event; no stage polls. A change is verified
in widening rings, each cheaper than the next and each catching what the
earlier one could not.

| Stage | Trigger | Workflow | Scope | Blocks merge |
|---|---|---|---|---|
| **0 Pre-PR** | Author, before opening or pushing | `scripts/ci/pre-pr-check.sh` (local) | Affected build, Fast tests and affected shards on the author's machine | No; the PR template asks for the result |
| **1 PR gate** | `pull_request` | `pr-gate.yml`, `review-gate.yml`, `repo-hygiene.yml` | Affected-scope build, selected shards, Fast/architecture, format, router validation, exact-head review | Yes: `PR Gate`, `Review Gate`, Repository hygiene |
| **2 Pre-merge** | Trunk moves under an open PR | The owning lane and the fleet lander | A behind PR is refreshed only when trunk touched its affected projects; the refreshed head reruns stage 1. The lander merges only an exact-head green, thread-free PR | Yes, through stage 1 on the refreshed head |
| **3 Post-merge affected** | `push` to `trunk` | `trunk-affected.yml` dispatching `ci.yml` | Selective CI over new merges since the latest completed CI run (`selective_base`): the shards and lanes those merges touch | No; a red run is assigned to an owning lane |
| **4 Full** | `schedule` 07:23 and 13:23 UTC | `ci.yml` | The full matrix, compatibility and certification lanes | No; full proof clears the lander's trunk-red brake |

Full runs use off-hour minutes to reduce schedule-delivery contention. GitHub still delivers these crons hours late, so
`full-ci-dispatch.yml` dispatches the full run itself on trunk pushes between 22:00 and
03:00 Pacific when no full run is active and trunk head has no full result (at most two a night). The
`trunk-signal-watchdog.yml` workflow runs on `workflow_run` completion of `CI` and `Full CI dispatch`
on `trunk` (with a half-hourly `schedule` fallback for quiet periods) and reports a missing scheduled start after
20 hours, independently of affected CI activity. It also reports any running run without a usable
result for 4 hours from its first runner-assigned job, a last result older than 22 hours, runs queued over 45 minutes, and excessive cancellations;
its issue closes only when all signals recover. GitHub can report a workflow as queued
while its children execute. For these runs the watchdog checks paginated, latest-attempt
job evidence: runner assignment proves a scheduled start, while child jobs waiting
over 45 minutes still alert. A queued job's `started_at` alone does not prove execution.

Stage 3 details:

- The execution base is the most recently completed `CI` run on trunk with
  a success conclusion and an ancestor head, selective or full.
  The newest 100 completed runs are ordered by completion update time.
  Failed and cancelled runs cannot advance the base, including deferred runs whose
  cancellation request fails. With no eligible ancestor, defer to scheduled full CI.
- A dispatcher that defers runs `Record coverage deferred to scheduled full CI`.
  Later pushes start from the newest such ancestor head after the completed base
  (`scripts/ci/completed_ci_base.py` reads the step from the newest 100 successful
  dispatcher runs, at most 20 job lookups), so a range already owed to the full run
  does not defer every later merge. Before this, one infrastructure merge after the
  last successful run deferred all 100 dispatches from 14:02 UTC on 16 Nov to
  16:27 UTC on 17 Nov; replaying the router per push over the same 99 ranges admits
  34 (30 docs/CI-only, 4 targeted). Unavailable history keeps the completed base,
  and a transient selection failure is not recorded, so the next push retries it.
- The dispatch summary separately lists the culprit range from the latest
  full green ancestor (searched among the newest 100 successful runs,
  excluding affected titles that cannot clear earlier failures). It passes
  that ancestor as `investigation_base` to `ci.yml` for the investigating lane; missing
  green history is reported as unknown. Failures
  remain owned by the investigating lane; twice-daily full runs retain complete coverage.
- Runs queue without cancellation. GitHub replaces older pending runs; the
  newer run covers merges since the latest completed head. Superseded pushes
  exit when checkout finds a newer trunk tip.
- Shards the PR gate deferred by time budget run here: the router selects
  them again from the merged diff, so a deferred shard is verified within one
  admitted affected run of its merge, or the next scheduled full run when deferred.
- `ci.yml` puts selective runs in their own concurrency group, so a burst of
  merges never replaces a pending scheduled full run.
- A started full run cancels the selective runs it covers, and a selective
  run under a covering full run cancels itself ([covered trunk runs](#covered-trunk-runs)).
- The dispatched run is titled `CI affected <base>..<head>`. Its `changes`
  job prints the suspect range (`scripts/ci/trunk-suspect-range.sh`: every
  trunk commit in the range and the pull requests it names) to the job log and
  summary. The investigating lane reads the title and log to identify the breaking merge.
- Affected runs never certify a sha: `scripts/ci/select-certified-nightly-sha.py`
  ignores runs titled `CI affected ...`, so a selective run that finishes after
  a full run on the same sha cannot supersede that full run's certification.
  Explicit full-matrix runs still certify; optional affected admission never
  escalates an unknown range into a full run.
- Optional affected CI admits at most 40 jobs using a conservative side-job
  bound plus selected shards, both before dispatch and again before execution
  fanout. Full routing and unknown selection/history defer the work to the
  twice-daily full run; an active PR Gate does not. Deferred execution cancels
  its workflow before publishing change outputs, so it cannot record a
  successful CI base.
- Admitted affected runs use two concurrent server shards and run beside PR
  Gate; only a covering full run or their own admission recheck cancels them.
  Full runs retain width 16. A selective affected run whose router reason is
  `no_path_match` does not schedule those default smoke shards: they do not own
  the diff, and the twice-daily full run keeps them. The same run skips
  Honua.Core package compatibility unless a changed file is one that packing
  Honua.Core reads: `src/Honua.Core/`, `src/Honua.Analyzers/`, a file directly under `src/`, a root-level file, `eng/`, `scripts/ci/analyzer-baseline/` or `.github/actions/setup-dotnet-ci/`. An empty path list still runs that heavy job.
  Skill (`skills/`, embedded in `src/Honua.Ai`) edits select the lanes a `src/`
  edit selects; `certification/` edits select no expensive lane.
  A skipped Core package does not cancel a selected AOT or MCP lane; a failed
  Core package or Postgres compatibility job still does.
- Stage 3 is not a required check and changes no ruleset.

## PR Lane (Required Checks)

Besides the required checks below and the required Clean code check, every
`pull_request` or `pull_request_target` workflow is optional and path-filtered,
apart from two cheap control workflows: auto-merge ready pull requests (every
event except non-hold label changes and body-only edits) and Close external pull requests. Runner label lint reads only
`.github/workflows`, so it runs only when a pull request or trunk push changes
that directory. A new optional pull request workflow needs a `paths` filter
naming what can change its result.

These workflows are merge-blocking for all PRs to trunk:

| Workflow | What it validates | Path filter |
|---|---|---|
| `pr-gate.yml` | **Required verification context `PR Gate`.** Admission includes tracked-text UTF-8 validation. The affected-scope warnings-as-errors producer publishes pristine, verified managed test outputs and runs the required boot/migration smoke against its canonical host output; selected shard consumers run the affected tests that fit the `pr_time_budget` in `.github/ci-shards.json` (shards over 10 measured minutes, or past 45 runner-minutes in total, are deferred to the next trunk run and named in the verdict). Independent Fast/architecture jobs remain required, one per test group: `architecture` and `unit-fast` compile their own roots, and `server-fast` and `server-governance` restore the producer Server.Tests output and compile it only when that restore is empty. Format, analyzer and CI-router jobs remain required. `required.needs` includes `pr-gate`, `fast-tests`, `fast-server-tests`, `format`, `ci-router-validation`, `affected-shards`, and `server-test-producer`. The affected-shard aggregate enforces every selected timing/TRX result before `PR Gate` succeeds. See the [artifact contract](server-test-binary-artifacts.md) for same-run identity, bounds, rollback and scoped fallback. | None — a required context must never be path-filtered |
| `review-gate.yml` | **Required admission context `Review Gate`.** Immutable default-branch workflow policy publishes exact-head review evidence from an attesting reviewer (Codex or Claude — see [Attesting reviewers](#attesting-reviewers)) and, in enforce mode, releases one admitted PR Gate rerun. In observe mode it reports its decision without retaining an experiment artifact. Review events arrive through the read-only `review-event-bridge.yml`; no PR-authored workflow has status or Actions write authority. | Drafts skip; every ready PR head requires attestation |
| `repo-hygiene.yml` | **Required context `repo-hygiene / Repository hygiene`.** Reads the root allowlist and exact gate-input exceptions from the trusted base branch; checks only PR-added paths for scratch, backups, run output, dated history, product samples and unexpected roots. The shared checker is pinned by SHA, runs with read-only permissions and has a five-minute timeout. | None — it must publish on every PR head |
| `ci.yml` | **No `pull_request` trigger.** Full CI runs twice daily at 07:23 and 13:23 UTC (23:23 and 05:23 Pacific standard time) and through manual dispatch. It covers managed builds, protocol profiles, foundation and server shards, native Worker tests, runtime proofs, architecture checks, JavaScript checks and compatibility. `CI Gate` requires the runtime proof result and appears only on full CI runs. | n/a — not PR-triggered |
| `openapi-contract-governance.yml` | OpenAPI spec stability | `src/**/api-specs/**`, `*.openapi.*` |
| `control-plane-sdk-governance.yml` | Control plane SDK governance | SDK/control-plane paths |
| `import-fidelity-scorecard-governance.yml` | Import-fidelity baseline stability + perf-parity gate smoke test (pass/fail fixtures) | Parity/baseline/perf-budget asset paths |
| `docs-link-gate.yml` | Relative links **and** `#fragment` anchors across `docs/**/*.md` (GitHub slug algorithm, ported from `geospatial-mcp`'s `tools/check_links.py`), plus `scripts/ci/code-referenced-anchors.v1.json` — the absolute phase-A `https://honua.io/docs/<slug>/` URLs product code and shipped config hand to an operator or an agent at runtime (`remediationRef`, SCIM `documentationUri`, Prometheus `runbook_url`). A URL served only through a `.gitbook.yaml` redirect warns; a `docs.honua.io` URL is an error (that host is dark until 2026.2), and a phase-A URL that lacks the trailing slash or is not listed in the manifest is an error; a `pendingPr` entry is warn-until-present so an anchor introduced by an open PR is enforced only once it lands. Pre-existing rot lives in `scripts/ci/doc-link-rot-allowlist.v1.json` with a stale-entry ratchet. Python with the pinned YAML reader from `scripts/ci/okf-requirements.txt`, ~15s. A pull request is validated as its merge into trunk, so a head cut before a checker existed is judged by the checkers the workflow calls. The uploaded OKF agent graph is the exception: it is generated from the exact head in a separate tree by that head's own generator, and fails the job when that generator cannot run. A documentation defect on trunk fails every pull request this gate runs on; a rerun reuses the same merge, so recovery after the trunk fix needs a new event on the pull request (a push, or close and reopen). | `docs/**/*.md`, the docs data files the checkers read, `scripts/ci/check-doc-links.py`, `scripts/ci/check-doc-links.test.py`, the workflow itself |

### Affected-scope gate build

The following timings are historical context, not a measurement of the current
producer/consumer topology. On the 2026-08-26 baseline the
shared lean gate was 18m09s, split roughly:

| Step | Duration |
|---|---:|
| Build (warnings as errors), full solution | 6m30s |
| `dotnet format Honua.sln --verify-no-changes` | 5m51s |
| Server Fast tier | 2m05s |
| Architecture tests | 1m31s |
| Governance/Drift (Testcontainers PostGIS, warm; moved to trailing trunk) | 1m15s |
| Fixtures, policy checks, restore | ~1m |

The build is now separated from test execution. Pristine outputs are packaged
once before tests or coverage can modify them. Downstream consumers verify the
same-run source, SDK, platform and build-property identity, relocate staged MVC
and static-asset content roots to their own checkout, then compile only
missing projects as one scoped fallback graph. Both architecture suites run
after projection regeneration; the full collector remains intact. Full CI also
uses a managed producer and an independent, required full-solution format job.
See the [managed artifact contract](server-test-binary-artifacts.md).

**Build scope.** `.github/actions/lean-gate` takes a `build-scope` input.
`PR Gate` passes `affected`; `Merge Queue Gate` keeps `full`.
`scripts/ci/compute-lean-gate-build-scope.sh` writes a solution filter (`.slnf`)
holding the union of

1. `scripts/ci/compute-affected-projects.sh` — the changed projects plus their
   reverse-dependency (consumer) closure, and
2. the catalogued test projects used by the Fast, source-architecture, full
   architecture, governance and execution-proof steps, and
3. the selected non-advisory shard projects supplied by the managed producer
   plan. Documentation-only exits skip compilation inside the jobs.

MSBuild resolves each listed project's `<ProjectReference>` graph itself, so
forward dependencies are built without being enumerated. A filter is used rather
than a project list because `dotnet build a.csproj b.csproj` is rejected outright
(MSB1008); the filter also keeps the build to a single MSBuild invocation with
cross-project parallelism bounded to four MSBuild workers.

The current build floor is the forward dependency closure of that explicit
producer plan, rather than a fixed count of test projects. Isolated unit projects
and component hosts retain their own runtime ownership; the full architecture
collector still requires its complete test-assembly graph.

*Miss-risk contract.* Anything that could invalidate the closure forces a full
build: `compute-affected-projects.sh` force-fulls on `Directory.*.props`,
`Honua.sln`, `global.json`, `NuGet.config`, `.github/` and `scripts/ci/`, and the
scope script force-fulls on any event without a trustworthy diff base, any
checkout too shallow to hold the base commit, and any failure of the underlying
script. Routine per-PR landings retain the required PR checks and trailing full
CI; a deterministic trunk failure pauses routine landing for fix-forward repair.
Explicit batch validation can run before landing. `Merge Queue Gate` retains its
full-solution build. The full CI producer keeps its affected/full decision and
unions every planned consumer root into an affected build graph.

`pr-gate.yml` must check out with `fetch-depth: 2`. The checked-out ref is
`refs/pull/N/merge` and its first parent is the current `trunk` tip, which is the
diff base. At the default depth 1 those parent commits are absent, the scope
computation force-fulls, and the optimization silently never fires.

**Format scope.** The PR's existing independent format job retains its affected
or full document selection and complete formatter restore context.
Analyzer rows skip that preliminary whole-solution restore: the shared format
action restores their selected graph in Release before compilation and source
ownership evaluation. Scope selection itself does not require restored assets.
The formatter retains its whole-solution restore for `dotnet format --no-restore`.
A parallel
`PR Gate / Analyzers` (source) and `PR Gate / Analyzers (tests)` jobs of the
same matrix own disjoint analyzer passes: every other
PR build runs with `/p:RunAnalyzers=false`, and both jobs build the diff's
affected closure (or the full solution on fallback, and nothing when the diff
reaches no project) with analyzers, code style and documentation diagnostics
as warnings-as-errors. `HonuaAnalyzerPartition` in `Directory.Build.targets`
assigns `IsTestProject=true` projects to `tests` and every other project to
`source`. After restore, the source partition evaluates `IsTestProject` through MSBuild
and omits test-only compilation roots; imports are respected, incomplete evaluation
fails closed, and multi-targeted projects conservatively remain included. Each
partition still compiles forward dependencies but runs analyzers only for its own set; source generators remain enabled.
Until this change the source partition ignored the affected filter and analyzed all
51 source roots on every PR; on nine green PR Gates (22-23 November) it was the
last job in five, at 16.8-18.8 minutes (build 15.6-17.5 minutes on hosted runners;
the same 51 roots took 9.1 minutes on faster hosts). An unaffected project's
analyzer inputs are unchanged. `compute-affected-projects.sh` forces a full scope for
every shared build or analyzer input: root `Directory.Build.*`, `scripts/ci/`
(baseline, banned symbols, globalconfig), `src/Honua.Analyzers/`, and any changed
`*.props`, `*.targets`, `.editorconfig`, `*.globalconfig` or `*.ruleset` anywhere
(nested `Directory.Build.*`, the `eng/*.props` imports). It also maps a file linked
from outside a project's directory (`Compile`, `EmbeddedResource` or `AdditionalFiles`
with a `..` path; MSBuild `**`, `*` and `?` globs are honoured) to every project that
links it. `$(MSBuildThisFileDirectory)` and `$(MSBuildProjectDirectory)` are expanded;
an include naming any other property cannot be resolved without MSBuild evaluation,
so its project always counts as affected (no project uses one today). Before this change
those inputs mapped to no project or only to their home project, so the test
partitions and the affected build missed them. The scheduled and `full_ci` runs
keep the whole-solution analyzer build. The `format` matrix remains
in `required.needs`, so both partitions must pass. The existing analyzer check
name is retained for the source partition. Analyzer builds disable persistent
MSBuild servers and node reuse so process teardown does not depend on a shared
server; compiler, analyzer and timeout failures still fail the job.
Unpartitioned callers default to
`all`; invalid partitions fail the build. `PR Gate` still fails on every analyzer finding. Full CI runs
an independent full-solution format job; `CI Gate` requires it when build inputs
are selected, without making test consumers wait for formatting.

**Fast-test selection.** The four existing fast-test check names remain present.
Before setup or compilation, each uses `compute-fast-test-selection.py` to read
`compute-affected-projects.sh` at the PR merge checkout (`HEAD^1..HEAD`) and
intersect its reverse-dependency closure with the group's existing test roots.
Empty closures skip all four builds and tests only after positive governed-docs proof;
a nonempty managed closure retains architecture and Server governance checks as well as every affected
group. Unknown projects, invalid roots, missing history, selector errors and
non-PR events retain every group. Existing docs/generated-only classification
and exact-head review admission still apply. A skipped test body completes its
matrix row successfully, preserving the required aggregate's needs contract.
Drafts instantiate only the Format matrix row; ready PRs retain all analyzer
partitions and Format.

**Governance and runtime proofs.** The independent PR Fast job retains the
complete `Category=Architecture` governance set in `Honua.Server.Tests`, the real
store-gated host and Testcontainers PostgreSQL, source architecture and the full
collector after fresh projections. It compiles its affected scope alongside the
producer so these checks do not wait for artifact publication. The required
boot/migration smoke remains on the producer after pristine artifact publication.
Full CI owns remote-source, raster, layer, geometry/calculate-field, imagery,
GDAL/PDAL and import/export fidelity proofs, including the existing receipt
tripwires. Protocol-profile compilation also runs in full CI, in its own
`Protocol profiles` job beside the producer rather than inside it. Every
server-test shard, the foundation tests and the runtime proofs need `build`, so a
break that only affects a reduced profile used to skip the whole trailing matrix:
full run 45066303710 skipped it for about six hours, which hid an unrelated
integration regression (#9351, #9371). `CI Gate` requires the job, so a profile
break still fails the run. It runs on a standard hosted runner and builds the
referenced projects once without analyzers. `build` analyzes them in full runs
and PR Gate in affected runs; `Honua.Server`, the only project that reads the
profile, compiles with analyzers for each profile. Full and scheduled
runs select every profile with `protocol-profile-selection.py --all`. A range
selection compares trunk with itself there and would build only `all`, which
is what full run 44309947920 did. Between full runs, Trunk Sanity's
`Reduced protocol profiles` job (`trunk_sanity_profiles.py`) compiles every
reduced profile whenever the range since the newest successful Trunk Sanity
run that already had the job changes anything other than `.github/` files and
Markdown under `docs/` or at the repository root. Both Trunk Sanity jobs queue the newest push
behind the running one instead of cancelling it, so a burst of merges still
gets a verdict within about two build lengths.

### Import-fidelity gates: correctness and performance

The migration-reconciliation import-fidelity suite (`GeoservicesImportFidelityIntegrationTests`) emits a scorecard that
carries two independent gate lanes per sampled operation:

- **Correctness gate** — the `Checks[]` array (query/geometry/statistics/error-shape parity, etc.) is
  gated by `scripts/ci/check-import-fidelity-scorecard-regression.sh` against
  `tests/dotnet/Honua.Server.Tests/Import/import-fidelity-scorecard-baseline.json` (any `pass`→`fail` flips the
  gate).
- **Performance gate (issue #1249)** — the suite already measures Honua-vs-source p95/p99 latency
  ratios; `GeoServicesPerfParityGate` (in `Honua.Core`) grades those ratios against a configurable
  `PerfParityBudget` and embeds a Pass/Warn/Fail `PerfParity` verdict into each scorecard case.
  `scripts/ci/check-import-fidelity-perf-budget.sh` enforces that verdict (and re-derives it from the raw
  ratios) against `tests/dotnet/Honua.Server.Tests/Import/import-fidelity-perf-budget.json`, failing the gate
  when latency regresses past the fail budget (default: p95 ≥ 2.0×, p99 ≥ 2.5× the source). Thresholds
  are configurable via the budget JSON, the `HONUA_PERF_PARITY_*` env vars (CI), or the
  `HONUA_TEST_PERF_PARITY_*` env vars (the integration suite). The gating logic, verdict emission, and
  scorecard shape are covered offline by `GeoServicesPerfParityGateTests` and smoke-tested on every PR
  via pass/fail fixtures under `scripts/ci/fixtures/`; the live latency measurement runs in
  `geoservices-import-fidelity-external.yml` (on-demand).

Shared OGC capabilities XML tests live in the Wms namespace and run in Maps Supporting. The WMS class/complement partition and its legacy budgets stay unchanged; Supporting also retains catalog metadata and cross-protocol XML source triggers.

Full CI `mcp-certification` and `mcp-llm-smoke`, and the manual certification job in
`mcp-certification-focused.yml`, build `honua-sdk-js` on Node 22.23.3 (SDK engine
minimum 22.12) with a 4 GiB V8 heap. Workflow-level `NODE_VERSION` stays 20 for the
browser and JavaScript integration jobs.

## Verification receipts and batch landing (quality-contract amendment, 2026-09)

The fleet lander (honua-flow `tools/land-ready-prs.sh`, `tools/land-batch.sh`)
is the only landing authority. Its verification rules are part of this gate
model:

1. **A verified baseline advances only with a receipt.** The last verified
   trunk head (`trunk-lastgreen`, the `selective_base` of every later trailing
   or batch run) moves forward only when a receipt `verified-<sha>` exists,
   and never backwards. A receipt records the candidate tree, the
   `selective_base`, the run id, the blob ids of `.github/workflows/ci.yml`,
   `.github/ci-shards.json` and `scripts/ci/honua-server-targeted-tests.sh` at
   the verified revision, the shard manifest (the `Server Tests (<shard>)`
   matrix the run instantiated), the conclusion of every required
   compatibility lane — Postgres Compatibility, AOT Build Verification, Docker
   Build & Integration Test, Python Integration Tests, JavaScript Integration
   Tests, Esri Leaflet Browser Tests, MapLibre GL JS Browser Compatibility,
   MCP Certification, Honua.Core Package Compatibility, Quickstart Console
   Compose Smoke; a matrix lane aggregates every variant, so one red Postgres
   image makes the lane red — and the `CI Gate` conclusion.
2. **Full proof** = `CI Gate` success, the manifest equals the full shard list
   at that revision, and every required lane is success. **Selective proof** =
   `CI Gate` success, the manifest is a subset of the full list and a superset
   of the uncapped router's shards over exactly the diff the run selected on
   (`selective_base..tip`), and every lane is success or was skipped by the
   run's own diff gating. Job counts are reported and never authoritative; the
   trunk-red brake clears only on full proof.
3. **Batch candidates** live on `land/batch/<base7>/<id>` refs, run
   `ci.yml` as `CI lander-batch-<id>`, and are never trunk verdicts. Members
   that change CI plumbing (`.github/workflows/`, `.github/ci-shards.json`,
   the router, `scripts/ci/compute-affected-*`) take the per-PR path.
4. **A failed batch quarantines; it never falls back.** After at most one
   rerun for an infrastructure-class failure (cancelled, timed out, runner
   loss, no test result), every member is quarantined at its current head and
   the batch is bisected into verified subsets (halves, member order kept,
   down to single members). A member is released only by a verified green
   candidate that contains it. Shard ownership orders which half runs first;
   it never releases anyone. Non-members keep landing per-PR.
5. **Stop at the first uncertain merge.** Every merge is preceded by a
   merge-intent record and followed by a merge-confirmed record. A refusal,
   moved head, closed PR or HTTP 5xx stops the batch; an unknown outcome is
   resolved against GitHub, never assumed. Remaining members are rebuilt on
   the new trunk tip and re-verified — never landed on the strength of the old
   run. After landing, the last member's merge commit must carry the verified
   tree; otherwise a trailing matrix is owed.
6. **Durable state.** Batch state changes follow an explicit transition table
   under one lock shared with the trunk watcher, and every lander start
   reconciles unconfirmed intents, vanished refs/runs and the owed trailing
   matrix against GitHub before deciding anything.

Rollout: shadow first (assemble, dispatch, judge, receipt; nothing merged).
Live two-PR batches follow only after at least three days and ten shadow
batches meet the plan's exit criteria (assembly under 120 s, no lander
timeouts, at least 80 % of batch runs green or uniquely attributable, every
would-land set's later per-PR landings reproducing the recorded tree).

## Full-CI compute runner selection

`ci.yml` uses the repository variable `HONUA_CI_HEAVY_RUNNER` for its compatible
build/test workloads. Unset or empty selects `ubuntu-latest`. A nonempty value
is one runner label, such as a GitHub-hosted larger-runner name; it is not a JSON
label array. Main owns configuring the variable after the runner is available
to this repository and the combined batch has passed build-host preflight.

The switch covers format, foundation-family tests, server shards, MCP
certification and LLM smoke, PostgreSQL and core-package compatibility, AOT,
Docker build, and Console quickstart smoke. Admission, routing, receipt
aggregation and final verdict jobs remain on standard hosted runners so a
compute-pool problem cannot strand their reporting. Light checks such as JS
type checking also stay hosted. The PR Gate has its own selection, below.

The selected runner must provide a clean Ubuntu x64 environment compatible with
GitHub's hosted tools, Docker, passwordless sudo, .NET/Node/Python setup actions,
and the existing disk-cleanup steps. This switch provisions no runners and
changes no command, permission, test selection, concurrency or timeout.

Worker GDAL, Python integration and browser jobs retain their Ubuntu 24.04 pins
for native-package and Chromium compatibility. They do not use this switch.
Main checks the chosen pool on the first ordinary combined batch using it;
no separate branch full run is needed. Clear the variable to restore the hosted
default for new runs. Treat existing runs as assigned to their original pool;
main handles their completion or cancellation.

Full CI and PR Gate publish PostgreSQL service container port 5432 without a
fixed host-port binding. GitHub assigns each job its own host port, so concurrent
jobs on the shared build host cannot collide at Docker service startup. Full-CI
clients inherit `PGPORT` and `HONUA_TEST_DB_URL` from the assigned service port;
server setup, SQL seeds and the host-network Docker smoke use that same port.
Existing shard connection strings already consume the assigned port. The server
setup action defaults to 5432 for callers without a PostgreSQL service override.
This changes no job count, test selection, required context or runner routing.

## Full-CI producer admission

`Build & Format Check` runs where `CI producer / Runner select` says, and that
job calls the shared `advisory-runner-select.yml`. On trunk schedules and
dispatches it picks an idle `honua-heavy` runner (an 8-vCPU slice) while more
than two heavy runners stay idle for PR Gate. Otherwise, and for any read
failure, it stays on `ubuntu-latest`. The selector reads the build-host heartbeat
with the existing `HONUA_PR_GATE_WAKE_ROLE_ARN` OIDC role and never wakes,
waits or retries. A snapshot is admission evidence, not a reservation, so a
later queue remains possible: if heavy runners go away after selection (spot
replacement, or other advisory callers taking the slack), this critical-path
job waits in the self-hosted queue. Before this the producer was hosted on every trunk
run: Build steps took 12-30 min and the job 21-46 min (13-18 Nov). The earlier
opt-in `HONUA_CI_PRODUCER_RUNNER=honua-build` route was removed, because its
read role was never provisioned.

Selective (affected) dispatches build the producer graph with
`RunAnalyzers=false`; scheduled and `full_ci` runs keep the analyzer build.
PR Gate's analyzer partitions already checked each landed PR's affected
projects on its merge ref, and ci.yml's `Format Check` still runs
`dotnet format` in the same affected run. Warnings-as-errors still applies to
compiler diagnostics and source generators still run; the PR Gate producer
already publishes its managed outputs from this no-analyzer build mode under
the same build-properties label. An analyzer finding that only appears when
several landed PRs combine is left to the scheduled full run. Selected
protocol-profile builds keep analyzers for Honua.Server, whose profile
constants change. The `Protocol profiles` job first resolves referenced projects
without analyzers and then reuses them with `BuildProjectReferences=false`, in
full and affected runs alike; the producer (full) or PR Gate (affected)
analyzed them. A single `RunAnalyzers=true` profile build would
recompile all 29 referenced projects: affected run 44379685745 spent 10.2 of
16.3 profile minutes on that (6.4-7.0 min before the producer dropped
analyzers).

The producer skips hosted sudo cleanup on self-hosted runners and installs its
SDK into `${{ runner.temp }}/honua-ci-producer-sdk`. Every producer restore,
build and nested MSBuild evaluation uses at most four workers; job-wide
`DOTNET_PROCESSOR_COUNT=4`, disabled MSBuild servers and disabled node reuse
also apply to child tooling. Required checks, warning/analyzer policy, artifact
selection and consumers remain unchanged. Other compute jobs retain their
existing switch; this change does not activate the global heavy-runner variable.

Source merge does not install or activate a runner. MAIN owns configuration
and a runner-context preflight on the actual gate accounts: verify the private
SDK host/runtime and native dependencies, writable SDK/tool/cache/temp paths,
setup action compatibility without sudo, and the bounded full producer graph
within the parent slice's 10 GiB/6 CPU limits. Preserve active workers; use the
next authorized ordinary run for build evidence, without a new full-CI dispatch
or rerun. Record producer/restore/build minutes and memory evidence separately
from the earlier PR producer; no full-trunk speedup is established by that PR.
Keep the variable unset until these prerequisites pass. A current-instance
heartbeat with two idle gate runners is still required for each later admission.
Clear the producer variable to restore hosted selection for new runs. Existing
jobs keep their assigned runner.

Both producer and PR Gate selectors use `scripts/ci/gate-runner-heartbeat.sh`
for the read-only heartbeat proof. It requires exactly one JSON object, the
current instance, an integer timestamp no older than 90 seconds, and two idle
gate runners. Heavy-pool readiness remains independent of gate-pool admission.
The producer takes one snapshot; the existing PR selector retains its bounded
wake/wait orchestration. Both sparse checkouts include the shared validator.

### Host NuGet fallback folder

Build-host runners start every job with an empty home. They export
`HONUA_NUGET_FALLBACK`, a package folder that only the host can write. A
no-credential host account fills it from trunk's package closure (honua-iac
`install-nuget-fallback.sh`). When a job passes `host-package-fallback: 'true'`,
`setup-dotnet-ci` resolves the link to one generation and sets
`NUGET_FALLBACK_PACKAGES` to it. The job then skips the ~1.7 GB NuGet cache
archive, and NuGet downloads only the packages the folder lacks (GitHub
Packages ids are never in it). If the variable is unset, the target is missing
or writable, or it holds no package, the job restores the cache archive as
before. Hosted runners never set the variable. Only the `ci.yml` `server-tests`
shards and the `PR Gate / Affected shard` jobs opt in, because both re-check
package imports after consuming managed outputs. `fast-server-tests` stays out:
a fully consumed group goes straight to its test phase. Producers stay out as
well, since a producer must keep its static web asset roots under the
package folder it packages. A consumed `obj` folder names the producer's
`~/.nuget/packages`, which the fallback leaves mostly empty, so on the fallback
even a fully consumed shard runs `managed-test-build.py fallback`: it restores
only projects whose package imports are missing and compiles nothing. A shard
restored from the exact-head shard cache gets the same restore-only check,
because that payload's `obj` came from whichever job wrote the cache. Measured
18 Nov 2026: Setup .NET took 77-86 s on build-host runners against 31 s hosted,
about 50 s of it the archive.

## PR Gate compute runner selection

`PR Gate / Runner select` decides where `pr-gate` (Build and tests) and
`server-test-producer` run, and which start jobs move to idle heavy runners. The
selector, Format and source Analyzers, affected-shard selection, the
affected-shard aggregate, CI router validation and the required aggregate stay
on `ubuntu-latest` for every pull request. `HONUA_LIGHT_RUNNER` does not place
them. That pool also runs hygiene, review and the other light workflows, and a
selector waiting on it leaves idle `honua-build` runners unused. Both
test-analyzer rows and the four Fast/architecture matrix jobs use `ubuntu-latest`
unless the selector planned them onto the heavy pool (below); test analyzers never
use the light pool, even during light-pool qualification. All eight keep their
existing check names and coverage. `HONUA_LIGHT_RUNNER` remains the operator
switch for those non-gate light workflows: keep it unset while the light pool
is unhealthy and they fall back to hosted runners. Forks always use hosted
runners. Routing does
not move an already queued job when pool health changes. With the repository variable
`HONUA_PR_GATE_RUNNER` unset, its steps skip and the two producers stay on
`ubuntu-latest`. Set it to `honua-build` to use the ephemeral runners on the lane
build host (honua-iac `bootstrap/aws-build-host`, "PR Gate runners"). It also
needs the variable `HONUA_PR_GATE_RUNNER_REGION` and the secret
`HONUA_PR_GATE_WAKE_ROLE_ARN`. The job assumes that role through GitHub OIDC
(`id-token: write` on this job only; the role can describe and wake the build
host and read its heartbeat, nothing else) and runs
`scripts/ci/select-gate-runner.sh`:

- When the current instance's heartbeat is fresh and shows two idle gate runners,
  Build and tests and Server test producer use the selected gate label.
- When the host is busy, or no heartbeat arrives within three minutes, the two
  producers use `ubuntu-latest`, unless idle heavy runners cover them (below). A
  stopped host is still woken for later runs.
- Heavy start plan (`start_heavy`): from the same proof, the selector never plans
  three idle heavy runners (two for this gate's even affected shards, which have no
  hosted fallback, and one for advisory and AOT jobs) and spends half of the idle
  runners above that, at most two per gate, so a second gate reading the same
  heartbeat still finds the reserve. The heartbeat records availability; it does
  not reserve runners, so several gates starting together can still queue on the
  pool, and a lost spot host leaves planned jobs queued as it does shards. A busy
  gate pool moves both producers first, on the label that alone selects the heavy
  pool (the group's own label, or a sole label that must be unique to the heavy
  pool), because they gate the shards; without such a label, including a
  group-only selector, nothing is planned and every start job stays hosted; the remaining budget goes to `analyzers-server`,
  `architecture`, `server-fast` and `analyzers-other`, in that order (longest
  median hosted run on 24 gates, 15 Nov 2026: 12.2, 12.0, 10.7 and 10.6 minutes
  after 5 to 9 minutes of hosted queue). `server-governance` (starts PostGIS) and
  `unit-fast` (shortest) stay hosted. A planned job runs on the fork-guarded
  `HONUA_HEAVY_RUNNER` JSON selector; a stale or invalid proof, enforce mode,
  drafts and forks keep it hosted. A failed-jobs rerun reuses the original plan.
  Fast/architecture jobs pin temp to `RUNNER_TEMP` and skip the hosted-only
  `sudo` disk cleanup on self-hosted runners.
- Any failure selects `ubuntu-latest`. Every selection step is
  `continue-on-error`, so the job cannot fail and skip the required producer,
  which carries no job condition.

A manual PR Gate dispatch on `trunk` can set the boolean `qualify_light` input
(default `false`) to place the PR Gate control jobs on the light pool.
Only `workflow_dispatch` on `refs/heads/trunk` honors this override. It pins the
`honua-light` runner group and label; it accepts no runner label input. Shard
selection and the affected-shard aggregate run only on pull requests, so a
dispatch qualifies runner select, Format and source Analyzers, CI router
validation and the required aggregate. Both test-analyzer rows remain hosted;
producer, heavy-shard and Fast/architecture routing stays unchanged. Other refs
and events keep these jobs on `ubuntu-latest`. Qualification is an operator-run
health check; the input does not enable normal routing or change repository
settings. The authored inventory template records this placement. A pull
request that edits `pr-gate.yml` cannot also submit the generated inventory
page, because that workflow is a generated-file verification input; the trunk
generator publishes the page.

Draft Format and the admission/control jobs stay on `ubuntu-latest` on a pull request.
Runner select, the format/analyzer
matrix, affected-shard selection, the affected-shard aggregate, CI router validation
and the required aggregate each set `TMPDIR`, `TEMP` and `TMP` to that job's
`RUNNER_TEMP` before any other step. Verdict fixtures select the named verdict
step so setup steps cannot replace the failure checks under test. Several self-hosted accounts share one host,
and a fixed `/tmp` path collides across them. Affected-shard heavy placement
(below) additionally requires independent version-1 heavy
availability from the same current-instance heartbeat. An integer idle-heavy count
from 1 through 8 permits the configured heavy JSON selector; absent, invalid,
stale, future-dated or replaced-instance proof keeps shards hosted. Producer
admission still requires two idle gate runners. The selector forwards heavy proof
through the existing Build and tests dependency, without adding aggregate needs.
The heartbeat records availability at selection time; it does not reserve runners.
Until MAIN activates the reviewed additive honua-iac heartbeat, heavy shards fall
back to hosted. Under
`REVIEW_FIRST_MODE: enforce` the selection steps skip and the gate stays hosted:
the review release reruns only failed jobs, so attempt 2 would reuse a choice
made at admission and could queue on a host that has since stopped.

The build-host runners are clean per job but are not hosted images. They have no
sudo, so the disk-cleanup steps do nothing there. Docker is rootless and per job,
and tools come from the setup actions plus the host's packages (Python 3, Node 22,
PowerShell, git, jq). Do not point `HONUA_CI_HEAVY_RUNNER` at this label: full CI
expects passwordless sudo.

A spot interruption fails the running job. Use **Re-run all jobs**, which selects
compute again; **Re-run failed jobs** keeps the earlier choice, and it waits if
the host has stopped since then. Clear `HONUA_PR_GATE_RUNNER` to return new runs
to hosted runners immediately.

## Nightly Lane

These workflows run on schedule and can be dispatched manually:

| Workflow | Schedule | What it validates |
|---|---|---|
| `geoservices-import-fidelity-external.yml` | On-demand (`workflow_dispatch`) | External GeoServices parity + Geoportal import vs live Esri services: runs the correctness regression gate **and** the perf-parity latency gate (issue #1249) over the freshly measured scorecard (deterministic parity stays in Import Fidelity Scorecard Governance); also runs `GeoservicesGeoportalImportIntegrationTests` |
| `routing-nightly.yml` | Weekly Sun 5:00am UTC | pgRouting provider + NAServer routing integration (`Category=Routing`); `PgRoutingFixture` manages its own Testcontainers `pgrouting/pgrouting` image |
| `warehouse-nightly.yml` | Weekly Sun 6:00am UTC | Snowflake/Redshift/Databricks/SqlServer creds-gated provider tests; self-skips cleanly without configured secrets, surfacing pass/fail/skip counts in the run summary |
| `cross-server-consume-nightly.yml` | Daily 7:00am UTC | Honua-as-client WMS/WFS/WMTS reads against GeoServer and MapServer reference containers |
| `client-compat-smoke-nightly.yml` | Daily 7:15am UTC | Full CERT-\* matrix certification (18 test cases × 4 protocol lanes) with `.cert.json` envelopes + reusable evidence pack |
| `pyqgis-client-compat-nightly.yml` | Daily 7:30am UTC | PyQGIS desktop client compatibility (OGC Features + WFS) with per-protocol `.cert.json` envelopes |
| `sdk-server-compatibility.yml` | Monday 8:35am UTC | Manifest-driven last-3 server refs x last-3 SDK sets compatibility matrix through `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`; manual dispatch can pin `server_current_ref` for release-candidate evidence; uses Node 22.23.3 (SDK engine minimum 22.12) with a 4 GiB V8 heap for source compilation; copies checked-out SDK refs to `$RUNNER_TEMP/sdk-compat` before smoke execution so server repo build policy does not affect SDK source builds; records SDK versions, server commit/image field, seed profile, exercised surfaces, migration automation surface status, and diagnostics; publishes the `sdk-compatibility-matrix-<run-id>` table artifact and fails on supported-cell regressions |
| `client-interop-nightly.yml` | Daily 7:00am UTC | Real-client interop matrix via Docker harnesses (`gdal`, `pyqgis`, `openlayers`, `cesium`, `geoservices-rest-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` plus `expected-pairs.json`, refreshes `artifacts/client-interop/gap-report.md`, and fails strict mode on baseline pass regressions, missing current envelopes, missing committed baselines, or new unbaselined failures. Non-blocking until 30 consecutive nightly passes (#806) Protected-style probes resolve the seeded publication ID and independently verify that numeric-layer vector tiles reject anonymous and invalid-key requests. |
| `client-roster-certification.yml` | On-demand (`workflow_dispatch`) | Bounded external-client roster (QGIS, GDAL/OGR, GDAL, MapLibre GL JS, OWSLib, PySTAC-Client) against a candidate image digest; dispatched by the release coordinator at the frozen candidate |
| `gdal-driver-e2e.yml` | Daily 7:45am UTC | GDAL `ogrinfo` + `ogr2ogr` round-trip against honua-server (ADR-0034 stand-in until `honua-gdal` plugin ships) |
| `load-soak-nightly.yml` | Daily 11:30 UTC | Load and soak diagnostics against trunk source; release qualification is `capacity-soak-candidate.yml` |
| `nightly-slow-tier.yml` | Daily 4am UTC | `Tier=Slow&Category=Emulator` .NET tests across `Honua.Server.Tests`, `Honua.Db.Postgres.Tests`, `Honua.Core.Tests` (`[EmulatorTest]` only). LocalStack and Azurite are provisioned by `EmulatorFixture` (Testcontainers) and Postgres by a service container; the workflow asserts `HONUA_TEST_DB_URL` before dispatch. `[ScaleTest]`, `[ExternalServiceTest]`, `[CloudTest]` need dedicated fixtures and are tracked as separate workflows (ADR-0037) |
| `flaky-detection.yml` | Daily 5am UTC | Re-runs a bounded rotating window of `.github/ci-shards.json` shards (default 6/night, 2 iterations each, so the whole set is covered every `ceil(shards / shard_count)` days) and uploads a per-shard flake-candidate report. Each iteration gets a fresh database. An ad-hoc `project`/`filter` dispatch bypasses the shard plan. Reporting only — it never fails on a candidate, but it DOES fail on missing evidence (ADR-0037) |
| `security-nightly.yml` | Daily 2am UTC | NuGet vulnerability scan, Trivy filesystem scan, and container security validation |
| `nightly-container-build.yml` | Scheduled | Container build validation |
| `codeql.yml` | Mon 0am UTC | CodeQL security analysis |

## Release Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `nuget-publish.yml` | Push (tags) / manual | NuGet package publishing |
| `control-plane-sdk-governance.yml` (release) | Release event | SDK release certification |

## Deploy Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `deploy.yml` | Tags / manual | Environment promotion |
| `deploy-platform-images.yml` | Tags / manual | Platform image deployment |
| `cloud-post-apply-validation.yml` | Workflow call / manual | Post-deploy validation |
| `cloud-post-apply-validation-nightly.yml` | Nightly (03:00 UTC) / manual | Post-apply contract against a local target; caller for the above |

## Maintenance Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `release-please.yml` (SDK repos only) | Push | Version automation |
| `disabled-workflow-check.yml` | Weekly schedule | Fails while a workflow disabled on GitHub is still in the tree, unless an active workflow calls it or a dated entry names the ticket that decides it |

## Attesting reviewers

`Review Gate` goes green only on exact-head review evidence from a bot identity
distinct from the PR author. `scripts/ci/review-gate-evidence.js` owns that
identity set and is the single source of truth: it exports `ATTESTING_LOGINS`
(printable via `--print-logins`) and each identity's clean-comment template.
Nothing else may restate a login or a
marker — including this document, which describes the registry rather than
duplicating it.

Two identities are accepted today: **Codex** (`chatgpt-codex-connector`) and
**Claude** (`claude`). Either alone can attest, which is the point: when Codex is
rate-limited ("You have reached your Codex usage limits for code reviews") the
gate would otherwise deadlock and nothing could land. GitHub Copilot code review
is deliberately **not** accepted; the reasoning is in the block comment in
`review-gate-evidence.js`.

### Identity spelling is load-bearing

Evidence reaches the evaluator in two shapes that spell bot logins differently:
REST reports `claude[bot]`, GraphQL reports `claude`. **The gate reads GraphQL**
(`review-gate-snapshot.js`), so the suffix-less spelling is the one that appears
in production — a registry that accepted only `claude[bot]` would make every
review the lane posts invisible to the gate.

Both suffix-less spellings are ordinary account names (`claude` is a real GitHub
User; `chatgpt-codex-connector` is a registered Organization), and an account can
hold a PAT, so each is accepted only when GitHub additionally types the author as
a `Bot`. That is why every `author` selection in the snapshot query carries
`__typename`, and why `reviewerFor(login, typename)` takes two arguments.
Dropping `__typename` from the query silently disables both identities, and the
gate then fails open: reviewer threads and objections stop blocking.

### The Claude lane (retired)

`.github/workflows/claude-review.yml` was disabled after the 11-11 review-path
ruling and deleted in simplify A4 (2026-11-15) with its helpers. The registry
still grades a review the Claude GitHub App posts, so the identity rules above
stay; nothing in this repository requests one any more.

## Adding New Checks

### Archived quickstart customer journey

`shipped-quickstart.yml` runs nightly and on package-related PRs. Nightly/manual
runs consume the newest available official candidate archive and its source-bound
customer document. PRs package their exact source revision with that candidate's
immutable image lock and a successful official Console trunk artifact. Their
receipts identify this as a prospective PR package, not a shipped artifact.
The nightly producer includes the static Console bundle, checksum and producing
Console workflow/run/source identity inside the customer archive. Cross-repository
artifact access uses a Console-scoped bot token with only Actions read permission.
The separate installation job replaces the disposable hosted runner's Docker
daemon with an empty data directory. It compiles no server code and never prunes
a shared developer host.

Assertions cover both documented profiles, completed initialization services,
healthy runtime services, secret-free rendered Compose configuration, documented
sample publication/query, HTTPS password sign-in to Console, an authenticated
embedded Studio session, idempotent initializer/credential preservation, removal
of all containers and volumes, and fresh credentials on a second installation.
Passwords travel to the browser through a generated secret file; reports, traces,
screenshots and videos are disabled, and browser output is withheld on failure.
Missing Console package files or immutable release pins fail. The external installed
proof is an explicit package-path PR exception to the ordinary five-minute
PR guidance below; offline controls run separately. This workflow does not dispatch
full CI or change required contexts.

Receipts have scope `archived-customer-journey`, distinguish `candidate` from
`prospective-pr`, and retain official publication run and archive digests.
`customerJourneyComplete` becomes true only after both complete installations
and their cleanup pass. A prospective PR receipt cannot qualify a shipped archive;
release verification needs a recorded pass from the published candidate.

Command output stays withheld; a failed command reports its operation, exit
code and fixed signal labels. Compose prints `pull access denied` for an image
the package builds locally (`LOCALLY_BUILT_IMAGES`: the PostGIS datum-grid image)
before building it, so that line does not count when a build line follows it;
denials of any other image, including official Docker Hub images Docker names
without a path, still count. `localBuildStarted` says whether a build started.
When initialization or the health read fails, the receipt also records the
Compose version and each service's name, state, health and exit code (60-second
reads, only short identifier-like values), and teardown still runs.

### GP Scoped Key Contract

`gp-scoped-key-contract.yml` runs one job per test project (6 jobs). Each job
builds its project once and runs every case with `--no-build`, its own filter,
its own TRX and the same execution proof: every source-derived method and theory
row, required class and named method must run and pass
(`scripts/ci/gp-scoped-key-contract.py`). Cases that never used PostgreSQL keep
an empty `HONUA_TEST_DB_URL`. Before this, 8 of 13 jobs each compiled
Honua.Server.Tests for one filter: run 43759686692 spent about 5,950 job-seconds,
4,000 of them in those 8 jobs, mostly compiling. A failing case no longer stops
the others; the job fails after all of them run.

### Check registration

Before adding a new workflow or check:

1. **Identify the tier.** Default to nightly unless the check meets PR-lane criteria.
2. **PR-lane criteria:** deterministic, < 5 min, author-actionable, no external dependencies.
3. **Document the new check** in this file and in `workflow-inventory.md`.
4. **Use path filters** for PR-lane checks that only apply to specific code areas.
5. **Follow artifact conventions** from `config-conventions.md`.

### Positive execution for server shards

`run-server-test-shard.sh` enables VSTest `RunConfiguration.TreatNoTestsAsError`
for every catalogued shard and removes only that invocation's named TRX before
launch. A successful process exit also requires the existing TRX tripwire to
find at least one passing case, no failed/unknown outcomes, and declared counters
consistent with actual result rows. All-skipped, absent, empty or malformed TRX
cannot pass; legitimate partial skips retain their existing policy. A child
failure or timeout retains its original exit code even if partial TRX contains
passing cases.

PR and full-CI shard jobs propagate that exit code without suppressing test
failures, and upload diagnostic artifacts even after failure. Both aggregates
re-read the selected shards' TRX files and require complete passing receipts
and successful jobs. Full CI also checks the merged foundation TRX outcomes;
foundation security build and test failures count even when built separately.
`CI Gate` depends on these enforced test summaries.

Each shard job uploads its results as `<artifact>-attempt-<run_attempt>`.
The PR Gate aggregate (`scripts/ci/download-affected-receipts.py`) lists the
run's artifacts and downloads only the newest id for each selected shard whose
attempt is at most the current run attempt. It reads the compact receipt
matrix (shard name and artifact suffix), not the execution matrix, so test
filters stay out of the job environment. A shard a failed-jobs rerun did
not repeat keeps its earlier receipt; an expired, ambiguous, digest-missing,
digest-malformed or digest-mismatched newest receipt fails closed and is not replaced by an older copy. Full CI still
downloads every attempt directory by name, without merging them. Each shard's
highest attempt present is the one counted, so a rerun's receipt replaces the
earlier attempt's. A newer failing attempt still overrides an older passing one.
Before this change the
aggregates merged same-named artifacts, and GitHub's artifact ids do not follow
upload order: on honua-server#7648 run 43603773296 attempt 2, attempt 1's empty
`Packages and Temporal` artifact outranked attempt 2's receipt, leaving 5/6
receipts. This adds no jobs or runners and changes no required check names or
`needs`; the receipt count, TRX re-verification and job-result checks are
unchanged. The quarantine publisher keeps one artifact per shard, the newest
attempt.

`CapabilitiesMetadataXmlTests` now lives in the Wms namespace and is covered by
Maps Supporting. Remove obsolete standalone metadata entries and the stale
Classic-namespace WMTS clause: they select no tests, and duplicate shard names
break the budget selector partition. The frozen WMS partition still covers all
134 methods (61 Core, 73 Supporting), with its original budgets and source
triggers plus catalog metadata invalidation.

The retained `*.timing.json` includes `execution` with actual result counts,
TRX SHA-256, source SHA/dirty flag, project, and runner/verifier hashes. The timing
record retains the effective filter and original shard status. Its count status
can be positive while the overall shard fails (for example a killed child after
partial passing results); consumers must require the overall successful outcome
as well. Missing counts stay missing. Synthetic supervisor fixtures are labelled
as such and do not qualify a candidate. These files are diagnostic provenance;
local files alone do not constitute a trusted release attestation.

## Bounded flaky-test quarantine

The founder-approved quarantine policy in [known-flakes.md](known-flakes.md#automatic-bounded-quarantine)
allows one same-job failed-method retry within the original shard budget. Success requires
complete passing retry evidence, retained alongside the first attempt. Ambiguous theory-row
names in selected failed methods (including passing siblings), or anywhere in the retry,
are rejected so a passing sibling cannot conceal a missing failure. Duplicate display names
in unrelated passing methods remain untouched in the merged results. Infrastructure and
execution failures remain blocking. Each test command runs in a dedicated process group.
Cancellation signals that group and its snapshotted descendants, including those that leave
the group. Signal handlers record cancellation; cleanup runs outside the active child wait
so it cannot reenter the subprocess wait lock. The supervisor checks cancellation at most
every 0.1 seconds. The snapshot before TERM has a 0.2-second discovery deadline, with group
cleanup preserved on discovery failure, followed by half a second for TERM. Group escalation
also reaches children forked after the snapshot. The SIGKILL step first stops the tree
(SIGSTOP) until a fresh listing finds no new descendant, each listing bounded to one second
because it runs inside the kill-after grace, then kills it deepest first and sweeps the group,
so a late child that left the group is killed instead of orphaned. Required check names and minimum-execution checks remain
unchanged. Bot-authored issues provide a validated, project-scoped exclusion list capped at
25 methods and 14 days per entry; expiry restores gating automatically. Foundation and lean
gate duplicate coverage consume the same list. A separate `Flaky Test Detection` job runs
quarantined methods without exclusions or retries and is explicitly non-gating.
Before execution, the shared filter runner logs `HONUA_QUARANTINE_FILTER` with the effective
filter and the current project's excluded methods, owning issues and expiry times. Shard
test-result logs retain this record so live verification can distinguish the applied filter
from the supervisor's base filter; the record does not replace execution or retry evidence.

The publisher has issue-write permission only on trusted default-branch code, reached by
a twice-hourly schedule or default-branch manual dispatch. Completion events do not
start full-history scans: at the measured 159 listing requests per sweep, two scheduled
sweeps consume 318 history requests per hour before bounded artifact inspections. Checkout is pinned to the default branch;
checkpoints from branch dispatches are ignored. It validates source workflow/repository provenance, raw case
evidence and hashes, and never executes downloaded data or checks out the triggering branch.
Job-level concurrency serializes issue writes and replaces pending sweeps. Source
provenance and completion filters remain enforced during discovery. Each Actions listing
reads at most eight pages concurrently and reports page progress. A listing whose total
changes mid-scan (a new run) restarts, at most three attempts in all; it fails closed
(checkpoint unchanged) when the total keeps changing or the unique entry count disagrees
with the total.
Each sweep lists the bounded histories of CI and PR Gate, selects completed trusted runs
updated since the checkpoint, and reads artifacts per source run. It never lists the
repository-wide artifact collection. Full creation-ordered histories preserve discovery
of recent reruns of old source runs. The sweep selects each shard's highest attempt and
inspects artifacts for at most 25 source runs, including runs with no evidence. Completed
run versions let equal-timestamp backlogs advance without repeatedly spending that budget.
When more than 25 unprocessed run versions remain, five slots serve the oldest pending
runs and twenty serve the newest. This preserves historical reconciliation while new
evidence avoids waiting behind months of empty runs; processed versions do not spend
either allocation again. Deferred evidence remains behind the watermark.
Source completion and attempt identity are rechecked after artifact discovery; a racing
rerun is deferred without consuming its artifacts. Its update watermark retains a five-minute overlap and
never passes the oldest deferred run, including runs with equal timestamps. Remaining
evidence stays discoverable for the next scheduled or manual sweep.

Only a fully successful publication uploads its checkpoint; failed publication or checkpoint
upload replays from the previous successful checkpoint. Successful skipped jobs are ignored
when finding that checkpoint. Artifact IDs prevent repeat downloads, and source-attempt
links use the artifact's producing attempt, preventing a partial rerun from changing
unchanged evidence identity. Previously quarantined evidence timestamps also prevent
legacy backfill from reopening expired/closed issues. Missing new
checkpoints, unsafe archives, excessive inventory and untrusted sources fail closed.
Migration reconciles every retained artifact rather than treating one successful legacy
event as a global watermark; first installation
backfills 14 days. The checkpoint retains processed IDs from source runs inspected
in the current sweep and expires after 90 days. Recovery therefore requires source evidence still retained by
GitHub. The existing 25-method cap, 14-day expiry, proof validation and required gates remain
unchanged. This adds no PR-time jobs or runners; recovery uses one publisher job per sweep.


### Grouped unit and component shards

The server matrix combines 33 small unit/component assemblies into four jobs:
Provider and AI Units, Runtime Units, Protocol Units, and Protocol and Host
Components. Each `csprojs` list is an explicit assembly union. Routing, build
selection, resource classification, and the source coverage guard inspect every
member. Catalogued Docker and PostgreSQL dependencies remain supported;
resourceful members retain service provisioning. Unknown resources fail closed.
The existing required check names and tier policies remain unchanged.

The shared runner executes each member with its own fresh TRX and timing record,
within one shared 20-minute test budget. Every assembly must execute tests and
pass; a passing sibling cannot conceal an empty, failed, or timed-out assembly.
It also emits a combined TRX and timing receipt for the existing aggregates.
Capability selection retains a group's full filter so sibling assemblies cannot
receive empty proving-test selections. Shared-budget timeouts retain the existing
capacity-versus-hang classification, progress measurement, and diagnostic tokens.
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

Before refreshing a behind branch, lanes run
`bash scripts/ci/compute-affected-projects.sh --trunk-overlap HEAD origin/trunk`.
Its JSON `refresh_required` compares committed changes on both sides of their
merge base using the canonical reverse-dependency selector. Disjoint project
closures avoid an unnecessary merge and gate restart. Non-managed files use
their top-level folder; root files, changed project graphs and unavailable refs
require revalidation conservatively. This decision does not resolve conflicts
or attest test execution. Callers must treat an execution or parsing failure
as requiring revalidation. Commit local changes before using this mode.

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
run at most two at once after the 40-job admission cap (full runs run at most 16). Expensive AOT, Docker,
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

Normalization consumers preserve a producer’s recorded superseded admission across same-SHA draft-to-ready transitions, using exact-attempt Actions job outcomes. If both producer jobs were skipped by job-level admission (draft or non-base edit), the consumer also returns without validation or mutation. Missing envelopes from ordinary producers remain errors.

### Router validator execution

Shard ownership lookups reuse parsed filter syntax in a bounded, process-local cache keyed by the complete filter text. Each method name is still evaluated separately; changed filters are parsed separately and malformed filters still raise errors. The affected-shard fixture retains its complete ownership and mutation checks. This changes CPU work within existing jobs, without changing their selection, runner count, or required aggregate.

`validate-ci-router.sh` runs independent fixtures in a bounded pool (four workers by default; `HONUA_ROUTER_WORKERS=1..8`), records each fixture duration and collects every exit code before returning failure. A failed or missing result cannot produce success. The routing assertions retain their exact cases and shard ownership checks. Cancellation sends TERM to owned worker groups, escalates to KILL after a bounded grace period, and reaps workers before deleting results.

`--changed-only` is a local feedback mode: stdin routing cases skip only when their complete input closure (router, shard map, jq wrapper and validator infrastructure) is unchanged against the merge base with `origin/trunk`. Staged, unstaged, renamed and untracked inputs count. Unknown base or an ambiguous path runs everything; undeclared fixture input closures also run. CI and GitHub Actions ignore the optimization and always run every fixture.

The PR managed producer compiles `Honua.Server.Tests` in a parallel, review-admitted job. The main producer excludes that project and the full-coverage architecture collector, whose imported references pull Server.Tests into its graph. The independent architecture path and analyzer jobs retain collector compilation and verification. The shared import-aware project parser rejects any other reverse dependency on Server.Tests. The parallel producer publishes the Server.Tests output after every non-docs build, and publishes other managed projects only when a selected shard consumes them. The `fast-server-tests` job restores Server.Tests for `server-fast` and `server-governance` and compiles that project only when the restore is empty. The final required context directly requires the parallel producer on both pull-request and manual events; the PR-only affected-shards aggregate also checks it for an empty shard matrix. Only the affected-shard matrix cancels sibling shards after a failure; fast-test and analyzer matrices retain every result.
Fast-test selection retains every group for linked cross-project inputs and for empty closures unless the governed docs classifier positively proves the diff safe.

### Server.Tests restore for the fast server groups

`server-fast` and `server-governance` run on `fast-server-tests`.
The `fast-tests` matrix keeps `architecture` and `unit-fast` only.
Those two groups still compile their own test roots, start with runner select, and do not wait for the Server.Tests producer.

`fast-server-tests` needs `runner-select` and `server-test-producer`.
It does not need Build and tests.
The check names stay `PR Gate / Fast and architecture (server-fast)` and `PR Gate / Fast and architecture (server-governance)`.
Fail-fast is false on both matrices, so one server group does not cancel the other, and a fast-server failure does not cancel architecture or unit-fast.
The job timeout stays 45 minutes.
The runner expression matches `fast-tests`.
It uses `HONUA_HEAVY_RUNNER` when `start_heavy` contains that test group, unless the event comes from a fork.
A missing `HONUA_HEAVY_RUNNER` stays on `ubuntu-latest`.
`workflow_dispatch` follows the same expression. This workflow has no `workflow_run` trigger.
On a draft, `runner-select` is skipped, and `fast-server-tests` is skipped with it because the job has no `always()` condition.

On every producer build that review-first has released, and that is neither docs-only nor generated-only, the producer publishes `tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj` unless `HONUA_MANAGED_TEST_ARTIFACTS` is `false`.
When the selected-shard project list does not already contain that project, the plan step appends it before publish.
A pull request that selects no Server.Tests shard still leaves a build for the fast server groups.
Other managed projects stay published only when a selected shard consumes them.

Each fast server group runs these steps, in order, unless a skip below applies:

1. Await exact-head review. In enforce mode, pull-request attempt 1 exits before checkout.
2. Check out the head with fetch depth 2.
3. Classify a generated-only diff, then a docs-only diff.
4. Select affected fast-test groups with `compute-fast-test-selection.py`.
5. On a GitHub-hosted runner, free disk in the background.
6. Set up .NET with `artifact-sdk-version` taken from the producer output `managed-sdk-version`.
7. Load the flaky-test quarantine snapshot into `HONUA_TEST_QUARANTINE_FILE`.
8. Plan a one-project consumer for `Honua.Server.Tests` through `managed-test-build.py projects --lane shard` and `partition-managed-producer.py consumer`. The plan file is `artifacts/pr-gate/fast-server-projects.json`.
9. Restore that project from `needs.server-test-producer.outputs.producer-attempt` when the plan names the server producer and `HONUA_MANAGED_TEST_ARTIFACTS` is not `false`.
10. Record `restored=true` when the restored project list contains `Honua.Server.Tests`, and `restored=false` otherwise. A missing receipt file is `restored=false`.
11. Run lean-gate with `phase: test` when `restored=true`. That phase does not compile the project again.
12. Run lean-gate with `phase: all` when `restored=false`. An empty restore compiles the test root in this job. It does not skip the tests. Artifacts disabled, a missing receipt, and a consumer plan that does not name the server producer are empty restores.

Free disk, .NET setup, the consumer plan, the restore record, and lean-gate are skipped when review-first has not released the job, when the diff is docs-only or generated-only, or when fast-test selection sets that group to `false`.
The restore action itself runs only when the consumer plan names the server producer.
Classification, fast-test selection, and the quarantine snapshot still run whenever earlier steps succeeded.
Unknown selection still runs the group.
Lean-gate uses `trx-prefix` `pr-fast-<group>`, `build-scope: test-roots`, `run-format: false`, and `run-analyzers: false`.
`server-governance` sets `prepull-postgis` true. `server-fast` does not.
Both groups still regenerate the admin parity exports with `--no-build --no-restore` before the tests.
`phase: test` uses the restored output. `phase: all` has already compiled it.
Format and analyzers stay off on these groups.
The analyzer matrix still compiles the architecture collector on its own job.
In review-first enforce mode, pull-request attempt 2 and later revalidate the exact-head review after lean-gate, before the job reports success.
That step uses the same script as the producer, `fast-tests`, and the required aggregate.
Attempt 1 does not reach it: the await step fails the job before checkout.

The required aggregate lists `fast-server-tests` in `needs` next to `fast-tests`.
`required.needs` is `pr-gate`, `fast-tests`, `fast-server-tests`, `format`, `ci-router-validation`, `affected-shards`, and `server-test-producer`.
On a ready pull request, and on manual dispatch, the aggregate fails unless `fast-server-tests` is `success`.
`skipped` is not success there.
A draft still accepts `success` or `skipped` for this dependency and fails on `failure` or `cancellation`.
Format must still succeed on a draft.
The ready context name stays `PR Gate`. A draft still reports `PR Gate (draft)`.

Affected shards still restore Server.Tests only when their own closure consumes it.
Shard filters, `pr_time_budget`, and the tests each group runs stay as they are.
The required check names stay `PR Gate` and `PR Gate / Affected shards`.

The split removes the second and third compile of `Honua.Server.Tests` from the fast path.
The wall clock of a pull request stays bound by whichever remaining job is slowest, often architecture, which still builds its own roots.
Hosted runner-minutes fall by the compiles that the restore replaces.
A gate that skips both server groups, or that takes the empty-restore compile path, does not record that saving.

Review revalidation shares `.github/scripts/revalidate-exact-head-review.cjs` across producer, fast-test and aggregate jobs. The aggregate checks out code only for enforced attempt-2 review validation. Generated-output classification shares `scripts/ci/classify-generated-output.sh`; executable skip policy still comes from the immutable PR base, and uncertainty preserves all checks.

Normalization consumers preserve a producer’s recorded superseded admission across same-SHA draft-to-ready transitions, using exact-attempt Actions job outcomes. Missing envelopes from ordinary producers remain errors.

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
filter. Retired Redis endpoint selectors are removed; the coverage guard still
rejects dead positive clauses, and the partition fixture verifies complete,
disjoint method ownership with unchanged source triggers and budgets.

### Layer publishing shared application fixture

`LayerPublishingIntegrationTests` shares one customized host and feature schema across
its cases. Each case uses distinct layer, table, service, and secure-connection identities
and cleans up its publications and source tables. Before each case the fixture reapplies
the server seed, which deletes and recreates the seeded features and catalog metadata, and
restores the metadata graph with a new revision.
The missing-SRID simulation creates and removes its expression index under the schema lock.

### WPS endpoint fixture lifetime

`Wps20EndpointsTests` shares its default application and identically configured
conformance-echo application for the lifetime of the sequential test class.
Each application seeds its database once. Tests with service substitutes or
custom host configuration retain isolated applications; echo results are
accessed through their request-generated job IDs and reference tokens.
All cases and assertions remain in the WFS 1–9 shard family on selected PR runs
and twice-daily full trunk runs. This changes no runner count, required check
name, required aggregate dependency, workflow, or shard definition.

### ImageServer endpoint database lifetime

`ImageServerEndpointsTests` retains a `PostgresFixture` reference for the class
to avoid repeating shared database initialization between cases. Default-store
read-only cases share the class-owned application and schema; cases with custom
configuration own their applications, schemas, and raster substitutes. Class
fixtures release the shared infrastructure after the last case. Pure capability XML
coverage lives in `ImageServerWmtsCapabilitiesTests` without a database fixture.
Full trunk runs include every case; PR routing selects affected ImageServer shards.

### Geocoding response fixture lifetime

`GeocodingEndpointTests` uses one class-lifetime Community application for
request-derived default-provider responses. Each case owns its HTTP client.
Request-capture, custom-provider and Enterprise configurations own separate
applications. Full trunk runs include every case; PR routing selects affected geocoding shards.

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
passing job duration (whole-second job timestamps), rounded half-to-even to
0.1 minute, across the five "CI full" trunk runs whose head contains both #9118
(shard disk-cleanup skip) and #9168 (leak-detector GC sweep skip, warm
PostgresFixture pool), 44991760272 to 45124758997. Together they cut every
child's job time: full runs before them spent 826 to 878 server runner-minutes,
the one run with only #9118 657, and the five with both 430 to 451, so older
samples no longer predict a child and are not reused (the previous record,
44131638572 to 44921455496, ends before them).
Post-merge "CI affected" runs are not samples. A sample counts only when the
shard's filter and project selection at the run's head match the current
definition. A run supplies at most one sample per child (its latest passing
attempt). Passing children count even when their full workflow fails or is
cancelled; failed, skipped and cancelled children do not supply timing samples.
Every measured definition has 3 to 5 matching passing samples; refresh again
once more full runs have completed.

`scripts/ci/fixtures/measured-dispatch-ranks.json` records the ranks and runs;
`test-full-run-capacity-partitions.py` rejects rank drift. Split children use
their measured rank, else their contract estimate, else the parent's rank.
Remove a child's estimate once measured; the estimate-sum conservation rule
applies only while every child is estimated. The one remaining estimate is the
packed GeoServices GPServer SOAP job (5.0, `soap-packing-contract.json`), which
has no passing full-run sample at its new definition yet.
Live drift (#9545, #9546, #9547): `shard-rank-drift.yml` runs after each completed
"CI full" trunk run and compares every rank with the median passing job minutes
of the latest three full runs (`scripts/ci/shard-rank-drift.py`). It keeps one
`area/ci` tracking issue (owner s-ci-shards) open while the total rank/measured
ratio is outside 0.77-1.30, a shard's median is at least 1.5x and 3 minutes over
its rank, or a shard absent from the measured record has two or more samples; it
closes the issue when none remain. Under 80% of shards measured, it changes
nothing. It is report-only and never gates a PR or trunk.
The timing repair's exact partitions, execution policies and evidence remain intact.

An explicit Temporal source override uses `prioritize: true` to keep a narrow
source owner ahead of broad inherited coverage when changed-test and path-hit
counts tie. It changes ranking only: candidate admission, the six-job cap and
time budgets still apply. Unmarked overrides retain their previous ranking.
The schema accepts only boolean override priorities; malformed values fail validation.

Ranks sum to 436.8 runner-minutes, previously 827.1 (877.5 at the 21 Nov
record, before #9489 packed the SOAP pair and earlier merges packed 225 children
into 202). The six largest ranks sum to 23.9 minutes, previously 48.6, so a
six-shard PR selection no longer exceeds the 45 runner-minute PR time budget and
defers shards to trunk on rank inflation alone. These are scheduling estimates,
not measured runtime savings. The separate tiny-shard consolidation measurement
remains 75.38 to 12.11 runner-minutes (63.27 saved).

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

Each affected shard selects the SDK and workflow-attempt identity exported by its own managed producer. Managed consumers also install the target framework’s .NET and ASP.NET shared runtimes into that private SDK root using the supported runtime bootstrap. The target channel comes from `HonuaTargetFramework`; non-versioned host files are preserved and exact producer SDK identity is verified afterward. This allows a newer producer SDK to execute older target artifacts without major runtime roll-forward or artifact changes. It adds no jobs and changes no required checks. The 12 November baseline aborted net10.0 tests and projection regeneration before execution when only the SDK 11 runtimes were present; recovery timing awaits a fresh eligible gate. A shard spanning both producers rebuilds its projects locally instead of combining independently resolved SDK outputs. A shard whose whole closure is Honua.Server.Tests starts when the parallel producer finishes. Other closures wait for the main producer in Build and tests. Fast-test selection reads imported build documents through the shared project parser; unresolved imports or linked inputs retain all four groups.

### Test analyzer compilation roots

The ready-PR format matrix retains `PR Gate / Analyzers (tests)` and adds `PR Gate / Analyzers (server tests)`. The latter compiles Honua.Server.Tests and Honua.Architecture.Tests, whose generated coverage references also require Server.Tests. The former restores the remaining selected solution roots, then builds their evaluated test roots and conservative multi-targeted roots, avoiding standalone source builds and a second Server.Tests compilation through the collector. The server root is also retained when an affected root imports or transitively references it. Forward dependencies still compile. The `server-tests` and `other-tests` diagnostic partitions assign Server.Tests and the collector exclusively to the server job and all other test diagnostics to the other job. Warnings remain errors. A partition with no roots skips restore and build explicitly. The required format matrix waits for both jobs; drafts retain Format only. The first split measured 11.13 minutes for other tests, but a subsequent run took 13.32 minutes; moving the collector addresses that duplicate compilation. The revised timing must be measured on a hosted gate run.

### Optional PR workflow input boundaries

Capability Impact Comparison was retired after being disabled in repository
settings; its workflow has been removed. Capability Matrix Aggregation and
OpenAPI Contract Governance retain their generator/contract input filters.
Aggregation includes `FeatureCatalog.cs`: its licensing constants resolve manifest
entitlements, so a change to those constants must run the crosswalk check even
when the registry is unchanged. This admits the existing single aggregation job
for that input and adds no jobs to unrelated PRs. The
operation-capability completeness, registry resolution and authored artifact
checks run in PR Gate's existing architecture job after canonical feature
catalog regeneration. Capability Matrix Aggregation retains these checks for
scheduled/manual audits of committed trunk projections. New authored routes
therefore use the catalog generated from the same head, while missing joins
and stale authored operation-capability artifacts still fail. No additional
build, runner or required check is added. Both Server Fast and governance
jobs already regenerate admin parity exports, including the MCP manifest,
before the strict Server.Tests projection checks.

GeoArrow and GeoParquet interop run on pull requests only for files whose names
contain GeoArrow or Parquet (sources, tests, CRS resources, test data), the few
shared helpers GeoArrow names, GeoParquet's format dispatch, PAR1 detection,
streaming import and limits files, the Python readers and seed data, and the package
graph (Directory.Packages.props, global.json, NuGet.config). Shared build, model,
host, protocol and TestKit edits run them in the twice-daily full CI instead:
ci.yml calls both through `workflow_call`, exactly like the store image proofs,
and `CI Gate` requires their success on scheduled and `full_ci=true` runs. A
called run's concurrency group falls back to the run id, so a standalone trunk
dispatch never cancels it. Their
.NET formatter and import cells also run in the PR Gate affected shards.
Measured on the 120 PRs merged since 9 Nov: GeoArrow 30 to 6 PR runs (4.4 runner
minutes each), GeoParquet 34 to 15 (10.2 minutes each), about 300 runner minutes.

Export Portolan Check (#8268) is advisory and not required. It runs on ready
pull requests that touch Exports (paired publication sources and tests, the
publication descriptor model and schema, `Exports/` folders) or the check itself,
on one hosted runner. The paired-publish integration test publishes an Export and
copies its version files to `HONUA_EXPORT_SNAPSHOT_DIR`; the validator pinned in
`scripts/ci/portolan-pin.v1.json`, the one place the specification version lives,
checks that copy. Specification findings are reported and never fail the job. A
failing test, an empty snapshot, a validator crash or a validator that applies a
different specification than the pin fails it, so a broken check never reads as a
clean Export. The same test also runs in the PR Gate affected shards.

Docs Link Gate retains its complete PR suite for documentation pages, skills,
checker and manifest edits, and for every docs file its checkers read
(capability keys and matrix, install artifacts, the OKF graph, llms.txt, the
GitBook redirect map, and the deploy examples the anchor manifest sweeps). Other non-page
files under docs/ are only link targets; the trunk link check covers them. Source/docker/eng-only edits instead run the link checker and
its adversarial fixtures in the existing ci.yml CI Router Validation job on affected trunk
runs and twice-daily full runs. That job already participates in CI Gate; no
required check name or aggregate dependency changes. The runtime URL scan still
runs and fails the trunk gate when a source/config link breaks.

Derived Artifact Normalization runs on pull requests only when its own recipe
can change: the producer workflow, the .NET setup action, the envelope builder,
every generator input the envelope pins (`GENERATOR_INPUTS` in
`scripts/ci/normalization-envelope.py`) and the three projections. Its consumer
remains observe-only. Source PRs carry their regenerated projections; the required
architecture job verifies submitted output directly, so an envelope for an
ordinary source change does not publish or repair its branch.
That generation still runs: PR Gate's architecture job regenerates the same
projections and reports drift on every ready PR, ci.yml's foundation job does
the same on affected trunk and twice-daily full runs, and the nightly
generated-files-on-trunk writer publishes them. `validate-optional-pr-inputs.py`
fails if a pinned generator input stops admitting the producer. The
producer/consumer security boundary and deterministic replay are unchanged.

The review-first and router validators project the workflow inventory into a
scratch file (or compare it with `--check`) instead of rewriting the tracked
page. The page is trunk-published and may lag the workflow YAML; a rewrite left
the PR Gate checkout dirty, and the later private-feed restore proof, which
requires an unchanged tracked checkout, failed on any PR touching its inputs.
Among the `scripts/ci/*.sh` entry points only `regenerate-generated-files.sh`
rewrites the page, and `generate-workflow-inventory.test.py` enforces that. The
doc-link steps in ci.yml (after the router validators) and Docs Link Gate
regenerate it on purpose so links are checked against this head's projection.

## Server test fixture lifetimes

Class-scoped test applications and database infrastructure that shorten server
test shards without changing what they run.

### WFS 2.0 endpoint fixture lifetime

Wfs20EndpointsTests shares one seeded Community-licensed WFS 2.0 application for
the lifetime of the sequential test class, used by every read-only case:
capabilities, DescribeFeatureType, GetFeature, GetPropertyValue, stored-query
listing and description, and error-report checks. Cases that send a WFS
Transaction, create or drop stored queries, or write features or resource
metadata through the fixture start their own application within the case.
Managed stored queries are held process-wide rather than per application, so a
stored query created by one case stays visible to later cases; no case asserts
an exact stored-query count. Cases with custom metadata providers, licenses, or
service replacements also keep their own per-case applications. Five read-only
Pro metadata/query applications borrow only the initialized class schema: prefixed
field names, projected extent, full declared field types, and the two inferred
geometry cases. Each retains its separately configured metadata provider, license,
services, client and disposal. Borrowers cannot drop the owner schema, which
outlives every case in this serial class. Transactions, managed stored-query
mutations and metadata writes retain private schemas.

All 440 assertion lines, 400 test metadata lines and 94 asynchronous methods are
unchanged. All cases and assertions remain in the WFS Endpoints shard on selected PR runs
and twice-daily full trunk runs. This changes no runner count, required check
name, required aggregate dependency, workflow, or shard definition.

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

### Cross-protocol permission matrix fixture lifetime

CrossProtocolPermissionMatrixTests starts one application for the whole class,
configured with the wildcard query grant, and shares it across the ten
read-denial cases for FeatureServer, OData, OData batch, OGC API Features and
OGC API Tiles. Each of those cases creates its own HttpClient with its own user
and role headers, so no login state or cookies are shared between cases. Every
write case, every ApplyEdits parity case (FeatureServer and gRPC-write), and the
layer-scoped grant cases still start their own application, because their grants
or edits differ per test. The shared application is disposed when the class
finishes, or when a shard finishes its share of the class.
All cases and assertions remain in the Security and Authorization 1 and Security
and Authorization 7 shards on selected PR runs and twice-daily full trunk runs.
This changes no runner count, required check name, required aggregate
dependency, workflow, or shard definition.

### FeatureServer endpoint fixture lifetime

FeatureServerEndpointTests starts one seeded, Pro-licensed application per test
class, through an xUnit class fixture, and every read-only metadata and query
case reuses it with the same schema and editing capabilities. Any case that
edits features, adds data or changes metadata calls
`UseIsolatedApplicationAsync()` first and gets its own application and schema,
which is disposed when the case ends. The shared application is never written
to, so the read-only cases do not depend on order. The class still runs one case
at a time in the `Database.CoreEndpoints` collection, and method names are
unchanged, so the Core Endpoints shard filter is unaffected.
All cases and assertions remain in the Core Endpoints, Core Endpoint Queries,
and Core Endpoint Mutations shards on selected PR runs and twice-daily full
trunk runs. This changes no runner count, required check name, required
aggregate dependency, workflow, or shard definition.

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

### FeatureServer RBAC denial fixture lifetime

FeatureServerServiceRbacTests uses one application per class, built once through
xUnit's class fixture, for cases where authorization must reject a request
before any write. Each case still creates its own HTTP client with its own role
headers, so no authentication state is shared between cases. Cases that write
successfully, or that create a replica as setup, still build their own
application and discard it when the case ends.
All cases and assertions remain in the Security and Authorization 5 shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

### Routing async web tool database lifetime

`RoutingAsyncWebToolTests` holds a class-level `PostgresFixture`, which keeps
the shared, reference-counted PostgreSQL container and data source alive for the
whole class instead of rebuilding them for each case. Each case still creates
its own no-Redis `WebAppFixture` host with an isolated schema, cleared job
tables, its own substitute routing provider and its own durable worker.
All cases and assertions remain in the GeoServices GPServer and NAServer shard
on selected PR runs and twice-daily full trunk runs. This changes no runner
count, required check name, required aggregate dependency, workflow, or shard
definition.

### Capability manifest endpoint fixture lifetime

`CapabilityManifestEndpointTests` starts its default manifest application once
per class through an `IClassFixture` and disposes it when the class finishes.
Each case still makes its own anonymous and admin `HttpClient` against that
host, and only cases that read without writing use it. Cases that need a
different license, configuration or set of services build, start and dispose
their own `WebAppFixture` inside the test, so configured applications stay
isolated from each other and from the shared one.
All cases and assertions remain in the Server Features Capabilities shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

### ImageServer raw pixel export database lifetime

`ImageServerRawPixelExportTests` holds a class-scoped `WebAppFixture` reference,
which retains both the default web host and shared PostgreSQL infrastructure
across its integration cases. Each case still creates its own fixture, client
and isolated schema, and seeds the raster under the schema-mutation lock. The
pure LERC/BIP encoder unit tests live in `ImageServerPixelBlockEncoderTests`,
which has no fixture, so running only unit tests never starts a database.
All cases and assertions remain in the selected raster rendering shard and
twice-daily full trunk runs. See the raw pixel host lifetime section below for
paired measurements. This changes no runner count, required check name,
required aggregate dependency, workflow or shard definition.

### ImageServer mosaic integration database lifetime

`ImageServerMosaicIntegrationTests` retains the default web host and PostgreSQL
infrastructure through a class-scoped `WebAppFixture`. Each case still creates
and disposes its own fixture and isolated application schema, and seeds its
raster under the schema-mutation lock. Cases that configure license entitlements
continue to build private hosts. The existing warm raster query performance
assertion and all response assertions remain unchanged.
All cases and assertions remain in the GeoServices ImageServer Raster Rendering 2
shard on selected PR runs and twice-daily full trunk runs. This changes no
runner count, required check name, required aggregate dependency, workflow, or
shard definition.

Paired build-host runs passed the same 20 cases with no failures or skips.
VSTest execution fell from 115 to 46 seconds (69 seconds, or 1.15 execution
runner-minutes saved). The fixture-inclusive TRX interval fell from 133.049096
to 73.394077 seconds (59.655019 seconds saved). Both runs used one test
process; complete CI shard/job savings remain unmeasured.

### GPServer endpoint application lifetime

`GPServerEndpointTests` shares one default application, isolated schema and
in-memory execution-job store across its default-host cases through a class
fixture; see the GPServer default application lifetime section below. Cases
that replace services or configuration still build their own hosts.
All cases and assertions remain in the GeoServices GPServer Endpoints shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

## OGC release-candidate qualification

External OGC conformance runs through individual manual dispatchers and, after the
RC image build of a promotable candidate, through `ogc-rc-conformance.yml`. The RC
manifest waits for success; non-candidate rc dispatches skip it because they produce no manifest.
Optional integration flags do not bypass it. See the [workflow inventory](workflow-inventory.md#ogc-cite--conformance)
for the complete dispatchers, source/image binding and separate certification policy.

## Trailing affected CI selection

Trailing affected CI refreshes its completed-ancestor base when the selection job starts,
so queued dispatches do not retain an obsolete range. Dispatch and execution use
the same completed-run selector; execution never regresses behind the dispatched
base. `affected_admission.py` admits only a complete selection whose conservative
job bound fits 40: 34 side jobs (33 before the `Protocol profiles` job, #9371)
plus selected server shards. The side bound counts
all foundation families, MCP transports and Postgres versions even when lane
conditions would skip them. Router fixtures check the bound against the workflow,
including matrix expansion; new dynamic or reusable jobs require an explicit bound.

There is no newest-merge narrowing or arbitrary shard truncation. Missing history,
router full-coverage escalation, API failure or a larger bound
leaves coverage to the unchanged twice-daily full runs. A deferred dispatcher does
not launch CI. A previously queued execution rechecks admission and cancels itself
before publishing change outputs; neither cancellation nor a failed cancellation request can advance the successful CI base.
Admitted affected runs use two concurrent server shards. A selective run whose
router reason is `no_path_match` schedules none of those default smoke shards.
Honua.Core package compatibility, which uses the heavy pool, runs on a selective
dispatch only when a changed file is a Core package input: `src/Honua.Core/`, `src/Honua.Analyzers/`, a file directly under `src/`, a root-level file, `eng/`, `scripts/ci/analyzer-baseline/` or `.github/actions/setup-dotnet-ci/`.
Affected run 45149626646 packed Core for `docker/` and test files alone, and 12
of 13 measured affected runs that packed Core touched no input. Its server build compiles project references that Build & Format Check
already compiles in the same run. Repository metadata (`.github/codeql/`, issue
templates, CODEOWNERS, Dependabot, actionlint, hygiene and PR template files)
starts no managed build or integration lane on any `ci.yml` run: affected run
44254098776 spent a 30-minute producer build plus Format, GDAL and Core package
jobs on a range whose only build input was `.github/codeql/pr-paths.txt`. An
empty path list still runs it. A skipped Core package does not cancel a selected AOT or MCP lane; a failed
Core package or Postgres compatibility job still does. All trusted affected
indices use the configured `honua-heavy` pool, so a run holds at most two heavy runners
for shards; side jobs use `HONUA_CI_HEAVY_RUNNER`, which is unset (hosted) today,
except the Core package job, which follows `HONUA_HEAVY_RUNNER`. Full runs
(the `run-name` rule) put every server-test index on free standard
`ubuntu-latest` runners: run 44271438677 finished all 225 shards in a 78-minute
server phase there, against 193-327 minutes on the six heavy runners. Full server
matrices retain their 16-job limit. A single pool prevents admitted hosted shards
waiting for capacity from exhausting that limit while heavy runners remain idle.
Full run 43709972683 had 13 waiting hosted shards plus three executing shards at
14:17 UTC on 15 Nov, blocking admission of 58 queued heavy shards. The measured
heavy runner listen-to-next-job gaps were 579 s, 673 s and 869 s for runners 6, 5
and 3 respectively. These are before-change observations; live after-change
measurements remain pending. Fork guards and configured/hosted fallback remain.
An active PR Gate does not defer or cancel an affected run. With up to ten open
PRs a PR Gate is almost always active: from 05:52 to 17:02 UTC on 14 Nov 2026 the
former PR-activity rule admitted 0 of 32 dispatches, leaving trunk with only the
twice-daily full runs. Before that rule, completed affected runs
took 4-62 minutes and PR Gate hosted jobs waited a median 4 s (p90 292 s) while
one ran, against 3 s (p90 60 s) with no trunk CI, at the wider pre-cap width.

Before this change, run 43544071705 expanded to roughly 170 jobs, queued for about
120 minutes and occupied seven of eight heavy runners (founder incident report).
The new bound defers that scope instead of queueing its matrix. Live after-change
runner/minute measurements remain pending deployment; fixture counts are not live
performance results. The 64-vCPU host audit found eight heavy 800% quotas and two
fast 600% quotas, totaling 76 quota CPUs. Recommend six heavy plus two fast
runners (60 quota CPUs, four CPUs of headroom). This change does not alter or apply
runner infrastructure configuration. Required PR gates and full coverage remain.

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

[Automatic merge admission](workflow-inventory.md#automatic-merge-admission) uses
the bot App to enable GitHub auto-merge. Repository rulesets and required checks decide when a PR merges.
The workflow does not override protections. Exact-head review admission must be
configured as a required check to be enforced by GitHub auto-merge.

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

## Admin authentication capacity split (12 November 2026)

The former authentication/credentials shard now has four jobs: authentication
and operator bearer; OAuth and OIDC; API keys; client certificates. Each retains
the original routing paths, project and 22/32-minute budgets. Required checks
are unchanged. Children inherit the parent's conservative 21.7-minute dispatch
rank, including existing PR budget deferral, until scheduled trunk CI measures
them. The coverage guard proves exact ownership of the former selection.
See [the capacity evidence and qualification requirement](shard-timeout-budgets.md#admin-authentication-capacity-split-12-november-2026).

### Affected-shard heavy placement

The same shard ran a median 1.83 times longer on `honua-heavy` than on a hosted
runner (27 shards that ran on both pools, 75 green PR Gate runs, 21-22 November
2026). The heavy pool also queued longer at the tail (p90 12.8 min against 5.9
min hosted). The former even-index split put matrix index 0, the longest shard,
on that pool, and a heavy shard was the last job in 26 of 47 runs with shards.
The selector now marks an entry `heavy` only when
`dispatch_rank * pr_heavy_placement.heavy_slowdown` fits within the longest
shard of its producer lane. It takes the shortest shards first and at most half
the lane, rounded up. The longest shard of each lane stays hosted.
`affected-shard` and `affected-shard-held` read `matrix.heavy`, and the existing
pool proof and fork guards are unchanged. Replaying those 47 runs with measured
durations moves the median shard-run wall from 23.7 to 20.8 min, with 47 heavy
jobs instead of 100. Fixtures: `scripts/ci/fixtures/validate-heavy-shard-placement.py`.

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

### Workflow loader validation in the fast stage

PR Gate's architecture matrix member runs pinned, checksum-verified actionlint
before setup/build and before any docs/generated-only skip. All workflow YAML
files receive syntax and expression checks; a job-level `runner.temp` expression
is rejected before landing. The compatibility exception allows only the unsupported
`concurrency.queue` diagnostic. No expression-context diagnostic is ignored.
Regression fixtures prove queue acceptance and failure for invalid contexts,
unknown keys and missing dependencies. Required names and `required.needs` stay
unchanged, and the check adds zero jobs per PR. Timing evidence will come from
the first completed run; no measured saving is claimed.

### OData RBAC denial fixture lifetime

`ODataServiceRbacTests` shares one authorization-denial application for the
lifetime of the sequential test class. Only cases that expect a request to be
rejected before any write use it, and each creates its own HTTP client with its
own role headers. Successful edits and cases with custom policies still build
and dispose their own application within the case.
All cases and assertions remain in the Security and Authorization 5 shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

### GeoServices geocode catalog fixture lifetime

`GeoservicesCatalogGeocodeServerTests` shares one seeded catalog host with
restricted features for the lifetime of the sequential test class, used by the
read-only mount-prefix cases. Public-service collision graphs are built once per
distinct configuration and kept apart from the unmodified catalog. Cases with
other locator or authorization settings still create their own application
within the case.
All cases and assertions remain in the GeoServices Catalog and ImageServer
Support shard on selected PR runs and twice-daily full trunk runs. This changes
no runner count, required check name, required aggregate dependency, workflow,
or shard definition.

### Secure connection default application lifetime

`SecureConnectionEndpointsTests` starts its default application lazily, once per
class, on the first case that uses it, and disposes it once after the class.
Each case creates and disposes its own client. Cases that build a custom host
never start the default application; see the secure connection endpoint test
host reuse section below.
All cases and assertions remain in the Server Features Admin Platform and
Connections shard on selected PR runs and twice-daily full trunk runs. This
changes no runner count, required check name, required aggregate dependency,
workflow, or shard definition.

### PrintingTools endpoint application lifetime

`PrintingToolsEndpointTests` keeps one default application for the class. Each
case creates its own admin client, as the per-case application did. Jobs are
addressed by generated id. The incomplete-job result assertion still accepts
either a completed parameter object or the GeoServices 400 error. No
assertion, test selection, runner count, required check name, required
aggregate dependency, workflow, or shard definition changes. The class stays in Server Features Spec Printing and Static
Maps 3 on selected PR runs and twice-daily full trunk runs.

A paired build-host run passed the same 22 cases with zero skips, one process
each. VSTest case time was 1 minute 41 seconds before and 2 seconds after,
because the one application start is now class-fixture time. Fixture-inclusive
TRX duration fell from 110.869 to 24.444 seconds, saving
86.424 seconds (1.44 test-run runner-minutes). Compilation, admission and
copyback are excluded.

The whole Spec Printing and Static Maps 3 filter ran as a paired build-host run
with one process each. Both passed the same 142 cases with zero skips and an
identical case and outcome list. VSTest reported 4 minutes 29 seconds before and
2 minutes 7 seconds after. TRX wall time fell from 281.6 to 152.9 seconds,
saving 128.7 seconds (2.15 runner-minutes) per shard run.

### Feature stream snapshot and conformance application lifetime

`FeatureStreamSnapshotEndpointsTests` and
`FeatureStreamConformanceEndpointsTests` start their default application lazily:
only cases that call `StartDefaultApplicationAsync()` start it, and only those
cases dispose it. Cases that need a custom configuration build, start and
dispose their own application, and each case keeps its own isolated event store,
sessions and credentials.
All cases and assertions remain in the Server Features Streaming Snapshot and
Conformance shard on selected PR runs and twice-daily full trunk runs. This
changes no runner count, required check name, required aggregate dependency,
workflow, or shard definition.

### WCS endpoint fixture lifetime

`Wcs10EndpointsTests` and `Wcs20EndpointsTests` each share one application with
a substituted raster store for the lifetime of the sequential test class,
through `SubstitutedRasterStoreApplication`. Cases that rebind the metadata
graph or need other services still create their own application, so no rebinding
leaks into the shared host.
All cases and assertions remain in the WFS 3, WFS 8, and WFS 9 shards on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

### Advanced spatial query fixture lifetime

`AdvancedSpatialQueryTests` shares one seeded `WebAppFixture` for the lifetime
of the sequential test class; every case is a read-only spatial query.
All cases and assertions remain in the Core Spatial Query and Streaming shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.

### Input validation fixture lifetime

`InputValidationIntegrationTests` shares one Pro-licensed production-mode
application for the lifetime of the sequential test class. Edit cases add
features to the shared host, but every assertion on edited data filters by a
name unique to that case and no case counts all rows, so results do not depend
on case order. A new case must keep to that rule or use its own application.
All cases and assertions remain in the Security and Authorization 5 shard on
selected PR runs and twice-daily full trunk runs. This changes no runner count,
required check name, required aggregate dependency, workflow, or shard
definition.


### Heavy runner shutdown recovery

`heavy-shutdown-retry.yml` observes completed full trunk `CI` runs. It reads
trusted trunk code and revalidates the source run before requesting one failed-job
rerun. Only attempt 1 qualifies, and every failed non-summary job must have the
`honua-heavy` label and the canonical runner shutdown message in its logs.
Successful jobs remain completed; failed jobs and their dependents run once more.
Full-run server shards run on hosted runners, so in a full run this covers
the heavy Core package and Native AOT jobs.
Manual cancellations, selective runs, mixed product failures, unreadable logs and
second interruptions do not retry. Summary failures qualify only in known dependent-verdict steps; independent foundation/artifact failures remain failures. Required job names and dependency contracts
are unchanged. Manual full trunk runs use the `CI full` title prefix and selective trunk runs retain `CI affected`, so a dispatch nonce cannot change the mode; merge-train
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

Repository hygiene also runs on `labeled` and `unlabeled`. The shared bundle
policy reads the PR's labels from the event payload, so a `bundle-exception`
label added just after `opened` was invisible until the next push and left a
false Bundle policy red (#8481, #8703, #8832, #9326). A label event reruns every
hygiene job; no job is skipped on label events, because a skipped job would
cancel the active run and report the required context without running the
policy. Concurrency cancels the superseded run, so a label change costs one
light-runner hygiene run.

The shared bundle policy (honua-io/.github repo-hygiene `bundle.py`) passes a
human-authored PR only when its body closes at least three distinct issues, or
when it carries the `bundle-exception` label with a
`Bundle-exception: trunk-red|security-tracker|main-named — reason` line. Size is
not an exception, and the label without one of those reasons fails even on a
three-issue PR.
Bot authors and same-repository publisher branches (`automation/`,
`dependabot/`, `renovate/`, `release-please--`) are exempt, so
`publish-generated-files.sh` opens `automation/regenerate-generated-files` PRs
with its user token and no label or exception line (#9466). The lane `gh` and
`pr-slot` gates refuse lane branches with those prefixes.

Numbered Operator Eval Harness children prepare Ryuk and PostGIS 17. Numbered Core and Cloud Contracts children prepare Ryuk, PostGIS 16 and PostGIS 18. The numbered MCP, GPServer/NAServer, Admin Authorization and Migration children request no images through this selector. `test-shard-image-selection.py` executes the workflow selector against every child of every split parent, compares it with the images the unsplit parent selected, and checks unrelated controls. Only Operator Eval Harness 1, which runs `EvalHarnessTests`, uploads `operator-eval-report`; the partition contracts require each report upload to move to exactly one child.

### Compose readiness failure evidence

The optional `Compose Release Lifecycle` job retains all four manual readiness
checks and their 300-second timeout. A timeout names the initial published rc.1,
candidate upgrade, restored rc.1, or candidate return stage and records service
state plus at most eight redacted server error lines before existing cleanup.
Diagnostic commands each have a 15-second limit; command failures omit output,
and an unreadable generated-secret inventory suppresses all diagnostic capture.
Only service/state/health/exit-code fields are retained; configuration, container
environment and command stderr are never printed. Redaction tests run before
image builds when the checked-out PR includes the diagnostic test module. Older
PR heads omit that module and skip only its unit-test step; their lifecycle
journeys and readiness assertions still run. Required checks and dependency
contracts are unchanged.

Run 43577434983 failed initial rc.1 readiness before candidate startup: its two
manual tests reported one pass and one failure in 332 seconds (5.53 minutes).
This change adds no runners or jobs: one lifecycle runner before and after.
Live after-change readiness diagnostics and elapsed time await a new failure;
no lifecycle rerun or runtime savings are claimed from unit validation.

The manual rc.1 upgrade and return each run migrate-only after stopping rc.1
and before candidate startup. The harness explicitly selects Gate, clears prior
approval, and accepts exit 5 only for annotated contracts on its reviewed script
allowlist with matching SHA-256 bytes. Migration 183 removes optional indexes;
future contracts require a separate review. The exact returned nonce authorizes
one retry, which must exit 0 with a verified schema. Unknown contracts, changed
SQL, malformed summaries and failed verification stop the journey. This adds no
jobs or runners (one lifecycle runner before and after). Run 43729796698's manual
journey reported one pass and one failure in 349 seconds before this repair.
Refusals name the script, refusal code and exit code, never raw output, and the
checks raise explicitly so `python -O` cannot strip them.

Both manual rollback and MCP lifecycle startup share the service-state allowlist and credential/error formatting; each caller retains its command context and timeouts.

GitHub-managed Code Quality is switched off for this repository, so there is no
managed analysis to supersede and no helper workflow for it; `codeql.yml` is the
only CodeQL analysis of C# (Roslyn analyzers still run as errors in the build).
Pull requests run that analysis only for the paths in `.github/codeql/pr-paths.txt`
(auth, admin endpoints, connections and secrets, import and object storage, SQL
building, operation approval, the MCP and Studio AI surface, outbound HTTP
proxies, sharing, sessions, exports, and workflows; s-sec-server reviewed it).
Trunk push still scans `src/**`, and the Monday schedule
remains the full instrumented scan. Serving Image Boundary builds the PostGIS
image on a pull request and leaves Lambda native-AOT to nightly and deploy.
Review Gate Attestation stays two jobs: identity resolution has to finish before
the attestation can serialize on the pull request number. Attempt 1 of PR Gate
still attests, because that completion is what review-first uses to observe or
release the admission run. A later PR Gate attempt does not attest again when
the newest Review Gate status for that head is already success. A later review
event still re-attests through the bridge.
CodeQL, Compose Release Lifecycle, Store Image Size and Serving Image Boundary
cancel superseded PR runs through their per-PR concurrency groups.

Normalization consumer admission reads the exact producer attempt before joining
its per-PR cancellation group. Only a successful envelope-packaging job admits
validation; late same-SHA metadata or draft skips cannot cancel useful work.
Generated-only publication stays with the trunk producer. Feature PRs commit
regeneration with their source changes and retain envelope validation and drift
reporting; the consumer stays in observe mode. Trusted admission cancels only consumers of older producer generations
associated with that PR; delayed old completions cannot cancel newer work. Only
admission has Actions write permission for cancellation. Validation retains
read-only Actions, Contents and Pull requests permissions, and its existing
installation-token and compare-and-swap mutation controls.

Image measurement exports stay outside the Docker build context: `.dockerignore` excludes root `artifacts/`, including the OCI archive produced before compiler-evidence export. The three Store Image Size exports therefore share unchanged source inputs instead of recompiling Native AOT when measurement output appears. All size, compiler-input and runtime proofs still run.

## Lightweight workflow routing and compatibility

The lightweight workflow callers (Repository hygiene, Clean code, Runner label
lint, Review Gate Attestation, Review Event Bridge and OpenAPI drift/governance)
use the fork-guarded `HONUA_LIGHT_RUNNER` JSON selector and `ubuntu-latest`
fallback. PR Gate control jobs do not: they stay on `ubuntu-latest` except for
a trunk `qualify_light` dispatch.
Reusable workflow pins must include the shared selector support. Each local
shell job exports `TMPDIR`, `TEMP` and `TMP` from `RUNNER_TEMP` before checkout.
The variable remains an operator availability switch for newly admitted jobs.

OpenAPI governance admits changes to `scripts/ci/lib/python-resolve.sh`, the
shared Python resolver sourced by its contract validator. A helper-only change
therefore runs the consuming PR check under the existing non-draft condition.

Docs Link Gate can run a newer workflow against an older PR head. When that head
predates both the workflow-inventory generator and its template, it validates the
existing documentation with the complete link and concept suite. A partial
inventory generator/template pair or a failing generator remains an error. This
compatibility path adds no jobs and preserves all documentation assertions.
Reachability grades tracked pages that still exist after concept generation,
so retired generated pages are not phantom orphans. Links to deleted pages
continue to fail the link check. This changes no job or runner allocation.

Older PR bases that lack the retired-cache policy load it from fetched trusted
trunk. The explicit base/head diff is still checked, head-supplied policy cannot
replace it, and a missing or incomplete trusted policy fails the required
hygiene job.

### Advisory image and CodeQL runner selection

CodeQL C# analysis, Store Image Size, Serving Image Boundary and the full-CI
producer call the shared `advisory-runner-select.yml` before allocating
compute. The selector job itself runs on `ubuntu-latest`: honua-light hosts
offer only the snap AWS CLI, which snap-confine refuses inside the runner
sandbox, so every selection there fell back as "host unreadable". Same-repository PRs use
trusted base scripts and the existing OIDC role to read the build-host instance
and heartbeat. Scheduled and manually dispatched runs on the repository default
branch use scripts pinned to the triggering commit. Other dispatch refs remain
hosted. A heartbeat no older than 90 seconds must match the current
instance and advertise more than two idle heavy runners: two stay free for
required PR Gate jobs. Otherwise the job uses its existing
hosted label. Missing configuration, credentials, read failures and selector
failure all fall back to hosted compute. This selector never wakes or resizes
the host; AWS retries and step timeouts are bounded. Default-branch selection
also requires the build-host role trust policy to admit that branch subject;
until the companion IaC policy is applied, these callers fall back to hosted
compute. No selector change grants AWS permissions or provisions capacity.

Each consumer also checks the PR's repository before accepting the selected
runner and falls back to hosted compute for forks, independently of the selector.
Forks stay hosted and do not receive the health credential. CodeQL coverage,
build modes, image matrices, evidence uploads and required check names remain
unchanged. A selector failure cannot skip analysis; image impact detection must
still succeed before its selected matrix executes. Hosted disk cleanup runs only
on GitHub-hosted runners.

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

### Server-test shard disk-headroom guard and measured shard re-packing

`scripts/ci/server-test-shard-free-disk.sh` checks available space on `/` (`HONUA_FREE_DISK_PATH`, default `/`) before spawning the background `sudo rm -rf` cleanup of preinstalled Android, Swift, GHCup, and CodeQL toolchains. When available space already meets `HONUA_MIN_FREE_DISK_GB` (default `40` GiB; invalid values fall back to `40`), the helper removes any stale PID file, logs the current `df -h` reading, and returns immediately so `Wait for free disk space` (`--await`) finishes without waiting on toolchain deletion. If `df -Pk` fails, returns non-numeric output, or reports less than the threshold, the helper fails safe and runs the existing background cleanup and `--await` barrier unchanged. Measured baseline on full trunk run `44819335862` (`225` `ubuntu-latest` server-test shards with `145` GiB root volumes): `Wait for free disk space` consumed `142.43` runner-minutes total (`36.0` s median, `72.0` s p90, `171.0` s max). Covered by `scripts/ci/fixtures/test-server-test-shard-free-disk.py`.

Eleven over-split full-run capacity parents whose combined `Run server test shard` step durations on full trunk run `44921455496` totaled `1.20`–`3.30` minutes (`33` child shards, 26-run median rank sum `103.6` runner-minutes) are re-packed back into their original single-job definitions (`225` → `203` server shards, saving `22` runner setups; remaining `28` full-run capacity contracts intact; `192` shards keep measured ranks and the `11` restored parents use conservative `2.5 + sum(run_m)` estimates of `3.7`–`5.8` minutes, bringing the scheduling rank sum from `877.5` to `827.1` runner-minutes). Every filter, path list, timeout, and report flag is conserved and checked by `check-server-test-shard-coverage.py` and `test-full-run-capacity-partitions.py`.

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

`AdminAuthEndpointsTests` starts its per-case default application on the first
request for its default client. The 32 discovered methods (49 cases) that create separately
configured OIDC or client-certificate applications avoid an unused default
application. All 47 discovered test methods (64 cases) retain per-case fixture isolation and
their existing assertions.
The Server Features Admin Authentication and Credentials shard still runs these
cases on selected PR runs and twice-daily full trunk runs. No required check,
aggregate dependency, runner count, workflow or shard definition changes.

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

## Review-first report collection retired

The review-first experiment was not promoted. The final successful daily audit,
[run 43752404798](https://github.com/honua-io/honua-server/actions/runs/43752404798),
reported 5 of 20 required current-policy heads, 151 non-current-policy heads,
and 1,724 successful Review Gate runs without receipts. It recommended
`observe-more`. Its single audit job ran for 156 seconds (2.60 runner-minutes).
Removing the daily collector saves one scheduled job per day; producer artifact
overhead was not isolated and is not included in that saving. PR workflow and
required-check counts are unchanged. Exact-head review, thread validation,
trusted policy provenance, and joint final revalidation remain required.

## Native-image observer retirement (#7880)

The disabled Native Image Impact Observation workflow and its report-only policy
were retired after the measured graph-derived selector showed no routing savings.
The workflow had been disabled since 9 November, so retirement saves 0 current
runner-minutes.
Image-input selection, policy/trigger drift checks, and v3 content digests remain
in `scripts/ci/native-image-impact.py` for authoritative serving-image verification.
No required check or nightly/release/deploy verification moved. Content-addressed
skip promotion still requires an audit of authoritative serving-image run evidence;
the retired observer cannot supply it.

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

## Generated projections alongside source changes

The required generated-file ownership check admits protected outputs alongside
source inputs that force the architecture gate. It compares the candidate's
validator, policy, PR workflow, classifiers, fast-test selector, regeneration
wrapper and architecture action with trusted trunk blobs. Older branches must
adopt the repair before mixing source and
output; changing or removing verification remains blocked. Generated-only PRs
still require the canonical producer's exact commit/tree artifact proof.

Before strict regeneration, the existing architecture job removes submitted
protected outputs so only freshly produced bytes and modes can pass. Unproduced
additions cannot survive the checkout, and symlink parents fail closed.
After regeneration, the job checks every protected
path submitted by the PR against its committed bytes and executable mode. Added
and deleted outputs are included, and symlinks fail. The MCP roster retains its
existing drift contract: its valid source SHA and UTC generation timestamp can
differ between local and CI generation; every other byte and the file mode must
match freshly generated output. No other projection receives that normalization.
It requires the PR merge
checkout and uses its target parent; unreadable Git state fails closed. Output
that the PR did not change retains advisory drift reporting. No job, required
context or aggregate dependency is added or removed.

The repair itself lands without protected generated output because the old
trusted checker cannot admit its own replacement. Its authored workflow inventory
template changes here; the existing trunk generator publishes that projection
afterward. Subsequent source PRs can carry their generated inventory normally.

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

## Generated proposal reconciliation

The existing nightly/on-demand generated-files publisher refreshes the fixed
`automation/regenerate-generated-files` branch and reuses its open PR when
regeneration changes protected outputs. When validated current trunk has no
generated diff, it closes an obsolete proposal after verifying the publication's
source ancestry and rechecking the PR head, automation head and trunk. Changed
heads or moved trunk preserve the proposal. The workflow concurrency group
serializes publishers; the retained branch supplies diagnostic evidence.
Required checks, aggregate dependencies and PR-time work are unchanged: before
and after this cleanup there are zero additional workflows, jobs or runners.

## Review Gate attestation after merge

Live trunk branch protection on 17 November lists four required contexts:
`PR Gate`, `PR Gate / Affected shards`, `clean-code / Clean code` and
`repo-hygiene / Repository hygiene`; `Review Gate` is not among them. The attestation already sheds pull requests
that are closed before it starts. A pull request that merges or closes while the
attestation runs used to receive a red status anyway, from the initial snapshot
(`pull request is not open`) or the final double snapshot (state changed between
reads). Six of 40 server PRs updated between 16 Nov 17:00Z and 17 Nov 17:10Z
carried that red status, each posted 1 to 8 seconds after the merge or close.
The attestation now re-reads the pull request before any red status and sheds
it when it is no longer open. Open pull requests keep every blocking reason, and
the success and dispatch paths are unchanged. A failed re-read keeps the red status. No trigger, job, runner, required
check or aggregate dependency changes.

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

### Shared default host survives between serial cases

`WebAppFixtureSharedBootstrapMixin` used to dispose the shared default host and
its Postgres fixture as soon as the last `WebAppFixture` released it. A serial
collection whose cases each own a default fixture holds no other reference, so
every case booted a new host: 132 such classes on trunk, 1357 cases and about
2555 s of case time on a full run. The last release now schedules disposal after
a 30-second idle grace and the next acquire cancels it, so consecutive cases and
classes reuse one host. Cases stay isolated by the schema each fixture creates
on initialization and drops on disposal, which is how parallel classes already
share the host. Process-wide singleton state now also carries from one serial
case to the next, as it already did whenever a parallel class held the host; a
reused host clears any graph swap a case armed for its next read. The
process-exit handler disposes a host still in its grace on a best-effort basis
(10-second limit; Testcontainers' reaper removes a container left behind).

`SharedHostCleanupTests` release an idle host explicitly before their cold-start
and reference-count assertions, assert that a caller inside the grace reuses the
same host, and assert that the grace on its own disposes the host. Measured on
the build host for the same 80 cases, `AttachmentEndpointTests` and
`OgcStylesEndpointTests` went from 170.0 s to 16.8 s of case time.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Raster store substitutes borrow a switched host

`ImageServerEndpointsTests`, `GeoservicesCatalogEndpointTests` and
`ImageServerWmtsMatrixSetTests` booted a new host in 60 cases whose only difference
from a host the class already had was the `IRasterStore` substitute. TestKit's
`SwitchedServiceHost<T>` registers a `ServiceSwitch<T>` (a forwarding proxy) in
place of the service, and such a case now borrows the class host with its own
substitute. Borrowing and returning restore the seeded catalog under a new
revision, so revision-keyed output and tile caches never serve one case's
response to another; the WMTS matrix-set class keeps one host per configuration
and deletes its stored tiles through the storage service on every borrow. Cases
that register anything besides the store keep their own hosts, and every
assertion is unchanged.

Measured on the build host (Release, the same 182 cases): case time went from
1078.0 s to 198.5 s (`ImageServerEndpointsTests` 692.9 to 103.7 s,
`GeoservicesCatalogEndpointTests` 313.8 to 88.2 s, `ImageServerWmtsMatrixSetTests`
71.3 to 6.6 s) and wall time from 810 s to 375 s. On hosted full run 44271438677
the three classes took 461.2 s of case time.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Per-case hosts in secure-connection, persisted-export and OIDC admission tests

`SecureConnectionEndpointsTests` built a new host for each of the 25 rows of its
port-contract theory, although every row uses the same allowlist, secret resolver
and capturing driver and creates its connection under a unique name. The rows now
borrow one lazily started class host, and the capturing driver is reset on every
borrow so a row sees only its own probe. `PersistedExportProgressTests` created a
schema harness for every case and a host for six of them; a class fixture now owns
the database adapter, one harness and one lazily started host, and every case
still seeds and asserts on its own record ids. `PostgresOidcProviderAdmissionTests`
seeds its unreadable provider once and starts one host per edition (two instead of
four); every case is refused before anything is written. Every assertion is
unchanged.

Measured on the build host (Release, the same 68 cases): `SecureConnectionEndpointsTests`
216.9 to 67.9 s and `PostgresOidcProviderAdmissionTests` 29.8 to 17.1 s of case
time. `PersistedExportProgressTests` stayed at 12.7 s there because a host boots in
about 1.5 s on that machine; on hosted full run 44271438677 its host cases took
11.5 to 18.8 s each (92.8 s for the class), which is the time the shared host
removes. On that run the three classes took 354.5 s of case time.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Shared job hosts in GP ownership and vector parity tests, feature-lock manifest shapes

`GPServerDurableRuntimeTests` started an identical Postgres job host for each of the
five operations of its SOAP ownership theory. The theory now lives in the nested
`OwnershipTests` class, in the same job-substrate collection, with one class host.
It has to be a separate class: starting a durable host truncates the shared job
tables and every durable host runs job workers, so a shared host must never be
alive while another case runs its own. `VectorProcessParityIntegrationTests` built
the same production-substrate host for eleven process cases; they now borrow one
lazily started host that clears the job tables when it starts and stops, and the
one case that needs a different host releases it first. In
`FeatureLockCapabilityManifestTests`, the twelve cases whose host has the shared
lock store read the manifest from one host per shape (composition path and
authorizer, four hosts); rows with the in-memory store keep their own hosts.
Every case still submits or reads only its own job or manifest, and every
assertion is unchanged. A job that never finishes now stays on the shared host,
so later cases in that class can time out behind it; that adds failures, never a
false pass.

Measured on the build host (Release, the same 52 cases, all passing): the
ownership theory went from 79.3 to 7.1 s, `FeatureLockCapabilityManifestTests`
from 195.3 to 49.4 s and `VectorProcessParityIntegrationTests` from 87.4 to 35.2 s
of case time (362.0 to 91.7 s). On hosted full run 44271438677 the three took
38, 105.6 and 87.9 s.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Shared hosts in MCP bearer, MCP scope and Studio AI proxy tests

`McpBearerAuthenticationTests`, `McpScopeAuthorizationTests` and
`StudioAiProxyEndpointsTests` started a new application for every case. The 34 MCP
bearer cases, including the 8 that then start a host of their own configuration,
now share one class host for the default configuration, and the 8 scope cases share
one. Each case mints its own tokens and opens its own sessions, well under the
32-session per-principal cap; the in-process client has no remote address, so the
failed-attempt limiter never counts it; the API-key case issues and revokes a key of
its own. The Studio AI proxy tests keep one class host per end-user authorization
setting (two instead of 31). Borrowing a host empties the captured audit log and
clears any admission denial or financial setup, and admission send counters are
read relative to the start of the case. Cases with another configuration still own
their hosts, and every assertion is unchanged.

Measured on the build host (Release, the same 68 cases, 68/68 passing before and
after): the test run went from 235 s to 51 s for the two MCP classes and from 148 s
to 4 s for the Studio AI proxy class. Case time went from 96.8 to 46.7 s
(`McpBearerAuthenticationTests`), 17.8 to 2.6 s (`McpScopeAuthorizationTests`) and
68.4 to 4.6 s (`StudioAiProxyEndpointsTests`); host start-up in a class fixture is
not counted as case time. On hosted full run 44271438677 the three classes took
168.0 s of case time.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Pooled control-plane schemas

`ControlPlaneStateHarness` serves 38 store test classes (212 cases on hosted full
run 44271438677, 1934.7 s of case time, almost all in
`caching-file-storage-styling-infrastructure`). Every case created a schema, ran the
job substrate migration and 13 control-plane migrations and dropped the schema
again, and each of those steps takes the fixture's global schema-mutation lock, so
cases from every class queued behind each other. `ControlPlaneSchemaPool` now keeps
a finished case's schema for the next case when its catalog still matches a freshly
migrated one (relations, columns in order, defaults, constraints, indexes, triggers
and their enabled state, rules, routines, views, policies, grants, sequence
settings and types, with the schema name normalised). Before reuse, one transaction
truncates every table, inserts the rows the migrations insert again (the
configuration generation row) and puts every sequence back where a fresh schema has
it. A schema whose catalog changed, or that cannot be reset, is dropped as before.
`ControlPlaneSchemaPoolTests` checks that a reused schema holds exactly the rows and
sequence positions of a fresh one, that a changed schema is dropped and that a
harness disposed twice is handed out once. Every store assertion is unchanged.

Measured on the build host (Release, the same 240 cases, all passing before and
after, plus the 3 new cases): case time went from 1619.6 s to 837.8 s and the test
run from 1 m 40 s to 1 m 9 s.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Shared hosts in gRPC spec, GP alias and attachment authoring tests

`GrpcSpecServiceIntegrationTests` built a Pro-licensed server in its constructor, so
each of its 17 Pro cases started one. They now share one class host. The spec
artifact cache is in memory and lives as long as the host, and a cache hit reports
the node as `Cached`, not `Succeeded`. So the two cases that apply with
`ReadWrite` each take a node id of their own (the id is part of the content hash)
and still miss the cache. The Community-licence case keeps its own host.

`GPServerEsriTaskAliasEndpointTests` started a server for each replaced-service
case: 7 with a recording job service, 3 with a synchronous execute service and 3
with the alias-colliding catalog. A class fixture now starts one host per
configuration on first use, and clears the recording service before each case
takes it. The case that needs the colliding catalog and the recording service
together keeps its own host.

`ServiceSettingsAttachmentAuthoringTests` started a server for every case. Its four
editing-graph cases with the default output cache now share one
`SeededMetadataHost`. Before each case it drops any queued graph swap, restores the
seeded graph under a new revision and evicts cached service metadata. The
output-cache case and the non-editing case keep their own hosts. Every assertion
is unchanged.

Measured on the build host (Release, the same 49 cases, 49/49 passing before and
after): case time went from 118.0 to 9.3 s (`GrpcSpecServiceIntegrationTests`),
108.0 to 18.8 s (`ServiceSettingsAttachmentAuthoringTests`) and 82.2 to 28.8 s
(`GPServerEsriTaskAliasEndpointTests`), 308.2 to 56.9 s in all. The test run went
from 117 s to 28 s for the two server classes and from 82 s to 28 s for the
GeoServices class. On hosted full run 44271438677 the three took 272.7 s of case
time.

Shard filters are unchanged, so every case runs in the same shards on selected PR
runs and twice-daily full trunk runs. No workflow, shard definition, required
check name or aggregate `needs` changed.

### Shared hosts in sharing OAuth2, introspection and portal discovery tests

`SharingOAuth2Tests` and `PortalFacadeDiscoveryContractTests` built their server
through the class's own `IAsyncLifetime`, so each of their 27 and 23 cases started
one. Each class now shares one class-fixture host. Every case seeds its own
authorization code or issues its own token, `PortalOAuthStore` and the token issuer
cap nothing per principal, and neither the OAuth2 token endpoint nor
`generateToken` is rate-limited, so no case sees another's codes or tokens.
`SharingOAuth2IntrospectionTests` started a server for each of its 6 cases. It now
starts one host per token format (opaque and JWT) on first use; the case that
checks disabled introspection keeps its own host. Cases with another configuration
still build their own hosts, and every assertion is unchanged.

Measured on the build host (Release, the same 56 cases, 56/56 passing before and
after): case time went from 128.8 to 38.3 s (`SharingOAuth2Tests`), 77.4 to 3.0 s
(`PortalFacadeDiscoveryContractTests`) and 71.5 to 34.0 s
(`SharingOAuth2IntrospectionTests`), 277.7 to 75.3 s in all; the test run went from
6 m 16 s to 51 s. On hosted full run 44401872530 the three took 237.0 s of case time.

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
