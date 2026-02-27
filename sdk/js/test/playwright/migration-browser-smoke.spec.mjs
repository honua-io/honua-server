import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { expect, test } from "@playwright/test";

function getProjectRoot() {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
}

async function importCodemod(projectRoot) {
  const codemodPath = path.join(projectRoot, "dist", "src", "migration", "codemod.js");
  return import(pathToFileURL(codemodPath).href);
}

function createTempRoot() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "honua-playwright-smoke-"));
}

function writeFixtureApp(appRoot) {
  fs.mkdirSync(appRoot, { recursive: true });
  const appFile = path.join(appRoot, "main.js");
  fs.writeFileSync(
    appFile,
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
}

function createIndexHtml() {
  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>Honua Migration Browser Smoke</title>
  </head>
  <body>
    <script type="module">
      window.__migrationDone = false;
      window.__migrationResult = null;
      window.__migrationError = null;

      import("/app/main.js")
        .then((mod) => {
          window.__migrationResult = mod.default;
          window.__migrationDone = true;
        })
        .catch((error) => {
          window.__migrationError = String(error);
          window.__migrationDone = true;
          console.error(error);
        });
    </script>
  </body>
</html>`;
}

function startServer(projectRoot, tempRoot) {
  const distSourceRoot = path.join(projectRoot, "dist", "src");
  const appMain = path.join(tempRoot, "app", "main.js");

  const server = http.createServer((req, res) => {
    const requestUrl = new URL(req.url ?? "/", "http://127.0.0.1");
    if (requestUrl.pathname === "/") {
      res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
      res.end(createIndexHtml());
      return;
    }

    if (requestUrl.pathname === "/app/main.js") {
      res.writeHead(200, { "content-type": "text/javascript; charset=utf-8" });
      res.end(fs.readFileSync(appMain, "utf8"));
      return;
    }

    if (requestUrl.pathname === "/compat-entry.js") {
      const compatEntry = path.join(distSourceRoot, "esri-compat-entry.js");
      res.writeHead(200, { "content-type": "text/javascript; charset=utf-8" });
      res.end(fs.readFileSync(compatEntry, "utf8"));
      return;
    }

    const distModulePath = path.join(distSourceRoot, requestUrl.pathname.slice(1));
    if (distModulePath.startsWith(distSourceRoot) && fs.existsSync(distModulePath)) {
      res.writeHead(200, { "content-type": "text/javascript; charset=utf-8" });
      res.end(fs.readFileSync(distModulePath, "utf8"));
      return;
    }

    res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
    res.end("Not found");
  });

  return new Promise((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      resolve(server);
    });
  });
}

async function getServerUrl(server) {
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("Failed to bind migration smoke server.");
  }
  return `http://127.0.0.1:${address.port}`;
}

test("migrated arcgis sample executes in real browser runtime", async ({ page }) => {
  const projectRoot = getProjectRoot();
  const tempRoot = createTempRoot();
  const appRoot = path.join(tempRoot, "app");
  writeFixtureApp(appRoot);

  const { runEsriCompatCodemod } = await importCodemod(projectRoot);
  const codemodResult = runEsriCompatCodemod({
    rootDir: tempRoot,
    write: true,
    compatImportPath: "/compat-entry.js",
  });

  expect(codemodResult.filesChanged).toBe(1);
  expect(codemodResult.metrics.autoMigratedCallSites).toBe(3);
  expect(codemodResult.metrics.manualCallSites).toBe(0);

  const pageErrors = [];
  page.on("pageerror", (error) => {
    pageErrors.push(error.message);
  });

  const server = await startServer(projectRoot, tempRoot);
  try {
    const serverUrl = await getServerUrl(server);
    await page.goto(serverUrl);

    await expect
      .poll(async () => page.evaluate(() => window.__migrationDone === true))
      .toBe(true);

    const migrationError = await page.evaluate(() => window.__migrationError);
    const migrationResult = await page.evaluate(() => window.__migrationResult);

    expect(migrationError).toBeNull();
    expect(pageErrors).toEqual([]);
    expect(migrationResult).toEqual({
      mapCtor: "MapCompat",
      featureLayerCtor: "FeatureLayerCompat",
      mapImageCtor: "MapImageLayerCompat",
      layerCount: 2,
    });
  } finally {
    await new Promise((resolve) => server.close(() => resolve(undefined)));
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
});
