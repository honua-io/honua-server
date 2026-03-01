export type QueryMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export interface HonuaRequestContext {
  url: string;
  path: string;
  method: QueryMethod;
  init: RequestInit;
}

export interface HonuaRequestMutation {
  url?: string;
  method?: QueryMethod;
  init?: RequestInit;
}

export interface HonuaResponseContext {
  request: HonuaRequestContext;
  response: Response;
  /** Wall-clock duration of the fetch call in milliseconds. */
  durationMs: number;
}

export interface HonuaErrorContext {
  request: HonuaRequestContext;
  error: unknown;
  /** Wall-clock duration of the fetch call in milliseconds, if available. */
  durationMs?: number;
}

export interface HonuaRequestInterceptor {
  before?(context: HonuaRequestContext): void | HonuaRequestMutation | Promise<void | HonuaRequestMutation>;
  after?(context: HonuaResponseContext): void | Promise<void>;
  error?(context: HonuaErrorContext): void | Promise<void>;
}

/** Parameters for querying features from a feature layer. */
export interface QueryFeaturesRequest {
  serviceId: string;
  layerId: number;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  orderByFields?: string;
  objectIds?: number[] | string;
  geometry?: string | Record<string, unknown>;
  geometryType?: EsriGeometryType;
  spatialRel?: EsriSpatialRel;
  returnDistinctValues?: boolean;
  returnCentroid?: boolean;
  groupByFieldsForStatistics?: string;
  outStatistics?: string | readonly Record<string, unknown>[];
  resultOffset?: number;
  resultRecordCount?: number;
  extraParams?: Record<string, string | number | boolean>;
  signal?: AbortSignal;
}

/** Parameters for querying features from a map service layer. */
export interface MapLayerQueryRequest {
  serviceId: string;
  layerId: number;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  orderByFields?: string;
  objectIds?: number[] | string;
  geometry?: string | Record<string, unknown>;
  geometryType?: EsriGeometryType;
  spatialRel?: EsriSpatialRel;
  returnDistinctValues?: boolean;
  returnCentroid?: boolean;
  groupByFieldsForStatistics?: string;
  outStatistics?: string | readonly Record<string, unknown>[];
  resultOffset?: number;
  resultRecordCount?: number;
  extraParams?: Record<string, string | number | boolean>;
  signal?: AbortSignal;
}

/** Parameters for querying related records from a feature layer. */
export interface QueryRelatedRecordsRequest {
  serviceId: string;
  layerId: number;
  relationshipId: number;
  objectIds?: number[] | string;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
  signal?: AbortSignal;
}

/** Parameters for querying related records from a map service layer. */
export interface MapRelatedRecordsRequest {
  serviceId: string;
  layerId: number;
  relationshipId: number;
  objectIds?: number[] | string;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
  signal?: AbortSignal;
}

