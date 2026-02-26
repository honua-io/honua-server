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
