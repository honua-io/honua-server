#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { scanArcGisUsage, summarizeArcGisScan } from "./scanner.js";
import {
  runEsriCompatCodemod,
  type CodemodMetricsByKind,
  type CodemodTarget,
} from "./codemod.js";
import { buildJsMigrationReport } from "./report.js";
import { evaluateMigrationGates } from "./gating.js";
import { runLayerReconciliation, summarizeLayerReconciliation } from "./reconcile.js";
import { getJsParityMatrix, summarizeJsParityMatrix } from "./parity-matrix.js";

interface ParsedArgs {
  command: "scan" | "codemod" | "reconcile" | "matrix" | "fixtures";
  target: string;
  codemodTarget: CodemodTarget;
  write: boolean;
  annotateTodos: boolean;
  failOnManual: boolean;
  failOnUnhandled: boolean;
  failOnBlocked: boolean;
  maxManualRatio?: number;
  maxManualInterventionRatio?: number;
  reportPath?: string;
  compatImportPath?: string;
  sourceBaseUrl?: string;
  sourceServiceId?: string;
  targetBaseUrl?: string;
  targetServiceId?: string;
  layerId?: number;
  sampleSize?: number;
  fixtureNames?: string[];
}

interface FixtureMetricSnapshot {
  fixture: string;
  rootDir: string;
  readiness: "ready" | "assisted" | "blocked";
  scanSummary: string;
  flags: string[];
  totalCallSites: number;
  autoMigratedCallSites: number;
  manualCallSites: number;
  manualRewrite: {
    numerator: number;
    denominator: number;
    ratio: number;
  };
  manualIntervention: {
    numerator: number;
    denominator: number;
    ratio: number;
    unhandledUsageHits: number;
  };
  gates: string;
}

interface FixtureMetricsSummary {
  fixtureCount: number;
  ready: number;
  assisted: number;
  blocked: number;
  totalCallSites: number;
  autoMigratedCallSites: number;
  manualCallSites: number;
  manualRewriteNumerator: number;
  manualRewriteDenominator: number;
  manualRewriteRatio: number;
  manualInterventionNumerator: number;
  manualInterventionDenominator: number;
  manualInterventionRatio: number;
}

interface FixtureMetricsReport {
  codemodTarget: CodemodTarget;
  fixturesRoot: string;
  fixtureNames: string[];
  generatedAt: string;
  summary: FixtureMetricsSummary;
  fixtures: FixtureMetricSnapshot[];
}

const DEFAULT_REAL_SAMPLE_FIXTURE_NAMES = [
  "esri-real-sample-ops-center-app",
  "esri-real-sample-editing-app",
  "esri-real-sample-network-app",
] as const;

const parsed = parseArgs(process.argv.slice(2));
if (!parsed) {
  printUsage();
  process.exitCode = 1;
} else {
  if (parsed.command === "scan") {
    runScan(parsed.target, parsed.reportPath);
  } else if (parsed.command === "matrix") {
    runMatrix(parsed.reportPath);
  } else if (parsed.command === "fixtures") {
    runFixtures(parsed);
  } else if (parsed.command === "reconcile") {
    void runReconcile(parsed).catch((error) => {
      process.stderr.write(`reconcileError=${error instanceof Error ? error.message : String(error)}\n`);
      process.exitCode = 1;
    });
  } else {
    runCodemod(parsed);
  }
}

function runMatrix(reportPath?: string): void {
  const matrix = getJsParityMatrix();
  const summary = summarizeJsParityMatrix(matrix);
  process.stdout.write(
    [
      `entries=${matrix.length}`,
      `honuaCompat=${summary.honuaCompat.compat}`,
      `honuaAssisted=${summary.honuaCompat.assisted}`,
      `honuaUnsupported=${summary.honuaCompat.unsupported}`,
      `esriLeafletCompat=${summary.esriLeaflet.compat}`,
      `esriLeafletAssisted=${summary.esriLeaflet.assisted}`,
      `esriLeafletUnsupported=${summary.esriLeaflet.unsupported}`,
    ].join(" "),
  );
  process.stdout.write("\n");
  process.stdout.write(`${JSON.stringify({ summary, matrix }, null, 2)}\n`);

  if (reportPath) {
    fs.writeFileSync(reportPath, `${JSON.stringify({ summary, matrix }, null, 2)}\n`, "utf8");
    process.stdout.write(`reportWritten=${reportPath}\n`);
  }
}

function runScan(target: string, reportPath?: string): void {
  const report = scanArcGisUsage(target);
  const summary = summarizeArcGisScan(report);

  process.stdout.write(`${summary}\n`);
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);

  if (reportPath) {
    fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stdout.write(`reportWritten=${reportPath}\n`);
  }
}

