# Geospatial Data APIs (Standards-Based)

Honua exposes multiple industry-standard geospatial APIs. This page helps you choose the right protocol and understand the shape of each API at a high level.

## **Quick Protocol Selection**

| If you're using... | Use this API | Endpoint Pattern | Why |
|-------------------|-------------|------------------|-----|
| **ArcGIS Pro/Desktop** | FeatureServer / MapServer | `/rest/services/{id}/FeatureServer` or `/rest/services/{id}/MapServer` | Esri compatibility (data + maps) |
| **QGIS/OpenLayers** | OGC API Features | `/ogc/features` | Open standards |
| **STAC browsers/catalog tooling** | STAC API | `/stac` | Catalog discovery, item search, extension-aware metadata |
| **QGIS/GeoServer clients (legacy OGC)** | WMS 1.3 / WMTS 1.0 | `.../MapServer/WMS` or `.../MapServer/WMTS` | Legacy OGC raster map services |
| **Server-rendered maps (OGC)** | OGC API Maps | `/ogc/maps` | Standards-based rendered map images |
| **Power BI/Excel** | OData v4 | `/odata` | BI integration |
| **Web Maps (MapLibre/OpenLayers)** | Vector Tiles + TileJSON | `/tiles/{layerId}/{z}/{x}/{y}.mvt` | Fast rendering with auto-styles |
| **Esri raster/image workflows** | ImageServer | `/rest/services/{id}/ImageServer` | Esri raster compatibility |
| **Esri geometry operations** | Geometry Service | `/rest/services/geometry` | Buffer, simplify, project, intersect, union, clip, difference, area, length |
| **Esri geoprocessing** | GPServer | `/rest/services/{id}/GPServer` | Esri GP compatibility (job status polling and cancellation; submitJob route registered, pending process catalog; result retrieval route registered, pending execution-engine/result-storage support) |
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
- Features: `json` (GeoServices), `geojson`, `pbf` (Protocol Buffers), `fgb` (FlatGeobuf), `geobuf` (when store supports native output), `parquet` (GeoParquet with WKB geometry)

**Typical use cases:**
- ArcGIS Pro connectivity
- ArcGIS SDK clients
- Legacy FeatureServer integrations
- Analytics workflows (GeoParquet export)

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
- Search supports GET and POST with `fields`, `sortby`, and CQL2 filtering (`filter` plus `filter-lang`).

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
|-- /collections/{id}/styles/{styleId}/map
|-- /collections/{id}/map/tiles
```

**Output formats:** PNG, JPEG, TIFF

**Typical use cases:**
- Server-rendered maps via open standards
- Dynamic map image generation without Esri dependencies
- OGC-compliant map rendering workflows

---

## **WMS 1.3 / WMTS 1.0**

**Best for**: Legacy OGC map services (QGIS, GeoServer ecosystem clients)

**Endpoint structure:**
```
/rest/services/{id}/MapServer/WMS    (or /ogc/services/{id}/wms)
|-- ?service=WMS&request=GetCapabilities
|-- ?service=WMS&request=GetMap
|-- ?service=WMS&request=GetFeatureInfo

/rest/services/{id}/MapServer/WMTS   (or /ogc/services/{id}/wmts)
|-- ?service=WMTS&request=GetCapabilities
|-- ?service=WMTS&request=GetTile
|-- ?service=WMTS&request=GetFeatureInfo
```

**Limitations:** WMTS currently supports WebMercatorQuad tile matrix set only.

**Typical use cases:**
- QGIS WMS/WMTS layer connections
- Desktop GIS clients expecting legacy OGC services
- INSPIRE/SDI compliance requiring WMS/WMTS endpoints

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

**Limitations:** `query` filtering happens in memory after the catalog is read; spatial filters and `orderByFields` are not pushed to PostGIS yet. `computeStatisticsHistograms` does not honour AOI clipping. `legend` uses a fixed viridis ramp keyed off the primary raster band-1 statistics. `computeClass` validates and plans `Identity`/`Stretch`/`Clip` chains (max depth 8) but does not execute the chain — the planner is not yet wired into `exportImage`/`identify`. See the [ImageServer Matrix](image-server-matrix.md) for full parameter coverage.

**Typical use cases:**
- ArcGIS Pro raster rendering
- Image export and pixel value queries
- Tiled image serving
- Raster catalog discovery (footprint polygons + per-item attributes via `query`)
- Per-band statistics and histograms for analytics dashboards
- Layer legend swatches for ArcGIS Maps SDK clients
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

**Best for**: Esri geoprocessing workflows (job status polling and cancellation functional; submission and result retrieval routes registered, pending process catalog)

**Endpoint structure:**
```
/rest/services/{service-name}/GPServer
|-- /                                      (service info — available tasks)
|-- /{taskName}                            (task info — parameters, data types)
|-- /{taskName}/execute                    (synchronous execution — 501 pending)
|-- /{taskName}/submitJob                  (async job submission)
|-- /{taskName}/jobs/{jobId}               (job status polling)
|-- /{taskName}/jobs/{jobId}/results/{paramName}  (named output result)
|-- /{taskName}/jobs/{jobId}/cancel        (cancel in-flight job)
```

**Output formats:** JSON (Esri camelCase convention)

**Limitations:** Synchronous `execute` returns 501 until canonical `ExecutePlan` is wired (#721). Task info and `submitJob` return 501 until a formal process catalog is available for task resolution. Service info returns stub metadata (empty task list) until the catalog is formalized. Unsupported GP environment controls (`env:*`, `context`) are rejected with 400. Per-parameter result retrieval route is registered but actual output retrieval is pending execution-engine and result-storage support.

**Typical use cases:**
- ArcGIS Pro / SDK geoprocessing tool connectivity
- Async analysis workflows with job lifecycle polling
- Per-parameter result retrieval (route registered; output retrieval pending execution-engine support)

**Contract notes:**
- GPServer is a protocol adapter over the canonical process runtime; it does not define its own job or result storage.
- `execute`, `submitJob`, and `cancel` accept both GET and POST per Esri GP convention. All other endpoints are GET-only. For POST requests, query-string parameters are read first and then overlaid by form-encoded body values (body takes precedence on key collision).
- `submitJob` currently returns 501 because task resolution requires a formal process catalog (not yet available). Once the catalog is wired, `submitJob` will return HTTP 202 (Accepted) with the job envelope (`jobId`, `jobStatus`), differing from Esri's convention of HTTP 200 but carrying the same response body shape.
- Canonical `ExecutionJobStatus` maps to Esri status strings: `Queued`→`esriJobSubmitted`, `Provisioning`→`esriJobWaiting`, `Running`→`esriJobExecuting`, `Succeeded`→`esriJobSucceeded`, `Failed`→`esriJobFailed`, `Cancelled`→`esriJobCancelled`.
- Parameter translation converts Esri GP types (GPDataFile, GPLinearUnit, GPFeatureRecordSetLayer, etc.) to canonical opaque step inputs and maps `ArtifactKind` back to GP data types on output.
- Route binding is validated: job status/result/cancel endpoints verify the `serviceId` and `taskName` match the stored job metadata, returning 404 for mismatches. Jobs submitted via other protocols (e.g. gRPC) are rejected to prevent cross-protocol access.
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
- **CITE conformance results** for OGC standards (automated in CI, 100% pass rate required).
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
- WMTS 1.0: 118/118 tests
- OGC API Maps: 32/32 tests
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
