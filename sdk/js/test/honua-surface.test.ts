import { describe, expect, it } from "vitest";

import {
  createHonuaOgcFeatures,
  createHonuaService,
  HonuaClient,
  HonuaFeatureLayer,
  HonuaMapLayer,
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
    const clientMapLayer = client.mapLayer("transport", 2);
    const mapService = service.mapService();
    const mapLayer = service.mapLayer(7);
    const mapLayerViaService = mapService.layer(8);
    const ogc = client.ogcFeatures();
    const ogcViaFactory = createHonuaOgcFeatures(client);
    const ogcCollection = ogc.collection(0);
    const helperService = createHonuaService(client, "transport");

    expect(service).toBeInstanceOf(HonuaService);
    expect(layer).toBeInstanceOf(HonuaFeatureLayer);
    expect(clientMapLayer).toBeInstanceOf(HonuaMapLayer);
    expect(mapLayer).toBeInstanceOf(HonuaMapLayer);
    expect(mapLayerViaService).toBeInstanceOf(HonuaMapLayer);
    expect(mapService).toBeInstanceOf(HonuaMapService);
    expect(ogc).toBeInstanceOf(HonuaOgcFeatures);
    expect(ogcViaFactory).toBeInstanceOf(HonuaOgcFeatures);
    expect(ogcCollection).toBeInstanceOf(HonuaOgcFeatureCollection);
    expect(helperService).toBeInstanceOf(HonuaService);
    expect(layer.serviceId).toBe("transport");
    expect(layer.layerId).toBe(5);
    expect(aliasLayer.layerId).toBe(6);
    expect(clientMapLayer.layerId).toBe(2);
    expect(mapService.serviceId).toBe("transport");
    expect(mapLayer.layerId).toBe(7);
    expect(mapLayerViaService.layerId).toBe(8);
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

  it("prevents queryFeaturesAll extraParams from overriding pagination controls", async () => {
    const requestedOffsets: string[] = [];
    const requestedRecordCounts: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        requestedOffsets.push(url.searchParams.get("resultOffset") ?? "");
        requestedRecordCounts.push(url.searchParams.get("resultRecordCount") ?? "");

        const offset = Number.parseInt(url.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 1 }, { id: 2 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 4);
    const allFeatures = await layer.queryFeaturesAll({
      pageSize: 2,
      extraParams: {
        resultOffset: 9999,
        resultRecordCount: 1,
      },
    });

    expect(allFeatures).toEqual([{ id: 1 }, { id: 2 }]);
    expect(requestedOffsets).toEqual(["0", "2"]);
    expect(requestedRecordCounts).toEqual(["2", "2"]);
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
    await mapService.queryLayer({
      layerId: 1,
      where: "1=1",
      outFields: ["*"],
      returnGeometry: false,
    });
    await mapService.identify({
      geometry: { x: -157.8, y: 21.3 },
      mapExtent: [-180, -90, 180, 90],
      imageDisplay: [1024, 768, 96],
    });
    await mapService.find({
      searchText: "Harbor",
    });
    await mapService.request({
      path: "layers",
      responseFormat: "pjson",
    });

    expect(requestedUrls[0]).toContain("/rest/services/basemap/MapServer?f=json");
    expect(requestedUrls[1]).toContain("/rest/services/basemap/MapServer/legend?");
    expect(requestedUrls[2]).toContain("/rest/services/basemap/MapServer/legend?");
    expect(requestedUrls[3]).toContain("/rest/services/basemap/MapServer/export?");
    expect(requestedUrls[4]).toContain("/rest/services/basemap/MapServer/export?");
    expect(requestedUrls[5]).toContain("/rest/services/basemap/MapServer/1/query?");
    expect(requestedUrls[6]).toContain("/rest/services/basemap/MapServer/identify?");
    expect(requestedUrls[7]).toContain("/rest/services/basemap/MapServer/find?");
    expect(requestedUrls[8]).toContain("/rest/services/basemap/MapServer/layers?f=pjson");
  });

  it("supports map-service query count/objectId/extent convenience helpers", async () => {
    const requestedUrls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = String(input);
        requestedUrls.push(url);
        const parsed = new URL(url);
        if (parsed.searchParams.get("returnCountOnly") === "true") {
          return new Response(JSON.stringify({ count: 6 }), { status: 200 });
        }
        if (parsed.searchParams.get("returnIdsOnly") === "true") {
          return new Response(JSON.stringify({ objectIds: [3, "4", "bad", 5] }), { status: 200 });
        }
        if (parsed.searchParams.get("returnExtentOnly") === "true") {
          return new Response(
            JSON.stringify({ extent: { xmin: 0, ymin: 0, xmax: 5, ymax: 5 }, count: 6 }),
            { status: 200 },
          );
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const mapService = client.mapService("basemap");
    const count = await mapService.queryLayerFeatureCount({ layerId: 2, where: "1=1" });
    const objectIds = await mapService.queryLayerObjectIds({ layerId: 2, where: "1=1" });
    const extent = await mapService.queryLayerExtent({ layerId: 2, where: "1=1" });

    expect(count).toBe(6);
    expect(objectIds).toEqual([3, 4, 5]);
    expect(extent).toEqual({
      extent: { xmin: 0, ymin: 0, xmax: 5, ymax: 5 },
      count: 6,
    });
    expect(requestedUrls[0]).toContain("/rest/services/basemap/MapServer/2/query?");
    expect(requestedUrls[0]).toContain("returnCountOnly=true");
    expect(requestedUrls[1]).toContain("/rest/services/basemap/MapServer/2/query?");
    expect(requestedUrls[1]).toContain("returnIdsOnly=true");
    expect(requestedUrls[2]).toContain("/rest/services/basemap/MapServer/2/query?");
    expect(requestedUrls[2]).toContain("returnExtentOnly=true");
  });

  it("supports map-service queryLayerFeaturesAll pagination helper", async () => {
    const requestedOffsets: string[] = [];
    const requestedRecordCounts: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const parsed = new URL(String(input));
        requestedOffsets.push(parsed.searchParams.get("resultOffset") ?? "");
        requestedRecordCounts.push(parsed.searchParams.get("resultRecordCount") ?? "");

        const offset = Number.parseInt(parsed.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 1 }, { id: 2 }] }), { status: 200 });
        }
        if (offset === 2) {
          return new Response(JSON.stringify({ features: [{ id: 3 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const mapService = client.mapService("basemap");
    const allFeatures = await mapService.queryLayerFeaturesAll({
      layerId: 2,
      pageSize: 2,
      extraParams: {
        resultOffset: 9999,
        resultRecordCount: 1,
      },
    });

    expect(allFeatures).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(requestedOffsets).toEqual(["0", "2"]);
    expect(requestedRecordCounts).toEqual(["2", "2"]);
  });

  it("supports map-service and map-layer related-record query helpers", async () => {
    const requestedUrls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        requestedUrls.push(String(input));
        return new Response(
          JSON.stringify({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] }),
          { status: 200 },
        );
      },
    });

    const mapService = client.mapService("basemap");
    const mapLayer = client.mapLayer("basemap", 4);

    const serviceRelated = await mapService.queryLayerRelatedRecords({
      layerId: 2,
      relationshipId: 9,
      objectIds: [1, 2],
    });
    const serviceRelatedAlias = await mapService.queryLayerRelatedFeatures({
      layerId: 3,
      relationshipId: 10,
      objectIds: [3],
    });
    const layerRelated = await mapLayer.queryRelatedRecords({
      relationshipId: 7,
      objectIds: [11],
    });
    const layerRelatedAlias = await mapLayer.queryRelatedFeatures({
      relationshipId: 8,
      objectIds: [12],
    });

    expect(serviceRelated).toEqual({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
    expect(serviceRelatedAlias).toEqual({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
    expect(layerRelated).toEqual({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
    expect(layerRelatedAlias).toEqual({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });

    expect(requestedUrls[0]).toContain("/rest/services/basemap/MapServer/2/queryRelatedRecords?");
    expect(requestedUrls[0]).toContain("relationshipId=9");
    expect(requestedUrls[0]).toContain("objectIds=1%2C2");
    expect(requestedUrls[1]).toContain("/rest/services/basemap/MapServer/3/queryRelatedRecords?");
    expect(requestedUrls[1]).toContain("relationshipId=10");
    expect(requestedUrls[2]).toContain("/rest/services/basemap/MapServer/4/queryRelatedRecords?");
    expect(requestedUrls[2]).toContain("relationshipId=7");
    expect(requestedUrls[3]).toContain("/rest/services/basemap/MapServer/4/queryRelatedRecords?");
    expect(requestedUrls[3]).toContain("relationshipId=8");
  });

  it("supports map-service layerIds and layers convenience helpers", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () =>
        new Response(
          JSON.stringify({ layers: [{ id: 0 }, { id: "1" }, { id: "bad" }] }),
          { status: 200 },
        ),
    });

    const mapService = client.mapService("basemap");
    const layerIds = await mapService.layerIds();
    const layers = await mapService.layers();

    expect(layerIds).toEqual([0, 1]);
    expect(layers).toHaveLength(2);
    expect(layers[0]).toBeInstanceOf(HonuaMapLayer);
    expect(layers[0]?.serviceId).toBe("basemap");
    expect(layers[0]?.layerId).toBe(0);
    expect(layers[1]?.layerId).toBe(1);
  });

  it("supports service-level featureLayers and mapLayers convenience helpers", async () => {
    const requests: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = String(input);
        requests.push(url);
        if (url.includes("/FeatureServer?")) {
          return new Response(
            JSON.stringify({ layers: [{ id: 0 }, { id: "1" }, { id: "bad" }] }),
            { status: 200 },
          );
        }
        if (url.includes("/MapServer?")) {
          return new Response(
            JSON.stringify({ layers: [{ id: 4 }, { id: "5" }, { id: "bad" }] }),
            { status: 200 },
          );
        }
        return new Response(JSON.stringify({ layers: [] }), { status: 200 });
      },
    });

    const service = client.service("transport");
    const featureLayers = await service.featureLayers();
    const mapLayers = await service.mapLayers();

    expect(featureLayers).toHaveLength(2);
    expect(featureLayers[0]).toBeInstanceOf(HonuaFeatureLayer);
    expect(featureLayers[0]?.serviceId).toBe("transport");
    expect(featureLayers[0]?.layerId).toBe(0);
    expect(featureLayers[1]?.layerId).toBe(1);

    expect(mapLayers).toHaveLength(2);
    expect(mapLayers[0]).toBeInstanceOf(HonuaMapLayer);
    expect(mapLayers[0]?.serviceId).toBe("transport");
    expect(mapLayers[0]?.layerId).toBe(4);
    expect(mapLayers[1]?.layerId).toBe(5);

    expect(requests.some((url) => url.includes("/FeatureServer?f=json"))).toBe(true);
    expect(requests.some((url) => url.includes("/MapServer?f=json"))).toBe(true);
  });

  it("supports map-layer wrappers for metadata, query helpers, and scoped requests", async () => {
    const requests: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = String(input);
        requests.push(url);
        const parsed = new URL(url);
        if (parsed.pathname.endsWith("/MapServer/4")) {
          return new Response(JSON.stringify({ id: 4, name: "Roads" }), { status: 200 });
        }
        if (parsed.searchParams.get("returnCountOnly") === "true") {
          return new Response(JSON.stringify({ count: 9 }), { status: 200 });
        }
        if (parsed.searchParams.get("returnIdsOnly") === "true") {
          return new Response(JSON.stringify({ objectIds: [10, "11", "bad"] }), { status: 200 });
        }
        if (parsed.searchParams.get("returnExtentOnly") === "true") {
          return new Response(
            JSON.stringify({ extent: { xmin: 1, ymin: 2, xmax: 3, ymax: 4 }, count: 9 }),
            { status: 200 },
          );
        }
        return new Response(JSON.stringify({ features: [{ id: 1 }] }), { status: 200 });
      },
    });

    const mapLayer = client.service("basemap").mapLayer(4);
    const query = mapLayer.createQuery();
    const metadata = await mapLayer.metadata();
    const features = await mapLayer.queryFeatures({ where: "1=1", outFields: ["*"] });
    const count = await mapLayer.queryFeatureCount({ where: "1=1" });
    const objectIds = await mapLayer.queryObjectIds({ where: "1=1" });
    const extent = await mapLayer.queryExtent({ where: "1=1" });
    await mapLayer.request({ path: "queryDomains" });

    expect(query).toEqual({ where: "1=1", outFields: ["*"], returnGeometry: true });
    expect(metadata).toEqual({ id: 4, name: "Roads" });
    expect(features).toEqual({ features: [{ id: 1 }] });
    expect(count).toBe(9);
    expect(objectIds).toEqual([10, 11]);
    expect(extent).toEqual({
      extent: { xmin: 1, ymin: 2, xmax: 3, ymax: 4 },
      count: 9,
    });
    expect(requests[0]).toContain("/rest/services/basemap/MapServer/4?f=json");
    expect(requests[1]).toContain("/rest/services/basemap/MapServer/4/query?");
    expect(requests[2]).toContain("returnCountOnly=true");
    expect(requests[3]).toContain("returnIdsOnly=true");
    expect(requests[4]).toContain("returnExtentOnly=true");
    expect(requests[5]).toContain("/rest/services/basemap/MapServer/4/queryDomains?f=json");
  });

  it("supports map-layer queryFeaturesAll pagination helper", async () => {
    const requestedOffsets: string[] = [];
    const requestedRecordCounts: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const parsed = new URL(String(input));
        requestedOffsets.push(parsed.searchParams.get("resultOffset") ?? "");
        requestedRecordCounts.push(parsed.searchParams.get("resultRecordCount") ?? "");

        const offset = Number.parseInt(parsed.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 10 }, { id: 11 }] }), { status: 200 });
        }
        if (offset === 2) {
          return new Response(JSON.stringify({ features: [{ id: 12 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const mapLayer = client.mapLayer("basemap", 4);
    const allFeatures = await mapLayer.queryFeaturesAll({
      pageSize: 2,
      extraParams: {
        resultOffset: 1234,
        resultRecordCount: 1,
      },
    });

    expect(allFeatures).toEqual([{ id: 10 }, { id: 11 }, { id: 12 }]);
    expect(requestedOffsets).toEqual(["0", "2"]);
    expect(requestedRecordCounts).toEqual(["2", "2"]);
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

  it("supports OGC itemsAll pagination helpers", async () => {
    const calls: string[] = [];
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        calls.push(url.toString());

        const match = url.pathname.match(/\/ogc\/features\/collections\/([^/]+)\/items$/);
        if (!match) {
          return new Response(JSON.stringify({ features: [] }), { status: 200 });
        }

        const collectionId = match[1];
        const limit = Number.parseInt(url.searchParams.get("limit") ?? "10", 10);
        const offset = Number.parseInt(url.searchParams.get("offset") ?? "0", 10);
        const data =
          collectionId === "3"
            ? [{ id: 1 }, { id: 2 }, { id: 3 }, { id: 4 }]
            : [{ id: "a" }, { id: "b" }, { id: "c" }];
        const page = data.slice(offset, offset + limit);
        return new Response(JSON.stringify({ features: page }), { status: 200 });
      },
    });

    const ogc = client.ogcFeatures();
    const allFromOgc = await ogc.itemsAll({
      collectionId: 3,
      pageSize: 2,
      limit: 3,
    });
    const collection = ogc.collection(7);
    const allFromCollection = await collection.itemsAll({
      pageSize: 2,
    });

    expect(allFromOgc).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(allFromCollection).toEqual([{ id: "a" }, { id: "b" }, { id: "c" }]);

    const collection3Calls = calls.filter((call) => call.includes("/ogc/features/collections/3/items?"));
    const collection7Calls = calls.filter((call) => call.includes("/ogc/features/collections/7/items?"));
    expect(collection3Calls).toHaveLength(2);
    expect(collection3Calls[0]).toContain("offset=0");
    expect(collection3Calls[0]).toContain("limit=2");
    expect(collection3Calls[1]).toContain("offset=2");
    expect(collection3Calls[1]).toContain("limit=1");
    expect(collection7Calls).toHaveLength(2);
    expect(collection7Calls[0]).toContain("offset=0");
    expect(collection7Calls[0]).toContain("limit=2");
    expect(collection7Calls[1]).toContain("offset=2");
    expect(collection7Calls[1]).toContain("limit=2");
  });

  it("queryFeaturesStream yields pages one at a time", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        const offset = Number.parseInt(url.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 1 }, { id: 2 }] }), { status: 200 });
        }
        if (offset === 2) {
          return new Response(JSON.stringify({ features: [{ id: 3 }, { id: 4 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 4);
    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 2 })) {
      pages.push(page);
    }

    expect(pages).toEqual([
      [{ id: 1 }, { id: 2 }],
      [{ id: 3 }, { id: 4 }],
    ]);
  });

  it("queryFeaturesStream stops on empty page", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () =>
        new Response(JSON.stringify({ features: [] }), { status: 200 }),
    });

    const layer = client.featureLayer("transport", 4);
    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 2 })) {
      pages.push(page);
    }

    expect(pages).toEqual([]);
  });

  it("queryFeaturesStream stops on partial page", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () =>
        new Response(JSON.stringify({ features: [{ id: 1 }] }), { status: 200 }),
    });

    const layer = client.featureLayer("transport", 4);
    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 5 })) {
      pages.push(page);
    }

    expect(pages).toEqual([[{ id: 1 }]]);
  });

  it("queryFeaturesStream stops at maxPages limit", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () =>
        new Response(JSON.stringify({ features: [{ id: 1 }, { id: 2 }] }), { status: 200 }),
    });

    const layer = client.featureLayer("transport", 4);
    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 2, maxPages: 2 })) {
      pages.push(page);
    }

    expect(pages).toHaveLength(2);
  });

  it("queryFeaturesStream caller can break early", async () => {
    let fetchCount = 0;
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async () => {
        fetchCount += 1;
        return new Response(JSON.stringify({ features: [{ id: fetchCount }] }), { status: 200 });
      },
    });

    const layer = client.featureLayer("transport", 4);
    const pages: unknown[][] = [];
    for await (const page of layer.queryFeaturesStream({ pageSize: 1 })) {
      pages.push(page);
      break;
    }

    expect(pages).toHaveLength(1);
    expect(fetchCount).toBe(1);
  });

  it("queryLayerFeaturesStream yields pages on map-service", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        const offset = Number.parseInt(url.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 1 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const mapService = client.mapService("basemap");
    const pages: unknown[][] = [];
    for await (const page of mapService.queryLayerFeaturesStream({ layerId: 2, pageSize: 5 })) {
      pages.push(page);
    }

    expect(pages).toEqual([[{ id: 1 }]]);
  });

  it("queryFeaturesStream on map-layer yields pages", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        const offset = Number.parseInt(url.searchParams.get("resultOffset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: 10 }, { id: 11 }] }), { status: 200 });
        }
        if (offset === 2) {
          return new Response(JSON.stringify({ features: [{ id: 12 }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const mapLayer = client.mapLayer("basemap", 4);
    const pages: unknown[][] = [];
    for await (const page of mapLayer.queryFeaturesStream({ pageSize: 2 })) {
      pages.push(page);
    }

    expect(pages).toEqual([
      [{ id: 10 }, { id: 11 }],
      [{ id: 12 }],
    ]);
  });

  it("OGC collection itemsStream yields pages", async () => {
    const client = new HonuaClient({
      baseUrl: "https://example.test",
      fetchFn: async (input) => {
        const url = new URL(String(input));
        const offset = Number.parseInt(url.searchParams.get("offset") ?? "0", 10);
        if (offset === 0) {
          return new Response(JSON.stringify({ features: [{ id: "a" }, { id: "b" }] }), { status: 200 });
        }
        if (offset === 2) {
          return new Response(JSON.stringify({ features: [{ id: "c" }] }), { status: 200 });
        }
        return new Response(JSON.stringify({ features: [] }), { status: 200 });
      },
    });

    const collection = client.ogcFeatures().collection(5);
    const pages: unknown[][] = [];
    for await (const page of collection.itemsStream({ pageSize: 2 })) {
      pages.push(page);
    }

    expect(pages).toEqual([
      [{ id: "a" }, { id: "b" }],
      [{ id: "c" }],
    ]);
  });
});
