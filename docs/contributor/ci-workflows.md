# GitHub Actions Workflows

This document tracks the current workflow layout. The source of truth is `.github/workflows/`.

## Core Workflows

| Workflow File | Purpose | Typical Trigger |
|---|---|---|
| `ci.yml` | Main build/test/format/validation pipeline | PR + push to `trunk` |
| `pr-validation.yml` | PR policy checks (labels/title/metadata) | PR events |
| `codeql.yml` | CodeQL static analysis | PR + push + schedule |
| `container-security.yml` | Container image security scanning | PR + push + schedule |
| `deploy.yml` | Build/publish container images | push to `trunk` and tags |
| `cloud-post-apply-validation.yml` | Reusable post-`terraform apply` validation against a real deployed environment | `workflow_call` + manual |

## Conformance and Performance

| Workflow File | Purpose | Typical Trigger |
|---|---|---|
| `cite-conformance.yml` | OGC API Features CITE tests | schedule + manual |
| `cite-tiles-conformance.yml` | OGC API Tiles CITE tests | PR + push + schedule + manual |
| `ogc-maps-conformance.yml` | OGC API Maps conformance tests | PR + push + schedule + manual |
| `cite-wms-conformance.yml` | OGC WMS 1.3 CITE tests | PR + push + schedule + manual |
| `cite-wmts-conformance.yml` | OGC WMTS 1.0 CITE tests | PR + push + schedule + manual |
| `openapi-contract-governance.yml` | Control-plane OpenAPI validation + breaking-change diff | PR + push + manual |
| `control-plane-sdk-governance.yml` | Reproducible control-plane SDK generation and release assets | PR + push + release + manual |
| `performance.yml` | Performance benchmark pipeline | PR/push/manual |
| `performance-benchmarks.yml` | Extended benchmark + baseline flow | PR/push/manual |
| `load-soak-nightly.yml` | Load/soak runs | schedule + manual |

## Nightly/Scheduled Security

| Workflow File | Purpose |
|---|---|
| `security-nightly.yml` | Dependency/security scanning |
| `trivy-nightly.yml` | Nightly Trivy scan |
| `nightly-container-build.yml` | Nightly container build checks |

## Useful Commands

```bash
# List recent runs for CI
gh run list --workflow=ci.yml

# View logs for a run
gh run view <run-id> --log

# Manually start a workflow
gh workflow run load-soak-nightly.yml
```

## Notes for Contributors

- If docs and workflows disagree, trust `.github/workflows/*.yml`.
- When adding a workflow, update this file in the same PR.
- `cloud-post-apply-validation.yml` is designed to be called from `honua-terraform` after provisioning succeeds, not to bring Terraform ownership back into `honua-server`.
- The workflow supports both non-mutating deployed-environment checks and an explicit opt-in live import publish/query round-trip via the `import_table_prefix` input plus DB connection envs.
- A real deploy-plan check is also supported when the caller supplies a configured deploy target id and revision inputs through environment handoff.
