# Database Support Matrix

Honua requires PostgreSQL with PostGIS. This page documents tested engine/extension versions and provider-specific guidance for managed Postgres deployments.

## Tested Configurations

| Provider | Engine Version | PostGIS Version | CI Status |
|----------|---------------|-----------------|-----------|
| Self-hosted | PostgreSQL 16.x | PostGIS 3.4 | Tested |
| Self-hosted | PostgreSQL 17.x | PostGIS 3.5 | Tested |
| Self-hosted | PostgreSQL 18.x | PostGIS 3.6 | Tested |
| AWS Aurora PostgreSQL | 16.x | PostGIS 3.4 | Tested (CI proxy) |
| Azure Database for PostgreSQL Flexible Server | 16.x, 17.x | PostGIS 3.5 | Tested (CI proxy) |

CI uses `postgis/postgis` Docker images as version-level proxies for managed service behavior. True managed-service validation requires deployment to actual Aurora/Azure instances.

## Required Extensions

| Extension | Required | Purpose |
|-----------|----------|---------|
| `postgis` | Yes | Spatial operations, geometry types, spatial indexing |
| `postgis_raster` | For raster features | Raster data storage and analysis |
| `pgcrypto` | Yes | Cryptographic functions for secure identifiers |
| `unaccent` | Yes | Search normalization (accent-insensitive text search) |

Honua validates PostGIS presence at startup and will fail fast in all non-Development environments (Staging, Production, etc.) if `postgis` is not installed. In Development mode the server logs a warning and continues. Missing `postgis_raster` produces a warning but does not block startup.

## Provider-Specific Setup

### AWS Aurora PostgreSQL

1. **Create a custom DB cluster parameter group** (or modify the default):
   - Set `shared_preload_libraries` to include `pg_stat_statements` (optional, for monitoring).
   - Aurora PostgreSQL bundles PostGIS; enable it via the parameter group.

2. **Enable extensions** on your database:
   ```sql
   CREATE EXTENSION IF NOT EXISTS postgis;
   CREATE EXTENSION IF NOT EXISTS postgis_raster;
   CREATE EXTENSION IF NOT EXISTS pgcrypto;
   CREATE EXTENSION IF NOT EXISTS unaccent;
   ```

3. **Connection pooling**: Aurora provides a built-in PgBouncer-compatible endpoint. Use the cluster writer endpoint for Honua's primary connection. Read replicas are not application-level load-balanced by Honua.

4. **Failover**: Honua expects a single primary endpoint. Aurora automatic failover updates the cluster DNS endpoint; no application-level routing changes are needed.

### Azure Database for PostgreSQL Flexible Server

1. **Enable extensions** via the Azure portal or CLI:
   - Navigate to **Server parameters** and set the `azure.extensions` parameter to include `POSTGIS`, `POSTGIS_RASTER`, `PGCRYPTO`, `UNACCENT`.
   - Apply the configuration change (may require a server restart).

2. **Create extensions** on your database:
   ```sql
   CREATE EXTENSION IF NOT EXISTS postgis;
   CREATE EXTENSION IF NOT EXISTS postgis_raster;
   CREATE EXTENSION IF NOT EXISTS pgcrypto;
   CREATE EXTENSION IF NOT EXISTS unaccent;
   ```

3. **Connection pooling**: Azure Flexible Server supports built-in PgBouncer. Enable it via the server **Networking** settings if connection pooling is needed. Honua works with both direct and pooled connections.

4. **High availability**: Configure zone-redundant HA in Azure. Honua connects to the primary endpoint; failover is transparent at the DNS level.

## Connection Guidance

- Honua expects a single primary (read-write) database connection.
- Read replicas are not utilized for query load-balancing at the application layer.
- Connection string format: `Host=<endpoint>;Port=5432;Database=<db>;Username=<user>;Password=<pass>`
- For TLS configuration, see [TLS Connection Guide](tls-connection-guide.md).

## Startup Validation

Honua performs a PostGIS preflight check at startup:

1. Queries `SELECT version()` for the engine version.
2. Queries `pg_extension` for installed extensions.
3. Logs engine and PostGIS versions for operator visibility.
4. **Non-Development environments**: Fails fast (CrashLoopBackOff) if PostGIS is missing.
5. **Development mode**: Logs a warning and continues.

The preflight result is available via the deploy preflight admin API (`GET /api/v1/admin/deploy/preflight?includeDiagnostics=true`) in the `databaseCompatibility` field.
