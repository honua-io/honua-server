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
}

export interface HonuaErrorContext {
  request: HonuaRequestContext;
  error: unknown;
}

export interface HonuaRequestInterceptor {
  before?(
    context: HonuaRequestContext,
  ): void | HonuaRequestMutation | Promise<void | HonuaRequestMutation>;
  after?(context: HonuaResponseContext): void | Promise<void>;
  error?(context: HonuaErrorContext): void | Promise<void>;
}

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
  geometryType?: string;
  spatialRel?: string;
  returnDistinctValues?: boolean;
  returnCentroid?: boolean;
  groupByFieldsForStatistics?: string;
  outStatistics?: string | readonly Record<string, unknown>[];
  resultOffset?: number;
  resultRecordCount?: number;
  extraParams?: Record<string, string | number | boolean>;
}

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
  geometryType?: string;
  spatialRel?: string;
  returnDistinctValues?: boolean;
  returnCentroid?: boolean;
  groupByFieldsForStatistics?: string;
  outStatistics?: string | readonly Record<string, unknown>[];
  resultOffset?: number;
  resultRecordCount?: number;
  extraParams?: Record<string, string | number | boolean>;
}

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
}

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
}

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

export interface MapLegendRequest {
  serviceId: string;
  responseFormat?: "json" | "pjson";
  size?: string | number | [number, number];
  dynamicLayers?: string;
  extraParams?: Record<string, string | number | boolean>;
}

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

export interface HonuaRawRequest {
  path: string;
  method?: QueryMethod;
  responseFormat?: "json" | "pjson";
  query?: Record<string, string | number | boolean>;
  headers?: HeadersInit;
  body?: BodyInit | null;
}

export interface ApplyEditsRequest {
  serviceId: string;
  layerId: number;
  adds?: unknown[];
  updates?: unknown[];
  deletes?: number[] | string;
  rollbackOnFailure?: boolean;
}

export interface HonuaClientOptions {
  baseUrl: string;
  apiKey?: string;
  bearerToken?: string;
  fetchFn?: typeof fetch;
  interceptors?: readonly HonuaRequestInterceptor[];
  timeoutMs?: number;
  retry?: HonuaRetryOptions;
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
}

export interface OgcItemRequest extends OgcCollectionRequest {
  featureId: string | number;
  crs?: string;
}

export interface OgcCreateItemRequest extends OgcCollectionRequest {
  feature: unknown;
  headers?: HeadersInit;
}

export interface OgcReplaceItemRequest extends OgcItemRequest {
  feature: unknown;
  headers?: HeadersInit;
}

export interface OgcPatchItemRequest extends OgcItemRequest {
  patch: unknown;
  headers?: HeadersInit;
}

export interface OgcDeleteItemRequest extends OgcItemRequest {}
