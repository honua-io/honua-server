import { describe, expect, it } from "vitest";

import {
  createHonuaOgcFeatures,
  createHonuaService,
  HonuaClient,
  HonuaFeatureLayer,
  HonuaMapService,
  HonuaOgcFeatureCollection,
  HonuaOgcFeatures,
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
    const aliasLayer = service.layer(6);
    const mapService = service.mapService();
    const ogc = client.ogcFeatures();
    const ogcViaFactory = createHonuaOgcFeatures(client);
    const ogcCollection = ogc.collection(0);
    const helperService = createHonuaService(client, "transport");

    expect(service).toBeInstanceOf(HonuaService);
    expect(layer).toBeInstanceOf(HonuaFeatureLayer);
    expect(mapService).toBeInstanceOf(HonuaMapService);
    expect(ogc).toBeInstanceOf(HonuaOgcFeatures);
    expect(ogcViaFactory).toBeInstanceOf(HonuaOgcFeatures);
    expect(ogcCollection).toBeInstanceOf(HonuaOgcFeatureCollection);
    expect(helperService).toBeInstanceOf(HonuaService);
    expect(layer.serviceId).toBe("transport");
    expect(layer.layerId).toBe(5);
    expect(aliasLayer.layerId).toBe(6);
    expect(mapService.serviceId).toBe("transport");
    expect(ogcCollection.collectionId).toBe(0);
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
    await mapService.getLegend({ size: 16 });
    await mapService.exportMap({
      bbox: [-180, -90, 180, 90],
      size: [256, 256],
    });
    await mapService.exportImage({
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
    expect(requestedUrls[2]).toContain("/rest/services/basemap/MapServer/legend?");
    expect(requestedUrls[3]).toContain("/rest/services/basemap/MapServer/export?");
    expect(requestedUrls[4]).toContain("/rest/services/basemap/MapServer/export?");
    expect(requestedUrls[5]).toContain("/rest/services/basemap/MapServer/identify?");
    expect(requestedUrls[6]).toContain("/rest/services/basemap/MapServer/find?");
  });

  it("supports extent, attachment, and scoped request helpers", async () => {
    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    let callCount = 0;

    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        requests.push({
          url: String(input),
          method: String(init?.method ?? "GET"),
          body: init?.body,
        });
        callCount += 1;
        if (callCount === 1) {
          return new Response(JSON.stringify({ extent: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 }, count: 11 }), {
            status: 200,
          });
        }
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 3);
    const extent = await layer.queryExtent({ where: "1=1" });
    await layer.queryAttachments({ objectIds: [1, 2], where: "status = 'open'" });
    await layer.queryAttachments({
      method: "POST",
      objectIds: [1, 2],
      where: "status = 'open'",
    });
    await layer.listAttachments({ objectId: 10 });
    await layer.deleteAttachments({ objectId: 10, attachmentIds: [100, 101] });
    await layer.addAttachment({
      objectId: 10,
      attachment: new Blob(["hello"], { type: "text/plain" }),
      name: "note.txt",
    });
    await layer.updateAttachment({
      objectId: 10,
      attachmentId: 100,
      attachment: "updated-body",
      name: "note.txt",
      contentType: "text/plain",
    });
    await layer.request({
      path: "queryDomains",
      query: { returnUpdates: true },
    });

    expect(extent).toEqual({
      extent: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
      count: 11,
    });
    expect(requests[0]?.url).toContain("/rest/services/transport/FeatureServer/3/query?");
    expect(requests[0]?.url).toContain("returnExtentOnly=true");
    expect(requests[1]?.url).toContain("/rest/services/transport/FeatureServer/3/queryAttachments?");
    expect(requests[1]?.url).toContain("objectIds=1%2C2");
    expect(requests[2]?.method).toBe("POST");
    expect(requests[2]?.url).toContain("/rest/services/transport/FeatureServer/3/queryAttachments?f=json");
    expect(String(requests[2]?.body ?? "")).toContain("objectIds=1%2C2");
    expect(requests[3]?.url).toContain("/rest/services/transport/FeatureServer/3/10/attachments?");
    expect(requests[4]?.url).toContain("/rest/services/transport/FeatureServer/3/10/deleteAttachments?f=json");
    expect(String(requests[4]?.body ?? "")).toContain("attachmentIds=100%2C101");
    expect(requests[5]?.url).toContain("/rest/services/transport/FeatureServer/3/10/addAttachment?");
    expect(requests[6]?.url).toContain("/rest/services/transport/FeatureServer/3/10/updateAttachment?");
    expect(requests[7]?.url).toContain("/rest/services/transport/FeatureServer/3/queryDomains?");
    expect(requests[7]?.url).toContain("returnUpdates=true");
  });

  it("supports service metadata/layer-id helpers and scoped service requests", async () => {
    const requests: string[] = [];
    let callCount = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requests.push(String(input));
        callCount += 1;
        if (callCount === 1) {
          return new Response(JSON.stringify({ layers: [{ id: 0 }, { id: "1" }, { id: "bad" }] }), { status: 200 });
        }
        if (callCount === 2) {
          return new Response(JSON.stringify({ layers: [{ id: 10 }, { id: 11 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
      },
    });

    const service = client.service("transport");
    const featureLayerIds = await service.featureLayerIds();
    const mapLayerIds = await service.mapLayerIds();
    await service.request({
      path: "FeatureServer",
      query: {
        f: "pjson",
      },
    });

    expect(featureLayerIds).toEqual([0, 1]);
    expect(mapLayerIds).toEqual([10, 11]);
    expect(requests[0]).toContain("/rest/services/transport/FeatureServer?f=json");
    expect(requests[1]).toContain("/rest/services/transport/MapServer?f=json");
    expect(requests[2]).toContain("/rest/services/transport/FeatureServer?f=pjson");
  });

  it("supports OGC features wrappers for metadata, items, and CRUD", async () => {
    const calls: Array<{ url: string; method: string; body: string | undefined }> = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input, init) => {
        calls.push({
          url: String(input),
          method: String(init?.method ?? "GET"),
          body: typeof init?.body === "string" ? init.body : undefined,
        });
        return new Response(JSON.stringify({ ok: true, features: [] }), { status: 200 });
      },
    });

    const ogc = client.ogcFeatures();
    await ogc.landing();
    await ogc.conformance();
    await ogc.collections();
    await ogc.getCollection({ collectionId: 3 });
    await ogc.queryables({ collectionId: 3, responseFormat: "schemajson" });
    await ogc.items({ collectionId: 3, limit: 5, ids: [1, 2], properties: ["name"] });
    await ogc.item({ collectionId: 3, featureId: "abc" });
    await ogc.createItem({ collectionId: 3, feature: { type: "Feature" } });
    await ogc.replaceItem({ collectionId: 3, featureId: "abc", feature: { type: "Feature" } });
    await ogc.patchItem({ collectionId: 3, featureId: "abc", patch: { properties: { name: "A" } } });
    await ogc.deleteItem({ collectionId: 3, featureId: "abc" });

    const collection = ogc.collection(3);
    await collection.metadata();
    await collection.queryables();
    await collection.items({ limit: 2 });
    await collection.item({ featureId: "def" });
    await collection.createItem({ feature: { type: "Feature" } });
    await collection.replaceItem({ featureId: "def", feature: { type: "Feature" } });
    await collection.patchItem({ featureId: "def", patch: { properties: { name: "B" } } });
    await collection.deleteItem({ featureId: "def" });

    expect(calls[0]?.url).toContain("/ogc/features?f=json");
    expect(calls[1]?.url).toContain("/ogc/features/conformance?f=json");
    expect(calls[2]?.url).toContain("/ogc/features/collections?f=json");
    expect(calls[3]?.url).toContain("/ogc/features/collections/3?f=json");
    expect(calls[4]?.url).toContain("/ogc/features/collections/3/queryables?f=schemajson");
    expect(calls[5]?.url).toContain("/ogc/features/collections/3/items?");
    expect(calls[6]?.url).toContain("/ogc/features/collections/3/items/abc?f=json");
    expect(calls[7]).toMatchObject({ method: "POST" });
    expect(calls[8]).toMatchObject({ method: "PUT" });
    expect(calls[9]).toMatchObject({ method: "PATCH" });
    expect(calls[10]).toMatchObject({ method: "DELETE" });
    expect(calls[11]?.url).toContain("/ogc/features/collections/3?f=json");
    expect(calls[18]).toMatchObject({ method: "DELETE" });
  });
});
