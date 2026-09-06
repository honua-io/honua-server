# Geoprocessing operations

Catalog of the built-in geoprocessing processes (process catalog `honua.process_catalog.builtin.v1`). Protocol adapters consume the catalog's execution-capability metadata instead of maintaining their own callable-process lists: OGC API Processes (`/ogc/processes/processes`) projects job processes and honors their advertised synchronous/asynchronous modes, the ArcGIS-compatible GPServer adapter (`/rest/services/{serviceId}/GPServer`) derives synchronous execution from the declared modes, and MCP exposes the complete classification. For a submit/poll/fetch walkthrough see [run geoprocessing](../guides/query-analyze/run-geoprocessing.md); to write your own process, see [author a geoprocessing process](../guides/query-analyze/gp-devkit-authoring.md).

The catalog currently registers **98 processes** across 15 families. This page is regenerated from `src/Honua.Geoprocessing/Features/Geoprocessing/BuiltInProcessCatalog.cs`; a catalog-vs-doc parity test (`tests/dotnet/Honua.Architecture.Tests/GeoprocessingCatalogDocParityTests.cs`) fails the build if a registered process id is missing here, and asserts this prose count matches the catalog.

Execution notes that apply across families:

- **Entry points.** GA is defined per entry point: the whole catalog is GA, and every entry declares which entry points it is callable through. The **Entry points** column on every table below is that declaration, and it is the same value the runtime projects (`ProcessDefinition.SupportedEntryPoints`, surfaced as `entryPoints` on the MCP process catalog resource).
  - `job` — the shared geoprocessing job runtime: OGC API Processes (`/ogc/processes/processes`), WPS 2.0, the GPServer adapter (`/rest/services/{serviceId}/GPServer`), and the MCP process tools. **80 entries.**
  - `protocol` — the operation's owning synchronous protocol endpoint (FeatureServer edit/maintenance routes, the spatial-analytics routes). **6 entries**, and they are deliberately absent from the job surfaces above.
  - `workflow` — composition inside a workflow DAG (`process:<processId>` nodes). Every job entry is also workflow-composable, because a workflow node compiles into the same analysis-plan step the job runtime executes. **92 entries.**

  An advertisement surface offers an operation only on the entry points it declares, which `ProcessEntryPointAdvertisementTests` enforces. Nothing is advertised that cannot be called there: there is no advertised-but-unexecutable state.
