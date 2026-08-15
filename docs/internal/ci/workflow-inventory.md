# CI Workflow Inventory

> Canonical inventory of all GitHub Actions workflows across the Honua project.
> Last updated: 2026-08-15

## honua-server

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `pr-gate.yml` | PR Gate | PR | `pull_request` (base `trunk`), `workflow_dispatch` | Yes — required verification context | The cheap per-PR gate added by #2865, publishing `PR Gate` (never `CI Gate`). A single service-free ubuntu-latest runner performs the whole-solution build, format, Fast smoke, and architecture enforcement. In review-first enforce mode, attempt 1 stops before these expensive steps and the trusted reviewer releases attempt 2 exactly once. It is deliberately un-path-filtered. When `HONUA_PR_GATE_BUILD_REUSE_SHADOW=true`, a best-effort post-gate step packages at most two repeated registered test projects from that already-paid Release build; packaging/upload remains non-authoritative and any failure is a cache miss. |
| `review-event-bridge.yml` | Review Event Bridge | PR | `pull_request_review`, `pull_request_review_comment` | No | Best-effort latency hint only. GitHub runs these event workflows from the PR merge branch, so the bridge is credential-free/no-checkout and is never trusted for invalidation or landing. |
| `review-gate.yml` | Review Gate Attestation | PR | `pull_request_target`, `issue_comment`, trusted `repository_dispatch`, PR Gate/bridge `workflow_run` | Yes — required admission context | Publishes `Review Gate` on the exact current head only when Codex has exact-head evidence and no unresolved Codex threads. It serializes every event by resolved PR number, pins the exact trusted workflow-policy SHA, and is the only authority allowed to release expensive verification. In observe mode it retains an immutable decision receipt; merge-train selection and pre-land independently re-attest source evidence. |
| `review-first-evidence-ledger.yml` | Review-first Evidence Ledger | Maintenance | daily schedule, `workflow_dispatch` | No | Read-only default-branch audit of retained Review Gate observation receipts. Replays the production dispatch helper, deduplicates exact heads, separates policy cohorts, and reports promotion readiness; it cannot change mode, status, labels, workflow runs, train state, or merge state. |
| `pr-gate-impact-observe.yml` | PR Gate Impact Observation | PR evidence | completed `PR Gate` `workflow_run`, `workflow_dispatch` | No | Trusted default-branch, read-only classification of the exact gate-time diff. It retains bounded docs-only/full receipts and, when present, validates PR Gate build metadata plus exact payload artifact identity before emitting a small tree/policy-bound build receipt. It never downloads or executes the large payload; the full required PR Gate remains authoritative. |
| `native-image-impact-observe.yml` | Native Image Impact Observation | PR evidence | completed `PR Gate` `workflow_run`, `workflow_dispatch` | No | Trusted default-branch, read-only comparison of graph-derived image inputs with legacy path triggers. Existing Serving/GDAL image workflows remain authoritative in observe mode. |
| `impact-routing-evidence-ledger.yml` | Impact Routing Evidence Ledger | Maintenance | daily schedule, `workflow_dispatch` | No | Read-only default-branch audit of attempt-bound PR Gate and native-image impact receipts. Selects only each producer run's current attempt, counts only current trusted policy and distinct candidate heads, reconciles native decisions with successful exact-head Serving/GDAL image outcomes, and reports cohort readiness without changing routing, statuses, workflows, or merge state. |
| `merge-train.yml` | Merge Train | Batch | schedule, workflow dispatch | Sole merge authority | Requires exact-head `PR Gate` + `Review Gate` at selection and immediately before compare-and-swap landing. With the build-reuse shadow enabled, only an exact one-member batch may carry the canonical successful PR Gate run/attempt/PR/head identity into Smart CI; multi-member or incomplete state omits it and follows the existing path. |
| `ci.yml` | CI | nightly + train batch CI (no `pull_request` trigger) | `schedule`, `merge_group`, `workflow_dispatch` | Its `CI Gate` context is produced only by the train's `train/batch/*` dispatch — it never appears on a PR head SHA (#2865) | Core build, test, architecture gate, CI router validation, JavaScript typecheck, and baseline Postgres compatibility; the `pr-template-check` job runs first and `pr-readiness` (and therefore every downstream test job) depends on it, so a malformed PR body short-circuits the pipeline before heavy runners are provisioned; per ADR-0037 the `targeted-shards` job runs `scripts/ci/honua-server-targeted-tests.sh` to pick the shards a diff exercises and emits a JSON `matrix_include` drawn from `.github/ci-shards.json`; `server-tests` consumes it via `strategy.matrix.include: fromJson(...)` so routine PRs only instantiate runners for selected shards. The full configured shard matrix runs on scheduled/manual full integration runs and PRs labeled `ci/full`. With the PR Gate build-reuse shadow enabled and exact one-member identity present, a separate non-gating job validates the trusted receipt, requires complete producer/consumer tree equality, safely restores the bounded payload, and runs registered proof tests with `--no-build --no-restore`; authoritative server shards still restore/build independently and `CI Gate` does not depend on the shadow. `scripts/ci/run-server-test-shard.sh` composes each shard filter as `(matrix.filter)&Tier!=Slow&Tier!=Fast`, emits heartbeat/tail diagnostics over normal-verbosity test logs, writes a `.timing.json` artifact, and enforces the inner `test_timeout_minutes` cap before the job-level `timeout_minutes` cancels the runner. PR shards therefore skip `[EmulatorTest]` / `[ScaleTest]` / `[ExternalServiceTest]` / `[CloudTest]` methods and do not rerun Fast tests already covered by the foundation lane. Expensive Python, browser, MCP, AOT, Docker/security, and expanded Postgres lanes run only in full CI (`schedule`, `workflow_dispatch`, or `ci/full`). |
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | PR | `pull_request`, `workflow_dispatch` | Yes | Path-scoped to API surface |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | PR + release | `pull_request`, `workflow_dispatch`, `release` | Yes (PR jobs) | PR governance separate from release publishing |
| `import-fidelity-scorecard-governance.yml` | Import Fidelity Scorecard Governance | PR | `pull_request`, `workflow_dispatch` | Yes | Path-scoped to parity/baseline/perf-budget assets; smoke-tests the perf-parity gate (#1249) via pass/fail fixtures |
| `trunk-sanity.yml` | Trunk Sanity | PR-adjacent sanity | `push` (trunk), `workflow_dispatch` | No | Cheap post-merge restore/build only; heavy CI does not run on merge-to-trunk pushes |
| `cite-conformance.yml` | OGC CITE Conformance (Features) | nightly | `schedule`, `workflow_dispatch` | No | Weekly Monday 6am UTC |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Tuesday 6am UTC |
| `cite-wfs20-conformance.yml` | WFS 2.0 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Monday 3am UTC |
| `cite-wms-conformance.yml` | OGC WMS CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Wednesday 6am UTC |
| `cite-wmts-conformance.yml` | OGC WMTS CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Thursday 6am UTC |
| `ogc-maps-conformance.yml` | OGC API Maps Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Friday 6am UTC |
| `cite-kml22-conformance.yml` | OGC KML 2.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Friday 3am UTC |
| `cite-gml32-conformance.yml` | OGC GML 3.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Saturday 6am UTC |
| `cite-gpkg12-conformance.yml` | OGC GeoPackage 1.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Saturday 3am UTC |
| `cite-evidence-report.yml` | CITE Evidence Report | weekly + manual | `schedule`, `workflow_dispatch` | No | Runs the public CITE suite set and builds `artifacts/cite-evidence/` with summary JSON, badge SVG, static index, and full TeamEngine HTML reports; optional GitHub Pages deployment gives the website a stable evidence URL. Weekly Friday 08:00 UTC (#2944), after the Wed/Thu per-suite crons; also asserts `docs/cite-status.md` freshness (fails + opens/updates an issue when the reviewed snapshot is >14 days stale) |
| `geoservices-import-fidelity-external.yml` | GeoServices Import Fidelity (External, On-Demand) | on-demand | `workflow_dispatch` | No | External parity vs live Esri services; run manually (upstream data drifts). Enforces both the correctness regression gate and the perf-parity latency gate (#1249) over the measured scorecard. Also runs `GeoservicesGeoportalImportIntegrationTests` (#2943), which previously matched no workflow filter. Renamed from `geoservices-import-fidelity-nightly.yml` (#2943) — it never ran on a schedule, so the filename now matches the deliberately on-demand-only reality (honua-server#1570) |
| `routing-nightly.yml` | Routing Nightly (pgRouting) | weekly | `schedule`, `workflow_dispatch` | No | Weekly Sunday 5:00 UTC (#2943); runs `Category=Routing` (`PgRoutingProviderIntegrationTests`, `NAServerPgRoutingEndToEndTests`) with `HONUA_ROUTING_TEST=1` — `PgRoutingFixture` manages its own Testcontainers `pgrouting/pgrouting` image, previously had no execution path anywhere |
| `warehouse-nightly.yml` | Warehouse Providers Nightly (Creds-Gated) | weekly | `schedule`, `workflow_dispatch` | No | Weekly Sunday 6:00 UTC (#2943); matrix over Honua.Snowflake/Redshift/Databricks/SqlServer.Tests, consuming optional repository secrets; surfaces passed/failed/skipped counts in the run summary so a missing secret reads as "not configured" rather than "silently absent from CI" |
| `cross-server-consume-nightly.yml` | Cross-Server Consume Nightly | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers via the Test-environment `/__test/cross-server-consume/proxy` endpoint, uploads TRX/report artifacts, and best-effort commits the refreshed gap report (warns instead of failing if push is blocked) |
| `windows-client-compat-nightly.yml` | Windows Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:15am UTC; full CERT-\* matrix (18 test cases × 4 protocol lanes: FeatureServer, OGC Features, MapServer, OData) with per-protocol `.cert.json` envelopes under `certification/`, plus `overall-summary.json`, per-lane transcripts, and `pack/`; supports `--profile smoke` (11-check MVP) and `--profile full` (default) |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:30am UTC; PyQGIS desktop client compatibility using real QGIS providers against `client-compat-v1.sql`; produces `desktop-qgis-ogc-features.cert.json` and `desktop-qgis-wfs.cert.json` envelopes |
| `sdk-server-compatibility.yml` | SDK Server Compatibility | nightly | `schedule`, `workflow_dispatch` | No | Manifest-driven last-3 server refs x last-3 SDK sets matrix from `docs/developer/sdk-compatibility-versions.json`; manual dispatch can pin `server_current_ref` for release-candidate evidence; checks out `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`, copies them to `$RUNNER_TEMP/sdk-compat`, and runs live compatibility smoke checks from the isolated copies so server repo build policy does not affect SDK source builds; records package versions/server commit/seed profile/surfaces/migration automation status/diagnostics in per-cell JSON evidence, and publishes `sdk-compatibility-matrix-<run-id>` with supported-cell regression failure |
| `client-interop-nightly.yml` | Real-Client Interop Matrix (Nightly) | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs the docker/client-compat matrix (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`) via Docker harnesses, diffs the per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` (gated by `expected-pairs.json`), refreshes `docs/gis/gap-report.md`, and fails strict mode on any baseline `pass`→non-`pass` regression, missing current envelope, missing expected-pair, missing committed baseline, or new `fail` in an unbaselined case. Lane artifacts include `lane-exit-code.txt` and `compose.log` when a lane exits non-zero; workflow-dispatch subsets are scoped by `--client-lanes`. Promote to PR-blocking once 30 consecutive nightly passes are observed (#806) |
| `gdal-driver-e2e.yml` | GDAL Driver End-to-End | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:45am UTC; runs `ogrinfo` + `ogr2ogr` against honua-server using GDAL's built-in `OAPIF:` stand-in driver. Tracks ADR-0034; swaps to `HONUA:` once the `honua-gdal` plugin ships |
| `load-soak-nightly.yml` | Load/Soak Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled load/soak tests |
| `nightly-slow-tier.yml` | Nightly Slow Tier (Emulator) | nightly | `schedule`, `workflow_dispatch` | No | Daily 4:00am UTC; runs `--filter "Tier=Slow&Category=Emulator"` across `Honua.Server.Tests`, `Honua.Db.Postgres.Tests`, and `Honua.Core.Tests` — `[EmulatorTest]` only. LocalStack S3 + Azurite are provisioned exclusively by `EmulatorFixture` (Testcontainers); Postgres comes from a GitHub Actions service container. Asserts `HONUA_TEST_DB_URL` before dispatch so a missing connection string fails loudly. The Scale/Cloud/External slow subfamilies (`[ScaleTest]`, `[CloudTest]`, `[ExternalServiceTest]`) need dedicated fixtures (multi-node compose, real cloud credentials, Esri Geoportal) and are tracked as separate workflows. ADR-0037 |
| `flaky-detection.yml` | Flaky Test Detection | nightly | `schedule`, `workflow_dispatch` | No | Daily 5:00am UTC; re-runs `--filter "Tier=Integration&Tier!=Slow"` three times against a fresh runner, parses TRX output, and uploads `flaky-detection-report` (JSON + per-iteration TRX) plus a `$GITHUB_STEP_SUMMARY` table of inconsistent tests. Always exits 0 — flake detection is a reporting concern, not a gate. ADR-0037 |
| `security-nightly.yml` | Security Nightly | nightly | `schedule`, `workflow_dispatch` | No | Consolidated NuGet vulnerability scan, Trivy filesystem scan, and container security scan (Hadolint, Trivy, structure tests, runtime constraints) |
| `codeql.yml` | CodeQL | nightly | `schedule` | No | Weekly security analysis |
| `nightly-container-build.yml` | Nightly Container Build | nightly | `schedule`, `workflow_dispatch` | No | Scheduled container build |
| `nuget-publish.yml` | NuGet Publish | release | `push`, `workflow_dispatch` | No | Release-only publishing |
| `deploy.yml` | Build & Publish Images | deploy | `push` (tags), `workflow_dispatch` | No | Builds and publishes multi-arch (and AOT) container images on `v*` tags. After `publish-manifests` succeeds on a tag, the `dispatch-geobench` job sends a `repository_dispatch` (`honua-server-release`) to `honua-io/geobench` carrying the release tag and `ghcr.io/honua-io/honua-server:<tag>` so the per-release benchmark suite (latency p50/p95/p99, RPS, error rate, cold-start) runs against the tagged image and flags regressions. Requires the `GEOBENCH_DISPATCH_TOKEN` secret (PAT with `repository_dispatch` on geobench); the step skips with a notice when the secret is absent (#1596) |
| `deploy-platform-images.yml` | Deploy Platform Images | deploy | `push` (tags), `workflow_dispatch` | No | Platform image deployment |
| `reusable-sdk-pr-gate.yml` | SDK PR Gate | PR | `workflow_call` | Yes (via caller) | Reusable gate for honua-sdk-js, honua-sdk-dotnet, and honua-sdk-python |
| `cloud-post-apply-validation.yml` | Cloud Post-Apply Validation | deploy | `workflow_call`, `workflow_dispatch` | No | Post-deploy validation |

Branch protection requires `PR Gate` and `Review Gate` together: unprivileged
verification plus trusted exact-head admission. `CI Gate` remains train-only.

## honua-sdk-js

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` | Yes | Core SDK PR gate |
| `integration.yml` | Integration | integration | `workflow_dispatch` / configured repo target | No | Repo-local Honua Server integration lane for `honua-sdk-js#39`; uploads integration metadata when a real server target is configured |
| `quickstart-staging.yml` | Quickstart Staging | integration | `workflow_dispatch` / staging | No | Staging quickstart smoke against Honua Server |
| `publish-js-sdk.yml` | Publish JS SDK | release | `workflow_dispatch`, `push` | No | Release-only |
| `publish-mcp-server.yml` | Publish MCP Server | release | `workflow_dispatch`, `push` | No | Release-only |
| `release-please.yml` | Release Please | maintenance | `push` | No | Version automation |

## honua-sdk-dotnet

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` | Yes | Core SDK PR gate |
| `staging-integration.yml` | Staging Integration | integration | `workflow_dispatch` / staging | No | Repo-local Honua Server integration lane for `honua-sdk-dotnet#31` |
| `publish-dotnet-sdk.yml` | Publish .NET SDK | release | `workflow_dispatch`, `push` | No | Release-only |
| `release-please.yml` | Release Please | maintenance | `push` | No | Version automation |

## honua-sdk-python

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` | Yes | Core SDK PR gate |
| `staging-integration.yml` | Staging Integration | integration | `workflow_dispatch` / staging | No | Repo-local Honua Server integration lane for `honua-sdk-python#21` |
| `publish-python-sdk.yml` | Publish Python SDK | release | `workflow_dispatch`, `push` | No | Release-only |
| `release-please.yml` | Release Please | maintenance | `push` | No | Version automation |

## Changes Made in This Audit (Ticket #485)

### Conformance workflows moved off PR path

The following workflows had `pull_request` and `push` triggers removed, leaving only `schedule` and `workflow_dispatch`:

- `cite-tiles-conformance.yml` (was PR + push + schedule)
- `cite-wfs20-conformance.yml` (was PR + schedule)
- `cite-wms-conformance.yml` (was PR + push + schedule)
- `cite-wmts-conformance.yml` (was PR + push + schedule)
- `ogc-maps-conformance.yml` (was PR + push + schedule)

Additionally, `cite-conformance.yml` (already schedule-only) had dead PR comment steps and a stale `pull-requests: write` permission removed to match.

**Rationale**: Conformance suites are external, heavyweight, and non-deterministic. They belong in the nightly certification lane, not the PR-blocking path. Regressions are caught by the weekly schedule and can be tested on-demand via `workflow_dispatch`.

### CodeQL moved off PR path

`codeql.yml` no longer triggers on `pull_request` or merge-to-trunk push. It runs on a weekly schedule. This avoids adding a slow, non-deterministic security scan to routine PR or merge cycles.

### PR template and validation redesigned

The PR template now includes explicit sections for gate impact, docs/contract impact, release/deploy impact, and breaking changes. The `pr-template-check` job at the top of `ci.yml` validates these sections directly; it replaces the previous standalone `pr-validation.yml` workflow and gates every downstream CI job through `pr-readiness`, so a malformed PR body no longer burns the full test matrix.

### Issue templates redesigned

All issue forms now require acceptance criteria, affected repos, gate-tier impact, and release/deploy impact. This ensures grooming inputs match the workflow contract.

### Reusable SDK PR gate added

`reusable-sdk-pr-gate.yml` provides a shared `workflow_call` contract for SDK repo PR gates. It accepts repo-specific build/test/lint commands and follows the toolchain and artifact conventions in `config-conventions.md`.

### Composite actions extracted

Five composite actions were added to `.github/actions/` for shared CI setup and evidence handling:

- `setup-dotnet-ci` — .NET SDK, NuGet cache *(active)*
- `setup-node-ci` — Node.js setup, npm cache *(future: SDK workflows)*
- `setup-python-ci` — Python setup, pip cache *(future: conformance/script workflows)*
- `upload-ci-evidence` — artifact upload with standard naming and tier-based retention *(active)*
- `run-conformance-stack` — Docker bootstrap/teardown for CITE workflows *(future: conformance workflows)*

## Changes Made in Workflow Refactor (2026-04-25)

### Security workflows consolidated

`container-security.yml` and `trivy-nightly.yml` were folded into `security-nightly.yml`. The consolidated nightly now owns NuGet vulnerability scanning, Trivy filesystem scanning, and container security validation in one security lane.

**Rationale**: The previous split created three scheduled security workflows with overlapping vulnerability-scan responsibilities and separate artifact conventions. One workflow keeps the security lane easier to monitor while preserving separate jobs for dependency, filesystem, and container concerns.

### CITE wrappers normalized

`cite-conformance.yml` and `cite-tiles-conformance.yml` now call `cite-conformance-common.yml`, matching the single-suite CITE wrappers for GML, GeoPackage, KML, WMS, and WMTS.

**Rationale**: Features and Tiles used the same checkout/build/run/parse/upload/fail skeleton as the reusable CITE workflow. Keeping only suite-specific inputs in the dispatcher files reduces drift in cache scopes, artifact upload behavior, and failure handling.
