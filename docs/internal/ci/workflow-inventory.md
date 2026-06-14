# CI Workflow Inventory

> Canonical inventory of all GitHub Actions workflows across the Honua project.
> Last updated: 2026-05-18

## honua-server

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR + nightly | `pull_request`, `schedule`, `workflow_dispatch` | Yes | Core build, test, architecture gate, CI router validation, JavaScript typecheck, and baseline Postgres compatibility; the `pr-template-check` job runs first and `pr-readiness` (and therefore every downstream test job) depends on it, so a malformed PR body short-circuits the pipeline before heavy runners are provisioned; per ADR-0037 the `targeted-shards` job runs `scripts/ci/honua-server-targeted-tests.sh` to pick the shards a diff exercises and emits a JSON `matrix_include` drawn from `.github/ci-shards.json`; `server-tests` consumes it via `strategy.matrix.include: fromJson(...)` so routine PRs only instantiate runners for selected shards. The full configured shard matrix runs on scheduled/manual full integration runs and PRs labeled `ci/full`. `scripts/ci/run-server-test-shard.sh` composes each shard filter as `(matrix.filter)&Tier!=Slow&Tier!=Fast`, emits heartbeat/tail diagnostics over normal-verbosity test logs, writes a `.timing.json` artifact, and enforces the inner `test_timeout_minutes` cap before the job-level `timeout_minutes` cancels the runner. PR shards therefore skip `[EmulatorTest]` / `[ScaleTest]` / `[ExternalServiceTest]` / `[CloudTest]` methods and do not rerun Fast tests already covered by the foundation lane. Expensive Python, browser, MCP, AOT, Docker/security, and expanded Postgres lanes run only in full CI (`schedule`, `workflow_dispatch`, or `ci/full`). |
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | PR | `pull_request`, `workflow_dispatch` | Yes | Path-scoped to API surface |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | PR + release | `pull_request`, `workflow_dispatch`, `release` | Yes (PR jobs) | PR governance separate from release publishing |
| `parity-scorecard-governance.yml` | Parity Scorecard Governance | PR | `pull_request`, `workflow_dispatch` | Yes | Path-scoped to parity/baseline assets |
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
| `cite-evidence-report.yml` | CITE Evidence Report | manual | `workflow_dispatch` | No | Runs the public CITE suite set and builds `artifacts/cite-evidence/` with summary JSON, badge SVG, static index, and full TeamEngine HTML reports; optional GitHub Pages deployment gives the website a stable evidence URL |
| `geoservices-parity-nightly.yml` | GeoServices Parity (External, On-Demand) | on-demand | `workflow_dispatch` | No | External parity vs live Esri services; run manually (upstream data drifts) |
| `cross-server-consume-nightly.yml` | Cross-Server Consume Nightly | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers via the Test-environment `/__test/cross-server-consume/proxy` endpoint, uploads TRX/report artifacts, and best-effort commits the refreshed gap report (warns instead of failing if push is blocked) |
| `windows-client-compat-nightly.yml` | Windows Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:15am UTC; full CERT-\* matrix (18 test cases × 4 protocol lanes: FeatureServer, OGC Features, MapServer, OData) with per-protocol `.cert.json` envelopes under `certification/`, plus `overall-summary.json`, per-lane transcripts, and `pack/`; supports `--profile smoke` (11-check MVP) and `--profile full` (default) |
| `arcgis-pro-desktop-evidence.yml` | Licensed ArcGIS Pro Desktop Evidence | nightly/manual | `schedule`, `workflow_dispatch` | No | Weekly scaffold for `desktop-arcgis` licensed evidence. The self-hosted Windows ArcGIS Pro job runs only when manually dispatched with `run_licensed_lane=true` or when `ARCGIS_PRO_EVIDENCE_ENABLED=true`; no PR trigger. Invokes the ArcPy runner against a seeded Honua FeatureServer/MapServer target, emits `desktop-arcgis` `.cert.json` envelopes, captures active-view or layout/map-frame screenshots, validates live evidence refs and redaction, writes an artifact manifest, and uploads nightly-retention evidence. |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:30am UTC; PyQGIS desktop client compatibility using real QGIS providers against `client-compat-v1.sql`; produces `desktop-qgis-ogc-features.cert.json` and `desktop-qgis-wfs.cert.json` envelopes |
| `sdk-server-compatibility.yml` | SDK Server Compatibility | nightly | `schedule`, `workflow_dispatch` | No | Manifest-driven last-3 server refs x last-3 SDK sets matrix from `docs/developer/sdk-compatibility-versions.json`; manual dispatch can pin `server_current_ref` for release-candidate evidence; checks out `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`, copies them to `$RUNNER_TEMP/sdk-compat`, and runs live compatibility smoke checks from the isolated copies so server repo build policy does not affect SDK source builds; records package versions/server commit/seed profile/surfaces/migration automation status/diagnostics in per-cell JSON evidence, and publishes `sdk-compatibility-matrix-<run-id>` with supported-cell regression failure |
| `client-interop-nightly.yml` | Real-Client Interop Matrix (Nightly) | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs the docker/client-compat matrix (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`) via Docker harnesses, diffs the per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` (gated by `expected-pairs.json`), refreshes `docs/gis/gap-report.md`, and fails strict mode on any baseline `pass`→non-`pass` regression, missing current envelope, missing expected-pair, missing committed baseline, or new `fail` in an unbaselined case. Lane artifacts include `lane-exit-code.txt` and `compose.log` when a lane exits non-zero; workflow-dispatch subsets are scoped by `--client-lanes`. Promote to PR-blocking once 30 consecutive nightly passes are observed (#806) |
| `gdal-driver-e2e.yml` | GDAL Driver End-to-End | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:45am UTC; runs `ogrinfo` + `ogr2ogr` against honua-server using GDAL's built-in `OAPIF:` stand-in driver. Tracks ADR-0034; swaps to `HONUA:` once the `honua-gdal` plugin ships |
| `load-soak-nightly.yml` | Load/Soak Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled load/soak tests |
| `nightly-slow-tier.yml` | Nightly Slow Tier (Emulator) | nightly | `schedule`, `workflow_dispatch` | No | Daily 4:00am UTC; runs `--filter "Tier=Slow&Category=Emulator"` across `Honua.Server.Tests`, `Honua.Postgres.Tests`, and `Honua.Core.Tests` — `[EmulatorTest]` only. LocalStack S3 + Azurite are provisioned exclusively by `EmulatorFixture` (Testcontainers); Postgres comes from a GitHub Actions service container. Asserts `HONUA_TEST_DB_URL` before dispatch so a missing connection string fails loudly. The Scale/Cloud/External slow subfamilies (`[ScaleTest]`, `[CloudTest]`, `[ExternalServiceTest]`) need dedicated fixtures (multi-node compose, real cloud credentials, Esri Geoportal) and are tracked as separate workflows. ADR-0037 |
| `flaky-detection.yml` | Flaky Test Detection | nightly | `schedule`, `workflow_dispatch` | No | Daily 5:00am UTC; re-runs `--filter "Tier=Integration&Tier!=Slow"` three times against a fresh runner, parses TRX output, and uploads `flaky-detection-report` (JSON + per-iteration TRX) plus a `$GITHUB_STEP_SUMMARY` table of inconsistent tests. Always exits 0 — flake detection is a reporting concern, not a gate. ADR-0037 |
| `security-nightly.yml` | Security Nightly | nightly | `schedule`, `workflow_dispatch` | No | Consolidated NuGet vulnerability scan, Trivy filesystem scan, and container security scan (Hadolint, Trivy, structure tests, runtime constraints) |
| `codeql.yml` | CodeQL | nightly | `schedule` | No | Weekly security analysis |
| `nightly-container-build.yml` | Nightly Container Build | nightly | `schedule`, `workflow_dispatch` | No | Scheduled container build |
| `nuget-publish.yml` | NuGet Publish | release | `push`, `workflow_dispatch` | No | Release-only publishing |
| `deploy.yml` | Deploy | deploy | `push` (tags), `workflow_dispatch` | No | Environment promotion |
| `deploy-platform-images.yml` | Deploy Platform Images | deploy | `push` (tags), `workflow_dispatch` | No | Platform image deployment |
| `reusable-sdk-pr-gate.yml` | SDK PR Gate | PR | `workflow_call` | Yes (via caller) | Reusable gate for honua-sdk-js, honua-sdk-dotnet, and honua-sdk-python |
| `cloud-post-apply-validation.yml` | Cloud Post-Apply Validation | deploy | `workflow_call`, `workflow_dispatch` | No | Post-deploy validation |

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
