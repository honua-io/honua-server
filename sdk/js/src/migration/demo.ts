import fs from "node:fs";
import path from "node:path";

import { type CodemodTarget, runEsriCompatCodemod } from "./codemod.js";
import {
  type LayerReconciliationOptions,
  type LayerReconciliationReport,
  runLayerReconciliation,
} from "./reconcile.js";
import { type JsMigrationReport, buildJsMigrationReport } from "./report.js";
import { scanArcGisUsage } from "./scanner.js";

const IMPORT_STATUS_BY_ENUM_VALUE = new Map<number, string>([
  [0, "Queued"],
  [1, "Discovering"],
  [2, "RetrievingFeatures"],
  [3, "CreatingTable"],
  [4, "InsertingFeatures"],
  [5, "Publishing"],
  [6, "Completed"],
  [7, "Failed"],
  [8, "Cancelled"],
]);

const TERMINAL_IMPORT_STATUSES = new Set(["Completed", "Failed", "Cancelled"]);
const DEFAULT_IMPORT_TIMEOUT_MS = 10 * 60 * 1000;
const DEFAULT_IMPORT_POLL_INTERVAL_MS = 2_000;

export interface ParsedGeoservicesServiceUrl {
  baseUrl: string;
  serviceId: string;
  serviceType: "FeatureServer" | "MapServer";
  layerId?: number;
}

export interface GeoservicesImportStageOptions {
  adminBaseUrl: string;
  adminApiKey?: string;
  sourceServiceUrl: string;
  layerId: number;
  tableName: string;
  targetSrid?: number;
  overwriteExisting?: boolean;
  whereClause?: string;
  outputFields?: string[];
  batchSize?: number;
  requestTimeoutSeconds?: number;
  maxRetries?: number;
  autoPublish?: boolean;
  timeoutMs?: number;
  pollIntervalMs?: number;
  fetchFn?: typeof fetch;
}

export interface GeoservicesImportJobReport {
  jobId: string;
  status: string;
  statusUrl: string;
  pollCount: number;
  currentPhase?: string;
  featuresProcessed?: number;
  estimatedTotalFeatures?: number;
  startedAt?: string;
  completedAt?: string;
  durationMs?: number;
  errorMessage?: string;
}

export interface MigrationDemoOptions {
  fixtureName: string;
  fixturesRoot: string;
  outputDir: string;
  codemodTarget?: CodemodTarget;
  compatImportPath?: string;
  annotateTodos?: boolean;
  skipImport?: boolean;
  skipReconciliation?: boolean;
  importOptions?: GeoservicesImportStageOptions;
  reconciliationOptions?: LayerReconciliationOptions;
}

export interface MigrationDemoReport {
  generatedAt: string;
  elapsedMs: number;
  fixtureName: string;
  codemodTarget: CodemodTarget;
  workingAppDir: string;
  import?: GeoservicesImportJobReport;
  migration: JsMigrationReport;
  reconciliation?: LayerReconciliationReport;
  passed: boolean;
}

export async function runMigrationDemo(options: MigrationDemoOptions): Promise<MigrationDemoReport> {
  const startedAtMs = Date.now();
  const codemodTarget = options.codemodTarget ?? "honua-compat";
  const fixtureRootDir = path.resolve(options.fixturesRoot);
  const fixtureDir = path.join(fixtureRootDir, options.fixtureName);
  const outputDir = path.resolve(options.outputDir);
  const workingAppDir = path.join(outputDir, options.fixtureName);

  if (!fs.existsSync(fixtureDir)) {
    throw new Error(`Fixture directory does not exist: ${fixtureDir}`);
  }

  fs.mkdirSync(outputDir, { recursive: true });
  fs.rmSync(workingAppDir, { recursive: true, force: true });
  fs.cpSync(fixtureDir, workingAppDir, { recursive: true });

  let importReport: GeoservicesImportJobReport | undefined;
  if (!options.skipImport) {
    if (!options.importOptions) {
      throw new Error("Import options are required unless --skip-import is set.");
    }
    importReport = await runGeoservicesImportJob(options.importOptions);
  }

  const scanReport = scanArcGisUsage(workingAppDir);
  const codemodResult = runEsriCompatCodemod({
    rootDir: workingAppDir,
    write: true,
    annotateTodos: options.annotateTodos ?? true,
    compatImportPath: options.compatImportPath,
    target: codemodTarget,
  });
  const migrationReport = buildJsMigrationReport(workingAppDir, codemodResult, scanReport);

  let reconciliationReport: LayerReconciliationReport | undefined;
  if (!options.skipReconciliation) {
    if (!options.reconciliationOptions) {
      throw new Error("Reconciliation options are required unless --skip-reconcile is set.");
    }
    reconciliationReport = await runLayerReconciliation(options.reconciliationOptions);
  }

  const importPassed = !importReport || importReport.status === "Completed";
  const migrationPassed = migrationReport.readiness === "ready";
  const reconciliationPassed = !reconciliationReport || reconciliationReport.passed;
  const elapsedMs = Math.max(0, Date.now() - startedAtMs);

  return {
    generatedAt: new Date().toISOString(),
    elapsedMs,
    fixtureName: options.fixtureName,
    codemodTarget,
    workingAppDir,
    import: importReport,
    migration: migrationReport,
    reconciliation: reconciliationReport,
    passed: importPassed && migrationPassed && reconciliationPassed,
  };
}

