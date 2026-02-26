export interface LayerReconciliationOptions {
  sourceBaseUrl: string;
  sourceServiceId: string;
  targetBaseUrl: string;
  targetServiceId: string;
  layerId: number;
  sampleSize?: number;
  fetchFn?: typeof fetch;
}

export interface ReconciliationCheckResult {
  check: "feature-count" | "geometry-validity" | "attribute-keys";
  passed: boolean;
  detail: string;
}

export interface LayerReconciliationReport {
  sourceBaseUrl: string;
  sourceServiceId: string;
  targetBaseUrl: string;
  targetServiceId: string;
  layerId: number;
  sampleSize: number;
  sourceFeatureCount: number;
  targetFeatureCount: number;
  countDelta: number;
  sourceGeometryValidityRatio: number;
  targetGeometryValidityRatio: number;
  sourceAttributeKeys: string[];
  targetAttributeKeys: string[];
  missingInTargetAttributeKeys: string[];
  extraInTargetAttributeKeys: string[];
  checks: ReconciliationCheckResult[];
  passed: boolean;
}

interface QueryFeaturesResponse {
  features?: unknown[];
  count?: number;
}

const DEFAULT_SAMPLE_SIZE = 100;

export async function runLayerReconciliation(
  options: LayerReconciliationOptions,
): Promise<LayerReconciliationReport> {
  const fetchFn = options.fetchFn ?? fetch;
  const sampleSize =
    typeof options.sampleSize === "number" && Number.isFinite(options.sampleSize)
      ? Math.max(1, Math.trunc(options.sampleSize))
      : DEFAULT_SAMPLE_SIZE;

  const [sourceCount, targetCount, sourceSample, targetSample] = await Promise.all([
    queryFeatureCount(fetchFn, options.sourceBaseUrl, options.sourceServiceId, options.layerId),
    queryFeatureCount(fetchFn, options.targetBaseUrl, options.targetServiceId, options.layerId),
    queryFeatureSample(fetchFn, options.sourceBaseUrl, options.sourceServiceId, options.layerId, sampleSize),
    queryFeatureSample(fetchFn, options.targetBaseUrl, options.targetServiceId, options.layerId, sampleSize),
  ]);

  const sourceFeatures = sourceSample.features ?? [];
  const targetFeatures = targetSample.features ?? [];

  const sourceGeometryValidityRatio = computeGeometryValidityRatio(sourceFeatures);
  const targetGeometryValidityRatio = computeGeometryValidityRatio(targetFeatures);
  const sourceAttributeKeys = collectAttributeKeys(sourceFeatures);
  const targetAttributeKeys = collectAttributeKeys(targetFeatures);
  const missingInTargetAttributeKeys = sourceAttributeKeys.filter((key) => !targetAttributeKeys.includes(key));
  const extraInTargetAttributeKeys = targetAttributeKeys.filter((key) => !sourceAttributeKeys.includes(key));
  const countDelta = targetCount - sourceCount;

  const checks: ReconciliationCheckResult[] = [
    {
      check: "feature-count",
      passed: sourceCount === targetCount,
      detail:
        sourceCount === targetCount
          ? `counts match (${sourceCount})`
          : `source=${sourceCount}, target=${targetCount}, delta=${countDelta}`,
    },
    {
      check: "geometry-validity",
      passed: targetGeometryValidityRatio >= 1,
      detail: `target valid geometry ratio=${targetGeometryValidityRatio.toFixed(3)}`,
    },
    {
      check: "attribute-keys",
      passed: missingInTargetAttributeKeys.length === 0,
      detail:
        missingInTargetAttributeKeys.length === 0
          ? "target covers source attribute keys"
          : `missing keys in target: ${missingInTargetAttributeKeys.join(", ")}`,
    },
  ];

  return {
    sourceBaseUrl: normalizeBaseUrl(options.sourceBaseUrl),
    sourceServiceId: options.sourceServiceId,
    targetBaseUrl: normalizeBaseUrl(options.targetBaseUrl),
    targetServiceId: options.targetServiceId,
    layerId: options.layerId,
    sampleSize,
    sourceFeatureCount: sourceCount,
    targetFeatureCount: targetCount,
    countDelta,
    sourceGeometryValidityRatio,
    targetGeometryValidityRatio,
    sourceAttributeKeys,
    targetAttributeKeys,
    missingInTargetAttributeKeys,
    extraInTargetAttributeKeys,
    checks,
    passed: checks.every((check) => check.passed),
  };
}

