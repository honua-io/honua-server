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
    "HttpFs": {
      "S3": {
        "Region": "us-east-1",
        "Endpoint": "minio:9000",
        "AccessKeyId": "${DUCKDB_S3_ACCESS_KEY_ID}",
        "SecretAccessKey": "${DUCKDB_S3_SECRET_ACCESS_KEY}",
        "UrlStyle": "path",
        "UseSsl": false
      }
    },
    "Layers": [
      {
        "Id": 0,
        "Name": "Parcels",
        "Description": "County parcel boundaries",
        "Table": "parcels",
        "ExternalSource": {
          "Format": "GeoParquet",
          "Path": "s3://honua-demo/parcels/*.parquet",
          "UnionByName": true
        },
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
| `SpatialExtensionPath` | `null` | Offline path for DuckDB extension files. When null, DuckDB downloads extensions automatically on first use. The current property name is historical; it is also used for `httpfs` and `azure`. |
| `HttpFs:S3` | empty | Optional S3-compatible settings applied to each DuckDB connection before external views are created. |
| `Azure` | empty | Optional Azure Blob/ADLS settings applied to each DuckDB connection before external views are created. |

#### Object-Store Settings

`HttpFs:S3` supports `Region`, `Endpoint`, `AccessKeyId`, `SecretAccessKey`, `SessionToken`,
`UrlStyle` (`path` or `vhost`), `UseSsl`, and `RequesterPays`. Use environment/secret-backed
configuration for credentials; do not embed credentials in layer source paths.

`Azure` supports `ConnectionString`, `AccountName`, `Endpoint`, and `CredentialChain`.
Do not configure `ConnectionString` and `CredentialChain` together. For managed identity
or CLI-based access, prefer `AccountName` plus `CredentialChain`.

#### Layer Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Id` | — | Unique integer layer ID. Must match service `LayerIds` references. |
| `Name` | — | Display name shown in service metadata. |
| `Table` | — | DuckDB table name containing the spatial data. For external sources this is the connection-scoped temporary view name that Honua creates. |
| `ExternalSource` | `null` | Optional Parquet/GeoParquet source scanned through DuckDB. When set, Honua creates a temporary DuckDB view named by `Table` on every provider connection. |
| `GeometryColumn` | `geom` | Column containing geometry values (GEOMETRY type). |
| `ObjectIdColumn` | `id` | Primary key column (BIGINT). |
| `Srid` | `4326` | Spatial Reference System ID of the geometry data. |
| `GeometryType` | `Point` | Geometry type: `Point`, `MultiPoint`, `LineString`, `MultiLineString`, `Polygon`, `MultiPolygon`. |
| `Attributes` | `null` | Optional explicit list of attribute column names to expose. When omitted, columns are discovered from the DuckDB schema at startup. |

`ExternalSource` settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `Format` | `GeoParquet` | `GeoParquet` or `Parquet`. Both use DuckDB `read_parquet`; `GeoParquet` is an operator declaration that the configured geometry column is usable by DuckDB spatial functions. |
| `Path` | `null` | Single local path, HTTP(S) URL, S3/R2/GCS-compatible URI, Azure URI, or glob. |
| `Paths` | `null` | Multiple paths or globs scanned as one layer. |
| `HivePartitioning` | `false` | Passes `hive_partitioning = true` to `read_parquet`. |
| `UnionByName` | `false` | Passes `union_by_name = true` to `read_parquet` for multi-file sources with compatible but non-identical schemas. |

Supported remote schemes are `http`, `https`, `s3`, `s3a`, `s3n`, `r2`, `gs`, `gcs`,
`az`, `azure`, `abfs`, `abfss`, `wasb`, and `wasbs`. Local paths and `file://` paths
are also allowed.

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

### Directly from Object Storage

For read-only analytical layers, Honua can create a temporary DuckDB view over Parquet
or GeoParquet files each time it opens a provider connection:

```json
{
  "DuckDB": {
    "DatabasePath": ":memory:",
    "ReadOnly": true,
    "HttpFs": {
      "S3": {
        "Region": "us-east-1",
        "Endpoint": "minio:9000",
        "UrlStyle": "path",
        "UseSsl": false
      }
    },
    "Layers": [
      {
        "Id": 0,
        "Name": "Parcels",
        "Table": "parcels_external",
        "ExternalSource": {
          "Format": "GeoParquet",
          "Path": "s3://honua-demo/parcels/*.parquet",
          "UnionByName": true
        },
        "GeometryColumn": "geom",
        "ObjectIdColumn": "id",
        "Srid": 4326,
        "GeometryType": "Polygon",
        "Attributes": ["name", "area", "type"]
      }
    ]
  }
}
```

At connection bootstrap, Honua installs/loads `spatial`, loads `httpfs` for S3/HTTP/GCS/R2
paths, loads `azure` for Azure paths, applies configured object-store settings, then runs:

```sql
CREATE OR REPLACE TEMP VIEW "parcels_external" AS
SELECT * FROM read_parquet('s3://honua-demo/parcels/*.parquet', union_by_name = true);
```

Set `Attributes` explicitly for production external sources when possible. If omitted,
Honua attempts startup schema discovery by querying DuckDB metadata for the temp view,
which can touch object storage and fail early when credentials, extension installation,
or paths are wrong.

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
| Object-store Parquet / GeoParquet scans | Supported for read-only analytical layers via DuckDB temporary views |
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
  DuckDB's `LOAD` statement is connection-scoped. When external sources require `httpfs`
  or `azure`, those extensions follow the same install-once/load-per-connection pattern.
  Air-gapped deployments should set `SpatialExtensionPath` to avoid network calls.
- **Object storage**: External Parquet/GeoParquet layers are best for read-mostly analytical
  datasets with column pruning and bounded filters. Honua does not cache arbitrary object
  scans by default. Use object-store lifecycle policies, CDN/proxy caches where appropriate,
  compact Parquet row groups, partition pruning, and explicit layer limits instead of treating
  object storage like an OLTP backend.
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

### "httpfs extension not found" or "azure extension not found"

External object-store sources require DuckDB extensions. S3, R2, GCS-compatible, HTTP, and HTTPS
paths require `httpfs`; Azure Blob and ADLS paths require `azure`. In air-gapped environments,
place the extension artifacts in the configured `SpatialExtensionPath` directory. The bootstrap
error intentionally names the failed step without printing credential values.

### Object-store credential errors

Keep credentials in environment/secret-backed configuration and bind them into `DuckDB:HttpFs:S3`
or `DuckDB:Azure`. Honua rejects obvious credential query parameters in external source paths so
layer metadata and logs do not carry access keys. For MinIO and many S3-compatible stores, set
`Endpoint`, `UrlStyle` to `path`, and `UseSsl` according to the endpoint.

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
