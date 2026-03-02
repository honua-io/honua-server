import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-cli-demo-"));
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

describe("migration cli demo", () => {
  it("runs codemod-only demo mode and writes report", { timeout: 60_000 }, () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();
    const outputDir = path.join(root, "output");
    const reportPath = path.join(root, "demo-report.json");

    const result = runCli(
      [
        "demo",
        "--fixtures-root",
        path.join(getProjectRoot(), "test", "fixtures"),
        "--fixture",
        "esri-demo-feature-table-relates-app",
        "--output-dir",
        outputDir,
        "--skip-import",
        "--skip-reconcile",
        "--report",
        reportPath,
      ],
      getProjectRoot(),
    );

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("demoStage=import skipped=yes");
    expect(result.stdout).toContain("demoStage=codemod");
    expect(result.stdout).toContain("demoStage=reconcile skipped=yes");
    expect(result.stdout).toContain("demoPassed=yes");
    expect(result.stdout).toContain(`reportWritten=${reportPath}`);

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8")) as {
      passed: boolean;
      import?: unknown;
      reconciliation?: unknown;
      migration: {
        readiness: string;
      };
      workingAppDir: string;
    };

    expect(report.passed).toBe(true);
    expect(report.import).toBeUndefined();
    expect(report.reconciliation).toBeUndefined();
    expect(report.migration.readiness).toBe("ready");
    expect(fs.existsSync(path.join(report.workingAppDir, "src", "main.js"))).toBe(true);
  });
});
