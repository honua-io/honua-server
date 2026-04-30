# Geospatial Data APIs (Standards-Based)

Honua exposes multiple industry-standard geospatial APIs. This page highlights the major protocol families and helps you choose the right surface at a high level. For the exhaustive launch support matrix and caveats, use [MVP Compatibility and Limitations](MVP_COMPATIBILITY_CONTRACT.md).

## **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|-------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer / MapServer | `/rest/services/{id}/FeatureServer` or `/rest/services/{id}/MapServer` | Esri compatibility (data + maps) |
| **QGIS/OpenLayers** | OGC API Features | `/ogc/features` | Open standards |
| **STAC browsers/catalog tooling** | STAC API | `/stac` | Catalog discovery, item search, extension-aware metadata |
| **QGIS/GeoServer clients (legacy OGC)** | WMS 1.1.1/1.3, WFS 1.0/1.1/2.0, WMTS 1.0 | `.../MapServer/WMS`, `/wfs`, or `.../MapServer/WMTS` | Legacy OGC raster map and feature services |
| **Server-rendered maps (OGC)** | OGC API Maps | `/ogc/maps` | Standards-based rendered map images |
| **Power BI/Excel** | OData v4 | `/odata` | BI integration |
| **Web Maps (MapLibre/OpenLayers)** | Vector Tiles + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | Fast rendering with auto-styles |
| **Web terrain/elevation** | Terrain-RGB | `/terrain/{datasetId}/tile.json` | MapLibre/Mapbox `raster-dem` clients |
| **Esri raster/image workflows** | ImageServer | `/rest/services/{id}/ImageServer` | Esri raster compatibility |
| **Science/elevation coverage workflows** | WCS 2.0.1 | `/rest/services/{id}/ImageServer/WCS` or `/ogc/services/{serviceId}/wcs` | Raw raster/coverage values |
| **Modern OGC coverage workflows** | OGC API Coverages | `/ogc/coverages` | REST/JSON raster coverage discovery and export |
| **Esri geometry operations** | Geometry Service | `/rest/services/geometry` | Buffer, simplify, project, intersect, union, clip, difference, area, length |
| **Esri geoprocessing** | GPServer | `/rest/services/{id}/GPServer` | Esri GP compatibility over the canonical runtime |
| **Geoprocessing (OGC)** | OGC API Processes | `/ogc/processes` | Standards-based async geoprocessing |
| **Spec plan/apply engine** | REST + gRPC | `/v1/spec/*` + `geospatial.v1.SpecService` | Terraform-style plan/apply with content-hash artifact cache — see [Spec Engine reference](../developer/SPEC_ENGINE.md) |
| **Custom Applications** | Any protocol | Multiple endpoints | Choose by client needs |

---

## **GeoServices REST FeatureServer**

**Best for**: Esri tooling and existing ArcGIS workflows

**Endpoint structure:**
```
/rest/services/{service-name}/FeatureServer/{layer-id}
|-- /query
|-- /queryClusters          (Pro extension, POST only)
|-- /spatialJoin            (Pro extension, POST only)
|-- /queryBufferAggregate   (Pro extension, POST only)
|-- /queryDensity           (Pro extension, POST only)
|-- /addFeatures
|-- /updateFeatures
|-- /deleteFeatures
|-- /applyEdits
```

**Output formats:**
- Metadata: `json`
- Features: `json` (GeoServices), `geojson`, `pbf` (Protocol Buffers), `fgb` (FlatGeobuf), `geobuf` (when store supports native output), `parquet` (GeoParquet with WKB geometry), `arrow` (GeoArrow IPC stream)

**Typical use cases:**
- ArcGIS Pro connectivity
- ArcGIS SDK clients
- Legacy FeatureServer integrations
- Analytics workflows (GeoParquet / GeoArrow export)

**Contract notes:**
- The Pro-tier analytics extensions always return GeoJSON FeatureCollections (`application/geo+json`) even on the FeatureServer route family.
- Analytics geometries are normalized to WGS 84 / EPSG:4326 and the payload always includes `numberReturned` plus a `metadata` object (`operation`, truncation flags, and configured limits).
- Per-feature cluster and spatial-join rows preserve `properties.objectId` plus nested `properties.attributes`; operation-specific fields then layer on top (`clusterId`, `matchCount`, `featureCount`, `cellId`, optional `weight`).

---

## **GeoServices REST MapServer**

