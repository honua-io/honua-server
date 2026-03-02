export type EsriRequestResponseTypeCompat = "json" | "text" | "blob" | "array-buffer";

export interface EsriRequestCompatOptions {
  method?: string;
  query?: Record<string, string | number | boolean>;
  headers?: Record<string, string>;
  body?: BodyInit | null;
  responseType?: EsriRequestResponseTypeCompat;
  signal?: AbortSignal;
}

export interface EsriRequestCompatResponse<TData = unknown> {
  data: TData;
  url: string;
  status: number;
  headers: Headers;
}

export async function esriRequest<TData = unknown>(
  url: string,
  options: EsriRequestCompatOptions = {},
): Promise<EsriRequestCompatResponse<TData>> {
  const finalUrl = appendQuery(url, options.query);
  const method = options.method?.toUpperCase() ?? "GET";
  const responseType = options.responseType ?? "json";

  const response = await fetch(finalUrl, {
    method,
    headers: options.headers,
    body: options.body ?? undefined,
    signal: options.signal,
  });

  if (!response.ok) {
    const detail = await readErrorDetail(response);
    throw new Error(`esriRequest failed (${response.status}): ${detail}`);
  }

  const data = (await parseResponseBody(response, responseType)) as TData;
  return {
    data,
    url: response.url,
    status: response.status,
    headers: response.headers,
  };
}

function appendQuery(urlText: string, query: Record<string, string | number | boolean> | undefined): string {
  if (!query || Object.keys(query).length === 0) {
    return urlText;
  }

  const hashIndex = urlText.indexOf("#");
  const hash = hashIndex >= 0 ? urlText.slice(hashIndex) : "";
  const withoutHash = hashIndex >= 0 ? urlText.slice(0, hashIndex) : urlText;

  const queryIndex = withoutHash.indexOf("?");
  const path = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash;
  const existingQuery = queryIndex >= 0 ? withoutHash.slice(queryIndex + 1) : "";
  const url = new URLSearchParams(existingQuery);
  for (const [key, value] of Object.entries(query)) {
    url.set(key, String(value));
  }
  const nextQuery = url.toString();
  const withQuery = nextQuery.length > 0 ? `${path}?${nextQuery}` : path;
  return `${withQuery}${hash}`;
}

async function parseResponseBody(response: Response, responseType: EsriRequestResponseTypeCompat): Promise<unknown> {
  switch (responseType) {
    case "text":
      return response.text();
    case "blob":
      return response.blob();
    case "array-buffer":
      return response.arrayBuffer();
    default:
      return response.json();
  }
}

async function readErrorDetail(response: Response): Promise<string> {
  try {
    const contentType = response.headers.get("content-type") ?? "";
    if (contentType.includes("application/json")) {
      const payload = await response.json();
      return JSON.stringify(payload);
    }
    const text = await response.text();
    return text || response.statusText;
  } catch {
    return response.statusText || "request failed";
  }
}