function runFixtures(args: ParsedArgs): void {
  const fixturesRoot = args.target;
  const fixtureNames =
    args.fixtureNames && args.fixtureNames.length > 0
      ? [...args.fixtureNames]
      : [...DEFAULT_REAL_SAMPLE_FIXTURE_NAMES];
  const report = buildFixtureMetricsReport(fixturesRoot, fixtureNames, args.codemodTarget);

  process.stdout.write(
    [
      `fixtures=${report.summary.fixtureCount}`,
      `ready=${report.summary.ready}`,
      `assisted=${report.summary.assisted}`,
      `blocked=${report.summary.blocked}`,
      `autoMigrated=${report.summary.autoMigratedCallSites}`,
      `manual=${report.summary.manualCallSites}`,
      `manualRewrite=${report.summary.manualRewriteNumerator}/${report.summary.manualRewriteDenominator}`,
      `manualIntervention=${report.summary.manualInterventionNumerator}/${report.summary.manualInterventionDenominator}`,
      `target=${report.codemodTarget}`,
    ].join(" "),
  );
  process.stdout.write("\n");
  process.stdout.write(`${formatFixtureMetricsTable(report.fixtures)}\n`);
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);

  if (args.reportPath) {
    fs.mkdirSync(path.dirname(args.reportPath), { recursive: true });
    fs.writeFileSync(args.reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stdout.write(`reportWritten=${args.reportPath}\n`);
  }
}

function buildFixtureMetricsReport(
  fixturesRoot: string,
  fixtureNames: readonly string[],
  codemodTarget: CodemodTarget,
): FixtureMetricsReport {
  const fixtures = fixtureNames.map((fixtureName) =>
    buildFixtureMetricSnapshot(fixturesRoot, fixtureName, codemodTarget),
  );

  const summary = summarizeFixtureMetrics(fixtures);
  return {
    codemodTarget,
    fixturesRoot,
    fixtureNames: [...fixtureNames],
    generatedAt: new Date().toISOString(),
    summary,
    fixtures,
  };
}

function buildFixtureMetricSnapshot(
  fixturesRoot: string,
  fixtureName: string,
  codemodTarget: CodemodTarget,
): FixtureMetricSnapshot {
  const rootDir = path.join(fixturesRoot, fixtureName);
  if (!fs.existsSync(rootDir)) {
    throw new Error(`Fixture directory does not exist: ${rootDir}`);
  }

  const scanReport = scanArcGisUsage(rootDir);
  const codemodResult = runEsriCompatCodemod({
    rootDir,
    write: false,
    annotateTodos: false,
    target: codemodTarget,
  });
  const report = buildJsMigrationReport(rootDir, codemodResult, scanReport);

  return {
    fixture: fixtureName,
    rootDir,
    readiness: report.readiness,
    scanSummary: report.scanSummary,
    flags: [...report.scanReport.flags],
    totalCallSites: codemodResult.metrics.totalCodemodScopedCallSites,
    autoMigratedCallSites: codemodResult.metrics.autoMigratedCallSites,
    manualCallSites: codemodResult.metrics.manualCallSites,
    manualRewrite: {
      numerator: report.manualRewriteMetric.numerator,
      denominator: report.manualRewriteMetric.denominator,
      ratio: report.manualRewriteMetric.ratio,
    },
    manualIntervention: {
      numerator: report.manualInterventionMetric.numerator,
      denominator: report.manualInterventionMetric.denominator,
      ratio: report.manualInterventionMetric.ratio,
      unhandledUsageHits: report.manualInterventionMetric.unhandledUsageHits,
    },
    gates: formatGateResults(report.gates),
  };
}

