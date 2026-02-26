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
