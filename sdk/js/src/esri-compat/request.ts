import type { HonuaRequestContext, HonuaRequestInterceptor, HonuaRequestMutation, QueryMethod } from "../core/types.js";

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

export interface EsriRequestInterceptorHandle {
  remove(): void;
}

export function createEsriRequestInterceptors(
  interceptors: readonly EsriRequestInterceptorCompat[],
): HonuaRequestInterceptor[] {
  return interceptors.map((interceptor) => createHonuaInterceptorFromEsri(interceptor));
}

export class EsriRequestInterceptorRegistry {
  private readonly interceptorsInternal: EsriRequestInterceptorCompat[];

  public constructor(initialInterceptors: readonly EsriRequestInterceptorCompat[] = []) {
    this.interceptorsInternal = [...initialInterceptors];
  }

  public get interceptors(): readonly EsriRequestInterceptorCompat[] {
    return [...this.interceptorsInternal];
  }

  public add(interceptor: EsriRequestInterceptorCompat): EsriRequestInterceptorHandle {
    this.interceptorsInternal.push(interceptor);
    return {
      remove: () => {
        this.remove(interceptor);
      },
    };
  }

  public remove(interceptor: EsriRequestInterceptorCompat): boolean {
    const index = this.interceptorsInternal.indexOf(interceptor);
    if (index < 0) {
      return false;
    }
    this.interceptorsInternal.splice(index, 1);
    return true;
  }

  public clear(): void {
    this.interceptorsInternal.length = 0;
  }

  public toHonuaInterceptors(): HonuaRequestInterceptor[] {
    return createEsriRequestInterceptors(this.interceptorsInternal);
  }

  public asHonuaInterceptor(): HonuaRequestInterceptor {
    return {
      before: async (context) => {
        let currentContext = cloneRequestContext(context);
        let applied = false;
        for (const interceptor of this.interceptorsInternal) {
          if (!interceptor.before || !matchesPattern(currentContext.url, interceptor.urls)) {
            continue;
          }
          const params = toEsriBeforeRequestParams(currentContext);
          await interceptor.before(params);
          const mutation = fromEsriBeforeRequestParams(currentContext, params);
          currentContext = applyMutationToContext(currentContext, mutation);
          applied = true;
        }

        return applied
          ? {
              url: currentContext.url,
              method: currentContext.method,
              init: currentContext.init,
            }
          : undefined;
      },
      after: async (context) => {
        for (const interceptor of this.interceptorsInternal) {
          if (!interceptor.after || !matchesPattern(context.request.url, interceptor.urls)) {
            continue;
          }
          await interceptor.after(context.response);
        }
      },
      error: async (context) => {
        for (const interceptor of this.interceptorsInternal) {
          if (!interceptor.error || !matchesPattern(context.request.url, interceptor.urls)) {
            continue;
          }
          await interceptor.error(context.error);
        }
      },
    };
  }
}

export function createArcGisTokenInterceptor(options: ArcGisTokenInterceptorOptions): HonuaRequestInterceptor {
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
        return {
          url: setQueryParam(context.url, queryParamName, token),
        };
      }

      const headers = headersToRecord(context.init.headers);
      headers[headerName] = bearerPrefix.trim().length > 0 ? `${bearerPrefix} ${token}` : token;
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

function createHonuaInterceptorFromEsri(interceptor: EsriRequestInterceptorCompat): HonuaRequestInterceptor {
  return {
    before: async (context) => {
      if (!matchesPattern(context.url, interceptor.urls) || !interceptor.before) {
        return undefined;
      }
      const params = toEsriBeforeRequestParams(context);
      await interceptor.before(params);
      return fromEsriBeforeRequestParams(context, params);
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
  };
}

function toEsriBeforeRequestParams(context: HonuaRequestContext): EsriBeforeRequestParams {
  return {
    url: context.url,
    requestOptions: {
      method: context.method,
      headers: headersToRecord(context.init.headers),
      body: context.init.body,
    },
  };
}

function fromEsriBeforeRequestParams(
  context: HonuaRequestContext,
  params: EsriBeforeRequestParams,
): HonuaRequestMutation {
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
}

function applyMutationToContext(context: HonuaRequestContext, mutation: HonuaRequestMutation): HonuaRequestContext {
  const method = mutation.method ?? context.method;
  const nextInit =
    mutation.init === undefined
      ? context.init
      : {
          ...context.init,
          ...mutation.init,
          method,
        };

  return {
    ...context,
    url: mutation.url ?? context.url,
    method,
    init: nextInit,
  };
}

function cloneRequestContext(context: HonuaRequestContext): HonuaRequestContext {
  return {
    ...context,
    init: {
      ...context.init,
      headers: headersToRecord(context.init.headers),
    },
  };
}

function matchesPattern(url: string, pattern: EsriUrlPattern | readonly EsriUrlPattern[] | undefined): boolean {
  if (!pattern) {
    return true;
  }

  const patterns = Array.isArray(pattern) ? pattern : [pattern];
  return patterns.some((candidate) =>
    typeof candidate === "string" ? matchesStringPattern(url, candidate) : matchesRegExpPattern(url, candidate),
  );
}

function matchesStringPattern(url: string, pattern: string): boolean {
  if (!pattern) {
    return true;
  }

  if (url === pattern) {
    return true;
  }

  const escapedPattern = pattern.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const boundaryRegex = new RegExp(`${escapedPattern}(?=$|[/?#])`, "g");
  return boundaryRegex.test(url);
}

function matchesRegExpPattern(url: string, pattern: RegExp): boolean {
  if (!pattern.global && !pattern.sticky) {
    return pattern.test(url);
  }
  pattern.lastIndex = 0;
  const matched = pattern.test(url);
  pattern.lastIndex = 0;
  return matched;
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

function setQueryParam(urlText: string, key: string, value: string): string {
  const hashIndex = urlText.indexOf("#");
  const hash = hashIndex >= 0 ? urlText.slice(hashIndex) : "";
  const withoutHash = hashIndex >= 0 ? urlText.slice(0, hashIndex) : urlText;

  const queryIndex = withoutHash.indexOf("?");
  const path = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash;
  const existingQuery = queryIndex >= 0 ? withoutHash.slice(queryIndex + 1) : "";
  const params = new URLSearchParams(existingQuery);
  params.set(key, value);
  const nextQuery = params.toString();
  const withQuery = nextQuery.length > 0 ? `${path}?${nextQuery}` : path;
  return `${withQuery}${hash}`;
}
