import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-cli-fixtures-"));
  tempDirs.push(dir);
  return dir;
}

function getProjectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function sleep(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function withCliLock<T>(work: () => T): T {
  const lockDir = path.join(getProjectRoot(), ".tmp", "vitest-cli-lock");
  fs.mkdirSync(path.dirname(lockDir), { recursive: true });
  for (;;) {
    try {
      fs.mkdirSync(lockDir);
      break;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== "EEXIST") {
        throw error;
      }
      sleep(25);
    }
  }

  try {
    return work();
  } finally {
    fs.rmSync(lockDir, { recursive: true, force: true });
  }
}

function ensureBuiltCliArtifacts(): void {
  withCliLock(() => {
    const cliPath = path.join(getProjectRoot(), "dist", "src", "migration", "cli.js");
    if (builtOnce && fs.existsSync(cliPath)) {
      return;
    }

    const buildResult = spawnSync("npm", ["run", "build", "--silent"], {
      cwd: getProjectRoot(),
      encoding: "utf8",
    });
    if (buildResult.status !== 0) {
      throw new Error(buildResult.stderr || buildResult.stdout || "failed to build migration CLI");
    }
    builtOnce = true;
  });
}

function runCli(args: readonly string[], cwd: string): { status: number | null; stdout: string; stderr: string } {
  return withCliLock(() => {
    const cliPath = path.join(getProjectRoot(), "dist", "src", "migration", "cli.js");
    const result = spawnSync("node", [cliPath, ...args], {
      cwd,
      encoding: "utf8",
    });

    return {
      status: result.status,
      stdout: result.stdout,
      stderr: result.stderr,
    };
  });
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration cli fixtures metrics", () => {
  it("prints and writes real-sample fixture metrics", () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();
    const reportPath = path.join(root, "real-sample-metrics.json");

    const result = runCli(["fixtures", "--report", reportPath], getProjectRoot());

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("fixtures=4");
    expect(result.stdout).toContain("target=honua-compat");
    expect(result.stdout).toContain(`reportWritten=${reportPath}`);
    expect(fs.existsSync(reportPath)).toBe(true);

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8")) as {
      codemodTarget: string;
      fixtureNames: string[];
      summary: {
        fixtureCount: number;
        totalCallSites: number;
        autoMigratedCallSites: number;
        manualCallSites: number;
        unhandledUsageHits: number;
      };
      gates: {
        passed: boolean;
        failures: string[];
      };
      fixtures: Array<{
        fixture: string;
        readiness: string;
        totalCallSites: number;
        autoMigratedCallSites: number;
        manualCallSites: number;
      }>;
    };

    expect(report.codemodTarget).toBe("honua-compat");
    expect(report.summary.fixtureCount).toBe(4);
    expect(report.fixtureNames).toEqual([
      "esri-real-sample-incident-command-app",
      "esri-real-sample-ops-center-app",
      "esri-real-sample-editing-app",
      "esri-real-sample-network-app",
    ]);
    expect(report.summary.totalCallSites).toBeGreaterThan(0);
    expect(report.summary.autoMigratedCallSites).toBe(report.summary.totalCallSites);
    expect(report.summary.manualCallSites).toBe(0);
    expect(report.summary.unhandledUsageHits).toBe(0);
    expect(report.gates.passed).toBe(true);
    expect(report.gates.failures).toEqual([]);
    expect(report.fixtures).toHaveLength(4);
    expect(report.fixtures.every((fixture) => fixture.readiness === "ready")).toBe(true);
    expect(report.fixtures.every((fixture) => fixture.manualCallSites === 0)).toBe(true);
  }, 60_000);

  it("supports fixture subset selection", () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();
    const reportPath = path.join(root, "subset-metrics.json");

    const result = runCli(
      [
        "fixtures",
        "--target",
        "esri-leaflet",
        "--fixtures",
        "esri-real-sample-network-app",
        "--report",
        reportPath,
      ],
      getProjectRoot(),
    );

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("fixtures=1");
    expect(result.stdout).toContain("target=esri-leaflet");
    expect(fs.existsSync(reportPath)).toBe(true);

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8")) as {
      codemodTarget: string;
      fixtureNames: string[];
      summary: {
        fixtureCount: number;
      };
      fixtures: Array<{
        fixture: string;
      }>;
    };

    expect(report.codemodTarget).toBe("esri-leaflet");
    expect(report.summary.fixtureCount).toBe(1);
    expect(report.fixtureNames).toEqual(["esri-real-sample-network-app"]);
    expect(report.fixtures).toEqual([
      expect.objectContaining({ fixture: "esri-real-sample-network-app" }),
    ]);
  }, 60_000);

  it("passes strict fixture gates for honua-compat target", () => {
    ensureBuiltCliArtifacts();

    const result = runCli(
      [
        "fixtures",
        "--fail-on-manual",
        "--fail-on-unhandled",
        "--fail-on-blocked",
        "--max-manual-ratio",
        "0",
        "--max-manual-intervention-ratio",
        "0",
      ],
      getProjectRoot(),
    );

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("fixturesGate=pass");
  }, 60_000);

  it("fails fixture gates when manual migration remains", () => {
    ensureBuiltCliArtifacts();

    const result = runCli(
      [
        "fixtures",
        "--target",
        "esri-leaflet",
        "--fixtures",
        "esri-real-sample-network-app",
        "--fail-on-manual",
        "--max-manual-ratio",
        "0",
      ],
      getProjectRoot(),
    );

    expect(result.status).toBe(2);
    expect(result.stdout).toContain("fixturesGate=fail");
    expect(result.stdout).toContain("gatingFailures:");
    expect(result.stdout).toContain("Manual call sites detected");
  }, 60_000);
});
