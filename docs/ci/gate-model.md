# CI Gate Model

> Defines the five-tier quality gate model governing all CI workflows across the Honua project.
> Last updated: 2026-04-14 (ticket #463)

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

5. **AOT verification is post-merge/manual until trim debt is retired.** The `AOT Build Verification` job in `ci.yml` runs on `push` to `trunk` and `workflow_dispatch`, not on PRs, because its current failures are not consistently fast or author-actionable within the PR lane.

6. **The `Tier=Fast|Integration|Slow` test trait is a sub-tier inside this gate model, not a replacement for it.** ADR-0035 splits the .NET test suite by execution cost so that PRs run only the `Tier=Fast` foundation tests plus a targeted subset of `Tier=Integration` shards selected by `scripts/ci/honua-server-targeted-tests.sh`, while `Tier=Slow` runs nightly via `nightly-slow-tier.yml` and the integration tier is re-run nightly by `flaky-detection.yml` for flake reporting. The five-tier PR/nightly/release/deploy/maintenance gate model above still defines *where* a workflow lives; the trait defines *which subset of tests* runs inside the workflow.

## PR Lane (Required Checks)

These workflows are merge-blocking for all PRs to trunk:

| Workflow | What it validates | Path filter |
|---|---|---|
| `ci.yml` | Build, test, architecture gate, Esri Leaflet browser compatibility | None (always runs) |
| `pr-validation.yml` | PR template compliance | None (always runs) |
| `openapi-contract-governance.yml` | OpenAPI spec stability | `src/**/api-specs/**`, `*.openapi.*` |
| `control-plane-sdk-governance.yml` | Control plane SDK governance | SDK/control-plane paths |
| `parity-scorecard-governance.yml` | Parity baseline stability | Parity/baseline asset paths |

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
| `geoservices-parity-nightly.yml` | Scheduled | GeoServices parity check |
| `cross-server-consume-nightly.yml` | Daily 7:00am UTC | Honua-as-client WMS/WFS/WMTS reads against GeoServer and MapServer reference containers |
| `windows-client-compat-nightly.yml` | Daily 7:15am UTC | Full CERT-\* matrix certification (18 test cases × 4 protocol lanes) with `.cert.json` envelopes + reusable evidence pack |
| `pyqgis-client-compat-nightly.yml` | Daily 7:30am UTC | PyQGIS desktop client compatibility (OGC Features + WFS) with per-protocol `.cert.json` envelopes |
| `sdk-server-compatibility.yml` | Monday 8:35am UTC plus post-merge `trunk` pushes | Manifest-driven last-3 server refs x last-3 SDK sets compatibility matrix through `honua-sdk-js`, `honua-sdk-python`, and `honua-sdk-dotnet`; publishes the `sdk-compatibility-matrix-<run-id>` table artifact and fails on supported-cell regressions |
| `client-interop-nightly.yml` | Daily 7:00am UTC | Real-client interop matrix via Docker harnesses (`gdal`, `pyqgis`, `openlayers`, `cesium`, `arcgis-stub`); diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` and refreshes `docs/gis/gap-report.md`. Non-blocking until 30 consecutive nightly passes (#806) |
| `gdal-driver-e2e.yml` | Daily 7:45am UTC | GDAL `ogrinfo` + `ogr2ogr` round-trip against honua-server (ADR-0034 stand-in until `honua-gdal` plugin ships) |
| `load-soak-nightly.yml` | Scheduled | Load and soak testing |
| `nightly-slow-tier.yml` | Daily 4am UTC | `Tier=Slow` .NET tests across `Honua.Server.Tests`, `Honua.Postgres.Tests`, `Honua.Core.Tests` ([ScaleTest], [ExternalServiceTest], [EmulatorTest], [CloudTest]); asserts required `HONUA_TEST_*` env vars before dispatch (ADR-0035) |
| `flaky-detection.yml` | Daily 5am UTC | Re-runs `Tier=Integration&Tier!=Slow` three times and uploads a flake-candidate report; reporting only — never fails the workflow (ADR-0035) |
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
| `deploy.yml` | Push / manual | Environment promotion |
| `deploy-platform-images.yml` | Push / manual | Platform image deployment |
| `cloud-post-apply-validation.yml` | Workflow call / manual | Post-deploy validation |

## Maintenance Lane

| Workflow | Trigger | What it does |
|---|---|---|
| `release-please.yml` (SDK repos only) | Push | Version automation |

## Adding New Checks

Before adding a new workflow or check:

1. **Identify the tier.** Default to nightly unless the check meets PR-lane criteria.
2. **PR-lane criteria:** deterministic, < 5 min, author-actionable, no external dependencies.
3. **Document the new check** in this file and in `workflow-inventory.md`.
4. **Use path filters** for PR-lane checks that only apply to specific code areas.
5. **Follow artifact conventions** from `config-conventions.md`.
