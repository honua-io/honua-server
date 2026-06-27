# Databricks provider (read-only, best-effort)

The Databricks provider serves Honua feature layers from tables/views in a Databricks
SQL Warehouse. It plugs in alongside the primary backend (PostGIS, DuckDB, or MySQL)
through the shared feature-provider router: any layer whose backing `DataConnection`
resolves to the `databricks` provider is read through this implementation.

> **Status: best-effort, read-only.** Databricks has no first-class .NET ADO.NET
> driver, so this provider is an HTTP read-through adapter over the Databricks **SQL
> Statement Execution REST API**, closer in shape to the ArcGIS REST provider than to
> the in-process RDBMS providers. A hardening follow-up is expected. Read the
> [limitations](#limitations) before relying on it in production.

## How it works

Each Honua query is translated to Databricks SQL text and submitted to a SQL Warehouse
over HTTPS with bearer-token (PAT/OAuth) authentication:

1. **Submit** — `POST /api/2.0/sql/statements/` with the SQL statement, the warehouse
   id, any named parameters, `disposition=INLINE`, `format=JSON_ARRAY`, and a server-side
   `wait_timeout`.
2. **Poll** — when the statement is `PENDING`/`RUNNING`, poll
   `GET /api/2.0/sql/statements/{statement_id}` on the configured interval until the
   state is `SUCCEEDED` (or the command-timeout budget elapses).
3. **Page** — read the inline `result.data_array` rows and follow
   `result.next_chunk_internal_link` to collect additional chunks.

Geometry is requested as a WKB hex string via `hex(st_asbinary(<geometry-column>))` and
decoded to the canonical feature geometry without an external geometry library. Extent
queries use `st_xmin`/`st_ymin`/`st_xmax`/`st_ymax` aggregates. These `ST_*` functions
require a Databricks runtime / DBSQL with spatial-function support.

## Requirements

- A running **Databricks SQL Warehouse** and its warehouse id.
- A **personal access token** (PAT) or OAuth bearer token with read access to the
  target catalog/schema/tables.
- For geometry/extent: a Databricks runtime / DBSQL that exposes the spatial `ST_*`
  functions used above.

## Configuration

Bound from the `Databricks` configuration section (environment variables shown with the
`__` section separator):

| Key | Required | Description |
| --- | --- | --- |
| `Databricks__Enabled` | no (default `true`) | Set `false` to compile the provider in but skip its DI registration. |
| `Databricks__Host` | yes | Absolute HTTPS workspace URL, e.g. `https://dbc-abc123.cloud.databricks.com`. |
| `Databricks__WarehouseId` | yes | SQL Warehouse id statements execute against. |
| `Databricks__Token` | yes | PAT / OAuth bearer token. Prefer a secret reference. Sent only as an `Authorization: Bearer …` header — never in a URL. |
| `Databricks__Catalog` | no | Unity Catalog catalog used to qualify tables when a layer does not override it. |
| `Databricks__Schema` | no | Schema (database) used to qualify tables when a layer does not override it. |
| `Databricks__CommandTimeoutSeconds` | no (default `120`) | Overall submit + poll budget. |
| `Databricks__PollIntervalMilliseconds` | no (default `750`) | Delay between status polls. |
| `Databricks__MaxRetryAttempts` | no (default `3`) | Automatic retries per Statement Execution HTTP call (submit, poll, chunk) on transient failures (HTTP 408/429/5xx, transient network faults). Set `0` to disable. |
| `Databricks__RetryBaseDelayMilliseconds` | no (default `500`) | Base delay for the exponential backoff (with jitter) applied between retries. |

### Layer mappings

Databricks exposes no Honua-aware service-metadata endpoint, so layer mappings are
configured explicitly (schema introspection is not performed in this slice). Each entry
under `Databricks:Layers` maps a Honua layer id to a physical table:

```jsonc
{
  "Databricks": {
    "Host": "https://dbc-abc123.cloud.databricks.com",
    "WarehouseId": "1234567890abcdef",
    "Token": "dapi_...",
    "Catalog": "main",
    "Schema": "gis",
    "Layers": [
      {
        "Id": 1,
        "Name": "parcels",
        "Table": "parcels",
        "GeometryColumn": "geom",
        "PrimaryKeyColumn": "id",
        "Srid": 4326,
        "GeometryType": "Polygon",
        "Attributes": ["name", "owner", "area_sqm"]
      }
    ]
  }
}
```

`Catalog`/`Schema` may be overridden per layer. Table, geometry, primary-key, and
attribute identifiers are validated against a simple-identifier allow-list at startup.

## Capabilities

| Capability | Supported |
| --- | --- |
| Query (where, object-ids, out-fields, order-by, paging) | Yes |
| Count | Yes |
| Extent (envelope) | Yes (requires `ST_*`) |
| Envelope/bbox spatial filter | Yes (`st_intersects`, requires `ST_*`) |
| Attribute/spatial `where` (canonical filter translation) | Yes (parsed to the shared AST, translated to parameterized Spark SQL) |
| Statistics (`outStatistics` + `groupByFieldsForStatistics`) | Yes (COUNT/SUM/MIN/MAX/AVG/STDDEV/VAR) |
| Top-features / date-bins / bins / H3 | No |
| Native MVT / FlatGeobuf / Geobuf / GML | No (shared formatters handle output) |
| Streaming GeoJSON | No |
| Edits (create/update/delete/transactions) | No — provider is read-only |

## Limitations

- **No writes.** The provider exposes no `IFeatureWriter`; all edit paths are rejected.
- **No native .NET driver.** Communication is over the REST Statement Execution API, so
  every request incurs submit + poll latency rather than a persistent connection.
- **Transient failures are retried automatically.** Each outbound Statement Execution call
  (submit, status poll, and result-chunk fetch) is fronted by the shared HTTP resilience
  policy: transient responses (HTTP 408/429/5xx) and transient network faults are retried
  with exponential backoff + jitter (`Databricks__MaxRetryAttempts`,
  `Databricks__RetryBaseDelayMilliseconds`), and a per-provider circuit breaker isolates
  Databricks failures from other backends. Non-transient errors (e.g. HTTP 400) are not
  retried.
- **Spatial-function availability is environment-dependent.** `ST_AsBinary`,
  `ST_GeomFromText`, `ST_Intersects`, and the `ST_*min/max` aggregates require a
  Databricks runtime / DBSQL build that ships the spatial functions. On warehouses
  without them, geometry, extent, and spatial-filter queries will fail.
- **`WHERE` is re-parsed, not forwarded verbatim.** The GeoServices-style `where` clause is
  parsed into the shared filter AST and translated into parameterized Spark SQL
  (backtick-quoted identifiers, `:pN` bind markers). Literal operands are bound as
  parameters, and field names are validated against the configured columns, so the raw
  client string never reaches the warehouse SQL unparsed.
- **Statistics use the shared pipeline.** `outStatistics` (with optional
  `groupByFieldsForStatistics`) is translated to Spark aggregates (COUNT/SUM/MIN/MAX/AVG,
  plus `STDDEV_SAMP`/`VAR_SAMP`) honoring the same WHERE/spatial filters as the SELECT.
- **Unsupported query shapes fail loudly.** Enforced/security SQL filters, temporal filters,
  distance predicates, scalar SQL functions (EXTRACT/SUBSTRING/CAST, …), and non-envelope
  spatial filters throw `NotSupportedException` rather than silently returning over-broad
  results. A pre-translated (Postgres-flavored) `SqlFilter` without the canonical `where`
  text is likewise rejected.
- **No schema introspection.** Attribute columns must be listed explicitly per layer.
- **Deferred to the hardening follow-up (#1719):** Metadata-v2 binding
  (`IBindableFeatureDataProvider`) so layers resolve from secure connections rather than
  static config. Top-features, date/value bins, and H3 aggregation remain unsupported.

Provider selection variables are listed in the
[environment variable reference](../environment-variables.md#database-and-providers).
