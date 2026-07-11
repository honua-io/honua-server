# Geoprocessing operations

Catalog of the built-in geoprocessing processes (process catalog `honua.process_catalog.builtin.v1`). The same catalog is exposed through three surfaces: OGC API Processes (`/ogc/processes/processes`), the ArcGIS-compatible GPServer adapter (`/rest/services/{serviceId}/GPServer`), and gRPC `geospatial.v1.ProcessService`. For a submit/poll/fetch walkthrough see [run geoprocessing](../guides/query-analyze/run-geoprocessing.md); to write your own process, see [author a geoprocessing process](../guides/query-analyze/gp-devkit-authoring.md).

The catalog currently registers **96 processes** across 14 families. This page is regenerated from `src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs`; a catalog-vs-doc parity test (`tests/dotnet/Honua.Architecture.Tests/GeoprocessingCatalogDocParityTests.cs`) fails the build if a registered process id is missing here, and asserts this prose count matches the catalog.

Execution notes that apply across families:

- **Runtime profile.** Processes marked *native* below declare `RuntimeProfile = native` and execute out-of-process in the heavyweight GDAL/PDAL worker image; the lean GDAL-free serving image validates their plans (parameter shape + per-process semantic rules) but never executes them. **A deployment without the GDAL worker cannot run any native process** — all `surface.*`, all `raster.*`, the native `conversion.*` (raster/OGR/point-cloud) idioms, `proximity.euclidean-*`, `source.ogr`, `gdal.*`, and `pcloud.translate`. 30 of the 96 processes are native.
- **Flagged / unsupported.** One native process is advertised so callers can *discover* the capability gap but **fails with a clear message when submitted** rather than silently substituting a different algorithm: `raster.interpolate-kriging` (no kriging backend in the worker image — use `raster.interpolate-idw`).
- **Raster sourcing.** Native raster/surface processes read the raster as base64-encoded GeoTIFF bytes on the `source` parameter; `layerId`/`rasterId` selectors are declared but layer-resolved sourcing is a follow-on and `source` remains required today.
- **Inline FeatureCollections.** Managed `*-managed`, `overlay.*`, `proximity.near*`, `statistics.*`, `transform.*`, `source.*`, and `sink.*` processes exchange features as `data:application/geo+json;base64` data URIs so they compose as workflow nodes.
- **Approval gate.** `data-management.delete-features` and `data-management.calculate-field` are destructive and require operator approval.
- **Admission.** Submissions pass through admission control (`ExecutionAdmission__*` — see [environment variables](configuration/environment-variables.md#admission-and-pooling)).

## Geometry (14)

Single-geometry operations; inputs are base64-encoded WKB plus an SRID. Managed (NetTopologySuite/PostGIS-equivalent).

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `geometry.buffer` | Polygon at a distance around the input geometry (planar; distance in the input CRS's coordinate units). `geodesic=true` is rejected at plan validation. | `wkb`, `srid`, `distance` (CRS units, > 0), `geodesic` |
| `geometry.simplify` | Douglas-Peucker simplification, topology-preserving by default. | `wkb`, `srid`, `tolerance`, `preserveTopology` |
| `geometry.project` | Reproject between spatial references. | `wkb`, `fromSrid`, `toSrid` |
| `geometry.make-valid` | Repair self-intersections, duplicate vertices, ring orientation. | `wkb`, `srid` |
| `geometry.union` | Union of multiple geometries. | `wkbs`, `srid` |
| `geometry.intersect` | Intersection of two geometries. | `targetWkb`, `intersectorWkb`, `srid` |
| `geometry.clip` | Clip to the bounding envelope of a clip geometry. | `targetWkb`, `clipEnvelopeWkb`, `srid` |
| `geometry.difference` | Subtract an eraser geometry. | `targetWkb`, `eraserWkb`, `srid` |
| `geometry.area` | Planar area of a polygon in squared CRS units (no geodesic conversion). | `wkb`, `srid` |
| `geometry.length` | Planar length of a line (polygon perimeter) in CRS units (no geodesic conversion). | `wkb`, `srid` |
| `geometry.centroid` | Centroid point. | `wkb`, `srid` |
| `geometry.convex-hull` | Convex hull (PostGIS `ST_ConvexHull` semantics). | `wkb`, `srid` |
| `geometry.dissolve` | Union by optional group key, one feature per group. | `wkbs`, `srid`, `groupKeys` |
| `geometry.snap` | Snap vertices to a reference geometry within a tolerance. | `wkb`, `referenceWkb`, `srid`, `tolerance` |

## Analytics (9)

The layer-scoped processes (`analytics.cluster`, `analytics.spatial-join`, `analytics.buffer-aggregate`, `analytics.density`) run synchronously against PostGIS-backed layers and are **not job-dispatchable**; each has a job-executable `*-managed` counterpart that runs in managed code (NetTopologySuite) over inline FeatureCollections. The layer-scoped processes accept the shared GeoServices filter parameters (`where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `time`, `timeRelation`). For geographic layers the meter-based parameters (`eps`, `distance`, `cellSize`) are evaluated after transforming to EPSG:3857 (Web Mercator), where distances overstate ground distance by 1/cos(latitude). Managed-counterpart distances are evaluated in CRS units (no geodesic conversion).

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `analytics.cluster` | DBSCAN or K-Means clustering on a layer (synchronous, PostGIS). | `layerId`, `algorithm`, `eps`, `minPoints`, `k`, `returnHullPerCluster`, `outStatistics` |
| `analytics.spatial-join` | Enrich target features from a join layer by spatial predicate (synchronous, PostGIS). | `layerId`, `joinLayerId`, `predicate`, `distance`, `carryFields`, `outStatistics` |
| `analytics.buffer-aggregate` | Buffer and optionally dissolve per group with statistics (synchronous, PostGIS). | `layerId`, `distance`, `unit`, `dissolve`, `groupByFields`, `outStatistics` |
| `analytics.density` | Hex or square grid binning with counts or weighted sums (synchronous, PostGIS). | `layerId`, `mode`, `cellSize` (m), `weightField` |
| `analytics.cluster-managed` | Job-executable DBSCAN/K-Means over inline features; appends `CLUSTER_ID`. | `input`, `algorithm`, `eps`, `minPoints`, `k` |
| `analytics.spatial-join-managed` | Job-executable join over two inline FeatureCollections; `JOIN_COUNT` plus sum/mean/min/max aggregates. | `input`, `join`, `predicate`, `statistics` |
| `analytics.buffer-aggregate-managed` | Job-executable buffer-and-dissolve over inline features. | `input`, `distance`, `unit`, `dissolve`, `groupByFields` |
| `analytics.density-managed` | Job-executable hex/square binning over inline features. | `input`, `mode`, `cellSize`, `weightField` |
| `analytics.hotspot-managed` | Job-executable Getis-Ord Gi* Hot Spot Analysis over inline features; appends `GI_ZSCORE`, `GI_PVALUE`, `GI_BIN`. | `input`, `field`, `distanceBand` |

## Overlay (6)

Managed (NetTopologySuite) overlay ops over **two inline FeatureCollections** addressed by data URI — the layer-aware counterparts of the single-WKB `geometry.*` primitives, covering the Esri Clip/Intersect/Union/Erase/Merge/Split toolset. No Postgres or GDAL dependency.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `overlay.clip` | Truncate input geometries to the clip layer's union (Esri Clip; also layer-level Extract). | `input`, `clip` |
| `overlay.intersect` | Pairwise intersection of input × overlay, merging attributes (Esri Intersect). | `input`, `overlay` |
| `overlay.union` | Planar union: input-only, overlay-only, and intersection pieces (Esri Union). | `input`, `overlay` |
| `overlay.erase` | Subtract the erase layer's union from each input geometry (Esri Erase). | `input`, `erase` |
| `overlay.merge` | Concatenate two FeatureCollections into a union-schema output (Esri Merge). | `input`, `merge` |
| `overlay.split` | Partition the input, tagging each output with `SPLIT_TARGET` (Esri Split). | `input`, `split`, `splitField` |

## Proximity (4)

Nearest-feature and Euclidean-distance tools.

| Process ID | Description | Key parameters | Profile |
| --- | --- | --- | --- |
| `proximity.near` | Append `NEAR_FID`/`NEAR_DIST` for the closest near-layer feature (Esri Near). Planar CRS units. | `input`, `near`, `nearIdField`, `searchRadius` | managed |
| `proximity.near-table` | Emit a table of `IN_FID`/`NEAR_FID`/`NEAR_DIST` rows (Esri GenerateNearTable). | `input`, `near`, `inputIdField`, `nearIdField`, `searchRadius` | managed |
| `proximity.euclidean-distance` | Raster of distance from each cell to the nearest source cell (`gdal_proximity.py`). | `source` (base64 GeoTIFF), `maxDistance`, `distUnits`, `values` | **native** |
| `proximity.euclidean-allocation` | Nearest-source allocation raster (discrete Voronoi): each cell takes the value/id of its nearest source cell. Custom worker step (`gdal_euclidean_allocation.py`, GDAL bindings + SciPy distance transform) since stock `gdal_proximity` computes distance only. | `source` (base64 GeoTIFF), `maxDistance`, `distUnits`, `values` | **native** |

## Statistics (3)

Managed table-producing aggregates over a single inline FeatureCollection. Table outputs are null-geometry FeatureCollections (one feature per row).

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `statistics.summarize` | Per-group summary stats over `caseFields` (Esri Summary Statistics); `FREQUENCY` plus SUM/MEAN/MIN/MAX/STDDEV. | `input`, `caseFields`, `statistics` |
| `statistics.frequency` | Count of each distinct `frequencyFields` combination (Esri Frequency); optional `SUM_<field>`. | `input`, `frequencyFields`, `summaryFields` |
| `statistics.calculate` | Descriptive stats (COUNT/MIN/MAX/MEAN/SUM/STDDEV) per requested field across the dataset. | `input`, `fields` |

## Generalization (2)

Layer-level counterparts of the geometry-scoped operations; accept the shared GeoServices filter parameters.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `generalization.simplify-layer` | Topology-aware simplification across a layer; tolerance in the layer's SR units. | `layerId`, `tolerance`, `preserveTopology` |
| `generalization.dissolve` | Union features by attribute group with optional aggregates (no buffer). | `layerId`, `groupByFields`, `dissolve`, `outStatistics` |

## Surface analysis (8) — native

DEM-derived raster products executed by the GDAL worker via `gdaldem` / `gdal_contour` / `gdal_viewshed`. All take `source` (base64 GeoTIFF DEM). **Require the GDAL worker.**

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `surface.slope` | Slope raster. | `units` (degrees/percent), `zFactor` |
| `surface.aspect` | Compass-bearing aspect raster. | `source` |
| `surface.hillshade` | Hillshade raster. | `azimuth` (315), `altitude` (45), `zFactor` |
| `surface.rugosity-tri` | Terrain ruggedness index (3×3 window only). | `windowRadius` (must be 1) |
| `surface.rugosity-tpi` | Topographic position index (3×3 window only). | `windowRadius` (must be 1) |
| `surface.roughness` | Roughness raster (3×3 window only). | `windowRadius` (must be 1) |
| `surface.contour` | Contour lines from a DEM; GeoJSON with an `ELEV` attribute. | `interval` (required), `base` |
| `surface.viewshed` | Binary visibility raster from a DEM and observer location. | `observerX`, `observerY`, `observerHeight`, `targetHeight`, `maxDistance` |

## Raster (13) — native

Raster analysis and mutation executed by the GDAL worker via `gdalwarp` / `gdalinfo` / `gdal_grid` / `gdal_calc.py`. All take `source` (base64 GeoTIFF) unless noted. **Require the GDAL worker.**

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `raster.clip` | Clip to a boundary geometry (`gdalwarp -cutline`). | `boundary` (WKB), `boundarySrid` |
| `raster.reproject` | Reproject with a resampling algorithm (`gdalwarp -t_srs`). | `targetSrid`, `resampling` |
| `raster.statistics` | Per-band min/max/mean/stddev (`gdalinfo -stats`). | `bands` |
| `raster.histogram` | Per-band 256-bin histograms (`gdalinfo -hist`). | `bands` |
| `raster.zonal-statistics` | Zonal aggregates over inline polygon zones (per-zone clip + `gdalinfo`). | `zones` (inline GeoJSON, required), `zonesLayerId` (reserved), `band`, `statistics` |
| `raster.resample` | Change cell size with a resampling algorithm (`gdalwarp -tr`). | `cellSize`, `cellSizeY`, `resampling` |
| `raster.interpolate-idw` | Inverse-distance-weighted surface from scattered points (`gdal_grid -a invdist`). | `points`, `zField`, `power`, `smoothing`, `radius`, `width`, `height` |
| `raster.interpolate-kriging` | **FLAGGED / UNSUPPORTED** — no kriging backend in the worker image; submitted jobs fail with a clear message. Use `raster.interpolate-idw`. | `points`, `zField` |
| `raster.mosaic` | Combine multiple rasters (`gdalwarp`); `first`/`last` overlap only. | `sources` (`|`-separated), `operator`, `resampling` |
| `raster.map-algebra` | Allow-listed arithmetic/logical expression over band variables (`gdal_calc.py`). | `sources` (`|`-separated), `expression`, `dataType` |
| `raster.spectral-index` | Named spectral index (NDVI/NDWI/NDBI/SAVI/EVI) compiled to map-algebra. | `index`, `red`, `nir`, `green`, `swir`, `blue`, `L` |
| `raster.reclassify` | Remap pixel values per a remap table (`gdal_calc.py`). | `remap`, `defaultValue`, `dataType` |
| `gdal.gdalwarp` | Full PROJ-backed raster reprojection, including datum shifts the managed path rejects. | `source`, `targetSrs`, `sourceSrs` |

## Conversion (8)

Explicit format/CRS conversion idioms. Two are managed; the rest run in the GDAL/PDAL worker (*native*).

| Process ID | Description | Key parameters | Profile |
| --- | --- | --- | --- |
| `conversion.geometry-format` | Convert a geometry to WKT, GeoJSON, WKB, or EWKT. | `geometry`, `target` | managed |
| `conversion.feature-project` | Reproject every feature in a layer. | `layerId`, `targetSrid` | managed |
| `conversion.raster-format` | Raster format conversion (GTiff/PNG/JPEG/COG) via real GDAL `gdal_translate` (#2138). | `source`, `targetFormat`, `compression` | **native** |
| `conversion.raster-reproject` | Raster CRS conversion via real GDAL `gdalwarp` (#2138). | `source`, `targetSrid`, `resampling` | **native** |
| `conversion.polygonize` | Vectorize a raster into polygons per connected region (`gdal_polygonize.py`). | `source`, `band`, `connectedness`, `fieldName` | **native** |
| `conversion.rasterize` | Burn vector features into a new raster grid (`gdal_rasterize`). | `source`, `burnValue`/`attribute`, `cellSize`/`width`+`height`, `nodata` | **native** |
| `gdal.ogr2ogr` | Vector format conversion via the GDAL worker. Target drivers: GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile. | `source`, `targetFormat`, `sourceFormat` | **native** |
| `pcloud.translate` | Decompress LAZ/COPC to uncompressed LAS, optionally reprojecting to EPSG:4979 (`pdal translate`). | `source`, `sourceSrs` | **native** |

## Data management (4)

Bulk, layer-level mutation workflows. `delete-features` and `calculate-field` are destructive and route through the approval gate.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `data-management.copy-features` | Copy (optionally filtered) features into a new layer; non-destructive. | `sourceLayerId`, `targetLayerName`, `where`, `objectIds` |
| `data-management.append` | Append a source FeatureCollection into the target's schema (Esri Append), with optional `fieldMap`. | `input` (target), `append` (source), `fieldMap` |
| `data-management.delete-features` | Delete matching features. **Destructive — requires approval**; `where` or `objectIds` is mandatory. | `layerId`, `where`, `objectIds` |
| `data-management.calculate-field` | Set a field via constant or allow-listed SQL expression. **Destructive — requires approval.** | `layerId`, `fieldName`, `expression`, `where`, `objectIds` |

## Import (1)

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `import.dataset` | End-to-end durable import: stage → import/chunk → flatten to a typed layer → optional raster tiling → extent/MVT refresh → provenance. The canonical execution layer the geospatial-mcp `publish_data` family submits into. Managed. | `connection`, `sourcePath`, `fileName`, `tableName`, `layerName`, `serviceName`, `targetSrid`, `rasterLayerId`, … |

## Transform (12)

GeoETL transform nodes: read a FeatureCollection data URI on `input`, emit a FeatureCollection data URI. Managed NetTopologySuite only — no GDAL.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `transform.attribute-rename` | Rename an attribute key on every feature. | `from`, `to` |
| `transform.attribute-cast` | Coerce an attribute to int/long/double/bool/string. | `field`, `to`, `onError` (drop/null/keep) |
| `transform.computed-field` | Derive a new attribute via a fixed op set (concat/add/…) or a sandboxed, AOT-safe expression engine. | `target`, `op`, `expression`, `fields`, `left`, `right`, `value` |
| `transform.attribute-filter` | Keep features matching a comparison (eq/neq/gt/gte/lt/lte/contains/exists). | `field`, `op`, `value` |
| `transform.attribute-join` | Hash-join to a right FeatureCollection on key columns, carrying selected fields (inner/left). | `input`, `right`, `leftKeys`, `rightKeys`, `fields`, `type`, `prefix` |
| `transform.aggregate` | Group-by aggregate (count/sum/min/max/mean/stddev/first/collect) with optional geometry reduction. | `input`, `groupBy`, `aggregates`, `geometry` |
| `transform.pivot` | Reshape long → wide: `pivotField` values become columns sourced from `valueField`. | `groupBy`, `pivotField`, `valueField`, `prefix` |
| `transform.unpivot` | Melt wide → long: one output feature per `fields` column. | `fields`, `keep`, `nameField`, `valueField`, `dropNulls` |
| `transform.spatial-filter` | Keep features intersecting/within a bbox or WKT region. | `bbox` or `wkt`, `predicate` |
| `transform.clip` | Clip feature geometries to a region, dropping features outside it. | `bbox` or `wkt` |
| `transform.dedup` | Keep the first feature per distinct attribute and/or geometry key. | `keys`, `geometry` |
| `transform.reproject` | Managed reprojection (4326 ↔ Web Mercator and aliases); datum-shift pairs are rejected — use `gdal.gdalwarp`. | `fromSrid`, `toSrid` |

## Source (8)

GeoETL DAG sources that produce a FeatureCollection artifact. Seven are managed parsers/connectors; `source.ogr` runs in the GDAL worker (*native*).

| Process ID | Description | Key parameters | Profile |
| --- | --- | --- | --- |
| `source.geojson` | Parse inline GeoJSON (or a data URI) into the standard FeatureCollection. | `inline` or `input` | managed |
| `source.csv` | Parse inline CSV; geometry from a WKT column or lon/lat pair. | `inline`, `delimiter` | managed |
| `source.honua-layer` | Stream a Honua catalog layer through the canonical query pipeline. | `layerId`, `where`, `bbox`, `outFields`, `outSrid`, `since`, `watermarkField` | managed |
| `source.esri-featureserver` | Stream an ArcGIS FeatureServer/MapServer layer (paged), Esri-JSON → GeoJSON. | `serviceUrl`, `esriLayerId`, `where`, `outFields`, `outSrid`, `pageSize`, token/credential params, `since` | managed |
| `source.ogc-features` | Stream an OGC API Features collection via `rel=next` link paging; `where` is CQL2-text. | `serviceUrl`, `collectionId`, `where`, `bbox`, `pageSize`, credential params, `since` | managed |
| `source.wfs` | Stream a WFS GetFeature endpoint with startIndex/count paging, GeoJSON output. | `serviceUrl`, `typeName`, `bbox`, `pageSize`, credential params | managed |
| `source.postgis` | Stream a customer-owned PostGIS table/view via a registered secure connection. | `connectionName`/`connectionId`, `table`, `schema`, `geometryColumn`, `where`, `bbox`, `outSrid`, `since`, `watermarkField` | managed |
| `source.ogr` | GDAL/OGR import reader for the full driver universe (FileGDB, GML, KML, MapInfo, Shapefile, GPKG, FlatGeobuf, …); multi-file datasets supplied as a base64 ZIP. | `source`, `sourceFormat` | **native** |

## Sink (4)

Terminate a workflow by writing the input FeatureCollection and emitting a result descriptor (target location + row counts). Managed writers / Npgsql only — no GDAL.

| Process ID | Description | Key parameters |
| --- | --- | --- |
| `sink.geojson-file` | Write the input FeatureCollection to a GeoJSON file under the configured output root. | `input`, `path` |
| `sink.quarantine` | Dead-letter sink: write rejected rows with batch id and reason. | `input`, `path`, `reasonField`, `batchId` |
| `sink.external-postgis` | Load features into a customer-owned PostGIS database via a registered secure connection (never a raw connection string). | `input`, `connectionName`/`connectionId`, `table`, `targetSrid`, `schema`, `geometryColumn`, `batchSize` |
| `sink.honua-layer` | Load features into a named layer in the Honua catalog database (append/replace/upsert). Executed by `HonuaLayerSinkExecutor` through the optional `IHonuaLayerSink` capability (#2210); **fails closed with a clear "unavailable in this deployment" message in lean, database-free deployments**. | `input`, `layer`, `targetSrid`, `loadMode`, `keyFields`, `schema`, `geometryColumn`, `batchId` |

Native-format sinks (shapefile, GeoPackage) are deferred to the GDAL worker stream; for those, convert with `gdal.ogr2ogr`.

## Related pages

- [Run geoprocessing guide](../guides/query-analyze/run-geoprocessing.md)
- [OGC APIs reference](protocols/ogc-apis.md)
- [gRPC ProcessService](protocols/grpc.md)
