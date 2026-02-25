# CI and Quality Gates (Contributor Reference)

This document summarizes the CI pipelines and quality gates that contributors must satisfy. It is not an operations runbook.

## Core Workflows

- `ci.yml`: build, formatting verification, and full test suite.
- `pr-validation.yml`: fast validation for pull requests.
- `codeql.yml`: static analysis.
- `container-security.yml`: container security scanning.
- `performance.yml`, `performance-benchmarks.yml`, `load-soak-nightly.yml`: performance coverage.
- `cite-conformance.yml`, `cite-tiles-conformance.yml`, `cite-wms-conformance.yml`, `cite-wmts-conformance.yml`, `ogc-maps-conformance.yml`: OGC conformance testing.
- `openapi-contract-governance.yml`: Admin/control-plane OpenAPI contract validation and breaking-change checks.
- `control-plane-sdk-governance.yml`: reproducible control-plane SDK generation and release artifact publishing.
- `nightly-container-build.yml`: nightly image builds.

## Quality Gates

- Warnings are treated as errors during CI builds.
- XML docs (`CS1591`) are currently enforced for `Honua.Core` as warnings (phase-in plan), with full-repo enforcement planned.
- Formatting is enforced with `dotnet format` checks.
- API surface coverage is enforced via architecture tests.
- Coverage thresholds are enforced via Codecov; see `CODECOV_SETUP.md` for current targets.

## Conformance Baseline Policy

The CITE regression gates for implemented map/tile standards must pass on:
- pull requests targeting `trunk`/`main`
- pushes to `trunk`/`main`
- scheduled weekly drift checks

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

## Notes

Image publishing is handled by `deploy.yml` and related workflows. These build and publish container images but do not deploy to any environment.

For the current protocol parity and OGC CITE automation audit, see `docs/contributor/PROTOCOL_PARITY_305_310_AUDIT.md`.
