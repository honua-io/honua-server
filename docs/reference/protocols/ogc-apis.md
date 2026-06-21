# OGC APIs

Honua implements the modern OGC API family — Features, Maps, Tiles, Coverages, Processes, Records, and Styles — as JSON-first REST surfaces under `/ogc/*`. Every API exposes a landing page, a `conformance` declaration, and (except Records) an OpenAPI document.

## OpenAPI documents

| API | OpenAPI route |
| --- | --- |
| Features | `GET /openapi.json` (service-wide) and `GET /ogc/features/api` (OGC alias) |
| Maps | `GET /ogc/maps/openapi.json` |
| Tiles | `GET /ogc/tiles/openapi.json` |
| Coverages | `GET /ogc/coverages/openapi.json` and `GET /ogc/coverages/api` |
| Processes | `GET /ogc/processes/openapi.json` |
| Styles | `GET /ogc/styles/openapi.json` |
| Records | none (landing-page links only) |

## OGC API Features

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/features` | Landing page. |
| GET | `/ogc/features/conformance` | Conformance classes. |
| GET | `/ogc/features/collections` | Collection list. |
| GET | `/ogc/features/collections/{collectionId}` | Collection metadata. |
| GET | `/ogc/features/collections/{collectionId}/queryables` | Filterable properties as JSON Schema. |
| GET | `/ogc/features/collections/{collectionId}/items` | Feature query. |
| GET | `/ogc/features/collections/{collectionId}/items/{featureId}` | Single feature. |
| POST | `/ogc/features/collections/{collectionId}/items` | Create feature (GeoJSON). |
| POST | `/ogc/features/collections/{collectionId}/items/batch` | Batch create (Honua extension). |
| PUT, PATCH, DELETE | `/ogc/features/collections/{collectionId}/items/{featureId}` | Replace, merge-patch, delete. |
| GET | `/ogc/features/collections/{collectionId}/h3` | H3 aggregation (Honua extension; requires `resolution`). |
| POST | `/ogc/features/collections/{collectionId}/clusters`, `/spatial-join`, `/buffer-aggregate`, `/density` | Spatial analytics extensions (Pro tier; 402 when entitlement inactive). |
| GET | `/ogc/features/schemas/honua-ogcapi-features.xsd` | GML application schema. |

### Items query parameters

| Parameter | Notes |
| --- | --- |
| `f` | `geojson`, `json`, `gml`, `csv`, `html`. |
| `limit`, `offset` | Paging, normalized by server limits. |
| `ids`, `properties`, `sortby` | ID filter, property projection, `+`/`-`/`asc`/`desc` sorting. |
| `bbox`, `bbox-crs` | 4 or 6 values; anti-meridian supported; any registry-resolvable EPSG CRS. |
| `crs` | Output CRS; response includes `Content-Crs` (Part 2). |
| `datetime` | RFC 3339 instant or interval; requires temporal fields. |
| `filter`, `filter-lang`, `filter-crs` | CQL2 filtering (Part 3): `cql2-text` (default) and `cql2-json`. |
| Queryable properties | Simple-valued queryables accepted directly as query parameters (combined with AND). |

CQL2 support covers logical/comparison/arithmetic operators, `LIKE`, `IN`, `BETWEEN`, all `S_*` spatial predicates (including `S_DWITHIN`/`S_BEYOND`), the full `T_*` temporal predicate set, `A_*` array predicates, and a string/numeric/datetime/`CASEI`/`ACCENTI` function set. Unsupported operators and functions return 400. Full operator tables: [archived coverage matrix](../../archive/specifications/ogc-api-features-coverage.md).

```bash
curl "https://server.example.com/ogc/features/collections/roads/items?filter=S_INTERSECTS(geometry,POINT(-122.4%2037.8))&limit=10"
```

## OGC API Maps

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/maps`, `/ogc/maps/conformance` | Landing page, conformance. |
| GET | `/ogc/maps/collections/{collectionId}/map` | Rendered map for one collection. |
| GET | `/ogc/maps/collections/{collectionId}/styles/{styleId}/map` | Rendered map with a named style. |
| GET | `/ogc/maps/collections/{collectionId}/map/tiles`, `.../map/tiles/{tileMatrixSetId}` | Map tileset metadata. |
| GET | `/ogc/maps/map` | Dataset map (multi-collection via `collections=`). |

Key parameters: `bbox`, `bbox-crs`, `crs`, `width`/`height` (1–4096, default 256), `f` (`png`, `jpeg`, `tiff`), `transparent` (default true), `bgcolor` (`0xRRGGBB`), `datetime`, `quality`.

```bash
curl -o map.png "https://server.example.com/ogc/maps/collections/roads/map?bbox=-122.5,37.7,-122.3,37.9&width=800&height=600&f=png"
```

