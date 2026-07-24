# Serve existing databases

You'll have tables you already own served as live layers, without copying data, in about 10 minutes.

**Prerequisites:** A running server ([quickstart](../../get-started/quickstart.md)) and admin credentials ([authentication](../secure/authentication.md)).

Honua connects in place to existing databases. PostGIS is the full read/write backend managed through the admin API; the other providers are read/query-only and are configured in server settings.

## Choose a provider

| Provider | Access | How you register it | Notes |
| --- | --- | --- | --- |
| PostGIS | Read/write, full feature set (edits, statistics, native MVT, analytics) | Admin connections API (this guide) | Primary backend. |
| DuckDB | Read/query only | `DataSource:Provider=duckdb` + `DuckDB` config section | Embedded single file; layers declared in config; good for GeoParquet/analytical and air-gapped serving. See [DuckDB](../../reference/configuration/data-sources/duckdb.md). |
| SQL Server | Read/query only | `SqlServer` config section alongside the primary backend | `geometry`/`geography` tables, SQL Server 2016+/Azure SQL. No edits, statistics, or native MVT. See [SQL Server](../../reference/configuration/data-sources/sql-server.md). |
| Oracle | Read/query only | Oracle provider config alongside the primary backend | Standard `SDO_GEOMETRY` tables (12c+). ArcSDE `ST_Geometry` and versioned tables are detected and refused. See [Oracle](../../reference/configuration/data-sources/oracle.md). |
| MySQL / MariaDB | Read/query only | MySQL provider config alongside the primary backend | MySQL 8.0.11+ / MariaDB 10.6+. No edits, statistics, temporal filters, KNN, or cross-SRID transforms. See [MySQL/MariaDB](../../reference/configuration/data-sources/mysql-mariadb.md). |

For the read-only providers, follow the linked reference page to configure the provider section and restart the server — the rest of this guide covers the PostGIS admin-API path.

## Steps

### 1. Register a connection to your PostGIS database

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/connections` with this body:

```json
{
  "name": "city-gis",
  "host": "db.example.internal",
  "port": 5432,
  "databaseName": "citygis",
  "username": "honua_reader",
  "password": "db-password",
  "sslMode": "Require"
}
```

Credentials are encrypted at rest and never returned by the API. Use `secretReference` + `secretType` instead of `password` to pull credentials from a secret manager.

### 2. Test the connection

Run `POST /api/v1/admin/connections/{connectionId}/test`, substituting the id returned in step 1.

`POST /api/v1/admin/connections/test` tests a draft payload before saving it.

### 3. Discover tables

Run `GET /api/v1/admin/connections/{connectionId}/tables`.

Lists spatial tables with schema, geometry column, geometry type, and SRID. The `{id}` segment accepts the connection GUID or its name.

### 4. Publish a table as a layer

Run `POST /api/v1/admin/connections/{connectionId}/layers` with this body:

```json
{
  "schema": "public",
  "table": "parcels",
  "layerName": "city-parcels",
  "geometryColumn": "geom",
  "srid": 4326
}
```

Returns `201 Created` with the new `layerId`. See [Publish layers](publish-layers.md) for the full request options and protocol checks.

## Verify

Open `http://localhost:8080/ogc/features/collections` in a browser and confirm the collection is present:

```json
{"collections": [{"id": "…", "title": "city-parcels", …}], …}
```

## Troubleshoot

- **Connection test fails** — verify host/port reachability from the server container and that `sslMode` matches the database TLS setup (`Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`).
- **`Cannot specify both Password and SecretReference` (400)** — supply exactly one credential mechanism.
- **Table missing from discovery** — discovery lists tables with a registered geometry column; confirm the table has a typed geometry column and the connection user has SELECT on it.
- **Publish returns `409 Conflict`** — a layer with that name already exists in the target service; pick another `layerName`.
- **Read-only provider table cannot be edited** — DuckDB, SQL Server, Oracle, and MySQL providers reject edits by design; copy data into PostGIS for editable layers.

More help: [troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Publish layers](publish-layers.md) — the full publish workflow and verification across protocols.
- [Database support matrix](../../reference/configuration/data-sources/README.md) — tested engine versions and managed-Postgres setup.
- [Publish tiles](publish-tiles.md) — vector tiles and cache operations for published layers.