export async function runGeoservicesImportJob(
  options: GeoservicesImportStageOptions,
): Promise<GeoservicesImportJobReport> {
  const fetchFn = options.fetchFn ?? fetch;
  const pollIntervalMs =
    typeof options.pollIntervalMs === "number" && Number.isFinite(options.pollIntervalMs)
      ? Math.max(100, Math.trunc(options.pollIntervalMs))
      : DEFAULT_IMPORT_POLL_INTERVAL_MS;
  const timeoutMs =
    typeof options.timeoutMs === "number" && Number.isFinite(options.timeoutMs)
      ? Math.max(1_000, Math.trunc(options.timeoutMs))
      : DEFAULT_IMPORT_TIMEOUT_MS;

  const importApiBase = `${normalizeBaseUrl(options.adminBaseUrl)}/api/v1/admin/import/geoservices`;
  const startUrl = `${importApiBase}/start`;

  const startPayload: Record<string, unknown> = {
    serviceUrl: options.sourceServiceUrl,
    layerId: options.layerId,
    tableName: options.tableName,
    autoPublish: options.autoPublish ?? true,
  };

  if (typeof options.targetSrid === "number" && Number.isFinite(options.targetSrid)) {
    startPayload.targetSrid = Math.trunc(options.targetSrid);
  }
  if (typeof options.overwriteExisting === "boolean") {
    startPayload.overwriteExisting = options.overwriteExisting;
  }
  if (typeof options.whereClause === "string" && options.whereClause.length > 0) {
    startPayload.whereClause = options.whereClause;
  }
  if (Array.isArray(options.outputFields) && options.outputFields.length > 0) {
    startPayload.outputFields = options.outputFields;
  }
  if (typeof options.batchSize === "number" && Number.isFinite(options.batchSize)) {
    startPayload.batchSize = Math.max(1, Math.trunc(options.batchSize));
  }
  if (typeof options.requestTimeoutSeconds === "number" && Number.isFinite(options.requestTimeoutSeconds)) {
    startPayload.requestTimeoutSeconds = Math.max(1, Math.trunc(options.requestTimeoutSeconds));
  }
  if (typeof options.maxRetries === "number" && Number.isFinite(options.maxRetries)) {
    startPayload.maxRetries = Math.max(0, Math.trunc(options.maxRetries));
  }

  const startResponse = await fetchJson(fetchFn, startUrl, {
    method: "POST",
    headers: buildJsonHeaders(options.adminApiKey),
    body: JSON.stringify(startPayload),
  });
  const startRecord = asRecord(startResponse, "Import start response");
  const jobId = readRequiredString(startRecord, "jobId");
  const statusUrl = resolveJobStatusUrl(importApiBase, readOptionalString(startRecord, "statusUrl"), jobId);

  const deadline = Date.now() + timeoutMs;
  let pollCount = 0;
  let latestProgress: Record<string, unknown> | undefined;

  for (;;) {
    pollCount += 1;
    const progressResponse = await fetchJson(fetchFn, statusUrl, {
      method: "GET",
      headers: buildJsonHeaders(options.adminApiKey),
    });
    const progressRecord = asRecord(progressResponse, "Import progress response");
    latestProgress = progressRecord;

    const normalizedStatus = normalizeImportStatus(progressRecord.status);
    if (TERMINAL_IMPORT_STATUSES.has(normalizedStatus)) {
      break;
    }

    if (Date.now() >= deadline) {
      throw new Error(`Timed out waiting for import job ${jobId} after ${timeoutMs}ms.`);
    }

    await delay(pollIntervalMs);
  }

  if (!latestProgress) {
    throw new Error(`Import job ${jobId} completed without a progress payload.`);
  }

  const status = normalizeImportStatus(latestProgress.status);
  const currentPhase = readOptionalString(latestProgress, "currentPhase");
  const errorMessage = readOptionalString(latestProgress, "errorMessage");

  if (status !== "Completed") {
    throw new Error(`Import job ${jobId} ended with status ${status}${errorMessage ? `: ${errorMessage}` : ""}`);
  }

  return {
    jobId,
    status,
    statusUrl,
    pollCount,
    currentPhase,
    featuresProcessed: readOptionalNumber(latestProgress, "featuresProcessed"),
    estimatedTotalFeatures: readOptionalNumber(latestProgress, "estimatedTotalFeatures"),
    startedAt: readOptionalString(latestProgress, "startedAt"),
    completedAt: readOptionalString(latestProgress, "completedAt"),
    durationMs: readOptionalNumber(latestProgress, "durationMs"),
    errorMessage,
  };
}

