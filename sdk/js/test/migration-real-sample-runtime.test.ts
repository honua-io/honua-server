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
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-real-sample-"));
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

describe("migration real sample runtime", () => {
  it("migrates and executes ops center sample", { timeout: 60_000 }, async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-real-sample-ops-center-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(16);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(16);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 16,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.gates.find((gate) => gate.gate === "no-manual-todos")?.passed).toBe(true);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")?.passed).toBe(true);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtor: "FeatureLayerCompat",
      uiCount: 13,
      popupBefore: { id: "parcel-1" },
      popupAfterNext: { id: "parcel-2" },
      toggledBasemapId: "satellite",
      locateLongitude: -157.857,
      locateLatitude: 21.307,
      searchResultCount: 2,
      searchSelectedResult: "Parcel honua-B",
      widgetCtors: [
        "LayerListCompat",
        "LegendCompat",
        "PopupCompat",
        "HomeCompat",
        "BasemapToggleCompat",
        "LocateCompat",
        "ScaleBarCompat",
        "SearchCompat",
        "ExpandCompat",
        "BookmarksCompat",
        "FullscreenCompat",
        "ZoomCompat",
        "AttributionCompat",
      ],
    });

    if (!isRecord(output) || typeof output.scaleText !== "string") {
      throw new Error("Expected ops center sample to return a scaleText string.");
    }
    expect(output.scaleText).toContain("1:");
    expect(output.scaleText).toContain("/");
  });

  it("migrates and executes editing workflow sample", { timeout: 60_000 }, async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-real-sample-editing-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(13);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(13);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 13,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.gates.find((gate) => gate.gate === "no-manual-todos")?.passed).toBe(true);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")?.passed).toBe(true);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtor: "FeatureLayerCompat",
      uiCount: 10,
      mapTableCount: 2,
      selectedTemplateName: "Open",
      formStatus: "approved",
      sketchState: "ready",
      sketchCompleteState: "complete",
      createWorkflowStarted: true,
      updateWorkflowStarted: true,
      trackedLongitude: -157.857,
      trackedLatitude: 21.307,
      swipePosition: 65,
      widgetCtors: [
        "FeatureCompat",
        "FeatureFormCompat",
        "FeatureTemplatesCompat",
        "TableListCompat",
        "SketchCompat",
        "EditorCompat",
        "TrackCompat",
        "MeasurementCompat",
        "TimeSliderCompat",
        "SwipeCompat",
      ],
    });

    if (!isRecord(output) || typeof output.measuredDistance !== "number") {
      throw new Error("Expected editing sample to return numeric measuredDistance.");
    }
    expect(output.measuredDistance).toBeGreaterThan(0);
    expect(output.nextExtentEnd).toBeInstanceOf(Date);
  });

  it("migrates and executes network workflow sample", { timeout: 60_000 }, async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-real-sample-network-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(10);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(10);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 10,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.gates.find((gate) => gate.gate === "no-manual-todos")?.passed).toBe(true);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")?.passed).toBe(true);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtors: ["MapImageLayerCompat", "TileLayerCompat", "RouteLayerCompat"],
      widgetCtors: ["DirectionsCompat", "CoordinateConversionCompat", "PrintCompat"],
      uiCount: 3,
      routeTaskCtor: "RouteTaskCompat",
      queryCtor: "QueryCompat",
      routeTaskCount: 1,
      directionsStopCount: 2,
      coordinateFormats: ["lonlat", "dms"],
      queryWhere: "status = 'active'",
    });

    if (!isRecord(output)) {
      throw new Error("Expected network sample output to be an object.");
    }
    expect(output.routeLayerPathPoints).toBeGreaterThan(1);
    expect(output.directionsPathPoints).toBeGreaterThan(1);
    expect(output.directionsDistanceMeters).toBeGreaterThan(0);
    expect(String(output.coordinateText)).toContain(",");
    expect(String(output.printUrl)).toContain("https://example.test/print");
    expect(String(output.printUrl)).toContain("title=Network+Demo");
  });

  it("migrates and executes incident command sample", { timeout: 60_000 }, async () => {
    const { codemodResult, report, output } = await migrateAndRunFixture(
      "esri-real-sample-incident-command-app",
    );

    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(28);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(28);
    expect(codemodResult.metrics.manualCallSites).toBe(0);
    expect(codemodResult.manualTodos).toEqual([]);
    expect(report.manualInterventionMetric).toMatchObject({
      numerator: 0,
      denominator: 28,
      ratio: 0,
      manualCodemodCallSites: 0,
      unhandledUsageHits: 0,
    });
    expect(report.gates.find((gate) => gate.gate === "no-manual-todos")?.passed).toBe(true);
    expect(report.gates.find((gate) => gate.gate === "no-unhandled-modules")?.passed).toBe(true);

    expect(output).toMatchObject({
      mapCtor: "MapCompat",
      viewCtor: "MapViewCompat",
      layerCtors: [
        "FeatureLayerCompat",
        "FeatureLayerCompat",
        "MapImageLayerCompat",
        "TileLayerCompat",
        "RouteLayerCompat",
      ],
      widgetCtors: [
        "LayerListCompat",
        "LegendCompat",
        "PopupCompat",
        "SearchCompat",
        "ExpandCompat",
        "BookmarksCompat",
        "FeatureFormCompat",
        "FeatureTemplatesCompat",
        "FeatureTableCompat",
        "SketchCompat",
        "EditorCompat",
        "TrackCompat",
        "MeasurementCompat",
        "TimeSliderCompat",
        "DirectionsCompat",
        "CoordinateConversionCompat",
        "PrintCompat",
        "BasemapGalleryCompat",
        "BasemapLayerListCompat",
      ],
      layerListActionTriggered: true,
      foundLayerId: "incidents-layer",
      popupSelectedId: "incident-2",
      selectedTemplateName: "Open Incident",
      formStatus: "active-response",
      highlightCount: 2,
      sketchState: "ready",
      sketchCompletionState: "complete",
      createWorkflowStarted: true,
      updateWorkflowStarted: true,
      directionsStopCount: 2,
      queryWhere: "priority = 'high'",
      activeBasemapId: "dark-gray",
      foundSublayerId: 2,
      searchResultCount: 2,
      searchSelectedResult: "Incident IC-B",
    });

    if (!isRecord(output)) {
      throw new Error("Expected incident command sample output to be an object.");
    }
    expect(output.uiCount).toBeGreaterThanOrEqual(19);
    expect(output.layerListCount).toBeGreaterThanOrEqual(4);
    expect(output.trackedLatitude).toBeCloseTo(21.3075, 4);
    expect(output.trackedLongitude).toBeCloseTo(-157.8565, 4);
    expect(output.measuredDistanceMeters).toBeGreaterThan(0);
    expect(output.routeTaskCount).toBe(1);
    expect(output.routeTaskDistance).toBeGreaterThan(0);
    expect(output.directionsPathPoints).toBeGreaterThan(1);
    expect(output.basemapBaseLayerCount).toBe(1);
    expect(output.sublayerCount).toBe(2);
    expect(Array.isArray(output.conversions)).toBe(true);
    expect(output.conversions).toEqual(["lonlat", "dms"]);
    expect(String(output.primaryConversionText)).toContain(",");
    expect(String(output.printUrl)).toContain("https://example.test/print");
    expect(String(output.printUrl)).toContain("title=Incident+Command+Board");
  });
});

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
