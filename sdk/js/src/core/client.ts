import { HonuaHttpError } from "./errors.js";
import type {
  ApplyEditsRequest,
  HonuaClientOptions,
  QueryFeaturesRequest,
  QueryMethod,
} from "./types.js";

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, "");
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

export class HonuaClient {
  private readonly baseUrl: string;
  private readonly fetchFn: typeof fetch;
  private readonly defaultHeaders: HeadersInit;

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
  }

  public async listServices(format: "json" | "pjson" = "json"): Promise<unknown> {
    const query = new URLSearchParams({ f: format });
    return this.requestJson("GET", `/rest/services?${query.toString()}`);
  }

  public async getLayerMetadata(serviceId: string, layerId: number): Promise<unknown> {
    const query = new URLSearchParams({ f: "json" });
    return this.requestJson(
      "GET",
      `/rest/services/${encodeURIComponent(serviceId)}/FeatureServer/${layerId}?${query.toString()}`,
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

  private async requestJson(
    method: "GET" | "POST",
    path: string,
    init?: RequestInit,
  ): Promise<unknown> {
    const response = await this.fetchFn(`${this.baseUrl}${path}`, {
      method,
      headers: {
        ...this.defaultHeaders,
        Accept: "application/json",
        ...(init?.headers ?? {}),
      },
      body: init?.body,
    });

    const body = await parseResponseBody(response);
    if (!response.ok) {
      throw this.toHttpError(response.status, body);
    }
    return body;
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
