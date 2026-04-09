# DuckDB Provider

Honua supports DuckDB as a read-only embedded feature provider. This is designed for analytical and
reference workloads where data is prepared offline (e.g. GeoParquet, CSV, or Shapefile imports into
DuckDB) and served as a static dataset.

## When to Use DuckDB

- **Reference layers** — static datasets (parcels, boundaries, zoning) that change infrequently
- **Analytical exports** — pre-computed analytics materialized into DuckDB tables
- **Edge / air-gapped deployments** — single-file database, no external database dependency
- **Prototyping** — quick setup without PostgreSQL infrastructure

DuckDB is **not suitable** for write-heavy or real-time workloads. For editable feature layers, use
the PostgreSQL provider.

## Configuration

Set `DataSource:Provider` to `"duckdb"` and add a `DuckDB` section to your `appsettings.json`:

```json
{
  "DataSource": {
    "Provider": "duckdb"
  },
  "DuckDB": {
    "DatabasePath": "/data/layers.duckdb",
    "ReadOnly": true,
    "SpatialExtensionPath": null,
    "Layers": [
      {
        "Id": 0,
        "Name": "Parcels",
        "Description": "County parcel boundaries",
        "Table": "parcels",
        "GeometryColumn": "geom",
        "ObjectIdColumn": "id",
        "Srid": 4326,
        "GeometryType": "Polygon"
      }
    ],
    "Services": [
      {
        "Name": "ParcelService",
        "Description": "Parcel data service",
        "LayerIds": [0],
        "Capabilities": ["Query"],
        "EnabledProtocols": ["FeatureServer", "OgcFeatures", "Grpc"]
      }
    ]
  }
}
```

### Configuration Reference

| Setting | Default | Description |
|---------|---------|-------------|
| `DatabasePath` | `:memory:` | Path to `.duckdb` file. Use absolute paths in production. |
| `ReadOnly` | `true` | Open database in read-only mode. Recommended for production. |
| `SpatialExtensionPath` | `null` | Offline path for the spatial extension. When null, DuckDB downloads it automatically on first use. |

#### Layer Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Id` | — | Unique integer layer ID. Must match service `LayerIds` references. |
| `Name` | — | Display name shown in service metadata. |
| `Table` | — | DuckDB table name containing the spatial data. |
| `GeometryColumn` | `geom` | Column containing geometry values (GEOMETRY type). |
| `ObjectIdColumn` | `id` | Primary key column (BIGINT). |
| `Srid` | `4326` | Spatial Reference System ID of the geometry data. |
| `GeometryType` | `Point` | Geometry type: `Point`, `MultiPoint`, `LineString`, `MultiLineString`, `Polygon`, `MultiPolygon`. |
| `Attributes` | `null` | Optional explicit list of attribute column names to expose. When omitted, columns are discovered from the DuckDB schema at startup. |

#### Service Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Name` | — | URL-safe service name. |
| `LayerIds` | — | Array of layer IDs included in this service. |
| `Capabilities` | `["Query"]` | Only `Query` is supported in V1. `Create`, `Update`, `Delete`, and `Extract` (replica creation) are stripped at startup because the provider is read-only and has no replica persistence path. |
| `EnabledProtocols` | all | Protocols to expose: `FeatureServer`, `OgcFeatures`, `Grpc`. `Wfs20` may be listed but WFS `GetFeature` requests that ask for GML output will return `NotSupportedException` because the DuckDB provider does not implement `IGmlFeatureStore`. |

## Preparing Data

DuckDB tables must have the spatial extension loaded and use the `GEOMETRY` column type.

### From GeoParquet

```sql
INSTALL spatial;
LOAD spatial;

CREATE TABLE parcels AS
SELECT * FROM read_parquet('/data/parcels.parquet');
```

### From Shapefile

```sql
INSTALL spatial;
LOAD spatial;

CREATE TABLE parcels AS
SELECT * FROM ST_Read('/data/parcels.shp');
```

### From CSV with coordinates

