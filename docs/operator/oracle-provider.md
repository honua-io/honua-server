# Oracle Spatial Provider

Honua exposes standard Oracle Spatial (`SDO_GEOMETRY`) tables as read-only feature layers
through the shared `IFeatureDataProvider` seam. Oracle is the dominant enterprise-geodatabase
backend and the most-requested connect-in-place target after PostGIS / SQL Server / MySQL.

This page describes the **read/query thin slice** delivered under issue
[#1252](https://github.com/honua-io/honua-server/issues/1252). Edits, native MVT, native
FlatGeobuf/Geobuf/GML, statistics aggregates, and ArcSDE proprietary formats are deliberately
out of scope.

## Supported Sources

| Source | Status | Notes |
|---|---|---|
| Oracle Spatial `SDO_GEOMETRY` tables | Supported | Reference target. Standard 2D `SDO_GEOMETRY` columns; `SDO_UTIL.TO_WKBGEOMETRY` returns 2D WKB and any Z/M ordinates are dropped. |
| Oracle Locator (subset of Spatial) | Supported | Same code path; functions used here (`SDO_RELATE`, `SDO_AGGR_MBR`, `SDO_UTIL.*`) are part of Locator. |
| ArcSDE `ST_Geometry` (binary or non-`SDO_GEOMETRY`) | **Refused** | The provider detects non-`SDO_GEOMETRY` column types at first execution and throws `NotSupportedException`. ArcSDE bytes are never decoded. |
| ArcSDE versioned tables (`GDB_FROM_DATE`, `GDB_TO_DATE`, `SDE_STATE_ID`) | **Refused** | Detected from `ALL_TAB_COLUMNS` and rejected with `NotSupportedException`. |

The provider does **not** require any Oracle option beyond Oracle Spatial (or Locator) and
the `ALL_TAB_COLUMNS` SELECT privilege used by the SDO/versioning detection guard.

## Required Oracle Versions

| Oracle | Status | Notes |
|---|---|---|
| 12c and newer | Supported | `OFFSET … FETCH NEXT … ROWS ONLY` pagination requires 12c. |
| 11g R1 | Best-effort | `SDO_UTIL.FROM_WKBGEOMETRY` is available from 11g R1, but pagination falls outside the standard syntax. File a bug if needed. |
| 11g R2 and earlier without `OFFSET/FETCH` | Not supported | The provider emits ANSI pagination; backport requests can fall back to a `ROWNUM` rewrite in a future slice. |

## When to Use

- **Enterprise geodatabase serving** where Oracle holds authoritative `SDO_GEOMETRY` spatial data.
- **Connect-in-place** read publication of existing Oracle tables/views without copying data into PostGIS.
- **Co-existence with ArcSDE-managed schemas** where standard Oracle Spatial layers can be
  served safely while ArcSDE-versioned/`ST_Geometry` layers are intentionally excluded.

The provider is **not** suitable for:

- Editable feature layers (use the PostgreSQL provider).
- ArcSDE proprietary formats (`ST_Geometry`, SDE-binary) — these are refused by design
  for both functional and IP/clean-room compliance reasons.
- ArcSDE versioned tables — versioned-table delta reconciliation is out of scope.
- Native MVT, FlatGeobuf, Geobuf, or GML emission (the in-process formatter handles these).
- Statistics, top-features, date bins, value bins, H3 aggregations, temporal extents,
  KNN/distance filters — these throw `NotSupportedException`.

## Configuration

Register the provider as an additional read backend by enabling the `Oracle` section in your
`appsettings.json`. The provider plugs in alongside the primary backend (PostGIS, DuckDB,
MySQL, SQL Server) and is selected per-layer based on the layer's `DataConnection` provider
name.

```json
{
  "DataSource": { "Provider": "postgres" },
  "Oracle": {
    "Enabled": true,
    "ConnectionString": "User Id=honua;Password=...;Data Source=oracle.example.com:1521/ORCL",
    "CommandTimeoutSeconds": 60
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Oracle:Enabled` | `true` | Set to `false` to skip provider registration even if the assembly is referenced. Disable for Native AOT publishing profiles. |
| `Oracle:ConnectionString` | _none_ | Default connection string used when a layer's secure connection is unavailable. Prefer secret-store references in production. |
| `Oracle:CommandTimeoutSeconds` | `60` | Per-command timeout in seconds. Must be positive. |

Connection pooling is handled by `Oracle.ManagedDataAccess.Core` (ODP.NET) internally; the
provider opens a fresh `OracleConnection` per operation and lets the driver pool transparently.

### Native AOT note

`Oracle.ManagedDataAccess.Core` uses internal reflection and is **not** Native AOT-compatible.
Deployments published with Native AOT must set `Oracle:Enabled=false` (or otherwise exclude
the assembly). Other relational providers (`Microsoft.Data.SqlClient`, `MySqlConnector`,
`Npgsql`) carry partial trim/AOT limitations as well; this is the most restrictive of the four.

### Per-Layer Storage Mapping

Layers that should be served from Oracle use the existing `LayerStorageMapping` model plus a
`DataConnection` whose provider name resolves to `oracle` (alias `oracledb`).

```json
{
  "Layers": [
    {
      "Id": 100,
      "Name": "Parcels",
      "GeometryType": "Polygon",
      "StorageMapping": {
        "TableName": "PARCELS",
        "SchemaName": "GIS",
        "PrimaryKeyColumn": "OBJECTID",
        "GeometryColumn": "SHAPE",
        "StorageSrid": 4326
      }
    }
  ]
}
```

All identifiers configured on a layer (`TableName`, `SchemaName`, `PrimaryKeyColumn`,
`GeometryColumn`, plus any field referenced in `Where`/`OutFields`/`OrderBy`) must match the
regular identifier pattern `[A-Za-z_][A-Za-z0-9_]*`. Configured names are double-quoted in
emitted SQL, so they are case-preserving (configure them exactly as they exist in Oracle).
The provider rejects any input that fails the allow-list and emits a problem-details response
through the shared exception pipeline.

## ArcSDE / Versioning Detection

At first query execution per binding, the provider probes Oracle's `ALL_TAB_COLUMNS` catalog
once and caches the result:

1. **Column type check.** Reads `DATA_TYPE` for the configured geometry column. Anything
   other than `SDO_GEOMETRY` (BLOB, `ST_GEOMETRY`, etc.) causes the read to throw
   `NotSupportedException` before any geometry bytes are fetched.
2. **Versioning column check.** Looks for `GDB_FROM_DATE`, `GDB_TO_DATE`, or `SDE_STATE_ID`
   on the same table. Any match causes the read to throw `NotSupportedException` naming the
   detected columns.

The guard is the IP / clean-room enforcement point: ArcSDE proprietary formats must never be
decoded. The cache key is the storage layer id; there is no TTL because schema metadata is
stable for the lifetime of a deployment. The connecting user must have SELECT on
`ALL_TAB_COLUMNS` (granted by default).

## Supported Operations

| Operation | Status | Implementation |
|---|---|---|
| `IFeatureReader.GetAsync` | Supported | SELECT primary key + geometry (WKB) + attributes. |
| `IFeatureReader.QueryAsync` | Supported | SELECT with attribute/`ObjectIds`/spatial filters and OFFSET/FETCH paging. |
| `IFeatureReader.QueryObjectIdsAsync` | Supported | SELECT primary key only. |
| `IFeatureReader.CountAsync` | Supported | `SELECT COUNT(*)`. |
| `IFeatureReader.GetExtentAsync` | Supported | `SDO_AGGR_MBR` with `SDO_ORDINATES` extraction. |
| `IFeatureReader.GetEstimatesAsync` | Supported | Combines `CountAsync` + `GetExtentAsync`. |
| `IFeatureReader.QueryFlatGeobufAsync` | Returns `null` (fallback) | Server falls back to the in-process formatter. |
| `IFeatureReader.QueryStatisticsAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.GetTemporalExtentAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.QueryTopFeaturesAsync` | Not supported | Throws `NotSupportedException`. |
| `IFeatureReader.QueryDateBinsAsync` / `QueryBinsAsync` / `QueryH3Async` | Not supported | Throws `NotSupportedException`. |
| Edits (Create/Update/Delete) | Not supported | `IFeatureDataProvider.Writer` is `null`. |

### Spatial Filters

| `SpatialRelationship` | Translation |
|---|---|
| `Intersects` | `SDO_RELATE(geom, SDO_UTIL.FROM_WKBGEOMETRY(:p), 'mask=ANYINTERACT') = 'TRUE'` |
| `EnvelopeIntersects` | `SDO_RELATE(SDO_GEOM.SDO_MBR(geom), SDO_UTIL.FROM_WKBGEOMETRY(:p), 'mask=ANYINTERACT') = 'TRUE'` |
| `Within` | `SDO_RELATE(geom, SDO_UTIL.FROM_WKBGEOMETRY(:p), 'mask=INSIDE+COVEREDBY') = 'TRUE'` |
| `Contains` | `SDO_RELATE(geom, SDO_UTIL.FROM_WKBGEOMETRY(:p), 'mask=CONTAINS+COVERS') = 'TRUE'` |
| `Disjoint` | `NOT (SDO_RELATE(geom, SDO_UTIL.FROM_WKBGEOMETRY(:p), 'mask=ANYINTERACT') = 'TRUE')` |
| `Crosses`, `Touches`, `Overlaps`, `Equals`, `WithinDistance`, `BeyondDistance`, `NearestNeighbor` | Not supported in this slice — request a follow-up if needed. |

Filter geometries are constructed in-database with `SDO_UTIL.FROM_WKBGEOMETRY(:p)`. The
spatial filter's SRID is not injected; Oracle uses the SRID stored in the geometry-column
metadata and spatial index. `SDO_RELATE` requires a spatial index on the geometry column —
configure one in Oracle before publishing the layer.

`GetExtentAsync` uses `SDO_AGGR_MBR` to compute a layer-wide MBR and reads the
`SDO_ORDINATES` varray (`(minX, minY, maxX, maxY)`) directly.

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
re-parses the canonical `Where` text with its own Oracle parser and ignores the
`SqlFilter`. The shared `ISqlFilterTranslator` pipeline currently registers a PostgreSQL
translator only, so `SqlFilter` fragments cannot be assumed to be valid Oracle SQL.

### Pagination

Pagination uses `OFFSET … ROWS FETCH NEXT … ROWS ONLY` (Oracle 12c+). When no `OrderBy` is
provided, the provider falls back to ordering by the layer's configured primary key column so
paging is deterministic across requests. To detect `HasMoreResults` without a separate
`COUNT` round-trip, the provider over-fetches one extra row beyond the requested limit,
trims it, and reports `HasMoreResults` based on whether the probe row arrived. `TotalCount`
reflects the size of the returned page only; callers that need the absolute total should use
`CountAsync`.

## Observability

- Activity source: `Honua.Oracle.FeatureStore` — emits spans named
  `oracle.feature.select`, `oracle.feature.count`, `oracle.feature.extent`, and
  `oracle.feature.objectids`, each tagged with `db.system=oracle`, `db.operation=<op>`,
  and `layer.id=<id>`.
- Logging: source-generated structured events (`OracleFeatureLog`) emitted under the
  `Honua.Oracle.Features.FeatureStore.Services.OracleFeatureDataAccess` category and the
  `Honua.Oracle.Features.FeatureStore.Services.OracleSpatialGuard` category. Event ids:
  `7100` (prepared queries), `7101` (unsupported-operation rejection), `7102` (non-SDO
  surface rejection), `7103` (versioned-table rejection). The provider does not emit raw
  Oracle exception messages or connection strings.

## Testing

### Unit Tests (always run in CI)

```bash
dotnet test tests/dotnet/Honua.Oracle.Tests
```

Covers the SQL translation paths (SELECT, COUNT, EXTENT, ObjectIds, paging, attribute
filters, spatial filters, identifier validation), provider-resolution through the shared
`FeatureProviderQueryRouter`, and the spatial-guard's refusal of non-`SDO_GEOMETRY` columns
and versioning columns. No Oracle instance is required.

### Integration

There is no live-Oracle integration suite in this slice — Oracle Free 23ai container
images are large and Oracle licensing for CI is treated as a separate decision. Operators
should run the read paths against a representative database before promoting the
configuration.

## Limitations and Known Gaps

- **Read-only.** Edits, transactions, and `applyEdits` are not implemented.
- **No write-through to enterprise geodatabases.** Out of scope (functional and IP).
- **No ArcSDE format support.** `ST_Geometry`, SDE-binary, and versioned tables are
  detected and refused, never parsed.
- **2D only.** `SDO_UTIL.TO_WKBGEOMETRY` returns 2D WKB; Z/M ordinates on 3D/4D geometries
  are dropped. Matches the behavior of other relational providers.
- **No native output formats.** MVT/FlatGeobuf/Geobuf/GML are produced by the shared
  in-process formatters using the WKB returned by SELECT.
- **No reprojection on output.** `OutputSrid` is ignored; results are returned in the
  layer's storage SRID.
- **WHERE grammar is intentionally narrow.** Use the canonical filter pipeline
  (`SqlFragment`/`Filter`) for complex predicates rather than free-form SQL.
- **Native AOT incompatible.** ODP.NET reflection blocks Native AOT; disable the provider
  in AOT-published images.

Follow-ups for write support, admin UI wiring, native output formats, statistics, and
temporal/H3 aggregations are tracked alongside the other Tier 1 enterprise providers.
