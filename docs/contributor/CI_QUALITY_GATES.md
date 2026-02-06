# CI and Quality Gates (Contributor Reference)

This document summarizes the CI pipelines and quality gates that contributors must satisfy. It is not an operations runbook.

## Core Workflows

- `ci.yml`: build, formatting verification, and full test suite.
- `pr-validation.yml`: fast validation for pull requests.
- `codeql.yml`: static analysis.
- `container-security.yml`: container security scanning.
- `performance.yml`, `performance-benchmarks.yml`, `load-soak-nightly.yml`: performance coverage.
- `cite-conformance.yml`, `cite-tiles-conformance.yml`: OGC conformance testing.
- `nightly-container-build.yml`: nightly image builds.

## Quality Gates

- Warnings are treated as errors during CI builds.
- Formatting is enforced with `dotnet format` checks.
- API surface coverage is enforced via architecture tests.
- Coverage thresholds are enforced via Codecov; see `CODECOV_SETUP.md` for current targets.

## Notes

Image publishing is handled by `deploy.yml` and related workflows. These build and publish container images but do not deploy to any environment.
