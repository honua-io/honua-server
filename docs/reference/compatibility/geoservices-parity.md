# GeoServices REST parity

Honua serves the Esri GeoServices REST API at compatible paths so ArcGIS Pro, the
ArcGIS SDKs, Esri Leaflet, and the ArcGIS API for Python can connect without
modification. This page summarizes endpoint-level parity per service; the
machine-readable source is
[`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json).

**How this matrix is produced (#2863).** It has two honestly-different halves, and only
one of them is written by hand:

- **Which operations exist is derived, never authored.** The route roster comes from the
  server's own endpoint registry via the generated
  [`feature-catalog.json`](../../gis/data/feature-catalog.json), so every `esriPaths`,
  `honuaEndpoints`, and `capabilityMaturity` in the JSON is generated. Nothing can be
  published here that Honua does not serve, and nothing Honua serves can be omitted.
- **How completely each is implemented is human judgement.** The
  Implemented/Partial/Stub verdicts and the gap prose live in
  [`geoservices-parity-judgment.json`](../../gis/data/geoservices-parity-judgment.json)
  and are never inferred from the code.

`GeoServicesParityMatrixDriftTests` gates the join **in both directions** on every CI
run: a served operation with no judgement fails, and a judgement naming an unserved
operation fails. See [Sources and upkeep](#sources-and-upkeep) for what that does and
does not prove.

Status vocabulary:

- **Implemented** — the Esri operation exists at a compatible path and the documented behavior is supported.
- **Partial** — the operation exists, but only a subset of documented parameters or behavior is supported.
- **Stub** — the route exists and returns the spec-shaped response, but the backing data model is deferred; read-style stubs return empty/`false` results and mutation stubs return HTTP 400 rather than fabricating success.
- **Not implemented** — the operation is not exposed.

## Service summary

| Service | Parity | Implemented surface | Headline gaps |
| --- | --- | --- | --- |
| [FeatureServer](#featureserver) | Partial | Query (7 output formats), edits, attachments, related records, domains, replication with incremental change tracking, contingent values (service operation + layer `contingentValuesDefinition`), subtype-derived layer `types`/editing templates, 3D (Z/M) queries + `hasZ`/`hasM` layer advertisement, true-curve input densification (circular-arc/Bézier `curvePaths`/`curveRings` → linear), estimates, calculate, validateSQL, append, bins/date bins/top features, generateRenderer, spatial analytics extensions (Pro) | Standalone shared-template store and utility-network/asset data models (deferred — no canonical backing), lossless true-curve storage/re-emission (input densifies, output stays linear), automated contingent-value import |
| [MapServer](#mapserver--wms--wmts) | Partial | Export, identify, find, legend/queryLegends, query, mapLayer + allowlisted workspace (`dataLayer`) dynamicLayers, allowlisted `joinTable` dynamicLayers joins (left-outer/inner, application-side, surfaced through identify + dynamicLayer metadata), tiles with cloud-storage cache, storage-backed exportTiles (synchronous ZIP + Esri exploded-cache TPK; asynchronous Esri Compact Cache V2 / TPKX with a durable job lifecycle), generateKml, WMS 1.3/1.1.1, WMTS 1.0 (WebMercatorQuad + WorldCRS84Quad gridsets) | Esri compact/generated tile-cache *management* (seeding, expiry, quota) for the live tile cache, dynamicLayers `queryTable` (raw SQL) data sources, image/KML-image child resources |
| [ImageServer](#imageserver) | Partial | Service metadata, exportImage, identify (including registered-Zarr point-slice reads), tile with cloud-storage cache, catalog query/item and item image/info resources, find (orientation-ranked when sensor metadata present), measure (Basic + DEM-backed height), statistics/histograms, getSamples (including registered-Zarr slice sampling), queryBoundary, computePixelLocation, project (incl. RPC image-CS warp), storage-backed exportTiles (synchronous ZIP + Esri exploded-cache TPK; asynchronous Esri Compact Cache V2 / TPKX with a durable job lifecycle), dynamic computeCacheInfo, legend, colormap resource (renderer-driven), Colormap renderingRule (explicit stops, named ColorrampName, or inline algorithmic/multipart Colorramp), raster item thumbnail, DEM-backed calculateVolume, control-point computeTiePoints, computeClassStatistics, admin raster CRUD (delete/update via admin API), WMTS 1.0 GetCapabilities/GetTile/GetFeatureInfo, WCS 2.0.1 KVP including native-CRS PNG reads of registered Zarr slices | Source-native raster item metadata XML and KML image, shadow/photogrammetric height analytics, Esri compact/generated tile-cache *management*, multi-factor camera-model find scoring, transformed registered-Zarr slice output (TIFF/JPEG, reprojection, advanced interpolation, and multi-coordinate additional-axis trims), non-allowlisted (raw sensor/orientation) mosaic inputs, automatic seamline generation/editing; ImageServer WMTS is WebMercatorQuad only |
| [Geometry Service](#geometry-service) | Complete | Root metadata plus all 23 ArcGIS geometry operations | None at operation level; parameter-level caveats only |
| GeocodeServer | Partial | Service metadata, findAddressCandidates, reverseGeocode (incl. provider-dependent `distance`/`featureTypes`), suggest, geocodeAddresses, `outFields` projection, `outSR` reprojection, `magicKey` suggest→candidate round-trip (self-issued signed token, all providers), `category` filtering (all providers, on provider-supplied address type) | `forStorage`/`matchOutOfRange` (re-deferred; no backing provider models them) — see [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md) |
| GPServer | Partial | PrintingTools; generic adapter with catalog-backed task metadata, async submitJob, synchronous `execute` for the deterministic single-geometry `geometry.*`/`conversion.geometry-format` family (inline over the canonical job runtime), job status/cancel/results over 96 seeded processes | Heavyweight/layer-scoped tasks stay async-only (their `execute` returns a 400 pointing at submitJob); `env:*` rejected on submitJob (sync `execute` honors `env:outSR`) — see [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md) |
| [NAServer](#naserver) | Partial | GET/POST route, service-area, closest-facility, OD-cost-matrix (cost-only), and location-allocation (minimize-impedance / maximize-coverage) solves over the shared routing provider, incl. point/line/polygon barriers and dataset-backed `travelMode`; addressable network-dataset registry with admin editing of the dataset mapping/metadata | OD-matrix geometry output, additional location-allocation problem types, and edge/vertex/turn-restriction feature editing with topology rebuild are deferred; the built-in topology remains driving-only unless operators map additional verified cost columns |
| [VectorTileServer](#vectortileserver) | Partial | Service metadata with a real LOD table, MVT tiles over the canonical tile pipeline, tilemap availability document, Mapbox GL style resource composed from the layer's stored style | Tiles carry only the service's primary tiled publication rather than every layer packed per tile; tilemap availability is grid arithmetic and never consults the tile store; no sprite or glyph store (those resources are content-free placeholders); service metadata hardcodes `capabilities`/`type`/`exportTilesAllowed` and always reports `minLOD` 0 |
| [VersionManagementServer](#versionmanagementserver-branch-versioning) | Partial (**experimental — off by default**) | Version list/info with an access policy, create/alter/delete, reconcile/post with conflict policy and async jobs, inspectConflicts three-way diffs, resolveConflicts, async job polling | No Esri edit/read session model (start/stop Reading/Editing are stateless acknowledgements with no session token); `create` ignores `parentVersion`; service metadata hardcodes its capabilities; Postgres-only — other providers return 501 on mutations. **Every route 404s unless the `versioning.branch` experimental capability is enabled; not part of the first-release surface.** |
| [Catalog / REST Info](#catalog--rest-info) | Partial | `/rest/services` graph-backed, RBAC-filtered service directory | `/rest/info` always reports `isTokenBasedSecurity: false` and omits `tokenServicesUrl`; no `folders`; `currentVersion` deliberately not advertised |
| [Portal Sharing](#portal-sharing) | Partial | `generateToken` opaque tokens consumable on `/rest/services/*`; `/sharing/rest/info` auth discovery; OAuth2 **named-user** flow at `/sharing/rest/oauth2/{authorize,callback,token}` — `authorization_code` + PKCE and rotating `refresh_token` (OIDC-delegated) — the opt-in `client_credentials` grant with optional IdP/OIDC federation (#1889), optional JWT access tokens + RFC 7662 `oauth2/introspect` (#1890, opt-in), RFC 7009 `oauth2/revoke`, and read/search/community projections over the Metadata v2 graph | Portal/community `self` and item documents are partly hardcoded (portal id/name, `role`, item `owner`); `search` honours a small `q` grammar and ignores `bbox`/`filter`/`categories`; the group + item-sharing overlay is **in-memory, single-node, and never read back** — sharing an item or joining a group changes nothing any client can observe; `/content/items/{id}/data` returns the item document, not the data payload — see [authentication](../../guides/secure/authentication.md) |

## FeatureServer

Esri spec: [Feature Service](https://developers.arcgis.com/rest/services-reference/enterprise/feature-service/).

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata; layer metadata | Implemented | Dynamic capabilities string; `editFieldsInfo`, `editingInfo`, `timeInfo`, `allowGeometryUpdates`, `supportsStatistics`, normalized `supportedQueryFormats` incl. binary formats. Subtype-bearing layers surface Esri-style editing `types` (one per subtype, each carrying per-field `domains` and an editing template whose `prototype` seeds the subtype code + field default values) and a layer-level `contingentValuesDefinition`, both projected from the canonical Metadata v2 graph and omitted byte-stably when unauthored. The layer-root `templates` array stays empty (in the Esri model, subtype-driven templates live inside `types[]`; no standalone template authoring exists on the canonical graph). |
| Query (service + layer), queryDomains, relationships, getEstimates (service + layer) | Implemented | Service-level query delegates to a target layer via `layerId`/`layers`. |
| applyEdits (service + layer), addFeatures, updateFeatures, deleteFeatures, append (service + layer), calculate, validateSQL | Implemented | Multi-layer batch edits; `rollbackOnFailure` defaults `false` for applyEdits, `true` for standalone endpoints; deleteFeatures supports `objectIds`, `where`, and spatial filters. |
| queryRelatedRecords, queryAttachments | Implemented | Full filter facets (`attachmentTypes`, `keywords`, `size`, `definitionExpression`); `globalIds` rejected with 400 (integer object IDs only). |
| addAttachment, updateAttachment, deleteAttachments, attachment infos, attachment download | Implemented | All attachment operations are **per-feature**: the route carries `{featureId}` — `POST .../{layerId}/{featureId}/addAttachment`, `.../updateAttachment`, `.../deleteAttachments`. Multipart form-data upload; `GET .../{layerId}/{featureId}/attachments` lists `attachmentInfos`; binary download at `.../{layerId}/{featureId}/attachments/{attachmentId}`. |
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
| Output formats `f=json/pjson/geojson/pbf/fgb/geobuf/parquet/arrow` | Implemented | GeoJSON and GeoArrow require EPSG:4326 when geometry is present; GeoParquet honours `outSR` for any CRS with a resolvable PROJJSON definition, emitting the authoritative PROJJSON `crs` in the `geo` metadata (EPSG:4326 uses the OGC:CRS84 default; an unresolvable `outSR` errors); `parquet`/`arrow` always strip M values; `fgb`/`geobuf` need native store support and ignore precision/simplification parameters; special query modes always return JSON. |
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
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad PNG tiles to configured cloud file storage (`local`, S3, Azure Blob). **Synchronous:** `storageFormat=zip` (default) writes a flat `{z}/{x}/{y}.png` ZIP; `storageFormat=tpk` (or `tilePackage=true`) writes an Esri exploded-cache **TPK** package (`v101/<cache>/_alllayers/Lzz/Rrrrrrrrr/Cccccccc.png` + `conf.xml`/`conf.cdi`) readable by ArcGIS Pro / Runtime SDKs / QGIS. **Asynchronous (#2688):** an explicit Compact Cache V2 negotiation — `storageFormatType=esriMapCacheStorageModeCompactV2`, or `storageFormat=tpkx`/`compact`/`compactv2` — submits a durable job and returns the ArcGIS `{ jobId, jobStatus }` envelope; the artifact is a real Esri **TPKX 1.0** archive of Compact Cache V2 `.bundle` files. Track it through the job child resources below. The durable path requires the tile-export job runtime (an execution job store); where that is not configured the request is rejected rather than silently downgraded. Partial because the export stays bounded to WebMercatorQuad and `gdbVersion`-style parameters are accepted and ignored. |
| exportTiles job child resources (`jobs/{jobId}`, `.../cancel`, `.../results/out_service_url`) | Implemented | Durable asynchronous exportTiles lifecycle (#2688): job status, cancel, and the `out_service_url` result resource, over the shared tile-export job service. |
| WMS | Implemented | WMS 1.3.0 and 1.1.1 GetCapabilities/GetMap/GetFeatureInfo (KVP) at `.../MapServer/WMS` and `/ogc/services/{serviceId}/wms`. Time-aware layers advertise a `time` dimension. WMS 1.3 is CITE-certified (199/199); 1.1.1 has no CITE evidence yet. |
| WMTS | Partial | GetCapabilities/GetTile/GetFeatureInfo (KVP + RESTful) at `.../MapServer/WMTS` and `/ogc/services/{serviceId}/wmts`. GetTile and GetFeatureInfo both resolve the requested tile matrix set through the shared `ITileMatrixSetRegistry`, so the built-in WebMercatorQuad and WorldCRS84Quad (CRS84/EPSG:4326) gridsets and operator-defined custom gridsets (#1791) are served end-to-end; GetFeatureInfo computes the clicked pixel from the gridset's own origin, cell size and matrix dimensions (#1873) rather than Web Mercator constants, and unsupported gridsets are rejected with `InvalidParameterValue`. WebMercatorQuad behaviour is byte-identical to the prior implementation. WMTS 1.0 is CITE-certified (60/60). |
| queryAnalytic, image/KML-image child resources, `exts/*` | Not implemented | |

## ImageServer

Esri spec: [Image Service](https://developers.arcgis.com/rest/services-reference/enterprise/image-service/).
Routes are layer-scoped: `{id}` in `GET /rest/services/{id}/ImageServer` is the
raster layer identifier.

### Operations

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Implemented | Aggregate mosaic extent/statistics, `timeInfo` when acquisition dates exist, output-cached. Dynamic (`singleFusedMapCache: false`) by default; opt in to a WebMercatorQuad `tileInfo` for tiled Esri clients via `GeoServices:ImageServer:TileMetadata:Enabled` (#1648). `objectIdField`/`fields`/`rasterFunctionInfos` root properties not yet populated. |
| tile | Implemented | PNG/JPEG/TIFF tiles, zoom 0–28, multi-raster mosaic. Dynamically generated tiles auto-apply a MinMax display stretch when the source raster is not 8-bit (elevation/analytic/float), so analytic layers are viewable; at low zoom levels the tile read path prefers a persisted reduced-resolution overview pyramid and otherwise coarsens the source on-the-fly toward the tile's ground resolution before resampling; pre-rendered cached tiles are served as stored. Tile GETs use configured cloud file storage (`local`, S3, Azure Blob) as a deterministic loose-object read-through/write-through cache. |
| computeHistograms, getSamples | Partial | `computeHistograms` returns per-band histograms **without** the parallel statistics array, over the same `rasterIds`/`bandIds`/`mosaicRule`/`time`/`histogramParameters.size` selection path as `computeStatisticsHistograms`; an AOI geometry clips the analysis (envelope-scoped) and `renderingRule` execution remains deferred. `getSamples` samples pixel values at point, multipoint, polyline/polygon vertex, or envelope-centre locations through the shared raster identify pipeline and caps `sampleCount` at 1000. For layers mapped to a registered Zarr coverage, `multidimensionalDefinition` resolves time, vertical/elevation, or other declared coordinate values through the shared axis indexer and reads the pinned slice through the Zarr subset pipeline (#1869/#1939). Layers without a readable Zarr backing store return an explicit 501 rather than silently sampling a dimension-collapsed raster. |
| conf.json | Partial | Interop route, **not** Esri's tile-cache configuration document: it returns the ImageServer service descriptor. Honua's ImageServer is dynamic, so no fused cache is advertised by default. It exists because the ArcGIS Maps SDK for .NET native runtime probes `conf.json` while loading an `ImageServiceRaster` and treats a 404 as fatal (#1456, #1648). |
| WMTS | Partial | OGC WMTS 1.0 GetCapabilities/GetTile (KVP + RESTful) at `.../ImageServer/WMTS` for numeric and service-name ImageServer routes. Uses the ImageServer tile/cloud-cache pipeline, advertises `WebMercatorQuad`, serves `image/png`, `image/jpeg`, and `image/tiff` tiles, answers GetFeatureInfo (application/json or text/xml) with the pixel band values at the requested tile pixel, and advertises a `TIME` dimension for temporal layers (GetTile/GetFeatureInfo honour a `TIME` parameter). Non-WebMercator matrix sets are deferred. |
| exportImage | Partial | JSON envelope with temporary `href` by default; `f=image` streams bytes. For a registered Zarr coverage, `multidimensionalDefinition` performs one bounded native-CRS 2D slice read and emits a grayscale PNG through the shared managed Zarr planner/renderer. This first slice requires `format=png`, `interpolation=RSP_NearestNeighbor`, one coordinate per selected dimension, and bbox/image SR equal to the coverage CRS; it rejects transforms, partial-outside bboxes, `renderingRule`, `bandIds`, `noData`, `time`, and `mosaicRule` explicitly. JPEG/TIFF, reprojection, advanced interpolation, and raster-function chains are tracked by #2717. For ordinary rasters, `bandIds`, `noData`, mosaic + single-instant `time` are supported and `pixelType` is validated but not applied. `renderingRule` executes Stretch, Colormap, Clip, ExtractBand, fixed BandArithmetic, and terrain functions on single and mosaic paths as described below. Other stretch types, histogram-equalize / unrecognized-colorramp-name / extract-band-by-name variants, and unrecognized band-arithmetic methods return 501; unknown functions return 400. `bmp`/`gif` are rejected with 400. |
| identify | Partial | Point/envelope/polygon (area geometries identify at the envelope centroid); `returnCatalogItems`; `pixelSize` echoed but pyramid selection deferred. `multidimensionalDefinition` performs a bounded point read from a registered Zarr slice through the same canonical reader as getSamples; unknown/out-of-range selections return 400 and unavailable registrations/readers return 501. Slice reads currently require coordinates in the store CRS and reject `renderingRule`, `time`, and `mosaicRule` combinations rather than silently serving the collapsed raster. For ordinary rasters, `renderingRule` is applied: the returned value reflects the **rendered** pixel (post clip/band-arithmetic/terrain/stretch/colormap, matching exportImage) instead of the raw source value. Omitting `renderingRule` preserves the raw-value contract. Unsupported chains surface 400/501 exactly as on exportImage. |
| query (raster catalog) | Partial | Esri-compatible catalog features with `where`, spatial filter, `outSR`, `orderByFields`, `outFields`, paging, and shaping flags. Filters run in-memory at footprint-envelope granularity. |
| raster catalog item (`{rasterId}`) | Implemented | Returns one catalog item as an Esri feature with the canonical catalog attributes and footprint geometry. Supports numeric-layer and service-name routes and applies the same access policy and field masking as catalog query. |
| raster catalog item children (`{rasterId}/image`, `{rasterId}/info`, `{rasterId}/info/keyProperties`, `{rasterId}/info/histograms`) | Implemented | Item image delivery pins the requested raster through the canonical export pipeline. Item metadata comes from the canonical raster store, uses the same layer access policy, validates JSON response formats, and returns an Esri 404 document for unknown raster IDs. |
| find | Partial | Finds raster catalog images whose footprints contain `toGeometry`, with `objectIds`, `where`, `inSR`, `fromGeometry` validation, and `maxCount` support. Results are **orientation-ranked** when candidates carry exterior-orientation metadata (`raster_sensor_metadata.exterior_orientation`): images are ordered by smallest off-nadir angle, then footprint distance, then ObjectId. When no candidate has orientation metadata (the common case for plain COGs), it falls back to pure footprint-distance ranking. Multi-factor camera-model scoring and per-request tunable weighting are deferred. |
| measure | Partial | Supports Basic map-space point, distance/azimuth, area/perimeter, and centroid measurements over point, envelope, and polygon Esri JSON. **DEM-backed height** (`esriMensurationHeightFromBaseAndTop`) is supported for rasters whose `raster_sensor_metadata.dem_source` resolves to a DEM layer: it differences the sampled ground elevation at the base and top points and emits the real `sensorName`. Shadow-based height (`*Shadow`), photogrammetric height, and the other 3D mensuration operations still return 501; height ops also return 501 when the raster has no DEM/sensor metadata or the DEM does not cover the supplied points (never a faked value). |
| computeStatisticsHistograms | Implemented | Per-band stats + histograms with `rasterIds`, `bandIds` (Honua extension), `mosaicRule`, `time`, `histogramParameters.size`. An AOI `geometry` clips the analysis to that area (envelope-scoped) for both single rasters and mosaics; without a geometry the full selected raster/mosaic is analysed. `renderingRule` is now applied (#1871): the `Stretch`/`Colormap`/`Clip` chain exportImage/identify execute is run against the pixels before per-band stats/histograms are computed, so the reported values describe the rendered output (the stretch is bounded from the source raster's persisted statistics, matching exportImage). Omitting `renderingRule` preserves the raw-source statistics contract. `ExtractBand`/`BandArithmetic` (which change the band set) and the same unsupported chains as exportImage return 501; unknown functions return 400. |
| queryBoundary | Implemented | Returns Esri `shape` + approximate `area` from the aggregate raster footprint extent, with `outSR` honoured when the shared CRS pipeline can transform the extent. NoData-trimmed boundaries are deferred. |
| computePixelLocation | Implemented | Converts point geometries to raster pixel column/row coordinates using the raster geotransform, with extent fallback and `rasterId`/input SR support. |
| project | Partial | Reprojects Esri JSON geometry arrays between supported spatial references via the shared CRS pipeline and returns the Image Service `{ "geometries": [...] }` response shape. Envelope geometry remains envelope-shaped. `datumTransformation` is honored: a client-supplied geotransformation WKID (or composite `geoTransforms`) is resolved through the shared Esri WKID → PROJ pipeline catalog and applied via 3-argument `ST_Transform`, matching the Geometry Service; an unknown/inapplicable WKID returns 400. The image-coordinate-system `transformation` parameter now warps **point** geometries between image (pixel sample/line) space and map space using the raster's RPC sensor model (`raster_sensor_metadata.rpc`): geometries are treated as image coordinates, mapped to ground (EPSG:4326) via the RPC offset/scale normalisation, then reprojected into `outSR`. It returns 400 when the layer's raster carries no RPC metadata, and supports points only for the first increment (non-RPC sensor models and polygon/multi-step chaining are deferred). |
| estimateExportTilesSize, exportTiles | Partial | Estimates and exports bounded WebMercatorQuad image tiles to configured cloud file storage (`local`, S3, Azure Blob). **Synchronous:** `storageFormat=zip` (default) writes a flat `{z}/{x}/{y}.ext` ZIP; `storageFormat=tpk` (or `tilePackage=true`) writes an Esri exploded-cache **TPK** package (`v101/<cache>/_alllayers/...` + `conf.xml`/`conf.cdi`). **Asynchronous (#2688):** an explicit Compact Cache V2 negotiation (`storageFormatType=esriMapCacheStorageModeCompactV2`, or `storageFormat=tpkx`/`compact`/`compactv2`) submits a durable job returning the ArcGIS `{ jobId, jobStatus }` envelope and produces a real Esri **TPKX 1.0** archive of Compact Cache V2 `.bundle` files. The durable path requires the tile-export job runtime (an execution job store). |
| exportTiles job child resources (`jobs/{jobId}`, `.../cancel`, `.../results/out_service_url`) | Implemented | Durable asynchronous exportTiles lifecycle (#2688) on both the numeric-layer and service-name routes. |
| computeCacheInfo | Partial | Returns a spec-shaped `cacheInfo` extent for dynamic ImageServer layers and intentionally omits `tileInfo`/`cacheType` until a formal Esri-style raster tile cache is configured. The loose-object tile GET cache is operational storage, not advertised cache metadata. |
| computeClassStatistics | Partial | Computes per-class statistical signatures over the `classDescriptions` training AOIs: per-class pixel count, per-band mean vector, per-band min/max/stddev, and the band-by-band **sample** covariance matrix (divided by n-1). Reads AOI pixels through the shared raster analytics path (the same clip+merge primitive `computeStatisticsHistograms` uses); a single raster or a layer mosaic (`rasterIds` + `mosaicRule`) and a `bandIds` subset are supported. Per-class analysis is memory-bounded by a configurable pixel budget (`GeoServices:ImageServer:ClassStatistics:MaxPixelsPerClass`) — an AOI whose bounding box exceeds it is rejected rather than truncated. `renderingRule` and `multidimensionalDefinition` are explicitly rejected (501): signatures are computed on source pixels. Tile-streaming an AOI larger than the budget is deferred follow-up. |
| raster-function chain validation | Not implemented (no route) | **There is no `computeClass` route** — Esri has no such operation, and Honua's chain analyzer is an internal helper, deliberately not exposed (`ImageServerAnalyzeHandler`). Chain validation is reached *through* the operations that execute a chain: a `renderingRule` is validated and planned as part of `exportImage`/`identify`/`computeStatisticsHistograms` (`Identity`, `Stretch`, `Colormap`, `Clip`, `ExtractBand`, depth ≤ 8), where `exportImage` and `identify` execute the `Stretch`, `Colormap` (explicit stops, named `ColorrampName`, or an inline algorithmic/multipart `Colorramp` object), `Clip` (including `ClippingType=1` keep-outside), and `ExtractBand` portions. Unknown functions return 400; recognized-but-unsupported chains return 501. |
| calculateVolume | Partial | Computes cut/fill volumes and the 2D surface area of each area-of-interest polygon against the layer's associated DEM (`raster_sensor_metadata.dem_source`), integrating `Σ (elevation − basePlane) · pixelArea` over the DEM pixels inside the AOI through the same shared clip primitive `computeClassStatistics` uses. Only a **constant base plane** is supported; the AOI is bounded by a per-operation pixel budget (over-budget returns 400); a raster with no modeled DEM returns 501 rather than a fabricated volume (ADR-0065). |
| computeTiePoints | Partial | Returns the raster's **pre-registered control points / GCPs** from its sensor metadata, passed through verbatim and bounded to 10,000 points per response. Automatic feature detection / descriptor matching (how ArcGIS *derives* tie points) needs a computer-vision dependency this repository bars, so it is out of scope by design: a raster with no modeled control points returns 501 rather than a fabricated result (ADR-0065). |
| addRasters, deleteRasters, updateRaster, uploads, downloadRasters, computeMultidimensionalInfo, validate | Not implemented (admin-API equivalents) | Raster ingestion and mutation happen through the canonical Honua admin API instead of the Esri ImageServer admin surface (#1875 decision memo). `addRasters` → `POST /api/v1/admin/import/raster` (+ `POST /api/v1/admin/cloud-rasters` for COGs); **`deleteRasters` → `DELETE /api/v1/admin/import/raster/{rasterId}`** (cascades to statistics/tiles/sensor metadata); **`updateRaster` → `PATCH /api/v1/admin/import/raster/{rasterId}`** (name/description/acquisitionDate). The remaining ops (uploads/downloadRasters/validate/computeMultidimensionalInfo) and a GeoServices-REST mutation shim remain deferred/by-design. See [ImageServer admin-op mapping](imageserver-admin-mapping.md). |

### Child resources

| Resource(s) | Status | Notes |
| --- | --- | --- |
| keyProperties, multidimensionalInfo, statistics, histograms, rasterFunctionInfos, rasterAttributeTable | Implemented | Spec-shaped documents from the shared raster store; non-applicable cases return spec-correct empty documents (e.g. `{"variables": []}`), not 404s. |
| legend | Partial | Default 5-class equal-interval ramp from band-1 statistics. A `renderingRule` with a `Colormap` (explicit stops, named `ColorrampName`, or an inline algorithmic/multipart `Colorramp` object) is honoured: the legend emits one swatch per colour stop so it matches the rendered image. Other renderer overrides are ignored. |
| colormap | Implemented (renderer-driven) | Honua rasters are continuous with no intrinsic source colormap, so the read-only `colormap` resource reflects the **active renderer**: a `renderingRule` whose `Colormap` function resolves a colormap (explicit stops, named `ColorrampName`, or an inline algorithmic/multipart `Colorramp`) returns the Esri `{ "colormap": [[value, r, g, b], ...] }` document (consistent with what `exportImage`/`legend` render); a malformed/unsupported `renderingRule` surfaces its reason, and any other request returns the not-available response. |
| WCS | Partial | WCS 2.0.1 KVP GetCapabilities/DescribeCoverage/GetCoverage over the primary raster (`image/tiff`/`png`/`jpeg`, `SUBSET`/`BBOX`, `OUTPUTCRS`). `RANGESUBSET` selects coverage bands (`band1`..`bandN`, comma list and `band1:bandN` intervals). The Scaling extension is supported in full: `SCALESIZE` (explicit per-axis size `x(w),y(h)`), `SCALEFACTOR` (uniform factor), `SCALEAXES` (per-axis factors `x(f),y(f)`), and `SCALEEXTENT` (per-axis grid intervals `x(low,high),y(low,high)`) — at most one operator per request, resolved against the subset-trimmed base grid. `SUBSET=phenomenonTime("instant")` / `("start","end")` applies temporal subsetting against the coverage acquisition time (non-intersecting windows return `InvalidSubsetting`). For a readable registered Zarr coverage, a single coordinate-valued additional-axis `SUBSET` is translated into the canonical bounded slice reader and returned as native-CRS, nearest-neighbor grayscale PNG. Out-of-range coordinates return `InvalidSubsetting`; undeclared axes return `InvalidAxisLabel`; unavailable registrations/readers and unsupported formats or transforms return `OperationNotSupported`. Multi-coordinate additional-axis trims remain unsupported. WCS 2.0 core is CITE-certified (82/82); no new extension conformance class is advertised by this slice path. |
| slices | Implemented | Returns enumerable multidimensional slice definitions, or a spec-shaped empty `slices` array for non-multidimensional rasters. |
| raster item thumbnail (`{rasterId}/thumbnail`) | Implemented | Renders a small thumbnail of the raster item through the shared export pipeline; honours `f=json` for an `href` envelope. Note the served path is `{rasterId}/thumbnail`, not `{rasterId}/info/thumbnail`. |
| raster item imageSupportData (`{rasterId}/imageSupportData`) | Partial | Returns the Esri-shaped `imageSupportData` — sensor name, camera model, interior/exterior/RPC orientation presence flags, and DEM source — projected from the raster sensor-metadata companion. Only rasters with a modeled sensor-metadata row carry support data; a plain COG reports not-available rather than a fabricated empty support-data document. |
| raster item source file (`{rasterId}/rasterFile`) | Stub | The route exists and resolves the raster item, then returns a precise capability-honest not-available response: Honua stores raster pixels in the provider (PostGIS raster / analytic stores) rather than referencing a downloadable source file, so there is no file to stream. Never a fabricated payload. |
| raster item metadata XML (`{rasterId}/info/metadata`), KML image | Not implemented | Source-native metadata and support files require an explicit canonical storage contract; Honua does not synthesize or leak provider files when that source data is unavailable. KML image is a service-level ImageServer resource rather than a child of a raster item. |

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
| ClosestFacility/solveClosestFacility | Implemented | Parses `incidents`, `facilities`, `defaultTargetFacilityCount`, `travelDirection`, `defaultCutoff`/`cutoff`, barriers, and `travelMode`; ranks facilities per incident by the selected dataset-backed profile impedance over `pgr_dijkstraCost` and materializes the closest routes, returning ranked Esri routes (`IncidentID`/`FacilityID`/`FacilityRank`/`Total_*`) plus optional directions. Bounded by `Routing:MaxIncidents`/`Routing:MaxClosestFacilities`. |
| ODCostMatrix/solveODCostMatrix | Partial (cost-only + straight lines) | Parses `origins`, `destinations`, `inSR`/`outSR`, `defaultCutoff`, `defaultTargetDestinationCount` (k-nearest), `outputType`, barriers, and `travelMode`; computes an origins×destinations impedance matrix via `pgr_dijkstraCost`. `esriNAODOutputNoLines` preserves the attribute-only fast path. `esriNAODOutputStraightLines` adds a two-vertex Esri polyline from each original origin to destination in the requested `outSR`; the pgRouting provider batch-transforms all returned cells through PostGIS in one query, while providers that do not advertise straight-line support receive a precise 400. `esriNAODOutputTrueShape` and `esriNAODOutputTrueShapeWithMeasure` return a precise GeoServices 400 until bounded provider path geometry is implemented. Bounded by `Routing:MaxOrigins`/`Routing:MaxDestinations`. Asymmetric per-mode impedance remains deferred. |
| LocationAllocation/solveLocationAllocation | Partial (minimize-impedance + maximize-coverage + minimize-facilities) | Parses candidate `facilities`, weighted `demandPoints`, `problemType`, `numberFacilitiesToFind`, and `impedanceCutoff`; builds a candidate×demand cost matrix over `pgr_dijkstraCost` and returns chosen facilities plus per-demand allocations. `esriMFPMinimizeFacilities` requires a cutoff and uses deterministic greedy set cover: O(F²D) time, O(D) memory, and the standard H(D)-approximation bound on facility count (exact for a single candidate/pick), with cancellation checks and configured facility/demand caps. The remaining Esri inventory is intentionally rejected with a precise GeoServices 400 because the canonical request lacks required semantics: maximize-attendance needs impedance transformation model/factor, maximize-capacitated-coverage needs facility capacities, and maximize/target-market-share need competitor facilities and attractiveness weights. This inventory follows the [Esri Location Allocation REST contract](https://developers.arcgis.com/rest/routing/location-allocation-service/). |
| Service metadata | Not implemented | Route, service-area, closest-facility, OD-cost-matrix, and location-allocation solves are the routing scope. Unsupported provider capabilities return GeoServices 400 error envelopes rather than fabricated solves. |

### Solve parameters

These are parameter facets of the solve operations above, not operations of their own —
listing them as operations is how the machine-readable matrix ended up giving the same
route two contradictory statuses (fixed in #2863).

| Parameter | Status | Notes |
| --- | --- | --- |
| `barriers`, `polylineBarriers`, `polygonBarriers` | Implemented (pgRouting) | Point, line, and polygon barriers are parsed from Esri FeatureSets and threaded into Route and ServiceArea solves. The pgRouting provider honours them by excluding the graph edges each barrier restricts: a point barrier blocks the single nearest edge; line/polygon barriers block every intersecting edge. A provider that does not advertise a barrier kind (e.g. the straight-line mock) returns a GeoServices 400 rather than silently ignoring the barrier. Bounded by `Routing:MaxBarriers` (default 1000). |
| `travelMode` | Partial (dataset-backed) | `travelMode` (bare token or Esri travel-mode object `name`) is parsed and validated against the selected dataset's asynchronously resolved profile capabilities; unsupported modes return a GeoServices 400 and an absent mode uses `driving`. Migration 083 adds `honua.network_datasets.travel_profiles`, mapping each lowercase profile to validated forward/reverse edge impedance columns. A profile is advertised only when both columns exist. The pgRouting provider aliases the selected columns into the canonical `cost`/`reverse_cost` graph for Route, ServiceArea (including correctly swapped reverse traversal), ClosestFacility, OD matrix, and LocationAllocation. The seeded default remains driving-only, so existing deployments behave identically. |

### Adjacent surface: the network-dataset registry

Not a GeoServices operation — it is the Honua Admin API that backs the routing network,
recorded here because the NAServer gaps above depend on it.

| Surface | Status | Notes |
| --- | --- | --- |
| Network dataset (registry + editing + rebuild + promotion) | Partial (addressable registry + registry editing + transactional content edits + durable rebuild + atomic promotion/rollback + multi-node fencing) | A first-class `honua.network_datasets` registry (migration 062) makes the routing network addressable: the pgRouting provider resolves edge/vertex table names per `Routing:NetworkDatasetId`, defaulting to a seeded `default` dataset mapping to the existing `public.ways` topology (zero behaviour change). The dataset registry is **editable** through the admin API (`/api/v1/admin/network-datasets`, migration 066 adds description + audit columns): list/get/register/update/delete a dataset's source-table mapping, SRID, status, and metadata, gated by the admin authorization policy with safe schema-qualified-identifier validation on the edge/vertex table names. Migration 084 and the provider-neutral routing domain define immutable, optimistic-concurrency-protected topology generations and safely backfill the existing mapping as the sole active generation; a database-owned insert trigger atomically creates the initial generation for registrations from both old and new replicas during a rolling upgrade. Migration 086 adds a batched **edge and turn-restriction content-edit** admin surface (`/api/v1/admin/network-datasets/{id}/generations`, `POST .../generations/{generation}/edits`): allocate a `draft` generation, then submit an all-or-nothing add/update/delete batch (bounded size, `Idempotency-Key` at-most-once replay, `If-Match` optimistic concurrency) that stages edge geometry/attributes and turn restrictions in per-generation tables, validates the #2655 travel-profile cost-column allowlist and referential integrity between staged edges and restrictions, and atomically bumps the source revision and transitions the generation from `draft`/`dirty` to `dirty`. Migration 087 adds a **durable isolated shadow-topology rebuild** (`POST .../generations/{generation}/rebuild`, `GET .../rebuild/{attempt}`): a `NetworkTopologyRebuild` execution job (shared job infra, in-process worker) materializes a generation-scoped pgRouting-shaped edge/vertex shadow topology directly from the staged edits (not from `pgr_createTopology` geometry-snapping — the edits already carry explicit stable vertex references), checkpoints each stage (snapshot/build/analyze/validate/cleanup) so a restarted worker resumes cleanly, and transitions the generation `building` → `ready`/`failed`. Every rebuild-attempt mutation is fenced by a monotonic lease token (multi-node safe); an expired lease is either handed to a fresh worker or the attempt is failed and its orphan shadow tables cleaned by a self-healing reconciler. Migration 088 adds **atomic promotion/rollback** (`POST .../promote`, `POST .../rollback`, `GET .../promotions`): one Postgres transaction verifies a `ready` candidate's shadow-topology evidence (or a `retired` rollback target's artifacts still exist), atomically retires the old active generation, activates the target, and repoints `honua.network_datasets` to the target's own tables so the read-only `INetworkDatasetResolver` resolves every routing solve family from one consistent snapshot on its next read — no resolver code changes required. `Idempotency-Key`-scoped replay and an immutable promotion-history table back every call. Active/ready/building/failed/retired generations reject content edits with a sanitized 409; only this promotion path ever changes the active generation pointer. Multi-node chaos/scale coverage (simulated Redis outage, rolling-deployment overlap, remote batch-compute backends) is deferred; see ADR-0050 for the exact scope this delivery covers. |

Operators add a real profile only after adding its numeric forward/reverse columns to
the selected edge table, then updating the registry metadata, for example:

```sql
UPDATE honua.network_datasets
SET travel_profiles = '[
  {"name":"driving","forwardCostColumn":"cost","reverseCostColumn":"reverse_cost"},
  {"name":"walking","forwardCostColumn":"walking_cost","reverseCostColumn":"walking_reverse_cost"}
]'::jsonb
WHERE id = 'default';
```

Profile and column identifiers are validated before SQL construction, and the
registry checks both columns against `information_schema.columns`; only primitive
PostgreSQL numeric types (`smallint`, `integer`, `bigint`, `numeric`, `real`, and
`double precision`) are accepted. Domains are rejected deliberately so routing does
not depend on implicit domain coercion; expose a primitive numeric view/generated
column instead. Missing, nonnumeric, unsafe, or driving-less mappings fail closed and
are never advertised.

## VectorTileServer

Esri spec: [Vector Tile Service](https://developers.arcgis.com/rest/services-reference/enterprise/vector-tile-service/).

Served since before this matrix existed, but never mentioned by it until #2863 — the
derived roster is what surfaced it.

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Partial | Emits a real LOD table and a unioned extent for the resolved service, but `capabilities` (`"TilesOnly"`), `type`, the `tiles` template, and `exportTilesAllowed=false` are hardcoded; `minLOD` is always 0 even though the tile route rejects zooms below the configured minimum; the extent unions per-resource bboxes **without reprojecting them** while labelling the result with the service SR; only `f=json`/`pjson` are accepted. |
| `tile/{z}/{y}/{x}.pbf` | Partial | Renders genuine MVT through the canonical tile provider with zoom and coordinate validation, but serves only the service's **single primary tiled publication** — a multi-layer service returns one layer's data per tile, where an Esri vector-tile cache packs every layer into the tile. |
| `tilemap`, `tilemap/{z}/{y}/{x}/{dim}/{dim2}` | Partial | Returns a spec-shaped availability document, but availability is computed **arithmetically from the grid** (in-range implies available) and never consults the tile store, so an empty tile is still reported as present. The root form is hardcoded to the 1x1 top of the pyramid; `adjusted` is always `false`; the block edge is capped at 32; a zoom above the service `maxLOD` returns 400. |
| `resources/styles` | Implemented | Composes a Mapbox GL v8 style document from the layer's stored MapLibre style through the shared style projection, rewriting source, sprite, and glyph URLs onto this service; falls back to a deterministic default style when the layer has none stored. |
| `resources/styles/{resourcePath}` | Partial | Only the empty path and `root.json` resolve, both to the composed style document above; every other style sub-resource returns 404. |
| `resources/sprites/{spriteResource}` | Stub | Routes exist and return spec-shaped assets with no content: `sprite.json`/`sprite@2x.json` return `{}` and `sprite.png`/`sprite@2x.png` a 1x1 transparent PNG (`@2x` returns the identical 1x asset). No sprite store backs them (#1780). |
| `resources/fonts/{fontstack}/{range}.pbf` | Stub | Returns a spec-shaped glyph PBF containing one fontstack (`"Honua Default"`) with **zero glyphs**. No font store backs it: the requested `fontstack` is ignored and every name yields the same bytes; only the `0-255` range is served. |

## VersionManagementServer (branch versioning)

Esri spec: [Version Management Service](https://developers.arcgis.com/rest/services-reference/enterprise/version-management-service/).

> **Experimental, off by default.** Every route below is gated by the `versioning.branch`
> capability and returns **404** unless `Capabilities:Experimental:versioning.branch:Enabled=true`
> (or the global experimental switch) is set. It is **not** part of the first-release
> surface. The `"maturity": "experimental"` on each operation in the JSON is derived from
> that capability descriptor, not asserted by hand.

Branch versioning is backed by Postgres only; other providers register a no-op version
manager whose mutations return **501** rather than fabricating success.

| Operation(s) | Status | Notes |
| --- | --- | --- |
| Service metadata | Partial | Returns a **hardcoded** descriptor (`defaultVersionName` `"sde.DEFAULT"`, `capabilities` `"Create,Delete,Alter,Reconcile,Post"`) after a service-read check only. It does not consult the provider's versioning support, so a non-Postgres store advertises five capabilities whose mutations then 501. |
| `versions`, `versions/{versionGuid}` | Implemented | Lists and resolves real versions, filtered by the access policy (private versions are visible to owner/admin only). A non-visible version returns 404 rather than 403, so existence is not leaked; an invalid GUID returns 400. Providers without versioning return an empty list rather than an error. |
| `create` | Partial | Creates a real version and maps a duplicate name to 409, but **`parentVersion` is accepted and ignored** (always null), so a version can only be branched from DEFAULT. |
| `versions/{versionGuid}/alter`, `.../delete` | Implemented | Alter updates name/access/description; delete removes the version and returns 409 rather than deleting mid-reconcile/post. Both owner-or-admin gated. The returned `moment` is wall-clock time, not a branch generation. |
| `reconcile`, `post`, `resolveConflicts`, `inspectConflicts` | Implemented | Real reconcile/post over the Postgres version manager: `conflictResolution`/`resolutionPolicy`/`abortIfConflicts` and `conflictDetection` (including the Esri tokens) are honoured, `withPost=true` posts inline, and `async=true` returns 202 with a pollable job; lock contention returns 409 and an unsupported policy 400. `inspectConflicts` returns the pending conflict set with three-way base/DEFAULT/version images and per-field diffs; `resolveConflicts` applies per-feature `{layerId, objectId, choice}` resolutions. |
| `versions/{versionGuid}/jobs/{jobId}` | Implemented | Polls the durable job runner for an async reconcile/post; a job GUID belonging to another service or version returns 404. |
| `startReading`, `stopReading`, `startEditing`, `stopEditing` | Partial | **Stateless acknowledgements**: Honua holds no Esri edit/read session and issues no session token, because edits land per-request via `gdbVersion` rather than inside a server-held session. `startEditing` does resolve the version, return its durable branch generation as `moment`, and return 409 mid-reconcile/post; `stopEditing` has no `saveEdits` semantics. A client that depends on session tokens will not get Esri's behaviour. |

## Catalog / REST Info

Esri spec: [Catalog](https://developers.arcgis.com/rest/services-reference/enterprise/catalog/) and [REST Info](https://developers.arcgis.com/rest/services-reference/enterprise/info/).

| Operation(s) | Status | Notes |
| --- | --- | --- |
| `/rest/services` | Implemented | Graph-backed service directory, RBAC-filtered per principal, listing every service type a publication exposes. Returns 401/403 rather than a misleading empty list when every publication is denied. Does not emit `folders`. |
| `/rest/info` | Stub | Returns the spec-shaped `{"authInfo":{"isTokenBasedSecurity":...}}` document, but the value is **not wired to the portal-token configuration**: it is always `false`, and `tokenServicesUrl` is absent from the response type entirely. An Esri client performing auth discovery at the canonical ArcGIS Server location is therefore told the server is not token-secured. [`/sharing/rest/info`](#portal-sharing) carries the real, configuration-driven `authInfo`. The route is not removed because clients probe it. |

Honua never advertises an ArcGIS Server `currentVersion`/`fullVersion` anywhere in this
surface; clients that branch on it must treat its absence as "unknown", not as a version
floor.

## Portal Sharing

Esri spec: [ArcGIS REST API - Users, groups, and items](https://developers.arcgis.com/rest/users-groups-and-items/).

The read surface (`info`, `portals/self`, `community/self`, `search`, `content/items/*`)
and the whole community/sharing surface are gated by `Sharing:ReadSurface:Enabled` plus
the `identity.portal-sharing` entitlement - both return **404** when off. Token issuance
and the OAuth2 surface are gated by `Authentication:PortalToken:Enabled` plus the
`identity.portal-token` entitlement.

| Operation(s) | Status | Notes |
| --- | --- | --- |
| `generateToken` | Implemented | Opaque tokens consumable on `/rest/services/*`. |
| `info` | Implemented | Emits `isTokenBasedSecurity` from the live portal-token configuration plus a real `tokenServicesUrl`. `currentVersion`/`fullVersion` are deliberately omitted (policy) - an intentional divergence from the Esri document. |
| `portals/self` | Partial | Only `username` and `fullName` are real (from the caller's claims). The portal id, name, `portalName`, `isPortal`, and `role` are **hardcoded**; org settings, `urlKey`, `allSSL`, basemaps, and privileges are absent. Sufficient for client bootstrap, not for portal administration. |
| `community/self` | Partial | Returns `username` and `fullName` from claims; `role` is **hardcoded** `"org_user"` rather than derived from RBAC, and Esri's `groups` array is absent. `id`/`email`/`orgId`/`privileges` are not emitted. Anonymous callers get 401. |
| `search` | Partial | Honours `q`, `start`, `num` (capped at 100), `sortField` (title/created/modified/owner only) and `sortOrder`. The `q` grammar supports `type:`/`owner:`/`tags:`/`id:`/`title:` plus free text; unknown qualifiers **silently degrade to free text**, and boolean operators, wildcards, and ranges are unsupported. `bbox`, `filter`, `categories`, `countFields`, and `restrict` are ignored. The item universe is FeatureServer/MapServer/ImageServer graph services only. |
| `content/items/{id}`, `.../data` | Partial | Projects a graph service into an Esri item document; RBAC-correct (404 for unknown and hidden alike). `owner` falls back to a hardcoded value when unauthored; `numViews`/`size`/`thumbnail`/`licenseInfo`/`ownerFolder`/`protected`/`appCategories` are absent. **`/data` currently returns the same item description document rather than the item's data payload** (web map JSON / file bytes) - a known divergence. |
| `content/items/{itemId}/share`, `.../unshare` | Partial | Records sharing intent in an **in-memory, single-node** store that does not survive restart and is not replicated. It is **write-only**: no read path consults it, so sharing an item changes nothing `search` or `content/items/{id}` return for any caller. Item existence is not validated, `notSharedWith` is always empty, and `confirmItemControl`, bulk `items`, and folder are ignored. Owner-or-admin gated. |
| `community/createGroup`, `groups/{id}`, `.../addUsers`, `.../removeUsers`, `.../delete` | Partial | **In-memory, single-node, non-durable** group overlay. Group membership is never consulted by any read path, so it does not affect item visibility. `createGroup` accepts only `title`/`description`/`access`/`tags` and silently defaults an unknown `access` to private. `addUsers`/`removeUsers` ignore `admins` and return `{success, groupId, members}` rather than Esri's `notAdded`/`notRemoved`. Private-group existence is not leaked; the owner cannot be removed; owner-or-admin gated. |
| `oauth2/authorize`, `oauth2/callback` | Implemented (opt-in) | Authorization-code + PKCE bridge delegating the user leg to the operator's OIDC provider. Returns 404 until an OIDC provider is configured, and **every `redirect_uri` is rejected until `Authentication:PortalToken:OAuth2:AllowedRedirectUris` is populated**. PKCE required by default. |
| `oauth2/token` | Implemented | `authorization_code` and `refresh_token` grants with rotating refresh tokens; Basic-auth client credentials accepted. POST only. The `client_credentials` grant is opt-in (default off, returns `unsupported_grant_type`), as is its IdP/OIDC federation (#1889). |
| `oauth2/introspect` | Implemented (off by default) | RFC 7662 over opaque, JWT, and refresh tokens, admin-authorized; 404 when disabled (#1890). Emits `active`, `sub`, `username`, `scope`, `token_type`, `exp`; `client_id`, `iat`, `nbf`, `aud`, `iss`, `jti` are not emitted. |
| `oauth2/revoke` | Implemented | RFC 7009 for opaque, JWT, and refresh tokens; always returns 200 per section 2.2. |

## Sources and upkeep

- **Hand-authored judgement source:** [`docs/gis/data/geoservices-parity-judgment.json`](../../gis/data/geoservices-parity-judgment.json) — the *only* file to edit. It carries the Implemented/Partial/Stub verdict and the gap prose, keyed to derived operation paths.
- **Generated machine-readable export:** [`docs/gis/data/geoservices-rest-parity.json`](../../gis/data/geoservices-rest-parity.json) — **do not hand-edit.** Regenerate with `scripts/generate-geoservices-parity.sh` and commit the result. Its `esriPaths`, `honuaEndpoints`, and `capabilityMaturity` fields are derived from the server's endpoint registry; a hand edit to them cannot survive the drift guard. Note `capabilityMaturity` is the ADR-0058 capability tier (*is this route in the release?*), **not** the parity `status` (*how much of Esri's documented behaviour does it support?*) — a Stub on an in-release route correctly reads `status: stub`, `capabilityMaturity: ["implemented"]`.
- **What is enforced.** `GeoServicesParityMatrixDriftTests` (in `Honua.Architecture.Tests`) fails the build when: a served GeoServices operation carries **no** judgement (served-but-unclassified); a judgement names an operation that is **not served** (the over-claim direction); an operation recorded Not implemented *is* served (the under-claim direction); one operation carries two judgements; a served Esri service type has no home in the matrix; a `Partial`/`Stub` states no gap; an `evidence` path does not resolve to a real file; or the committed export is not byte-identical to freshly-generated output. **Do not satisfy any of it by relabelling a status** — `Stub` is a deliberate, honest category and must keep its meaning. If a gate here pressures you to upgrade a status, the gate is wrong.
- **What is not enforced (read this before trusting a status).** The gate proves a claimed operation *exists* and that nothing served is unclassified. It never proves a status is the *right* one. Nothing mechanically stops a `Partial` whose parameter coverage has since regressed, or a `Stub` mislabelled `Implemented`. Those remain human review, anchored by `lastReviewed` in the judgement source and the release checklist.
- **Not to be confused with** the import-fidelity scorecard workflows/tests (`import-fidelity-scorecard-governance.yml`, `geoservices-import-fidelity-nightly.yml`, `GeoservicesImportFidelityIntegrationTests`, `import-fidelity-scorecard-baseline.json`), which measure *data-import fidelity* across ten dataset cases and never read this matrix. They are a different question that once shared a confusingly similar name; that side was renamed off `parity` by [#2861](https://github.com/honua-io/honua-server/issues/2861).
- [GeocodeServer matrix](../../internal/spikes/geocode-server-matrix.md), [run geoprocessing](../../guides/query-analyze/run-geoprocessing.md), [authentication](../../guides/secure/authentication.md) — drill-downs for the services not detailed on this page.
- [Supported clients](clients.md) — which Esri clients are certified against this surface.
- Release owners verify this page during the [release checklist](../../internal/contributor/RELEASE_CHECKLIST.md).
