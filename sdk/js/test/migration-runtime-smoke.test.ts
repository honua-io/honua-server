import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-runtime-smoke-"));
  tempDirs.push(dir);
  return dir;
}

function getProjectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function getCompatDistEntry(): string {
  return path.join(getProjectRoot(), "dist", "src", "esri-compat-entry.js");
}

function ensureBuiltCompatArtifacts(): void {
  if (builtOnce && fs.existsSync(getCompatDistEntry())) {
    return;
  }

  execSync("npm run build --silent", {
    cwd: getProjectRoot(),
    stdio: "pipe",
  });
  builtOnce = true;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration runtime smoke", () => {
  it("executes migrated constructor flow with compat runtime imports", { timeout: 20_000 }, async () => {
    ensureBuiltCompatArtifacts();
    const tempRoot = makeTempDir();
    const file = path.join(tempRoot, "main.js");
    const compatEntryPath = getCompatDistEntry();

    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "const featureLayer = new FeatureLayer({ url: 'https://example.test/rest/services/default/FeatureServer/0' });",
        "const mapImage = new MapImageLayer({ url: 'https://example.test/rest/services/default/MapServer' });",
        "const map = new Map({ basemap: 'streets', layers: [featureLayer, mapImage] });",
        "export default {",
        "  mapCtor: map.constructor.name,",
        "  featureLayerCtor: featureLayer.constructor.name,",
        "  mapImageCtor: mapImage.constructor.name,",
        "  layerCount: map.layers.length,",
        "};",
      ].join("\n"),
      "utf8",
    );

    const codemodResult = runEsriCompatCodemod({
      rootDir: tempRoot,
      write: true,
      compatImportPath: compatEntryPath,
    });
    expect(codemodResult.filesChanged).toBe(1);
    expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
    expect(codemodResult.metrics.manualCallSites).toBe(0);

    const migrated = await import(pathToFileURL(file).href);
    expect(migrated.default).toEqual({
      mapCtor: "MapCompat",
      featureLayerCtor: "FeatureLayerCompat",
      mapImageCtor: "MapImageLayerCompat",
      layerCount: 2,
    });
  });
});
