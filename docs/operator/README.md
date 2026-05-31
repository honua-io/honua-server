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
- [Client Certificate Authentication](client-certificate-authentication.md) — Native/admin mTLS modes, trust profiles, mappings, revocations, and response contracts
- [Compliance Framework](compliance-framework.md) — SOC 2 / FedRAMP readiness evidence, data residency policy + dry-run, compliance key-version rotation, report export
- [HTTP Client Resilience](http-client-resilience.md) — Retry, circuit breaker, and timeout tuning for external services
- [Feature Change Webhooks](feature-change-webhooks.md) — Event notification setup
- [Feature Streaming](feature-streaming.md) — WebSocket/SSE feature-change subscriptions

## Database

- [Database Support Matrix](database-support-matrix.md) — Tested PostgreSQL/PostGIS versions, Aurora, Azure
- [DuckDB Provider](duckdb-provider.md) — Embedded read-only provider for analytics, GeoParquet, and edge deployments
- [SQL Server Provider](sqlserver-provider.md) — Read-only SQL Server (`geometry`/`geography`) provider for enterprise data sources
- [Oracle Provider](oracle-provider.md) — Read-only Oracle Spatial (`SDO_GEOMETRY`) provider for enterprise-geodatabase data sources (ArcSDE `ST_Geometry` and versioned tables refused)
- [MySQL/MariaDB Provider](mysql-provider.md) — Read/query-only provider for MySQL 8.0.11+ and MariaDB 10.6+ tables
- [TLS Connection Guide](tls-connection-guide.md) — SSL/TLS configuration for managed and self-hosted deployments

## Server Management

- [Control Plane API](CONTROL_PLANE_API.md) — Admin REST API for connections, layers, services, and migration inventory scans
- [Console Job Observability](../admin-api/console-job-observability.md) — Durable job history, details, logs, artifacts, actions, cancellation, retry, and Operate event correlation for Console job viewers
- [Migration Toolkit](migration-toolkit.md) — Inventory, manifest, parity evidence, and cutover readiness artifact workflow
- [ArcGIS Inventory Discovery](arcgis-inventory-discovery.md) — Deterministic FeatureServer/MapServer inventory artifact, JSON export, and compatibility codes
- [GeoServer Migration Guide](../gis/tutorials/geoserver-migration-guide.md) — Discovery scanner workflow, compatibility review, dry-run validation, and bounded catalog apply
- [Tile Operations](tile-operations-runbook.md) — Vector tile seeding, warming, invalidation, archive
- [PMTiles Publishing](pmtiles-publishing.md) — Durable PMTiles artifacts for MapLibre/PMTiles browser clients
- [Operations](operations.md) — Backups, migrations, connection pooling, query tuning, job orchestration, workflow orchestration, workspace lifecycle

## Monitoring

- [Monitoring & Observability](monitoring.md) — Health checks, Prometheus, OpenTelemetry, alerting
- [Troubleshooting](troubleshooting.md) — Common issues and diagnostic steps

## Runbooks

- [Upgrade & Rollback](runbooks/UPGRADE_AND_ROLLBACK.md) — Version upgrade procedures
- [Runbook Index](runbooks/README.md) — Incident response playbooks