**Best for**: Esri map rendering workflows (dynamic map images, identify, legends)

**Endpoint structure:**
```
/rest/services/{service-name}/MapServer
|-- /export
|-- /identify
|-- /legend
|-- /{layer-id}/query
```

**Typical use cases:**
- ArcGIS Pro map rendering
- Dynamic map images for web clients
- Identify and legend requests from Esri tooling

---

## **OGC API Features**

**Best for**: Standards-based, vendor-neutral access

**Endpoint structure:**
```
/ogc/features
|-- /
|-- /conformance
|-- /collections
|-- /collections/{id}
|-- /collections/{id}/items
|-- /collections/{id}/clusters           (Pro extension, POST only)
|-- /collections/{id}/spatial-join       (Pro extension, POST only)
|-- /collections/{id}/buffer-aggregate   (Pro extension, POST only)
|-- /collections/{id}/density            (Pro extension, POST only)
```

**Output formats:**
- Metadata: `json` or `html`
- Features: `geojson` (default), `json`, `html`, `gml` (GML 3.2 via content negotiation; advertised as the `gml-sf0` conformance class and independently CITE-validated at format level)

**Typical use cases:**
- QGIS and open-source GIS tooling
- Vendor-neutral integration
- Simple feature queries by bbox or filter

**Contract notes:**
- The analytics mirrors are POST-only Honua extensions that share the same request fields and response contract as the FeatureServer analytics routes.
- Responses remain `application/geo+json` in WGS 84 with `numberReturned` and analytics `metadata`; `application/json` is the canonical request content type, and the shared POST-body parser also accepts `application/x-www-form-urlencoded`.
- Per-feature cluster and spatial-join mirrors preserve `properties.objectId` plus nested `properties.attributes`; aggregate outputs then surface operation-specific summary fields such as `featureCount`, `cellId`, and optional `weight`.

---

## **STAC API**

**Best for**: STAC-native catalog discovery, collection review, and item search

**Endpoint structure:**
```
/stac
|-- /
|-- /collections
|-- /collections/{id}
|-- /collections/{id}/items
|-- /collections/{id}/items/{itemId}
|-- /search
```

**Output formats:**
- Catalog and collections: `json`
- Items and search: `geojson`

**Contract notes:**
- Catalog, collection list, and single-collection metadata routes emit strong `ETag` values for conditional GET.
- Collections always include a `license`; when no STAC-specific license is declared, Honua emits `proprietary`. `keywords` and `stac_extensions` appear when declared in layer metadata.
- Collection detail includes `items` links plus an `alternate` link to the corresponding OGC API Features collection.
- Items and search hits preserve declared `stac_extensions` when item-level extension metadata is configured.
- Items always include `properties.datetime`; when a layer has no resolvable time field, the property remains present with a `null` value.
- Pagination links preserve encoded `bbox` and `datetime` filters so clients can replay sampled queries exactly.
- Search supports GET and POST with `fields`, `sortby`, and CQL2 filtering (`filter`, `filter-lang`, and registry-backed `filter-crs` / explicit geometry CRS for CQL2 spatial literals).

**Typical use cases:**
- STAC browser and catalog interoperability
- Extension-awareness review for EO, Projection, and View metadata
- Cross-checking STAC discovery output against OGC API Features item access

---

## **OData v4**

**Best for**: BI tooling and enterprise data integration

**Endpoint structure:**
```
/odata
|-- /
|-- /$metadata
|-- /{entity-set}
|-- /$batch
```

**Typical use cases:**
- Excel and Power BI dashboards
- BI pipelines and reporting
- Non-GIS systems consuming spatial data

---

## **OGC API Maps**

**Best for**: Standards-based server-rendered map images

**Endpoint structure:**
```
/ogc/maps
|-- /conformance
|-- /map
|-- /collections/{id}/map
|-- /collections/{id}/map/tiles
```

**Output formats:** PNG, JPEG, TIFF

**Current scope:** Core map rendering, collection maps, map tiles, and Pro-tier temporal raster mosaic via `datetime` instants and intervals (`start/end`, `../end`, `start/..`). Temporal selection uses the newest effective acquisition batch across the addressed collection or dataset before bbox windowing, so mixed-date scenes can leave coverage gaps until per-pixel temporal mosaicking lands. The optional styled-map conformance class is not claimed in MVP.

**Typical use cases:**
- Server-rendered maps via open standards
- Dynamic map image generation without Esri dependencies
- OGC-compliant map rendering workflows