/** Parameters for exporting a map image from a map service. */
export interface ExportMapRequest {
  serviceId: string;
  bbox: string | [number, number, number, number];
  size: string | [number, number];
  responseFormat?: "json" | "pjson";
  format?: string;
  dpi?: number;
  transparent?: boolean;
  layers?: string;
  bboxSr?: string | number;
  imageSr?: string | number;
  backgroundColor?: string;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

/** Parameters for requesting legend information from a map service. */
export interface MapLegendRequest {
  serviceId: string;
  responseFormat?: "json" | "pjson";
  size?: string | number | [number, number];
  dynamicLayers?: string;
  extraParams?: Record<string, string | number | boolean>;
}

/** Parameters for identifying features at a point on a map service. */
export interface MapIdentifyRequest {
  serviceId: string;
  geometry: string | Record<string, unknown>;
  geometryType?: string;
  sr?: string | number;
  layers?: string;
  tolerance?: number;
  mapExtent: string | [number, number, number, number];
  imageDisplay: string | [number, number, number];
  returnGeometry?: boolean;
  responseFormat?: "json" | "pjson";
  maxAllowableOffset?: number;
  layerDefs?: string;
  dynamicLayers?: string;
  time?: string;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

/** Parameters for finding features by attribute value in a map service. */
export interface MapFindRequest {
  serviceId: string;
  searchText: string;
  contains?: boolean;
  searchFields?: string | string[];
  layers?: string;
  sr?: string | number;
  layerDefs?: string;
  returnGeometry?: boolean;
  maxAllowableOffset?: number;
  dynamicLayers?: string;
  returnZ?: boolean;
  returnM?: boolean;
  gdbVersion?: string;
  time?: string;
  relationParam?: string;
  responseFormat?: "json" | "pjson";
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

/** Parameters for making a raw HTTP request through the client. */
export interface HonuaRawRequest {
  path: string;
  method?: QueryMethod;
  responseFormat?: "json" | "pjson";
  query?: Record<string, string | number | boolean>;
  headers?: HeadersInit;
  body?: BodyInit | null;
  signal?: AbortSignal;
}

/** Parameters for applying feature edits (add, update, delete) to a feature layer. */
export interface ApplyEditsRequest {
  serviceId: string;
  layerId: number;
  adds?: HonuaFeature[];
  updates?: HonuaFeature[];
  deletes?: number[] | string;
  rollbackOnFailure?: boolean;
}

export type HonuaTransport = "rest" | "grpc-web";

export interface HonuaClientOptions {
  baseUrl: string;
  apiKey?: string;
  bearerToken?: string;
  fetchFn?: typeof fetch;
  interceptors?: readonly HonuaRequestInterceptor[];
  timeoutMs?: number;
  retry?: HonuaRetryOptions;
  /**
   * When `true`, query methods use `f=pbf` for binary protobuf responses
   * and decode them transparently into the same JSON-compatible shape.
   * Falls back to `f=json` on decode failure. Default: `false`.
   */
  preferBinary?: boolean;
  /**
   * Transport protocol. `"rest"` uses JSON/PBF over HTTP (default).
   * `"grpc-web"` uses typed RPC via Connect gRPC-Web transport.
   */
  transport?: HonuaTransport;
}

export interface HonuaRetryOptions {
  maxRetries?: number;
  baseDelayMs?: number;
  maxDelayMs?: number;
  retryStatuses?: readonly number[];
}

export type OgcResponseFormat = "json" | "html" | "geojson" | "gml" | "csv" | "schemajson" | "schema+json";

export interface OgcMetadataRequest {
  responseFormat?: OgcResponseFormat | string;
  extraParams?: Record<string, string | number | boolean>;
}

export interface OgcCollectionRequest extends OgcMetadataRequest {
  collectionId: string | number;
}

export interface OgcItemsRequest extends OgcCollectionRequest {
  limit?: number;
  offset?: number;
  bbox?: string;
  datetime?: string;
  filter?: string;
  ids?: string | readonly (string | number)[];
  properties?: string | readonly string[];
  sortby?: string;
  crs?: string;
  signal?: AbortSignal;
}

export interface OgcItemRequest extends OgcCollectionRequest {
  featureId: string | number;
  crs?: string;
  signal?: AbortSignal;
}

export interface OgcCreateItemRequest extends OgcCollectionRequest {
  feature: GeoJsonFeature | Record<string, unknown>;
  headers?: HeadersInit;
}

export interface OgcReplaceItemRequest extends OgcItemRequest {
  feature: GeoJsonFeature | Record<string, unknown>;
  headers?: HeadersInit;
}

export interface OgcPatchItemRequest extends OgcItemRequest {
  patch: Record<string, unknown>;
  headers?: HeadersInit;
}

export interface OgcDeleteItemRequest extends OgcItemRequest {}

// ── Esri Geometry Types ───────────────────────────────────────

/** Well-known Esri geometry type identifiers with open-ended fallback. */
export type EsriGeometryType =
  | "esriGeometryPoint"
  | "esriGeometryPolyline"
  | "esriGeometryPolygon"
  | "esriGeometryEnvelope"
  | "esriGeometryMultipoint"
  | (string & {});

/** Well-known Esri spatial relationship identifiers. */
export type EsriSpatialRel =
  | "esriSpatialRelIntersects"
  | "esriSpatialRelContains"
  | "esriSpatialRelCrosses"
  | "esriSpatialRelEnvelopeIntersects"
  | "esriSpatialRelIndexIntersects"
  | "esriSpatialRelOverlaps"
  | "esriSpatialRelTouches"
  | "esriSpatialRelWithin"
  | (string & {});

/** Well-known Esri field type identifiers. */
export type EsriFieldType =
  | "esriFieldTypeString"
  | "esriFieldTypeInteger"
  | "esriFieldTypeSmallInteger"
  | "esriFieldTypeDouble"
  | "esriFieldTypeSingle"
  | "esriFieldTypeDate"
  | "esriFieldTypeOID"
  | "esriFieldTypeGeometry"
  | "esriFieldTypeBlob"
  | "esriFieldTypeRaster"
  | "esriFieldTypeGUID"
  | "esriFieldTypeGlobalID"
  | "esriFieldTypeXML"
  | (string & {});

/** An Esri point geometry. */
export interface EsriPoint {
  x: number;
  y: number;
  z?: number;
  m?: number;
  spatialReference?: HonuaSpatialReference;
}

/** An Esri polyline geometry. */
export interface EsriPolyline {
  paths: number[][][];
  spatialReference?: HonuaSpatialReference;
}

/** An Esri polygon geometry. */
export interface EsriPolygon {
  rings: number[][][];
  spatialReference?: HonuaSpatialReference;
}

/** An Esri envelope (bounding box) geometry. */
export interface EsriEnvelope {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference?: HonuaSpatialReference;
}

/** An Esri multipoint geometry. */
export interface EsriMultipoint {
  points: number[][];
  spatialReference?: HonuaSpatialReference;
}

/** Union of all Esri geometry shapes. */
export type EsriGeometry = EsriPoint | EsriPolyline | EsriPolygon | EsriEnvelope | EsriMultipoint;

/** A GeoJSON Feature object. */
export interface GeoJsonFeature {
  type: "Feature";
  id?: string | number;
  geometry: import("../expr/expression.js").GeoJsonGeometry | null;
  properties: Record<string, unknown> | null;
}

// ── Response Types ────────────────────────────────────────────

// ── Geometry & Spatial Reference ──────────────

/** A coordinate system reference, identified by WKID or WKT. */
export interface HonuaSpatialReference {
  wkid?: number;
  latestWkid?: number;
  wkt?: string;
}

/** An axis-aligned bounding box with an optional spatial reference. */
export interface HonuaExtent {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference?: HonuaSpatialReference;
}

// ── Field Definitions ─────────────────────────

/** Metadata for a single attribute field in a layer. */
export interface HonuaFieldInfo {
  name: string;
  /** Esri field type string, e.g. `"esriFieldTypeString"`. */
  type: EsriFieldType;
  alias?: string;
  length?: number;
  nullable?: boolean;
  editable?: boolean;
  defaultValue?: unknown;
}

// ── Feature ───────────────────────────────────

/** A single feature with attribute values and optional geometry. */
export interface HonuaFeature {
  attributes: Record<string, unknown>;
  geometry?: EsriGeometry | Record<string, unknown> | null;
}

// ── Query Responses ───────────────────────────

/** Response from a feature or map-layer query returning features. */
export interface HonuaQueryResponse {
  objectIdFieldName?: string;
  geometryType?: EsriGeometryType;
  spatialReference?: HonuaSpatialReference;
  fields?: HonuaFieldInfo[];
  features?: HonuaFeature[];
  exceededTransferLimit?: boolean;
}

/** Response from a count-only query (`returnCountOnly=true`). */
export interface HonuaCountResponse {
  count: number;
}

/** Response from an IDs-only query (`returnIdsOnly=true`). */
export interface HonuaObjectIdsResponse {
  objectIdFieldName: string;
  objectIds: number[];
}

/** Response from an extent-only query (`returnExtentOnly=true`). */
export interface HonuaExtentResponse {
  extent: HonuaExtent;
  count?: number;
}

// ── Apply Edits Response ──────────────────────

/** Result for a single add, update, or delete operation. */
export interface HonuaEditResult {
  objectId: number;
  success: boolean;
  error?: { code: number; description: string };
}

/** Response from `applyEdits` containing results for each operation type. */
export interface HonuaApplyEditsResponse {
  addResults?: HonuaEditResult[];
  updateResults?: HonuaEditResult[];
  deleteResults?: HonuaEditResult[];
}

// ── Related Records Response ──────────────────

/** A group of related records for a single source object ID. */
export interface HonuaRelatedRecordGroup {
  objectId: number;
  relatedRecords?: HonuaFeature[];
}

/** Response from `queryRelatedRecords`. */
export interface HonuaRelatedRecordsResponse {
  relatedRecordGroups?: HonuaRelatedRecordGroup[];
  fields?: HonuaFieldInfo[];
  geometryType?: EsriGeometryType;
  spatialReference?: HonuaSpatialReference;
}

// ── Map Service Responses ─────────────────────

/** Response from `exportMap` / `export`. */
export interface HonuaExportMapResponse {
  href?: string;
  width?: number;
  height?: number;
  extent?: HonuaExtent;
  scale?: number;
}

/** A single legend entry (swatch) for a layer. */
export interface HonuaLegendEntry {
  label?: string;
  url?: string;
  imageData?: string;
  contentType?: string;
  width?: number;
  height?: number;
}

/** Legend information for a single layer. */
export interface HonuaLegendLayer {
  layerId: number;
  layerName?: string;
  layerType?: string;
  minScale?: number;
  maxScale?: number;
  legend?: HonuaLegendEntry[];
}

/** Response from `legend`. */
export interface HonuaLegendResponse {
  layers?: HonuaLegendLayer[];
}

/** A single identified feature with layer info. */
export interface HonuaIdentifyResult {
  layerId: number;
  layerName?: string;
  displayFieldName?: string;
  value?: string;
  attributes?: Record<string, unknown>;
  geometryType?: string;
  geometry?: Record<string, unknown> | null;
}

/** Response from `identify`. */
export interface HonuaIdentifyResponse {
  results?: HonuaIdentifyResult[];
}

/** A single result from `find`. */
export interface HonuaFindResult {
  layerId: number;
  layerName?: string;
  displayFieldName?: string;
  foundFieldName?: string;
  value?: string;
  attributes?: Record<string, unknown>;
  geometryType?: string;
  geometry?: Record<string, unknown> | null;
}

/** Response from `find`. */
export interface HonuaFindResponse {
  results?: HonuaFindResult[];
}

// ── Attachment Responses ──────────────────────

/** Metadata for a single attachment on a feature. */
export interface HonuaAttachmentInfo {
  id: number;
  name?: string;
  contentType?: string;
  size?: number;
  parentObjectId?: number;
}

/** Response from `listAttachments`. */
export interface HonuaAttachmentListResponse {
  attachmentInfos?: HonuaAttachmentInfo[];
}

/** Result of an add/update/delete attachment operation. */
export interface HonuaAttachmentEditResult {
  objectId?: number;
  success: boolean;
  error?: { code: number; description: string };
}

/** Response from `addAttachment`. */
export interface HonuaAddAttachmentResponse {
  addAttachmentResult: HonuaAttachmentEditResult;
}

/** Response from `updateAttachment`. */
export interface HonuaUpdateAttachmentResponse {
  updateAttachmentResult: HonuaAttachmentEditResult;
}

/** Response from `deleteAttachments`. */
export interface HonuaDeleteAttachmentsResponse {
  deleteAttachmentResults: HonuaAttachmentEditResult[];
}

/** Response from attachment query with grouped results. */
export interface HonuaAttachmentGroup {
  parentObjectId: number;
  attachmentInfos?: HonuaAttachmentInfo[];
}

/** Response from `queryAttachments`. */
export interface HonuaQueryAttachmentsResponse {
  attachmentGroups?: HonuaAttachmentGroup[];
}

// ── Layer / Service Metadata ──────────────────

/** Metadata for a relationship between two layers/tables. */
export interface HonuaRelationshipInfo {
  id: number;
  name?: string;
  relatedTableId: number;
  role?: string;
  cardinality?: string;
  keyField?: string;
  keyFieldInRelationshipTable?: string;
}

/** Full metadata for a single feature or map layer. */
export interface HonuaLayerMetadata {
  id: number;
  name: string;
  type?: string;
  geometryType?: EsriGeometryType;
  description?: string;
  fields?: HonuaFieldInfo[];
  extent?: HonuaExtent;
  spatialReference?: HonuaSpatialReference;
  maxRecordCount?: number;
  supportsAttachments?: boolean;
  relationships?: HonuaRelationshipInfo[];
}

/** Top-level metadata for a FeatureServer or MapServer service. */
export interface HonuaServiceMetadata {
  serviceDescription?: string;
  layers?: Array<{ id: number; name: string }>;
  tables?: Array<{ id: number; name: string }>;
  spatialReference?: HonuaSpatialReference;
  fullExtent?: HonuaExtent;
  maxRecordCount?: number;
}

/** Response from `listServices()` — the REST services directory. */
export interface HonuaServicesResponse {
  currentVersion?: number;
  folders?: string[];
  services?: Array<{ name: string; type: string }>;
}

// ── OGC Responses ─────────────────────────────

/** OGC API Features collection response (GeoJSON FeatureCollection). */
export interface HonuaOgcFeatureCollectionResponse {
  type: "FeatureCollection";
  features: HonuaOgcFeatureResponse[];
  numberMatched?: number;
  numberReturned?: number;
  links?: HonuaOgcLink[];
}

/** A single OGC API Feature (GeoJSON Feature). */
export interface HonuaOgcFeatureResponse {
  type: "Feature";
  id?: string | number;
  geometry: import("../expr/expression.js").GeoJsonGeometry | null;
  properties: Record<string, unknown> | null;
  links?: HonuaOgcLink[];
}

/** A hypermedia link in an OGC API response. */
export interface HonuaOgcLink {
  href: string;
  rel?: string;
  type?: string;
  title?: string;
}

// ── OGC Metadata Responses ───────────────────

/** Response from the OGC API landing page (`/ogc/features`). */
export interface HonuaOgcLandingResponse {
  title?: string;
  description?: string;
  links?: HonuaOgcLink[];
}

/** Response from OGC API conformance (`/ogc/features/conformance`). */
export interface HonuaOgcConformanceResponse {
  conformsTo: string[];
}

/** A single collection summary in an OGC collections listing. */
export interface HonuaOgcCollectionSummary {
  id: string;
  title?: string;
  description?: string;
  links?: HonuaOgcLink[];
  extent?: {
    spatial?: { bbox?: number[][]; crs?: string };
    temporal?: { interval?: (string | null)[][]; trs?: string };
  };
  itemType?: string;
  crs?: string[];
}

/** Response from OGC API collections listing (`/ogc/features/collections`). */
export interface HonuaOgcCollectionsResponse {
  collections: HonuaOgcCollectionSummary[];
  links?: HonuaOgcLink[];
}

/** Response from a single OGC API collection (`/ogc/features/collections/{id}`). */
export interface HonuaOgcCollectionMetadata extends HonuaOgcCollectionSummary {}

/** A single queryable property definition. */
export interface HonuaOgcQueryableProperty {
  title?: string;
  description?: string;
  type?: string;
  enum?: unknown[];
}

/** Response from OGC API queryables (`/ogc/features/collections/{id}/queryables`). */
export interface HonuaOgcQueryablesResponse {
  type?: string;
  title?: string;
  description?: string;
  properties?: Record<string, HonuaOgcQueryableProperty>;
}

// ── Schema-Aware Typed Collections ────────────

/**
 * A feature with a typed attribute schema. Use with `HonuaFeatureLayer<T>` to
 * get autocompletion and type checking on attribute access.
 */
export interface HonuaTypedFeature<T = Record<string, unknown>> {
  attributes: T;
  geometry?: EsriGeometry | Record<string, unknown> | null;
}

/**
 * A query response with typed feature attributes. The generic parameter `T`
 * flows from `HonuaFeatureLayer<T>` to provide typed attribute access.
 */
export interface HonuaTypedQueryResponse<T = Record<string, unknown>> {
  objectIdFieldName?: string;
  geometryType?: EsriGeometryType;
  spatialReference?: HonuaSpatialReference;
  fields?: HonuaFieldInfo[];
  features?: HonuaTypedFeature<T>[];
  exceededTransferLimit?: boolean;
}
