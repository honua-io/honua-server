# Operational Excellence

This page summarizes the operational tooling that exists in this repository today.

---

## Release and Image Automation

- GitHub Actions workflows build and publish container images.
- Security and vulnerability scans run in CI.

---

## Deployment Artifacts

- Dockerfiles and published images (Docker Hub + GHCR).
- `infrastructure/` includes Docker Compose, Helm, and Terraform templates.
- Deployment is user-managed (no built-in staging or prod environments).

---

## Observability

- Health: `/healthz/live`, `/healthz/ready`
- Metrics snapshots: `/api/v1/metrics/*`
- Admin observability: `/api/v1/admin/observability/*`

---

## Runbooks

Operational playbooks live under `docs/devops/runbooks/`.
