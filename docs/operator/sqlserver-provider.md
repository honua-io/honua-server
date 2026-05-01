# SQL Server Spatial Provider

Honua exposes SQL Server (`geometry` and `geography`) tables as read-only feature layers
through the shared `IFeatureDataProvider` seam. SQL Server is the Tier 1 enterprise backend
for organizations standardized on Microsoft data platforms.

This page describes the **read/query thin slice** delivered under issue
[#850](https://github.com/honua-io/honua-server/issues/850). Edits, native MVT, native
FlatGeobuf/Geobuf/GML, statistics aggregates, and admin UI integration are deliberately
out of scope and will land as separate slices under epic
[#362](https://github.com/honua-io/honua-server/issues/362).

## Supported Versions

| SQL Server | Status | Notes |
|---|---|---|
| 2016 and newer | Supported | Reference target. Uses `geometry::EnvelopeAggregate` / `geography::EnvelopeAggregate` for extent. |
| 2012, 2014 | Best-effort | `EnvelopeAggregate` is available since 2012, but the provider has not been validated on these versions. File a bug if you observe a regression. |
| 2008 R2 and earlier | Not supported | Missing required spatial functions. |
| Azure SQL Database / Managed Instance | Supported | Treat as 2016+ in this matrix. |

The provider does **not** require any feature flag at the database level beyond the
default spatial features that ship with SQL Server.

## When to Use

- **Tier 1 enterprise integrations** where SQL Server already holds authoritative spatial data.
- **Read-only publication** of existing spatial tables/views without copying data into PostGIS.
- **Cloud-hosted SQL Server** (Azure SQL DB, Managed Instance, RDS for SQL Server, GCP Cloud SQL).

The provider is **not** suitable for:

- Editable feature layers (use the PostgreSQL provider).
- Native MVT, FlatGeobuf, Geobuf, or GML emission (the in-process formatter handles these).
- Statistics, top-features, date bins, value bins, H3 aggregations, temporal extents,
  spatial analytics, or KNN/distance filters — these throw `NotSupportedException` and are
  reported as unsupported in `FeatureProviderCapabilities`.

## Configuration

Register the provider as an additional read backend by enabling the `SqlServer` section
in your `appsettings.json`. The provider plugs in alongside the primary backend (PostGIS or
DuckDB) and is selected per-layer based on the layer's `DataConnection` provider name.

```json
{
  "DataSource": { "Provider": "postgres" },
  "SqlServer": {
    "Enabled": true,
    "ConnectionString": "Server=mssql.example.com,1433;Database=geo;User Id=honua;Password=...;Encrypt=True;TrustServerCertificate=False",
    "CommandTimeoutSeconds": 60
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `SqlServer:Enabled` | `true` | Set to `false` to skip provider registration even if the assembly is referenced. |
| `SqlServer:ConnectionString` | _none_ | Default connection string used when a layer's secure connection is unavailable. Prefer secret-store references in production. |
| `SqlServer:CommandTimeoutSeconds` | `60` | Per-command timeout in seconds. Must be positive. |

Connection pooling is handled by `Microsoft.Data.SqlClient` internally; the provider opens
a fresh `SqlConnection` per operation and lets the client library pool transparently.

### Per-Layer Storage Mapping

Layers that should be served from SQL Server use the existing `LayerStorageMapping` model
plus a `DataConnection` whose provider name resolves to `sqlserver` (alias `mssql`).

```json
{
  "Layers": [
    {
      "Id": 100,
      "Name": "Parcels",
      "GeometryType": "Polygon",
      "StorageMapping": {
        "TableName": "Parcels",
        "SchemaName": "geo",
        "PrimaryKeyColumn": "ObjectID",
        "GeometryColumn": "Shape",
        "StorageSrid": 4326,
        "ProviderOptions": {
          "geometryType": "geometry"
        }
      }
    }
  ]
}
```

`ProviderOptions["geometryType"]` accepts:

- `"geometry"` (default) — planar SQL Server `geometry` column. Distances are in CRS units.
- `"geography"` — geodetic SQL Server `geography` column. Distances are in meters on the
  reference spheroid; lat/lon datasets should explicitly opt in to this mode.

All identifiers configured on a layer (`TableName`, `SchemaName`, `CatalogName`,
`PrimaryKeyColumn`, `GeometryColumn`, plus any field referenced in WHERE/`OutFields`/
`OrderBy`) must match the regular SQL identifier pattern `[A-Za-z_][A-Za-z0-9_]*`. The
provider rejects any input that fails the allow-list and emits a problem-details response
through the shared exception pipeline.

## Supported Operations

| Operation | Status | Implementation |
|---|---|---|
| `IFeatureReader.GetAsync` | Supported | SELECT primary key + geometry (WKB) + attributes. |
| `IFeatureReader.QueryAsync` | Supported | SELECT with attribute/`ObjectIds`/spatial filters and OFFSET/FETCH paging. |
| `IFeatureReader.QueryObjectIdsAsync` | Supported | SELECT primary key only. |
| `IFeatureReader.CountAsync` | Supported | `SELECT COUNT_BIG(*)`. |
| `IFeatureReader.GetExtentAsync` | Supported | `geometry::EnvelopeAggregate` / `geography::EnvelopeAggregate`. |
| `IFeatureReader.GetEstimatesAsync` | Supported | Combines `CountAsync` + `GetExtentAsync`. |
| `IFeatureReader.QueryFlatGeobufAsync` | Returns `null` (fallback) | Server falls back to the in-process formatter. |
| `IFeatureReader.QueryStatisticsAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.GetTemporalExtentAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.QueryTopFeaturesAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.QueryDateBinsAsync` / `QueryBinsAsync` / `QueryH3Async` | Not supported | Throws `NotSupportedException`. |
| Edits (Create/Update/Delete) | Not supported | `IFeatureDataProvider.Writer` is `null`. |

### Spatial Filters

| `SpatialRelationship` | Translation (geometry) | Translation (geography) |
|---|---|---|
| `Intersects` | `geom.STIntersects(@filter) = 1` | `geom.STIntersects(@filter) = 1` |
| `EnvelopeIntersects` | `geom.STEnvelope().STIntersects(@filter.STEnvelope()) = 1` | **Not supported** — `STEnvelope` is geometry-only. Use `Intersects` instead, or convert the layer to `geometry`. |
| `Within` | `geom.STWithin(@filter) = 1` | `geom.STWithin(@filter) = 1` |
| `Contains` | `geom.STContains(@filter) = 1` | `geom.STContains(@filter) = 1` |
| `Disjoint` | `geom.STDisjoint(@filter) = 1` | `geom.STDisjoint(@filter) = 1` |
| `Crosses`, `Touches`, `Overlaps`, `Equals`, `WithinDistance`, `BeyondDistance`, `NearestNeighbor` | Not supported in this slice — request a follow-up if needed. | _(same)_ |

Filter geometries are parsed with `geometry::STGeomFromWKB(@wkb, @srid)` (or
`geography::STGeomFromWKB`). When the spatial filter does not specify an SRID, the
provider falls back to the layer's `StorageSrid` so the comparison is in a single CRS.

`GetExtentAsync` uses `geometry::EnvelopeAggregate` for planar layers and
`geography::EnvelopeAggregate` for geodetic layers. Corner extraction reads
`STPointN(n).STX` / `.STY` for `geometry` and `STPointN(n).Long` / `.Lat` for `geography`,
since SQL Server exposes a different point-coordinate API per spatial type.

### WHERE Clause

Simple WHERE expressions are parsed and parameterized in-process. Supported tokens:

- `field = literal`, `field <> literal`, `field != literal`
- `field <`, `<=`, `>`, `>=` `literal`
- `field LIKE 'pattern'`, `field NOT LIKE 'pattern'`
- `field IS [NOT] NULL`
- Any number of the above joined with `AND`

Anything outside this grammar is rejected with `ArgumentException`; full-CQL2 / OData /
ArcGIS expression translation is not part of this slice.

When a request supplies both `Where` and a translated `SqlFilter`, the provider always
re-parses the canonical `Where` text with its own SQL Server parser and ignores the
`SqlFilter`. The shared `ISqlFilterTranslator` pipeline currently only registers a
PostgreSQL translator, so `SqlFilter` fragments cannot be assumed to be valid T-SQL.

### Pagination

Pagination uses `OFFSET … ROWS FETCH NEXT … ROWS ONLY` (T-SQL standard since SQL Server
2012). When no `OrderBy` is provided, the provider falls back to ordering by the layer's
configured primary key column so paging is deterministic across requests. To detect
`HasMoreResults` without a separate `COUNT` round-trip, the provider over-fetches one
extra row beyond the requested limit, trims it, and reports `HasMoreResults` based on
whether the probe row arrived. `TotalCount` reflects the size of the returned page only;
callers that need the absolute total should use `CountAsync`.

## Observability

- Activity source: `Honua.SqlServer.FeatureStore` — emits spans named
  `sqlserver.feature.select`, `sqlserver.feature.count`, and `sqlserver.feature.extent`,
  each tagged with `layer.id`.
- Logging: source-generated structured events (`SqlServerFeatureLog`) emitted under the
  `Honua.SqlServer.Features.FeatureStore.Services.SqlServerFeatureDataAccess` category
  (event ids `7000` for prepared queries, `7001` for unsupported-operation rejections).
  The provider does not emit raw SQL exception messages or connection strings.

## Testing

### Unit Tests (always run in CI)

```bash
dotnet test tests/dotnet/Honua.SqlServer.Tests
```

Covers the SQL translation paths (SELECT, COUNT, EXTENT, ObjectIds, paging, attribute
filters, spatial filters, identifier validation) plus provider-resolution against
`FeatureProviderBindingResolver`. No SQL Server instance is required.

### Gated Integration Tests

The integration suite is skipped automatically in standard PR CI. To run it locally
against a SQL Server 2016+ instance:

```bash
export HONUA_SQLSERVER_TEST_CONNECTION="Server=localhost,1433;Database=tempdb;User Id=sa;Password=Strong!Pass;Encrypt=False"
dotnet test tests/dotnet/Honua.SqlServer.Tests --filter Category=SqlServerIntegration
```

The test fixture creates and drops a temporary table named
`honua_sqlserver_test_<guid>` in the configured database. The test user must therefore
have `CREATE TABLE` and `DROP TABLE` permission in the target database (a scratch
database such as `tempdb` is recommended).

## Limitations and Known Gaps

- **Read-only.** Edits, transactions, and applyEdits are not implemented.
- **No native output formats.** MVT/FlatGeobuf/Geobuf/GML are produced by the shared
  in-process formatters using the WKB returned by SELECT.
- **No reprojection on output.** `OutputSrid` is ignored; results are returned in the
  layer's storage SRID.
- **No ad-hoc `bbox`-only fast paths.** The provider does not currently use
  `STEnvelope` indexing tricks beyond the explicit `EnvelopeIntersects` filter.
- **WHERE grammar is intentionally narrow.** Use the canonical filter pipeline
  (`SqlFragment`/`Filter`) for complex predicates rather than free-form SQL.

Follow-ups for write support, admin UI wiring, native output formats, statistics, and
temporal/H3 aggregations are tracked under epic
[#362](https://github.com/honua-io/honua-server/issues/362).