---

## **OGC API Coverages**

**Best for**: Modern OGC raster coverage discovery, metadata, and raw coverage export

**Endpoint structure:**
```
/ogc/coverages
|-- /
|-- /conformance
|-- /api
|-- /openapi.json
|-- /collections
|-- /collections/{id}
|-- /collections/{id}/schema
|-- /collections/{id}/coverage
```

**Output formats:** JSON/HTML for metadata; `image/tiff` GeoTIFF by default for coverage bytes; `image/png` via `f=png` or `Accept: image/png`.

**Current scope:** Accessible raster-backed collections only. Collection documents include `itemType: "coverage"`, `crs`, `storageCrs`, `extent.spatial.bbox`, `extent.spatial.storageCrsBbox` when known, grid/domain metadata, default band fields, schema links, and coverage links. Coverage retrieval supports `bbox`, `bbox-crs`, output `crs`, `properties=band_N,...`, and one scaling control (`resolution` in native/storage CRS units, `scale-factor`, or `scale-size`) capped at 8192 pixels on either axis. Response headers include `Content-Bbox` when the raster result reports an extent, `Content-Crs` for non-WGS 84 output, and `Link` alternates for GeoTIFF/PNG that preserve the request query while swapping `f`.

**Limitations:** This is a thin REST/JSON adapter over the shared primary-raster export pipeline. Collection listing, collection metadata, schema, and coverage bytes are not output-cached because they are access-filtered or high-cardinality. `datetime`, `subset`, `scale-axes`, NetCDF, JPEG, CoverageJSON, multipart responses, per-scene catalog selection, and tiled coverage delivery are deferred. See [OGC API Coverages Coverage](specifications/ogc-api-coverages-coverage.md) for parameter details and examples.

**Typical use cases:**
- GeoTIFF coverage export through modern OGC APIs
- REST/JSON coverage metadata discovery
- Band selection and reprojection over existing raster layers

---

## **OGC API Processes**

**Best for**: Standards-based asynchronous geoprocessing

**Endpoint structure:**
```
/ogc/processes
|-- /
|-- /openapi.json
|-- /conformance
|-- /processes
|-- /processes/{processId}
|-- /processes/{processId}/execution
|-- /jobs
|-- /jobs/{jobId}
|-- /jobs/{jobId}/results
```

**Output formats:** JSON (landing, metadata, status, and errors today; planned successful results remain document-mode, by-value)

**Typical use cases:**
- Standards-based geoprocessing workflows
- Async job submission and status polling
- OGC-compliant process discovery and execution
- Interoperability with OGC API Processes clients

