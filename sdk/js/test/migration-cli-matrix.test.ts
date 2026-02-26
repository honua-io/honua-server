import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-cli-matrix-"));
  tempDirs.push(dir);
  return dir;
}

function getProjectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function ensureBuiltCliArtifacts(): void {
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
}

function runCli(args: readonly string[], cwd: string): { status: number | null; stdout: string; stderr: string } {
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
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration cli parity matrix", () => {
  it("prints and writes the parity matrix artifact", () => {
    ensureBuiltCliArtifacts();
    const root = makeTempDir();
    const reportPath = path.join(root, "parity.json");

    const result = runCli(["matrix", "--report", reportPath], getProjectRoot());

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("entries=");
    expect(result.stdout).toContain("esriLeafletCompat=");
    expect(result.stdout).toContain(`reportWritten=${reportPath}`);
    expect(fs.existsSync(reportPath)).toBe(true);

    const report = JSON.parse(fs.readFileSync(reportPath, "utf8")) as {
      summary: {
        honuaCompat: Record<string, number>;
        esriLeaflet: Record<string, number>;
      };
      matrix: Array<{
        kind: string;
        honuaCompat: string;
        esriLeaflet: string;
      }>;
    };

    const featureLayer = report.matrix.find((row) => row.kind === "feature-layer");
    const track = report.matrix.find((row) => row.kind === "track-widget");
    const routeTask = report.matrix.find((row) => row.kind === "route-task");
    const swipe = report.matrix.find((row) => row.kind === "swipe-widget");
    const featureWidget = report.matrix.find((row) => row.kind === "feature-widget");
    const featureFormWidget = report.matrix.find((row) => row.kind === "feature-form-widget");
    const tableListWidget = report.matrix.find((row) => row.kind === "table-list-widget");
    const featureTemplatesWidget = report.matrix.find((row) => row.kind === "feature-templates-widget");
    const basemapLayerListWidget = report.matrix.find((row) => row.kind === "basemap-layer-list-widget");
    expect(featureLayer).toMatchObject({ honuaCompat: "compat", esriLeaflet: "compat" });
    expect(track).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(routeTask).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(swipe).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureFormWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(tableListWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureTemplatesWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(basemapLayerListWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(report.summary.honuaCompat.compat).toBeGreaterThan(0);
    expect(report.summary.esriLeaflet.assisted).toBeGreaterThan(0);
  }, 20_000);
});
