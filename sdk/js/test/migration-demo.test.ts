import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import {
  parseGeoservicesServiceUrl,
  runGeoservicesImportJob,
  runMigrationDemo,
} from "../src/migration/demo.js";

const tempDirs: string[] = [];

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-migration-demo-"));
  tempDirs.push(dir);
  return dir;
}

function fixtureRoot(): string {
  return fileURLToPath(new URL("./fixtures", import.meta.url));
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("migration demo helpers", () => {
  it("parses geoservices service URL details", () => {
    const parsed = parseGeoservicesServiceUrl(
      "https://example.test/gis/rest/services/incidents/FeatureServer/3",
    );

    expect(parsed).toEqual({
      baseUrl: "https://example.test/gis",
      serviceId: "incidents",
      serviceType: "FeatureServer",
      layerId: 3,
    });
  });

  it("runs geoservices import polling until completion", async () => {
    const requests: Array<{
      url: string;
      method: string;
      headers: Record<string, string>;
      body: string;
    }> = [];
    let pollCount = 0;

    const fetchFn: typeof fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;
      const method = init?.method ?? "GET";
      const headers = normalizeHeaders(init?.headers);
      const body =
        typeof init?.body === "string"
          ? init.body
          : init?.body instanceof Uint8Array
            ? Buffer.from(init.body).toString("utf8")
            : "";

      requests.push({ url, method, headers, body });

      if (url.endsWith("/api/v1/admin/import/geoservices/start")) {
        return new Response(JSON.stringify({ jobId: "job-123", statusUrl: "jobs/job-123" }), {
          status: 202,
          headers: { "Content-Type": "application/json" },
        });
      }

      if (url.endsWith("/api/v1/admin/import/geoservices/jobs/job-123")) {
        pollCount += 1;
        if (pollCount === 1) {
          return new Response(
            JSON.stringify({
              jobId: "job-123",
              status: 0,
              currentPhase: "Queued",
              featuresProcessed: 0,
            }),
            { status: 200, headers: { "Content-Type": "application/json" } },
          );
        }

        return new Response(
          JSON.stringify({
            jobId: "job-123",
            status: "Completed",
            currentPhase: "Done",
            featuresProcessed: 42,
            estimatedTotalFeatures: 42,
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        );
      }

      return new Response(JSON.stringify({ error: "not found" }), { status: 404 });
    }) as typeof fetch;

    const result = await runGeoservicesImportJob({
      adminBaseUrl: "http://127.0.0.1:5050",
      adminApiKey: "demo-key",
      sourceServiceUrl: "https://arcgis.example/rest/services/incidents/FeatureServer",
      layerId: 0,
      tableName: "incidents",
      pollIntervalMs: 1,
      timeoutMs: 5_000,
      fetchFn,
    });

    expect(result.jobId).toBe("job-123");
    expect(result.status).toBe("Completed");
    expect(result.pollCount).toBe(2);
    expect(result.featuresProcessed).toBe(42);

    const startRequest = requests.find((request) => request.url.endsWith("/start"));
    expect(startRequest?.method).toBe("POST");
    expect(startRequest?.headers["x-api-key"]).toBe("demo-key");
    expect(startRequest?.body).toContain("\"tableName\":\"incidents\"");
  });

  it("runs migration demo codemod stage and writes fixture output", async () => {
    const outputDir = makeTempDir();
    const report = await runMigrationDemo({
      fixtureName: "esri-ready-app",
      fixturesRoot: fixtureRoot(),
      outputDir,
      skipImport: true,
      skipReconciliation: true,
    });

    expect(report.passed).toBe(true);
    expect(report.elapsedMs).toBeGreaterThanOrEqual(0);
    expect(report.migration.readiness).toBe("ready");
    expect(report.migration.codemodResult.metrics.manualCallSites).toBe(0);
    expect(fs.existsSync(path.join(report.workingAppDir, "src", "main.ts"))).toBe(true);
  });
});

function normalizeHeaders(headers: HeadersInit | undefined): Record<string, string> {
  if (!headers) {
    return {};
  }

  const normalized: Record<string, string> = {};
  const entries = new Headers(headers).entries();
  for (const [key, value] of entries) {
    normalized[key] = value;
  }
  return normalized;
}
