# Operator Guide

Deploy, configure, monitor, and manage Honua Server.

## Getting Started

- [Infrastructure & Deployment](../../guides/deploy/kubernetes.md) — Docker Compose, Kubernetes, cloud deployment
- [Docker Compose Reference](../../guides/deploy/docker-compose.md) — Local and evaluation setup
- [Deployment Scenarios](../../guides/deploy/cloud-deployments.md) — Patterns by team size
- [STAC Ops Demo](../../../samples/Honua.StacOpsDemo/README.md) — One-click local sample for STAC health, extension drift, and cache-awareness review

## Configuration

- [Environment Variables](../../../.env.example) — Complete configuration reference
- [Security](../../guides/secure/authentication.md) — Authentication, authorization, CORS, CSP
- [Client Certificate Authentication](../../guides/secure/tls-and-mtls.md) — Native/admin mTLS modes, trust profiles, mappings, revocations, and response contracts
- [Compliance Framework](../../guides/secure/compliance.md) — SOC 2 / FedRAMP readiness evidence, data residency policy + dry-run, compliance key-version rotation, report export
- [Audit Coverage Matrix](../../internal/operator/audit-coverage-matrix.md) — Which operations emit audit events (admin actions, destructive writes, authentication/authorization) and where emission lives
- [HTTP Client Resilience](../../guides/deploy/scaling-and-performance.md) — Retry, circuit breaker, and timeout tuning for external services
- [Feature Change Webhooks](../../guides/edit/react-to-changes.md) — Event notification setup
- [Feature Streaming](../../guides/edit/react-to-changes.md) — WebSocket/SSE feature-change subscriptions

## Database

- [Database Support Matrix](../../reference/configuration/data-sources/README.md) — Tested PostgreSQL/PostGIS versions, Aurora, Azure
- [DuckDB Provider](../../reference/configuration/data-sources/duckdb.md) — Embedded read-only provider for analytics, GeoParquet, and edge deployments
- [SQL Server Provider](../../reference/configuration/data-sources/sql-server.md) — Read-only SQL Server (`geometry`/`geography`) provider for enterprise data sources
- [Oracle Provider](../../reference/configuration/data-sources/oracle.md) — Read-only Oracle Spatial (`SDO_GEOMETRY`) provider for enterprise-geodatabase data sources (ArcSDE `ST_Geometry` and versioned tables refused)
- [MySQL/MariaDB Provider](../../reference/configuration/data-sources/mysql-mariadb.md) — Read/query-only provider for MySQL 8.0.11+ and MariaDB 10.6+ tables
- [TLS Connection Guide](../../guides/secure/tls-and-mtls.md) — SSL/TLS configuration for managed and self-hosted deployments

## Server Management

- [Control Plane API](../../reference/admin-api/overview.md) — Admin REST API for connections, layers, services, and migration inventory scans
- [Console Job Observability](../../internal/admin-api/console-job-observability.md) — Durable job history, details, logs, artifacts, actions, cancellation, retry, and Operate event correlation for Console job viewers
- [Migration Toolkit](../../guides/migrate/from-arcgis-server.md) — Inventory, manifest, parity evidence, and cutover readiness artifact workflow
- [ArcGIS Inventory Discovery](../../guides/migrate/from-arcgis-server.md) — Deterministic FeatureServer/MapServer inventory artifact, JSON export, and compatibility codes
- [GeoServer Migration Guide](../../guides/migrate/from-geoserver.md) — Discovery scanner workflow, compatibility review, dry-run validation, and bounded catalog apply
- [Tile Operations](../../guides/publish/publish-tiles.md) — Vector tile seeding, warming, invalidation, archive
- [PMTiles Publishing](../../guides/publish/publish-tiles.md) — Durable PMTiles artifacts for MapLibre/PMTiles browser clients
- [Operations](../../guides/deploy/backup-and-restore.md) — Backups, migrations, connection pooling, query tuning, job orchestration, workflow orchestration, workspace lifecycle

## Monitoring

- [Monitoring & Observability](../../guides/deploy/monitoring.md) — Health checks, Prometheus, OpenTelemetry, alerting
- [Troubleshooting](../../guides/deploy/troubleshooting.md) — Common issues and diagnostic steps

## Runbooks

- [Upgrade & Rollback](../../guides/deploy/upgrade-and-rollback.md) — Version upgrade procedures
- [Runbook Index](operator-runbooks-README.md) — Incident response playbooks
