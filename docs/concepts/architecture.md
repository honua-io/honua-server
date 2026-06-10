# Architecture

Honua is a geospatial server that publishes, queries, edits, and renders spatial data through standard protocols. It ships as a single container running one ASP.NET Core (.NET 10) process. There is no site model, no separate tile server, and no required sidecar: one process serves every protocol, the admin API, and the web endpoints.

## One process, two ports

| Port | Transport | Serves |
|---|---|---|
| `8080` | HTTP/1.1 (+ gRPC-Web) | All REST protocols, OGC services, admin API, health checks |
| `8081` | HTTP/2 cleartext (h2c) | Native gRPC (`geospatial.v1.*`) for SDK and mobile clients |

Run a reverse proxy or load balancer in front for TLS. Health probes are `GET /healthz/live` and `GET /healthz/ready`.

## Data flow

```
 Clients                         Honua Server                    Storage
 -------                         ------------                    -------
 ArcGIS Pro / Esri SDKs ──┐   ┌─────────────────────┐   ┌─ PostGIS (read/write)
 QGIS / MapLibre / Cesium ┼──▶│ Protocol adapters    │──▶├─ DuckDB        (read-only)
 Excel / Power BI ────────┤   │   over one shared    │   ├─ SQL Server    (read-only)
 SDKs / gRPC / AI (MCP) ──┘   │   query/edit/render  │   ├─ Oracle        (read-only)
                              │   pipeline           │   └─ MySQL/MariaDB (read-only)
                              └──────────┬───────────┘
                                         ├─ Redis (optional: cache, durable jobs)
                                         └─ Local / S3 / Azure Blob file storage
```

Every protocol endpoint is a thin adapter over the same canonical query, edit, metadata, and rendering pipeline. Publish a layer once and it is served simultaneously through every protocol its service enables — see [Data model](data-model.md) and the [protocol matrix](protocols.md).

## Storage

**PostGIS is the primary store.** It is the only provider with full read/write support: editing, attachments, replicas, versioning, raster mosaics, and native vector tile generation all run against PostgreSQL/PostGIS. Migrations run automatically on startup.

**Read-only providers** serve existing databases in place, without copying data into PostGIS:

| Provider | Source | Typical use |
|---|---|---|
| DuckDB | Embedded `.duckdb` file | Analytical or reference data without external database infrastructure |
| SQL Server | `geometry`/`geography` tables | Serve enterprise SQL Server spatial tables as-is |
| Oracle | `SDO_GEOMETRY` tables | Serve standard Oracle Spatial tables (ArcSDE `ST_Geometry` and versioned tables are refused) |
| MySQL/MariaDB | User-managed spatial tables (MySQL 8.0.11+ / MariaDB 10.6+) | Serve existing MySQL spatial data |

Read-only providers support query, count, extent, and pagination; they report unsupported capabilities (edits, statistics, native MVT) honestly rather than emulating them. Per-provider details and limits are in the [data source configuration reference](../reference/configuration/data-sources/README.md).

## Optional infrastructure

- **Redis** — distributed caching for multi-node deployments, and the durable store for background jobs and workflow orchestration. Without Redis, caching falls back to in-memory and durable job/workflow endpoints report unavailable.
- **File storage** — attachments, imports, and raster assets use the local filesystem by default; S3-compatible storage (including MinIO) and Azure Blob Storage are configurable alternatives (`FileStorage__Provider`).

## Scaling

Server instances are stateless: catalog state lives in PostGIS, shared cache and job state in Redis, and files in the configured file store. To scale, run more containers behind a load balancer and point them at the same PostgreSQL, Redis, and file storage. The same image runs single-node evaluation stacks and multi-node production deployments.

## Observability and security

- Structured JSON logging (Serilog), OpenTelemetry tracing, and a Prometheus metrics endpoint.
- Authentication: API key, OIDC (license-gated), ArcGIS portal token compatibility (`/sharing/rest/generateToken`), and optional mTLS client certificates.
- Authorization: per-service and per-layer access policies with role-based control, enforced in the shared pipeline for every protocol.

See [Authentication](../guides/secure/authentication.md) and [TLS and mTLS](../guides/secure/tls-and-mtls.md).

## Where to go next

- [Quickstart](../get-started/quickstart.md) — run the server with Docker Compose
- [Protocols](protocols.md) — the full protocol-to-endpoint matrix
- [Data model](data-model.md) — connections, layers, services, and styles
- [Docker Compose deployment](../guides/deploy/docker-compose.md) and [Kubernetes](../guides/deploy/kubernetes.md)
- [Operations](../guides/deploy/backup-and-restore.md) — jobs, workflows, monitoring
