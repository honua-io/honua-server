#!/usr/bin/env node

import fs from "node:fs";
import { scanArcGisUsage, summarizeArcGisScan } from "./scanner.js";
import { runEsriCompatCodemod, type CodemodMetricsByKind } from "./codemod.js";
import { buildJsMigrationReport } from "./report.js";
import { evaluateMigrationGates } from "./gating.js";

interface ParsedArgs {
  command: "scan" | "codemod";
  target: string;
  write: boolean;
  annotateTodos: boolean;
  failOnManual: boolean;
  failOnUnhandled: boolean;
  failOnBlocked: boolean;
  maxManualRatio?: number;
  maxManualInterventionRatio?: number;
  reportPath?: string;
  compatImportPath?: string;
}

const parsed = parseArgs(process.argv.slice(2));
if (!parsed) {
  printUsage();
  process.exitCode = 1;
} else {
  if (parsed.command === "scan") {
    runScan(parsed.target, parsed.reportPath);
  } else {
    runCodemod(parsed);
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

function runCodemod(args: ParsedArgs): void {
  const scanReport = scanArcGisUsage(args.target);
  const codemodResult = runEsriCompatCodemod({
    rootDir: args.target,
    write: args.write,
    compatImportPath: args.compatImportPath,
    annotateTodos: args.annotateTodos,
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
    };
  }

  const maybeCommand = argv[0];
  const command: "scan" | "codemod" =
    maybeCommand === "scan" || maybeCommand === "codemod" ? maybeCommand : "scan";
  const positional = command === maybeCommand ? argv.slice(1) : argv.slice(0);

  let target: string | undefined;
  let reportPath: string | undefined;
  let write = false;
  let annotateTodos = false;
  let failOnManual = false;
  let failOnUnhandled = false;
  let failOnBlocked = false;
  let maxManualRatio: number | undefined;
  let maxManualInterventionRatio: number | undefined;
  let compatImportPath: string | undefined;

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
    if (token.startsWith("--")) {
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
    target: target ?? process.cwd(),
    write,
    annotateTodos,
    failOnManual,
    failOnUnhandled,
    failOnBlocked,
    maxManualRatio,
    maxManualInterventionRatio,
    reportPath,
    compatImportPath,
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
      "  honua-migrate codemod <path> [--write] [--annotate-todos] [--report <file>] [--compat-import-path <pkg>] [--fail-on-manual] [--fail-on-unhandled] [--fail-on-blocked] [--max-manual-ratio <0..1>] [--max-manual-intervention-ratio <0..1>]",
      "",
      "Examples:",
      "  node dist/src/migration/cli.js scan ./src",
      "  node dist/src/migration/cli.js codemod ./src --write --annotate-todos --report migration-report.json",
      "  node dist/src/migration/cli.js codemod ./src --fail-on-manual --fail-on-unhandled --max-manual-ratio 0.2 --max-manual-intervention-ratio 0.3",
    ].join("\n"),
  );
  process.stdout.write("\n");
}
