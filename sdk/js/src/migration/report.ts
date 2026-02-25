import type { EsriCompatCodemodResult, MigrationTodo } from "./codemod.js";
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
  manualTodos: MigrationTodo[];
}

export function buildJsMigrationReport(
  rootDir: string,
  codemodResult: EsriCompatCodemodResult,
  scanReport?: ArcGisScanReport,
): JsMigrationReport {
  const resolvedScan = scanReport ?? scanArcGisUsage(rootDir);
  const scanSummary = summarizeArcGisScan(resolvedScan);

  const denominator = codemodResult.metrics.totalFeatureLayerCallSites;
  const numerator = codemodResult.metrics.manualCallSites;
  const ratio = denominator === 0 ? 0 : numerator / denominator;

  return {
    rootDir: codemodResult.rootDir,
    scanSummary,
    scanReport: resolvedScan,
    codemodResult,
    manualRewriteMetric: {
      numerator,
      denominator,
      ratio,
      scope: "FeatureLayer constructor call sites in safe-codemod scope",
    },
    manualTodos: codemodResult.manualTodos,
  };
}
