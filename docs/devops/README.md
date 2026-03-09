# DevOps Documentation

Installing, configuring, operating, and upgrading Honua in production.

## Deployment

- [Infrastructure & Deployment](infrastructure.md) — deployment paths (Docker Compose, Helm, and cloud IaC handoff)
- [Docker Compose Sample](docker-compose.md) — pre-built image with PostGIS, Redis, MinIO
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md) — patterns by team size

Control-plane direction:
- Honua is building its own GitOps controller and deploy-coordination model.
- Flux and Argo CD are not the planned primary Honua control plane.
- Helm and Terraform remain deployment surfaces, not the source of truth for Honua change management.

## Security

- [Security](security.md) — authentication, authorization, rate limiting, CSP, credential rotation
- [Enterprise Procurement Readiness](../user/ENTERPRISE_PROCUREMENT_READINESS.md) — buyer-facing summary of security policy, support levels, and deployment commitments

## Monitoring & Performance

- [Monitoring & Alerting](monitoring.md) — health endpoints, metrics, OpenTelemetry, cloud-native alerting, optional Prometheus/Grafana
- [Operations](operations.md) — backups, migrations, connection pools, query tuning, caching, memory
- [Tile Operations Runbook](tile-operations-runbook.md) — async tile seed/warm/invalidate/purge controls and metrics
- [Feature Change Webhooks](feature-change-webhooks.md) — signed webhook delivery, replay cursor recovery, idempotency guidance

## Troubleshooting

- [Troubleshooting](troubleshooting.md) — database, performance, auth, import, and spatial query issues
- [Runbooks](runbooks/README.md) — incident response playbooks