```sql
INSTALL spatial;
LOAD spatial;

CREATE TABLE stations AS
SELECT id, ST_Point(longitude, latitude) AS geom, name, type
FROM read_csv('/data/stations.csv');
```

Ensure the `ObjectIdColumn` is a `BIGINT PRIMARY KEY` and the geometry column uses
the `GEOMETRY` type.

## Limitations

| Capability | Status |
|-----------|--------|
| Feature queries (select, count, object IDs) | Supported |
| Spatial filters (intersects, within, contains, etc.) | Supported |
| Statistics and aggregation (sum, avg, count, group by) | Supported |
| GeoJSON export | Supported |
| Feature streaming | Supported |
| Extent computation | Supported |
| **Feature editing (create, update, delete)** | **Not supported** — returns `NotSupportedException` |
| **Replica / Extract workflows** | **Not supported** — capability stripped at startup; replica repository and change tracker are no-op stubs so any cache writes do not propagate to durable storage |
| **MVT vector tiles** | **Not supported** — tile provider stub returns `NotSupportedException` for both `GetMvtTile` and `GetH3MvtTile` |
| **H3 hexagonal aggregation** | **Not supported** |
| **FlatGeobuf / Geobuf export** | **Not supported** — `QueryFlatGeobufAsync` returns null so the server falls back to JSON encoding when possible |
| **Native GML output (WFS 2.0)** | **Not supported** — `IGmlFeatureStore` stub returns `NotSupportedException`, so WFS `GetFeature` requests that need GML output will fail |
| **Relationship queries** | **Not supported** |

## Workload Profile

DuckDB is an embedded analytical database optimized for read-heavy OLAP workloads:

- **Concurrency**: DuckDB uses a single-writer model. Read queries execute concurrently. The
  provider opens a fresh connection per query to avoid connection-state conflicts.
- **Memory**: DuckDB uses memory-mapped I/O. Memory usage scales with data size and query
  complexity, not connection count.
- **Startup**: The spatial extension is installed once on the first query (persisted to
  the extension directory) and loaded onto every freshly opened connection because
  DuckDB's `LOAD` statement is connection-scoped. Air-gapped deployments should set
  `SpatialExtensionPath` to avoid network calls.
- **File locking**: DuckDB holds a file lock on the `.duckdb` file. Only one process can open
  the file at a time (even in read-only mode, a shared lock is acquired). Do not run multiple
  Honua instances against the same DuckDB file.

## Troubleshooting

### "DllNotFoundException: duckdb"

The `DuckDB.NET.Data.Full` package bundles the native DuckDB library for common platforms.
If running on an unsupported OS/architecture, you may need to provide the native library manually.

### "Spatial extension not found"

On first startup, DuckDB downloads the spatial extension from the DuckDB extension repository.
In air-gapped environments, download the extension file separately and set `SpatialExtensionPath`.

### Write operations return errors

This is expected. The DuckDB provider is read-only by design. Feature editing, MVT tiles,
H3 aggregation, native GML output, replica/extract workflows, and the Pro-tier spatial
analytics endpoints (`queryClusters`, `spatialJoin`, `queryBufferAggregate`, `queryDensity`
and their OGC mirrors) are not supported — the analytics endpoints require PostGIS
primitives (`ST_ClusterDBSCAN`/`ST_ClusterKMeans`, `ST_Buffer`/`ST_Union`, hex/square grid
generators) that DuckDB Spatial does not provide. Use the PostgreSQL provider for write
workloads, vector tiles, WFS GML responses, or spatial analytics.

The analytics routes are mapped unconditionally so the route surface stays consistent
across providers, but on the DuckDB provider calling any of them returns **HTTP 501
Not Implemented** with a `StandardErrorResponse` body naming the unsupported operation
(title `Not Implemented`, detail `Spatial analytics ({operation}) is not supported by
the active feature-store provider.`). Clients should treat 501 as a permanent signal
that the deployment does not ship an analytics backend — it is not a transient failure
and will not recover without switching providers. This mirrors how the H3 capability
gate surfaces an unsupported deployment instead of leaking an `InvalidOperationException`
as HTTP 500.
