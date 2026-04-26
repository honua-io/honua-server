# CI and Quality Gates (Contributor Reference)

This document summarizes the CI pipelines and quality gates that contributors must satisfy. It is not an operations runbook.

> **Canonical references**: See [`docs/ci/gate-model.md`](../ci/gate-model.md) for the five-tier gate model, [`docs/ci/workflow-inventory.md`](../ci/workflow-inventory.md) for the full workflow inventory, and [`docs/ci/config-conventions.md`](../ci/config-conventions.md) for cross-repo configuration conventions.

## Core Workflows

- `ci.yml`: build, formatting verification, full test suite, merge-blocking Esri Leaflet browser compatibility tests, and MCP certification (see [MCP Certification](mcp-certification.md)).
- `pr-validation.yml`: PR template compliance validation.
- `load-soak-nightly.yml`: nightly load/soak testing.
- `windows-client-compat-nightly.yml`: nightly/manual Windows client compatibility certification (full CERT-\* matrix: 18 test cases × 4 protocol lanes) with per-protocol `.cert.json` envelopes and reusable evidence pack artifacts.
- `codeql.yml`: static analysis (nightly + trunk push; not PR-blocking).
- `container-security.yml`: container security scanning (nightly).
- `cite-conformance.yml`, `cite-tiles-conformance.yml`, `cite-wfs20-conformance.yml`, `cite-wms-conformance.yml`, `cite-wmts-conformance.yml`, `cite-kml22-conformance.yml`, `cite-gml32-conformance.yml`, `cite-gpkg12-conformance.yml`, `ogc-maps-conformance.yml`: OGC conformance testing (nightly; not PR-blocking).
- `openapi-contract-governance.yml`: Admin/control-plane OpenAPI contract validation and breaking-change checks.
- `control-plane-sdk-governance.yml`: reproducible control-plane SDK generation and release artifact publishing.
- `nightly-container-build.yml`: nightly image builds.

## CI Gate (Required Status Check)

The `ci-gate` job in `ci.yml` is a summary job that depends on all merge-blocking CI jobs (`pr-readiness`, `changes`, `build`, `test-all`, `aot-build`, `js-integration-tests`, `esri-leaflet-browser-tests`, `mcp-certification`, `core-package-compatibility`, `postgres-compat`, `docker-build`, `llm-architecture-review`, `architecture-gate`). Configure it as a required status check in branch protection to gate PRs on a single job.

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

Standards compatibility policy is documented in `docs/gis/STANDARDS_APIS.md`.

## Notes

Image publishing is handled by `deploy.yml` and related workflows. These build and publish container images but do not deploy to any environment.

For current protocol compatibility and conformance setup, use `docs/gis/MVP_COMPATIBILITY_CONTRACT.md` together with the contributor CITE guides linked above.