export function parseGeoservicesServiceUrl(serviceUrl: string): ParsedGeoservicesServiceUrl | undefined {
  let parsed: URL;
  try {
    parsed = new URL(serviceUrl);
  } catch {
    return undefined;
  }

  const pathname = parsed.pathname.replace(/\/+$/, "");
  const marker = "/rest/services/";
  const lowerPath = pathname.toLowerCase();
  const markerIndex = lowerPath.indexOf(marker);
  if (markerIndex < 0) {
    return undefined;
  }

  const afterMarker = pathname.slice(markerIndex + marker.length);
  const lowerAfterMarker = afterMarker.toLowerCase();
  const featureIndex = lowerAfterMarker.indexOf("/featureserver");
  const mapIndex = lowerAfterMarker.indexOf("/mapserver");
  const serviceTypeIndex =
    featureIndex >= 0 && mapIndex >= 0 ? Math.min(featureIndex, mapIndex) : featureIndex >= 0 ? featureIndex : mapIndex;
  if (serviceTypeIndex < 0) {
    return undefined;
  }

  const serviceType = featureIndex >= 0 && featureIndex === serviceTypeIndex ? "FeatureServer" : "MapServer";
  const serviceId = decodeURIComponent(afterMarker.slice(0, serviceTypeIndex));
  const remainder = afterMarker.slice(serviceTypeIndex + `/${serviceType}`.length);

  let layerId: number | undefined;
  if (remainder.length > 0) {
    const layerMatch = remainder.match(/^\/(\d+)$/);
    if (!layerMatch) {
      return undefined;
    }
    layerId = Number.parseInt(layerMatch[1], 10);
    if (!Number.isFinite(layerId)) {
      return undefined;
    }
  }

  const basePath = pathname.slice(0, markerIndex);
  const baseUrl = `${parsed.protocol}//${parsed.host}${basePath}`;
  return {
    baseUrl,
    serviceId,
    serviceType,
    layerId,
  };
}

async function fetchJson(fetchFn: typeof fetch, url: string, init: RequestInit): Promise<unknown> {
  const response = await fetchFn(url, init);
  const text = await response.text();

  let body: unknown = {};
  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = {};
    }
  }

  if (!response.ok) {
    const preview = text.length > 0 ? text.slice(0, 300) : `${response.status} ${response.statusText}`;
    throw new Error(`HTTP ${response.status} for ${url}: ${preview}`);
  }

  return body;
}

function asRecord(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${context} was not a JSON object.`);
  }
  return value as Record<string, unknown>;
}

function readRequiredString(source: Record<string, unknown>, key: string): string {
  const value = source[key];
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`Expected "${key}" to be a non-empty string.`);
  }
  return value;
}

function readOptionalString(source: Record<string, unknown>, key: string): string | undefined {
  const value = source[key];
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function readOptionalNumber(source: Record<string, unknown>, key: string): number | undefined {
  const value = source[key];
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function normalizeImportStatus(statusValue: unknown): string {
  if (typeof statusValue === "string" && statusValue.length > 0) {
    return statusValue;
  }
  if (typeof statusValue === "number" && Number.isFinite(statusValue)) {
    const mapped = IMPORT_STATUS_BY_ENUM_VALUE.get(Math.trunc(statusValue));
    if (mapped) {
      return mapped;
    }
  }
  return "Unknown";
}

function buildJsonHeaders(adminApiKey?: string): Record<string, string> {
  const headers: Record<string, string> = {
    Accept: "application/json",
    "Content-Type": "application/json",
    Connection: "close",
  };
  if (adminApiKey) {
    headers["X-API-Key"] = adminApiKey;
  }
  return headers;
}

function normalizeBaseUrl(baseUrl: string): string {
  return baseUrl.replace(/\/+$/, "");
}

function resolveJobStatusUrl(importApiBase: string, providedStatusUrl: string | undefined, jobId: string): string {
  const statusPath = providedStatusUrl && providedStatusUrl.length > 0 ? providedStatusUrl : `jobs/${jobId}`;
  if (/^https?:\/\//i.test(statusPath)) {
    return statusPath;
  }
  return `${normalizeBaseUrl(importApiBase)}/${statusPath.replace(/^\/+/, "")}`;
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
