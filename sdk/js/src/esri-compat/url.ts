export interface ParsedFeatureLayerUrl {
  baseUrl: string;
  serviceId: string;
  layerId: number;
}

const FEATURE_LAYER_PATH_RE =
  /^(?<prefix>.*)\/rest\/services\/(?<serviceId>[^/]+)\/FeatureServer\/(?<layerId>\d+)\/?$/;

export function parseFeatureLayerUrl(url: string): ParsedFeatureLayerUrl {
  const parsed = new URL(url);
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
  const baseUrl = `${parsed.protocol}//${parsed.host}${prefix}`.replace(/\/+$/, "");
  return { baseUrl, serviceId, layerId };
}
