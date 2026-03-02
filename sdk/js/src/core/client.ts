import type { Client } from "@connectrpc/connect";
import type { FeatureService } from "../gen/honua/v1/feature_service_pb.js";
import { HonuaAbortError, HonuaHttpError, HonuaNetworkError, HonuaTimeoutError } from "./errors.js";
import { decodePbfQueryResponse, isPbfResponse } from "./pbf-decoder.js";
import { HonuaFeatureLayer, HonuaMapLayer, HonuaMapService, HonuaOgcFeatures, HonuaService } from "./surfaces.js";
import type {
  ApplyEditsRequest,
  ExportMapRequest,
  HonuaApplyEditsResponse,
  HonuaClientOptions,
  HonuaErrorContext,
  HonuaExportMapResponse,
  HonuaFeature,
  HonuaFindResponse,
  HonuaIdentifyResponse,
  HonuaLayerMetadata,
  HonuaLegendResponse,
  HonuaOgcCollectionMetadata,
  HonuaOgcCollectionsResponse,
  HonuaOgcConformanceResponse,
  HonuaOgcFeatureCollectionResponse,
  HonuaOgcFeatureResponse,
  HonuaOgcLandingResponse,
  HonuaOgcQueryablesResponse,
  HonuaQueryResponse,
  HonuaRawRequest,
  HonuaRelatedRecordsResponse,
  HonuaRequestContext,
  HonuaRequestInterceptor,
  HonuaRequestMutation,
  HonuaResponseContext,
  HonuaRetryOptions,
  HonuaServiceMetadata,
  HonuaServicesResponse,
  HonuaTransport,
  MapFindRequest,
  MapIdentifyRequest,
  MapLayerQueryRequest,
  MapLegendRequest,
  MapRelatedRecordsRequest,
  OgcCollectionRequest,
  OgcCreateItemRequest,
  OgcDeleteItemRequest,
  OgcItemRequest,
  OgcItemsRequest,
  OgcMetadataRequest,
  OgcPatchItemRequest,
  OgcReplaceItemRequest,
  QueryFeaturesRequest,
  QueryMethod,
  QueryRelatedRecordsRequest,
} from "./types.js";

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
    if (!baseUrl.startsWith("http://") && !baseUrl.startsWith("https://")) {
      throw new Error("Absolute request URLs are not allowed when baseUrl is relative.");
    }
    const baseOrigin = new URL(baseUrl).origin;
    const requestUrl = new URL(path);
    if (requestUrl.origin !== baseOrigin) {
      throw new Error(`Cross-origin request URL is not allowed: ${path}`);
    }
    return requestUrl.toString();
  }
  return `${baseUrl}${path}`;
}

