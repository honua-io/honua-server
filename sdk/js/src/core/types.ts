export type QueryMethod = "GET" | "POST";

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
}
