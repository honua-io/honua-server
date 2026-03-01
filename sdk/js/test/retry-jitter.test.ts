import { afterEach, describe, expect, it, vi } from "vitest";

import { HonuaClient } from "../src/index.js";

describe("Retry jitter (Direction 16)", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("applies equal jitter to exponential backoff", async () => {
    const delays: number[] = [];
    const originalSetTimeout = globalThis.setTimeout;

    // Capture sleep durations by spying on setTimeout
    vi.spyOn(globalThis, "setTimeout").mockImplementation((fn, ms) => {
      if (typeof ms === "number" && ms > 0) {
        delays.push(ms);
      }
      // Execute immediately for test speed
      if (typeof fn === "function") fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    });

    let attempt = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 3,
        baseDelayMs: 1000,
        maxDelayMs: 30000,
        retryStatuses: [503],
      },
      fetchFn: async () => {
        attempt++;
        if (attempt <= 3) {
          return new Response("Service Unavailable", { status: 503 });
        }
        return new Response(JSON.stringify({ features: [] }));
      },
    });

    await client.queryFeatures({ serviceId: "svc", layerId: 0 });

    // Should have retried 3 times before succeeding
    expect(delays).toHaveLength(3);

    // With equal jitter: delay = cappedDelay * (0.5 + random * 0.5)
    // For attempt 0: baseDelay * 2^0 = 1000, jittered range: [500, 1000]
    // For attempt 1: baseDelay * 2^1 = 2000, jittered range: [1000, 2000]
    // For attempt 2: baseDelay * 2^2 = 4000, jittered range: [2000, 4000]
    expect(delays[0]).toBeGreaterThanOrEqual(500);
    expect(delays[0]).toBeLessThanOrEqual(1000);
    expect(delays[1]).toBeGreaterThanOrEqual(1000);
    expect(delays[1]).toBeLessThanOrEqual(2000);
    expect(delays[2]).toBeGreaterThanOrEqual(2000);
    expect(delays[2]).toBeLessThanOrEqual(4000);
  });

  it("jitter lower bound is deterministic when Math.random returns 0", async () => {
    vi.spyOn(Math, "random").mockReturnValue(0);

    const delays: number[] = [];
    vi.spyOn(globalThis, "setTimeout").mockImplementation((fn, ms) => {
      if (typeof ms === "number" && ms > 0) {
        delays.push(ms);
      }
      if (typeof fn === "function") fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    });

    let attempt = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 1,
        baseDelayMs: 1000,
        maxDelayMs: 30000,
        retryStatuses: [503],
      },
      fetchFn: async () => {
        attempt++;
        if (attempt <= 1) {
          return new Response("", { status: 503 });
        }
        return new Response(JSON.stringify({ features: [] }));
      },
    });

    await client.queryFeatures({ serviceId: "svc", layerId: 0 });

    // random=0: delay = 1000 * (0.5 + 0*0.5) = 500
    expect(delays[0]).toBe(500);
  });

  it("jitter upper bound is deterministic when Math.random returns 1", async () => {
    vi.spyOn(Math, "random").mockReturnValue(1);

    const delays: number[] = [];
    vi.spyOn(globalThis, "setTimeout").mockImplementation((fn, ms) => {
      if (typeof ms === "number" && ms > 0) {
        delays.push(ms);
      }
      if (typeof fn === "function") fn();
      return 0 as unknown as ReturnType<typeof setTimeout>;
    });

    let attempt = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 1,
        baseDelayMs: 1000,
        maxDelayMs: 30000,
        retryStatuses: [503],
      },
      fetchFn: async () => {
        attempt++;
        if (attempt <= 1) {
          return new Response("", { status: 503 });
        }
        return new Response(JSON.stringify({ features: [] }));
      },
    });

    await client.queryFeatures({ serviceId: "svc", layerId: 0 });

    // random=1: delay = 1000 * (0.5 + 1*0.5) = 1000
    expect(delays[0]).toBe(1000);
  });
});