function summarizeFixtureMetrics(
  fixtures: readonly FixtureMetricSnapshot[],
): FixtureMetricsSummary {
  const ready = fixtures.filter((fixture) => fixture.readiness === "ready").length;
  const assisted = fixtures.filter((fixture) => fixture.readiness === "assisted").length;
  const blocked = fixtures.filter((fixture) => fixture.readiness === "blocked").length;
  const totalCallSites = fixtures.reduce((sum, fixture) => sum + fixture.totalCallSites, 0);
  const autoMigratedCallSites = fixtures.reduce(
    (sum, fixture) => sum + fixture.autoMigratedCallSites,
    0,
  );
  const manualCallSites = fixtures.reduce((sum, fixture) => sum + fixture.manualCallSites, 0);
  const manualRewriteNumerator = fixtures.reduce(
    (sum, fixture) => sum + fixture.manualRewrite.numerator,
    0,
  );
  const manualRewriteDenominator = fixtures.reduce(
    (sum, fixture) => sum + fixture.manualRewrite.denominator,
    0,
  );
  const manualInterventionNumerator = fixtures.reduce(
    (sum, fixture) => sum + fixture.manualIntervention.numerator,
    0,
  );
  const manualInterventionDenominator = fixtures.reduce(
    (sum, fixture) => sum + fixture.manualIntervention.denominator,
    0,
  );

  return {
    fixtureCount: fixtures.length,
    ready,
    assisted,
    blocked,
    totalCallSites,
    autoMigratedCallSites,
    manualCallSites,
    manualRewriteNumerator,
    manualRewriteDenominator,
    manualRewriteRatio:
      manualRewriteDenominator === 0 ? 0 : manualRewriteNumerator / manualRewriteDenominator,
    manualInterventionNumerator,
    manualInterventionDenominator,
    manualInterventionRatio:
      manualInterventionDenominator === 0
        ? 0
        : manualInterventionNumerator / manualInterventionDenominator,
  };
}

function formatFixtureMetricsTable(fixtures: readonly FixtureMetricSnapshot[]): string {
  const rows = [
    "| fixture | readiness | auto/manual/total | rewrite ratio | intervention ratio | flags |",
    "| --- | --- | --- | --- | --- | --- |",
  ];
  for (const fixture of fixtures) {
    rows.push(
      `| ${fixture.fixture} | ${fixture.readiness} | ${fixture.autoMigratedCallSites}/${fixture.manualCallSites}/${fixture.totalCallSites} | ${fixture.manualRewrite.numerator}/${fixture.manualRewrite.denominator} (${fixture.manualRewrite.ratio.toFixed(3)}) | ${fixture.manualIntervention.numerator}/${fixture.manualIntervention.denominator} (${fixture.manualIntervention.ratio.toFixed(3)}) | ${fixture.flags.join(",") || "none"} |`,
    );
  }
  return rows.join("\n");
}

function runCodemod(args: ParsedArgs): void {
  const scanReport = scanArcGisUsage(args.target);
  const codemodResult = runEsriCompatCodemod({
    rootDir: args.target,
    write: args.write,
    compatImportPath: args.compatImportPath,
    annotateTodos: args.annotateTodos,
    target: args.codemodTarget,
  });
  const report = buildJsMigrationReport(args.target, codemodResult, scanReport);

  process.stdout.write(
    [
      `filesScanned=${codemodResult.filesScanned}`,
      `filesChanged=${codemodResult.filesChanged}`,
      `autoMigrated=${codemodResult.metrics.autoMigratedCallSites}`,
      `manual=${codemodResult.metrics.manualCallSites}`,
      `manualRewrite=${report.manualRewriteMetric.numerator}/${report.manualRewriteMetric.denominator}`,
      `manualIntervention=${report.manualInterventionMetric.numerator}/${report.manualInterventionMetric.denominator}`,
      `writeMode=${args.write ? "enabled" : "dry-run"}`,
      `annotateTodos=${args.annotateTodos ? "enabled" : "disabled"}`,
      `target=${args.codemodTarget}`,
      `readiness=${report.readiness}`,
      `byKind=${formatByKindMetrics(codemodResult.metrics.byKind)}`,
    ].join(" "),
  );
  process.stdout.write("\n");

  process.stdout.write(`gates=${formatGateResults(report.gates)}\n`);

  if (report.manualTodos.length > 0) {
    process.stdout.write("manualTodos:\n");
    for (const todo of report.manualTodos) {
      process.stdout.write(`- ${todo.file}:${todo.line}:${todo.column} [${todo.kind}] ${todo.reason}\n`);
    }
  }

  if (report.manualTodoReasons.length > 0) {
    process.stdout.write("manualReasons:\n");
    for (const reason of report.manualTodoReasons.slice(0, 5)) {
      process.stdout.write(`- ${reason.count}x [${reason.kinds.join(",")}] ${reason.reason}\n`);
    }
  }

  if (report.unhandledArcGisModules.length > 0) {
    process.stdout.write("unhandledArcGisModules:\n");
    for (const moduleItem of report.unhandledArcGisModules.slice(0, 10)) {
      process.stdout.write(`- ${moduleItem.modulePath} [${moduleItem.usageStyle}] (${moduleItem.count})\n`);
    }
  }

  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);

  if (args.reportPath) {
    fs.writeFileSync(args.reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stdout.write(`reportWritten=${args.reportPath}\n`);
  }

  const gateEvaluation = evaluateMigrationGates(report, {
    failOnManual: args.failOnManual,
    failOnUnhandled: args.failOnUnhandled,
    failOnBlocked: args.failOnBlocked,
    maxManualRatio: args.maxManualRatio,
    maxManualInterventionRatio: args.maxManualInterventionRatio,
  });
  if (gateEvaluation.failed) {
    process.stdout.write("gatingFailures:\n");
    for (const failure of gateEvaluation.failures) {
      process.stdout.write(`- ${failure}\n`);
    }
    process.exitCode = 2;
  }
}

