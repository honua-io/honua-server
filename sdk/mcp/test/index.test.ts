import { describe, it, expect } from "vitest";
import { HonuaClient } from "@honua/sdk-js";
import { createServer, createClientFromEnv, resolveRuntimeOptions } from "../src/index.js";

describe("MCP server setup", () => {
  it("creates a server from a HonuaClient", () => {
    const client = new HonuaClient({ baseUrl: "http://localhost:5000" });
    const server = createServer(client);

    expect(server).toBeDefined();
  });
});

describe("runtime options", () => {
  it("defaults transport to grpc-web", () => {
    const options = resolveRuntimeOptions({
      HONUA_BASE_URL: "https://example.test",
    } as NodeJS.ProcessEnv);

    expect(options.transport).toBe("grpc-web");
  });

  it("accepts explicit rest transport", () => {
    const options = resolveRuntimeOptions({
      HONUA_BASE_URL: "https://example.test",
      HONUA_TRANSPORT: "rest",
    } as NodeJS.ProcessEnv);

    expect(options.transport).toBe("rest");
  });

  it("accepts grpc alias values", () => {
    const grpc = resolveRuntimeOptions({
      HONUA_BASE_URL: "https://example.test",
      HONUA_TRANSPORT: "grpc",
    } as NodeJS.ProcessEnv);
    const grcp = resolveRuntimeOptions({
      HONUA_BASE_URL: "https://example.test",
      HONUA_TRANSPORT: "grcp",
    } as NodeJS.ProcessEnv);

    expect(grpc.transport).toBe("grpc-web");
    expect(grcp.transport).toBe("grpc-web");
  });

  it("rejects invalid transport values", () => {
    expect(() =>
      resolveRuntimeOptions({
        HONUA_BASE_URL: "https://example.test",
        HONUA_TRANSPORT: "websocket",
      } as NodeJS.ProcessEnv),
    ).toThrow('HONUA_TRANSPORT must be "grpc-web" (aliases: "grpc", "grcp") or "rest"');
  });

  it("rejects missing base URL", () => {
    expect(() => resolveRuntimeOptions({} as NodeJS.ProcessEnv)).toThrow(
      "HONUA_BASE_URL environment variable is required",
    );
  });

  it("rejects invalid base URL", () => {
    expect(() =>
      resolveRuntimeOptions({
        HONUA_BASE_URL: "not-a-url",
      } as NodeJS.ProcessEnv),
    ).toThrow("HONUA_BASE_URL must be a valid absolute URL");
  });

  it("creates client from env with configured transport", () => {
    const client = createClientFromEnv({
      HONUA_BASE_URL: "https://example.test",
      HONUA_TRANSPORT: "grcp",
    } as NodeJS.ProcessEnv);

    expect(client).toBeInstanceOf(HonuaClient);
    expect(client.isGrpcWeb).toBe(true);
  });
});
