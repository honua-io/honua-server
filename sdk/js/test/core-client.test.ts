import { describe, expect, it } from "vitest";

import { HonuaClient, HonuaHttpError } from "../src/index.js";

describe("HonuaClient", () => {
  it("queries features using GET params", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const response = await client.queryFeatures({
      serviceId: "default",
      layerId: 1000,
      where: "OBJECTID > 1",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
      method: "GET",
    });

    expect(response).toEqual({ features: [] });
    expect(requestedUrl).toContain("/rest/services/default/FeatureServer/1000/query?");
    expect(requestedUrl).toContain("where=OBJECTID+%3E+1");
    expect(requestedUrl).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedUrl).toContain("returnGeometry=false");
    expect(requestedInit?.method).toBe("GET");
  });

  it("keeps empty outFields array as blank outFields value", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "default",
      layerId: 0,
      outFields: [],
      method: "GET",
    });

    const parsed = new URL(requestedUrl ?? "https://example.test");
    expect(parsed.searchParams.get("outFields")).toBe("");
  });

  it("queries map layers using MapServer layer query endpoint", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const response = await client.queryMapLayer({
      serviceId: "default",
      layerId: 3,
      where: "status = 'open'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
      method: "GET",
    });

    expect(response).toEqual({ features: [] });
    expect(requestedUrl).toContain("/rest/services/default/MapServer/3/query?");
    expect(requestedUrl).toContain("where=status+%3D+%27open%27");
    expect(requestedUrl).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedUrl).toContain("returnGeometry=false");
    expect(requestedInit?.method).toBe("GET");
  });

  it("queries map layers using MapServer layer query POST payload", async () => {
    let requestedInit: RequestInit | undefined;
    let requestedBody = "";

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (_input, init) => {
        requestedInit = init;
        requestedBody = String(init?.body ?? "");
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const response = await client.queryMapLayer({
      serviceId: "default",
      layerId: 3,
      where: "status = 'open'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
      method: "POST",
      extraParams: {
        orderByFields: "NAME ASC",
      },
    });

    expect(response).toEqual({ features: [] });
    expect(requestedInit?.method).toBe("POST");
    expect(requestedBody).toContain("f=json");
    expect(requestedBody).toContain("where=status+%3D+%27open%27");
    expect(requestedBody).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedBody).toContain("returnGeometry=false");
    expect(requestedBody).toContain("orderByFields=NAME+ASC");
  });

  it("retrieves map service metadata", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ mapName: "default" }), { status: 200 });
      },
    });

    const response = await client.getMapServiceMetadata("default");
    expect(response).toEqual({ mapName: "default" });
    expect(requestedUrl).toContain("/rest/services/default/MapServer?f=json");
  });

  it("applies edits using form payload", async () => {
    let requestedBody = "";

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (_input, init) => {
        requestedBody = String(init?.body ?? "");
        return new Response(JSON.stringify({ addResults: [{ success: true }] }), { status: 200 });
      },
    });

    const response = await client.applyEdits({
      serviceId: "default",
      layerId: 1000,
      adds: [{ attributes: { NAME: "A" } }],
    });

    expect(response).toEqual({ addResults: [{ success: true }] });
    expect(requestedBody).toContain("rollbackOnFailure=true");
    expect(requestedBody).toContain("adds=");
    expect(requestedBody).toContain("%22NAME%22");
  });

  it("queries related records endpoint with expected params", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ relatedRecordGroups: [] }), { status: 200 });
      },
    });

    const response = await client.queryRelatedRecords({
      serviceId: "default",
      layerId: 2,
      relationshipId: 3,
      objectIds: [1, 2],
      where: "status = 'open'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
    });

    expect(response).toEqual({ relatedRecordGroups: [] });
    expect(requestedUrl).toContain("/rest/services/default/FeatureServer/2/queryRelatedRecords?");
    expect(requestedUrl).toContain("relationshipId=3");
    expect(requestedUrl).toContain("objectIds=1%2C2");
    expect(requestedUrl).toContain("where=status+%3D+%27open%27");
    expect(requestedUrl).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedUrl).toContain("returnGeometry=false");
    expect(requestedInit?.method).toBe("GET");
  });

  it("queries map related records endpoint with expected params", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ relatedRecordGroups: [] }), { status: 200 });
      },
    });

    const response = await client.queryMapRelatedRecords({
      serviceId: "default",
      layerId: 2,
      relationshipId: 3,
      objectIds: [1, 2],
      where: "status = 'open'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
    });

    expect(response).toEqual({ relatedRecordGroups: [] });
    expect(requestedUrl).toContain("/rest/services/default/MapServer/2/queryRelatedRecords?");
    expect(requestedUrl).toContain("relationshipId=3");
    expect(requestedUrl).toContain("objectIds=1%2C2");
    expect(requestedUrl).toContain("where=status+%3D+%27open%27");
    expect(requestedUrl).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedUrl).toContain("returnGeometry=false");
    expect(requestedInit?.method).toBe("GET");
  });

  it("queries map related records endpoint using POST payload", async () => {
    let requestedInit: RequestInit | undefined;
    let requestedBody = "";

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (_input, init) => {
        requestedInit = init;
        requestedBody = String(init?.body ?? "");
        return new Response(JSON.stringify({ relatedRecordGroups: [] }), { status: 200 });
      },
    });

    const response = await client.queryMapRelatedRecords({
      serviceId: "default",
      layerId: 5,
      relationshipId: 4,
      objectIds: [2, 3],
      where: "status = 'open'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
      method: "POST",
      extraParams: {
        orderByFields: "NAME ASC",
      },
    });

    expect(response).toEqual({ relatedRecordGroups: [] });
    expect(requestedInit?.method).toBe("POST");
    expect(requestedBody).toContain("f=json");
    expect(requestedBody).toContain("relationshipId=4");
    expect(requestedBody).toContain("objectIds=2%2C3");
    expect(requestedBody).toContain("where=status+%3D+%27open%27");
    expect(requestedBody).toContain("outFields=OBJECTID%2CNAME");
    expect(requestedBody).toContain("returnGeometry=false");
    expect(requestedBody).toContain("orderByFields=NAME+ASC");
  });

  it("exports map image metadata from MapServer using GET params", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ href: "/tmp/map.png", width: 256, height: 256 }), {
          status: 200,
        });
      },
    });

    const response = await client.exportMap({
      serviceId: "default",
      bbox: [-180, -90, 180, 90],
      size: [256, 256],
      format: "png32",
      transparent: true,
      imageSr: 3857,
      bboxSr: 4326,
      backgroundColor: "255,255,255",
    });

    expect(response).toEqual({ href: "/tmp/map.png", width: 256, height: 256 });
    expect(requestedUrl).toContain("/rest/services/default/MapServer/export?");
    expect(requestedUrl).toContain("f=json");
    expect(requestedUrl).toContain("bbox=-180%2C-90%2C180%2C90");
    expect(requestedUrl).toContain("size=256%2C256");
    expect(requestedUrl).toContain("format=png32");
    expect(requestedUrl).toContain("transparent=true");
    expect(requestedUrl).toContain("bboxSR=4326");
    expect(requestedUrl).toContain("imageSR=3857");
    expect(requestedUrl).toContain("backgroundColor=255%2C255%2C255");
    expect(requestedInit?.method).toBe("GET");
  });

  it("exports map image metadata from MapServer using POST payload", async () => {
    let requestedBody = "";
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (_input, init) => {
        requestedInit = init;
        requestedBody = String(init?.body ?? "");
        return new Response(JSON.stringify({ href: "/tmp/map.png" }), {
          status: 200,
        });
      },
    });

    const response = await client.exportMap({
      serviceId: "default",
      bbox: "-180,-90,180,90",
      size: "512,512",
      method: "POST",
      responseFormat: "pjson",
      extraParams: {
        time: "2023-01-01,2023-01-31",
      },
    });

    expect(response).toEqual({ href: "/tmp/map.png" });
    expect(requestedInit?.method).toBe("POST");
    expect(requestedBody).toContain("f=pjson");
    expect(requestedBody).toContain("bbox=-180%2C-90%2C180%2C90");
    expect(requestedBody).toContain("size=512%2C512");
    expect(requestedBody).toContain("time=2023-01-01%2C2023-01-31");
  });

  it("gets map legend from MapServer/legend", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ layers: [] }), { status: 200 });
      },
    });

    const response = await client.getMapLegend({
      serviceId: "default",
      size: [20, 20],
      dynamicLayers: '[{"id":0}]',
      extraParams: { locale: "en-US" },
    });

    expect(response).toEqual({ layers: [] });
    expect(requestedInit?.method).toBe("GET");
    expect(requestedUrl).toContain("/rest/services/default/MapServer/legend?");
    expect(requestedUrl).toContain("f=json");
    expect(requestedUrl).toContain("size=20%2C20");
    expect(requestedUrl).toContain("dynamicLayers=%5B%7B%22id%22%3A0%7D%5D");
    expect(requestedUrl).toContain("locale=en-US");
  });

  it("identifies map features through MapServer/identify", async () => {
    let requestedBody = "";
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (_input, init) => {
        requestedInit = init;
        requestedBody = String(init?.body ?? "");
        return new Response(JSON.stringify({ results: [{ layerId: 1 }] }), { status: 200 });
      },
    });

    const response = await client.identifyMap({
      serviceId: "default",
      geometry: { x: -157.8, y: 21.3 },
      mapExtent: [-180, -90, 180, 90],
      imageDisplay: [1024, 768, 96],
      tolerance: 6,
      sr: 4326,
      layers: "visible:0,1",
      dynamicLayers: '[{"id":1}]',
      method: "POST",
      extraParams: { time: "2024-01-01,2024-12-31" },
    });

    expect(response).toEqual({ results: [{ layerId: 1 }] });
    expect(requestedInit?.method).toBe("POST");
    expect(requestedBody).toContain("f=json");
    expect(requestedBody).toContain("geometry=%7B%22x%22%3A-157.8%2C%22y%22%3A21.3%7D");
    expect(requestedBody).toContain("geometryType=esriGeometryPoint");
    expect(requestedBody).toContain("mapExtent=-180%2C-90%2C180%2C90");
    expect(requestedBody).toContain("imageDisplay=1024%2C768%2C96");
    expect(requestedBody).toContain("sr=4326");
    expect(requestedBody).toContain("layers=visible%3A0%2C1");
    expect(requestedBody).toContain("dynamicLayers=%5B%7B%22id%22%3A1%7D%5D");
    expect(requestedBody).toContain("time=2024-01-01%2C2024-12-31");
  });

  it("finds map features through MapServer/find", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ results: [{ layerId: 0, value: "Parcels" }] }), {
          status: 200,
        });
      },
    });

    const response = await client.findMap({
      serviceId: "default",
      searchText: "Parcels",
      contains: false,
      searchFields: ["NAME", "ALIAS"],
      layers: "all:0,1",
      sr: 3857,
      returnGeometry: true,
      dynamicLayers: '[{"id":1}]',
      extraParams: { time: "2024-01-01,2024-12-31" },
    });

    expect(response).toEqual({ results: [{ layerId: 0, value: "Parcels" }] });
    expect(requestedInit?.method).toBe("GET");
    expect(requestedUrl).toContain("/rest/services/default/MapServer/find?");
    expect(requestedUrl).toContain("f=json");
    expect(requestedUrl).toContain("searchText=Parcels");
    expect(requestedUrl).toContain("contains=false");
    expect(requestedUrl).toContain("searchFields=NAME%2CALIAS");
    expect(requestedUrl).toContain("layers=all%3A0%2C1");
    expect(requestedUrl).toContain("sr=3857");
    expect(requestedUrl).toContain("returnGeometry=true");
    expect(requestedUrl).toContain("dynamicLayers=%5B%7B%22id%22%3A1%7D%5D");
    expect(requestedUrl).toContain("time=2024-01-01%2C2024-12-31");
  });

  it("supports raw request helper for custom endpoints", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    const response = await client.request({
      path: "/rest/services/default/FeatureServer/0/queryAttachments",
      method: "POST",
      responseFormat: "pjson",
      query: { objectIds: "1,2" },
      headers: { "X-Debug": "1" },
      body: "where=1%3D1",
    });

    expect(response).toEqual({ ok: true });
    expect(requestedUrl).toContain("/rest/services/default/FeatureServer/0/queryAttachments?");
    expect(requestedUrl).toContain("f=pjson");
    expect(requestedUrl).toContain("objectIds=1%2C2");
    expect(requestedInit?.method).toBe("POST");
    expect(requestedInit?.headers).toMatchObject({ "X-Debug": "1" });
    expect(requestedInit?.body).toBe("where=1%3D1");
  });

  it("throws HonuaHttpError for non-2xx responses", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () =>
        new Response(JSON.stringify({ error: { message: "Layer not found" } }), { status: 404 }),
    });

    await expect(
      client.getLayerMetadata("default", 999),
    ).rejects.toMatchObject({
      name: "HonuaHttpError",
      statusCode: 404,
      message: "HTTP 404: Layer not found",
    });
  });

  it("queries features using POST form payload", async () => {
    let requestedUrl: string | undefined;
    let requestedInit: RequestInit | undefined;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requestedUrl = String(input);
        requestedInit = init;
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const response = await client.queryFeatures({
      serviceId: "default",
      layerId: 4,
      where: "status = 'open'",
      outFields: ["OBJECTID"],
      returnGeometry: false,
      method: "POST",
      extraParams: { resultRecordCount: 50 },
    });

    expect(response).toEqual({ features: [] });
    expect(requestedUrl).toBe("https://example.test/rest/services/default/FeatureServer/4/query");
    expect(requestedInit?.method).toBe("POST");
    expect(String(requestedInit?.body ?? "")).toContain("where=status+%3D+%27open%27");
    expect(String(requestedInit?.body ?? "")).toContain("outFields=OBJECTID");
    expect(String(requestedInit?.body ?? "")).toContain("returnGeometry=false");
    expect(String(requestedInit?.body ?? "")).toContain("resultRecordCount=50");
  });

  it("applies apiKey and bearerToken constructor headers", async () => {
    let requestedHeaders: HeadersInit | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      apiKey: "api-key-1",
      bearerToken: "token-1",
      fetchFn: async (_input, init) => {
        requestedHeaders = init?.headers;
        return new Response(JSON.stringify({ services: [] }), { status: 200 });
      },
    });

    await client.listServices();
    expect(requestedHeaders).toMatchObject({
      "X-API-Key": "api-key-1",
      Authorization: "Bearer token-1",
      Accept: "application/json",
    });
  });

  it("calls expected listServices and getLayerMetadata URLs", async () => {
    const requestedUrls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrls.push(String(input));
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    await client.listServices("pjson");
    await client.getLayerMetadata("default", 12);

    expect(requestedUrls[0]).toBe("https://example.test/rest/services?f=pjson");
    expect(requestedUrls[1]).toBe("https://example.test/rest/services/default/FeatureServer/12?f=json");
  });

  it("encodes special characters in serviceId paths", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "Public Works/Utilities & More",
      layerId: 0,
    });

    expect(requestedUrl).toContain(
      "/rest/services/Public%20Works%2FUtilities%20%26%20More/FeatureServer/0/query?",
    );
  });

  it("returns empty object for empty responses and raw text for non-JSON responses", async () => {
    const responses = [
      new Response("", { status: 200 }),
      new Response("plain text", { status: 200 }),
    ];
    let responseIndex = 0;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => {
        const response = responses[responseIndex];
        responseIndex += 1;
        return response ?? new Response("", { status: 200 });
      },
    });

    await expect(client.listServices()).resolves.toEqual({});
    await expect(client.listServices()).resolves.toEqual({ raw: "plain text" });
  });

  it("uses fallback HTTP error message for array response bodies", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => new Response(JSON.stringify([{ message: "array-body-error" }]), { status: 400 }),
    });

    await expect(client.listServices()).rejects.toMatchObject({
      name: "HonuaHttpError",
      statusCode: 400,
      message: "HTTP 400: Request failed",
    });
  });

  it("routes network errors through error interceptors", async () => {
    const intercepted: unknown[] = [];
    const networkError = new Error("network-down");
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          error: (context) => {
            intercepted.push(context.error);
          },
        },
      ],
      fetchFn: async () => {
        throw networkError;
      },
    });

    await expect(client.listServices()).rejects.toThrow("network-down");
    expect(intercepted).toEqual([networkError]);
  });

  it("retries transient network failures when retry policy is configured", async () => {
    let attempts = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 2,
        baseDelayMs: 1,
        maxDelayMs: 1,
      },
      fetchFn: async () => {
        attempts += 1;
        if (attempts < 3) {
          throw new Error("temporary-network-failure");
        }
        return new Response(JSON.stringify({ services: [] }), { status: 200 });
      },
    });

    await expect(client.listServices()).resolves.toEqual({ services: [] });
    expect(attempts).toBe(3);
  });

  it("retries configured HTTP status codes and succeeds", async () => {
    let attempts = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 2,
        baseDelayMs: 1,
        maxDelayMs: 1,
        retryStatuses: [503],
      },
      fetchFn: async () => {
        attempts += 1;
        if (attempts < 3) {
          return new Response(JSON.stringify({ error: { message: "service-unavailable" } }), { status: 503 });
        }
        return new Response(JSON.stringify({ services: [{ id: "ok" }] }), { status: 200 });
      },
    });

    await expect(client.listServices()).resolves.toEqual({ services: [{ id: "ok" }] });
    expect(attempts).toBe(3);
  });

  it("does not retry non-retryable HTTP status codes", async () => {
    let attempts = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      retry: {
        maxRetries: 3,
        baseDelayMs: 1,
        maxDelayMs: 1,
        retryStatuses: [503],
      },
      fetchFn: async () => {
        attempts += 1;
        return new Response(JSON.stringify({ error: { message: "bad-request" } }), { status: 400 });
      },
    });

    await expect(client.listServices()).rejects.toMatchObject({
      name: "HonuaHttpError",
      statusCode: 400,
    });
    expect(attempts).toBe(1);
  });

  it("aborts requests when timeoutMs is exceeded", async () => {
    const intercepted: unknown[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      timeoutMs: 10,
      interceptors: [
        {
          error: (context) => {
            intercepted.push(context.error);
          },
        },
      ],
      fetchFn: async (_input, init) =>
        new Promise<Response>((_resolve, reject) => {
          const signal = init?.signal as AbortSignal | undefined;
          if (!signal) {
            reject(new Error("missing signal"));
            return;
          }
          signal.addEventListener(
            "abort",
            () => {
              reject(new Error("aborted"));
            },
            { once: true },
          );
        }),
    });

    await expect(client.listServices()).rejects.toThrow("Request timed out after 10ms");
    expect(intercepted).toHaveLength(1);
    expect(intercepted[0]).toBeInstanceOf(Error);
    expect((intercepted[0] as Error).message).toBe("Request timed out after 10ms");
  });

  it("continues calling error interceptors when one interceptor throws", async () => {
    const seen: string[] = [];
    const networkError = new Error("network-down");
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          error: () => {
            seen.push("first");
            throw new Error("interceptor-failed");
          },
        },
        {
          error: () => {
            seen.push("second");
          },
        },
      ],
      fetchFn: async () => {
        throw networkError;
      },
    });

    await expect(client.listServices()).rejects.toBe(networkError);
    expect(seen).toEqual(["first", "second"]);
  });

  it("allows after interceptors to read response bodies without consuming returned payload", async () => {
    let afterPayload: unknown;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after: async ({ response }) => {
            afterPayload = await response.json();
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ services: [{ id: "default" }] }), { status: 200 }),
    });

    const response = await client.listServices();
    expect(afterPayload).toEqual({ services: [{ id: "default" }] });
    expect(response).toEqual({ services: [{ id: "default" }] });
  });

  it("invokes only error interceptors for HTTP error responses", async () => {
    const seen: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      interceptors: [
        {
          after: () => {
            seen.push("after");
          },
          error: () => {
            seen.push("error");
          },
        },
      ],
      fetchFn: async () =>
        new Response(JSON.stringify({ error: { message: "missing" } }), { status: 404 }),
    });

    await expect(client.listServices()).rejects.toMatchObject({
      name: "HonuaHttpError",
      statusCode: 404,
    });
    expect(seen).toEqual(["error"]);
  });

  it("rejects cross-origin absolute request paths", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => new Response(JSON.stringify({ ok: true }), { status: 200 }),
    });

    await expect(
      client.request({
        path: "https://attacker.test/rest/services/default/FeatureServer/0/query",
        method: "GET",
      }),
    ).rejects.toThrow("Cross-origin request URL is not allowed");
  });

  it("calls OGC metadata endpoints with explicit response formats", async () => {
    const requestedUrls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrls.push(String(input));
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    await client.getOgcFeaturesLanding({ responseFormat: "json" });
    await client.getOgcFeaturesConformance({ responseFormat: "json" });
    await client.listOgcCollections({ responseFormat: "json" });
    await client.getOgcCollection({ collectionId: "0", responseFormat: "json" });
    await client.getOgcQueryables({ collectionId: "0", responseFormat: "schemajson" });

    expect(requestedUrls[0]).toBe("https://example.test/ogc/features?f=json");
    expect(requestedUrls[1]).toBe("https://example.test/ogc/features/conformance?f=json");
    expect(requestedUrls[2]).toBe("https://example.test/ogc/features/collections?f=json");
    expect(requestedUrls[3]).toBe("https://example.test/ogc/features/collections/0?f=json");
    expect(requestedUrls[4]).toBe("https://example.test/ogc/features/collections/0/queryables?f=schemajson");
  });

  it("calls OGC items endpoints with filter/query parameters", async () => {
    const requestedUrls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrls.push(String(input));
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.listOgcItems({
      collectionId: 3,
      limit: 10,
      offset: 20,
      bbox: "-180,-90,180,90",
      datetime: "2025-01-01/2025-12-31",
      filter: "name = 'A'",
      ids: [1, 2, 3],
      properties: ["name", "category"],
      sortby: "-name",
      crs: "EPSG:4326",
      responseFormat: "geojson",
    });
    await client.getOgcItem({
      collectionId: 3,
      featureId: "abc-1",
      crs: "EPSG:3857",
      responseFormat: "geojson",
    });

    expect(requestedUrls[0]).toContain("/ogc/features/collections/3/items?");
    expect(requestedUrls[0]).toContain("f=geojson");
    expect(requestedUrls[0]).toContain("limit=10");
    expect(requestedUrls[0]).toContain("offset=20");
    expect(requestedUrls[0]).toContain("bbox=-180%2C-90%2C180%2C90");
    expect(requestedUrls[0]).toContain("datetime=2025-01-01%2F2025-12-31");
    expect(requestedUrls[0]).toContain("filter=name+%3D+%27A%27");
    expect(requestedUrls[0]).toContain("ids=1%2C2%2C3");
    expect(requestedUrls[0]).toContain("properties=name%2Ccategory");
    expect(requestedUrls[0]).toContain("sortby=-name");
    expect(requestedUrls[0]).toContain("crs=EPSG%3A4326");
    expect(requestedUrls[1]).toContain("/ogc/features/collections/3/items/abc-1?");
    expect(requestedUrls[1]).toContain("f=geojson");
    expect(requestedUrls[1]).toContain("crs=EPSG%3A3857");
  });

  it("supports OGC item CRUD methods", async () => {
    const calls: Array<{ url: string; method: string; body: string | undefined; headers: HeadersInit | undefined }> =
      [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        calls.push({
          url: String(input),
          method: String(init?.method ?? "GET"),
          body: typeof init?.body === "string" ? init.body : undefined,
          headers: init?.headers,
        });
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    await client.createOgcItem({
      collectionId: "3",
      feature: { type: "Feature", properties: { name: "A" } },
      responseFormat: "json",
    });
    await client.replaceOgcItem({
      collectionId: "3",
      featureId: "1",
      feature: { type: "Feature", properties: { name: "B" } },
      responseFormat: "json",
    });
    await client.patchOgcItem({
      collectionId: "3",
      featureId: "1",
      patch: { properties: { name: "C" } },
      responseFormat: "json",
    });
    await client.deleteOgcItem({
      collectionId: "3",
      featureId: "1",
      responseFormat: "json",
    });

    expect(calls[0]).toMatchObject({
      method: "POST",
    });
    expect(calls[0]?.url).toContain("/ogc/features/collections/3/items?f=json");
    expect(String(calls[0]?.body ?? "")).toContain('"name":"A"');
    expect(calls[0]?.headers).toMatchObject({ "Content-Type": "application/geo+json" });

    expect(calls[1]).toMatchObject({
      method: "PUT",
    });
    expect(calls[1]?.url).toContain("/ogc/features/collections/3/items/1?f=json");
    expect(String(calls[1]?.body ?? "")).toContain('"name":"B"');
    expect(calls[1]?.headers).toMatchObject({ "Content-Type": "application/geo+json" });

    expect(calls[2]).toMatchObject({
      method: "PATCH",
    });
    expect(calls[2]?.url).toContain("/ogc/features/collections/3/items/1?f=json");
    expect(String(calls[2]?.body ?? "")).toContain('"name":"C"');
    expect(calls[2]?.headers).toMatchObject({ "Content-Type": "application/merge-patch+json" });

    expect(calls[3]).toMatchObject({
      method: "DELETE",
    });
    expect(calls[3]?.url).toContain("/ogc/features/collections/3/items/1?f=json");
  });

  it("serializes first-class query parameters on queryFeatures", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "default",
      layerId: 0,
      where: "1=1",
      method: "GET",
      orderByFields: "NAME DESC",
      objectIds: [1, 2, 3],
      geometry: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
      geometryType: "esriGeometryEnvelope",
      spatialRel: "esriSpatialRelIntersects",
      returnDistinctValues: true,
      returnCentroid: false,
      groupByFieldsForStatistics: "TYPE",
      outStatistics: [{ statisticType: "count", onStatisticField: "OBJECTID", outStatisticFieldName: "cnt" }],
      resultOffset: 10,
      resultRecordCount: 50,
    });

    const url = new URL(requestedUrl ?? "https://example.test");
    expect(url.searchParams.get("orderByFields")).toBe("NAME DESC");
    expect(url.searchParams.get("objectIds")).toBe("1,2,3");
    expect(url.searchParams.get("geometryType")).toBe("esriGeometryEnvelope");
    expect(url.searchParams.get("spatialRel")).toBe("esriSpatialRelIntersects");
    expect(url.searchParams.get("returnDistinctValues")).toBe("true");
    expect(url.searchParams.get("returnCentroid")).toBe("false");
    expect(url.searchParams.get("groupByFieldsForStatistics")).toBe("TYPE");
    expect(url.searchParams.get("resultOffset")).toBe("10");
    expect(url.searchParams.get("resultRecordCount")).toBe("50");

    const geometry = JSON.parse(url.searchParams.get("geometry") ?? "{}");
    expect(geometry).toEqual({ xmin: 0, ymin: 0, xmax: 1, ymax: 1 });

    const stats = JSON.parse(url.searchParams.get("outStatistics") ?? "[]");
    expect(stats).toEqual([{ statisticType: "count", onStatisticField: "OBJECTID", outStatisticFieldName: "cnt" }]);
  });

  it("serializes string objectIds and string geometry and string outStatistics", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryFeatures({
      serviceId: "default",
      layerId: 0,
      method: "GET",
      objectIds: "1,2,3",
      geometry: "-180,-90,180,90",
      outStatistics: "[{\"statisticType\":\"count\"}]",
    });

    const url = new URL(requestedUrl ?? "https://example.test");
    expect(url.searchParams.get("objectIds")).toBe("1,2,3");
    expect(url.searchParams.get("geometry")).toBe("-180,-90,180,90");
    expect(url.searchParams.get("outStatistics")).toBe("[{\"statisticType\":\"count\"}]");
  });

  it("serializes first-class query parameters on queryMapLayer", async () => {
    let requestedUrl: string | undefined;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrl = String(input);
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    await client.queryMapLayer({
      serviceId: "default",
      layerId: 0,
      method: "GET",
      orderByFields: "POP DESC",
      objectIds: [10, 20],
      returnDistinctValues: true,
    });

    const url = new URL(requestedUrl ?? "https://example.test");
    expect(url.searchParams.get("orderByFields")).toBe("POP DESC");
    expect(url.searchParams.get("objectIds")).toBe("10,20");
    expect(url.searchParams.get("returnDistinctValues")).toBe("true");
  });
});
