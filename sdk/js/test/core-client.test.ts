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
