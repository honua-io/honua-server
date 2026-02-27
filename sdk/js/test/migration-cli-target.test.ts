import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-cli-target-"));
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

describe("migration cli target selection", () => {
  it("runs codemod with --target esri-leaflet and emits report/manual TODOs", () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();
    const appFile = path.join(root, "app.ts");
    const reportPath = path.join(root, "report.json");

    fs.writeFileSync(
      appFile,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import Map from '@arcgis/core/Map';",
        "const layer = new FeatureLayer({ url: serviceUrl });",
        "const map = new Map({ basemap: 'streets' });",
        "void layer; void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runCli(
      ["codemod", root, "--target", "esri-leaflet", "--write", "--annotate-todos", "--report", reportPath],
      getProjectRoot(),
    );

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("target=esri-leaflet");
    expect(result.stdout).toContain("manual=1");
    expect(fs.existsSync(reportPath)).toBe(true);

    const migrated = fs.readFileSync(appFile, "utf8");
    expect(migrated).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(migrated).toContain("const layer = HonuaEsriLeaflet.featureLayer({ url: serviceUrl });");
    expect(migrated).toContain("const map = new Map({ basemap: 'streets' });");
    expect(migrated).toContain("// TODO(honua-migrate)[map]:");

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8")) as {
      readiness: string;
      manualTodos: Array<{ kind: string; reason: string }>;
      unhandledArcGisModules: Array<{ modulePath: string; usageStyle: string; count: number }>;
    };
    expect(report.readiness).toBe("assisted");
    expect(report.manualTodos).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "map",
        }),
      ]),
    );
    expect(report.manualTodos.some((todo) => todo.reason.includes("esri-leaflet mapping"))).toBe(true);
    expect(report.unhandledArcGisModules).toEqual(
      expect.arrayContaining([
        {
          modulePath: "@arcgis/core/Map",
          usageStyle: "static-import",
          count: 1,
        },
      ]),
    );
  }, 60_000);

  it("fails fast for invalid --target values", () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();

    const result = runCli(["codemod", root, "--target", "not-a-target"], getProjectRoot());

    expect(result.status).toBe(1);
    expect(result.stdout).toContain("Usage:");
  });
});
