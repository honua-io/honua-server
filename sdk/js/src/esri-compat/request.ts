import type { HonuaRequestInterceptor, QueryMethod } from "../core/types.js";

export type EsriUrlPattern = string | RegExp;

export interface EsriRequestOptionsLike {
  method?: string;
  headers?: Record<string, string>;
  body?: BodyInit | null;
}

export interface EsriBeforeRequestParams {
  url: string;
  requestOptions: EsriRequestOptionsLike;
}

export interface EsriRequestInterceptorCompat {
  urls?: EsriUrlPattern | readonly EsriUrlPattern[];
  before?(params: EsriBeforeRequestParams): void | Promise<void>;
  after?(response: Response): void | Promise<void>;
  error?(error: unknown): void | Promise<void>;
}

export interface ArcGisTokenInterceptorOptions {
  getToken(): string | undefined | Promise<string | undefined>;
  applyTo?: EsriUrlPattern | readonly EsriUrlPattern[];
  mode?: "query" | "bearer";
  queryParamName?: string;
  headerName?: string;
  bearerPrefix?: string;
}

export function createEsriRequestInterceptors(
  interceptors: readonly EsriRequestInterceptorCompat[],
): HonuaRequestInterceptor[] {
  return interceptors.map((interceptor) => ({
    before: async (context) => {
      if (!matchesPattern(context.url, interceptor.urls)) {
        return undefined;
      }

      if (!interceptor.before) {
        return undefined;
      }

      const requestOptions: EsriRequestOptionsLike = {
        method: context.method,
        headers: headersToRecord(context.init.headers),
        body: context.init.body,
      };
      const params: EsriBeforeRequestParams = {
        url: context.url,
        requestOptions,
      };

      await interceptor.before(params);
      const method = normalizeMethod(params.requestOptions.method, context.method);
      return {
        url: params.url,
        method,
        init: {
          ...context.init,
          method,
          headers: params.requestOptions.headers,
          body: params.requestOptions.body,
        },
      };
    },
    after: async (context) => {
      if (!matchesPattern(context.request.url, interceptor.urls)) {
        return;
      }
      await interceptor.after?.(context.response);
    },
    error: async (context) => {
      if (!matchesPattern(context.request.url, interceptor.urls)) {
        return;
      }
      await interceptor.error?.(context.error);
    },
  }));
}

export function createArcGisTokenInterceptor(
  options: ArcGisTokenInterceptorOptions,
): HonuaRequestInterceptor {
  const mode = options.mode ?? "query";
  const queryParamName = options.queryParamName ?? "token";
  const headerName = options.headerName ?? "Authorization";
  const bearerPrefix = options.bearerPrefix ?? "Bearer";

  return {
    before: async (context) => {
      if (!matchesPattern(context.url, options.applyTo)) {
        return undefined;
      }

      const token = await options.getToken();
      if (!token) {
        return undefined;
      }

      if (mode === "query") {
        const url = new URL(context.url);
        url.searchParams.set(queryParamName, token);
        return { url: url.toString() };
      }

      const headers = headersToRecord(context.init.headers);
      headers[headerName] = `${bearerPrefix} ${token}`;
      return {
        init: {
          ...context.init,
          headers,
        },
      };
    },
  };
}

function normalizeMethod(method: string | undefined, fallback: QueryMethod): QueryMethod {
  if (typeof method !== "string") {
    return fallback;
  }

  const upper = method.toUpperCase();
  return upper === "POST" ? "POST" : "GET";
}

function matchesPattern(url: string, pattern: EsriUrlPattern | readonly EsriUrlPattern[] | undefined): boolean {
  if (!pattern) {
    return true;
  }

  const patterns = Array.isArray(pattern) ? pattern : [pattern];
  return patterns.some((candidate) =>
    typeof candidate === "string" ? url.includes(candidate) : candidate.test(url),
  );
}

function headersToRecord(headers: HeadersInit | undefined): Record<string, string> {
  if (!headers) {
    return {};
  }

  const record: Record<string, string> = {};
  if (headers instanceof Headers) {
    for (const [key, value] of headers.entries()) {
      record[key] = value;
    }
    return record;
  }

  if (Array.isArray(headers)) {
    for (const [key, value] of headers) {
      record[key] = value;
    }
    return record;
  }

  for (const [key, value] of Object.entries(headers)) {
    if (value === undefined) {
      continue;
    }
    record[key] = String(value);
  }

  return record;
}
