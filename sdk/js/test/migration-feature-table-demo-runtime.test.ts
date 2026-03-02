import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";
import { buildJsMigrationReport } from "../src/migration/report.js";
import { scanArcGisUsage } from "../src/migration/scanner.js";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-featuretable-demo-"));
  tempDirs.push(dir);
  return dir;
}

function fixturePath(...parts: string[]): string {
  const fixturesDir = fileURLToPath(new URL("./fixtures/", import.meta.url));
  return path.join(fixturesDir, ...parts);
}

function projectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function compatDistEntry(): string {
  return path.join(projectRoot(), "dist", "src", "esri-compat-entry.js");
}

function ensureBuiltCompatArtifacts(): void {
  if (builtOnce && fs.existsSync(compatDistEntry())) {
    return;
  }

  execSync("npm run build --silent", {
    cwd: projectRoot(),
    stdio: "pipe",
  });
  builtOnce = true;
}

async function migrateAndRunFixture(fixtureName: string): Promise<{
  codemodResult: ReturnType<typeof runEsriCompatCodemod>;
  report: ReturnType<typeof buildJsMigrationReport>;
  output: unknown;
}> {
  ensureBuiltCompatArtifacts();

  const tempRoot = makeTempDir();
  const sourceDir = fixturePath(fixtureName);
  const workingCopy = path.join(tempRoot, fixtureName);
  fs.cpSync(sourceDir, workingCopy, { recursive: true });

  const scanReport = scanArcGisUsage(workingCopy);
  const codemodResult = runEsriCompatCodemod({
    rootDir: workingCopy,
    write: true,
    compatImportPath: compatDistEntry(),
  });
  const report = buildJsMigrationReport(workingCopy, codemodResult, scanReport);

  const entry = path.join(workingCopy, "src", "main.js");
  const moduleUrl = `${pathToFileURL(entry).href}?cachebust=${Date.now()}`;
  const migrated = await import(moduleUrl);

  return {
    codemodResult,
    report,
    output: migrated.default,
  };
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("feature-table demo runtime", () => {
  it("migrates and executes primary related-records fixture with table/map and legend flows", async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-demo-feature-table-relates-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.manualRewriteMetric.ratio).toBe(0);
    expect(report.manualInterventionMetric.ratio).toBe(0);
    expect(report.unhandledArcGisModules).toEqual([]);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtors: ["FeatureLayerCompat", "FeatureLayerCompat"],
      tableSizeBeforeFilter: 3,
      tableSizeAfterFilter: 2,
      selectedObjectIds: [101],
      selectedRowCount: 1,
      popupSelectedId: "hydrant-101",
      popupAfterNextId: "hydrant-102",
      filterBySelectionEnabled: true,
      filterGeometryApplied: true,
      tableMapSyncOpened: true,
      relatedGroupCount: 1,
      relatedRecordCount: 2,
      layerActionTriggered: true,
    });

    if (!isRecord(output)) {
      throw new Error("Expected primary feature-table demo output to be an object.");
    }
    expect(output.layerListCount).toBeGreaterThanOrEqual(2);
    expect(output.legendLayerCount).toBeGreaterThanOrEqual(1);
    expect(output.legendEntryCount).toBeGreaterThanOrEqual(1);
    expect(output.widgetCtors).toEqual([
      "FeatureTableCompat",
      "PopupCompat",
      "LayerListCompat",
      "LegendCompat",
    ]);
  }, 60_000);

  it("migrates and executes fallback popup-interaction fixture", async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-demo-feature-table-popup-interaction-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.readiness).toBe("ready");
    expect(report.manualRewriteMetric.ratio).toBe(0);
    expect(report.manualInterventionMetric.ratio).toBe(0);
    expect(report.unhandledArcGisModules).toEqual([]);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtor: "FeatureLayerCompat",
      tableCtor: "FeatureTableCompat",
      popupCtor: "PopupCompat",
      tableSizeBeforeFilter: 3,
      tableSizeAfterFilter: 2,
      selectedObjectIds: [202],
      popupSelectedId: "incident-202",
      popupVisible: true,
      where: "priority = 'high'",
      filterBySelectionEnabled: true,
      tableMapSyncOpened: true,
    });
  }, 60_000);
});

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
