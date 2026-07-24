# Amazon Redshift provider

## Protocol routing

OData collection queries, counts, and streaming responses resolve this provider per layer from the
Metadata v2 storage binding. Providers without native streaming use the same routed reader through
a bounded, materialized page; Honua never falls back to the primary provider for that layer.
This provider is read-only, so OData create/update/delete requests (including `$batch` mutations)
return `501 ProviderWriteNotSupported` instead of dispatching to the primary provider.

OGC API Tiles raster (`f=png`) tile requests resolve this provider per collection the same way,
through `FeatureProviderQueryRouter`; Honua never falls back to the primary provider for a routed
collection's raster tiles. Vector (MVT) tile requests instead return a `501 Not Implemented`
problem response naming the collection and provider: native MVT generation is a per-provider
capability that only the PostGIS provider implements today, independent of the routing fix
delivered under [issue #2962](https://github.com/honua-io/honua-server/issues/2962).


Honua exposes Amazon Redshift (`GEOMETRY` and `GEOGRAPHY`) tables as read-only feature layers
through the shared `IFeatureDataProvider` seam. Redshift is the analytical-warehouse backend for
organizations that already keep authoritative spatial data in Redshift and want to publish it
without copying it into PostGIS.

This page describes the **read/query thin slice** delivered under issue
[#1712](https://github.com/honua-io/honua-server/issues/1712). Edits, native MVT, native
FlatGeobuf/Geobuf/GML, statistics aggregates, and admin UI integration are deliberately out of
scope and would land as separate slices.

## Wire Protocol vs. Spatial Layer

Redshift speaks the **PostgreSQL wire protocol**, so connectivity uses the Npgsql driver — the
same client the PostGIS provider uses. The spatial layer, however, is **not PostGIS**: Redshift
ships its own native `GEOMETRY`/`GEOGRAPHY` types and a distinct set of `ST_*` functions. The
provider therefore restricts itself to Redshift-native spatial SQL (`ST_AsBinary`, `ST_GeomFromWKB`,
`ST_Intersects`, `ST_Within`, `ST_Contains`, `ST_Disjoint`, `ST_Envelope`, and the
`ST_XMin`/`ST_YMin`/`ST_XMax`/`ST_YMax` bounding-box accessors) and never calls PostGIS-only
functions that Redshift lacks.

## When to Use

- **Analytical-warehouse publication** where Redshift already holds authoritative spatial data.
- **Read-only publication** of existing Redshift spatial tables/views without copying into PostGIS.
- **Cloud-native AWS deployments** that standardize on Redshift for warehouse-scale geospatial data.

The provider is **not** suitable for:

- Editable feature layers (use the PostgreSQL provider).
- Native MVT, FlatGeobuf, Geobuf, or GML emission (the in-process formatter handles these).
- Statistics, top-features, date bins, value bins, H3 aggregations, temporal extents, spatial
  analytics, or KNN/distance filters — these throw `NotSupportedException` and are reported as
  unsupported in `FeatureProviderCapabilities`.

## Configuration

Register the provider as an additional read backend by enabling the `Redshift` section in your
`appsettings.json`. The provider plugs in alongside the primary backend (PostGIS or DuckDB) and is
selected per-layer based on the layer's `DataConnection` provider name.

```json
{
  "DataSource": { "Provider": "postgres" },
  "Redshift": {
    "Enabled": true,
    "ConnectionString": "Host=my-cluster.abc123.us-east-1.redshift.amazonaws.com;Port=5439;Database=geo;Username=honua;Password=...;SSL Mode=Require",
    "CommandTimeoutSeconds": 60
  }
}
```

| Setting | Default | Description |
|---|---|---|
| `Redshift:Enabled` | `true` | Set to `false` to skip provider registration even if the assembly is referenced. |
| `Redshift:ConnectionString` | _none_ | Default connection string used when a layer's secure connection is unavailable. Prefer secret-store references in production. |
| `Redshift:CommandTimeoutSeconds` | `60` | Per-command timeout in seconds. Must be positive. |

Connection pooling is handled by Npgsql internally; the provider opens a fresh connection per
operation and lets the driver pool transparently. The default Redshift port is `5439`.

### Per-Layer Storage Mapping

Layers that should be served from Redshift use the existing `LayerStorageMapping` model plus a
`DataConnection` whose provider name resolves to `redshift` (aliases `amazon-redshift`,
`amazon_redshift`, `redshiftdb`, `aws-redshift`).

```json
{
  "Layers": [
    {
      "Id": 100,
      "Name": "Parcels",
      "GeometryType": "Polygon",
      "StorageMapping": {
        "TableName": "parcels",
        "SchemaName": "public",
        "PrimaryKeyColumn": "id",
        "GeometryColumn": "geom",
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

- `"geometry"` (default) — planar Redshift `GEOMETRY` column. Distances are in CRS units.
- `"geography"` — geodetic Redshift `GEOGRAPHY` column. Distances are in meters on the reference
  spheroid; lat/lon datasets should explicitly opt in to this mode. Geography values are
  round-tripped through `ST_AsBinary`/`ST_GeomFromWKB` so the planar accessors used for WKB
  output and extent apply.

All identifiers configured on a layer (`TableName`, `SchemaName`, `PrimaryKeyColumn`,
`GeometryColumn`, plus any field referenced in WHERE/`OutFields`/`OrderBy`) must match the regular
SQL identifier pattern `[A-Za-z_][A-Za-z0-9_]*`. The provider rejects any input that fails the
allow-list and emits a problem-details response through the shared exception pipeline.

## Supported Operations

| Operation | Status | Implementation |
|---|---|---|
| `IFeatureReader.GetAsync` | Supported | SELECT primary key + geometry (WKB via `ST_AsBinary`) + attributes. |
| `IFeatureReader.QueryAsync` | Supported | SELECT with attribute/`ObjectIds`/spatial filters and `LIMIT`/`OFFSET` paging. |
| `IFeatureReader.QueryObjectIdsAsync` | Supported | SELECT primary key only. |
| `IFeatureReader.CountAsync` | Supported | `SELECT COUNT(*)`. |
| `IFeatureReader.GetExtentAsync` | Supported | `MIN/MAX` over `ST_XMin`/`ST_YMin`/`ST_XMax`/`ST_YMax`. |
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
| `Intersects` | `ST_Intersects(geom, ST_GeomFromWKB(@wkb, @srid))` |
| `EnvelopeIntersects` | `ST_Intersects(ST_Envelope(geom), ST_Envelope(ST_GeomFromWKB(@wkb, @srid)))` |
| `Within` | `ST_Within(geom, ST_GeomFromWKB(@wkb, @srid))` |
| `Contains` | `ST_Contains(geom, ST_GeomFromWKB(@wkb, @srid))` |
| `Disjoint` | `ST_Disjoint(geom, ST_GeomFromWKB(@wkb, @srid))` |
| `Crosses`, `Touches`, `Overlaps`, `Equals` | Not supported in this slice — request a follow-up if needed. |
| `WithinDistance`, `BeyondDistance`, `NearestNeighbor` | Not supported — distance/KNN filters throw `NotSupportedException`. |

Filter geometries are parsed with `ST_GeomFromWKB(@wkb, @srid)`. When the spatial filter does not
specify an SRID, the provider falls back to the layer's `StorageSrid` so the comparison stays in a
single CRS. Cross-SRID filters are rejected up front with `NotSupportedException` rather than
returning silent zero-row results.

### WHERE Clause

Simple WHERE expressions are parsed and parameterized in-process. Supported tokens:

- `field = literal`, `field <> literal`, `field != literal`
- `field <`, `<=`, `>`, `>=` `literal`
- `field LIKE 'pattern'`, `field NOT LIKE 'pattern'`
- `field IS [NOT] NULL`
- Any number of the above joined with `AND`

Anything outside this grammar is rejected with `ArgumentException`; full-CQL2 / OData / ArcGIS
expression translation is not part of this slice.

When a request supplies a translated `SqlFilter`, the provider rejects it with
`NotSupportedException`: the shared `ISqlFilterTranslator` pipeline currently only registers a
PostGIS translator, whose JSONB operators, `::casts`, and PostGIS-only spatial functions are not
valid Redshift SQL. Populate the canonical `Where` text (e.g. the FeatureServer `where` parameter)
to filter Redshift layers.

### Pagination

Pagination uses standard PostgreSQL `LIMIT … OFFSET …` syntax. Unlike SQL Server, Redshift does not
require an `ORDER BY` for `OFFSET`; callers that paginate without an `ORDER BY` accept the usual
undefined-order caveat. To detect `HasMoreResults` without a separate `COUNT` round-trip, the
provider over-fetches one extra row beyond the requested limit, trims it, and reports
`HasMoreResults` based on whether the probe row arrived. `TotalCount` reflects the size of the
returned page only; callers that need the absolute total should use `CountAsync`.

## Observability

- Activity source: `Honua.Redshift.FeatureStore` — emits spans named `redshift.feature.select`,
  `redshift.feature.count`, `redshift.feature.extent`, and `redshift.feature.objectids`, each
  tagged with `layer.id`.
- Logging: source-generated structured events (`RedshiftFeatureLog`) emitted under the
  `Honua.Redshift.Features.FeatureStore.Services.RedshiftFeatureDataAccess` category (event ids
  `7200` for prepared queries, `7201` for unsupported-operation rejections, `7202` for query
  failures). The provider does not emit raw SQL exception messages or connection strings.

## Testing

### Unit Tests (always run in CI)

```bash
dotnet test tests/dotnet/Honua.Redshift.Tests
```

Covers the SQL translation paths (SELECT, COUNT, EXTENT, ObjectIds, paging, attribute filters,
spatial filters, identifier validation), provider-name normalization, and the SQL dialect. No
Redshift instance is required.

### Gated Integration Tests

There is no official Amazon Redshift Testcontainer image. The integration suite is doubly gated —
it is excluded from the default PR run by the `Category=Redshift` trait, and it additionally
requires `HONUA_TEST_REDSHIFT=1` so a stray category filter does not start Docker. Because Redshift
is PostgreSQL-wire-compatible and the SQL emitted for non-spatial reads, COUNT, object-id listings,
and extent is also valid against PostGIS, the suite uses a PostGIS Testcontainer purely as a
wire-compatible stand-in to exercise the Npgsql connection factory and data-access materialization.
It does **not** prove Redshift-specific spatial semantics — that requires a real Redshift cluster.

```bash
HONUA_TEST_REDSHIFT=1 dotnet test tests/dotnet/Honua.Redshift.Tests --filter Category=Redshift
```

## Limitations and Known Gaps

- **Read-only.** Edits, transactions, and applyEdits are not implemented; `Writer` is `null`.
- **No native output formats.** MVT/FlatGeobuf/Geobuf/GML are produced by the shared in-process
  formatters using the WKB returned by SELECT.
- **No reprojection on output.** `OutputSrid` is rejected with `NotSupportedException`; results are
  returned in the layer's storage SRID.
- **No distance / KNN / temporal filters.** These throw `NotSupportedException`.
- **WHERE grammar is intentionally narrow.** Translated `SqlFilter` fragments are rejected because
  the shared translator emits PostGIS SQL; use the canonical `Where` text for predicates.
- **Spatial semantics are not CITE-validated against a live Redshift cluster.** The gated
  integration suite validates only the wire/data-access path against a PostGIS stand-in.
