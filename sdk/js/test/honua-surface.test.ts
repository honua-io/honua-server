import { describe, expect, it } from "vitest";

import {
  createHonuaService,
  HonuaClient,
  HonuaFeatureLayer,
  HonuaMapService,
  HonuaService,
} from "../src/index.js";

describe("Honua native API surfaces", () => {
  it("builds fluent service, layer, and map-service wrappers", () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => new Response(JSON.stringify({}), { status: 200 }),
    });

    const service = client.service("transport");
    const layer = service.featureLayer(5);
    const mapService = service.mapService();
    const helperService = createHonuaService(client, "transport");

    expect(service).toBeInstanceOf(HonuaService);
    expect(layer).toBeInstanceOf(HonuaFeatureLayer);
    expect(mapService).toBeInstanceOf(HonuaMapService);
    expect(helperService).toBeInstanceOf(HonuaService);
    expect(layer.serviceId).toBe("transport");
    expect(layer.layerId).toBe(5);
    expect(mapService.serviceId).toBe("transport");
  });

  it("queries features and related records through fluent layer wrapper", async () => {
    const requests: Array<{ url: string; method: string }> = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requests.push({
          url: String(input),
          method: String(init?.method ?? "GET"),
        });
        return new Response(JSON.stringify({ features: [{ attributes: { OBJECTID: 1 } }] }), {
          status: 200,
        });
      },
    });

    const layer = client.featureLayer("transport", 3);
    const queryResponse = await layer.queryFeatures({
      where: "status = 'active'",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: false,
    });
    const relatedResponse = await layer.queryRelatedRecords({
      relationshipId: 2,
      objectIds: [1, 2],
      outFields: ["*"],
      returnGeometry: true,
    });

    expect(queryResponse).toEqual({ features: [{ attributes: { OBJECTID: 1 } }] });
    expect(relatedResponse).toEqual({ features: [{ attributes: { OBJECTID: 1 } }] });
    expect(requests[0]?.url).toContain("/rest/services/transport/FeatureServer/3/query?");
    expect(requests[0]?.url).toContain("where=status+%3D+%27active%27");
    expect(requests[1]?.url).toContain("/rest/services/transport/FeatureServer/3/queryRelatedRecords?");
    expect(requests[1]?.url).toContain("relationshipId=2");
    expect(requests[1]?.url).toContain("objectIds=1%2C2");
  });

  it("supports queryFeatureCount and queryObjectIds convenience methods", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        if (url.searchParams.get("returnCountOnly") === "true") {
          return new Response(JSON.stringify({ count: 7 }), { status: 200 });
        }
        if (url.searchParams.get("returnIdsOnly") === "true") {
          return new Response(JSON.stringify({ objectIds: [1, 2, "bad", 3] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 9);
    const count = await layer.queryFeatureCount({ where: "1=1" });
    const objectIds = await layer.queryObjectIds({ where: "status = 'open'" });

    expect(count).toBe(7);
    expect(objectIds).toEqual([1, 2, 3]);
  });

  it("supports queryFeaturesAll pagination helper", async () => {
    const requestedOffsets: number[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        const offset = Number.parseInt(url.searchParams.get("resultOffset") ?? "0", 10);
        requestedOffsets.push(offset);

        if (offset === 0) {
          return new Response(
            JSON.stringify({ features: [{ id: 1 }, { id: 2 }] }),
            { status: 200 },
          );
        }
        if (offset === 2) {
          return new Response(
            JSON.stringify({ features: [{ id: 3 }] }),
            { status: 200 },
          );
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 4);
    const allFeatures = await layer.queryFeaturesAll({
      where: "1=1",
      pageSize: 2,
    });

    expect(allFeatures).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(requestedOffsets).toEqual([0, 2]);
  });

  it("invokes map-service metadata, legend, export, identify, and find wrappers", async () => {
    const requestedUrls: string[] = [];

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrls.push(String(input));
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    const mapService = client.mapService("basemap");
    await mapService.metadata();
    await mapService.legend({ size: [20, 20] });
    await mapService.exportMap({
      bbox: [-180, -90, 180, 90],
      size: [256, 256],
    });
    await mapService.identify({
      geometry: { x: -157.8, y: 21.3 },
      mapExtent: [-180, -90, 180, 90],
      imageDisplay: [1024, 768, 96],
    });
    await mapService.find({
      searchText: "Harbor",
    });

    expect(requestedUrls[0]).toContain("/rest/services/basemap/MapServer?f=json");
    expect(requestedUrls[1]).toContain("/rest/services/basemap/MapServer/legend?");
    expect(requestedUrls[2]).toContain("/rest/services/basemap/MapServer/export?");
    expect(requestedUrls[3]).toContain("/rest/services/basemap/MapServer/identify?");
    expect(requestedUrls[4]).toContain("/rest/services/basemap/MapServer/find?");
  });
});