async function runReconcile(args: ParsedArgs): Promise<void> {
  if (
    !args.sourceBaseUrl ||
    !args.sourceServiceId ||
    !args.targetBaseUrl ||
    !args.targetServiceId ||
    args.layerId === undefined
  ) {
    printUsage();
    process.exitCode = 1;
    return;
  }

  const report = await runLayerReconciliation({
    sourceBaseUrl: args.sourceBaseUrl,
    sourceServiceId: args.sourceServiceId,
    targetBaseUrl: args.targetBaseUrl,
    targetServiceId: args.targetServiceId,
    layerId: args.layerId,
    sampleSize: args.sampleSize,
  });

  process.stdout.write(`${summarizeLayerReconciliation(report)}\n`);
  process.stdout.write(
    `checks=${report.checks
      .map((check) => `${check.check}:${check.passed ? "pass" : "fail"}`)
      .join(",")}\n`,
  );
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);

  if (args.reportPath) {
    fs.writeFileSync(args.reportPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    process.stdout.write(`reportWritten=${args.reportPath}\n`);
  }

  if (!report.passed) {
    process.exitCode = 2;
  }
}

function parseArgs(argv: string[]): ParsedArgs | undefined {
  if (argv.length === 0) {
    return {
      command: "scan",
      target: process.cwd(),
      write: false,
      annotateTodos: false,
      failOnManual: false,
      failOnUnhandled: false,
      failOnBlocked: false,
      codemodTarget: "honua-compat",
    };
  }

  const maybeCommand = argv[0];
  const command: "scan" | "codemod" | "reconcile" | "matrix" | "fixtures" =
    maybeCommand === "scan" ||
    maybeCommand === "codemod" ||
    maybeCommand === "reconcile" ||
    maybeCommand === "matrix" ||
    maybeCommand === "fixtures"
      ? maybeCommand
      : "scan";
  const positional = command === maybeCommand ? argv.slice(1) : argv.slice(0);

  let target: string | undefined;
  let fixtureNames: string[] | undefined;
  let reportPath: string | undefined;
  let codemodTarget: CodemodTarget = "honua-compat";
  let write = false;
  let annotateTodos = false;
  let failOnManual = false;
  let failOnUnhandled = false;
  let failOnBlocked = false;
  let maxManualRatio: number | undefined;
  let maxManualInterventionRatio: number | undefined;
  let compatImportPath: string | undefined;
  let sourceBaseUrl: string | undefined;
  let sourceServiceId: string | undefined;
  let targetBaseUrl: string | undefined;
  let targetServiceId: string | undefined;
  let layerId: number | undefined;
  let sampleSize: number | undefined;

  for (let i = 0; i < positional.length; i += 1) {
    const token = positional[i];
    if (token === "--help" || token === "-h") {
      return undefined;
    }
    if (token === "--write") {
      write = true;
      continue;
    }
    if (token === "--annotate-todos") {
      annotateTodos = true;
      continue;
    }
    if (token === "--fail-on-manual") {
      failOnManual = true;
      continue;
    }
    if (token === "--fail-on-unhandled") {
      failOnUnhandled = true;
      continue;
    }
    if (token === "--fail-on-blocked") {
      failOnBlocked = true;
      continue;
    }
    if (token === "--max-manual-ratio") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }

      const parsedRatio = Number.parseFloat(next);
      if (!Number.isFinite(parsedRatio) || parsedRatio < 0 || parsedRatio > 1) {
        return undefined;
      }

      maxManualRatio = parsedRatio;
      i += 1;
      continue;
    }
    if (token === "--max-manual-intervention-ratio") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }

      const parsedRatio = Number.parseFloat(next);
      if (!Number.isFinite(parsedRatio) || parsedRatio < 0 || parsedRatio > 1) {
        return undefined;
      }

      maxManualInterventionRatio = parsedRatio;
      i += 1;
      continue;
    }
    if (token === "--report") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      reportPath = next;
      i += 1;
      continue;
    }
    if (token === "--compat-import-path") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      compatImportPath = next;
      i += 1;
      continue;
    }
    if (token === "--target") {
      const next = positional[i + 1];
      if (next !== "honua-compat" && next !== "esri-leaflet") {
        return undefined;
      }
      codemodTarget = next;
      i += 1;
      continue;
    }
    if (token === "--fixtures") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      const parsedFixtures = next
        .split(",")
        .map((fixture) => fixture.trim())
        .filter((fixture) => fixture.length > 0);
      if (parsedFixtures.length === 0) {
        return undefined;
      }
      fixtureNames = parsedFixtures;
      i += 1;
      continue;
    }
    if (token === "--source-base-url") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      sourceBaseUrl = next;
      i += 1;
      continue;
    }
    if (token === "--source-service-id") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      sourceServiceId = next;
      i += 1;
      continue;
    }
    if (token === "--target-base-url") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      targetBaseUrl = next;
      i += 1;
      continue;
    }
    if (token === "--target-service-id") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      targetServiceId = next;
      i += 1;
      continue;
    }
    if (token === "--layer-id") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      const parsedLayerId = Number.parseInt(next, 10);
      if (!Number.isFinite(parsedLayerId)) {
        return undefined;
      }
      layerId = parsedLayerId;
      i += 1;
      continue;
    }
    if (token === "--sample-size") {
      const next = positional[i + 1];
      if (!next) {
        return undefined;
      }
      const parsedSampleSize = Number.parseInt(next, 10);
      if (!Number.isFinite(parsedSampleSize) || parsedSampleSize <= 0) {
        return undefined;
      }
      sampleSize = parsedSampleSize;
      i += 1;
      continue;
    }
    if (token.startsWith("--")) {
      return undefined;
    }
    if (command === "reconcile") {
      return undefined;
    }
    if (!target) {
      target = token;
      continue;
    }
    return undefined;
  }

  return {
    command,
    target:
      target ??
      (command === "fixtures" ? path.join(process.cwd(), "test", "fixtures") : process.cwd()),
    write,
    codemodTarget,
    annotateTodos,
    failOnManual,
    failOnUnhandled,
    failOnBlocked,
    maxManualRatio,
    maxManualInterventionRatio,
    reportPath,
    compatImportPath,
    sourceBaseUrl,
    sourceServiceId,
    targetBaseUrl,
    targetServiceId,
    layerId,
    sampleSize,
    fixtureNames,
  };
}

