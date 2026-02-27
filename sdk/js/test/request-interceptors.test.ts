import { describe, expect, it } from "vitest";

import {
  EsriRequestInterceptorRegistry,
  HonuaClient,
  HonuaHttpError,
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
} from "../src/index.js";

describe("HonuaClient request interceptors", () => {
  it("applies before/after interceptor hooks", async () => {
    let requestedUrl: string | undefined;
    let requestedHeaders: HeadersInit | undefined;
    const afterStatuses: number[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          before: (context) => ({
            url: `${context.url}&traceId=abc123`,
            init: {
              headers: {
                "X-Trace-ID": "abc123",
              },
            },
          }),
          after: (context) => {
            afterStatuses.push(context.response.status);
          },
        },
      ],
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedHeaders = init?.headers;
        return new Response(JSON.stringify({ services: [] }), { status: 200 });
      },
    });

    const response = await client.listServices();
    expect(response).toEqual({ services: [] });
    expect(requestedUrl).toContain("/rest/services?f=json&traceId=abc123");
    expect(requestedHeaders).toMatchObject({
      Accept: "application/json",
      "X-Trace-ID": "abc123",
    });
    expect(afterStatuses).toEqual([200]);
  });

  it("calls error interceptors for http errors", async () => {
    let interceptedError: unknown;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          error: (context) => {
            interceptedError = context.error;
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ error: { message: "bad request" } }), {
          status: 400,
        }),
    });

    await expect(client.listServices()).rejects.toBeInstanceOf(HonuaHttpError);
    expect(interceptedError).toBeInstanceOf(HonuaHttpError);
  });

  it("does not run after interceptors for HTTP error responses", async () => {
    const afterStatuses: number[] = [];
    const errors: unknown[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after: (context) => {
            afterStatuses.push(context.response.status);
          },
          error: (context) => {
            errors.push(context.error);
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ error: { message: "failed" } }), {
          status: 500,
        }),
    });

    await expect(client.listServices()).rejects.toBeInstanceOf(HonuaHttpError);
    expect(afterStatuses).toEqual([]);
    expect(errors).toHaveLength(1);
  });

  it("lets after interceptors read response body without consuming client parsing", async () => {
    const afterBodies: unknown[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after: async (context) => {
            afterBodies.push(await context.response.json());
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ services: [{ name: "demo" }] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
    });

    const response = await client.listServices();
    expect(afterBodies).toEqual([{ services: [{ name: "demo" }] }]);
    expect(response).toEqual({ services: [{ name: "demo" }] });
  });

  it("clones responses so multiple after interceptors can read body streams", async () => {
    const afterTexts: string[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after: async (context) => {
            afterTexts.push(await context.response.text());
          },
        },
        {
          after: async (context) => {
            afterTexts.push(await context.response.text());
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ services: [] }), {
          status: 200,
          headers: { "content-type": "application/json" },
        }),
    });

    const response = await client.listServices();
    expect(afterTexts).toEqual(['{"services":[]}', '{"services":[]}']);
    expect(response).toEqual({ services: [] });
  });
});

describe("esri-style request bridge", () => {
  it("bridges esri-style interceptor hooks into HonuaClient", async () => {
    let requestedUrl: string | undefined;
    let requestedHeaders: HeadersInit | undefined;
    let afterStatus: number | undefined;

    const [bridge] = createEsriRequestInterceptors([
      {
        urls: "/rest/services/default",
        before: (params) => {
          params.url = `${params.url}&esriBridge=1`;
          params.requestOptions.headers = {
            ...(params.requestOptions.headers ?? {}),
            "X-Esri-Bridge": "true",
          };
        },
        after: (response) => {
          afterStatus = response.status;
        },
      },
    ]);

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [bridge],
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedHeaders = init?.headers;
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const response = await client.queryFeatures({
      serviceId: "default",
      layerId: 0,
    });

    expect(response).toEqual({ features: [] });
    expect(requestedUrl).toContain("/rest/services/default/FeatureServer/0/query?");
    expect(requestedUrl).toContain("esriBridge=1");
    expect(requestedHeaders).toMatchObject({ "X-Esri-Bridge": "true" });
    expect(afterStatus).toBe(200);
  });

  it("adds ArcGIS token through query or bearer modes", async () => {
    const requestedUrls: string[] = [];
    const requestedHeaders: HeadersInit[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        createArcGisTokenInterceptor({
          applyTo: "/rest/services/default",
          getToken: () => "query-token",
          mode: "query",
        }),
        createArcGisTokenInterceptor({
          applyTo: /FeatureServer\/0\//,
          getToken: () => "bearer-token",
          mode: "bearer",
        }),
      ],
      fetchFn: async (input, init) => {
        requestedUrls.push(String(input));
        requestedHeaders.push(init?.headers ?? {});
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({ serviceId: "default", layerId: 0 });

    expect(requestedUrls[0]).toContain("token=query-token");
    expect(requestedHeaders[0]).toMatchObject({ Authorization: "Bearer bearer-token" });
  });

  it("supports dynamic interceptor add/remove via registry bridge", async () => {
    const registry = new EsriRequestInterceptorRegistry();
    const requestedHeaders: HeadersInit[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [registry.asHonuaInterceptor()],
      fetchFn: async (_input, init) => {
        requestedHeaders.push(init?.headers ?? {});
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({ serviceId: "default", layerId: 0 });
    expect(requestedHeaders[0]).not.toMatchObject({ "X-Registry": "on" });

    const handle = registry.add({
      before: (params) => {
        params.requestOptions.headers = {
          ...(params.requestOptions.headers ?? {}),
          "X-Registry": "on",
        };
      },
    });
    await client.queryFeatures({ serviceId: "default", layerId: 0 });
    expect(requestedHeaders[1]).toMatchObject({ "X-Registry": "on" });

    handle.remove();
    await client.queryFeatures({ serviceId: "default", layerId: 0 });
    expect(requestedHeaders[2]).not.toMatchObject({ "X-Registry": "on" });
  });

  it("applies global regex url patterns consistently across repeated requests", async () => {
    const requestedHeaders: HeadersInit[] = [];
    const [bridge] = createEsriRequestInterceptors([
      {
        urls: /FeatureServer\/0\/query/g,
        before: (params) => {
          params.requestOptions.headers = {
            ...(params.requestOptions.headers ?? {}),
            "X-Global-RegExp": "matched",
          };
        },
      },
    ]);

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [bridge],
      fetchFn: async (_input, init) => {
        requestedHeaders.push(init?.headers ?? {});
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({ serviceId: "default", layerId: 0 });
    await client.queryFeatures({ serviceId: "default", layerId: 0 });

    expect(requestedHeaders[0]).toMatchObject({ "X-Global-RegExp": "matched" });
    expect(requestedHeaders[1]).toMatchObject({ "X-Global-RegExp": "matched" });
  });
});
