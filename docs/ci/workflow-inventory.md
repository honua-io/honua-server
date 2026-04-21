# CI Workflow Inventory

> Canonical inventory of all GitHub Actions workflows across the Honua project.
> Last updated: 2026-04-18 (ticket #734)

## honua-server

| Workflow file | Name | Tier | Triggers | Merge-blocking | Notes |
|---|---|---|---|---|---|
| `ci.yml` | CI | PR | `pull_request`, `push` (trunk) | Yes | Core build, test, architecture gate; includes the merge-blocking operator eval harness lane (`Features.Eval|Features.Geoprocessing|Features.OgcProcesses|Features.Grpc`) and uploads `operator-eval-report` plus STAC and Esri Leaflet client-compat artifacts |
| `pr-validation.yml` | PR Validation | PR | `pull_request` | Yes | Template compliance check |
| `openapi-contract-governance.yml` | OpenAPI Contract Governance | PR | `pull_request`, `push`, `workflow_dispatch` | Yes | Path-scoped to API surface |
| `proto-wire-governance.yml` | Proto Wire Governance | PR | `pull_request`, `push`, `workflow_dispatch` | Yes | Path-scoped to `.proto` changes |
| `control-plane-sdk-governance.yml` | Control Plane SDK Governance | PR + release | `pull_request`, `push`, `workflow_dispatch`, `release` | Yes (PR jobs) | PR governance separate from release publishing |
| `parity-scorecard-governance.yml` | Parity Scorecard Governance | PR | `pull_request`, `push`, `workflow_dispatch` | Yes | Path-scoped to parity/baseline assets |
| `terraform-ci.yml` | Terraform CI | PR + deploy | `pull_request`, `push`, `workflow_dispatch` | Yes (plan/validate) | `fmt`/`validate`/`plan` in PR; apply in deploy |
| `performance-benchmarks.yml` | Performance Benchmarks | PR + nightly | `pull_request` (path-scoped), `push`, `schedule`, `workflow_dispatch` | Yes (critical regression) | Event-driven: quick load on PR, full load on push/schedule/manual |
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
| `windows-client-compat-nightly.yml` | Windows Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:15am UTC; full CERT-\* matrix (18 test cases × 4 protocol lanes: FeatureServer, OGC Features, MapServer, OData) with per-protocol `.cert.json` envelopes under `certification/`, plus `overall-summary.json`, per-lane transcripts, and `pack/`; supports `--profile smoke` (11-check MVP) and `--profile full` (default) |
| `pyqgis-client-compat-nightly.yml` | PyQGIS Client Compatibility Certification | nightly | `schedule`, `workflow_dispatch` | No | Daily 7:30am UTC; PyQGIS desktop client compatibility using real QGIS providers against `client-compat-v1.sql`; produces `desktop-qgis-ogc-features.cert.json` and `desktop-qgis-wfs.cert.json` envelopes |
| `load-soak-nightly.yml` | Load/Soak Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled load/soak tests |
| `container-security.yml` | Container Security | nightly | `schedule`, `workflow_dispatch` | No | Scheduled container scan |
| `security-nightly.yml` | Security Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled security analysis |
| `trivy-nightly.yml` | Trivy Nightly | nightly | `schedule`, `workflow_dispatch` | No | Scheduled Trivy scan |
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

### Performance workflows consolidated

`performance.yml` (nightly-only) was removed. Its scheduled coverage is now handled by `performance-benchmarks.yml` with event-driven mode selection:

- **PR**: path-filtered, reduced load parameters (quick load test)
- **push / schedule / manual**: full benchmark suite including full load tests

### CodeQL moved off PR path

`codeql.yml` no longer triggers on `pull_request`. It runs on `push` to trunk and on a weekly schedule. This avoids adding a slow, non-deterministic security scan to every PR cycle.

### PR template and validation redesigned

The PR template now includes explicit sections for gate impact, docs/contract impact, release/deploy impact, and breaking changes. The `pr-validation.yml` validator checks these sections directly instead of using brittle regex patterns.

### Issue templates redesigned

All issue forms now require acceptance criteria, affected repos, gate-tier impact, and release/deploy impact. This ensures grooming inputs match the workflow contract.

### Reusable SDK PR gate added

`reusable-sdk-pr-gate.yml` provides a shared `workflow_call` contract for SDK repo PR gates. It accepts repo-specific build/test/lint commands and follows the toolchain and artifact conventions in `config-conventions.md`.

### Composite actions extracted

Five composite actions were added to `.github/actions/`. Two are actively used in `performance-benchmarks.yml`; three are pre-positioned for future workflow adoption:

- `setup-dotnet-ci` — .NET SDK, NuGet cache *(active)*
- `setup-node-ci` — Node.js setup, npm cache *(future: SDK workflows)*
- `setup-python-ci` — Python setup, pip cache *(future: conformance/script workflows)*
- `upload-ci-evidence` — artifact upload with standard naming and tier-based retention *(active)*
- `run-conformance-stack` — Docker bootstrap/teardown for CITE workflows *(future: conformance workflows)*