function normalizeOutFields(outFields: string | string[] | undefined): string {
  if (outFields === undefined) {
    return "*";
  }
  if (Array.isArray(outFields)) {
    return outFields.length > 0 ? outFields.join(",") : "";
  }
  return outFields;
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

interface NormalizedRetryOptions {
  maxRetries: number;
  baseDelayMs: number;
  maxDelayMs: number;
  retryStatuses: ReadonlySet<number>;
}

const DEFAULT_RETRY_STATUSES: ReadonlySet<number> = new Set([429, 502, 503, 504]);

export class HonuaClient {
  private readonly baseUrl: string;
  private readonly fetchFn: typeof fetch;
  private readonly defaultHeaders: HeadersInit;
  private readonly interceptors: readonly HonuaRequestInterceptor[];
  private readonly timeoutMs: number | undefined;
  private readonly retryOptions: NormalizedRetryOptions | undefined;
  private readonly preferBinary: boolean;
  private readonly transport: HonuaTransport;
  private connectClient: Client<typeof FeatureService> | undefined;

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
    this.timeoutMs = normalizeTimeoutMs(options.timeoutMs);
    this.retryOptions = normalizeRetryOptions(options.retry);
    this.preferBinary = options.preferBinary === true;
    this.transport = options.transport ?? "rest";

    if (this.transport === "grpc-web") {
      this.initConnectClient();
    }
  }

  private initConnectClient(): void {
    // Dynamic imports are used to avoid bundling Connect dependencies
    // when only the REST transport is used. The imports are resolved
    // at module level since they are static ESM imports.
    import("@connectrpc/connect").then(({ createClient }) =>
      import("@connectrpc/connect-web").then(({ createGrpcWebTransport }) =>
        import("../gen/honua/v1/feature_service_pb.js").then(({ FeatureService }) => {
          const transport = createGrpcWebTransport({
            baseUrl: this.baseUrl,
            fetch: this.fetchFn,
          });
          this.connectClient = createClient(FeatureService, transport);
        }),
      ),
    );
  }

  private async ensureConnectClient(): Promise<Client<typeof FeatureService>> {
    if (this.connectClient) {
      return this.connectClient;
    }
    // If initConnectClient hasn't resolved yet, wait for it
    const { createClient } = await import("@connectrpc/connect");
    const { createGrpcWebTransport } = await import("@connectrpc/connect-web");
    const { FeatureService } = await import("../gen/honua/v1/feature_service_pb.js");
    const transport = createGrpcWebTransport({
      baseUrl: this.baseUrl,
      fetch: this.fetchFn,
    });
    this.connectClient = createClient(FeatureService, transport);
    return this.connectClient;
  }

  private static async loadGrpcAdapter() {
    return import("./grpc-adapter.js");
  }

  public get isGrpcWeb(): boolean {
    return this.transport === "grpc-web";
  }

  public async *queryFeaturesStream(request: QueryFeaturesRequest): AsyncGenerator<HonuaFeature[], void, undefined> {
    const client = await this.ensureConnectClient();
    const grpcAdapter = await HonuaClient.loadGrpcAdapter();
    const protoRequest = grpcAdapter.toProtoQueryRequest(request);
    yield* grpcAdapter.streamProtoPages(client.queryFeaturesStream(protoRequest));
  }

  public service(serviceId: string): HonuaService {
    return new HonuaService({
      client: this,
      serviceId,
    });
  }

  public featureLayer<T = Record<string, unknown>>(serviceId: string, layerId: number): HonuaFeatureLayer<T> {
    return new HonuaFeatureLayer<T>({
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

  public mapLayer(serviceId: string, layerId: number): HonuaMapLayer {
    return new HonuaMapLayer({
      client: this,
      serviceId,
      layerId,
    });
  }

  public ogcFeatures(): HonuaOgcFeatures {
    return new HonuaOgcFeatures({
      client: this,
    });
  }

  public async listServices(format: "json" | "pjson" = "json"): Promise<HonuaServicesResponse> {
    const query = new URLSearchParams({ f: format });
    return this.requestJson("GET", `/rest/services?${query.toString()}`) as Promise<HonuaServicesResponse>;
  }

  public async request<T = unknown>(request: HonuaRawRequest): Promise<T> {
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
    return this.requestJson(
      method,
      pathWithQuery,
      {
        headers: request.headers,
        body: request.body,
      },
      request.signal,
    ) as Promise<T>;
  }

  public async getLayerMetadata(serviceId: string, layerId: number): Promise<HonuaLayerMetadata> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/FeatureServer/${layerId}?${query.toString()}`,
    ) as Promise<HonuaLayerMetadata>;
  }

  public async getFeatureServiceMetadata(serviceId: string): Promise<HonuaServiceMetadata> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/FeatureServer?${query.toString()}`,
    ) as Promise<HonuaServiceMetadata>;
  }

  public async getOgcFeaturesLanding(request: OgcMetadataRequest = {}): Promise<HonuaOgcLandingResponse> {
    const params = createOgcMetadataParams(request);
    return this.requestJson("GET", `/ogc/features?${params.toString()}`) as Promise<HonuaOgcLandingResponse>;
  }

  public async getOgcFeaturesConformance(request: OgcMetadataRequest = {}): Promise<HonuaOgcConformanceResponse> {
    const params = createOgcMetadataParams(request);
    return this.requestJson(
      "GET",
      `/ogc/features/conformance?${params.toString()}`,
    ) as Promise<HonuaOgcConformanceResponse>;
  }

  public async listOgcCollections(request: OgcMetadataRequest = {}): Promise<HonuaOgcCollectionsResponse> {
    const params = createOgcMetadataParams(request);
    return this.requestJson(
      "GET",
      `/ogc/features/collections?${params.toString()}`,
    ) as Promise<HonuaOgcCollectionsResponse>;
  }

  public async getOgcCollection(request: OgcCollectionRequest): Promise<HonuaOgcCollectionMetadata> {
    const params = createOgcMetadataParams(request);
    const path = `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}`;
    return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaOgcCollectionMetadata>;
  }

  public async getOgcQueryables(request: OgcCollectionRequest): Promise<HonuaOgcQueryablesResponse> {
    const params = createOgcMetadataParams(request);
    const path = `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}/queryables`;
    return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaOgcQueryablesResponse>;
  }

  public async listOgcItems(request: OgcItemsRequest): Promise<HonuaOgcFeatureCollectionResponse> {
    const params = createOgcMetadataParams(request);
    if (request.limit !== undefined) {
      params.set("limit", String(request.limit));
    }
    if (request.offset !== undefined) {
      params.set("offset", String(request.offset));
    }
    if (request.bbox !== undefined) {
      params.set("bbox", request.bbox);
    }
    if (request.datetime !== undefined) {
      params.set("datetime", request.datetime);
    }
    if (request.filter !== undefined) {
      params.set("filter", request.filter);
    }
    if (request.ids !== undefined) {
      params.set("ids", normalizeCsv(request.ids));
    }
    if (request.properties !== undefined) {
      params.set("properties", normalizeCsv(request.properties));
    }
    if (request.sortby !== undefined) {
      params.set("sortby", request.sortby);
    }
    if (request.crs !== undefined) {
      params.set("crs", request.crs);
    }
    const path = `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}/items`;
    return this.requestJson(
      "GET",
      `${path}?${params.toString()}`,
      undefined,
      request.signal,
    ) as Promise<HonuaOgcFeatureCollectionResponse>;
  }

  public async getOgcItem(request: OgcItemRequest): Promise<HonuaOgcFeatureResponse> {
    const params = createOgcMetadataParams(request);
    if (request.crs !== undefined) {
      params.set("crs", request.crs);
    }
    const path =
      `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}` +
      `/items/${encodeURIComponent(String(request.featureId))}`;
    return this.requestJson(
      "GET",
      `${path}?${params.toString()}`,
      undefined,
      request.signal,
    ) as Promise<HonuaOgcFeatureResponse>;
  }

  public async createOgcItem(request: OgcCreateItemRequest): Promise<HonuaOgcFeatureResponse> {
    const params = createOgcMetadataParams(request);
    const path = `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}/items`;
    return this.requestJson("POST", `${path}?${params.toString()}`, {
      headers: mergeHeaders({ "Content-Type": "application/geo+json" }, request.headers),
      body: JSON.stringify(request.feature),
    }) as Promise<HonuaOgcFeatureResponse>;
  }

  public async replaceOgcItem(request: OgcReplaceItemRequest): Promise<HonuaOgcFeatureResponse> {
    const params = createOgcMetadataParams(request);
    if (request.crs !== undefined) {
      params.set("crs", request.crs);
    }
    const path =
      `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}` +
      `/items/${encodeURIComponent(String(request.featureId))}`;
    return this.requestJson("PUT", `${path}?${params.toString()}`, {
      headers: mergeHeaders({ "Content-Type": "application/geo+json" }, request.headers),
      body: JSON.stringify(request.feature),
    }) as Promise<HonuaOgcFeatureResponse>;
  }

  public async patchOgcItem(request: OgcPatchItemRequest): Promise<HonuaOgcFeatureResponse> {
    const params = createOgcMetadataParams(request);
    if (request.crs !== undefined) {
      params.set("crs", request.crs);
    }
    const path =
      `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}` +
      `/items/${encodeURIComponent(String(request.featureId))}`;
    return this.requestJson("PATCH", `${path}?${params.toString()}`, {
      headers: mergeHeaders({ "Content-Type": "application/merge-patch+json" }, request.headers),
      body: JSON.stringify(request.patch),
    }) as Promise<HonuaOgcFeatureResponse>;
  }

  public async deleteOgcItem(request: OgcDeleteItemRequest): Promise<void> {
    const params = createOgcMetadataParams(request);
    if (request.crs !== undefined) {
      params.set("crs", request.crs);
    }
    const path =
      `/ogc/features/collections/${encodeURIComponent(String(request.collectionId))}` +
      `/items/${encodeURIComponent(String(request.featureId))}`;
    await this.requestJson("DELETE", `${path}?${params.toString()}`);
  }

  public async getMapServiceMetadata(serviceId: string): Promise<HonuaServiceMetadata> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/MapServer?${query.toString()}`,
    ) as Promise<HonuaServiceMetadata>;
  }

  public async queryFeatures(request: QueryFeaturesRequest): Promise<HonuaQueryResponse> {
    if (this.transport === "grpc-web") {
      const client = await this.ensureConnectClient();
      const grpcAdapter = await HonuaClient.loadGrpcAdapter();
      const protoRequest = grpcAdapter.toProtoQueryRequest(request);
      try {
        const response = await client.queryFeatures(protoRequest);
        return grpcAdapter.fromProtoQueryResponse(response) as HonuaQueryResponse;
      } catch (error) {
        throw grpcAdapter.wrapConnectError(error);
      }
    }

    const method: QueryMethod = request.method ?? "GET";
    const usePbf = this.preferBinary && method === "GET";
    const params = new URLSearchParams();
    params.set("f", usePbf ? "pbf" : "json");
    params.set("where", request.where ?? "1=1");
    params.set("outFields", normalizeOutFields(request.outFields));
    params.set("returnGeometry", String(request.returnGeometry ?? true));

    serializeQueryParams(params, request);

    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/FeatureServer/${request.layerId}/query`;

    if (usePbf) {
      return this.requestBinaryWithJsonFallback(
        "GET",
        `${path}?${params.toString()}`,
        params,
        request.signal,
      ) as Promise<HonuaQueryResponse>;
    }

    if (method === "GET") {
      return this.requestJson(
        "GET",
        `${path}?${params.toString()}`,
        undefined,
        request.signal,
      ) as Promise<HonuaQueryResponse>;
    }

    return this.requestJson(
      "POST",
      path,
      {
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: params.toString(),
      },
      request.signal,
    ) as Promise<HonuaQueryResponse>;
  }

  public async queryMapLayer(request: MapLayerQueryRequest): Promise<HonuaQueryResponse> {
    const method: QueryMethod = request.method ?? "GET";
    const params = new URLSearchParams();
    params.set("f", "json");
    params.set("where", request.where ?? "1=1");
    params.set("outFields", normalizeOutFields(request.outFields));
    params.set("returnGeometry", String(request.returnGeometry ?? true));

    serializeQueryParams(params, request);

    if (request.extraParams) {
      for (const [key, value] of Object.entries(request.extraParams)) {
        params.set(key, String(value));
      }
    }

    const path = `/rest/services/${encodeURIComponent(request.serviceId)}/MapServer/${request.layerId}/query`;
    if (method === "GET") {
      return this.requestJson(
        "GET",
        `${path}?${params.toString()}`,
        undefined,
        request.signal,
      ) as Promise<HonuaQueryResponse>;
    }

    return this.requestJson(
      "POST",
      path,
      {
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: params.toString(),
      },
      request.signal,
    ) as Promise<HonuaQueryResponse>;
  }

  public async applyEdits(request: ApplyEditsRequest): Promise<HonuaApplyEditsResponse> {
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
    }) as Promise<HonuaApplyEditsResponse>;
  }

  public async queryRelatedRecords(request: QueryRelatedRecordsRequest): Promise<HonuaRelatedRecordsResponse> {
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
      return this.requestJson(
        "GET",
        `${path}?${params.toString()}`,
        undefined,
        request.signal,
      ) as Promise<HonuaRelatedRecordsResponse>;
    }

    return this.requestJson(
      "POST",
      path,
      {
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: params.toString(),
      },
      request.signal,
    ) as Promise<HonuaRelatedRecordsResponse>;
  }

  public async queryMapRelatedRecords(request: MapRelatedRecordsRequest): Promise<HonuaRelatedRecordsResponse> {
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
      `/rest/services/${encodeURIComponent(request.serviceId)}` + `/MapServer/${request.layerId}/queryRelatedRecords`;
    if (method === "GET") {
      return this.requestJson(
        "GET",
        `${path}?${params.toString()}`,
        undefined,
        request.signal,
      ) as Promise<HonuaRelatedRecordsResponse>;
    }

    return this.requestJson(
      "POST",
      path,
      {
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
        },
        body: params.toString(),
      },
      request.signal,
    ) as Promise<HonuaRelatedRecordsResponse>;
  }

  public async exportMap(request: ExportMapRequest): Promise<HonuaExportMapResponse> {
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
      return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaExportMapResponse>;
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    }) as Promise<HonuaExportMapResponse>;
  }

  public async getMapLegend(request: MapLegendRequest): Promise<HonuaLegendResponse> {
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
    return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaLegendResponse>;
  }

  public async identifyMap(request: MapIdentifyRequest): Promise<HonuaIdentifyResponse> {
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
      return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaIdentifyResponse>;
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    }) as Promise<HonuaIdentifyResponse>;
  }

  public async findMap(request: MapFindRequest): Promise<HonuaFindResponse> {
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
      return this.requestJson("GET", `${path}?${params.toString()}`) as Promise<HonuaFindResponse>;
    }

    return this.requestJson("POST", path, {
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    }) as Promise<HonuaFindResponse>;
  }

  private async requestJson(
    method: QueryMethod,
    path: string,
    init?: RequestInit,
    callerSignal?: AbortSignal,
  ): Promise<unknown> {
    let request: HonuaRequestContext = {
      url: resolveRequestUrl(this.baseUrl, path),
      path,
      method,
      init: {
        method,
        headers: mergeHeaders(this.defaultHeaders, { Accept: "application/json" }, init?.headers),
        body: init?.body,
      },
    };

    request = await this.applyBeforeInterceptors(request);

    for (let attempt = 0; ; attempt += 1) {
      let response: Response;
      const timeout = createTimeoutSignal(callerSignal ?? request.init.signal, this.timeoutMs);
      const startTime = performance.now();
      try {
        response = await this.fetchFn(request.url, {
          ...request.init,
          method: request.method,
          signal: timeout.signal,
        });
      } catch (error) {
        const durationMs = performance.now() - startTime;
        const normalizedError = timeout.didTimeout
          ? new HonuaTimeoutError(this.timeoutMs ?? 0)
          : normalizeNetworkError(error);
        if (this.shouldRetryRequest(attempt, undefined, normalizedError)) {
          await sleep(this.resolveRetryDelayMs(attempt));
          continue;
        }
        await this.applyErrorInterceptors({
          request: cloneRequestContext(request),
          error: normalizedError,
          durationMs,
        });
        throw normalizedError;
      } finally {
        timeout.dispose();
      }
      const durationMs = performance.now() - startTime;

      const body = await parseResponseBody(response.clone());
      if (!response.ok) {
        const httpError = this.toHttpError(response.status, body);
        if (this.shouldRetryRequest(attempt, response.status, httpError)) {
          await sleep(this.resolveRetryDelayMs(attempt, response));
          continue;
        }
        await this.applyErrorInterceptors({ request: cloneRequestContext(request), error: httpError, durationMs });
        throw httpError;
      }

      try {
        await this.applyAfterInterceptors(cloneRequestContext(request), response, durationMs);
      } catch (error) {
        await this.applyErrorInterceptors({ request: cloneRequestContext(request), error, durationMs });
        throw error;
      }

      return body;
    }
  }

  /**
   * Request a PBF binary response and decode it. Falls back to JSON on failure.
   */
  private async requestBinaryWithJsonFallback(
    method: QueryMethod,
    path: string,
    params: URLSearchParams,
    callerSignal?: AbortSignal,
  ): Promise<unknown> {
    let request: HonuaRequestContext = {
      url: resolveRequestUrl(this.baseUrl, path),
      path,
      method,
      init: {
        method,
        headers: mergeHeaders(this.defaultHeaders, { Accept: "application/x-protobuf, application/json;q=0.9" }),
      },
    };

    request = await this.applyBeforeInterceptors(request);

    for (let attempt = 0; ; attempt += 1) {
      let response: Response;
      const timeout = createTimeoutSignal(callerSignal ?? request.init.signal, this.timeoutMs);
      const startTime = performance.now();
      try {
        response = await this.fetchFn(request.url, {
          ...request.init,
          method: request.method,
          signal: timeout.signal,
        });
      } catch (error) {
        const durationMs = performance.now() - startTime;
        const normalizedError = timeout.didTimeout
          ? new HonuaTimeoutError(this.timeoutMs ?? 0)
          : normalizeNetworkError(error);
        if (this.shouldRetryRequest(attempt, undefined, normalizedError)) {
          await sleep(this.resolveRetryDelayMs(attempt));
          continue;
        }
        await this.applyErrorInterceptors({
          request: cloneRequestContext(request),
          error: normalizedError,
          durationMs,
        });
        throw normalizedError;
      } finally {
        timeout.dispose();
      }
      const durationMs = performance.now() - startTime;

      if (!response.ok) {
        const body = await parseResponseBody(response.clone());
        const httpError = this.toHttpError(response.status, body);
        if (this.shouldRetryRequest(attempt, response.status, httpError)) {
          await sleep(this.resolveRetryDelayMs(attempt, response));
          continue;
        }
        await this.applyErrorInterceptors({ request: cloneRequestContext(request), error: httpError, durationMs });
        throw httpError;
      }

      try {
        await this.applyAfterInterceptors(cloneRequestContext(request), response, durationMs);
      } catch (error) {
        await this.applyErrorInterceptors({ request: cloneRequestContext(request), error, durationMs });
        throw error;
      }

      // If server returned PBF, decode it
      if (isPbfResponse(response)) {
        try {
          const buffer = await response.arrayBuffer();
          return decodePbfQueryResponse(buffer);
        } catch {
          // PBF decode failed — fall back to JSON request
          params.set("f", "json");
          const jsonPath = `${path.replace(/\?.*$/, "")}?${params.toString()}`;
          return this.requestJson("GET", jsonPath);
        }
      }

      // Server returned JSON despite PBF request (e.g. error or unsupported)
      return parseResponseBody(response);
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

  private async applyAfterInterceptors(
    request: HonuaRequestContext,
    response: Response,
    durationMs: number,
  ): Promise<void> {
    for (const interceptor of this.interceptors) {
      const context: HonuaResponseContext = {
        request: cloneRequestContext(request),
        response: response.clone(),
        durationMs,
      };
      await interceptor.after?.(context);
    }
  }

  private async applyErrorInterceptors(context: HonuaErrorContext): Promise<void> {
    for (const interceptor of this.interceptors) {
      try {
        await interceptor.error?.(context);
      } catch {
        // Preserve original request failure; interceptor failures should not mask it.
      }
    }
  }

  private shouldRetryRequest(attempt: number, statusCode: number | undefined, error: unknown): boolean {
    if (!this.retryOptions || attempt >= this.retryOptions.maxRetries) {
      return false;
    }

    if (statusCode !== undefined) {
      return this.retryOptions.retryStatuses.has(statusCode);
    }

    return error instanceof Error;
  }

  private resolveRetryDelayMs(attempt: number, response?: Response): number {
    const retryAfterMs = response ? parseRetryAfterMs(response) : undefined;
    if (retryAfterMs !== undefined) {
      return retryAfterMs;
    }
    if (!this.retryOptions) {
      return 0;
    }
    const exponentialDelay = this.retryOptions.baseDelayMs * 2 ** attempt;
    const cappedDelay = Math.min(this.retryOptions.maxDelayMs, exponentialDelay);
    return cappedDelay * (0.5 + Math.random() * 0.5);
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

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function applyRequestMutation(request: HonuaRequestContext, mutation: HonuaRequestMutation): HonuaRequestContext {
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
      if (value === undefined || value === null) {
        continue;
      }
      merged[key] = String(value);
    }
  }
  return merged;
}

function normalizeTimeoutMs(timeoutMs: number | undefined): number | undefined {
  if (typeof timeoutMs !== "number" || !Number.isFinite(timeoutMs)) {
    return undefined;
  }
  return Math.max(1, Math.trunc(timeoutMs));
}

function normalizeRetryOptions(options: HonuaRetryOptions | undefined): NormalizedRetryOptions | undefined {
  if (!options) {
    return undefined;
  }

  const maxRetries =
    typeof options.maxRetries === "number" && Number.isFinite(options.maxRetries)
      ? Math.max(0, Math.trunc(options.maxRetries))
      : 0;
  if (maxRetries < 1) {
    return undefined;
  }

  const baseDelayMs =
    typeof options.baseDelayMs === "number" && Number.isFinite(options.baseDelayMs)
      ? Math.max(1, Math.trunc(options.baseDelayMs))
      : 100;
  const maxDelayMs =
    typeof options.maxDelayMs === "number" && Number.isFinite(options.maxDelayMs)
      ? Math.max(baseDelayMs, Math.trunc(options.maxDelayMs))
      : 2_000;
  const retryStatuses = new Set<number>(
    (options.retryStatuses ?? Array.from(DEFAULT_RETRY_STATUSES))
      .map((status) => Math.trunc(status))
      .filter((status) => Number.isFinite(status) && status >= 100 && status <= 599),
  );
  if (retryStatuses.size === 0) {
    for (const status of DEFAULT_RETRY_STATUSES) {
      retryStatuses.add(status);
    }
  }

  return {
    maxRetries,
    baseDelayMs,
    maxDelayMs,
    retryStatuses,
  };
}

function parseRetryAfterMs(response: Response): number | undefined {
  const value = response.headers.get("retry-after");
  if (!value) {
    return undefined;
  }

  const seconds = Number.parseInt(value, 10);
  if (Number.isFinite(seconds) && seconds >= 0) {
    return seconds * 1_000;
  }

  const targetTime = Date.parse(value);
  if (!Number.isFinite(targetTime)) {
    return undefined;
  }
  return Math.max(0, targetTime - Date.now());
}

async function sleep(ms: number): Promise<void> {
  if (ms <= 0) {
    return;
  }
  await new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

function normalizeNetworkError(error: unknown): Error {
  if (error instanceof Error && error.name === "AbortError") {
    return new HonuaAbortError();
  }
  if (error instanceof Error) {
    return new HonuaNetworkError(error.message, error);
  }
  return new HonuaNetworkError(String(error), error);
}

function createTimeoutSignal(
  existingSignal: AbortSignal | null | undefined,
  timeoutMs: number | undefined,
): {
  signal: AbortSignal | undefined;
  didTimeout: boolean;
  dispose(): void;
} {
  if (timeoutMs === undefined) {
    return {
      signal: existingSignal ?? undefined,
      didTimeout: false,
      dispose: () => undefined,
    };
  }

  const controller = new AbortController();
  let didTimeout = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let onAbort: (() => void) | undefined;

  timer = setTimeout(() => {
    didTimeout = true;
    controller.abort();
  }, timeoutMs);

  if (existingSignal) {
    if (existingSignal.aborted) {
      controller.abort();
    } else {
      onAbort = () => {
        controller.abort();
      };
      existingSignal.addEventListener("abort", onAbort, { once: true });
    }
  }

  return {
    signal: controller.signal,
    get didTimeout() {
      return didTimeout;
    },
    dispose: () => {
      if (timer) {
        clearTimeout(timer);
        timer = undefined;
      }
      if (existingSignal && onAbort) {
        existingSignal.removeEventListener("abort", onAbort);
        onAbort = undefined;
      }
    },
  };
}

function createOgcMetadataParams(request: OgcMetadataRequest): URLSearchParams {
  const params = new URLSearchParams();
  params.set("f", request.responseFormat ?? "json");
  if (request.extraParams) {
    for (const [key, value] of Object.entries(request.extraParams)) {
      params.set(key, String(value));
    }
  }
  return params;
}

function serializeQueryParams(params: URLSearchParams, request: QueryFeaturesRequest | MapLayerQueryRequest): void {
  if (request.orderByFields !== undefined) {
    params.set("orderByFields", request.orderByFields);
  }
  if (request.objectIds !== undefined) {
    params.set("objectIds", Array.isArray(request.objectIds) ? request.objectIds.join(",") : String(request.objectIds));
  }
  if (request.geometry !== undefined) {
    params.set(
      "geometry",
      typeof request.geometry === "object" && request.geometry !== null
        ? JSON.stringify(request.geometry)
        : String(request.geometry),
    );
  }
  if (request.geometryType !== undefined) {
    params.set("geometryType", request.geometryType);
  }
  if (request.spatialRel !== undefined) {
    params.set("spatialRel", request.spatialRel);
  }
  if (request.returnDistinctValues !== undefined) {
    params.set("returnDistinctValues", String(request.returnDistinctValues));
  }
  if (request.returnCentroid !== undefined) {
    params.set("returnCentroid", String(request.returnCentroid));
  }
  if (request.groupByFieldsForStatistics !== undefined) {
    params.set("groupByFieldsForStatistics", request.groupByFieldsForStatistics);
  }
  if (request.outStatistics !== undefined) {
    params.set(
      "outStatistics",
      Array.isArray(request.outStatistics) ? JSON.stringify(request.outStatistics) : String(request.outStatistics),
    );
  }
  if (request.resultOffset !== undefined) {
    params.set("resultOffset", String(request.resultOffset));
  }
  if (request.resultRecordCount !== undefined) {
    params.set("resultRecordCount", String(request.resultRecordCount));
  }
}

function normalizeCsv(value: string | readonly (string | number)[]): string {
  if (typeof value === "string") {
    return value;
  }
  return Array.from(value).join(",");
}
