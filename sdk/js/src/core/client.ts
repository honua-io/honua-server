import { HonuaHttpError } from "./errors.js";
import type {
  ApplyEditsRequest,
  ExportMapRequest,
  HonuaClientOptions,
  HonuaErrorContext,
  HonuaRawRequest,
  HonuaRequestContext,
  HonuaRequestInterceptor,
  HonuaRequestMutation,
  HonuaResponseContext,
  MapFindRequest,
  MapIdentifyRequest,
  MapLegendRequest,
  QueryFeaturesRequest,
  QueryMethod,
  QueryRelatedRecordsRequest,
} from "./types.js";
import {
  HonuaFeatureLayer,
  HonuaMapService,
  HonuaService,
} from "./surfaces.js";

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, "");
}

function normalizePath(path: string): string {
  if (path.startsWith("http://") || path.startsWith("https://")) {
    return path;
  }
  return path.startsWith("/") ? path : `/${path}`;
}

function resolveRequestUrl(baseUrl: string, path: string): string {
  if (path.startsWith("http://") || path.startsWith("https://")) {
    return path;
  }
  return `${baseUrl}${path}`;
}

function normalizeOutFields(outFields: string | string[] | undefined): string {
  if (!outFields) {
    return "*";
  }
  return Array.isArray(outFields) ? outFields.join(",") : outFields;
}

function encodeFormValue(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (typeof value === "number" || typeof value === "boolean") {
    return String(value);
  }
  return JSON.stringify(value);
}

function normalizeBBox(bbox: ExportMapRequest["bbox"]): string {
  return Array.isArray(bbox) ? bbox.join(",") : bbox;
}

function normalizeSize(size: ExportMapRequest["size"]): string {
  return Array.isArray(size) ? size.join(",") : size;
}

function normalizeLegendSize(size: NonNullable<MapLegendRequest["size"]>): string {
  if (typeof size === "number") {
    return String(size);
  }
  if (Array.isArray(size)) {
    return size.join(",");
  }
  return size;
}

function normalizeIdentifyGeometry(geometry: MapIdentifyRequest["geometry"]): string {
  return typeof geometry === "string" ? geometry : JSON.stringify(geometry);
}

function normalizeMapExtent(mapExtent: MapIdentifyRequest["mapExtent"]): string {
  return Array.isArray(mapExtent) ? mapExtent.join(",") : mapExtent;
}

function normalizeImageDisplay(imageDisplay: MapIdentifyRequest["imageDisplay"]): string {
  return Array.isArray(imageDisplay) ? imageDisplay.join(",") : imageDisplay;
}

function normalizeSearchFields(searchFields: MapFindRequest["searchFields"]): string {
  if (!searchFields) {
    return "";
  }
  return Array.isArray(searchFields) ? searchFields.join(",") : searchFields;
}

export class HonuaClient {
  private readonly baseUrl: string;
  private readonly fetchFn: typeof fetch;
  private readonly defaultHeaders: HeadersInit;
  private readonly interceptors: readonly HonuaRequestInterceptor[];

  public constructor(options: HonuaClientOptions) {
    this.baseUrl = normalizeBaseUrl(options.baseUrl);
    this.fetchFn = options.fetchFn ?? fetch;

    const headers: Record<string, string> = {};
    if (options.apiKey) {
      headers["X-API-Key"] = options.apiKey;
    }
    if (options.bearerToken) {
      headers.Authorization = `Bearer ${options.bearerToken}`;
    }
    this.defaultHeaders = headers;
    this.interceptors = options.interceptors ?? [];
  }

  public service(serviceId: string): HonuaService {
    return new HonuaService({
      client: this,
      serviceId,
    });
  }

  public featureLayer(serviceId: string, layerId: number): HonuaFeatureLayer {
    return new HonuaFeatureLayer({
      client: this,
      serviceId,
      layerId,
    });
  }

  public mapService(serviceId: string): HonuaMapService {
    return new HonuaMapService({
      client: this,
      serviceId,
    });
  }

  public async listServices(format: "json" | "pjson" = "json"): Promise<unknown> {
    const query = new URLSearchParams({ f: format });
    return this.requestJson("GET", `/rest/services?${query.toString()}`);
  }

  public async request(request: HonuaRawRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", request.responseFormat ?? "json");
    if (request.query) {
      for (const [key, value] of Object.entries(request.query)) {
        params.set(key, String(value));
      }
    }

    const normalizedPath = normalizePath(request.path);
    const pathWithQuery = params.size > 0 ? `${normalizedPath}?${params.toString()}` : normalizedPath;
    return this.requestJson(method, pathWithQuery, {
      headers: request.headers,
      body: request.body,
    });
  }

