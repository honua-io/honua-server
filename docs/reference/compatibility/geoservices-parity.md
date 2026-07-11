# GeoServices REST parity

Honua serves the Esri GeoServices REST API at compatible paths so ArcGIS Pro, the
ArcGIS SDKs, Esri Leaflet, and the ArcGIS API for Python can connect without
modification. This page summarizes endpoint-level parity per service; the
machine-readable source is
[`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json).

Status vocabulary:

- **Implemented** — the Esri operation exists at a compatible path and the documented behavior is supported.
- **Partial** — the operation exists, but only a subset of documented parameters or behavior is supported.
- **Stub** — the route exists and returns the spec-shaped response, but the backing data model is deferred; read-style stubs return empty/`false` results and mutation stubs return HTTP 400 rather than fabricating success.
- **Not implemented** — the operation is not exposed.

## Service summary

| Service | Parity | Implemented surface | Headline gaps |
| --- | --- | --- | --- |
| [FeatureServer](#featureserver) | Partial | Query (7 output formats), edits, attachments, related records, domains, replication with incremental change tracking, contingent values (service operation + layer `contingentValuesDefinition`), subtype-derived layer `types`/editing templates, 3D (Z/M) queries + `hasZ`/`hasM` layer advertisement, true-curve input densification (circular-arc/Bézier `curvePaths`/`curveRings` → linear), estimates, calculate, validateSQL, append, bins/date bins/top features, generateRenderer, spatial analytics extensions (Pro) | Standalone shared-template store and utility-network/asset data models (deferred — no canonical backing), lossless true-curve storage/re-emission (input densifies, output stays linear), automated contingent-value import |
| [MapServer](#mapserver--wms--wmts) | Partial | Export, identify, find, legend/queryLegends, query, mapLayer + allowlisted workspace (`dataLayer`) dynamicLayers, allowlisted `joinTable` dynamicLayers joins (left-outer/inner, application-side, surfaced through identify + dynamicLayer metadata), tiles with cloud-storage cache, storage-backed exportTiles (ZIP + Esri exploded-cache TPK), generateKml, WMS 1.3/1.1.1, WMTS 1.0 (WebMercatorQuad + WorldCRS84Quad gridsets) | Esri compact TPKX cache + async exportTiles job child resources, dynamicLayers `queryTable` (raw SQL) data sources, several child resources |
| [ImageServer](#imageserver) | Partial | Service metadata, exportImage, identify, tile with cloud-storage cache, catalog query/item and item image/info resources, find (orientation-ranked when sensor metadata present), measure (Basic + DEM-backed height), statistics/histograms, getSamples (including registered-Zarr slice sampling), queryBoundary, computePixelLocation, project (incl. RPC image-CS warp), storage-backed exportTiles (ZIP + Esri exploded-cache TPK), dynamic computeCacheInfo, legend, admin raster CRUD (delete/update via admin API), WMTS 1.0 GetCapabilities/GetTile/GetFeatureInfo, WCS 2.0.1 KVP | Remaining source-native raster item metadata (metadata XML, thumbnail, imageSupportData), Esri compact TPKX cache + async exportTiles job child resources, shadow/photogrammetric analytics, Esri compact/generated tile-cache management, multi-factor camera-model find scoring, ImageServer identify/exportImage and classic-WCS reads of resolved multidimensional Zarr slices, non-allowlisted (raw sensor/orientation) mosaic inputs, automatic seamline generation/editing, inline ColorRamp objects; ImageServer WMTS is WebMercatorQuad only |
| [Geometry Service](#geometry-service) | Complete | Root metadata plus all 23 ArcGIS geometry operations | None at operation level; parameter-level caveats only |
| GeocodeServer | Partial | Service metadata, findAddressCandidates, reverseGeocode (incl. provider-dependent `distance`/`featureTypes`), suggest, geocodeAddresses, `outFields` projection, `outSR` reprojection, `magicKey` suggest→candidate round-trip (self-issued signed token, all providers), `category` filtering (all providers, on provider-supplied address type) | `forStorage`/`matchOutOfRange` (re-deferred; no backing provider models them) — see [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md) |
| GPServer | Partial | PrintingTools; generic adapter with catalog-backed task metadata, async submitJob, synchronous `execute` for the deterministic single-geometry `geometry.*`/`conversion.geometry-format` family (inline over the canonical job runtime), job status/cancel/results over 96 seeded processes | Heavyweight/layer-scoped tasks stay async-only (their `execute` returns a 400 pointing at submitJob); `env:*` rejected on submitJob (sync `execute` honors `env:outSR`) — see [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md) |
| [NAServer](#naserver) | Partial | GET/POST route, service-area, closest-facility, OD-cost-matrix (cost-only), and location-allocation (minimize-impedance / maximize-coverage) solves over the shared routing provider, incl. point/line/polygon barriers and validated `travelMode`; addressable network-dataset registry with admin editing of the dataset mapping/metadata | OD-matrix geometry output, additional location-allocation problem types, and edge/vertex/turn-restriction feature editing with topology rebuild are deferred; only the driving travel mode is genuinely routable (single stored cost weight) |
| Portal Sharing | Partial | `generateToken` opaque tokens consumable on `/rest/services/*`; OAuth2 **named-user** flow at `/sharing/rest/oauth2/{authorize,callback,token}` — `authorization_code` + PKCE and rotating `refresh_token` (OIDC-delegated) — the opt-in `client_credentials` (service-to-service) grant with optional pluggable IdP/OIDC federation (#1889), optional JWT access tokens + RFC 7662 `oauth2/introspect` (#1890, opt-in), and the community-group + item-sharing surface (createGroup/groups/{id}/addUsers/removeUsers, content/items/{id}/share·unshare; #1868) | sharing surface is an in-memory overlay (no durable group store yet); JWT/introspection and IdP federation are opt-in — see [authentication](../../guides/secure/authentication.md) |

## FeatureServer

Esri spec: [Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata; layer metadata | Implemented | Dynamic capabilities string; `editFieldsInfo`, `editingInfo`, `timeInfo`, `allowGeometryUpdates`, `supportsStatistics`, normalized `supportedQueryFormats` incl. binary formats. Subtype-bearing layers surface Esri-style editing `types` (one per subtype, each carrying per-field `domains` and an editing template whose `prototype` seeds the subtype code + field default values) and a layer-level `contingentValuesDefinition`, both projected from the canonical Metadata v2 graph and omitted byte-stably when unauthored. The layer-root `templates` array stays empty (in the Esri model, subtype-driven templates live inside `types[]`; no standalone template authoring exists on the canonical graph). |
| Query (service + layer), queryDomains, relationships, getEstimates (service + layer) | Implemented | Service-level query delegates to a target layer via `layerId`/`layers`. |
| applyEdits (service + layer), addFeatures, updateFeatures, deleteFeatures, append (service + layer), calculate, validateSQL | Implemented | Multi-layer batch edits; `rollbackOnFailure` defaults `false` for applyEdits, `true` for standalone endpoints; deleteFeatures supports `objectIds`, `where`, and spatial filters. |
| queryRelatedRecords, queryAttachments | Implemented | Full filter facets (`attachmentTypes`, `keywords`, `size`, `definitionExpression`); `globalIds` rejected with 400 (integer object IDs only). |
| addAttachment, updateAttachment, deleteAttachments, attachment download | Implemented | Form-data upload; binary download at `.../{featureId}/attachments/{attachmentId}`. |
| createReplica, extractChanges, synchronizeReplica, unRegisterReplica | Implemented | DB-level incremental change tracking (Postgres): a trigger records every insert/update/delete against a monotonic generation counter; first sync delivers a one-time snapshot-as-adds (including a baseline seeded for pre-change-tracking data) and every later sync is a pure delta from the recorded server generation. Non-Postgres backends (DuckDB/MySQL/SQL Server) have no change tracker and fall back to a full snapshot on each sync. |
| generateRenderer | Implemented | Simple renderer by default; `classificationDef` generates class-breaks (equal interval, quantile, natural breaks, standard deviation) or unique-value renderers. |
| queryBins, queryDateBins, queryTopFeatures | Implemented | queryDateBins requires the `temporal.histogram` entitlement. |
| queryClusters, spatialJoin, queryBufferAggregate, queryDensity, temporalExtent | Implemented (Honua extensions) | No Esri equivalent. Analytics ops are Pro-entitlement-gated (`analytics.*`, HTTP 402 when inactive), return GeoJSON in EPSG:4326, and are bounded by configurable limits. Return 501 on stores without an analytics reader (DuckDB, MySQL/MariaDB). |
| queryContingentValues; layer `contingentValuesDefinition` | Implemented | Serves per-layer contingent value definitions (cross-field value-combination constraints) from the Metadata v2 graph (`contingentValueGroups` on each resource); layers with none, and services with none, return an empty `contingentValuesDefinitions` collection. The same definition is also surfaced on the layer metadata document as `contingentValuesDefinition` (omitted when the layer has none), mirroring how Esri exposes contingent values at the layer level. Authored through the layer metadata graph; automated import of contingent values from Esri sources is not yet wired (deferred). |
| sharedTemplates (+ query/add/update/delete), htmlPopup, image | Stub | Routes exist and return spec-shaped documents, but Honua has no shared-template, popup, or feature-image store — reads return empty documents; mutations return 400. |
| hasAssets, queryAssets, cleanupAssets, uploadAssets, convert3D, query3D, metadata/update | Stub | No layer asset store or 3D pipeline; reads return empty/`false`, mutations/conversions return 400. The full utility-network/asset data model (asset groups/types, association rules) is deferred: there is no canonical Metadata v2 model backing it, so no empty layer-level asset descriptor is fabricated. The portion of the data model the canonical graph *does* carry — field domains, subtypes, subtype-derived `types`/templates, and contingent values — is surfaced on the layer metadata (see the layer-metadata and `contingentValuesDefinition` rows). |
| cleanupChangeTracking | Not implemented | No change-tracking tables exist to clean up. |

### Layer query parameters

| Area | Status | Notes |
| --- | --- | --- |
| `where`, `objectIds`, spatial filters (`geometry`, `geometryType`, `spatialRel`, `distance`, `units`), `inSR`/`outSR`, pagination, `outFields`, `orderByFields`, output flags (`returnGeometry`, `returnCentroid`, `returnIdsOnly`, `returnCountOnly`, `returnExtentOnly`, `returnZ`, `returnM`), `returnDistinctValues`, `outStatistics` + `groupByFieldsForStatistics` + `having`, `time`/`timeRelation`, `geometryPrecision`, `maxAllowableOffset`, `nearestCount`/`returnDistance` | Implemented | ArcGIS SQL `where` parser (comparison/logical/arithmetic operators, `IS [NOT] NULL`, `[NOT] LIKE [ESCAPE 'c']`, `[NOT] IN`, `[NOT] BETWEEN`, searched/simple `CASE WHEN … THEN … [ELSE …] END`; string/math/date-time functions including `SUBSTRING(x FROM s [FOR n])`, `POSITION(sub IN str)`, `EXTRACT(field FROM source)` over an allowlisted field set (`YEAR`, `MONTH`, `DAY`, `HOUR`, `MINUTE`, `SECOND`, plus `DOW`, `DOY`, `QUARTER`, `WEEK`, `EPOCH`), and `CAST(value AS type)` over an allowlisted type set; all operands — including `CASE` branch values, the `LIKE` escape character, and simple-`CASE` selector comparisons — parameterized). Re-deferred: subqueries (correlation, planning-cost, and injection surface make a parameter-safe, bounded implementation a separate effort), and `EXTRACT` fields outside the allowlist above. KNN via `nearestCount`; statistics support COUNT/SUM/MIN/MAX/AVG/STDDEV/VAR with GROUP BY and post-aggregation HAVING. `returnCentroid` emits polygon centroids for GeoServices JSON responses and advertises `supportsReturningGeometryCentroid` on polygon layers. |
| Output formats `f=json/pjson/geojson/pbf/fgb/geobuf/parquet/arrow` | Implemented | GeoJSON/GeoParquet/GeoArrow require EPSG:4326 when geometry is present; `parquet`/`arrow` always strip M values; `fgb`/`geobuf` need native store support and ignore precision/simplification parameters; special query modes always return JSON. |
| `resultType`, `sqlFormat`, `gdbVersion`, `quantizationParameters`, `datumTransformation` | Partial | `quantizationParameters` is honored for `f=json`: the featureSet emits a `transform` (`originPosition`/`scale`/`translate`, `upperLeft` or `lowerLeft`) and geometry coordinates become integer grid deltas; layer metadata advertises `supportsCoordinatesQuantization=true`, and `f=pbf` likewise returns a quantized `transform`. Quantization is ignored for `f=geojson` (GeoJSON has no quantization). `gdbVersion`/`datumTransformation` are accepted for client compatibility and ignored. |
| `returnExceededLimitFeatures` | Accepted (no-op) | Accept-and-ignore for interop (#1460): the ArcGIS Maps SDK for .NET always sends it. Honua already returns the truncated page plus `exceededTransferLimit=true`, so both the default and an explicit `false` return the same page and flag. |
| `returnZ`, `returnM` (3D/measured queries) | Implemented (Postgres) | `returnZ`/`returnM` carry the higher ordinates through the storage read: the Postgres geometry read switches to extended WKB (EWKB) when either flag is set, so stored Z/M survive to the output and are emitted on the GeoServices JSON/PBF geometry; without the flag the read stays 2D OGC WKB (byte-identical to prior behavior). The layer descriptor advertises `hasZ`/`hasM` from the authored Metadata v2 display flags (emitted only when true, so 2D layer documents stay byte-stable — #1877 Part C). GeoJSON output still rejects `returnM=true` (RFC 7946 has no M); `parquet`/`arrow` strip M. |
| `returnTrueCurves` | Partial | **Input** is densified: true-curve geometry supplied as `curvePaths`/`curveRings` on `applyEdits` or a query spatial filter is densified to linear vertices before storage — circular-arc (`{"c":…}`) and cubic-Bézier (`{"b":…}`) segments are interpolated (bounded vertex count); elliptic-arc (`{"a":…}`) segments are rejected with an explicit message (#1877 Part A). **Output** stays linear: NTS/WKB cannot represent a true curve, so densified geometry is what is stored and queried back — there is no `curve` re-emission from stored linear geometry. The parse↔serialize round-trip is proven at the converter level (`CurveGeometryConverter.Parse`/`Serialize`). On query, `returnTrueCurves=true` is accepted but returns the densified linear geometry with correct `hasZ`/`hasM`; Honua still advertises `supportsTrueCurve=false`. Lossless curve storage/re-emission remains deferred (requires a sidecar curve-storage design) — `Refs #1877` Part B. |

### applyEdits parameters

`adds`/`updates`/`deletes` and `rollbackOnFailure` are implemented (object-ID-keyed).
`useGlobalIds`, `gdbVersion`, `returnEditMoment`, and `attachments` are rejected with
400; session/async/upload-style parameters (`assetMaps`, `sessionID`, `async`,
`editsUploadId`, ...) are silently ignored. queryRelatedRecords rejects
`gdbVersion` and `historicMoment` with 400 and accepts/ignores `returnTrueCurves`.

#### Idempotency (at-most-once edits)

`applyEdits`, `addFeatures`, `updateFeatures`, and `deleteFeatures` (layer-level) honour
an optional `Idempotency-Key` request header so a client can retry an edit on a transient
failure without creating duplicate features (#2250). When the header is present, the first
request that commits at least one row records its full response keyed by the
`(principal, serviceId, layerId, key)` tuple; any later request that repeats the same key
within the dedupe window (24 hours) replays that original response — the same `objectId`s,
with `success: true` — instead of re-applying the edit. This is the server-side contract the
Honua SDKs and field clients rely on for true at-most-once semantics.

Contract details:

- The key is a client-generated, stable string of at most 200 characters with no control
  characters; an empty, oversized, or malformed header is rejected with a 400.
- The key is scoped to the authenticated principal, service, and layer — one caller's key
  can never replay another caller's response, and the same key on a different layer is a
  distinct edit.
- Only requests that committed rows are recorded; a fully-failed/no-op request is not
  recorded, so it can be retried fresh.
- The store is Redis-backed when an `IDistributedCache` is configured (durable across
  replicas) and falls back to an in-process window on a single node. Because
  `IDistributedCache` exposes no atomic reserve, two *truly concurrent* identical requests
  can both miss the window before either records its response; the header guarantees
  at-most-once for the common *sequential-retry* pattern, not for simultaneous in-flight
  duplicates.

### applyEdits per-feature error codes

`applyEdits` (and the standalone `addFeatures`/`updateFeatures`/`deleteFeatures`
endpoints) return HTTP 200 with a per-feature result in `addResults`/`updateResults`/
`deleteResults`. A failed result carries `success:false` and an `error` object
`{ "code": <int>, "description": "<safe message>" }`. The `code` is a **stable,
machine-readable classification** so clients can branch on the *kind* of failure without
parsing `description` (descriptions are sanitized free-form text and may change). These
codes are the contract; once published, a code does not change meaning.

| `error.code` | Class | Meaning |
| ---: | --- | --- |
| `1000` | Generic | Unclassified / fallback failure (unexpected provider error). |
| `1001` | Invalid object id | Update/delete object id missing, non-numeric, or the object-id field could not be resolved (request-shape error). |
| `1002` | Not found | The `update` target feature does not exist (or is hidden by row-level security). |
| `1003` | Delete conflict (delete-delete) | The `delete` target was already removed, typically by another writer. |
| `1004` | Update conflict (update-update) | Optimistic-concurrency / version mismatch: the row changed since the caller read it. Clients may re-read and retry. |
| `1005` | Locked | The feature is locked by another editor/session (HTTP 423 semantics). Reserved for lock-aware providers; the default edit path produces no locks yet. |
| `1006` | Validation failed | Invalid attributes/geometry, attribute-rule violation, or invalid contingent-value combination (request-shape error). |
| `1007` | Not permitted | Denied by an owner-based edit policy (non-owning/non-admin or anonymous caller). |
| `1008` | Rolled back | The operation was otherwise applicable but the transaction rolled back because a sibling operation failed under `rollbackOnFailure=true`. |

The classification is deterministic: it is derived from the edit pipeline's typed outcome
(e.g. a writer precondition failure → `1004`), never from the error message. The codes are
defined in `GeoServicesEditErrorCodes` and exercised per class by
`FeatureServerApplyEditsConflictCodeTests`.

## MapServer + WMS / WMTS

Esri spec: [Map Service](https://developers.arcgis.com/rest/services-reference/enterprise/map-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata, layer metadata, allLayersAndTables, queryDomains, feature child resource | Implemented | Includes `drawingInfo`, `tileInfo`, scale ranges; `parentLayerId`/`subLayerIds` are always flat (`-1`/`null`) — no group layers. |
| dynamicLayer child resource | Partial | `GET .../MapServer/dynamicLayer` returns layer metadata for `source.type=mapLayer` and `source.type=dataLayer` definitions, including request-scoped `id`, `definitionExpression`, and `drawingInfo`. A `dataLayer` source references a registered workspace (`dataSource.workspaceId`) and table (`dataSource.dataSourceName`); it is accepted only when `GeoServices:MapServer:DynamicLayers:WorkspaceLayersEnabled` is set, the workspace is allowlisted, and the table is already materialized by a published resource on that workspace (RBAC is enforced through the shared pipeline). A `dataSource.type=joinTable` joins two such allowlisted layers on `leftTableKey`/`rightTableKey` (`joinType` left-outer/inner); the merged field schema is reported with right-source fields qualified as `{table}.{field}`, and the right layer's access policy is enforced. Dynamic query tables (`dataSource.type=queryTable`, raw SQL) remain deferred. |
| export | Implemented | `bbox`, `size`, `dpi`, `format` (png/png8/png24/png32/jpg/gif), `transparent`, `layers`, `bboxSR`/`imageSR`, `layerDefs`, `dynamicLayers`, `time`/`layerTimeOptions`, `backgroundColor`, `f=image\|json\|pjson`. `dynamicLayers` supports `source.type=mapLayer`, allowlisted `source.type=dataLayer` workspace data layers, and allowlisted `joinTable` joins, request-scoped `definitionExpression`, and `drawingInfo.renderer` overrides for simple, unique-value, and class-breaks renderers. For a `joinTable` source, export renders the left (geometry) layer and enforces both layers' access policies; joined right-source attributes only affect attribute-bearing operations (identify, dynamicLayer metadata), not the rendered image. `gdbVersion`, `maxAllowableOffset`, `geometryPrecision`, `returnZ`, `returnM` are accepted but ignored. |
| identify, find | Implemented | All geometry types, `mapExtent`, `imageDisplay`, `tolerance`, `layerDefs`, `dynamicLayers` (`mapLayer`, allowlisted `dataLayer` workspace sources, and allowlisted `joinTable` joins), `time`/`timeRelation`. For a `joinTable` source, identify returns the left feature's attributes plus the first matching right row's attributes qualified as `{table}.{field}` (inner joins drop unmatched left features); the join is materialized application-side via a parameter-safe key lookup and both layers' access policies are enforced. `find` searches string fields with SQL LIKE. |
| Layer query, service query, generateRenderer (service + per layer), queryRelatedRecords, queryAttachments | Implemented | Thin adapters delegating to the FeatureServer handlers — same parameter coverage as the [FeatureServer section](#featureserver). Service-level `generateRenderer` selects the target layer via `layer` or `layerId`. |
| legend, queryLegends, generateKml, tile | Implemented | Legend swatch images at both legend routes, including dynamic layer `drawingInfo.renderer` overrides for simple, unique-value, and class-breaks renderers; `f=kml`/`f=kmz`; dynamic PNG tiles at `.../MapServer/tile/{z}/{y}/{x}` use configured cloud file storage (`local`, S3, Azure Blob) as a deterministic loose-object read-through/write-through cache. |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad PNG tiles to configured cloud file storage (`local`, S3, Azure Blob). `storageFormat=zip` (default) writes a flat `{z}/{x}/{y}.png` ZIP; `storageFormat=tpk` (or `tilePackage=true`) writes an Esri exploded-cache **TPK** package (`v101/<cache>/_alllayers/Lzz/Rrrrrrrrr/Cccccccc.png` + `conf.xml`/`conf.cdi`) readable by ArcGIS Pro / Runtime SDKs / QGIS. The proprietary compact bundle (**TPKX**, `storageFormat=tpkx`/`compact`) is rejected with a 400 and remains deferred (requires the `.bundle`/`.bundlx` binary index format). The async `exportTiles` job child-resources (`submitJob`/`jobs/{id}`) are also deferred — they need a dedicated canonical job type, progress record, AOT progress-context registration, and background worker (its own PR per the #1660 umbrella). |
| WMS | Implemented | WMS 1.3.0 and 1.1.1 GetCapabilities/GetMap/GetFeatureInfo (KVP) at `.../MapServer/WMS` and `/ogc/services/{serviceId}/wms`. Time-aware layers advertise a `time` dimension. WMS 1.3 is CITE-certified (199/199); 1.1.1 has no CITE evidence yet. |
| WMTS | Partial | GetCapabilities/GetTile/GetFeatureInfo (KVP + RESTful) at `.../MapServer/WMTS` and `/ogc/services/{serviceId}/wmts`. GetTile and GetFeatureInfo both resolve the requested tile matrix set through the shared `ITileMatrixSetRegistry`, so the built-in WebMercatorQuad and WorldCRS84Quad (CRS84/EPSG:4326) gridsets and operator-defined custom gridsets (#1791) are served end-to-end; GetFeatureInfo computes the clicked pixel from the gridset's own origin, cell size and matrix dimensions (#1873) rather than Web Mercator constants, and unsupported gridsets are rejected with `InvalidParameterValue`. WebMercatorQuad behaviour is byte-identical to the prior implementation. WMTS 1.0 is CITE-certified (60/60). |
| queryAnalytic, image/KML-image/job child resources, `exts/*` | Not implemented | |

## ImageServer

Esri spec: [Image Service](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/).
Routes are layer-scoped: `{id}` in `GET /rest/services/{id}/ImageServer` is the
raster layer identifier.

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Implemented | Aggregate mosaic extent/statistics, `timeInfo` when acquisition dates exist, output-cached. Dynamic (`singleFusedMapCache: false`) by default; opt in to a WebMercatorQuad `tileInfo` for tiled Esri clients via `GeoServices:ImageServer:TileMetadata:Enabled` (#1648). `objectIdField`/`fields`/`rasterFunctionInfos` root properties not yet populated. |
| tile, computeHistograms, getSamples | Implemented | PNG/JPEG/TIFF tiles, zoom 0–28, multi-raster mosaic. Dynamically generated tiles auto-apply a MinMax display stretch when the source raster is not 8-bit (elevation/analytic/float), so analytic layers are viewable; at low zoom levels the tile read path prefers a persisted reduced-resolution overview pyramid and otherwise coarsens the source on-the-fly toward the tile's ground resolution before resampling; pre-rendered cached tiles are served as stored. Tile GETs use configured cloud file storage (`local`, S3, Azure Blob) as a deterministic loose-object read-through/write-through cache; getSamples caps at 1000 samples. For layers mapped to a registered Zarr coverage, `multidimensionalDefinition` resolves time, vertical/elevation, or other declared coordinate values through the shared axis indexer and reads the pinned slice through the Zarr subset pipeline (#1869/#1939). Layers without a readable Zarr backing store return an explicit 501 rather than silently sampling a dimension-collapsed raster. |
| WMTS | Partial | OGC WMTS 1.0 GetCapabilities/GetTile (KVP + RESTful) at `.../ImageServer/WMTS` for numeric and service-name ImageServer routes. Uses the ImageServer tile/cloud-cache pipeline, advertises `WebMercatorQuad`, serves `image/png`, `image/jpeg`, and `image/tiff` tiles, answers GetFeatureInfo (application/json or text/xml) with the pixel band values at the requested tile pixel, and advertises a `TIME` dimension for temporal layers (GetTile/GetFeatureInfo honour a `TIME` parameter). Non-WebMercator matrix sets are deferred. |
| exportImage | Partial | JSON envelope with temporary `href` by default; `f=image` streams bytes. `bandIds`, `noData`, mosaic + single-instant `time` supported; `pixelType` validated but not applied. `renderingRule` executes a `Stretch` chain — types MinMax (5), StandardDeviation (3), and PercentClip (6), with optional per-band `Statistics` — linearly rescaling each band to 8-bit; `Identity` is a no-op pass-through. A `Colormap` raster function maps single-band values to an RGBA image via interpolation, from explicit `[value, r, g, b]` stops or a recognized `ColorrampName` (e.g. `Elevation`, `Red to Green`, `Viridis`) resolved to anchor stops over the display range; it may wrap a `Stretch`. A `Clip` raster function masks the output to its `ClippingGeometry`/`Extent` (Esri envelope or polygon) as a second clip after the export `bbox`; `ClippingType=1` (clip inside / keep outside) inverts the mask. An `ExtractBand` raster function selects/reorders the output bands by 0-based `BandIds` (supersedes the `bandIds` parameter when both are present). A `BandArithmetic` raster function derives a single analytic band from two source bands given as 0-based `BandIndexes` via fixed, injection-safe formulae (closed enum, no free-form expressions): NDVI (`Method=3`, `[visible, infrared]`), SAVI (`Method=5`, `[visible, infrared]`, soil factor L=0.5), and NDWI (`Method=9`, `[green, NIR]`), each zero-denominator guarded; it is applied between band selection and stretch, and when present only an explicitly-supplied `Stretch` with `Statistics` bounds is applied (auto-derived whole-raster bounds are skipped). Inline terrain raster functions `Hillshade` (Esri `Azimuth`/`Altitude`/`ZFactor` defaults 315/45/1), `Slope`, and `Aspect` derive a single analytic band from one elevation band via injection-safe PostGIS surface functions (`ST_HillShade`/`ST_Slope`/`ST_Aspect`); a renderingRule may not combine band arithmetic with a terrain function. These also apply on the **mosaic** export path. Other stretch types, the histogram-equalize / unrecognized-colorramp-name / extract-band-by-name variants, and unrecognized band-arithmetic methods return 501; unknown functions return 400. `bmp`/`gif` rejected with 400. |
| identify | Partial | Point/envelope/polygon (area geometries identify at the envelope centroid); `returnCatalogItems`; `pixelSize` echoed but pyramid selection deferred. `renderingRule` is now applied: when supplied, the returned value reflects the **rendered** pixel (post clip/band-arithmetic/terrain/stretch/colormap, matching exportImage) instead of the raw source value. Omitting `renderingRule` preserves the raw-value contract. Unsupported chains surface 400/501 exactly as on exportImage. |
| query (raster catalog) | Partial | Esri-compatible catalog features with `where`, spatial filter, `outSR`, `orderByFields`, `outFields`, paging, and shaping flags. Filters run in-memory at footprint-envelope granularity. |
| raster catalog item (`{rasterId}`) | Implemented | Returns one catalog item as an Esri feature with the canonical catalog attributes and footprint geometry. Supports numeric-layer and service-name routes and applies the same access policy and field masking as catalog query. |
| raster catalog item children (`{rasterId}/image`, `{rasterId}/info`, `{rasterId}/info/keyProperties`, `{rasterId}/info/histograms`) | Implemented | Item image delivery pins the requested raster through the canonical export pipeline. Item metadata comes from the canonical raster store, uses the same layer access policy, validates JSON response formats, and returns an Esri 404 document for unknown raster IDs. |
| find | Partial | Finds raster catalog images whose footprints contain `toGeometry`, with `objectIds`, `where`, `inSR`, `fromGeometry` validation, and `maxCount` support. Results are **orientation-ranked** when candidates carry exterior-orientation metadata (`raster_sensor_metadata.exterior_orientation`): images are ordered by smallest off-nadir angle, then footprint distance, then ObjectId. When no candidate has orientation metadata (the common case for plain COGs), it falls back to pure footprint-distance ranking. Multi-factor camera-model scoring and per-request tunable weighting are deferred. |
| measure | Partial | Supports Basic map-space point, distance/azimuth, area/perimeter, and centroid measurements over point, envelope, and polygon Esri JSON. **DEM-backed height** (`esriMensurationHeightFromBaseAndTop`) is supported for rasters whose `raster_sensor_metadata.dem_source` resolves to a DEM layer: it differences the sampled ground elevation at the base and top points and emits the real `sensorName`. Shadow-based height (`*Shadow`), photogrammetric height, and the other 3D mensuration operations still return 501; height ops also return 501 when the raster has no DEM/sensor metadata or the DEM does not cover the supplied points (never a faked value). |
| computeStatisticsHistograms | Implemented | Per-band stats + histograms with `rasterIds`, `bandIds` (Honua extension), `mosaicRule`, `time`, `histogramParameters.size`. An AOI `geometry` clips the analysis to that area (envelope-scoped) for both single rasters and mosaics; without a geometry the full selected raster/mosaic is analysed. `renderingRule` is now applied (#1871): the `Stretch`/`Colormap`/`Clip` chain exportImage/identify execute is run against the pixels before per-band stats/histograms are computed, so the reported values describe the rendered output (the stretch is bounded from the source raster's persisted statistics, matching exportImage). Omitting `renderingRule` preserves the raw-source statistics contract. `ExtractBand`/`BandArithmetic` (which change the band set) and the same unsupported chains as exportImage return 501; unknown functions return 400. |
| queryBoundary | Implemented | Returns Esri `shape` + approximate `area` from the aggregate raster footprint extent, with `outSR` honoured when the shared CRS pipeline can transform the extent. NoData-trimmed boundaries are deferred. |
| computePixelLocation | Implemented | Converts point geometries to raster pixel column/row coordinates using the raster geotransform, with extent fallback and `rasterId`/input SR support. |
| project | Partial | Reprojects Esri JSON geometry arrays between supported spatial references via the shared CRS pipeline and returns the Image Service `{ "geometries": [...] }` response shape. Envelope geometry remains envelope-shaped. `datumTransformation` is honored: a client-supplied geotransformation WKID (or composite `geoTransforms`) is resolved through the shared Esri WKID → PROJ pipeline catalog and applied via 3-argument `ST_Transform`, matching the Geometry Service; an unknown/inapplicable WKID returns 400. The image-coordinate-system `transformation` parameter now warps **point** geometries between image (pixel sample/line) space and map space using the raster's RPC sensor model (`raster_sensor_metadata.rpc`): geometries are treated as image coordinates, mapped to ground (EPSG:4326) via the RPC offset/scale normalisation, then reprojected into `outSR`. It returns 400 when the layer's raster carries no RPC metadata, and supports points only for the first increment (non-RPC sensor models and polygon/multi-step chaining are deferred). |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad image tiles to configured cloud file storage (`local`, S3, Azure Blob). `storageFormat=zip` (default) writes a flat `{z}/{x}/{y}.ext` ZIP; `storageFormat=tpk` (or `tilePackage=true`) writes an Esri exploded-cache **TPK** package (`v101/<cache>/_alllayers/...` + `conf.xml`/`conf.cdi`). The proprietary compact **TPKX** form (`storageFormat=tpkx`/`compact`) is rejected with a 400 and remains deferred. The async `exportTiles` job child-resources (`submitJob`/`jobs/{id}`) are deferred — its own PR per the #1660 umbrella. |
| computeCacheInfo | Partial | Returns a spec-shaped `cacheInfo` extent for dynamic ImageServer layers and intentionally omits `tileInfo`/`cacheType` until a formal Esri-style raster tile cache is configured. The loose-object tile GET cache is operational storage, not advertised cache metadata. |
| computeClassStatistics | Partial | Route validates input; computation returns 501 until the signature pipeline lands. |
| computeClass (Honua extension) | Implemented (validation only) | Validates `renderingRule` raster-function chains (`Identity`, `Stretch`, `Colormap`, `Clip`, `ExtractBand`, depth ≤ 8) and returns planned execution metadata. `exportImage` and `identify` execute the `Stretch`, `Colormap` (explicit stops or named `ColorrampName`), `Clip` (including `ClippingType=1` keep-outside), and `ExtractBand` portions of a chain. |
| addRasters, deleteRasters, updateRaster, uploads, downloadRasters, calculateVolume, computeMultidimensionalInfo, computeTiePoints, validate | Not implemented (admin-API equivalents) | Raster ingestion and mutation happen through the canonical Honua admin API instead of the Esri ImageServer admin surface (#1875 decision memo). `addRasters` → `POST /api/v1/admin/import/raster` (+ `POST /api/v1/admin/cloud-rasters` for COGs); **`deleteRasters` → `DELETE /api/v1/admin/import/raster/{rasterId}`** (cascades to statistics/tiles/sensor metadata); **`updateRaster` → `PATCH /api/v1/admin/import/raster/{rasterId}`** (name/description/acquisitionDate). The remaining ops (uploads/downloadRasters/validate/calculateVolume/computeMultidimensionalInfo/computeTiePoints) and a GeoServices-REST mutation shim remain deferred/by-design. See [ImageServer admin-op mapping](imageserver-admin-mapping.md). |

### Child resources

| Resource(s) | Status | Notes |
| --- | --- | --- |
| keyProperties, multidimensionalInfo, statistics, histograms, rasterFunctionInfos, rasterAttributeTable | Implemented | Spec-shaped documents from the shared raster store; non-applicable cases return spec-correct empty documents (e.g. `{"variables": []}`), not 404s. |
| legend | Partial | Default 5-class equal-interval ramp from band-1 statistics. A `renderingRule` with an explicit `Colormap` is honoured: the legend emits one swatch per colour stop so it matches the rendered image. Other renderer overrides are ignored. |
| WCS | Partial | WCS 2.0.1 KVP GetCapabilities/DescribeCoverage/GetCoverage over the primary raster (`image/tiff`/`png`/`jpeg`, `SUBSET`/`BBOX`, `OUTPUTCRS`). `RANGESUBSET` selects coverage bands (`band1`..`bandN`, comma list and `band1:bandN` intervals). The Scaling extension is supported in full: `SCALESIZE` (explicit per-axis size `x(w),y(h)`), `SCALEFACTOR` (uniform factor), `SCALEAXES` (per-axis factors `x(f),y(f)`), and `SCALEEXTENT` (per-axis grid intervals `x(low,high),y(low,high)`) — at most one operator per request, resolved against the subset-trimmed base grid. `SUBSET=phenomenonTime("instant")` / `("start","end")` applies temporal subsetting against the coverage acquisition time (non-intersecting windows return `InvalidSubsetting`). Multidimensional slicing beyond `phenomenonTime` (e.g. a vertical/elevation or other named axis) is parsed and validated against the coverage's registered dimension axes (#1872). When a layer has a registered multidimensional (Zarr) store, its declared additional axes are resolved here: a coordinate-valued slice on a declared axis is resolved to a concrete grid-index slice through the shared coordinate-axis indexer (an out-of-range coordinate returns `InvalidSubsetting`). A slice on an axis the coverage does not declare returns `InvalidAxisLabel`; a malformed value returns `InvalidSubsetting`. The classic `GetCoverage` export path (`IRasterStore.ExportImageAsync` over the primary 2D raster) cannot yet read Zarr-slice pixels, so a resolved-but-unservable slice returns `OperationNotSupported` directing clients to the OGC API - Coverages endpoint; wiring the Zarr export pipeline into classic WCS remains the open part of #1872. WCS 2.0 core is CITE-certified (82/82); the range-subsetting and scaling extension conformance classes are not advertised pending CITE validation. |
| slices | Implemented | Returns enumerable multidimensional slice definitions, or a spec-shaped empty `slices` array for non-multidimensional rasters. |
| colormap, raster item metadata XML/thumbnail/imageSupportData, KML image, rasterFile | Not implemented | Source-native metadata and support files require an explicit canonical storage contract; Honua does not synthesize or leak provider files when that source data is unavailable. Colormap, KML image, and rasterFile are service-level ImageServer resources rather than children of a raster item. |

Mosaic semantics: rasters are selected by footprint intersection. The
`mosaicRule.mosaicMethod` sets the pixel-selection ordering on overlap —
`esriMosaicByAttribute` over an acquisition/date field (newest- or oldest-first by
sort order), `esriMosaicByAttribute` over an allowlisted NON-date attribute (#1870 —
`sortField` resolves to a vetted physical raster-catalog column: `OBJECTID`→`id`,
`BandCount`/`num_bands`→`band_count`, `width`, `height`, `SRID`; the highest value
wins by default, lowest when `ascending` is set; non-allowlisted/sensor fields stay
unsupported), `esriMosaicNorthwest` (the upper-left-most raster wins),
`esriMosaicLockRaster` (only the pinned raster IDs participate, newest-acquisition
tiebreak), `esriMosaicSeamline` (each raster is clipped to its persisted
seamline/cutline before the union, so a contested pixel is resolved by the
per-raster seamline geometry; rasters without a seamline contribute their full
footprint, and the seam falls back to a newest-acquisition tiebreak), and
`esriMosaicNadir` (#1870 — the raster acquired closest to straight-down wins,
ranked by the off-nadir angle persisted in the per-raster sensor/orientation
metadata `raster_sensor_metadata.exterior_orientation` (`offNadirAngle`); rasters
with no recorded off-nadir angle rank last, with a newest-acquisition tiebreak among
equal/unknown angles); an `id ASC` tiebreaker is always appended for determinism. A
per-raster footprint and a default seamline (equal to the footprint) are persisted at
import; automatic seamline generation/editing is out of scope. The merge strategy
(pixel-resolution operation) is the request `mosaicRule.operation`, then the layer
default, then `newest` (`newest`/`oldest`/`average`/`max`/`min` via PostGIS
`ST_Union`). `esriMosaicByAttribute` over a non-allowlisted (e.g. raw
sensor/orientation) field and the remaining unmodeled methods (`esriMosaicCenter`,
`esriMosaicViewpoint`) return 501 when more than one raster is selected. Temporal
`time` filters use newest-batch semantics (see
[known limitations](clients.md#known-limitations)).

## Geometry Service

Esri spec: [Geometry Service](https://developers.arcgis.com/rest/services-reference/enterprise/geometry-service/).
All operations are served at the canonical Esri route
`/rest/services/Utilities/Geometry/GeometryServer/<operation>` (GET + POST) over
NetTopologySuite, a PROJ-backed projection service, and a geography-based geodesic
measurement path. The live surface was verified end-to-end through the ArcGIS API
for Python (`arcgis.geometry`).

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Root `GeometryServer` metadata | Implemented | ArcGIS-style descriptor (`currentVersion`, `maxBufferCount`, ...) so ArcGIS Pro / SDK discovery handshakes complete. |
| buffer, simplify, project, intersect, union, clip, difference, areasAndLengths, lengths, distance, relation, densify, convexHull, generalize, labelPoints, cut, trimExtend, offset, autoComplete, reshape, findTransformations, toGeoCoordinateString, fromGeoCoordinateString | Implemented | Full ArcGIS operation set: geodesic buffer/area/length/distance, datum transforms (e.g. 4326 → 4267), DE-9IM relations, MGRS/USNG round-trips, the `bufferSR` cascade. No operation-level gaps. |

Parameter-level caveats (the complete list):

- `f` supports `json`/`pjson` only; `f=html` is rejected.
- `clip` uses the envelope of the clip geometry, not its full shape.
- `findTransformations` returns an empty list — CRS transformation runs through the
  PROJ pipeline rather than a discrete Esri transformation catalog; `project` still
  applies the correct datum transform directly.
- `toGeoCoordinateString`/`fromGeoCoordinateString` support `MGRS` and `USNG`;
  `UTM`, `GARS`, `GEOREF`, `DD`, `DDM`, and `DMS` return a clear 400.

## NAServer

Esri spec: [Network Analyst Service](https://developers.arcgis.com/rest/services-reference/enterprise/network-analyst-service/).
Routes accept GET query parameters or POST form parameters under `/rest/services/{serviceId}/NAServer` and adapt to
the shared `IRoutingProvider` routing pipeline.

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Route/solve (GET, POST) | Implemented | Parses query-string or form `stops`, `inSR`/`outSR`, `returnRoutes`, `returnDirections`, `barriers`/`polylineBarriers`/`polygonBarriers`, and `travelMode` through one handler; delegates to the configured routing provider and returns Esri route/directions feature sets. |
| ServiceArea/solveServiceArea | Implemented | Parses `facilities`, `defaultBreaks`, `inSR`/`outSR`, `travelDirection`, `barriers`/`polylineBarriers`/`polygonBarriers`, and `travelMode`; honours provider-advertised FromFacility/ToFacility support and returns Esri `saPolygons`. |
| Barriers (point/line/polygon) | Implemented (pgRouting) | `barriers` (points), `polylineBarriers` (lines), and `polygonBarriers` (areas) are parsed from Esri FeatureSets and threaded into both Route and ServiceArea solves. The pgRouting provider honours them by excluding the graph edges each barrier restricts: a point barrier blocks the single nearest edge; line/polygon barriers block every intersecting edge. A provider that does not advertise a barrier kind (e.g. the straight-line mock) returns a GeoServices 400 rather than silently ignoring the barrier. Bounded by `Routing:MaxBarriers` (default 1000). |
| Multiple travel modes | Partial (validation + driving) | `travelMode` (bare token or Esri travel-mode object `name`) is parsed and validated against the provider's advertised modes; an unsupported mode returns a GeoServices 400, an absent mode uses the provider default. The pgRouting topology stores a single `cost`/`reverse_cost` weight pair, so only the **driving** mode is genuinely routable today — walking/cycling/truck require additional per-mode cost columns (deferred). The request surface, validation, and capability advertisement are wired so multi-mode topologies/providers slot in without an adapter change. |
| ClosestFacility/solveClosestFacility | Implemented | Parses `incidents`, `facilities`, `defaultTargetFacilityCount`, `travelDirection`, `defaultCutoff`/`cutoff`, barriers, and `travelMode`; ranks facilities per incident by network impedance over the pgRouting cost matrix (`pgr_dijkstraCost`) and materializes the route to each of the closest N, returning ranked Esri routes (`IncidentID`/`FacilityID`/`FacilityRank`/`Total_*`) plus optional directions. Bounded by `Routing:MaxIncidents`/`Routing:MaxClosestFacilities`. Real per-mode impedance stays deferred (single stored cost weight — same limitation as Route/ServiceArea). |
| ODCostMatrix/solveODCostMatrix | Implemented (cost-only) | Parses `origins`, `destinations`, `defaultCutoff`, `defaultTargetDestinationCount` (k-nearest), `outputType` (`esriNAODOutputNoLines`), barriers, and `travelMode`; computes an origins×destinations impedance matrix via `pgr_dijkstraCost` and returns attribute-only Esri `odLines` (`OriginID`/`DestinationID`/`DestinationRank`/`Total_Time`/`Total_Distance`). Bounded by `Routing:MaxOrigins`/`Routing:MaxDestinations`. True-shape/straight-line geometry output and asymmetric per-mode impedance are deferred. |
| LocationAllocation/solveLocationAllocation | Implemented (minimize-impedance + maximize-coverage) | Parses candidate `facilities`, weighted `demandPoints`, `problemType`, `numberFacilitiesToFind`, and `impedanceCutoff`; builds a candidate×demand cost matrix over `pgr_dijkstraCost` and chooses facilities with a greedy solver, returning chosen facilities and per-demand allocations. Other Esri problem types (maximize-attendance/market-share/target-market-share), non-linear impedance transformations, and any LP/heuristic solver return a GeoServices 400 ("unsupported problem type"). Bounded by `Routing:MaxLocationAllocationFacilities`/`Routing:MaxDemandPoints`. |
| Network dataset (registry + editing) | Partial (addressable registry + registry editing) | A first-class `honua.network_datasets` registry (migration 062) makes the routing network addressable: the pgRouting provider resolves edge/vertex table names per `Routing:NetworkDatasetId`, defaulting to a seeded `default` dataset mapping to the existing `public.ways` topology (zero behaviour change). The dataset registry is now **editable** through the admin API (`/api/v1/admin/network-datasets`, migration 066 adds description + audit columns): list/get/register/update/delete a dataset's source-table mapping, SRID, status, and metadata, gated by the admin authorization policy with safe schema-qualified-identifier validation on the edge/vertex table names. Edge/vertex feature editing, turn/restriction-attribute editing, and topology rebuild (the heavyweight, rarely-requested tail) remain deferred to a later phase. |
| Service metadata | Not implemented | Route, service-area, closest-facility, OD-cost-matrix, and location-allocation solves are the routing scope. Unsupported provider capabilities return GeoServices 400 error envelopes rather than fabricated solves. |

## Sources and upkeep

- Machine-readable parity export: [`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json) — update it in the same PR as any GeoServices route or behavior change.
- [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md), [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md), [authentication](../../guides/secure/authentication.md) — drill-downs for the services not detailed on this page.
- [Supported clients](clients.md) — which Esri clients are certified against this surface.
- Release owners verify this page during the [release checklist](../../internal/contributor/RELEASE_CHECKLIST.md).
