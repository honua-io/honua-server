import {
  SUPPORTED_ARCGIS_MODULES,
  type CodemodConstructorKind,
  type EsriCompatCodemodResult,
  type MigrationTodo,
} from "./codemod.js";
import { scanArcGisUsage, summarizeArcGisScan, type ArcGisScanReport } from "./scanner.js";

export interface ManualRewriteMetric {
  numerator: number;
  denominator: number;
  ratio: number;
  scope: string;
}

export interface JsMigrationReport {
  rootDir: string;
  scanSummary: string;
  scanReport: ArcGisScanReport;
  codemodResult: EsriCompatCodemodResult;
  manualRewriteMetric: ManualRewriteMetric;
  manualTodosByKind: Record<CodemodConstructorKind, number>;
  manualTodoReasons: MigrationReasonSummary[];
  unhandledArcGisModules: ArcGisModuleSummary[];
  manualTodos: MigrationTodo[];
}

export interface MigrationReasonSummary {
  reason: string;
  count: number;
  kinds: CodemodConstructorKind[];
}

export interface ArcGisModuleSummary {
  modulePath: string;
  count: number;
}

export function buildJsMigrationReport(
  rootDir: string,
  codemodResult: EsriCompatCodemodResult,
  scanReport?: ArcGisScanReport,
): JsMigrationReport {
  const resolvedScan = scanReport ?? scanArcGisUsage(rootDir);
  const scanSummary = summarizeArcGisScan(resolvedScan);

  const denominator = codemodResult.metrics.totalCodemodScopedCallSites;
  const numerator = codemodResult.metrics.manualCallSites;
  const ratio = denominator === 0 ? 0 : numerator / denominator;
  const manualTodosByKind = summarizeManualTodosByKind(codemodResult.manualTodos);
  const manualTodoReasons = summarizeManualTodoReasons(codemodResult.manualTodos);
  const unhandledArcGisModules = summarizeUnhandledModules(resolvedScan);

  return {
    rootDir: codemodResult.rootDir,
    scanSummary,
    scanReport: resolvedScan,
    codemodResult,
    manualRewriteMetric: {
      numerator,
      denominator,
      ratio,
      scope: "FeatureLayer/Map/MapView constructor call sites in safe-codemod scope",
    },
    manualTodosByKind,
    manualTodoReasons,
    unhandledArcGisModules,
    manualTodos: codemodResult.manualTodos,
  };
}

function summarizeManualTodosByKind(
  todos: readonly MigrationTodo[],
): Record<CodemodConstructorKind, number> {
  const summary: Record<CodemodConstructorKind, number> = {
    "feature-layer": 0,
    map: 0,
    "map-view": 0,
  };

  for (const todo of todos) {
    summary[todo.kind] += 1;
  }

  return summary;
}

function summarizeManualTodoReasons(todos: readonly MigrationTodo[]): MigrationReasonSummary[] {
  const reasons = new Map<string, { count: number; kinds: Set<CodemodConstructorKind> }>();

  for (const todo of todos) {
    let bucket = reasons.get(todo.reason);
    if (!bucket) {
      bucket = { count: 0, kinds: new Set<CodemodConstructorKind>() };
      reasons.set(todo.reason, bucket);
    }

    bucket.count += 1;
    bucket.kinds.add(todo.kind);
  }

  return Array.from(reasons.entries())
    .map(([reason, bucket]) => ({
      reason,
      count: bucket.count,
      kinds: Array.from(bucket.kinds).sort(),
    }))
    .sort((a, b) => (a.count === b.count ? a.reason.localeCompare(b.reason) : b.count - a.count));
}

function summarizeUnhandledModules(scanReport: ArcGisScanReport): ArcGisModuleSummary[] {
  const supportedModules = new Set(SUPPORTED_ARCGIS_MODULES);
  const moduleCounts = new Map<string, number>();

  for (const hit of scanReport.imports) {
    if (supportedModules.has(hit.modulePath)) {
      continue;
    }
    moduleCounts.set(hit.modulePath, (moduleCounts.get(hit.modulePath) ?? 0) + 1);
  }

  return Array.from(moduleCounts.entries())
    .map(([modulePath, count]) => ({ modulePath, count }))
    .sort((a, b) => (a.count === b.count ? a.modulePath.localeCompare(b.modulePath) : b.count - a.count));
}