function formatByKindMetrics(byKind: CodemodMetricsByKind): string {
  return (Object.keys(byKind) as Array<keyof CodemodMetricsByKind>)
    .map((kind) => {
      const metric = byKind[kind];
      return `${kind}:${metric.autoMigrated}/${metric.manual}/${metric.total}`;
    })
    .join(",");
}

function formatGateResults(gates: readonly { gate: string; passed: boolean }[]): string {
  return gates
    .map((gate) => `${gate.gate}:${gate.passed ? "pass" : "fail"}`)
    .join(",");
}

function printUsage(): void {
  process.stdout.write(
    [
      "Usage:",
      "  honua-migrate [scan] <path> [--report <file>]",
      "  honua-migrate codemod <path> [--target <honua-compat|esri-leaflet>] [--write] [--annotate-todos] [--report <file>] [--compat-import-path <pkg>] [--fail-on-manual] [--fail-on-unhandled] [--fail-on-blocked] [--max-manual-ratio <0..1>] [--max-manual-intervention-ratio <0..1>]",
      "  honua-migrate matrix [--report <file>]",
      "  honua-migrate fixtures [<fixtures-root>] [--target <honua-compat|esri-leaflet>] [--fixtures <name1,name2,...>] [--report <file>]",
      "  honua-migrate reconcile --source-base-url <url> --source-service-id <id> --target-base-url <url> --target-service-id <id> --layer-id <n> [--sample-size <n>] [--report <file>]",
      "",
      "Examples:",
      "  node dist/src/migration/cli.js scan ./src",
      "  node dist/src/migration/cli.js codemod ./src --write --annotate-todos --report migration-report.json",
      "  node dist/src/migration/cli.js codemod ./src --target esri-leaflet --write --report migration-report.json",
      "  node dist/src/migration/cli.js matrix --report parity-matrix.json",
      "  node dist/src/migration/cli.js fixtures --report real-sample-metrics.json",
      "  node dist/src/migration/cli.js codemod ./src --fail-on-manual --fail-on-unhandled --max-manual-ratio 0.2 --max-manual-intervention-ratio 0.3",
      "  node dist/src/migration/cli.js reconcile --source-base-url https://source.example --source-service-id parcels --target-base-url https://target.example --target-service-id parcels --layer-id 0 --sample-size 200 --report reconcile-report.json",
    ].join("\n"),
  );
  process.stdout.write("\n");
}