  public async getLayerMetadata(serviceId: string, layerId: number): Promise<unknown> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/FeatureServer/${layerId}?${query.toString()}`,
    );
  }

  public async getMapServiceMetadata(serviceId: string): Promise<unknown> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/MapServer?${query.toString()}`,
    );
  }

  public async queryFeatures(request: QueryFeaturesRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", "json");
    params.set("where", request.where ?? "1=1");
    params.set("outFields", normalizeOutFields(request.outFields));
    params.set("returnGeometry", String(request.returnGeometry ?? true));

    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/FeatureServer/${request.layerId}/query`;
    if (method === "GET") {
      return this.requestJson("GET", `${path}?${params.toString()}`);
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public async applyEdits(request: ApplyEditsRequest): Promise<unknown> {
    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/FeatureServer/${request.layerId}/applyEdits`;
    const params = new URLSearchParams();
    params.set("f", "json");
    params.set("rollbackOnFailure", String(request.rollbackOnFailure ?? true));
    if (request.adds !== undefined) {
      params.set("adds", encodeFormValue(request.adds));
    }
    if (request.updates !== undefined) {
      params.set("updates", encodeFormValue(request.updates));
    }
    if (request.deletes !== undefined) {
      params.set("deletes", encodeFormValue(request.deletes));
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public async queryRelatedRecords(request: QueryRelatedRecordsRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", "json");
    params.set("relationshipId", String(request.relationshipId));
    if (request.objectIds !== undefined) {
      params.set(
        "objectIds",
        Array.isArray(request.objectIds) ? request.objectIds.join(",") : String(request.objectIds),
      );
    }
    params.set("where", request.where ?? "1=1");
    params.set("outFields", normalizeOutFields(request.outFields));
    params.set("returnGeometry", String(request.returnGeometry ?? true));

    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path =
      `/rest/services/${encodeURIComponent(request.serviceId)}` +
      `/FeatureServer/${request.layerId}/queryRelatedRecords`;
    if (method === "GET") {
      return this.requestJson("GET", `${path}?${params.toString()}`);
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public async exportMap(request: ExportMapRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", request.responseFormat ?? "json");
    params.set("bbox", normalizeBBox(request.bbox));
    params.set("size", normalizeSize(request.size));
    if (request.format !== undefined) {
      params.set("format", request.format);
    }
    if (request.dpi !== undefined) {
      params.set("dpi", String(request.dpi));
    }
    if (request.transparent !== undefined) {
      params.set("transparent", String(request.transparent));
    }
    if (request.layers !== undefined) {
      params.set("layers", request.layers);
    }
    if (request.bboxSr !== undefined) {
      params.set("bboxSR", String(request.bboxSr));
    }
    if (request.imageSr !== undefined) {
      params.set("imageSR", String(request.imageSr));
    }
    if (request.backgroundColor !== undefined) {
      params.set("backgroundColor", request.backgroundColor);
    }

    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/MapServer/export`;
    if (method === "GET") {
      return this.requestJson("GET", `${path}?${params.toString()}`);
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public async getMapLegend(request: MapLegendRequest): Promise<unknown> {
    const params = new URLSearchParams();
    params.set("f", request.responseFormat ?? "json");
    if (request.size !== undefined) {
      params.set("size", normalizeLegendSize(request.size));
    }
    if (request.dynamicLayers !== undefined) {
      params.set("dynamicLayers", request.dynamicLayers);
    }
    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/MapServer/legend`;
    return this.requestJson("GET", `${path}?${params.toString()}`);
  }

  public async identifyMap(request: MapIdentifyRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", request.responseFormat ?? "json");
    params.set("geometry", normalizeIdentifyGeometry(request.geometry));
    params.set("geometryType", request.geometryType ?? "esriGeometryPoint");
    params.set("mapExtent", normalizeMapExtent(request.mapExtent));
    params.set("imageDisplay", normalizeImageDisplay(request.imageDisplay));
    params.set("returnGeometry", String(request.returnGeometry ?? true));
    params.set("tolerance", String(request.tolerance ?? 3));

    if (request.sr !== undefined) {
      params.set("sr", String(request.sr));
    }
    if (request.layers !== undefined) {
      params.set("layers", request.layers);
    }
    if (request.maxAllowableOffset !== undefined) {
      params.set("maxAllowableOffset", String(request.maxAllowableOffset));
    }
    if (request.layerDefs !== undefined) {
      params.set("layerDefs", request.layerDefs);
    }
    if (request.dynamicLayers !== undefined) {
      params.set("dynamicLayers", request.dynamicLayers);
    }
    if (request.time !== undefined) {
      params.set("time", request.time);
    }
    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/MapServer/identify`;
    if (method === "GET") {
      return this.requestJson("GET", `${path}?${params.toString()}`);
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public async findMap(request: MapFindRequest): Promise<unknown> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", request.responseFormat ?? "json");
    params.set("searchText", request.searchText);
    params.set("contains", String(request.contains ?? true));
    if (request.searchFields !== undefined) {
      params.set("searchFields", normalizeSearchFields(request.searchFields));
    }
    if (request.layers !== undefined) {
      params.set("layers", request.layers);
    }
    if (request.sr !== undefined) {
      params.set("sr", String(request.sr));
    }
    if (request.layerDefs !== undefined) {
      params.set("layerDefs", request.layerDefs);
    }
    if (request.returnGeometry !== undefined) {
      params.set("returnGeometry", String(request.returnGeometry));
    }
    if (request.maxAllowableOffset !== undefined) {
      params.set("maxAllowableOffset", String(request.maxAllowableOffset));
    }
    if (request.dynamicLayers !== undefined) {
      params.set("dynamicLayers", request.dynamicLayers);
    }
    if (request.returnZ !== undefined) {
      params.set("returnZ", String(request.returnZ));
    }
    if (request.returnM !== undefined) {
      params.set("returnM", String(request.returnM));
    }
    if (request.gdbVersion !== undefined) {
      params.set("gdbVersion", request.gdbVersion);
    }
    if (request.time !== undefined) {
      params.set("time", request.time);
    }
    if (request.relationParam !== undefined) {
      params.set("relationParam", request.relationParam);
    }
    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/MapServer/find`;
    if (method === "GET") {
      return this.requestJson("GET", `${path}?${params.toString()}`);
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  private async requestJson(
    method: "GET" | "POST",
    path: string,
    init?: RequestInit,
  ): Promise<unknown> {
    let request: HonuaRequestContext = {
      url: resolveRequestUrl(this.baseUrl, path),
      path,
      method,
      init: {
        method,
        headers: mergeHeaders(
          this.defaultHeaders,
          { Accept: "application/json" },
          init?.headers,
        ),
        body: init?.body,
      },
    };

    request = await this.applyBeforeInterceptors(request);

    try {
      const response = await this.fetchFn(request.url, {
        ...request.init,
        method: request.method,
      });
      await this.applyAfterInterceptors({ request: cloneRequestContext(request), response });

      const body = await parseResponseBody(response);
      if (!response.ok) {
        throw this.toHttpError(response.status, body);
      }
      return body;
    } catch (error) {
      await this.applyErrorInterceptors({ request: cloneRequestContext(request), error });
      throw error;
    }
  }

  private async applyBeforeInterceptors(request: HonuaRequestContext): Promise<HonuaRequestContext> {
    let next = cloneRequestContext(request);
    for (const interceptor of this.interceptors) {
      const mutation = await interceptor.before?.(cloneRequestContext(next));
      if (!mutation) {
        continue;
      }
      next = applyRequestMutation(next, mutation);
    }
    return next;
  }

  private async applyAfterInterceptors(context: HonuaResponseContext): Promise<void> {
    for (const interceptor of this.interceptors) {
      await interceptor.after?.(context);
    }
  }

  private async applyErrorInterceptors(context: HonuaErrorContext): Promise<void> {
    for (const interceptor of this.interceptors) {
      await interceptor.error?.(context);
    }
  }

  private toHttpError(statusCode: number, body: unknown): HonuaHttpError {
    const fallback = "Request failed";
    if (isObject(body)) {
      const error = body.error;
      if (isObject(error) && typeof error.message === "string") {
        return new HonuaHttpError(statusCode, error.message, body);
      }
      if (typeof body.message === "string") {
        return new HonuaHttpError(statusCode, body.message, body);
      }
      if (typeof body.detail === "string") {
        return new HonuaHttpError(statusCode, body.detail, body);
      }
    }

    return new HonuaHttpError(statusCode, fallback, body);
  }
}

async function parseResponseBody(response: Response): Promise<unknown> {
  const text = await response.text();
  if (!text) {
    return {};
  }
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return { raw: text };
  }
}

function isObject(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}

function applyRequestMutation(
  request: HonuaRequestContext,
  mutation: HonuaRequestMutation,
): HonuaRequestContext {
  const nextInit =
    mutation.init === undefined
      ? request.init
      : {
          ...request.init,
          ...mutation.init,
          headers: mergeHeaders(request.init.headers, mutation.init.headers),
        };

  return {
    url: mutation.url ?? request.url,
    path: request.path,
    method: mutation.method ?? request.method,
    init: {
      ...nextInit,
      method: mutation.method ?? request.method,
    },
  };
}

function cloneRequestContext(request: HonuaRequestContext): HonuaRequestContext {
  return {
    ...request,
    init: {
      ...request.init,
      headers: cloneHeadersInit(request.init.headers),
    },
  };
}

function cloneHeadersInit(headers: HeadersInit | undefined): HeadersInit {
  return mergeHeaders(headers);
}

function mergeHeaders(...headersList: Array<HeadersInit | undefined>): Record<string, string> {
  const merged: Record<string, string> = {};
  for (const headers of headersList) {
    if (!headers) {
      continue;
    }

    if (headers instanceof Headers) {
      for (const [key, value] of headers.entries()) {
        merged[key] = value;
      }
      continue;
    }

    if (Array.isArray(headers)) {
      for (const [key, value] of headers) {
        merged[key] = value;
      }
      continue;
    }

    for (const [key, value] of Object.entries(headers)) {
      if (value === undefined) {
        continue;
      }
      merged[key] = String(value);
    }
  }
  return merged;
}
