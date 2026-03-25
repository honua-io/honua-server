# Operator Guide

Deploy, configure, monitor, and manage Honua Server.

## Getting Started

- [Infrastructure & Deployment](infrastructure.md) — Docker Compose, Kubernetes, cloud deployment
- [Docker Compose Reference](docker-compose.md) — Local and evaluation setup
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md) — Patterns by team size

## Configuration

- [Environment Variables](../../.env.example) — Complete configuration reference
- [Security](security.md) — Authentication, authorization, CORS, CSP
- [Feature Change Webhooks](feature-change-webhooks.md) — Event notification setup

## Server Management

- [Control Plane API](CONTROL_PLANE_API.md) — Admin REST API for connections, layers, services
- [Tile Operations](tile-operations-runbook.md) — Vector tile seeding, warming, invalidation
- [Operations](operations.md) — Backups, migrations, connection pooling, query tuning

## Monitoring

- [Monitoring & Observability](monitoring.md) — Health checks, Prometheus, OpenTelemetry, alerting
- [Troubleshooting](troubleshooting.md) — Common issues and diagnostic steps
- [Benchmarks](BENCHMARK_RESULTS.md) — Performance baselines ([methodology](BENCHMARK_METHODOLOGY.md), [reproduction](BENCHMARK_REPRODUCTION.md))

## Runbooks

- [Upgrade & Rollback](runbooks/UPGRADE_AND_ROLLBACK.md) — Version upgrade procedures
- [Runbook Index](runbooks/README.md) — Incident response playbooks