**Contract notes:**
- This is a protocol adapter over the canonical Honua geoprocessing runtime, not a separate processing framework.
- V1 supports async execution only; synchronous execution returns `501 Not Implemented`.
- Execution requires `Prefer: respond-async`, accepts only `response=document`, and returns `201 Created` with `Location` plus `Preference-Applied: respond-async` on success.
- V1: Succeeded jobs return `200 OK` with a document-mode, by-value JSON body (empty `{}` until the canonical process declares value-typed outputs and result storage is populated). Non-terminal jobs return `404`, failed jobs return `500`, and dismissed jobs return `410 Gone`.
- Succeeded jobs advertise the OGC `results` relation in their `StatusInfo` document so clients can follow it to `/jobs/{jobId}/results`.
- Async execution and job routes require Redis-backed durable storage; execution/job list/status/results/dismiss return `503 Service Unavailable` when the store is not configured.
- V1 exposes one canonical process (`honua-geoprocessing`) in `/processes`; the internal `IProcessCatalog` enumerates 34 built-in processes across seven families — 10 `geometry.*` (`buffer`, `simplify`, `project`, `make-valid`, `union`, `intersect`, `clip`, `difference`, `area`, `length`), 4 `analytics.*` (`cluster`, `spatial-join`, `buffer-aggregate`, `density`), 6 `surface.*` (`slope`, `aspect`, `hillshade`, `rugosity-tri`, `rugosity-tpi`, `roughness`), 5 `raster.*` (`clip`, `reproject`, `statistics`, `histogram`, `zonal-statistics`), 4 `conversion.*` (`geometry-format`, `feature-project`, `raster-format`, `raster-reproject`), 2 `generalization.*` (`simplify-layer`, `dissolve`), and 3 `data-management.*` (`copy-features`, `delete-features`, `calculate-field`) — and plan submissions are validated against it (`MISSING_PROCESS_ID`, `UNKNOWN_PROCESS`, `MISSING_REQUIRED_PARAMETER`, `UNKNOWN_PARAMETER`, `INVALID_PARAMETER_VALUE`). The `surface.*` and `raster.*` catalog contributions are declarative and validation-only today; `ISurfaceAnalysisService` and the new `IRasterStore.ComputeZonalStatisticsAsync` path expose the PostGIS-backed execution primitives that runtime handler/executor wiring will call into in a follow-on ticket (tracked alongside the #727 cloud executor adapters). Validation mirrors the live handlers across (1) enum value sets (`analytics.cluster` `algorithm`, `analytics.spatial-join` `predicate`, `analytics.density` `mode`, `analytics.buffer-aggregate` `unit`, `surface.slope` `units` ∈ {`degrees`, `percent`, `radians`}, `raster.reproject`/`conversion.raster-reproject` `resampling` ∈ {`nearestneighbor`, `bilinear`, `cubic`, `lanczos`}, `conversion.raster-format` `targetFormat` ∈ {`GTiff`, `PNG`, `JPEG`, `COG`}, `conversion.geometry-format` `target` ∈ {`wkt`, `geojson`, `wkb`, `ewkt`}, `raster.zonal-statistics` `statistics` ∈ {`count`, `sum`, `mean`, `min`, `max`, `stddev`, `variance`}), (2) configured numeric ranges honoring `Limits:Analytics` bounds (`eps` > 0 and ≤ `MaxDbscanEpsMeters`, dwithin `distance` > 0 and ≤ `MaxDWithinDistanceMeters`, `cellSize` within `[MinDensityCellSizeMeters, MaxDensityCellSizeMeters]`, buffer-aggregate `distance` ≥ 0 with `MaxBufferDistanceMeters` applied after unit conversion, `minPoints` ≥ 1, `k` ≥ 1 and ≤ `MaxKMeansK`, `generalization.simplify-layer` `tolerance` > 0 in SRID units, surface/raster `zFactor` > 0, hillshade `azimuth` ∈ [0, 360] and `altitude` ∈ [0, 90] degrees, surface rugosity `windowRadius` exactly 1 (PostGIS built-ins only support a 3×3 focal neighborhood today), raster.histogram `binCount` ≥ 1, raster.zonal-statistics `band` ≥ 1, optional `rasterId` when supplied must be a positive 64-bit integer, and raster statistics/histogram `bands` parses as comma-separated positive integers), (3) conditional requiredness (`eps`+`minPoints` when `algorithm=dbscan`, `k` when `algorithm=kmeans`, `distance` when `predicate=dwithin`, at least one of `where`/`objectIds` on `data-management.delete-features`; blank whitespace values are treated as "not supplied" so they surface as `MISSING_REQUIRED_PARAMETER`), (4) cross-field invariants (`analytics.spatial-join` rejects `joinLayerId == layerId`; `analytics.cluster` requires `returnHullPerCluster=true` when `outStatistics` is supplied; `analytics.buffer-aggregate` and `generalization.dissolve` both require `dissolve=true` when `outStatistics` is supplied), (5) structured Text-input parsing for the dependency-free fields the handlers parse (`outStatistics` JSON shape with `statisticType` ∈ {`count`,`sum`,`min`,`max`,`avg`,`stddev`,`var`} and each entry field required to be a JSON string, `objectIds` as comma-separated integers, `spatialRel` rejection of distance-based variants `esriSpatialRelWithinDistance`/`esriSpatialRelBeyondDistance` only when a `geometry` filter is supplied, matching `AnalyticsFeatureQueryFactory`, `data-management.calculate-field` `fieldName` restricted to a simple unquoted identifier), and (6) `LayerId`-typed inputs require non-negative integers — zero-based layer ids are accepted to match the live `RouteParameterValidator.ValidateLayerId` contract and the analytics REST `{layerId:int}` route. The remaining shared analytics filter inputs (`where`, `geometry`, `geometryType`, `inSR`, `time`, `timeRelation`) are accepted opaquely at this gate and validated at execution time by `AnalyticsFeatureQueryFactory`, which needs runtime services not available at catalog-validation time. Destructive `data-management.*` ids (`delete-features`, `calculate-field`) additionally route through `OperatorApprovalGate` with `IsDestructive = true`. When `Operator:Approval:DestructiveActionsRequireApproval` is on, submissions hard-fail at the gate (gRPC `FailedPrecondition`, OGC `403 Approval required`) before any job or progress record is created; pending-approval persistence and a `Validated → AwaitingApproval` status projection are follow-on work. See [OGC API Processes Coverage](specifications/ogc-api-processes-coverage.md) for the full per-process validation table. Per-process projection into the OGC adapter surface is follow-on work.
- Conforms to OGC API Processes Part 1: Core conformance classes: `core`, `json`, `dismiss`, plus OGC API Common `core` and `json`. The `job-list` conformance class is implemented at MVP level but not advertised (V1 lacks required filters and pagination).

---

## **WMS 1.1.1 / 1.3 and WMTS 1.0**

**Best for**: Legacy OGC map services (QGIS, GeoServer ecosystem clients)

**Endpoint structure:**
```
/rest/services/{id}/MapServer/WMS    (or /ogc/services/{id}/wms)
|-- ?service=WMS&request=GetCapabilities&version=1.3.0
|-- ?service=WMS&request=GetCapabilities&version=1.1.1
|-- ?service=WMS&request=GetMap
|-- ?service=WMS&request=GetFeatureInfo

/rest/services/{id}/MapServer/WMTS   (or /ogc/services/{id}/wmts)
|-- ?service=WMTS&request=GetCapabilities
|-- ?service=WMTS&request=GetTile
|-- ?service=WMTS&request=GetFeatureInfo
```

**Limitations:** WMTS currently supports WebMercatorQuad tile matrix set only. WMS 1.1.1 is KVP read-only compatibility; use `SRS`, `X/Y`, and lon/lat `EPSG:4326` BBOX order for that version.

**Typical use cases:**
- QGIS WMS/WMTS layer connections
- Desktop GIS clients expecting legacy OGC services
- INSPIRE/SDI compliance requiring WMS/WMTS endpoints

---

## **WCS 2.0.1**

**Best for**: Raw raster and coverage values for science, elevation, and remote-sensing workflows

**Endpoint structure:**
```
/rest/services/{id}/ImageServer/WCS
|-- ?service=WCS&request=GetCapabilities&version=2.0.1
|-- ?service=WCS&request=DescribeCoverage&coverageId={layerId}
|-- ?service=WCS&request=GetCoverage&coverageId={layerId}&format=image/tiff

/ogc/services/{serviceId}/wcs
|-- ?service=WCS&request=GetCapabilities&version=2.0.1
|-- ?service=WCS&request=GetCoverage&coverageId={layerId}&subset=Long(min,max)&subset=Lat(min,max)
```

**Output formats:** `image/tiff` (default), `image/png`, `image/jpeg` (`image/geotiff`, `tif`/`tiff`, `png`, `jpg`/`jpeg` aliases are accepted)

**Limitations:** WCS is a thin KVP adapter over the existing raster store. It only lists and serves WCS-enabled raster layers visible to the caller. `GetCoverage` uses the primary raster for the addressed layer, returns buffered `RasterResult.Data` bytes, and opts out of exact response output caching for ad hoc trims. Capabilities advertise CRS support from visible coverage native CRSs. Single-axis `SUBSET` fills the omitted axis from the stored raster extent and is rejected when `SUBSETTINGCRS`/`BBOXCRS` is non-native; non-native subsetting callers must provide both axes or use `BBOX`. Range subset/band selection, scaling extensions, XML POST bodies, polygon trims, NetCDF, temporal/multidimensional slicing, and WCS-specific multi-raster mosaic selection are deferred. See [WCS 2.0.1 Coverage](specifications/wcs-2.0.1-coverage.md) for parameter details and examples.

**Typical use cases:**
- GeoTIFF coverage export for science and elevation tools
- Spatially subsetted raw raster extracts
- Migration paths for GeoServer WCS consumers

---

## **Terrain-RGB Elevation Tiles**

**Best for**: Web terrain visualization and hillshade/extrusion clients that consume Mapbox-compatible Terrain-RGB through MapLibre or Mapbox `raster-dem` sources

**Endpoint structure:**
```
/terrain/{datasetId}/tile.json
/terrain/{datasetId}/{z}/{x}/{y}.png
```

**Output formats:** TileJSON 3.0 metadata (`application/json`) and 256x256 opaque Terrain-RGB PNG tiles (`image/png`) in WebMercator XYZ coordinates.

**Contract notes:** `datasetId` accepts a numeric layer id or a layer collection name. The Terrain protocol must be enabled on the service or layer metadata; omitted `EnabledProtocols` means Terrain is enabled by default with the rest of the protocol set. Tile requests validate zoom and `z/x/y` against configured `Limits:Tiles` and WebMercator matrix bounds. Missing datasets or layers with no raster source return `404`; unsupported DEM sources return `422` with a problem response.

Terrain v1 expects registered PostGIS rasters with one numeric elevation band, one usable source CRS/SRID, and a consistent CRS across the dataset. Source no-data and uncovered pixels are encoded as Terrain-RGB `[0,0,0]`, which decodes to `-10000m`; fully uncovered but otherwise valid tiles return an all-sentinel PNG instead of `404`. Metadata reports TileJSON bounds/center, source CRS/extent when available, raster ids/count, band/pixel details, vertical unit/datum when declared, the meter-unit assumption, and unsupported reasons when the source cannot be tiled. Terrain metadata and tile cache entries are varied by dataset/tile route values and evicted through layer/raster plus admin service/collection/all invalidation. See [Terrain-RGB Elevation Tiles](terrain-tiles.md) for the full response contract.

**Typical use cases:**
- MapLibre GL JS `raster-dem` terrain sources
- Web terrain exaggeration, hillshade, and elevation inspection
- Lightweight DEM serving without client-side terrain generation

---

## **GeoServices REST ImageServer**

**Best for**: Esri raster/image workflows

**Endpoint structure:**
```
/rest/services/{id}/ImageServer
|-- /exportImage
|-- /identify
|-- /tile/{level}/{row}/{col}
|-- /query                          (raster catalog features; in-memory WHERE)
|-- /computeStatisticsHistograms    (per-band statistics + histograms)
|-- /legend                         (fixed 5-class equal-interval ramp)
|-- /computeClass                   (raster function chain validation)
```

**Limitations:** `query` filtering still happens in memory after the catalog is read; spatial filters and `orderByFields` are not pushed to PostGIS yet. `computeStatisticsHistograms` does not honour AOI clipping. `legend` uses a fixed viridis ramp keyed off the resolved layer mosaic's band-1 statistics. ImageServer temporal raster mosaic accepts single instants only, requires Pro edition licensing, and selects the newest layer-wide effective acquisition batch before request geometry/windowing. `computeClass` validates and plans `Identity`/`Stretch`/`Clip` chains (max depth 8) but does not execute the chain — the planner is not yet wired into `exportImage`/`identify`. See the [ImageServer Matrix](image-server-matrix.md) for full parameter coverage.

**Typical use cases:**
- ArcGIS Pro raster rendering
- Image export and pixel value queries across multi-raster mosaics
- Tiled image serving
- Raster catalog discovery (footprint polygons + per-item attributes, including `AcquisitionDate`, via `query`)
- Per-band statistics and histograms for analytics dashboards, including selected mosaics
- Layer legend swatches for ArcGIS Maps SDK clients
- Time-aware raster mosaic requests on Pro editions
- Validating raster function chains before submitting them to the server

---

## **GeoServices REST Geometry Service**

**Best for**: Esri geometry operations

**Endpoint structure:**
```
/rest/services/geometry
|-- /buffer
|-- /simplify
|-- /project
|-- /intersect
|-- /union
|-- /clip
|-- /difference
|-- /area
|-- /length
```

**Typical use cases:**
- Coordinate reprojection
- Geometry buffering and simplification
- Esri SDK geometry helper operations

---

## **GeoServices REST GPServer**

**Best for**: Esri geoprocessing workflows over catalog-backed tasks with async submission, job polling, and cancellation

**Endpoint structure:**
```
/rest/services/{service-name}/GPServer
|-- /                                      (service info — available tasks)
|-- /{taskName}                            (task info — parameters, data types)
|-- /{taskName}/submitJob                  (async job submission)
|-- /{taskName}/jobs/{jobId}               (job status polling)
|-- /{taskName}/jobs/{jobId}/results/{paramName}  (named output result)
|-- /{taskName}/jobs/{jobId}/cancel        (cancel in-flight job)
```

**Output formats:** JSON (Esri camelCase convention)

**Limitations:** Generic built-in tasks are currently async-only and do not publish a generic `execute` route until canonical `ExecutePlan` and synchronous-task projection exist. Generic task names are currently the published built-in process IDs (for example `geometry.buffer`). GP environment controls (`env:*`) are rejected with `400`; `context` is preserved as protocol metadata but not interpreted yet. Per-parameter result retrieval route is registered but actual output retrieval is pending execution-engine and result-storage support.

**Typical use cases:**
- ArcGIS Pro / SDK geoprocessing tool connectivity
- Async analysis workflows with job lifecycle polling
- Per-parameter result retrieval for terminal jobs

**Contract notes:**
- GPServer is a protocol adapter over the canonical process runtime; it does not define its own job storage, and result packages are read from persisted terminal packages or synthesized from the durable execution-job record.
- Service root, task info, `submitJob`, and `cancel` accept both GET and POST on the generic adapter. Job status and named result resources are GET-only. PrintingTools continues to publish `execute` because it has a real synchronous implementation; generic built-in GPServer tasks do not. For POST requests, query-string parameters are read first and then overlaid by form-encoded body values (body takes precedence on key collision).
- `submitJob` resolves `taskName` against the built-in `IProcessCatalog` and returns HTTP `200 OK` with the Esri job envelope (`jobId`, `jobStatus`), matching ArcGIS GPServer response shape.
- Canonical `ExecutionJobStatus` maps to Esri status strings: `Queued`→`esriJobSubmitted`, `Provisioning`→`esriJobWaiting`, `Running`→`esriJobExecuting`, `Succeeded`→`esriJobSucceeded`, `Failed`→`esriJobFailed`, `Cancelled`→`esriJobCancelled`.
- Parameter translation converts Esri GP types (GPDataFile, GPLinearUnit, GPFeatureRecordSetLayer, etc.) to canonical opaque step inputs and maps `ArtifactKind` back to GP data types on output.
- Route binding is validated: job status/result/cancel endpoints verify the `serviceId` and `taskName` match the stored job metadata, returning 404 for mismatches. Jobs submitted via other protocols (e.g. gRPC) are rejected to prevent cross-protocol access.
- Generic built-in tasks are currently async-only. PrintingTools remains the only published GPServer surface with synchronous `execute` support in this codebase.
- See [ADR-0029](../contributor/adr/0029-geoprocess-canonical-model-mappings.md) for adapter invariants and the [Geoprocess Framework Analysis](geoprocess-framework-analysis.md) for the full canonical model mapping.

---

## **Vector Tiles (MVT) + TileJSON**

**Best for**: High-performance web maps

**Endpoint structure:**
```
/tiles/{layerId}/{z}/{x}/{y}.mvt     (vector tiles)
/tiles/{layerId}/tile.json            (TileJSON metadata)
/api/styles/{layerId}.json            (auto-generated MapLibre style)
```

**Typical use cases:**
- MapLibre GL JS maps with auto-generated styles
- OpenLayers VectorTile layers
- Leaflet and Mapbox GL maps
- Fast vector rendering at multiple zoom levels

---

## **Versioning and Compatibility Policy**

Standards-based APIs follow a fundamentally different versioning model than the control-plane admin API.

### Path stability

Standards endpoints (`/rest/services/*/FeatureServer`, `/rest/services/*/MapServer`, `/ogc/*`, `/stac`, `/odata`, WMS/WMTS) use **stable protocol paths dictated by the specification they implement**. They are **not path-versioned by Honua**. The URL structure is defined by the external standard (Esri REST, OGC, OData, STAC), not by Honua's internal release cadence.

### Backward compatibility

Backward compatibility for standards APIs is defined by the external standard, not by Honua versioning. A change that conforms to the upstream specification is not considered a Honua breaking change, even if it alters behavior. Conversely, deviating from the specification in a way that breaks compliant clients is treated as a bug, not a version change.

### Compatibility artifacts

Compatibility is validated through:
- **Coverage matrices** tracking supported operations per standard (see [Coverage and Compliance](#coverage-and-compliance) below).
- **CITE conformance results** for OGC standards (scheduled nightly/manual workflows, 100% pass rate required).
- **Client template validation** via the [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md).
- **Release notes** documenting any changes to standards API behavior.

### Honua-specific additions

Any Honua-specific extensions to standards APIs (e.g., additional query parameters, extra response fields) are:
- Additive only (they do not alter standard-defined behavior).
- Discoverable via the standard's introspection mechanism where applicable.
- Documented in the relevant coverage matrix.

### Deprecation of previously supported operations

Removal of previously supported standards API operations follows the same deprecation lifecycle as control-plane APIs: a minimum of **90 calendar days** and at least **2 minor releases**, whichever is longer. See [CONTROL_PLANE_VERSIONING_POLICY.md](../developer/CONTROL_PLANE_VERSIONING_POLICY.md#deprecation-lifecycle) for the full lifecycle.

---

## **Coverage and Compliance**

Protocol support is tracked per standard and operation. Use these docs to confirm supported behaviors:

**GeoServices REST (Esri-compatible):**
- [GeoServices REST Parity](geoservices-rest-parity.md) — canonical landing page for FeatureServer, MapServer, ImageServer, Geometry Service, and GPServer
- [GeoServices REST Parity Data (JSON)](data/geoservices-rest-parity.json) — machine-readable export of the same operation and parameter contract
- [FeatureServer Coverage Matrix](feature-server-matrix.md) — aligned to [Esri REST Feature Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/)
- [MapServer Coverage Matrix](map-server-matrix.md) (includes WMS 1.3 and WMTS 1.0) — aligned to [Esri REST Map Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/)
- [ImageServer Coverage Matrix](image-server-matrix.md) — aligned to [Esri REST Image Service spec](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/)
- [Geometry Service Matrix](geometry-service-matrix.md) — buffer, simplify, project, intersect, union, clip, difference, plus Honua supplemental `area`/`length` routes
- [Geoprocess Framework Analysis](geoprocess-framework-analysis.md) — GPServer canonical model mapping, lifecycle state matrix, and adapter invariants

**OGC API:**
- [OGC API Features Coverage](specifications/ogc-api-features-coverage.md)
  - [Part 1 — Core](specifications/ogc-api-features-part1-core.md)
  - [Part 2 — CRS](specifications/ogc-api-features-part2-crs.md)
  - [Part 3 — Filtering](specifications/ogc-api-features-part3-filtering.md)
- [OGC API Tiles Coverage](specifications/ogc-api-tiles-coverage.md)
- [OGC API Processes Coverage](specifications/ogc-api-processes-coverage.md)

**OData v4:**
- [OData v4 Coverage](specifications/odata-v4-coverage.md)

**Public interface governance:**
- [Public Interface Proof Ledger (JSON)](data/public-interface-proof.json) — canonical machine-readable inventory of every shipped surface, proof classes, CI lanes, and evidence locations
- [Public Interface Quality Model](../contributor/public-interface-quality-model.md) — human-readable explanation of proof classes, release evidence rules, and ticket reconciliation

**Client validation artifacts:**
- [Client Templates + Manual Smoke Runbook](CLIENT_TEMPLATE_RUNBOOK.md)
- [Client Template Version Matrix](CLIENT_TEMPLATE_VERSION_MATRIX.md)
- [Cross-Client Certification Matrix](CROSS_CLIENT_CERTIFICATION_MATRIX.md)
- [Cross-Client Certification Evidence](CROSS_CLIENT_CERTIFICATION_EVIDENCE.md)

**OGC CITE conformance (100% pass rate):**
- OGC API Features: 137/137 tests
- OGC API Tiles: 16/16 tests
- WMS 1.3: 227/227 tests
- WMS 1.1.1: CITE Basic evidence pending
- WFS 1.1.0 / 1.0.0: CITE Basic evidence pending
- WMTS 1.0: 118/118 tests
- OGC API Maps: 32/32 tests
- OGC API Processes: CITE ETS not yet available; conformance validated manually against OGC 18-062r2
- KML 2.2: format-level validation (schema conformance)
- GML 3.2: format-level validation (schema conformance)
- GeoPackage 1.2: format-level validation (file structure conformance)

---

## **Related Documentation**

- [MVP Compatibility Contract](MVP_COMPATIBILITY_CONTRACT.md)
- [Geospatial API Examples](../developer/API_EXAMPLES.md)
- [Integration Patterns](../developer/INTEGRATION_PATTERNS.md)
- [Interactive API Explorer](http://localhost:8080/docs) *(requires running server)*
- [GeoServices REST Parity](geoservices-rest-parity.md)
- [GeoServices REST Parity Data (JSON)](data/geoservices-rest-parity.json)
- [FeatureServer Coverage Matrix](feature-server-matrix.md)
- [MapServer Coverage Matrix](map-server-matrix.md)
- [ImageServer Coverage Matrix](image-server-matrix.md)
- [Geometry Service Matrix](geometry-service-matrix.md)
- [Geoprocess Framework Analysis](geoprocess-framework-analysis.md)
