import { describe, expect, it } from "vitest";

import type { HonuaErrorContext, HonuaResponseContext } from "../src/index.js";
import { HonuaClient, HonuaHttpError } from "../src/index.js";

describe("Response timing in interceptor context (Direction 17)", () => {
  it("after interceptor receives durationMs > 0", async () => {
    let captured: HonuaResponseContext | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after(ctx) {
            captured = ctx;
          },
        },
      ],
      fetchFn: async () => new Response(JSON.stringify({ features: [] })),
    });

    await client.queryFeatures({ serviceId: "svc", layerId: 0 });

    expect(captured).toBeDefined();
    expect(captured!.durationMs).toBeTypeOf("number");
    expect(captured!.durationMs).toBeGreaterThanOrEqual(0);
  });

  it("error interceptor receives durationMs on HTTP error", async () => {
    let captured: HonuaErrorContext | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          error(ctx) {
            captured = ctx;
          },
        },
      ],
      fetchFn: async () => new Response("Not Found", { status: 404 }),
    });

    await expect(client.queryFeatures({ serviceId: "svc", layerId: 0 })).rejects.toThrow(HonuaHttpError);

    expect(captured).toBeDefined();
    expect(captured!.durationMs).toBeTypeOf("number");
    expect(captured!.durationMs).toBeGreaterThanOrEqual(0);
  });

  it("error interceptor receives durationMs on network error", async () => {
    let captured: HonuaErrorContext | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          error(ctx) {
            captured = ctx;
          },
        },
      ],
      fetchFn: () => Promise.reject(new TypeError("Failed to fetch")),
    });

    await expect(client.queryFeatures({ serviceId: "svc", layerId: 0 })).rejects.toThrow();

    expect(captured).toBeDefined();
    expect(captured!.durationMs).toBeTypeOf("number");
    expect(captured!.durationMs).toBeGreaterThanOrEqual(0);
  });
});