- **Execution classification.** Every entry is classified exactly once: **80 Job**, **6 ProtocolOnly**, and **12 WorkflowOnly**. Job entries declare asynchronous execution and may additionally declare synchronous execution. ProtocolOnly entries remain callable only through their owning protocol endpoint; WorkflowOnly sources and sinks compose inside DAGs but cannot be submitted directly.
- **OGC execution negotiation.** OGC requests run synchronously by default when the selected process advertises `sync-execute`. Send `Prefer: respond-async` to request durable asynchronous admission (`201`, `Location`, and `Preference-Applied: respond-async`). `respond-sync` is not a defined preference. WKB parameters accept the existing base64 WKB string or a GeoJSON Geometry, Feature, or FeatureCollection; GeoJSON is normalized through the shared geometry codec. Catalog requests may use `"response": "raw"` with synchronous or asynchronous execution: one selected output returns its native representation, and multiple outputs return `multipart/related`. Document mode is the default and returns inline qualified values. Qualified inputs and bounded public HTTPS or data-URI `href` references are normalized to the catalog parameter format. The canonical `honua-geoprocessing` plan process requires document mode and retains its artifact document.
- **Runtime profile.** Processes marked *native* below declare `RuntimeProfile = native` and execute out-of-process in the heavyweight GDAL/PDAL worker image; the lean GDAL-free serving image validates their plans (parameter shape + per-process semantic rules) but never executes them. **A deployment without the GDAL worker cannot run any native process** — all `surface.*`, all `raster.*`, the native `conversion.*` (raster/OGR/point-cloud) idioms, `proximity.euclidean-*`, `source.ogr`, `gdal.*`, and `pcloud.translate`. 30 of the 98 processes are native.
- **Delegated cloud inference.** `imagery.classify` delegates ML inference to a configured cloud endpoint (`Geoprocessing:ImageryInference`) — Honua bundles no model runtime. When no backend is configured the process stays advertised but every execution fails with a clear "no cloud inference backend is configured" message (no silent stub, no fake result).
- **Raster sourcing.** Native raster/surface processes read the raster as base64-encoded GeoTIFF bytes on the `source` parameter; `layerId`/`rasterId` selectors are declared but layer-resolved sourcing is a follow-on and `source` remains required today.
- **Inline FeatureCollections.** Managed `*-managed`, `overlay.*`, `proximity.near*`, `statistics.*`, `transform.*`, `source.*`, and `sink.*` processes exchange features as `data:application/geo+json;base64` data URIs so they compose as workflow nodes.
- **Approval gate.** `data-management.delete-features` and `data-management.calculate-field` are destructive and require operator approval.
- **Deferred role revalidation.** Approval resumes re-resolve current roles for identities managed by the configured membership source and fail closed when the submitter is inactive or lost required membership. For identity-provider modes that cannot answer membership queries, the durable submitter snapshot remains authoritative with an operator warning; resubmit pending approvals after revoking roles in that mode.
- **Admission.** Submissions pass through admission control (`ExecutionAdmission__*` — see [environment variables](configuration/environment-variables.md#admission-and-pooling)).

## Job ownership and tenant scope

Job lookup, listing, result retrieval, and cancellation require the effective request
tenant to match the tenant recorded at submission. A matching subject or display name
in another tenant does not grant access. The `admin` role can manage other owners'
jobs within the effective tenant; it does not bypass this tenant boundary. Requests
for another tenant's job return not found, and listings omit those jobs.

Jobs without a recorded tenant remain accessible only from an unscoped request,
subject to the existing owner and operator permissions. Tenant-scoped callers must
resubmit legacy jobs whose submission did not record a tenant.

## Geometry (14)

Single-geometry operations; inputs are base64-encoded WKB plus an SRID. Managed (NetTopologySuite/PostGIS-equivalent).

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `geometry.buffer` | Polygon at a distance around the input geometry (planar; distance in the input CRS's coordinate units). `geodesic=true` is rejected at plan validation. | `wkb`, `srid`, `distance` (CRS units, > 0), `geodesic` | job, workflow |
| `geometry.simplify` | Douglas-Peucker simplification, topology-preserving by default. | `wkb`, `srid`, `tolerance`, `preserveTopology` | job, workflow |
| `geometry.project` | Reproject between spatial references. | `wkb`, `fromSrid`, `toSrid` | job, workflow |
| `geometry.make-valid` | Repair self-intersections, duplicate vertices, ring orientation. | `wkb`, `srid` | job, workflow |
| `geometry.union` | Union of multiple geometries. | `wkbs`, `srid` | job, workflow |
| `geometry.intersect` | Intersection of two geometries. | `targetWkb`, `intersectorWkb`, `srid` | job, workflow |
| `geometry.clip` | Clip to the bounding envelope of a clip geometry. | `targetWkb`, `clipEnvelopeWkb`, `srid` | job, workflow |
| `geometry.difference` | Subtract an eraser geometry. | `targetWkb`, `eraserWkb`, `srid` | job, workflow |
| `geometry.area` | Planar area of a polygon in squared CRS units (no geodesic conversion). | `wkb`, `srid` | job, workflow |
| `geometry.length` | Planar length of a line (polygon perimeter) in CRS units (no geodesic conversion). | `wkb`, `srid` | job, workflow |
| `geometry.centroid` | Centroid point. | `wkb`, `srid` | job, workflow |
| `geometry.convex-hull` | Convex hull (PostGIS `ST_ConvexHull` semantics). | `wkb`, `srid` | job, workflow |
| `geometry.dissolve` | Union by optional group key, one feature per group. | `wkbs`, `srid`, `groupKeys` | job, workflow |
| `geometry.snap` | Snap vertices to a reference geometry within a tolerance. | `wkb`, `referenceWkb`, `srid`, `tolerance` | job, workflow |

## Analytics (9)

The layer-scoped cluster and density processes (`analytics.cluster`, `analytics.density`) run synchronously against PostGIS-backed layers and are **ProtocolOnly**; their `*-managed` counterparts run as managed jobs over inline FeatureCollections. `analytics.spatial-join` and `analytics.buffer-aggregate` have layer-aware job executors in addition to their inline `*-managed` counterparts. Layer-scoped processes accept the shared GeoServices filter parameters (`where`, `objectIds`, `geometry`, `geometryType`, `inSR`, `spatialRel`, `time`, `timeRelation`). For geographic layers the meter-based parameters (`eps`, `distance`, `cellSize`) are evaluated after transforming to EPSG:3857 (Web Mercator), where distances overstate ground distance by 1/cos(latitude). Managed-counterpart distances are evaluated in CRS units (no geodesic conversion).

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `analytics.cluster` | DBSCAN or K-Means clustering on a layer (synchronous, PostGIS). | `layerId`, `algorithm`, `eps`, `minPoints`, `k`, `returnHullPerCluster`, `outStatistics` | protocol |
| `analytics.spatial-join` | Enrich target features from a join layer by spatial predicate (synchronous, PostGIS). | `layerId`, `joinLayerId`, `predicate`, `distance`, `carryFields`, `outStatistics` | job, workflow |
| `analytics.buffer-aggregate` | Buffer and optionally dissolve per group with statistics (synchronous, PostGIS). | `layerId`, `distance`, `unit`, `dissolve`, `groupByFields`, `outStatistics` | job, workflow |
| `analytics.density` | Hex or square grid binning with counts or weighted sums (synchronous, PostGIS). | `layerId`, `mode`, `cellSize` (m), `weightField` | protocol |
| `analytics.cluster-managed` | Job-executable DBSCAN/K-Means over inline features; appends `CLUSTER_ID`. | `input`, `algorithm`, `eps`, `minPoints`, `k` | job, workflow |
| `analytics.spatial-join-managed` | Job-executable join over two inline FeatureCollections; `JOIN_COUNT` plus sum/mean/min/max aggregates. | `input`, `join`, `predicate`, `statistics` | job, workflow |
| `analytics.buffer-aggregate-managed` | Job-executable buffer-and-dissolve over inline features. | `input`, `distance`, `unit`, `dissolve`, `groupByFields` | job, workflow |
| `analytics.density-managed` | Job-executable hex/square binning over inline features. | `input`, `mode`, `cellSize`, `weightField` | job, workflow |
| `analytics.hotspot-managed` | Job-executable Getis-Ord Gi* Hot Spot Analysis over inline features; appends `GI_ZSCORE`, `GI_PVALUE`, `GI_BIN`. | `input`, `field`, `distanceBand` | job, workflow |

For the spatial-join processes the `predicate` is read join-subject: `contains`
means *the join geometry contains the target* (the classic point-in-polygon case)
and `within` means *the target contains the join geometry*. `intersects` and
`dwithin` are symmetric.

## Enrichment (1)

Asynchronous batch counterpart of the synchronous `POST /api/enrich` endpoint. Resolves a managed or configured enrichment dataset by `datasetId` through the same catalog the sync endpoint uses, then joins the target features against the dataset's layer with the shared managed spatial-join computation (`JOIN_COUNT`, carried attributes, `field:stat` aggregates) or annotates each target with its nearest dataset feature (`NEAR_DIST`). Targets come from EITHER a registered `layerId` (with `where`/`bbox` windowing) OR a staged inline FeatureCollection (`input` data URI). The dataset's minimum edition and the shared `analytics.spatial-join` (Pro) entitlement are enforced at execution, and the published FeatureCollection carries the dataset id and attribution as foreign members. Both layers are streamed in EPSG:4326 and the artifact is published in EPSG:4326, so a cross-SRID pair is never joined on incomparable ordinates and the GeoJSON output is valid WGS 84 (RFC 7946). Distances (`distance`, `NEAR_DIST`) are therefore in degrees — no geodesic conversion, matching the other managed analytics executors — so the sync endpoint's meters-based dataset default is not inherited. `maxInputFeatures` (default 250000, clamped to an operator ceiling of 1000000 — callers may only lower it) bounds each layer read while streaming — including a staged `input` collection — and `maxCarriedMatchValues` (default 20000000, likewise lower-only) bounds carried values across the whole operation: join methods charge every carried match value, while nearest-neighbor charges one value per output field for each annotated target.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `enrichment.enrich` | Job-executable enrichment of a layer-backed or staged feature set from a registered enrichment dataset. | `datasetId`, `layerId` \| `input`, `method`, `predicate`, `distance`, `outputFields`, `aggregates`, `where`, `bbox`, `maxInputFeatures`, `maxCarriedMatchValues` | job, workflow |

## Overlay (6)

Managed (NetTopologySuite) overlay ops over **two inline FeatureCollections** addressed by data URI — the layer-aware counterparts of the single-WKB `geometry.*` primitives, covering the Esri Clip/Intersect/Union/Erase/Merge/Split toolset. No Postgres or GDAL dependency.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `overlay.clip` | Truncate input geometries to the clip layer's union (Esri Clip; also layer-level Extract). | `input`, `clip` | job, workflow |
| `overlay.intersect` | Pairwise intersection of input × overlay, merging attributes (Esri Intersect). | `input`, `overlay` | job, workflow |
| `overlay.union` | Planar union: input-only, overlay-only, and intersection pieces (Esri Union). | `input`, `overlay` | job, workflow |
| `overlay.erase` | Subtract the erase layer's union from each input geometry (Esri Erase). | `input`, `erase` | job, workflow |
| `overlay.merge` | Concatenate two FeatureCollections into a union-schema output (Esri Merge). | `input`, `merge` | job, workflow |
| `overlay.split` | Partition the input, tagging each output with `SPLIT_TARGET` (Esri Split). | `input`, `split`, `splitField` | job, workflow |

## Proximity (4)

Nearest-feature and Euclidean-distance tools.

| Process ID | Description | Key parameters | Profile | Entry points |
| --- | --- | --- | --- | --- |
| `proximity.near` | Append `NEAR_FID`/`NEAR_DIST` for the closest near-layer feature (Esri Near). Planar CRS units. | `input`, `near`, `nearIdField`, `searchRadius` | managed | job, workflow |
| `proximity.near-table` | Emit a table of `IN_FID`/`NEAR_FID`/`NEAR_DIST` rows (Esri GenerateNearTable). | `input`, `near`, `inputIdField`, `nearIdField`, `searchRadius` | managed | job, workflow |
| `proximity.euclidean-distance` | Raster of distance from each cell to the nearest source cell (`gdal_proximity.py`). | `source` (base64 GeoTIFF), `maxDistance`, `distUnits`, `values` | **native** | job, workflow |
| `proximity.euclidean-allocation` | Nearest-source allocation raster (discrete Voronoi): each cell takes the value/id of its nearest source cell. Custom worker step (`gdal_euclidean_allocation.py`, GDAL bindings + SciPy distance transform) since stock `gdal_proximity` computes distance only. | `source` (base64 GeoTIFF), `maxDistance`, `distUnits`, `values` | **native** | job, workflow |

## Statistics (3)

Managed table-producing aggregates over a single inline FeatureCollection. Table outputs are null-geometry FeatureCollections (one feature per row).

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `statistics.summarize` | Per-group summary stats over `caseFields` (Esri Summary Statistics); `FREQUENCY` plus SUM/MEAN/MIN/MAX/STDDEV. | `input`, `caseFields`, `statistics` | job, workflow |
| `statistics.frequency` | Count of each distinct `frequencyFields` combination (Esri Frequency); optional `SUM_<field>`. | `input`, `frequencyFields`, `summaryFields` | job, workflow |
| `statistics.calculate` | Descriptive stats (COUNT/MIN/MAX/MEAN/SUM/STDDEV) per requested field across the dataset. | `input`, `fields` | job, workflow |

## Generalization (2)

Layer-level counterparts of the geometry-scoped operations; accept the shared GeoServices filter parameters.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `generalization.simplify-layer` | Topology-aware simplification across a layer; tolerance in the layer's SR units. | `layerId`, `tolerance`, `preserveTopology` | job, workflow |
| `generalization.dissolve` | Union features by attribute group with optional aggregates (no buffer). | `layerId`, `groupByFields`, `dissolve`, `outStatistics` | job, workflow |

## Surface analysis (8) — native

DEM-derived raster products executed by the GDAL worker via `gdaldem` / `gdal_contour` / `gdal_viewshed`. All take `source` (base64 GeoTIFF DEM). **Require the GDAL worker.**

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `surface.slope` | Slope raster. | `units` (degrees/percent), `zFactor` | job, workflow |
| `surface.aspect` | Compass-bearing aspect raster. | `source` | job, workflow |
| `surface.hillshade` | Hillshade raster. | `azimuth` (315), `altitude` (45), `zFactor` | job, workflow |
| `surface.rugosity-tri` | Terrain ruggedness index (3×3 window only). | `windowRadius` (must be 1) | job, workflow |
| `surface.rugosity-tpi` | Topographic position index (3×3 window only). | `windowRadius` (must be 1) | job, workflow |
| `surface.roughness` | Roughness raster (3×3 window only). | `windowRadius` (must be 1) | job, workflow |
| `surface.contour` | Contour lines from a DEM; GeoJSON with an `ELEV` attribute. | `interval` (required), `base` | job, workflow |
| `surface.viewshed` | Binary visibility raster from a DEM and observer location. | `observerX`, `observerY`, `observerHeight`, `targetHeight`, `maxDistance` | job, workflow |

## Raster (13) — native

Raster analysis and mutation executed by the GDAL worker via `gdalwarp` / `gdalinfo` / `gdal_grid` / `gdal_calc.py`. All take `source` (base64 GeoTIFF) unless noted. **Require the GDAL worker.**

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `raster.clip` | Clip to a boundary geometry (`gdalwarp -cutline`). | `boundary` (WKB), `boundarySrid` | job, workflow |
| `raster.reproject` | Reproject with a resampling algorithm (`gdalwarp -t_srs`). | `targetSrid`, `resampling` | job, workflow |
| `raster.statistics` | Per-band min/max/mean/stddev (`gdalinfo -stats`). | `bands` | job, workflow |
| `raster.histogram` | Per-band 256-bin histograms (`gdalinfo -hist`). | `bands` | job, workflow |
| `raster.zonal-statistics` | Zonal aggregates over inline polygon zones (per-zone clip + `gdalinfo`). | `zones` (inline GeoJSON, required), `zonesLayerId` (reserved), `band`, `statistics` | job, workflow |
| `raster.resample` | Change cell size with a resampling algorithm (`gdalwarp -tr`). | `cellSize`, `cellSizeY`, `resampling` | job, workflow |
| `raster.interpolate-idw` | Inverse-distance-weighted surface from scattered points (`gdal_grid -a invdist`). | `points`, `zField`, `power`, `smoothing`, `radius`, `width`, `height` | job, workflow |
| `raster.interpolate-kriging` | Ordinary kriging onto a raster surface under an isotropic semivariogram (the worker's own solver; stock `gdal_grid` has no kriging algorithm). Exact at the sample locations. Omitted variogram parameters are derived from the samples: total sill = sample variance, range = one third of the largest pairwise separation, nugget = 0. | `points`, `zField`, `model` (`spherical`/`exponential`/`gaussian`), `nugget`, `sill`, `range`, `srid`, `width`, `height` | job, workflow |
| `raster.mosaic` | Combine multiple rasters (`gdalwarp`); `first`/`last` overlap only. | `sources` (`|`-separated), `operator`, `resampling` | job, workflow |
| `raster.map-algebra` | Allow-listed arithmetic/logical expression over band variables (`gdal_calc.py`). | `sources` (`|`-separated), `expression`, `dataType` | job, workflow |
| `raster.spectral-index` | Named spectral index (NDVI/NDWI/NDBI/SAVI/EVI) compiled to map-algebra. | `index`, `red`, `nir`, `green`, `swir`, `blue`, `L` | job, workflow |
| `raster.reclassify` | Remap pixel values per a remap table (`gdal_calc.py`). | `remap`, `defaultValue`, `dataType` | job, workflow |
| `gdal.gdalwarp` | Full PROJ-backed raster reprojection, including datum shifts the managed path rejects. | `source`, `targetSrs`, `sourceSrs` | job, workflow |

## Imagery (1)

Imagery/ML analysis by **delegation to cloud-native inference** — Honua GP orchestrates, a managed cloud endpoint runs the model (no model runtime is bundled, and there is no in-process GPU/model execution or training). Managed profile: the lean dispatcher performs the HTTP delegation itself. Configure the backend with `Geoprocessing:ImageryInference` (`Provider`, `Endpoint`, `ApiKey` — the key may be a secret reference resolved through the secret store, or the `HONUA_IMAGERY_INFERENCE_API_KEY` environment variable; it is never logged). The generic `http` provider is supported end-to-end. **It speaks Honua's own JSON inference contract, not the OpenAI chat-completions format and not any vendor's native protocol** — request `{model, task, image (base64 GeoTIFF), imageMediaType, sourceCrs?, confidenceThreshold?}`, response `{outputType: "raster"|"features", raster: base64 GeoTIFF | features: GeoJSON FeatureCollection in WGS 84}`. Point `Endpoint` at a model server implementing that contract, or at a thin gateway that translates it (for example in front of an Azure ML online endpoint). `sagemaker`, `vertex`, and `azureml` SDK-authenticated adapters are recognized configuration values that fail clearly until their adapters land. With no backend configured, submissions validate but execution fails with a clear unavailability message.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `imagery.classify` | Classification / segmentation / object detection on a raster scene via a configured cloud inference backend. The model is a *reference* into the backend. Lands either a classification/segmentation GeoTIFF (backend-preserved georeferencing, passed through byte-for-byte and verified against the source CRS/extent) or detected features as a GeoJSON FeatureCollection in WGS 84 lon/lat (RFC 7946). The source must be an axis-aligned north-up GeoTIFF with an EPSG-coded CRS (user-defined GeoKey `32767` definitions are rejected). Detected features are checked against the source footprint where the CRS can be mapped to WGS 84 without PROJ — exactly for WGS 84 and Web Mercator sources, and against the zone's area of use for UTM sources; other CRSs fall back to a global lon/lat bounds check. | `source` (base64 GeoTIFF) or `layerId`/`rasterId`, `model`, `task` (`classification`/`segmentation`/`detection`), `confidenceThreshold` | job, workflow |

## Conversion (8)

Explicit format/CRS conversion idioms. Two are managed; the rest run in the GDAL/PDAL worker (*native*).

| Process ID | Description | Key parameters | Profile | Entry points |
| --- | --- | --- | --- | --- |
| `conversion.geometry-format` | Convert a geometry to WKT, GeoJSON, WKB, or EWKT. | `geometry`, `target` | managed | protocol |
| `conversion.feature-project` | Reproject every feature in a layer. | `layerId`, `targetSrid` | managed | job, workflow |
| `conversion.raster-format` | Raster format conversion (GTiff/PNG/JPEG/COG) via real GDAL `gdal_translate` (#2138). | `source`, `targetFormat`, `compression` | **native** | job, workflow |
| `conversion.raster-reproject` | Raster CRS conversion via real GDAL `gdalwarp` (#2138). | `source`, `targetSrid`, `resampling` | **native** | job, workflow |
| `conversion.polygonize` | Vectorize a raster into polygons per connected region (`gdal_polygonize.py`). | `source`, `band`, `connectedness`, `fieldName` | **native** | job, workflow |
| `conversion.rasterize` | Burn vector features into a new raster grid (`gdal_rasterize`). | `source`, `burnValue`/`attribute`, `cellSize`/`width`+`height`, `nodata` | **native** | job, workflow |
| `gdal.ogr2ogr` | Vector format conversion via the GDAL worker. Target drivers: GeoJSON, GPKG, CSV, FlatGeobuf, ESRI Shapefile. | `source`, `targetFormat`, `sourceFormat` | **native** | job, workflow |
| `pcloud.translate` | Decompress LAZ/COPC to uncompressed LAS, optionally reprojecting to EPSG:4979 (`pdal translate`). | `source`, `sourceSrs` | **native** | job, workflow |

## Data management (4)

Bulk, layer-level mutation workflows. `delete-features` and `calculate-field` are destructive and route through the approval gate.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `data-management.copy-features` | Copy (optionally filtered) features into a new layer; non-destructive. | `sourceLayerId`, `targetLayerName`, `where`, `objectIds` | protocol |
| `data-management.append` | Append a source FeatureCollection into the target's schema (Esri Append), with optional `fieldMap`. | `input` (target), `append` (source), `fieldMap` | job, workflow |
| `data-management.delete-features` | Delete matching features. **Destructive — requires approval**; `where` or `objectIds` is mandatory. | `layerId`, `where`, `objectIds` | protocol |
| `data-management.calculate-field` | Set a field via constant or allow-listed SQL expression. **Destructive — requires approval.** | `layerId`, `fieldName`, `expression`, `where`, `objectIds` | protocol |

## Import (1)

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `import.dataset` | End-to-end durable import: stage → import/chunk → flatten to a typed layer → optional raster tiling → extent/MVT refresh → provenance. The canonical execution layer the geospatial-mcp `publish_data` family submits into. Managed. | `connection`, `sourcePath`, `fileName`, `tableName`, `layerName`, `serviceName`, `targetSrid`, `rasterLayerId`, … | job, workflow |

## Transform (12)

GeoETL transform nodes: read a FeatureCollection data URI on `input`, emit a FeatureCollection data URI. Managed NetTopologySuite only — no GDAL.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `transform.attribute-rename` | Rename an attribute key on every feature. | `from`, `to` | job, workflow |
| `transform.attribute-cast` | Coerce an attribute to int/long/double/bool/string. | `field`, `to`, `onError` (drop/null/keep) | job, workflow |
| `transform.computed-field` | Derive a new attribute via a fixed op set (concat/add/…) or a sandboxed, AOT-safe expression engine. | `target`, `op`, `expression`, `fields`, `left`, `right`, `value` | job, workflow |
| `transform.attribute-filter` | Keep features matching a comparison (eq/neq/gt/gte/lt/lte/contains/exists). | `field`, `op`, `value` | job, workflow |
| `transform.attribute-join` | Hash-join to a right FeatureCollection on key columns, carrying selected fields (inner/left). | `input`, `right`, `leftKeys`, `rightKeys`, `fields`, `type`, `prefix` | job, workflow |
| `transform.aggregate` | Group-by aggregate (count/sum/min/max/mean/stddev/first/collect) with optional geometry reduction. | `input`, `groupBy`, `aggregates`, `geometry` | job, workflow |
| `transform.pivot` | Reshape long → wide: `pivotField` values become columns sourced from `valueField`. | `groupBy`, `pivotField`, `valueField`, `prefix` | job, workflow |
| `transform.unpivot` | Melt wide → long: one output feature per `fields` column. | `fields`, `keep`, `nameField`, `valueField`, `dropNulls` | job, workflow |
| `transform.spatial-filter` | Keep features intersecting/within a bbox or WKT region. | `bbox` or `wkt`, `predicate` | job, workflow |
| `transform.clip` | Clip feature geometries to a region, dropping features outside it. | `bbox` or `wkt` | job, workflow |
| `transform.dedup` | Keep the first feature per distinct attribute and/or geometry key. | `keys`, `geometry` | job, workflow |
| `transform.reproject` | Managed reprojection (4326 ↔ Web Mercator and aliases); datum-shift pairs are rejected — use `gdal.gdalwarp`. | `fromSrid`, `toSrid` | job, workflow |

## Source (8)

GeoETL DAG sources that produce a FeatureCollection artifact. Seven are managed parsers/connectors; `source.ogr` runs in the GDAL worker (*native*).

| Process ID | Description | Key parameters | Profile | Entry points |
| --- | --- | --- | --- | --- |
| `source.geojson` | Parse inline GeoJSON (or a data URI) into the standard FeatureCollection. | `inline` or `input` | managed | workflow |
| `source.csv` | Parse inline CSV; geometry from a WKT column or lon/lat pair. | `inline`, `delimiter` | managed | workflow |
| `source.honua-layer` | Stream a Honua catalog layer through the canonical query pipeline. | `layerId`, `where`, `bbox`, `outFields`, `outSrid`, `since`, `watermarkField` | managed | workflow |
| `source.esri-featureserver` | Stream an ArcGIS FeatureServer/MapServer layer (paged), Esri-JSON → GeoJSON. | `serviceUrl`, `esriLayerId`, `where`, `outFields`, `outSrid`, `pageSize`, token/credential params, `since` | managed | workflow |
| `source.ogc-features` | Stream an OGC API Features collection via `rel=next` link paging; `where` is CQL2-text. | `serviceUrl`, `collectionId`, `where`, `bbox`, `pageSize`, credential params, `since` | managed | workflow |
| `source.wfs` | Stream a WFS GetFeature endpoint with startIndex/count paging, GeoJSON output. | `serviceUrl`, `typeName`, `bbox`, `pageSize`, credential params | managed | workflow |
| `source.postgis` | Stream a customer-owned PostGIS table/view via a registered secure connection. | `connectionName`/`connectionId`, `table`, `schema`, `geometryColumn`, `where`, `bbox`, `outSrid`, `since`, `watermarkField` | managed | workflow |
| `source.ogr` | GDAL/OGR import reader for the full driver universe (FileGDB, GML, KML, MapInfo, Shapefile, GPKG, FlatGeobuf, …); multi-file datasets supplied as a base64 ZIP. | `source`, `sourceFormat` | **native** | workflow |

## Sink (4)

Terminate a workflow by writing the input FeatureCollection and emitting a result descriptor (target location + row counts). Managed writers / Npgsql only — no GDAL.

| Process ID | Description | Key parameters | Entry points |
| --- | --- | --- | --- |
| `sink.geojson-file` | Write the input FeatureCollection to a GeoJSON file under the configured output root. | `input`, `path` | workflow |
| `sink.quarantine` | Dead-letter sink: write rejected rows with batch id and reason. | `input`, `path`, `reasonField`, `batchId` | workflow |
| `sink.external-postgis` | Load features into a customer-owned PostGIS database via a registered secure connection (never a raw connection string). | `input`, `connectionName`/`connectionId`, `table`, `targetSrid`, `schema`, `geometryColumn`, `batchSize` | workflow |
| `sink.honua-layer` | Load features into a named layer in the Honua catalog database (append/replace/upsert). Executed by `HonuaLayerSinkExecutor` through the optional `IHonuaLayerSink` capability (#2210); **fails closed with a clear "unavailable in this deployment" message in lean, database-free deployments**. | `input`, `layer`, `targetSrid`, `loadMode`, `keyFields`, `schema`, `geometryColumn`, `batchId` | workflow |

Native-format sinks (shapefile, GeoPackage) are deferred to the GDAL worker stream; for those, convert with `gdal.ogr2ogr`.

## Related pages

- [Run geoprocessing guide](../guides/query-analyze/run-geoprocessing.md)
- [OGC APIs reference](protocols/ogc-apis.md)
- [gRPC ProcessService](protocols/grpc.md)
