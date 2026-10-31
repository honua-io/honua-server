# CI Gate Model

> Defines the five-tier quality gate model governing all CI workflows across the Honua project.
> Last updated: 2026-09-11

## Tier Definitions

| Tier | Purpose | Characteristics | Merge-blocking |
|---|---|---|---|
| **PR** | Deterministic pre-merge confidence | Fast, path-filtered when possible, no flaky external dependencies | Yes |
| **nightly** | Broad regression and compatibility coverage | Expensive, long-running, external-system-heavy, certification-style | No |
| **release** | Packaging and release certification | Publish/package/sign/smoke-test tied to tags or release branches | No (for routine PRs) |
| **deploy** | Environment promotion and post-apply validation | Manual or protected-branch workflows tied to environments | No (for routine PRs) |
| **maintenance** | Repo automation and housekeeping | Version automation, metadata updates, scheduled hygiene | No |

## Governing Rules

1. **PR gates must be deterministic and directly actionable by the author.** If a check fails, the author must be able to reproduce and fix it locally without external system access.

2. **External conformance suites, soak tests, and long security scans do not belong in the PR lane.** These run on schedule or manual dispatch only.

3. **Release and deploy workflows remain strict** but do not burden everyday feature merges.

4. **New checks default to nightly** unless explicitly justified as PR-blocking. Justification requires: deterministic behavior, sub-5-minute runtime, and author-actionable failure messages.

   **Named exceptions — `PR Gate` and `Review Gate`.** Together, `PR Gate` and `Review Gate` remain the required PR lane on `trunk`: unprivileged verification and trusted exact-head admission. Verification uses a managed build producer, a downstream test/runtime consumer, and the existing independent format and CI-router jobs. Selected shard consumers reuse the same verified outputs. The test consumer retains real PostGIS-backed governance and execution proofs. Full collector compilation and runtime proofs remain the deliberate exception to the sub-five-minute rule. `Review Gate` adds no build runner. Any further PR-blocking check still owes the sub-5-minute justification above and should first be considered inside an existing required workflow.

5. **AOT verification is full-CI only until trim debt is retired.** The `AOT Build Verification` job in `ci.yml` runs in the full scheduled/manual integration lane or on PRs explicitly labeled `ci/full`, not on routine PRs or merge-to-trunk pushes, because its current failures are not consistently fast or author-actionable within the default PR lane.

6. **The `Tier=Fast|Integration|Slow` test trait is a sub-tier inside this gate model, not a replacement for it.** ADR-0037 splits the .NET test suite by execution cost so that PRs run the `Tier=Fast` foundation tests plus a targeted subset of `server-tests` shards selected by `scripts/ci/honua-server-targeted-tests.sh`. The `targeted-shards` job emits a JSON `matrix_include` drawn from `.github/ci-shards.json` and `server-tests` consumes it via `strategy.matrix.include: fromJson(...)`, so unselected shards never instantiate a runner. The shared shard runner composes `&Tier!=Slow&Tier!=Fast` onto the matrix filter so Slow-tagged tests inside a shard's namespace are skipped and Fast tests run only once in the foundation lane; it also emits heartbeat/tail diagnostics over normal-verbosity test logs, writes timing artifacts, and enforces the inner `test_timeout_minutes` cap before the job-level timeout cancels the runner. `Tier=Slow&Category=Emulator` runs nightly via `nightly-slow-tier.yml`, and `flaky-detection.yml` re-runs a bounded rotating window of the same `.github/ci-shards.json` shards several times a night for flake reporting (the whole shard set is covered every ceil(shards / shard_count) days). The Scale/Cloud/External slow subfamilies need dedicated workflows once their fixtures are wired up. The five-tier PR/nightly/release/deploy/maintenance gate model above still defines *where* a workflow lives; the trait defines *which subset of tests* runs inside the workflow.

## PR Lane (Required Checks)

These workflows are merge-blocking for all PRs to trunk:

