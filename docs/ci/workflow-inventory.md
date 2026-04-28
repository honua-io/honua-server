# CI Workflow Inventory

> Canonical inventory of all GitHub Actions workflows across the Honua project.
> Last updated: 2026-04-27 (ticket #809)

## honua-server

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` (trunk) | Yes | Core build, test, architecture gate; includes the merge-blocking operator eval harness lane (`Features.Eval|Features.Geoprocessing|Features.Protocols.Ogc.Api.Processes|Features.Protocols.Grpc|Features.Protocols.Mcp|Features.Protocols.GeoServices.GPServer`) and uploads `operator-eval-report` plus STAC and Esri Leaflet client-compat artifacts |
| `pr-validation.yml` | PR Validation | PR | `pull_request` | Yes | Template compliance check |
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | PR | `pull_request`, `push`, `workflow_dispatch` | Yes | Path-scoped to API surface |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | PR + release | `pull_request`, `push`, `workflow_dispatch`, `release` | Yes (PR jobs) | PR governance separate from release publishing |
| `parity-scorecard-governance.yml` | Parity Scorecard Governance | PR | `pull_request`, `push`, `workflow_dispatch` | Yes | Path-scoped to parity/baseline assets |
| `cite-conformance.yml` | OGC CITE Conformance (Features) | nightly | `schedule`, `workflow_dispatch` | No | Weekly Monday 6am UTC |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Tuesday 6am UTC |
| `cite-wfs20-conformance.yml` | WFS 2.0 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Monday 3am UTC |
| `cite-wms-conformance.yml` | OGC WMS CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Wednesday 6am UTC |
| `cite-wmts-conformance.yml` | OGC WMTS CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Thursday 6am UTC |
| `ogc-maps-conformance.yml` | OGC API Maps Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Friday 6am UTC |
| `cite-kml22-conformance.yml` | OGC KML 2.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Friday 3am UTC |
| `cite-gml32-conformance.yml` | OGC GML 3.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Saturday 6am UTC |
| `cite-gpkg12-conformance.yml` | OGC GeoPackage 1.2 CITE Conformance | nightly | `schedule`, `workflow_dispatch` | No | Weekly Saturday 3am UTC |
| `geoservices-parity-nightly.yml` | GeoServices Parity Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled parity check |
| `cross-server-consume-nightly.yml` | Cross-Server Consume Nightly | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers via the Test-environment `/__test/cross-server-consume/proxy` endpoint, uploads TRX/report artifacts, and best-effort commits the refreshed gap report (warns instead of failing if push is blocked) |
| `windows-client-compat-nightly.yml` | Windows Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:15am UTC; full CERT-\* matrix (18 test cases × 4 protocol lanes: FeatureServer, OGC Features, MapServer, OData) with per-protocol `.cert.json` envelopes under `certification/`, plus `overall-summary.json`, per-lane transcripts, and `pack/`; supports `--profile smoke` (11-check MVP) and `--profile full` (default) |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:30am UTC; PyQGIS desktop client compatibility using real QGIS providers against `client-compat-v1.sql`; produces `desktop-qgis-ogc-features.cert.json` and `desktop-qgis-wfs.cert.json` envelopes |
| `sdk-server-compatibility.yml` | SDK Server Compatibility | nightly | `push` (trunk), `schedule`, `workflow_dispatch` | No | Manifest-driven last-3 server refs x last-3 SDK sets matrix from `docs/developer/sdk-compatibility-versions.json`; runs live compatibility smoke checks through checked-out `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`, validates admin compatibility metadata plus seeded FeatureServer and OGC API Features surfaces, uploads per-cell JSON evidence, and publishes `sdk-compatibility-matrix-<run-id>` with supported-cell regression failure |
| `client-interop-nightly.yml` | Real-Client Interop Matrix (Nightly) | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:00am UTC; runs the docker/client-compat matrix (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`) via Docker harnesses, diffs the per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` (gated by `expected-pairs.json`), refreshes `docs/gis/gap-report.md`, and fails strict mode on any baseline `pass`→non-`pass` regression, missing lane envelope, missing expected-pair, or new `fail` in an unbaselined case. Promote to PR-blocking once 30 consecutive nightly passes are observed (#806) |
| `gdal-driver-e2e.yml` | GDAL Driver End-to-End | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:45am UTC; runs `ogrinfo` + `ogr2ogr` against honua-server using GDAL's built-in `OAPIF:` stand-in driver. Tracks ADR-0034; swaps to `HONUA:` once the `honua-gdal` plugin ships |
| `load-soak-nightly.yml` | Load/Soak Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled load/soak tests |
| `security-nightly.yml` | Security Nightly | nightly | `schedule`, `workflow_dispatch` | No | Consolidated NuGet vulnerability scan, Trivy filesystem scan, and container security scan (Hadolint, Trivy, structure tests, runtime constraints) |
| `codeql.yml` | CodeQL | nightly | `push` (trunk), `schedule` | No | Default-branch + weekly schedule |
| `nightly-container-build.yml` | Nightly Container Build | nightly | `schedule`, `workflow_dispatch` | No | Scheduled container build |
| `nuget-publish.yml` | NuGet Publish | release | `push`, `workflow_dispatch` | No | Release-only publishing |
| `deploy.yml` | Deploy | deploy | `push`, `workflow_dispatch` | No | Environment promotion |
| `deploy-platform-images.yml` | Deploy Platform Images | deploy | `push`, `workflow_dispatch` | No | Platform image deployment |
| `reusable-sdk-pr-gate.yml` | SDK PR Gate | PR | `workflow_call` | Yes (via caller) | Reusable gate for honua-sdk-js and honua-sdk-dotnet |
| `cloud-post-apply-validation.yml` | Cloud Post-Apply Validation | deploy | `workflow_call`, `workflow_dispatch` | No | Post-deploy validation |

## honua-sdk-js

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` | Yes | Core SDK PR gate |
| `publish-js-sdk.yml` | Publish JS SDK | release | `workflow_dispatch`, `push` | No | Release-only |
| `publish-mcp-server.yml` | Publish MCP Server | release | `workflow_dispatch`, `push` | No | Release-only |
| `release-please.yml` | Release Please | maintenance | `push` | No | Version automation |

## honua-sdk-dotnet

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` | Yes | Core SDK PR gate |
| `publish-dotnet-sdk.yml` | Publish .NET SDK | release | `workflow_dispatch`, `push` | No | Release-only |
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

`codeql.yml` no longer triggers on `pull_request`. It runs on `push` to trunk and on a weekly schedule. This avoids adding a slow, non-deterministic security scan to every PR cycle.

### PR template and validation redesigned

The PR template now includes explicit sections for gate impact, docs/contract impact, release/deploy impact, and breaking changes. The `pr-validation.yml` validator checks these sections directly instead of using brittle regex patterns.

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
