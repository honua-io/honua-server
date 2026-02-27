import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

const tempDirs: string[] = [];
let builtOnce = false;

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-dual-protocol-"));
  tempDirs.push(dir);
  return dir;
}

function projectRoot(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
}

function fixturePath(...parts: string[]): string {
  const fixturesDir = fileURLToPath(new URL("./fixtures/", import.meta.url));
  return path.join(fixturesDir, ...parts);
}

function honuaDistEntry(): string {
  return path.join(projectRoot(), "dist", "src", "honua.js");
}

function ensureBuiltHonuaArtifacts(): void {
  if (builtOnce && fs.existsSync(honuaDistEntry())) {
    return;
  }

  execSync("npm run build --silent", {
    cwd: projectRoot(),
    stdio: "pipe",
  });
  builtOnce = true;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json",
    },
  });
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
  delete (globalThis as Record<string, unknown>).__honuaFetchFn;
});

describe("honua dual protocol runtime", () => {
  it("executes one app flow that mixes Esri REST and OGC Features surfaces", { timeout: 60_000 }, async () => {
    ensureBuiltHonuaArtifacts();

    const tempRoot = makeTempDir();
    const sourceDir = fixturePath("honua-dual-protocol-app");
    const workingCopy = path.join(tempRoot, "honua-dual-protocol-app");
    fs.cpSync(sourceDir, workingCopy, { recursive: true });

    const entryPath = path.join(workingCopy, "src", "main.js");
    const source = fs.readFileSync(entryPath, "utf8");
    const honuaEntryHref = pathToFileURL(honuaDistEntry()).href;
    fs.writeFileSync(
      entryPath,
      source.replace("__HONUA_ENTRY__", honuaEntryHref),
      "utf8",
    );

    const requests: Array<{ url: string; method: string; body?: string }> = [];
    (globalThis as Record<string, unknown>).__honuaFetchFn = async (
      input: string | URL | Request,
      init?: RequestInit,
    ): Promise<Response> => {
      const requestUrl = new URL(
        typeof input === "string" ? input : input instanceof URL ? input.href : input.url,
      );
      const method = String(init?.method ?? "GET").toUpperCase();
      requests.push({
        url: requestUrl.href,
        method,
        body: typeof init?.body === "string" ? init.body : undefined,
      });

      if (requestUrl.pathname === "/rest/services/transport/FeatureServer" && method === "GET") {
        return jsonResponse({
          serviceDescription: "Transport Service",
          layers: [{ id: 0, name: "Parcels" }],
        });
      }

      if (requestUrl.pathname === "/rest/services/transport/FeatureServer/0" && method === "GET") {
        return jsonResponse({
          id: 0,
          name: "Parcels",
          fields: [{ name: "OBJECTID", type: "esriFieldTypeOID" }],
        });
      }

      if (requestUrl.pathname === "/rest/services/transport/FeatureServer/0/query" && method === "GET") {
        if (requestUrl.searchParams.get("returnCountOnly") === "true") {
          return jsonResponse({ count: 3 });
        }
        return jsonResponse({
          features: [
            { attributes: { OBJECTID: 1, NAME: "A" } },
            { attributes: { OBJECTID: 2, NAME: "B" } },
            { attributes: { OBJECTID: 3, NAME: "C" } },
          ],
        });
      }

      if (requestUrl.pathname === "/rest/services/transport/MapServer/legend" && method === "GET") {
        return jsonResponse({
          layers: [
            { layerId: 0, legend: [{ label: "Active", url: "legend-0.png" }] },
            { layerId: 1, legend: [{ label: "Inactive", url: "legend-1.png" }] },
          ],
        });
      }

      if (requestUrl.pathname === "/ogc/features" && method === "GET") {
        return jsonResponse({
          title: "Honua OGC API Features",
        });
      }

      if (requestUrl.pathname === "/ogc/features/collections" && method === "GET") {
        return jsonResponse({
          collections: [{ id: "parcels" }, { id: "roads" }],
        });
      }

      if (requestUrl.pathname === "/ogc/features/collections/parcels/items" && method === "GET") {
        return jsonResponse({
          type: "FeatureCollection",
          features: [
            { id: "parcel-1", type: "Feature", properties: { status: "active" }, geometry: null },
            { id: "parcel-2", type: "Feature", properties: { status: "active" }, geometry: null },
          ],
        });
      }

      if (requestUrl.pathname === "/ogc/features/collections/parcels/items/parcel-1" && method === "GET") {
        return jsonResponse({
          id: "parcel-1",
          type: "Feature",
          properties: { status: "active" },
          geometry: null,
        });
      }

      if (requestUrl.pathname === "/ogc/features/collections/parcels/items" && method === "POST") {
        return jsonResponse({
          id: "parcel-3",
          status: "created",
        });
      }

      if (requestUrl.pathname === "/ogc/features/collections/parcels/items/parcel-3" && method === "DELETE") {
        return jsonResponse({
          status: "deleted",
        });
      }

      return jsonResponse(
        {
          message: `Unhandled request in dual protocol fixture: ${method} ${requestUrl.pathname}`,
        },
        404,
      );
    };

    const moduleUrl = `${pathToFileURL(entryPath).href}?cachebust=${Date.now()}`;
    const runtime = await import(moduleUrl);

    expect(runtime.default).toEqual({
      serviceDescription: "Transport Service",
      layerName: "Parcels",
      layerQueryCount: 3,
      layerCount: 3,
      legendLayerCount: 2,
      serviceRequestCount: 3,
      ogcTitle: "Honua OGC API Features",
      ogcCollectionCount: 2,
      ogcItemsCount: 2,
      ogcFirstItemId: "parcel-1",
      ogcSingleId: "parcel-1",
      ogcCreatedId: "parcel-3",
      ogcDeleteStatus: "deleted",
    });

    expect(
      requests.some((request) =>
        request.url.includes("/rest/services/transport/FeatureServer/0/query"),
      ),
    ).toBe(true);
    expect(
      requests.some((request) => request.url.includes("/rest/services/transport/MapServer/legend")),
    ).toBe(true);
    expect(
      requests.some((request) => request.url.includes("/ogc/features/collections/parcels/items?")),
    ).toBe(true);

    const createCall = requests.find(
      (request) =>
        request.method === "POST" &&
        request.url.includes("/ogc/features/collections/parcels/items"),
    );
    expect(createCall).toBeDefined();
    expect(createCall?.body).toContain('"source":"dual-protocol-fixture"');

    expect(
      requests.some(
        (request) =>
          request.method === "DELETE" &&
          request.url.includes("/ogc/features/collections/parcels/items/parcel-3"),
      ),
    ).toBe(true);
  });
});
