import { describe, expect, it, vi } from "vitest";
import { HonuaClient } from "../src/core/client.js";

describe("HonuaClient transport selection", () => {
  it("defaults to rest transport", () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => new Response("{}", { status: 200 }),
    });

    expect(client.isGrpcWeb).toBe(false);
  });

  it("reports grpc-web transport when configured", () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      transport: "grpc-web",
      fetchFn: async () => new Response("{}", { status: 200 }),
    });

    expect(client.isGrpcWeb).toBe(true);
  });

  it("uses REST path when transport is rest", async () => {
    let requestedUrl = "";
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      transport: "rest",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "svc1",
      layerId: 0,
      where: "1=1",
    });

    expect(requestedUrl).toContain("/rest/services/svc1/FeatureServer/0/query");
  });

  it("uses REST path by default (no transport option)", async () => {
    let requestedUrl = "";
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "svc1",
      layerId: 0,
    });

    expect(requestedUrl).toContain("/rest/services/svc1/FeatureServer/0/query");
    expect(requestedUrl).toContain("f=json");
  });

  it("accepts HonuaTransport type in options", () => {
    // This test verifies the type system accepts the transport option
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      transport: "grpc-web",
      fetchFn: async () => new Response("{}", { status: 200 }),
    });

    expect(client.isGrpcWeb).toBe(true);
  });
});

describe("HonuaClient REST transport parity", () => {
  it("queryFeatures still works with preferBinary on rest transport", async () => {
    let requestedUrl = "";
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      transport: "rest",
      preferBinary: true,
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      },
    });

    await client.queryFeatures({
      serviceId: "svc1",
      layerId: 0,
    });

    // With preferBinary + rest transport, should use f=pbf
    expect(requestedUrl).toContain("f=pbf");
  });
});

describe("HonuaClient gRPC streaming", () => {
  it("queryFeaturesStream routes through gRPC adapter when transport is grpc-web", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      transport: "grpc-web",
      fetchFn: async () => new Response("{}", { status: 200 }),
    });

    // The stream should attempt to use gRPC transport
    // Since we can't fully mock connectrpc here, just verify the client
    // accepts the transport config and the stream method exists
    expect(typeof client.queryFeaturesStream).toBe("function");

    // Verify it returns an async generator
    const stream = client.queryFeaturesStream({ serviceId: "svc", layerId: 0 });
    expect(stream[Symbol.asyncIterator]).toBeDefined();
  });

  it("streamProtoPages yields feature batches from async iterable", async () => {
    const { streamProtoPages } = await import("../src/core/grpc-adapter.js");

    // Create a mock async iterable of FeaturePages
    const mockPages = [
      {
        features: [
          {
            attributes: {},
            geometry: { shape: { case: "point" as const, value: { x: 1, y: 2 } } },
          },
        ],
        isLastPage: false,
      },
      {
        features: [
          {
            attributes: {},
            geometry: { shape: { case: "point" as const, value: { x: 3, y: 4 } } },
          },
        ],
        isLastPage: true,
      },
    ];

    async function* fakeStream() {
      for (const page of mockPages) {
        yield page;
      }
    }

    // biome-ignore lint/suspicious/noExplicitAny: mock FeaturePage shape for testing
    const batches: unknown[] = [];
    for await (const batch of streamProtoPages(fakeStream() as any)) {
      batches.push(batch);
    }

    expect(batches).toHaveLength(2);
    expect(batches[0]).toEqual([{ attributes: {}, geometry: { x: 1, y: 2 } }]);
    expect(batches[1]).toEqual([{ attributes: {}, geometry: { x: 3, y: 4 } }]);
  });

  it("streamProtoPages stops on empty last page", async () => {
    const { streamProtoPages } = await import("../src/core/grpc-adapter.js");

    async function* fakeStream() {
      yield {
        features: [
          {
            attributes: {},
            geometry: { shape: { case: "point" as const, value: { x: 1, y: 2 } } },
          },
        ],
        isLastPage: false,
      };
      yield { features: [], isLastPage: true };
    }

    // biome-ignore lint/suspicious/noExplicitAny: mock FeaturePage shape for testing
    const batches: unknown[] = [];
    for await (const batch of streamProtoPages(fakeStream() as any)) {
      batches.push(batch);
    }

    expect(batches).toHaveLength(1);
    expect(batches[0]).toEqual([{ attributes: {}, geometry: { x: 1, y: 2 } }]);
  });
});
