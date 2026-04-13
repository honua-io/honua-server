# Operator Guide

Deploy, configure, monitor, and manage Honua Server.

## Getting Started

- [Infrastructure & Deployment](infrastructure.md) — Docker Compose, Kubernetes, cloud deployment
- [Docker Compose Reference](docker-compose.md) — Local and evaluation setup
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md) — Patterns by team size
- [STAC Ops Demo](../../samples/Honua.StacOpsDemo/README.md) — One-click local sample for STAC health, extension drift, and cache-awareness review

## Configuration

- [Environment Variables](../../.env.example) — Complete configuration reference
- [Security](security.md) — Authentication, authorization, CORS, CSP
- [Feature Change Webhooks](feature-change-webhooks.md) — Event notification setup

## Database

- [Database Support Matrix](database-support-matrix.md) — Tested PostgreSQL/PostGIS versions, Aurora, Azure
- [DuckDB Provider](duckdb-provider.md) — Embedded read-only provider for analytics, GeoParquet, and edge deployments
- [TLS Connection Guide](tls-connection-guide.md) — SSL/TLS configuration for managed and self-hosted deployments

## Server Management

- [Control Plane API](CONTROL_PLANE_API.md) — Admin REST API for connections, layers, services, and migration inventory scans
- [GeoServer Migration Guide](../gis/tutorials/geoserver-migration-guide.md) — Discovery-only scanner workflow, compatibility review, and dry-run import planning
- [Tile Operations](tile-operations-runbook.md) — Vector tile seeding, warming, invalidation
- [Operations](operations.md) — Backups, migrations, connection pooling, query tuning, job orchestration, workspace lifecycle

## Monitoring

- [Monitoring & Observability](monitoring.md) — Health checks, Prometheus, OpenTelemetry, alerting
- [Troubleshooting](troubleshooting.md) — Common issues and diagnostic steps
- [Benchmarks](BENCHMARK_RESULTS.md) — Performance baselines ([methodology](BENCHMARK_METHODOLOGY.md), [reproduction](BENCHMARK_REPRODUCTION.md))

## Runbooks

- [Upgrade & Rollback](runbooks/UPGRADE_AND_ROLLBACK.md) — Version upgrade procedures
- [Runbook Index](runbooks/README.md) — Incident response playbooks
