# DevOps Documentation

Installing, configuring, operating, and upgrading Honua in production.

## Deployment

- [Infrastructure & Deployment](infrastructure.md) — deployment paths (Docker Compose, Helm, Terraform AWS/Azure)
- [Terraform Validation Runbook](terraform-validation.md) — on-demand AWS/Azure/Kubernetes Terraform validation and integration testing
- [Docker Compose Sample](docker-compose.md) — pre-built image with PostGIS, Redis, MinIO
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md) — patterns by team size

## Security

- [Security](security.md) — authentication, authorization, rate limiting, CSP, credential rotation

## Monitoring & Performance

- [Monitoring & Alerting](monitoring.md) — health endpoints, metrics, OpenTelemetry, cloud-native alerting, optional Prometheus/Grafana
- [Operations](operations.md) — backups, migrations, connection pools, query tuning, caching, memory

## Troubleshooting

- [Troubleshooting](troubleshooting.md) — database, performance, auth, import, and spatial query issues
- [Runbooks](runbooks/README.md) — incident response playbooks
