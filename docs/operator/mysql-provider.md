# MySQL / MariaDB Provider (Read/Query Slice)

Honua supports MySQL 8.0.11+ and MariaDB 10.6+ as a **read-only** spatial feature
provider. This is a thin slice intended for serving spatial data that already
lives in MySQL/MariaDB tables (Tier 2 enterprise backend under epic
[#362](https://github.com/honua-io/honua-server/issues/362)).

Use the [PostGIS provider](database-support-matrix.md) for full read/write,
edits, statistics, MVT, and analytics. Use the
[DuckDB provider](duckdb-provider.md) for analytical Parquet workflows.

## Supported Operations

| Operation                                    | Supported? |
|----------------------------------------------|-----------|
| `QueryAsync` (feature read with attributes)  | Yes       |
| `CountAsync`                                 | Yes       |
| `GetExtentAsync` (per-row `ST_Envelope`)     | Yes       |
| `QueryObjectIdsAsync`                        | Yes       |
| `GetAsync` (single feature by id)            | Yes       |
| `QueryPageAsync` (paged read without count)  | Yes       |
| Spatial filters: Intersects, Within, Contains, Crosses, Touches, Overlaps, Disjoint, Equals, EnvelopeIntersects | Yes |
| Distance filters (Point layers only)         | Yes (approximate, `ST_Distance_Sphere`) |
| Attribute `Where`, `SqlFilter`, `ObjectIds`  | Yes       |
| Pagination, `OrderBy`                        | Yes       |
| Edits (Create / Update / Delete)             | **No**    |
| Aggregate statistics, top features, bins     | **No**    |
| Native MVT, FlatGeobuf, Geobuf, GML          | **No**    |
| Streaming GeoJSON                            | **No**    |
| Nearest-neighbor / KNN                       | **No**    |
| Cross-SRID `ST_Transform` of filter geometry | **No**    |
| Replicas / change tracking                   | **No**    |

The capability set is published at
`Honua.Core.Features.FeatureStore.Domain.FeatureProviderCapabilities.ReadOnlyMySql`.
Callers must check `SupportsStatistics`, `SupportsNativeMvt`, etc. before
invoking those paths; the provider raises `NotSupportedException` with a
descriptive message for any unsupported operation.

## Version Floor

| Engine | Minimum Version | Notes |
|--------|-----------------|-------|
| MySQL  | 8.0.11          | Required for `ST_Distance_Sphere`, SRID-aware spatial functions |
| MariaDB | 10.6 LTS       | Spatial parity with MySQL 8.0 lineage |

MySQL 5.7 is **not** supported; spatial function semantics changed
significantly between 5.7 and 8.0 and the test matrix targets 8.0+.

## Configuration

Set `DataSource:Provider` to `mysql` (or its alias `mariadb`) and add a `MySql`
section to `appsettings.json`:

```json
{
  "DataSource": {
    "Provider": "mysql"
  },
  "MySql": {
    "ConnectionString": "Server=localhost;Database=honua;User=honua_ro;Password=${MYSQL_PASSWORD};SslMode=Required",
    "Layers": [
      {
        "Id": 1,
        "Name": "Parcels",
        "Description": "Parcel boundaries",
        "Schema": "honua",
        "Table": "parcels",
        "GeometryColumn": "geom",
        "PrimaryKeyColumn": "id",
        "Srid": 4326,
        "GeometryType": "Polygon",
        "Attributes": ["name", "area", "type"],
        "AttributeTypes": {
          "name": "VARCHAR",
          "area": "DOUBLE",
          "type": "VARCHAR"
        }
      }
    ],
    "Services": [
      {
        "Name": "ParcelService",
        "Description": "Parcel data service",
        "LayerIds": [1],
        "Capabilities": ["Query"],
        "EnabledProtocols": ["FeatureServer", "OgcFeatures", "Grpc"]
      }
    ]
  }
}
```

### Configuration Reference

| Setting              | Required | Default | Description |
|----------------------|----------|---------|-------------|
| `ConnectionString`   | Yes      | —       | MySqlConnector connection string. Prefer secret-backed configuration in production. |
| `Layers`             | Yes      | —       | At least one layer mapping. |

#### Layer settings

| Setting             | Required | Default | Description |
|---------------------|----------|---------|-------------|
| `Id`                | Yes      | —       | Positive integer Honua layer ID (must be unique). |
| `Name`              | Yes      | —       | Display name. |
| `Description`       | No       | `null`  | Catalog description. |
| `Table`             | Yes      | —       | MySQL/MariaDB table or view name. |
| `Schema`            | No       | `null`  | Optional database/schema qualifier. |
| `GeometryColumn`    | Yes      | `geom`  | Geometry column name. |
| `PrimaryKeyColumn`  | Yes      | `id`    | Primary key / object-id column. |
| `Srid`              | Yes      | `4326`  | Storage SRID; must match `ST_SRID(geom)`. |
| `GeometryType`      | No       | `Point` | Used for catalog metadata and to gate distance filters. |
| `Attributes`        | Yes      | `[]`    | Explicit attribute column list. **No schema introspection in this slice.** |
| `AttributeTypes`    | No       | `{}`    | Optional column-type hints for catalog field-type mapping. |

#### Service settings

| Setting             | Default | Description |
|---------------------|---------|-------------|
| `Name`              | —       | Service URL segment. |
| `Description`       | `null`  | Catalog description. |
| `LayerIds`          | `[]`    | Layer IDs in this service. |
| `Capabilities`      | `["Query"]` | `Create`, `Update`, `Delete`, and `Extract` are stripped at startup. |
| `EnabledProtocols`  | `null`  | Optional protocol filter (e.g. `["FeatureServer", "OgcFeatures"]`). |

## Schema Requirements

The provider expects user-managed tables with the following shape:

1. A primary-key column (BIGINT or INT, NOT NULL) named per
   `PrimaryKeyColumn`.
2. A geometry column with the configured `Srid` declared (MySQL 8.0+ supports
   per-column SRID in DDL, and MariaDB 10.6 reads SRID via `ST_SRID`).
3. Optional attribute columns listed under `Attributes`.

A `SPATIAL INDEX` on the geometry column is strongly recommended for
`MBRIntersects`-eligible filters (`Intersects`, `EnvelopeIntersects`). Spatial
indexes require `NOT NULL` on the indexed column. Without a spatial index the
provider falls back to a full table scan; this is functionally correct but
slow.

Example DDL:

```sql
CREATE TABLE parcels (
    id BIGINT PRIMARY KEY,
    geom POLYGON NOT NULL SRID 4326,
    name VARCHAR(64),
    area DOUBLE,
    type VARCHAR(32),
    SPATIAL INDEX(geom)
) ENGINE=InnoDB;
```

## Spatial Filter Mapping

The MySQL provider follows MySQL 8.0+ / MariaDB 10.6+ canonical spatial
function names. The same mapping is applied to `FeatureQuery.SpatialFilter`
and to filter expressions translated through `MySqlSqlFilterTranslator`.

| Honua relationship    | MySQL/MariaDB SQL |
|-----------------------|-------------------|
| `Intersects`          | `MBRIntersects(col, ST_GeomFromWKB(?, srid)) AND ST_Intersects(col, ST_GeomFromWKB(?, srid))` |
| `EnvelopeIntersects`  | `MBRIntersects(col, ST_GeomFromWKB(?, srid))` (index-only, no exact check) |
| `Within`              | `ST_Within(col, ST_GeomFromWKB(?, srid))` |
| `Contains`            | `ST_Contains(col, ST_GeomFromWKB(?, srid))` |
| `Crosses` / `Touches` / `Overlaps` / `Disjoint` / `Equals` | matching `ST_*` function |
| `WithinDistance` (Point only) | `ST_Distance_Sphere(col, ST_GeomFromWKB(?, srid)) <= ?` |
| `BeyondDistance` (Point only) | `ST_Distance_Sphere(col, ST_GeomFromWKB(?, srid)) > ?` |
| `NearestNeighbor`     | `NotSupportedException` — not implemented |

`ST_Distance_Sphere` is documented by both engines as a WGS84 spherical
**approximation**; the resulting distance differs from a true geodesic
calculation. For accurate geodesic distance use a PostGIS-backed layer.
Distance filters on non-`Point` layers raise `NotSupportedException`.

## SRID Handling

MySQL/MariaDB do not provide a portable `ST_Transform`. The provider
**rejects** spatial filters whose `Srid` differs from the layer SRID:

```
NotSupportedException: Cross-SRID spatial filters are not supported by the
MySQL/MariaDB provider (layer SRID is 4326, filter SRID is 3857). Pre-project
filter geometries to the layer SRID before querying.
```

Output SRID transforms (`FeatureQuery.OutputSrid`) are likewise rejected.
Callers must pre-project geometries before invoking the provider.

## Extent Performance Note

`GetExtentAsync` derives the layer bounding box via per-row `ST_Envelope` and
`ST_PointN(ST_ExteriorRing(...), N)` aggregation. MySQL 8.0+/MariaDB 10.6+
do not allow `ST_Envelope` on geographic SRSes directly, so the geometry is
retagged with `ST_SRID(geom, 0)` (Cartesian) before envelope extraction —
this preserves the underlying coordinates. The result is portable across
both engines but is **O(n)** on the table or filtered subset. For large
tables, cache the extent with the layer metadata; do not call
`GetExtentAsync` on every request.

## Identifier Quoting

All table, schema, primary-key, geometry-column, attribute-column, and
`OrderBy` field identifiers are backtick-quoted. Configuration is validated
at startup: any identifier containing characters outside `[a-zA-Z0-9_]` is
rejected with a descriptive message. Embedded backticks in catalog names
(rare) are doubled per MySQL convention.

## Connection Pooling

The provider builds a singleton `MySqlConnector.MySqlDataSource` from the
configured connection string. Configure pooling on the connection string
(`Pooling=true;MinimumPoolSize=...;MaximumPoolSize=...`); the provider does
not override these knobs.

## Telemetry

Each query opens a `mysql.<operation>` activity span on the
`Honua.MySql.FeatureDataAccess` `ActivitySource`, tagged with
`db.system=mysql`, `db.operation`, and `layer.id`. Failures set
`ActivityStatusCode.Error` with the underlying exception message and emit a
structured `MySqlLog.QueryFailed` event (EventId 8902).

## Testing

### Unit tests

`tests/dotnet/Honua.MySql.Tests` contains unit tests for SQL generation,
filter translation, registry behaviour, and provider resolution. They run on
every PR and require no external services.

```bash
dotnet test tests/dotnet/Honua.MySql.Tests/Honua.MySql.Tests.csproj \
    --filter "Category!=MySql"
```

### Integration tests (gated)

`MySqlFeatureStoreIntegrationTests` exercises the full stack against a MySQL
8 container provisioned by Testcontainers. They are tagged
`[Trait("Category", "MySql")]` and are **opt-in**:

```bash
# Requires Docker and the MySQL 8 image.
dotnet test tests/dotnet/Honua.MySql.Tests/Honua.MySql.Tests.csproj \
    --filter "Category=MySql"
```

The tests skip gracefully if Docker is unavailable. They are not part of the
default PR test suite.

### MariaDB compatibility

The integration suite uses MySQL 8 as the canonical engine. MariaDB 10.6 is
nominally supported via the same SQL surface; manual verification against a
MariaDB container is the recommended path until a second Testcontainers
fixture is added in a follow-on slice.

## Limitations Summary

- Read-only. No `applyEdits`, no replicas, no change tracking.
- No schema introspection — declare attribute columns in configuration.
- No native MVT, FlatGeobuf, Geobuf, GML; no streaming GeoJSON.
- No statistics, top-features, bins, H3, density, cluster, buffer-aggregate, or
  spatial-join paths.
- No KNN / nearest-neighbor.
- No `ST_Transform`; cross-SRID filters fail with a descriptive error.
- Distance filters require Point/MultiPoint geometry.
- Per-row extent is O(n); cache the result.

## Cloud-Hosted Deployment Notes

- **Amazon RDS for MySQL / MariaDB** — supported. Use SSL/TLS by setting
  `SslMode=Required` (or `VerifyCA`) in the connection string.
- **Amazon Aurora MySQL-compatible** — supported with the cluster writer
  endpoint. Aurora's serverless v2 mode works for read traffic.
- **Azure Database for MySQL Flexible Server** — supported. Enforce TLS via
  parameter `require_secure_transport` and use the public endpoint or
  Private Endpoint.
- **Google Cloud SQL for MySQL** — supported. Enable Private IP and use the
  Cloud SQL Auth Proxy for IAM-based authentication.
- **PlanetScale (Vitess)** — best-effort. PlanetScale's spatial support
  depends on the underlying MySQL major version; confirm 8.0.11+ before
  enabling.

In all hosted environments, prefer secret-backed configuration for
`ConnectionString` (e.g. `aws:secretsmanager:...`, `env:...`, Azure Key
Vault) and a dedicated read-only database role.
