# Operational Excellence (Current State)

This document summarizes the operational tooling that exists in this repository today. It focuses on what is implemented, not future plans.

## Release and Image Automation

- `deploy.yml`: build and publish container images (no environment deployment).
- `nightly-container-build.yml`: nightly image builds.
- `container-security.yml`, `security-nightly.yml`, `trivy-nightly.yml`: container and vulnerability scanning.

Contributor-focused CI and quality gates are documented separately in `docs/contributor/CI_QUALITY_GATES.md`.

## Deployment Artifacts

- Dockerfiles and published images (Docker Hub + GHCR).
- `infrastructure/` includes Docker Compose, Helm, and Terraform templates (including serverless modules).
- `scripts/deploy-*.sh` provide manual blue-green, canary, and rolling deployment helpers.

There are no built-in staging or production environments in this repository. Deployment is user-managed.

## Security

- API key authentication is required for admin and write operations.
- Optional OIDC authentication integrates with the admin policy and write endpoints when configured.
- Secret references are supported for connections: `env:` plus AWS Secrets Manager and Azure Key Vault resolvers.
- Container security scans are automated via the workflows listed above.

## Observability

- Health endpoints: `/healthz/live`, `/healthz/ready`.
- Metrics snapshots: `/api/v1/metrics/health`, `/api/v1/metrics/performance`, `/api/v1/metrics/database`, `/api/v1/metrics/cache`, `/api/v1/metrics/memory`.
- Admin observability: `/api/v1/admin/observability/errors`, `/api/v1/admin/observability/telemetry` (admin auth required).
- OTLP export via `OTEL_EXPORTER_OTLP_ENDPOINT` or `Tracing:OtlpEndpoint` for external collectors/Aspire.

## Runbooks

Operational playbooks live under `docs/devops/runbooks/`.