export function summarizeLayerReconciliation(report: LayerReconciliationReport): string {
  return [
    `sourceCount=${report.sourceFeatureCount}`,
    `targetCount=${report.targetFeatureCount}`,
    `countDelta=${report.countDelta}`,
    `sourceGeometryValid=${report.sourceGeometryValidityRatio.toFixed(3)}`,
    `targetGeometryValid=${report.targetGeometryValidityRatio.toFixed(3)}`,
    `missingTargetKeys=${report.missingInTargetAttributeKeys.length}`,
    `extraTargetKeys=${report.extraInTargetAttributeKeys.length}`,
    `passed=${report.passed ? "yes" : "no"}`,
  ].join(" ");
}

async function queryFeatureCount(
  fetchFn: typeof fetch,
  baseUrl: string,
  serviceId: string,
  layerId: number,
): Promise<number> {
  const response = await fetchJson(fetchFn, buildQueryUrl(baseUrl, serviceId, layerId, {
    f: "json",
    where: "1=1",
    returnCountOnly: "true",
  }));

  if (typeof response.count === "number" && Number.isFinite(response.count)) {
    return response.count;
  }

  return Array.isArray(response.features) ? response.features.length : 0;
}

async function queryFeatureSample(
  fetchFn: typeof fetch,
  baseUrl: string,
  serviceId: string,
  layerId: number,
  sampleSize: number,
): Promise<QueryFeaturesResponse> {
  return fetchJson(fetchFn, buildQueryUrl(baseUrl, serviceId, layerId, {
    f: "json",
    where: "1=1",
    outFields: "*",
    returnGeometry: "true",
    resultRecordCount: String(sampleSize),
  }));
}

async function fetchJson(
  fetchFn: typeof fetch,
  url: string,
): Promise<QueryFeaturesResponse> {
  const response = await fetchFn(url, {
    method: "GET",
    headers: {
      Accept: "application/json",
    },
  });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Reconciliation query failed (${response.status}): ${text.slice(0, 200)}`);
  }

  if (!text) {
    return {};
  }

  try {
    return JSON.parse(text) as QueryFeaturesResponse;
  } catch {
    return {};
  }
}

function buildQueryUrl(
  baseUrl: string,
  serviceId: string,
  layerId: number,
  params: Record<string, string>,
): string {
  const query = new URLSearchParams(params);
  return (
    `${normalizeBaseUrl(baseUrl)}/rest/services/${encodeURIComponent(serviceId)}` +
    `/FeatureServer/${layerId}/query?${query.toString()}`
  );
}

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, "");
}

function computeGeometryValidityRatio(features: readonly unknown[]): number {
  if (features.length === 0) {
    return 1;
  }

  let valid = 0;
  for (const feature of features) {
    if (isFeatureGeometryValid(feature)) {
      valid += 1;
    }
  }
  return valid / features.length;
}

function isFeatureGeometryValid(feature: unknown): boolean {
  if (!isObject(feature) || !isObject(feature.geometry)) {
    return false;
  }

  const geometry = feature.geometry as Record<string, unknown>;
  if (typeof geometry.x === "number" && typeof geometry.y === "number") {
    return true;
  }
  if (Array.isArray(geometry.rings)) {
    return geometry.rings.length > 0;
  }
  if (Array.isArray(geometry.paths)) {
    return geometry.paths.length > 0;
  }
  if (Array.isArray(geometry.points)) {
    return geometry.points.length > 0;
  }

  return false;
}

function collectAttributeKeys(features: readonly unknown[]): string[] {
  const keys = new Set<string>();
  for (const feature of features) {
    if (!isObject(feature) || !isObject(feature.attributes)) {
      continue;
    }
    for (const key of Object.keys(feature.attributes)) {
      keys.add(key);
    }
  }
  return Array.from(keys).sort();
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
