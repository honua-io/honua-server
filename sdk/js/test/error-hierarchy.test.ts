import { describe, expect, it } from "vitest";

import {
  HonuaAbortError,
  HonuaClient,
  HonuaGrpcError,
  HonuaHttpError,
  HonuaNetworkError,
  HonuaTimeoutError,
  isHonuaError,
} from "../src/index.js";

describe("Error hierarchy", () => {
  describe("HonuaTimeoutError", () => {
    it("exposes timeoutMs", () => {
      const err = new HonuaTimeoutError(5000);
      expect(err.timeoutMs).toBe(5000);
      expect(err.name).toBe("HonuaTimeoutError");
      expect(err.message).toContain("5000");
      expect(err).toBeInstanceOf(Error);
      expect(err).toBeInstanceOf(HonuaTimeoutError);
    });

    it("is thrown on timeout via slow fetch", async () => {
      const client = new HonuaClient({
        baseUrl: "https://example.test",
        timeoutMs: 50,
        fetchFn: (_input, init) =>
          new Promise((_resolve, reject) => {
            const signal = init?.signal;
            if (signal) {
              if (signal.aborted) {
                reject(signal.reason ?? new DOMException("signal is aborted without reason", "AbortError"));
                return;
              }
              signal.addEventListener("abort", () => {
                reject(signal.reason ?? new DOMException("signal is aborted without reason", "AbortError"));
              });
            }
          }),
      });

      await expect(client.queryFeatures({ serviceId: "svc", layerId: 0 })).rejects.toThrow(HonuaTimeoutError);
    }, 10_000);
  });

  describe("HonuaNetworkError", () => {
    it("wraps underlying cause", () => {
      const cause = new TypeError("Failed to fetch");
      const err = new HonuaNetworkError("Network failure", cause);
      expect(err.name).toBe("HonuaNetworkError");
      expect(err.cause).toBe(cause);
      expect(err).toBeInstanceOf(Error);
    });

    it("is thrown on fetch failure", async () => {
      const client = new HonuaClient({
        baseUrl: "https://example.test",
        fetchFn: () => Promise.reject(new TypeError("Failed to fetch")),
      });

      await expect(client.queryFeatures({ serviceId: "svc", layerId: 0 })).rejects.toThrow(HonuaNetworkError);
    });
  });

  describe("HonuaAbortError", () => {
    it("has correct name and message", () => {
      const err = new HonuaAbortError();
      expect(err.name).toBe("HonuaAbortError");
      expect(err.message).toBe("Request was aborted");
      expect(err).toBeInstanceOf(Error);
    });

    it("is thrown when a pre-aborted signal is passed", async () => {
      const controller = new AbortController();
      controller.abort();

      const client = new HonuaClient({
        baseUrl: "https://example.test",
        fetchFn: async (_input, init) => {
          init?.signal?.throwIfAborted();
          return new Response("ok");
        },
      });

      await expect(
        client.queryFeatures({
          serviceId: "svc",
          layerId: 0,
          signal: controller.signal,
        }),
      ).rejects.toThrow(HonuaAbortError);
    });
  });

  describe("HonuaGrpcError", () => {
    it("exposes code and details", () => {
      const err = new HonuaGrpcError(14, "unavailable", { raw: true });
      expect(err.name).toBe("HonuaGrpcError");
      expect(err.code).toBe(14);
      expect(err.details).toEqual({ raw: true });
      expect(err.message).toBe("unavailable");
      expect(err).toBeInstanceOf(Error);
    });
  });

  describe("isHonuaError", () => {
    it("returns true for all 5 error types", () => {
      expect(isHonuaError(new HonuaHttpError(404, "not found", null))).toBe(true);
      expect(isHonuaError(new HonuaTimeoutError(1000))).toBe(true);
      expect(isHonuaError(new HonuaNetworkError("fail", null))).toBe(true);
      expect(isHonuaError(new HonuaAbortError())).toBe(true);
      expect(isHonuaError(new HonuaGrpcError(2, "unknown"))).toBe(true);
    });

    it("returns false for plain Error and non-Error values", () => {
      expect(isHonuaError(new Error("plain"))).toBe(false);
      expect(isHonuaError("string")).toBe(false);
      expect(isHonuaError(null)).toBe(false);
      expect(isHonuaError(undefined)).toBe(false);
    });
  });
});
