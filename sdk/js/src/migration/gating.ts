import type { JsMigrationReport } from "./report.js";

export interface MigrationGateOptions {
  failOnManual: boolean;
  failOnUnhandled: boolean;
  failOnBlocked: boolean;
  maxManualRatio?: number;
  maxManualInterventionRatio?: number;
}

export interface MigrationGateEvaluation {
  failed: boolean;
  failures: string[];
}

export function evaluateMigrationGates(
  report: JsMigrationReport,
  options: MigrationGateOptions,
): MigrationGateEvaluation {
  const failures: string[] = [];

  if (options.failOnManual && report.manualRewriteMetric.numerator > 0) {
    failures.push(
      `manual rewrite required (${report.manualRewriteMetric.numerator}/${report.manualRewriteMetric.denominator})`,
    );
  }

  if (options.failOnUnhandled && report.unhandledArcGisModules.length > 0) {
    failures.push(`${report.unhandledArcGisModules.length} ArcGIS modules remain outside codemod scope`);
  }

  if (options.maxManualRatio !== undefined && report.manualRewriteMetric.ratio > options.maxManualRatio) {
    failures.push(
      `manual rewrite ratio ${report.manualRewriteMetric.ratio.toFixed(3)} exceeds max ${options.maxManualRatio.toFixed(3)}`,
    );
  }

  if (
    options.maxManualInterventionRatio !== undefined &&
    report.manualInterventionMetric.ratio > options.maxManualInterventionRatio
  ) {
    failures.push(
      `manual intervention ratio ${report.manualInterventionMetric.ratio.toFixed(3)} exceeds max ${options.maxManualInterventionRatio.toFixed(3)}`,
    );
  }

  if (options.failOnBlocked && report.readiness === "blocked") {
    const failedGates = report.gates
      .filter((gate) => !gate.passed)
      .map((gate) => gate.gate)
      .join(", ");
    failures.push(`readiness is blocked (failed gates: ${failedGates})`);
  }

  return {
    failed: failures.length > 0,
    failures,
  };
}
