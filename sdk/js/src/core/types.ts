export type QueryMethod = "GET" | "POST";

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
}