## OGC API Tiles

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/tiles`, `/ogc/tiles/conformance` | Landing page, conformance. |
| GET | `/ogc/tiles/collections`, `.../collections/{collectionId}` | Collection discovery. |
| GET | `/ogc/tiles/collections/{collectionId}/tiles`, `.../tiles/{tileMatrixSetId}` | Per-collection tileset metadata. |
| GET | `/ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}` | Tile retrieval (vector or raster). |
| GET | `/ogc/tiles/tiles`, `.../tiles/{tileMatrixSetId}`, `.../tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}` | Dataset-level tilesets and tiles. |
| GET | `/ogc/tiles/tileMatrixSets`, `.../tileMatrixSets/{tileMatrixSetId}` | Tile matrix set registry: the reserved built-ins (`WebMercatorQuad`, `WorldCRS84Quad`) plus any operator-defined custom gridsets. |

Custom tile matrix sets are merged in from the `TileMatrixSets` configuration section (validated for unique IDs, no reserved-ID collision, monotonic scale denominators, positive tile dimensions, and a valid SRID). Custom gridsets are advertised through the registry and served by `GetTile` as both PNG (raster) and MVT (vector): the vector-tile provider derives the tile envelope and target SRID from the gridset geometry and reprojects the stored geometry into the gridset CRS with `ST_Transform`. The `crs`/`subset-crs` request parameters accept the gridset's own CRS for custom gridsets (tiles are delivered in the gridset CRS). Built-in gridset output (WebMercatorQuad / WorldCRS84Quad) is byte-identical to before. The per-dataset / per-collection tileset-metadata documents (`/ogc/tiles/.../tiles/{tileMatrixSetId}` and the tileset lists) advertise custom gridsets as `vector` (#1916): each custom gridset is emitted with its own CRS/URI and full-coverage per-level tile-matrix limits derived from the gridset geometry, threaded through the `ITileMatrixSetRegistry`; the two built-ins keep the byte-identical static-descriptor path. Tile requests accept a vertical/elevation subset (`subset=Z(...)` / `elevation(...)` / `height(...)`): the value is parsed, validated, and recorded on the render descriptor — raster layers can honour the coordinate, vector layers record-but-do-not-render it (Zarr-slice render binding is deferred). Non-vertical or unknown subset axes (e.g. `E(0:1)`) still return 400.

```bash
curl -o tile.mvt "https://server.example.com/ogc/tiles/collections/roads/tiles/WebMercatorQuad/12/1586/2412"
```

## OGC API Coverages

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/coverages`, `/ogc/coverages/conformance` | Landing page, conformance. |
| GET | `/ogc/coverages/collections`, `.../collections/{collectionId}` | Raster-backed collection discovery. |
| GET | `/ogc/coverages/collections/{collectionId}/schema` | Band fields (`band_1`, `band_2`, …) as JSON Schema. |
| GET | `/ogc/coverages/collections/{collectionId}/coverage` | Coverage bytes (GeoTIFF default, PNG via `f=png` or `Accept`). |

Key coverage parameters: `f` (`geotiff`/`tiff`/`png` and MIME forms), `bbox`, `bbox-crs`, `crs` (output CRS), `properties` (band selection, order-preserving), and exactly one scaling control per request — `resolution`, `scale-factor`, or `scale-size` (max 8192 px per axis). `datetime` (RFC 3339 instant or interval) applies temporal subsetting to Zarr multidimensional coverages that declare an evenly-spaced time axis — an instant rounds to the nearest index, an interval is resolved by ceil/floor over the index range; coverages with no time axis, an irregular axis, or a conflicting `subset` over time return 400. `subset` and `scale-axes` are deferred and return 400.

```bash
curl -o clip.tif "https://server.example.com/ogc/coverages/collections/0/coverage?bbox=-122.5,37.7,-122.3,37.9&properties=band_1"
```

## OGC API Processes

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/processes`, `/ogc/processes/conformance` | Landing page, conformance. |
| GET | `/ogc/processes/processes`, `.../processes/{processId}` | Process list and description. |
| POST | `/ogc/processes/processes/{processId}/execution` | Execute (async-only; requires `Prefer: respond-async`, returns 201 + `Location`). |
| GET | `/ogc/processes/jobs`, `.../jobs/{jobId}`, `.../jobs/{jobId}/results` | Job list (active only, `limit`), status, results. |
| DELETE | `/ogc/processes/jobs/{jobId}` | Dismiss (cancel) a job. |

V1 projects a single canonical process (`honua-geoprocessing`); plans are validated against the built-in 34-process catalog at submission. Synchronous execution returns 501; job endpoints require Redis-backed durable storage (503 otherwise).

```bash
curl -X POST "https://server.example.com/ogc/processes/processes/honua-geoprocessing/execution" \
  -H "Prefer: respond-async" -H "Content-Type: application/json" \
  -d '{"inputs":{"plan":{"planId":"buffer-demo","steps":[{"kind":"geoprocess","processId":"geometry.buffer","inputs":{"layerId":"0","distance":"100"}}]}},"response":"document"}'
```

## OGC API Records

Read-only catalog discovery over published services and layers (record ids `layer:{layerId}`, `service:{serviceName}`).

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/records`, `/ogc/records/conformance` | Landing page, conformance (read-only core + GeoJSON classes). |
| GET | `/ogc/records/collections`, `.../collections/{collectionId}` | Single `honua-catalog` collection. |
| GET | `/ogc/records/collections/{collectionId}/items`, `.../items/{recordId}` | GeoJSON records. |

`/items` parameters: `limit` (cap 1000), `offset`, `ids`, `type` (`service`/`dataset`), `externalIds`, `q`, `bbox`, `datetime`. Record create/update/delete, harvesting, facets, and CQL filtering are not implemented.

## OGC API Styles

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/ogc/styles`, `/ogc/styles/conformance` | Style list, conformance. |
| GET | `/ogc/styles/{styleId}`, `.../{styleId}/metadata` | Style document and metadata. |
| POST, PUT, DELETE | `/ogc/styles`, `/ogc/styles/{styleId}` | Style management. |

## Conformance

OGC API Features CITE: 137/137; OGC API Tiles CITE: 16/16. Authoritative status: [API standards summary](../compatibility/ogc-conformance.md) and [cite-status.md](../../cite-status.md). Per-API conformance class URIs are served live at each `/conformance` route.

## Guides that use this

- [Query features](../../guides/query-analyze/query-features.md)
- [Run geoprocessing](../../guides/query-analyze/run-geoprocessing.md)
- [Work with time](../../guides/query-analyze/work-with-time.md)
- [Publish rasters](../../guides/publish/publish-rasters.md)
- [Connect from QGIS](../../guides/connect/qgis.md)
