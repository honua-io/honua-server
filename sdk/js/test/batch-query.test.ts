import { describe, expect, it } from "vitest";

import { HonuaClient, batchQuery } from "../src/index.js";
import type { BatchQueryItem } from "../src/index.js";

function createMockClient(
  handler: (input: string | URL | Request, init?: RequestInit) => Promise<Response>,
): HonuaClient {
  return new HonuaClient({
    baseUrl: "https://example.test",
    fetchFn: handler,
  });
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status });
}

describe("batchQuery", () => {
  it("single query batch returns single result", async () => {
    const client = createMockClient(async () => jsonResponse({ features: [{ attributes: { id: 1 } }] }));

    const results = await batchQuery(client, [{ request: { serviceId: "svc", layerId: 0, where: "1=1" } }]);

    expect(results).toHaveLength(1);
    expect(results[0].response).toEqual({ features: [{ attributes: { id: 1 } }] });
    expect(results[0].error).toBeUndefined();
    expect(results[0].label).toBeUndefined();
  });

  it("multiple queries execute and return results in order", async () => {
    let callIndex = 0;
    const client = createMockClient(async () => {
      const idx = callIndex++;
      return jsonResponse({ features: [{ attributes: { idx } }] });
    });

    const items: BatchQueryItem[] = [
      { request: { serviceId: "svc", layerId: 0 }, label: "first" },
      { request: { serviceId: "svc", layerId: 1 }, label: "second" },
      { request: { serviceId: "svc", layerId: 2 }, label: "third" },
    ];

    const results = await batchQuery(client, items);

    expect(results).toHaveLength(3);
    expect(results[0].label).toBe("first");
    expect(results[0].response?.features?.[0].attributes).toEqual({ idx: 0 });
    expect(results[1].label).toBe("second");
    expect(results[1].response?.features?.[0].attributes).toEqual({ idx: 1 });
    expect(results[2].label).toBe("third");
    expect(results[2].response?.features?.[0].attributes).toEqual({ idx: 2 });
  });

  it("failed queries populate error field (not response)", async () => {
    const client = createMockClient(async () => jsonResponse({ error: { code: 400, message: "Bad" } }, 400));

    const results = await batchQuery(client, [{ request: { serviceId: "svc", layerId: 0 }, label: "fail" }]);

    expect(results).toHaveLength(1);
    expect(results[0].label).toBe("fail");
    expect(results[0].response).toBeUndefined();
    expect(results[0].error).toBeInstanceOf(Error);
  });

  it("mixed success/failure results", async () => {
    let callCount = 0;
    const client = createMockClient(async () => {
      const idx = callCount++;
      if (idx === 1) {
        return jsonResponse({ error: { code: 500, message: "Server error" } }, 500);
      }
      return jsonResponse({ features: [{ attributes: { ok: true } }] });
    });

    const items: BatchQueryItem[] = [
      { request: { serviceId: "svc", layerId: 0 }, label: "ok-1" },
      { request: { serviceId: "svc", layerId: 1 }, label: "fail" },
      { request: { serviceId: "svc", layerId: 2 }, label: "ok-2" },
    ];

    const results = await batchQuery(client, items);

    expect(results).toHaveLength(3);
    expect(results[0].response).toBeDefined();
    expect(results[0].error).toBeUndefined();
    expect(results[1].response).toBeUndefined();
    expect(results[1].error).toBeInstanceOf(Error);
    expect(results[2].response).toBeDefined();
    expect(results[2].error).toBeUndefined();
  });

  it("concurrency limiting works", async () => {
    let currentConcurrent = 0;
    let maxConcurrent = 0;
    const resolvers: Array<() => void> = [];

    const client = createMockClient(async () => {
      currentConcurrent++;
      maxConcurrent = Math.max(maxConcurrent, currentConcurrent);

      // Wait until explicitly released.
      await new Promise<void>((resolve) => {
        resolvers.push(resolve);
      });

      currentConcurrent--;
      return jsonResponse({ features: [] });
    });

    const items: BatchQueryItem[] = Array.from({ length: 8 }, (_, i) => ({
      request: { serviceId: "svc", layerId: i },
      label: `q${i}`,
    }));

    const promise = batchQuery(client, items, { concurrency: 3 });

    // Allow microtasks to settle so the first 3 queries start.
    await new Promise((r) => setTimeout(r, 10));
    expect(currentConcurrent).toBe(3);

    // Release all queries one at a time.
    while (resolvers.length > 0) {
      const resolver = resolvers.shift()!;
      resolver();
      await new Promise((r) => setTimeout(r, 10));
    }

    const results = await promise;

    expect(results).toHaveLength(8);
    expect(maxConcurrent).toBe(3);
    // All results should be successful.
    for (const r of results) {
      expect(r.response).toBeDefined();
      expect(r.error).toBeUndefined();
    }
  });

  it("abort signal skips remaining queries", async () => {
    const controller = new AbortController();
    let callCount = 0;

    const client = createMockClient(async () => {
      callCount++;
      // Abort after the first query starts.
      if (callCount === 1) {
        controller.abort();
      }
      return jsonResponse({ features: [] });
    });

    const items: BatchQueryItem[] = Array.from({ length: 5 }, (_, i) => ({
      request: { serviceId: "svc", layerId: i },
      label: `q${i}`,
    }));

    // Use concurrency=1 so queries execute sequentially and we can abort mid-batch.
    const results = await batchQuery(client, items, {
      concurrency: 1,
      signal: controller.signal,
    });

    expect(results).toHaveLength(5);
    // First query completed successfully.
    expect(results[0].response).toBeDefined();
    expect(results[0].error).toBeUndefined();
    // Remaining queries should be skipped with abort errors.
    for (let i = 1; i < 5; i++) {
      expect(results[i].response).toBeUndefined();
      expect(results[i].error).toBeDefined();
      expect(results[i].error!.name).toBe("AbortError");
    }
  });

  it("routes map-layer type to queryMapLayer", async () => {
    let capturedUrl = "";
    const client = createMockClient(async (input) => {
      capturedUrl = String(input);
      return jsonResponse({ features: [] });
    });

    await batchQuery(client, [{ type: "map-layer", request: { serviceId: "svc", layerId: 0 }, label: "map" }]);

    expect(capturedUrl).toContain("/MapServer/");
  });

  it("defaults type to feature (FeatureServer)", async () => {
    let capturedUrl = "";
    const client = createMockClient(async (input) => {
      capturedUrl = String(input);
      return jsonResponse({ features: [] });
    });

    await batchQuery(client, [{ request: { serviceId: "svc", layerId: 0 } }]);

    expect(capturedUrl).toContain("/FeatureServer/");
  });

  it("returns empty array for empty input", async () => {
    const client = createMockClient(async () => jsonResponse({ features: [] }));
    const results = await batchQuery(client, []);
    expect(results).toEqual([]);
  });
});
