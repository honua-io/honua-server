import { afterEach, describe, expect, it } from "vitest";

import {
  esriConfig,
  getEsriConfigHonuaInterceptors,
  resetEsriConfig,
} from "../src/index.js";

afterEach(() => {
  resetEsriConfig();
});

describe("esriConfig compat", () => {
  it("stores common request/auth settings", () => {
    esriConfig.apiKey = "demo-key";
    esriConfig.portalUrl = "https://portal.example.test";
    esriConfig.request.timeout = 10_000;
    esriConfig.request.useIdentity = false;
    esriConfig.request.trustedServers.push("portal.example.test");

    expect(esriConfig.apiKey).toBe("demo-key");
    expect(esriConfig.portalUrl).toBe("https://portal.example.test");
    expect(esriConfig.request.timeout).toBe(10_000);
    expect(esriConfig.request.useIdentity).toBe(false);
    expect(esriConfig.request.trustedServers).toEqual(["portal.example.test"]);
  });

  it("converts request interceptors to Honua interceptors", () => {
    esriConfig.request.interceptors.push({
      urls: "services/parcels",
      before(params) {
        params.requestOptions.headers = {
          ...(params.requestOptions.headers ?? {}),
          Authorization: "Bearer token",
        };
      },
    });

    const honuaInterceptors = getEsriConfigHonuaInterceptors();
    expect(honuaInterceptors).toHaveLength(1);
    expect(honuaInterceptors[0]?.before).toBeTypeOf("function");
  });

  it("resets mutable config state", () => {
    esriConfig.apiKey = "changed";
    esriConfig.request.interceptors.push({});
    esriConfig.request.trustedServers.push("changed");

    resetEsriConfig();

    expect(esriConfig.apiKey).toBeUndefined();
    expect(esriConfig.portalUrl).toBe("https://www.arcgis.com");
    expect(esriConfig.request.interceptors).toEqual([]);
    expect(esriConfig.request.trustedServers).toEqual([]);
  });
});
