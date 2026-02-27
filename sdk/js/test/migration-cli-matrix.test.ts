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
    const graphic = report.matrix.find((row) => row.kind === "graphic");
    const point = report.matrix.find((row) => row.kind === "point-geometry");
    const polyline = report.matrix.find((row) => row.kind === "polyline-geometry");
    const polygon = report.matrix.find((row) => row.kind === "polygon-geometry");
    const extent = report.matrix.find((row) => row.kind === "extent-geometry");
    const spatialReference = report.matrix.find((row) => row.kind === "spatial-reference");
    const color = report.matrix.find((row) => row.kind === "color");
    const simpleLineSymbol = report.matrix.find((row) => row.kind === "simple-line-symbol");
    const simpleMarkerSymbol = report.matrix.find((row) => row.kind === "simple-marker-symbol");
    const pictureMarkerSymbol = report.matrix.find((row) => row.kind === "picture-marker-symbol");
    const textSymbol = report.matrix.find((row) => row.kind === "text-symbol");
    const labelClass = report.matrix.find((row) => row.kind === "label-class");
    const simpleFillSymbol = report.matrix.find((row) => row.kind === "simple-fill-symbol");
    const classBreaksRenderer = report.matrix.find((row) => row.kind === "class-breaks-renderer");
    const simpleRenderer = report.matrix.find((row) => row.kind === "simple-renderer");
    const uniqueValueRenderer = report.matrix.find((row) => row.kind === "unique-value-renderer");
    const basemap = report.matrix.find((row) => row.kind === "basemap");
    const track = report.matrix.find((row) => row.kind === "track-widget");
    const routeTask = report.matrix.find((row) => row.kind === "route-task");
    const swipe = report.matrix.find((row) => row.kind === "swipe-widget");
    const featureWidget = report.matrix.find((row) => row.kind === "feature-widget");
    const featureSet = report.matrix.find((row) => row.kind === "feature-set");
    const featureFormWidget = report.matrix.find((row) => row.kind === "feature-form-widget");
    const tableListWidget = report.matrix.find((row) => row.kind === "table-list-widget");
    const featureTemplatesWidget = report.matrix.find((row) => row.kind === "feature-templates-widget");
    const basemapLayerListWidget = report.matrix.find((row) => row.kind === "basemap-layer-list-widget");
    const distanceMeasurement2dWidget = report.matrix.find(
      (row) => row.kind === "distance-measurement-2d-widget",
    );
    const areaMeasurement2dWidget = report.matrix.find(
      (row) => row.kind === "area-measurement-2d-widget",
    );
    const query = report.matrix.find((row) => row.kind === "query");
    const oauthInfo = report.matrix.find((row) => row.kind === "oauth-info");
    const identityManager = report.matrix.find((row) => row.kind === "identity-manager");
    const esriRequest = report.matrix.find((row) => row.kind === "esri-request");
    const esriConfig = report.matrix.find((row) => row.kind === "esri-config");
    const reactiveUtils = report.matrix.find((row) => row.kind === "reactive-utils");
    expect(featureLayer).toMatchObject({ honuaCompat: "compat", esriLeaflet: "compat" });
    expect(graphic).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(point).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(polyline).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(polygon).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(extent).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(spatialReference).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(color).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(simpleLineSymbol).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(simpleMarkerSymbol).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(pictureMarkerSymbol).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(textSymbol).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(labelClass).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(simpleFillSymbol).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(classBreaksRenderer).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(simpleRenderer).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(uniqueValueRenderer).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(basemap).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(track).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(routeTask).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(swipe).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureSet).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureFormWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(tableListWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(featureTemplatesWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(basemapLayerListWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(distanceMeasurement2dWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(areaMeasurement2dWidget).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(query).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(oauthInfo).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(identityManager).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(esriRequest).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(esriConfig).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(reactiveUtils).toMatchObject({ honuaCompat: "compat", esriLeaflet: "assisted" });
    expect(report.summary.honuaCompat.compat).toBeGreaterThan(0);
    expect(report.summary.esriLeaflet.assisted).toBeGreaterThan(0);
  }, 60_000);
});
