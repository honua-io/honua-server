# CI and Quality Gates (Contributor Reference)

This document summarizes the CI pipelines and quality gates that contributors must satisfy. It is not an operations runbook.

> **Canonical references**: See [`docs/ci/gate-model.md`](../ci/gate-model.md) for the five-tier gate model, [`docs/ci/workflow-inventory.md`](../ci/workflow-inventory.md) for the full workflow inventory, and [`docs/ci/config-conventions.md`](../ci/config-conventions.md) for cross-repo configuration conventions.

## Core Workflows

- `ci.yml`: build, formatting verification, full test suite, and MCP certification (see [MCP Certification](mcp-certification.md)).
- `pr-validation.yml`: PR template compliance validation.
- `performance-benchmarks.yml`: performance benchmarks (`quick` mode on PR, `full` mode on nightly/manual).
- `load-soak-nightly.yml`: nightly load/soak testing.
- `codeql.yml`: static analysis (nightly + trunk push; not PR-blocking).
- `container-security.yml`: container security scanning (nightly).
- `cite-conformance.yml`, `cite-tiles-conformance.yml`, `cite-wfs20-conformance.yml`, `cite-wms-conformance.yml`, `cite-wmts-conformance.yml`, `ogc-maps-conformance.yml`: OGC conformance testing (nightly; not PR-blocking).
- `openapi-contract-governance.yml`: Admin/control-plane OpenAPI contract validation and breaking-change checks.
- `control-plane-sdk-governance.yml`: reproducible control-plane SDK generation and release artifact publishing.
- `proto-wire-governance.yml`: protobuf wire compatibility enforcement via `buf breaking`.
- `nightly-container-build.yml`: nightly image builds.

## CI Gate (Required Status Check)

The `ci-gate` job in `ci.yml` is a summary job that depends on all merge-blocking CI jobs (`changes`, `build`, `test-all`, `aot-build`, `js-integration-tests`, `mcp-certification`, `core-package-compatibility`, `docker-build`, `llm-architecture-review`, `architecture-gate`). Configure it as a required status check in branch protection to gate PRs on a single job.

## Quality Gates

- Warnings are treated as errors during CI builds.
- XML docs (`CS1591`) are currently enforced for `Honua.Core` as warnings (phase-in plan), with full-repo enforcement planned.
- Formatting is enforced with `dotnet format` checks.
- API surface coverage is enforced via architecture tests.
- Coverage thresholds are enforced via Codecov; see `CODECOV_SETUP.md` for current targets.

## Conformance Baseline Policy

The CITE regression gates for implemented map/tile standards run on:
- scheduled weekly drift checks (see [`docs/ci/workflow-inventory.md`](../ci/workflow-inventory.md) for schedule)
- manual `workflow_dispatch` for on-demand validation

> **Note**: Conformance suites are not PR-blocking. They are external, heavyweight, and non-deterministic. See [`docs/ci/gate-model.md`](../ci/gate-model.md) for the rationale.

### Current baseline thresholds

| Workflow | Scope | Required baseline |
|---|---|---|
| `cite-wms-conformance.yml` | WMS 1.3 (`ets-wms13`) | `failed_tests == 0` and `total_tests > 0` |
| `cite-wmts-conformance.yml` | WMTS 1.0 (`ets-wmts10`) | `failed_tests == 0` and `total_tests > 0` |
| `cite-tiles-conformance.yml` | OGC API Tiles 1.0 (`ets-ogcapi-tiles10`) | `failed_tests == 0` and `total_tests > 0` |
| `ogc-maps-conformance.yml` | OGC API Maps 1.0 (integration conformance suite) | `failed_tests == 0` and `total_tests > 0` |

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

- OGC API Tiles: `./scripts/run-cite-tiles-tests.sh`
- OGC API Maps: `./scripts/run-ogc-maps-conformance-tests.sh`
- WMS 1.3: `./scripts/run-cite-wms-tests.sh`
- WMTS 1.0: `./scripts/run-cite-wmts-tests.sh`

Detailed setup and troubleshooting:
- `docs/contributor/cite-tiles-conformance-testing.md`
- `docs/contributor/ogc-maps-conformance-testing.md`
- `docs/contributor/cite-wms-conformance-testing.md`
- `docs/contributor/cite-wmts-conformance-testing.md`

## Contract Governance vs Standards Compatibility

The CI quality gates enforce two distinct categories of API stability. They are separate concerns and are validated by different workflows.

### Admin/Control-Plane Contract Governance

These workflows enforce the control-plane versioning policy defined in `docs/user/CONTROL_PLANE_VERSIONING_POLICY.md`:

| Workflow | What it checks |
|---|---|
| `openapi-contract-governance.yml` | OpenAPI spec shape and breaking-change detection for admin endpoints |
| `control-plane-sdk-governance.yml` | Reproducible SDK generation from the admin OpenAPI spec |
| `proto-wire-governance.yml` | Protobuf wire compatibility via `buf breaking` |

Breaking changes in these workflows require explicit opt-in (`OPENAPI_ALLOW_BREAKING_CHANGES=true` or `BUF_ALLOW_BREAKING_CHANGES=true`) and corresponding documentation updates.

### Standards Compatibility

These workflows enforce conformance to external geospatial standards. They are not governed by Honua's versioning policy; instead, correctness is defined by the upstream specification:

| Workflow | What it checks |
|---|---|
| `cite-conformance.yml` | OGC API Features CITE conformance |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE conformance |
| `ogc-maps-conformance.yml` | OGC API Maps conformance |
| `cite-wms-conformance.yml` | WMS 1.3 CITE conformance |
| `cite-wmts-conformance.yml` | WMTS 1.0 CITE conformance |
| `geoservices-parity-nightly.yml` | GeoServices REST parity checks (nightly) |

Standards compatibility policy is documented in `docs/user/STANDARDS_APIS.md`.

## Notes

Image publishing is handled by `deploy.yml` and related workflows. These build and publish container images but do not deploy to any environment.

For the current protocol parity and OGC CITE automation audit, see `docs/contributor/PROTOCOL_PARITY_305_310_AUDIT.md`.
