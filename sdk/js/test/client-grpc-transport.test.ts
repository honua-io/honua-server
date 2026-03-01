import { describe, expect, it } from "vitest";
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
