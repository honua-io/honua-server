# Geoprocessing operations

Catalog of the built-in geoprocessing processes (process catalog `honua.process_catalog.builtin.v1`). The same catalog is exposed through three surfaces: OGC API Processes (`/ogc/processes/processes`), the ArcGIS-compatible GPServer adapter (`/rest/services/{serviceId}/GPServer`), and gRPC `geospatial.v1.ProcessService`. For a submit/poll/fetch walkthrough see [run geoprocessing](../guides/query-analyze/run-geoprocessing.md); to write your own process, see [author a geoprocessing process](../guides/query-analyze/gp-devkit-authoring.md).

Execution notes that apply across families:

- **Runtime profile.** Processes marked *native* below execute out-of-process in the heavyweight GDAL worker image; the lean GDAL-free serving image validates their plans but never executes them. A deployment without the GDAL worker cannot run them.
- **Raster sourcing.** Native raster/surface processes accept the raster either as base64-encoded GeoTIFF bytes on the `source` parameter, or by reference to a registered catalog raster via `layerId`/`rasterId` (#2264). Layer/raster references are resolved on the submit side and materialized onto `source` before dispatch, so a plan must supply exactly one of `source`, `layerId`, or `rasterId`. Layer-resolved sourcing requires a configured raster catalog; deployments without one must supply an inline `source`.
- **NoData (raster calc).** `raster.map-algebra`, `raster.spectral-index`, and `raster.reclassify` propagate NoData: each input is masked by its own NoData, and the output band is tagged with an explicit `noData` value when supplied, otherwise with the first source raster's detected band NoData (#2267).
- **Inline FeatureCollections.** `*-managed`, `transform.*`, `source.*`, and `sink.*` processes exchange features as `data:application/geo+json;base64` data URIs so they compose as workflow nodes.
- **Approval gate.** `data-management.delete-features` and `data-management.calculate-field` are destructive and require operator approval.
- **Admission.** Submissions pass through admission control (`ExecutionAdmission__*` — see [environment variables](configuration/environment-variables.md#admission-and-pooling)).

## Geometry (14)

Single-geometry operations; inputs are base64-encoded WKB plus an SRID.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `geometry.buffer` | Polygon at a distance around the input geometry. | `wkb`, `srid`, `distance` (m), `geodesic` |
| `geometry.simplify` | Douglas-Peucker simplification, topology-preserving by default. | `wkb`, `srid`, `tolerance`, `preserveTopology` |
| `geometry.project` | Reproject between spatial references. | `wkb`, `fromSrid`, `toSrid` |
| `geometry.make-valid` | Repair self-intersections, duplicate vertices, ring orientation. | `wkb`, `srid` |
| `geometry.union` | Union of multiple geometries. | `wkbs`, `srid` |
| `geometry.intersect` | Intersection of two geometries. | `targetWkb`, `intersectorWkb`, `srid` |
| `geometry.clip` | Clip to the bounding envelope of a clip geometry. | `targetWkb`, `clipEnvelopeWkb`, `srid` |
| `geometry.difference` | Subtract an eraser geometry. | `targetWkb`, `eraserWkb`, `srid` |
| `geometry.area` | Geodesic area of a polygon (m²). | `wkb`, `srid` |
| `geometry.length` | Geodesic length of a line (m). | `wkb`, `srid` |
| `geometry.centroid` | Centroid point. | `wkb`, `srid` |
| `geometry.convex-hull` | Convex hull (PostGIS `ST_ConvexHull` semantics). | `wkb`, `srid` |
| `geometry.dissolve` | Union by optional group key, one feature per group. | `wkbs`, `srid`, `groupKeys` |
| `geometry.snap` | Snap vertices to a reference geometry within a tolerance. | `wkb`, `referenceWkb`, `srid`, `tolerance` |

## Analytics (8)

The layer-scoped processes (`analytics.cluster`, `analytics.spatial-join`, `analytics.buffer-aggregate`, `analytics.density`) run synchronously against PostGIS-backed layers and are **not job-dispatchable**; each has a job-executable `*-managed` counterpart that runs in managed code (NetTopologySuite) over inline FeatureCollections. All layer-scoped processes accept the shared GeoServices filter parameters (`where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `time`, `timeRelation`).

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `analytics.cluster` | DBSCAN or K-Means clustering on a layer. | `layerId`, `algorithm`, `eps`, `minPoints`, `k`, `returnHullPerCluster`, `outStatistics` |
| `analytics.spatial-join` | Enrich target features from a join layer by spatial predicate. | `layerId`, `joinLayerId`, `predicate` (intersects/contains/within/dwithin), `distance`, `carryFields`, `outStatistics` |
| `analytics.buffer-aggregate` | Buffer and optionally dissolve per group with statistics. | `layerId`, `distance`, `unit`, `dissolve`, `groupByFields`, `outStatistics` |
| `analytics.density` | Hex or square grid binning with counts or weighted sums. | `layerId`, `mode`, `cellSize` (m), `weightField` |
| `analytics.cluster-managed` | Job-executable DBSCAN/K-Means over inline features; appends `CLUSTER_ID`. Distances in CRS units. | `input`, `algorithm`, `eps`, `minPoints`, `k` |
| `analytics.spatial-join-managed` | Job-executable join over two inline FeatureCollections; `JOIN_COUNT` plus sum/mean/min/max aggregates. | `input`, `join`, `predicate`, `statistics` |
| `analytics.buffer-aggregate-managed` | Job-executable buffer-and-dissolve over inline features. | `input`, `distance`, `unit`, `dissolve`, `groupByFields` |
| `analytics.density-managed` | Job-executable hex/square binning over inline features; cell size in CRS units. | `input`, `mode`, `cellSize`, `weightField` |

## Surface analysis (6) — native

DEM-derived raster products executed by the GDAL worker via `gdaldem`. All take `source` (base64 GeoTIFF DEM) and publish a GeoTIFF artifact.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `surface.slope` | Slope raster. | `units` (degrees/percent), `zFactor` |
| `surface.aspect` | Compass-bearing aspect raster. | `source` |
| `surface.hillshade` | Hillshade raster. | `azimuth` (315), `altitude` (45), `zFactor` |
| `surface.rugosity-tri` | Terrain ruggedness index (3×3 window only). | `windowRadius` (must be 1) |
| `surface.rugosity-tpi` | Topographic position index (3×3 window only). | `windowRadius` (must be 1) |
| `surface.roughness` | Roughness raster (3×3 window only). | `windowRadius` (must be 1) |

## Raster (6) — native

Executed by the GDAL worker via `gdalwarp`/`gdalinfo`. All take `source` (base64 GeoTIFF).

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `raster.clip` | Clip to a boundary geometry (`gdalwarp -cutline`). | `boundary` (WKB), `boundarySrid` |
| `raster.reproject` | Reproject with a resampling algorithm (`gdalwarp -t_srs`). | `targetSrid`, `resampling` (nearestneighbor/bilinear/cubic/lanczos) |
| `raster.statistics` | Per-band min/max/mean/stddev (`gdalinfo -stats`). | `bands` |
| `raster.histogram` | Per-band 256-bin histograms (`gdalinfo -hist`). | `bands` |
| `raster.zonal-statistics` | Zonal aggregates over inline polygon zones. | `zones` (inline GeoJSON, required), `band`, `statistics` (count/sum/mean/min/max/stddev/variance) |
| `gdal.gdalwarp` | Full PROJ-backed raster reprojection, including datum shifts the managed path rejects. | `source`, `targetSrs`, `sourceSrs` |

## Conversion (5)

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `conversion.geometry-format` | Convert a geometry to WKT, GeoJSON, WKB, or EWKT. | `geometry`, `target` |
| `conversion.feature-project` | Reproject every feature in a layer. | `layerId`, `targetSrid` |
| `conversion.raster-format` | Raster format conversion (GTiff/PNG/JPEG/COG). **Validation-only today — no executor is wired.** | `layerId`, `rasterId`, `targetFormat`, `compression` |
| `conversion.raster-reproject` | Raster CRS conversion. **Validation-only today — no executor is wired.** | `layerId`, `rasterId`, `targetSrid`, `resampling` |
| `gdal.ogr2ogr` | Vector format conversion via the GDAL worker (*native*). Target drivers: GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile. | `source`, `targetFormat`, `sourceFormat` |

## Generalization (2)

Layer-level counterparts of the geometry-scoped operations; accept the shared GeoServices filter parameters.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `generalization.simplify-layer` | Topology-aware simplification across a layer; tolerance in the layer's SR units. | `layerId`, `tolerance`, `preserveTopology` |
| `generalization.dissolve` | Union features by attribute group with optional aggregates (no buffer). | `layerId`, `groupByFields`, `dissolve`, `outStatistics` |

## Data management (3)

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `data-management.copy-features` | Copy (optionally filtered) features into a new layer; non-destructive. | `sourceLayerId`, `targetLayerName`, `where`, `objectIds` |
| `data-management.delete-features` | Delete matching features. **Destructive — requires approval**; `where` or `objectIds` is mandatory. | `layerId`, `where`, `objectIds` |
| `data-management.calculate-field` | Set a field via constant or allow-listed SQL expression. **Destructive — requires approval.** | `layerId`, `fieldName`, `expression`, `where`, `objectIds` |

## Transform (8)

GeoETL transform nodes: read a FeatureCollection data URI on `input`, emit a FeatureCollection data URI. Managed NetTopologySuite only.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `transform.attribute-rename` | Rename an attribute key on every feature. | `from`, `to` |
| `transform.attribute-cast` | Coerce an attribute to int/long/double/bool/string. | `field`, `to`, `onError` (drop/null/keep) |
| `transform.computed-field` | Derive a new attribute (concat, add, subtract, multiply, divide, const). | `target`, `op`, `fields`, `left`, `right`, `value` |
| `transform.attribute-filter` | Keep features matching a comparison (eq/neq/gt/gte/lt/lte/contains/exists). | `field`, `op`, `value` |
| `transform.spatial-filter` | Keep features intersecting/within a bbox or WKT region. | `bbox` or `wkt`, `predicate` |
| `transform.clip` | Clip feature geometries to a region, dropping features outside it. | `bbox` or `wkt` |
| `transform.dedup` | Keep the first feature per distinct attribute and/or geometry key. | `keys`, `geometry` |
| `transform.reproject` | Managed reprojection (4326 ↔ Web Mercator and aliases); datum-shift pairs are rejected — use `gdal.gdalwarp`. | `fromSrid`, `toSrid` |

## Source (2)

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `source.geojson` | Parse inline GeoJSON (or a data URI) into the standard FeatureCollection artifact. | `inline` or `input` |
| `source.csv` | Parse inline CSV; geometry from a WKT column or lon/lat pair. | `inline`, `delimiter` |

Native-format sources (shapefile, GeoPackage) are not catalog sources today; convert with `gdal.ogr2ogr` first.

## Sink (3)

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `sink.geojson-file` | Write the input FeatureCollection to a GeoJSON file under the configured output root. | `input`, `path` |
| `sink.quarantine` | Dead-letter sink: write rejected rows with batch id and reason. | `input`, `path`, `reasonField`, `batchId` |
| `sink.external-postgis` | Load features into a customer-owned PostGIS database via a registered secure connection (never a raw connection string). | `input`, `connectionName`/`connectionId`, `table`, `targetSrid`, `schema`, `geometryColumn`, `batchSize` |

A sink that writes back into the Honua catalog itself is deferred.

## Related pages

- [Run geoprocessing guide](../guides/query-analyze/run-geoprocessing.md)
- [OGC APIs reference](protocols/ogc-apis.md)
- [gRPC ProcessService](protocols/grpc.md)
