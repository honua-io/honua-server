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
});
