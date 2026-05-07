# CI and Quality Gates (Contributor Reference)

This document summarizes the CI pipelines and quality gates that contributors must satisfy. It is not an operations runbook.

> **Canonical references**: See [`docs/ci/gate-model.md`](../ci/gate-model.md) for the five-tier gate model, [`docs/ci/workflow-inventory.md`](../ci/workflow-inventory.md) for the full workflow inventory, and [`docs/ci/config-conventions.md`](../ci/config-conventions.md) for cross-repo configuration conventions.

## Core Workflows

- `ci.yml`: build, formatting verification, tier-aware test dispatch (PRs run only the `targeted-shards` subset emitted by `scripts/ci/honua-server-targeted-tests.sh`; scheduled/manual full integration runs the full configured `server-tests` matrix — see [Test Tier Strategy](#test-tier-strategy) and [ADR-0037](adr/0037-unified-ci-test-tier-strategy.md)), merge-blocking Esri Leaflet browser compatibility tests, and MCP certification (see [MCP Certification](mcp-certification.md)).
- `pr-validation.yml`: PR template compliance validation.
- `load-soak-nightly.yml`: nightly load/soak testing.
- `nightly-slow-tier.yml`: nightly `Tier=Slow&Category=Emulator` execution (`[EmulatorTest]` only) across `Honua.Server.Tests`, `Honua.Postgres.Tests`, and `Honua.Core.Tests`. Daily 4am UTC. Scale/Cloud/External slow subfamilies need dedicated fixtures and are tracked as separate workflows.
- `flaky-detection.yml`: nightly flake reporting — re-runs the integration tier 3× and uploads a flake-candidate report. Daily 5am UTC. See [ADR-0037](adr/0037-unified-ci-test-tier-strategy.md) for the quarantine workflow.
- `windows-client-compat-nightly.yml`: nightly/manual Windows client compatibility certification (full CERT-\* matrix: 18 test cases × 4 protocol lanes) with per-protocol `.cert.json` envelopes and reusable evidence pack artifacts.
- `client-interop-nightly.yml`: nightly real-client interop matrix that exercises Honua against actual GIS clients (QGIS, GDAL/OGR, OpenLayers, Cesium, ArcGIS Pro stub) via the Docker harnesses under `docker/client-compat/`. The workflow diffs per-lane `.cert.json` envelopes against `tests/baselines/client-compat/` (gated by `tests/baselines/client-compat/expected-pairs.json`), refreshes `docs/gis/gap-report.md`, and fails on any baseline `pass` regressing to a non-`pass` status, missing current envelope, missing expected pair, missing committed baseline, or new `fail` in an unbaselined case. Non-PR-blocking until 30 consecutive nightly passes (#806).
- `codeql.yml`: static analysis (nightly; not PR-blocking).
- `container-security.yml`: container security scanning (nightly).
- `cite-conformance.yml`, `cite-tiles-conformance.yml`, `cite-wfs20-conformance.yml`, `cite-wms-conformance.yml`, `cite-wmts-conformance.yml`, `cite-kml22-conformance.yml`, `cite-gml32-conformance.yml`, `cite-gpkg12-conformance.yml`, `ogc-maps-conformance.yml`: OGC conformance testing (nightly; not PR-blocking).
- `openapi-contract-governance.yml`: Admin/control-plane OpenAPI contract validation and breaking-change checks.
- `control-plane-sdk-governance.yml`: reproducible control-plane SDK generation and release artifact publishing.
- `nightly-container-build.yml`: nightly image builds.

## CI Gate (Required Status Check)

The `ci-gate` job in `ci.yml` is a summary job that depends on all merge-blocking CI jobs (`pr-readiness`, `changes`, `targeted-shards`, `build`, `test-all`, `aot-build`, `js-integration-tests`, `esri-leaflet-browser-tests`, `maplibre-compat`, `mcp-certification`, `core-package-compatibility`, `postgres-compat`, `docker-build`). Configure it as a required status check in branch protection to gate PRs on a single job.

The `targeted-shards` job runs `scripts/ci/honua-server-targeted-tests.sh` to pick the active shards for the diff, then projects the selection into a JSON `matrix_include` array drawn from `.github/ci-shards.json` (the single source of truth for both shard routing and matrix-runtime metadata). The `server-tests` job declares its matrix as `strategy.matrix.include: ${{ fromJson(needs.targeted-shards.outputs.matrix_include) }}`, so **unselected shards never instantiate a runner job** — there is no per-shard checkout, build, or Postgres service container cost for shards a PR did not select. On scheduled full CI and `workflow_dispatch` the descriptor is forced to `run_all: true`, so every configured shard entry appears in `matrix_include` and runs.

## Test Tier Strategy

ADR-0037 defines a `Tier` xUnit trait — `Fast`, `Integration`, or `Slow` — that the existing TestKit attributes emit alongside the legacy `Category` trait. Tier assignment is **additive on the attribute**, so no test method needs to be re-tagged for the default mapping. See [ADR-0037: Unified CI Test Tier Strategy](adr/0037-unified-ci-test-tier-strategy.md) and [TestKit attributes](testkit.md#test-categories-and-tiers) for the full mapping.

| Event | Workflow | Tier scope |
|---|---|---|
| Pull Request | `ci.yml` | Foundation tests (Core/Architecture/LoadTests, primarily `Tier=Fast`) plus a `--filter "Tier=Fast"` step against `Honua.Server.Tests` plus the `server-tests` shards selected by `scripts/ci/honua-server-targeted-tests.sh`. The shard step composes its filter as `(matrix.filter)&Tier!=Slow` so `[EmulatorTest]` / `[ScaleTest]` / `[ExternalServiceTest]` / `[CloudTest]` methods sitting in a shard's namespace are skipped on PRs. |
| Merge to trunk | `trunk-sanity.yml` | Restore and build only. Heavy CI is already covered by PR gates and scheduled/manual full integration runs. |
| Scheduled/manual full integration | `ci.yml` (`schedule` / `workflow_dispatch`) | Full configured `server-tests` matrix and the Postgres compat matrix. The `&Tier!=Slow` shard filter still applies — Slow stays nightly-only. |
| Nightly slow tier | `nightly-slow-tier.yml` | `--filter "Tier=Slow&Category=Emulator"` across `Honua.Server.Tests`, `Honua.Postgres.Tests`, `Honua.Core.Tests` — `[EmulatorTest]` only. Scale/Cloud/External slow subfamilies need dedicated workflows. |
| Nightly flake hunt | `flaky-detection.yml` | Re-runs `--filter "Tier=Integration&Tier!=Slow"` three times and reports inconsistent outcomes (never fails the workflow). |

The Tier=Fast PR step always runs regardless of which shards `targeted-shards` selects, so a PR whose diff matches no integration shard still exercises `[UnitTest]` methods in `Honua.Server.Tests`.

The shard map has a single source of truth: `.github/ci-shards.json` carries every shard's routing data (`paths`) and matrix-runtime metadata (`shard_name`, `filter`, `artifact_suffix`, `log_name`, `timeout_minutes`, `test_timeout_minutes`, `max_cpu_count`, upload flags). Adding or renaming a shard means editing one file. When a PR touches a source path under `unmapped_source_run_all_prefixes` (`src/Honua.Server/`, `src/Honua.DuckDB/`, `tests/dotnet/Honua.Server.Tests/`) that no shard claims, the targeted-tests script emits `{"run_all": true, "reason": "unmapped_source_change"}` so new feature directories run on a full matrix until a follow-up PR adds explicit shard routing.

`scripts/ci/run-server-test-shard.sh` is the shared shard runner for CI and local `scripts/ci/pre-pr-check.sh`. It applies `(matrix.filter)&Tier!=Slow`, emits 30-second heartbeat lines with elapsed time, writes normal-verbosity console output to the shard log, tails that log every fourth heartbeat so recently completed test methods are visible during long runs, enforces the per-shard `test_timeout_minutes` cap inside the job-level `timeout_minutes`, and writes `<log_name>.timing.json` next to the TRX/log artifacts for runtime triage.

`Tier=Slow` tests still respect their existing env-var skip logic (`HONUA_TEST_DB_URL`, `HONUA_TEST_S3_*`, `HONUA_TEST_AZURE_BLOB_*`, etc.). PR shards never run Slow-tagged tests (the test-invocation step composes `&Tier!=Slow`), so PR shards do not need LocalStack/Azurite — `server-tests` does not provision them. Emulator provisioning is owned by `EmulatorFixture` (Testcontainers) and only fires under `nightly-slow-tier.yml`, which asserts `HONUA_TEST_DB_URL` is set before the test step; the `[EmulatorTest]` attribute fills in defaults for any missing emulator env vars.

## Quality Gates

- Warnings are treated as errors during CI builds.
- XML docs (`CS1591`) are currently enforced for `Honua.Core` as warnings (phase-in plan), with full-repo enforcement planned.
- Formatting is enforced with `dotnet format` checks.
- API surface coverage is enforced via architecture tests.
## Conformance Baseline Policy

The CITE regression gates for implemented map/tile standards run on:
- scheduled weekly drift checks (see [`docs/ci/workflow-inventory.md`](../ci/workflow-inventory.md) for schedule)
- manual `workflow_dispatch` for on-demand validation

> **Note**: Conformance suites are not PR-blocking. They are external, heavyweight, and non-deterministic. See [`docs/ci/gate-model.md`](../ci/gate-model.md) for the rationale.

### Current baseline thresholds

| Workflow | Scope | Required baseline |
|---|---|---|
| `cite-conformance.yml` | OGC API Features 1.0 (`ets-ogcapi-features10`) | `failed_tests == 0` |
| `cite-wfs20-conformance.yml` | WFS 2.0 (`ets-wfs20`) | COMPLIANT or PARTIAL with `passed_tests > 0` ¹ |
| `cite-wms-conformance.yml` | WMS 1.3 (`ets-wms13`) | `results_available` and `failed_tests == 0` |
| `cite-wmts-conformance.yml` | WMTS 1.0 (`ets-wmts10`) | `results_available` and `failed_tests == 0` |
| `cite-tiles-conformance.yml` | OGC API Tiles 1.0 (`ets-ogcapi-tiles10`) | `results_available` and `failed_tests == 0` |
| `ogc-maps-conformance.yml` | OGC API Maps 1.0 (integration conformance suite) | `results_available`, `total_tests > 0`, and `failed_tests == 0` |
| `cite-kml22-conformance.yml` | KML 2.2 (`ets-kml22`) | `results_available` and `failed_tests == 0` |
| `cite-gml32-conformance.yml` | GML 3.2 (`ets-gml32`) | `results_available` and `failed_tests == 0` |
| `cite-gpkg12-conformance.yml` | GeoPackage 1.2 (`ets-gpkg12`) | `results_available` and `failed_tests == 0` |
| `windows-client-compat-nightly.yml` | Full CERT-\* matrix (automated) | Zero `fail` results in `.cert.json` envelopes; `skip`/`not-applicable` allowed with documented reason |
| `client-interop-nightly.yml` | Real-client interop matrix (Docker: gdal, pyqgis, openlayers, cesium, arcgis-stub) | Zero baseline `pass`→non-`pass` regressions vs `tests/baselines/client-compat/`; missing current envelopes, missing `expected-pairs.json` evidence, expected pairs without committed baselines, and new `fail` in unbaselined cases also fail the gate. Baseline-diff failure surfaces in `docs/gis/gap-report.md` |

¹ WFS 2.0 accepts partial compliance during development — the workflow passes when at least one test succeeds, even if some tests fail. NON_COMPLIANT status (zero passed tests) fails the workflow.

### Temporary failures

No temporary baseline failures are allowed in protected branch CI by default.
If a temporary exception is required, it must be documented in the PR and this file must be updated in the same PR with:
- exact failing test identifiers
- expiry date for the exception
- linked follow-up issue

### Conformance CI outputs

Each conformance workflow must publish:
- human-readable summary markdown (`cite-*-summary.md`)
- machine-readable raw result files (`testng-results.xml` and other TeamEngine XML/HTML artifacts)
- captured protocol metadata payloads (for example `conformance.json` or `capabilities.xml`)

## Local Conformance Runs

- OGC API Features: `./scripts/conformance/cite/run-cite-tests.sh`
- WFS 2.0: `./scripts/conformance/cite/run-cite-wfs20-tests.sh`
- OGC API Tiles: `./scripts/conformance/cite/run-cite-tiles-tests.sh`
- OGC API Maps: `./scripts/conformance/ogc/run-ogc-maps-conformance-tests.sh`
- WMS 1.3: `./scripts/conformance/cite/run-cite-wms-tests.sh`
- WMTS 1.0: `./scripts/conformance/cite/run-cite-wmts-tests.sh`
- KML 2.2: `./scripts/conformance/cite/run-cite-kml22-tests.sh`
- GML 3.2: `./scripts/conformance/cite/run-cite-gml32-tests.sh`
- GeoPackage 1.2: `./scripts/conformance/cite/run-cite-gpkg12-tests.sh`

Detailed setup and troubleshooting:
- `docs/contributor/cite-conformance-testing.md`
- `docs/contributor/cite-wfs20-conformance-testing.md`
- `docs/contributor/cite-tiles-conformance-testing.md`
- `docs/contributor/ogc-maps-conformance-testing.md`
- `docs/contributor/cite-wms-conformance-testing.md`
- `docs/contributor/cite-wmts-conformance-testing.md`
- `docs/contributor/cite-kml22-conformance-testing.md`
- `docs/contributor/cite-gml32-conformance-testing.md`
- `docs/contributor/cite-gpkg12-conformance-testing.md`

## Contract Governance vs Standards Compatibility

The CI quality gates enforce two distinct categories of API stability. They are separate concerns and are validated by different workflows.

### Admin/Control-Plane Contract Governance

These workflows enforce the control-plane versioning policy defined in `docs/developer/CONTROL_PLANE_VERSIONING_POLICY.md`:

| Workflow | What it checks |
|---|---|
| `openapi-contract-governance.yml` | OpenAPI spec shape and breaking-change detection for admin endpoints |
| `control-plane-sdk-governance.yml` | Reproducible SDK generation from the admin OpenAPI spec |

Breaking changes in these workflows require explicit opt-in (`OPENAPI_ALLOW_BREAKING_CHANGES=true`) and corresponding documentation updates.

### Standards Compatibility

These workflows enforce conformance to external geospatial standards. They are not governed by Honua's versioning policy; instead, correctness is defined by the upstream specification:

| Workflow | What it checks |
|---|---|
| `cite-conformance.yml` | OGC API Features CITE conformance |
| `cite-wfs20-conformance.yml` | WFS 2.0 CITE conformance |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE conformance |
| `ogc-maps-conformance.yml` | OGC API Maps conformance |
| `cite-wms-conformance.yml` | WMS 1.3 CITE conformance |
| `cite-wmts-conformance.yml` | WMTS 1.0 CITE conformance |
| `cite-kml22-conformance.yml` | KML 2.2 CITE conformance |
| `cite-gml32-conformance.yml` | GML 3.2 CITE conformance |
| `cite-gpkg12-conformance.yml` | GeoPackage 1.2 CITE conformance |
| `geoservices-parity-nightly.yml` | GeoServices REST parity checks (nightly) |
| `cross-server-consume-nightly.yml` | Honua-as-client WMS/WFS/WMTS reads against reference GeoServer and MapServer containers (nightly) |

Standards compatibility policy is documented in `docs/gis/STANDARDS_APIS.md`.

## Notes

Image publishing is handled by `deploy.yml` and related workflows. These build and publish container images but do not deploy to any environment.

For current protocol compatibility and conformance setup, use `docs/gis/MVP_COMPATIBILITY_CONTRACT.md` together with the contributor CITE guides linked above.
