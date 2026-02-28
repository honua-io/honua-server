export interface ParsedFeatureLayerUrl {
  baseUrl: string;
  serviceId: string;
  layerId: number;
}

export interface ParsedMapServiceUrl {
  baseUrl: string;
  serviceId: string;
}

const ABSOLUTE_URL_RE = /^[A-Za-z][A-Za-z\d+.-]*:\/\//;
const PROTOCOL_RELATIVE_URL_RE = /^\/\//;
const RELATIVE_URL_BASE = "https://honua.invalid";
const FEATURE_LAYER_PATH_RE =
  /^(?<prefix>.*)\/rest\/services\/(?<serviceId>[^/]+)\/FeatureServer\/(?<layerId>\d+)\/?$/;
const MAP_SERVICE_PATH_RE =
  /^(?<prefix>.*)\/rest\/services\/(?<serviceId>[^/]+)\/MapServer(?:\/\d+)?\/?$/;

export function parseFeatureLayerUrl(url: string): ParsedFeatureLayerUrl {
  const { parsed, absolute } = parseServiceUrl(url);
  const match = parsed.pathname.match(FEATURE_LAYER_PATH_RE);
  if (!match || !match.groups) {
    throw new Error("Invalid FeatureLayer URL. Expected .../rest/services/{serviceId}/FeatureServer/{layerId}");
  }

  const serviceId = decodeURIComponent(match.groups.serviceId);
  const layerId = Number.parseInt(match.groups.layerId, 10);
  if (Number.isNaN(layerId)) {
    throw new Error("FeatureLayer URL contains an invalid numeric layerId.");
  }

  const prefix = match.groups.prefix || "";
  const baseUrl = absolute
    ? `${parsed.protocol}//${parsed.host}${prefix}`.replace(/\/+$/, "")
    : prefix.replace(/\/+$/, "");
  return { baseUrl, serviceId, layerId };
}

export function parseMapServiceUrl(url: string): ParsedMapServiceUrl {
  const { parsed, absolute } = parseServiceUrl(url);
  const match = parsed.pathname.match(MAP_SERVICE_PATH_RE);
  if (!match || !match.groups) {
    throw new Error("Invalid MapServer URL. Expected .../rest/services/{serviceId}/MapServer");
  }

  const serviceId = decodeURIComponent(match.groups.serviceId);
  const prefix = match.groups.prefix || "";
  const baseUrl = absolute
    ? `${parsed.protocol}//${parsed.host}${prefix}`.replace(/\/+$/, "")
    : prefix.replace(/\/+$/, "");
  return { baseUrl, serviceId };
}

function parseServiceUrl(url: string): { parsed: URL; absolute: boolean } {
  if (ABSOLUTE_URL_RE.test(url)) {
    return {
      parsed: new URL(url),
      absolute: true,
    };
  }

  if (PROTOCOL_RELATIVE_URL_RE.test(url)) {
    return {
      parsed: new URL(`https:${url}`),
      absolute: true,
    };
  }

  const normalized = url.startsWith("/") ? url : `/${url}`;
  return {
    parsed: new URL(normalized, RELATIVE_URL_BASE),
    absolute: false,
  };
}