| Workflow | What it validates | Path filter |
|---|---|---|
| `pr-gate.yml` | **Required verification context `PR Gate`.** Admission includes tracked-text UTF-8 validation. The affected-scope warnings-as-errors producer publishes pristine, verified managed test outputs and runs the required boot/migration smoke against its canonical host output; a downstream consumer runs Fast units, source architecture and full collector architecture, MCP/governance checks, and the existing HTTP/PostGIS, GDAL/PDAL proofs. Independent format and CI-router jobs remain required. `required.needs` includes `pr-gate`, `pr-gate-tests`, `format`, and `ci-router-validation`. See the [artifact contract](server-test-binary-artifacts.md) for same-run identity, bounds, rollback and scoped fallback. | None — a required context must never be path-filtered |
| `review-gate.yml` | **Required admission context `Review Gate`.** Immutable default-branch workflow policy publishes exact-head review evidence from an attesting reviewer (Codex or Claude — see [Attesting reviewers](#attesting-reviewers)) and, in enforce mode, releases one admitted PR Gate rerun. In observe mode it retains a SHA-bound decision receipt for the read-only `review-first-evidence-ledger.yml`; the ledger reports but cannot promote. Review events arrive through the read-only `review-event-bridge.yml`; no PR-authored workflow has status or Actions write authority. | None — it must publish on every PR head |
| `ci.yml` | **No `pull_request` trigger** (deliberate — see the header comment). PR template compliance, CI router validation, build, .NET foundation tests, targeted server-test shards, architecture gate, JavaScript typecheck, baseline Postgres compatibility all run on the train's `train/batch/*` `workflow_dispatch` and the nightly schedule. Its aggregator context `CI Gate` is produced by the batch CI only and never appears on a PR. | n/a — not PR-triggered |
| `openapi-contract-governance.yml` | OpenAPI spec stability | `src/**/api-specs/**`, `*.openapi.*` |
| `control-plane-sdk-governance.yml` | Control plane SDK governance | SDK/control-plane paths |
| `import-fidelity-scorecard-governance.yml` | Import-fidelity baseline stability + perf-parity gate smoke test (pass/fail fixtures) | Parity/baseline/perf-budget asset paths |
| `docs-link-gate.yml` | Relative links **and** `#fragment` anchors across `docs/**/*.md` (GitHub slug algorithm, ported from `geospatial-mcp`'s `tools/check_links.py`), plus `scripts/ci/code-referenced-anchors.v1.json` — the absolute phase-A `https://honua.io/docs/<slug>/` URLs product code and shipped config hand to an operator or an agent at runtime (`remediationRef`, SCIM `documentationUri`, Prometheus `runbook_url`). A URL served only through a `.gitbook.yaml` redirect warns; a `docs.honua.io` URL is an error (that host is dark until 2026.2), and a phase-A URL that lacks the trailing slash or is not listed in the manifest is an error; a `pendingPr` entry is warn-until-present so an anchor introduced by an open PR is enforced only once it lands. Pre-existing rot lives in `scripts/ci/doc-link-rot-allowlist.v1.json` with a stale-entry ratchet. Stdlib Python, ~15s, no external dependencies. | `docs/**`, `scripts/ci/check-doc-links.py`, `scripts/ci/check-doc-links.test.py`, the workflow itself |

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
same-run source, SDK, platform and build-property identity, then compile only
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
full cross-project parallelism.

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
or full document selection and complete formatter restore context. Full CI runs
an independent full-solution format job; `CI Gate` requires it when build inputs
are selected, without making test consumers wait for formatting.

**Governance and runtime proofs.** The PR test consumer retains the complete
`Category=Architecture` governance set in `Honua.Server.Tests`, the real
store-gated host and Testcontainers PostgreSQL, and the existing remote-source,
raster, layer, geometry/calculate-field, imagery and PDAL proofs. The required
boot/migration smoke runs on the producer after pristine artifact publication,
using the canonical host output without duplicating it in test artifacts.
Moving compilation to the producer does not remove these checks or move them to
an advisory lane. Source architecture and full collector architecture remain
separate required executions after fresh projections.

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

## Nightly Lane

These workflows run on schedule and can be dispatched manually:

| Workflow | Schedule | What it validates |
|---|---|---|
| `cite-conformance.yml` | Mon 6am UTC | OGC CITE Features conformance |
| `cite-tiles-conformance.yml` | Tue 6am UTC | OGC API Tiles CITE conformance |
| `cite-wfs20-conformance.yml` | Mon 3am UTC | WFS 2.0 CITE conformance |
| `cite-wms-conformance.yml` | Wed 6am UTC | OGC WMS CITE conformance |
| `cite-wmts-conformance.yml` | Thu 6am UTC | OGC WMTS CITE conformance |
| `ogc-maps-conformance.yml` | Fri 6am UTC | OGC API Maps conformance |
| `cite-kml22-conformance.yml` | Fri 3am UTC | OGC KML 2.2 CITE conformance |
| `cite-gml32-conformance.yml` | Sat 6am UTC | OGC GML 3.2 CITE conformance |
| `cite-gpkg12-conformance.yml` | Sat 3am UTC | OGC GeoPackage 1.2 CITE conformance |
| `geoservices-import-fidelity-external.yml` | On-demand (`workflow_dispatch`) | External GeoServices parity + Geoportal import vs live Esri services: runs the correctness regression gate **and** the perf-parity latency gate (issue #1249) over the freshly measured scorecard (deterministic parity stays in Import Fidelity Scorecard Governance); also runs `GeoservicesGeoportalImportIntegrationTests` |
| `routing-nightly.yml` | Weekly Sun 5:00am UTC | pgRouting provider + NAServer routing integration (`Category=Routing`); `PgRoutingFixture` manages its own Testcontainers `pgrouting/pgrouting` image |
| `warehouse-nightly.yml` | Weekly Sun 6:00am UTC | Snowflake/Redshift/Databricks/SqlServer creds-gated provider tests; self-skips cleanly without configured secrets, surfacing pass/fail/skip counts in the run summary |
| `cross-server-consume-nightly.yml` | Daily 7:00am UTC | Honua-as-client WMS/WFS/WMTS reads against GeoServer and MapServer reference containers |
| `client-compat-smoke-nightly.yml` | Daily 7:15am UTC | Full CERT-\* matrix certification (18 test cases × 4 protocol lanes) with `.cert.json` envelopes + reusable evidence pack |
| `pyqgis-client-compat-nightly.yml` | Daily 7:30am UTC | PyQGIS desktop client compatibility (OGC Features + WFS) with per-protocol `.cert.json` envelopes |
| `sdk-server-compatibility.yml` | Monday 8:35am UTC | Manifest-driven last-3 server refs x last-3 SDK sets compatibility matrix through `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`; manual dispatch can pin `server_current_ref` for release-candidate evidence; copies checked-out SDK refs to `$RUNNER_TEMP/sdk-compat` before smoke execution so server repo build policy does not affect SDK source builds; records SDK versions, server commit/image field, seed profile, exercised surfaces, migration automation surface status, and diagnostics; publishes the `sdk-compatibility-matrix-<run-id>` table artifact and fails on supported-cell regressions |
| `client-interop-nightly.yml` | Daily 7:00am UTC | Real-client interop matrix via Docker harnesses (`gdal`, `pyqgis`, `openlayers`, `cesium`, `geoservices-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` plus `expected-pairs.json`, refreshes `docs/gis/gap-report.md`, and fails strict mode on baseline pass regressions, missing current envelopes, missing committed baselines, or new unbaselined failures. Non-blocking until 30 consecutive nightly passes (#806) |
| `gdal-driver-e2e.yml` | Daily 7:45am UTC | GDAL `ogrinfo` + `ogr2ogr` round-trip against honua-server (ADR-0034 stand-in until `honua-gdal` plugin ships) |
| `load-soak-nightly.yml` | Scheduled | Load and soak testing |
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

## Attesting reviewers

`Review Gate` goes green only on exact-head review evidence from a bot identity
distinct from the PR author. `scripts/ci/review-gate-evidence.js` owns that
identity set and is the single source of truth: it exports `ATTESTING_LOGINS`,
`scripts/ci/merge-train/select.sh` reads it via `--print-logins`, and the
`.github/workflows/claude-review.yml` lane reads its body template through
`scripts/ci/claude-review-body.js`. Nothing else may restate a login or a
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

`claude` is also a real GitHub User, and a User can hold a PAT, so it is accepted
only when GitHub additionally types the author as a `Bot`. That is why every
`author` selection in the snapshot query carries `__typename`, and why
`reviewerFor(login, typename)` takes two arguments. Dropping `__typename` from
the query silently disables the Claude identity.

### The Claude lane

`.github/workflows/claude-review.yml` produces the `claude` half.

**It never runs from the pull request it reviews.** It triggers on `workflow_run`
(completed `PR Gate`) and on `issue_comment`, both of which execute the
default-branch copy, so the workflow, the prompt and the `AGENTS.md` the reviewer
follows all come from trunk. A `pull_request` trigger would hand a candidate its
own reviewer definition plus the secrets and `id-token: write` — enough to attest
to itself and to exfiltrate the credentials. `review-gate.yml` uses
`pull_request_target` + `github.workflow_sha` for the same reason. The PR is
resolved through `scripts/ci/trusted-pr-workflow-run.js`, which binds the PR and
its exact head to the immutable GitHub-managed check association and fails closed
once the PR has moved. The PR tree is checked out into `pr/` as data, with its
`CLAUDE.md` / `AGENTS.md` / `.claude` / `.github` deleted first so a candidate
cannot smuggle reviewer instructions through a nested memory file. The diff is
still untrusted text; the tool allowlist is the control that contains it.

**It cannot merge, push, or edit a workflow.** The action authenticates as the
Claude GitHub App, whose org installation carries write scopes, so the reviewer
gets read-only analysis tools plus exactly two append-only publishing tools
(`gh pr comment` and inline review comments) — no `gh api`, no general Bash, no
git. `validate-single-merge-authority.sh` cannot see runtime API calls, so this
allowlist is the control, and `scripts/ci/claude-review-lane.test.js` asserts it.
The workflow's own `GITHUB_TOKEN` stays read-only.

**It only ever posts comments — never a review verdict.** A
`CHANGES_REQUESTED` is identity-scoped and is cleared only by a *newer positive
from the same identity*; an optional reviewer that later stops running (secret
revoked, App uninstalled, API outage) would leave such a PR blocked permanently,
with no operator override short of editing the evaluator on trunk. So:

- no blocking findings → one comment carrying the generated clean body, which
  attests through the clean-comment path;
- blocking findings → inline review comments only. Those become review threads
  and hold the gate red through `unresolvedCount` until a human resolves them.

Note the honest limitation, which the lane shares with Codex: `unresolvedCount`
is **head-scoped** — a thread is counted only while its comment sits on the
current head, so findings threads drop out of the gate's view on the next push
and the new head is judged on its own evidence.

A `@claude review` comment re-runs the lane; it is gated on a human commenter
with `OWNER`/`MEMBER`/`COLLABORATOR` association, because the action hard-fails
its own permission check otherwise and would turn a courtesy trigger into a red
check. Re-review is suppressed only by an existing clean attestation *for the
same head*, so a prior findings run never blocks a re-request.

**Enabling the lane (repository owner only).** Add **one** repository secret:

- `CLAUDE_CODE_OAUTH_TOKEN` — run `claude setup-token` locally and paste the
  token (subscription billing); or
- `ANTHROPIC_API_KEY` — an API key from console.anthropic.com (usage billing).

Until one exists the job exits 0 with a `::notice::` naming the secrets; it never
fails a PR. The `claude` GitHub App must stay installed on the org with
`pull_requests: write`. The lane is **not** merge-blocking itself and publishes
no check context — it only feeds evidence to `Review Gate` — but it does
consume one runner per PR-Gate completion, which is why it is documented here
rather than treated as a silent addition (rule 4).

## Adding New Checks

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

The retained `*.timing.json` includes `execution` with actual result counts,
TRX SHA-256, source SHA/dirty flag, project, and runner/verifier hashes. The timing
record retains the effective filter and original shard status. Its count status
can be positive while the overall shard fails (for example a killed child after
partial passing results); consumers must require the overall successful outcome
as well. Missing counts stay missing. Synthetic supervisor fixtures are labelled
as such and do not qualify a candidate. These files are diagnostic provenance;
local files alone do not constitute a trusted release attestation.
