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

function fixtureMainPath(projectRoot, fixtureName) {
  return path.join(projectRoot, "test", "fixtures", fixtureName, "src", "main.js");
}

function createTempRoot() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "honua-playwright-real-sample-"));
}

function createIndexHtml() {
  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <title>Honua Migration Real Sample Browser Smoke</title>
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

function startServer(projectRoot, appMain) {
  const distSourceRoot = path.join(projectRoot, "dist", "src");

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
    throw new Error("Failed to bind real sample smoke server.");
  }
  return `http://127.0.0.1:${address.port}`;
}

async function runMigratedFixtureBrowserSmoke(page, options) {
  const projectRoot = getProjectRoot();
  const fixtureFile = fixtureMainPath(projectRoot, options.fixtureName);
  const tempRoot = createTempRoot();
  const appRoot = path.join(tempRoot, "app");
  const appMain = path.join(appRoot, "main.js");

  fs.mkdirSync(appRoot, { recursive: true });
  fs.copyFileSync(fixtureFile, appMain);

  const { runEsriCompatCodemod } = await importCodemod(projectRoot);
  const codemodResult = runEsriCompatCodemod({
    rootDir: tempRoot,
    write: true,
    compatImportPath: "/compat-entry.js",
  });

  expect(codemodResult.filesChanged).toBe(1);
  expect(codemodResult.metrics.totalCodemodScopedCallSites).toBe(options.expectedCallSites);
  expect(codemodResult.metrics.autoMigratedCallSites).toBe(options.expectedCallSites);
  expect(codemodResult.metrics.manualCallSites).toBe(0);

  const pageErrors = [];
  page.on("pageerror", (error) => {
    pageErrors.push(error.message);
  });

  const server = await startServer(projectRoot, appMain);
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
    options.assertResult(migrationResult);
  } finally {
    await new Promise((resolve) => server.close(() => resolve(undefined)));
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
}

test("migrated complex ops-center sample executes in browser runtime", async ({ page }) => {
  await runMigratedFixtureBrowserSmoke(page, {
    fixtureName: "esri-real-sample-ops-center-app",
    expectedCallSites: 16,
    assertResult: (migrationResult) => {
      expect(migrationResult).toMatchObject({
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
      });
      expect(migrationResult.scaleText).toContain("1:");
      expect(migrationResult.scaleText).toContain("/");
    },
  });
});

test("migrated incident command demo sample executes in browser runtime", async ({ page }) => {
  await runMigratedFixtureBrowserSmoke(page, {
    fixtureName: "esri-real-sample-incident-command-app",
    expectedCallSites: 28,
    assertResult: (migrationResult) => {
      expect(migrationResult).toMatchObject({
        mapCtor: "MapCompat",
        viewCtor: "MapViewCompat",
        layerCtors: [
          "FeatureLayerCompat",
          "FeatureLayerCompat",
          "MapImageLayerCompat",
          "TileLayerCompat",
          "RouteLayerCompat",
        ],
        layerListActionTriggered: true,
        foundLayerId: "incidents-layer",
        popupSelectedId: "incident-2",
        selectedTemplateName: "Open Incident",
        formStatus: "active-response",
        routeTaskCount: 1,
        directionsStopCount: 2,
        activeBasemapId: "dark-gray",
        foundSublayerId: 2,
      });
      expect(migrationResult.uiCount).toBeGreaterThanOrEqual(19);
      expect(migrationResult.measuredDistanceMeters).toBeGreaterThan(0);
      expect(migrationResult.printUrl).toContain("https://example.test/print");
      expect(migrationResult.printUrl).toContain("title=Incident+Command+Board");
      expect(migrationResult.primaryConversionText).toContain(",");
    },
  });
});
