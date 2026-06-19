# Snowflake provider

Honua exposes Snowflake native `GEOGRAPHY` and `GEOMETRY` tables as read-only feature layers
through the shared `IFeatureDataProvider` seam. Snowflake is a widely deployed cloud data
warehouse and a common connect-in-place target for analytical spatial data.

This page describes the **read/query thin slice** delivered under issue
[#1713](https://github.com/honua-io/honua-server/issues/1713). Edits, native MVT, native
FlatGeobuf/Geobuf/GML, statistics aggregates, top features, bins, and H3 aggregation are
deliberately out of scope.

Use the [PostGIS provider](README.md) for full read/write, edits, statistics, MVT, and
analytics. Use the [DuckDB provider](duckdb.md) for analytical Parquet workflows.

## Supported Sources

| Source | Status | Notes |
|---|---|---|
| Snowflake `GEOGRAPHY` columns | Supported | Default. Geodetic (WGS84) coordinates; distances/lengths computed on the sphere in meters. |
| Snowflake `GEOMETRY` columns | Supported | Planar/projected. Set `geometryType=geometry` in the storage mapping provider options. |

Geometry is read out as OGC 2D WKB via `ST_ASWKB`; any Z/M ordinates on the source geometry are
dropped (documented limitation matching the other relational providers). Filter geometries are
constructed in-database from WKB with `TO_GEOGRAPHY(?, TRUE)` / `TO_GEOMETRY(?, TRUE)` and
compared with Snowflake's `ST_*` relationship functions.

## Supported Operations

| Operation | Supported? |
|---|---|
| `QueryAsync` (feature read with attributes) | Yes |
| `CountAsync` | Yes |
| `GetExtentAsync` | Yes (`ST_XMIN`/`ST_YMIN`/`ST_XMAX`/`ST_YMAX` aggregates) |
| `QueryObjectIdsAsync` | Yes |
| `GetAsync` (single feature by id) | Yes |
| `GetEstimatesAsync` (count + extent) | Yes |
| Spatial filters: Intersects, Within, Contains, Disjoint, EnvelopeIntersects | Yes |
| Attribute `Where` (Snowflake-aware parser), `ObjectIds` | Yes |
| Pagination, `OrderBy` | Yes |
| Distance / nearest-neighbor (KNN) filters | **No** |
| Translated `SqlFilter` (CQL2/FES/OData `$filter`) | **No** (rejected; see below) |
| Cross-SRID filter geometry | **No** (rejected with `NotSupportedException`) |
| Edits (Create / Update / Delete) | **No** |
| Aggregate statistics, top features, bins, H3 | **No** |
| Temporal / `datetime` extent | **No** (rejected with `NotSupportedException`) |
| Native MVT, FlatGeobuf, Geobuf, GML, streaming GeoJSON | **No** |
| Replicas / change tracking / transactional outbox | **No** |

The provider raises `NotSupportedException` with a descriptive message for any unsupported
operation. Like the other read-only relational providers, the FeatureServer query handler maps
these client-driven capability/shape mismatches to **HTTP 400 Bad Request** rather than HTTP 500.

### Translated filters (`SqlFilter`)

The shared `ISqlFilterTranslator` pipeline currently registers only a PostgreSQL translator, whose
emitted SQL (JSONB `->>` operators, `::` casts, Postgres `ST_*` signatures) is not valid Snowflake
SQL. The provider therefore rejects a populated `FeatureQuery.SqlFilter` and instead re-parses the
canonical `Where` text (FeatureServer `where`) with its own Snowflake-aware parser. This matches the
SQL Server and Oracle providers. Restrict CQL2/FES/OData `$filter` usage to providers that register
their own dialect translator.

## Configuration

Set `DataSource:Provider` to your primary backend (for example `postgres`) and add a `Snowflake`
section to `appsettings.json`. The Snowflake provider is registered as a **secondary** read-only
backend; layers are routed to it by their secure connection's provider name, not by
`DataSource:Provider`.

```json
{
  "DataSource": { "Provider": "postgres" },
  "Snowflake": {
    "Enabled": true,
    "ConnectionString": "account=xy12345.us-east-1;user=honua;password=...;db=ANALYTICS;schema=PUBLIC;warehouse=COMPUTE_WH;role=ANALYST",
    "CommandTimeoutSeconds": 60
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Snowflake:Enabled` | `true` | Set to `false` to skip provider registration even if the assembly is referenced. Disable for Native AOT publishing profiles. |
| `Snowflake:ConnectionString` | _none_ | Default `Snowflake.Data` connection string used when a layer's secure connection is unavailable. Takes precedence over the discrete account/warehouse/database/schema/role fields. Prefer secret-store references in production. |
| `Snowflake:Account` | _none_ | Account identifier (for example `xy12345.us-east-1`). Documentation field used only when composing a connection string from discrete fields. |
| `Snowflake:User` | _none_ | Snowflake user name. Documentation field. |
| `Snowflake:Warehouse` | _none_ | Virtual warehouse used to run read queries. Documentation field. |
| `Snowflake:Database` | _none_ | Default database. Documentation field. |
| `Snowflake:Schema` | _none_ | Default schema. Documentation field. |
| `Snowflake:Role` | _none_ | Session role. Documentation field. |
| `Snowflake:CommandTimeoutSeconds` | `60` | Per-command timeout in seconds. Must be positive. |

Connection pooling is handled by `Snowflake.Data` internally; the provider opens a fresh
`SnowflakeDbConnection` per operation and lets the driver pool transparently.

The provider name resolves from `snowflake` (canonical) or the alias `snowflakedb`.

### Native AOT note

`Snowflake.Data` uses internal reflection and is **not** Native AOT-compatible. Deployments
published with Native AOT must set `Snowflake:Enabled=false` (or otherwise exclude the assembly).
The default Honua publish image references the provider; the AOT-verification publish drops the
`Honua.Snowflake` project (via `HonuaSkipSnowflakeForAotVerification=true`, which defines
`HONUA_SKIP_SNOWFLAKE`) so the non-single-file-safe driver is never linked into the AOT image. The
`slim` build profile also excludes Snowflake. Operators publishing trimmed/AOT artifacts should
leave the provider disabled.

### GEOGRAPHY vs GEOMETRY and SRID

Snowflake distinguishes geodetic `GEOGRAPHY` (WGS84, longitude/latitude on the sphere) from planar
`GEOMETRY` (projected, CRS units). The storage mapping provider option `geometryType` selects which
constructor (`TO_GEOGRAPHY` vs `TO_GEOMETRY`) the provider uses to materialize filter geometries:

```json
{
  "TableName": "PARCELS",
  "SchemaName": "PUBLIC",
  "DatabaseName": "ANALYTICS",
  "PrimaryKeyColumn": "OBJECTID",
  "GeometryColumn": "SHAPE",
  "StorageSrid": 4326,
  "ProviderOptions": { "geometryType": "geography" }
}
```

When `geometryType` is omitted the provider defaults to `geography` (Snowflake's flagship spatial
type). Snowflake `GEOGRAPHY` is fixed to WGS84 (SRID 4326); `GEOMETRY` carries an SRID. The
provider rejects cross-SRID spatial filters with a clear `NotSupportedException` rather than
silently returning an empty result set — pre-project the filter geometry to the layer SRID before
submitting the request.

### Identifier case sensitivity

Snowflake folds **unquoted** identifiers to upper case but treats **double-quoted** identifiers as
case-sensitive and exactly as written. The provider always double-quotes identifiers, so configured
names are case-preserving — configure them exactly as they exist in the Snowflake catalog.
Conventional unquoted objects (e.g. `CREATE TABLE parcels (...)`) are stored upper-case, so
configure them as `PARCELS`. Configured identifiers are restricted to the regular form
(`[A-Za-z_][A-Za-z0-9_]*`) so they are unambiguous and SQL-injection-safe.

## Known Limitations

- **Read-only.** No edits, transactional outbox, replicas, or change tracking.
- **No aggregates.** Statistics, top features, date/value bins, and H3 aggregation are not
  supported in this slice.
- **No distance/KNN filters.** Only Intersects/Within/Contains/Disjoint/EnvelopeIntersects map to
  `ST_*` predicates; distance and nearest-neighbor filters are rejected.
- **No translated `SqlFilter`.** Only the canonical `Where` text is parsed (Snowflake-aware
  parser); the Postgres-flavored `ISqlFilterTranslator` output is rejected.
- **Z/M dropped.** `ST_ASWKB` emits 2D WKB; higher dimensions are not surfaced.
- **No cross-SRID transform.** Filter geometries must already match the layer SRID.
- **Native AOT incompatible.** `Snowflake.Data` reflection blocks Native AOT; disable the provider
  in AOT-published images.

## Testing

Unit tests for the query builder, SQL dialect, and identifier quoting run in normal CI
(`tests/dotnet/Honua.Snowflake.Tests`). Integration tests against a live Snowflake account are
gated behind `HONUA_TEST_SNOWFLAKE=1` plus `HONUA_SNOWFLAKE_TEST_CONNECTION` and are tagged
`[Trait("Category", "Snowflake")]`; they no-op when those environment variables are absent.
