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
  readiness: MigrationReadiness;
  gates: MigrationGateResult[];
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
  usageStyle: ArcGisUsageStyle;
  count: number;
}

export type ArcGisUsageStyle = "static-import" | "dynamic-import" | "require";

export type MigrationReadiness = "ready" | "assisted" | "blocked";

export interface MigrationGateResult {
  gate: "no-manual-todos" | "no-unhandled-modules" | "no-blocking-flags";
  passed: boolean;
  detail: string;
}

const BLOCKING_FLAGS = new Set([
  "scene-3d-detected",
  "advanced-widget-or-networking-detected",
]);

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
  const gates = buildMigrationGates(codemodResult, resolvedScan, unhandledArcGisModules);
  const readiness = determineReadiness(gates);

  return {
    rootDir: codemodResult.rootDir,
    scanSummary,
    scanReport: resolvedScan,
    codemodResult,
    manualRewriteMetric: {
      numerator,
      denominator,
      ratio,
      scope: "FeatureLayer/Map/MapView/WebMap constructor call sites in safe-codemod scope",
    },
    readiness,
    gates,
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
    "web-map": 0,
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
    const usageStyle = classifyUsageStyle(hit.importClause);
    const isHandledByCodemodScope =
      supportedModules.has(hit.modulePath) && usageStyle === "static-import";
    if (isHandledByCodemodScope) {
      continue;
    }

    const key = `${hit.modulePath}|${usageStyle}`;
    moduleCounts.set(key, (moduleCounts.get(key) ?? 0) + 1);
  }

  return Array.from(moduleCounts.entries())
    .map(([key, count]) => {
      const [modulePath, usageStyleText] = key.split("|", 2);
      return {
        modulePath,
        usageStyle: usageStyleText as ArcGisUsageStyle,
        count,
      };
    })
    .sort((a, b) => {
      if (a.count !== b.count) {
        return b.count - a.count;
      }
      if (a.modulePath !== b.modulePath) {
        return a.modulePath.localeCompare(b.modulePath);
      }
      return a.usageStyle.localeCompare(b.usageStyle);
    });
}

function classifyUsageStyle(importClause: string): ArcGisUsageStyle {
  if (importClause === "import(...)") {
    return "dynamic-import";
  }
  if (importClause === "require(...)") {
    return "require";
  }
  return "static-import";
}

function buildMigrationGates(
  codemodResult: EsriCompatCodemodResult,
  scanReport: ArcGisScanReport,
  unhandledModules: readonly ArcGisModuleSummary[],
): MigrationGateResult[] {
  const hasManualTodos = codemodResult.metrics.manualCallSites > 0;
  const blockingFlags = scanReport.flags.filter((flag) => BLOCKING_FLAGS.has(flag)).sort();

  return [
    {
      gate: "no-manual-todos",
      passed: !hasManualTodos,
      detail: hasManualTodos
        ? `${codemodResult.metrics.manualCallSites} manual codemod-scoped call sites remain`
        : "all codemod-scoped call sites auto-migrated",
    },
    {
      gate: "no-unhandled-modules",
      passed: unhandledModules.length === 0,
      detail:
        unhandledModules.length === 0
          ? "all discovered ArcGIS modules are in codemod scope"
          : `${unhandledModules.length} ArcGIS modules remain outside codemod scope`,
    },
    {
      gate: "no-blocking-flags",
      passed: blockingFlags.length === 0,
      detail:
        blockingFlags.length === 0
          ? "no blocking migration flags detected"
          : `blocking flags: ${blockingFlags.join(", ")}`,
    },
  ];
}

function determineReadiness(gates: readonly MigrationGateResult[]): MigrationReadiness {
  const blockingGate = gates.find((gate) => gate.gate === "no-blocking-flags");
  if (blockingGate && !blockingGate.passed) {
    return "blocked";
  }

  const allPassed = gates.every((gate) => gate.passed);
  return allPassed ? "ready" : "assisted";
}
