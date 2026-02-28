import { describe, expect, it } from "vitest";

import { CompatEventBus, MapImageLayerCompat, MapImageSublayerCompat, parseMapServiceUrl } from "../src/index.js";

describe("parseMapServiceUrl", () => {
  it("parses canonical map service URL", () => {
    const parsed = parseMapServiceUrl("https://example.test/rest/services/transport/MapServer");
    expect(parsed.baseUrl).toBe("https://example.test");
    expect(parsed.serviceId).toBe("transport");
  });

  it("parses map service URL with path prefix and optional layer suffix", () => {
    const parsed = parseMapServiceUrl("https://example.test/honua/rest/services/transport/MapServer/0");
    expect(parsed.baseUrl).toBe("https://example.test/honua");
    expect(parsed.serviceId).toBe("transport");
  });

  it("parses relative map service URL shape", () => {
    const parsed = parseMapServiceUrl("/rest/services/transport/MapServer");
    expect(parsed.baseUrl).toBe("");
    expect(parsed.serviceId).toBe("transport");
  });

  it("parses relative map service URL with path prefix", () => {
    const parsed = parseMapServiceUrl("/honua/rest/services/transport/MapServer");
    expect(parsed.baseUrl).toBe("/honua");
    expect(parsed.serviceId).toBe("transport");
  });

  it("throws on invalid URL shape", () => {
    expect(() => parseMapServiceUrl("https://example.test/rest/services/transport/FeatureServer/0")).toThrow();
  });
});

describe("MapImageLayerCompat", () => {
  it("supports watch handles for lifecycle and mutable properties", async () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({ mapName: "default" });
        }
      })() as any,
    });

    const loadStatusValues: unknown[] = [];
    const visibleValues: unknown[] = [];
    const opacityValues: unknown[] = [];
    const metadataValues: unknown[] = [];

    const loadStatusHandle = layer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const visibleHandle = layer.watch("visible", (value) => {
      visibleValues.push(value);
    });
    const opacityHandle = layer.watch("opacity", (value) => {
      opacityValues.push(value);
    });
    const metadataHandle = layer.watch("metadata", (value) => {
      metadataValues.push(value);
    });

    await layer.load();
    layer.setVisibility(false);
    layer.setOpacity(0.3);
    layer.refresh();

    loadStatusHandle.remove();
    visibleHandle.remove();
    opacityHandle.remove();
    metadataHandle.remove();

    layer.setVisibility(true);
    layer.setOpacity(0.8);

    expect(loadStatusValues).toEqual(["loading", "loaded", "not-loaded"]);
    expect(visibleValues).toEqual([false]);
    expect(opacityValues).toEqual([0.3]);
    expect(metadataValues).toEqual([{ mapName: "default" }, undefined]);
    expect(layer.loadStatus).toBe("not-loaded");
    expect(layer.loaded).toBe(false);
  });

  it("supports relative map service URLs with default client requests", async () => {
    const requestedUrls: string[] = [];
    const originalFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      requestedUrls.push(String(input));
      return new Response(JSON.stringify({ layers: [] }), { status: 200 });
    }) as typeof fetch;

    try {
      const layer = new MapImageLayerCompat({
        url: "/rest/services/default/MapServer",
      });
      await layer.getLegend();
    } finally {
      globalThis.fetch = originalFetch;
    }

    expect(requestedUrls[0]).toContain("/rest/services/default/MapServer/legend?");
    expect(requestedUrls[0]).not.toContain("honua.invalid");
  });

  it("supports prefixed relative map service URLs with default client requests", async () => {
    const requestedUrls: string[] = [];
    const originalFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      requestedUrls.push(String(input));
      return new Response(JSON.stringify({ layers: [] }), { status: 200 });
    }) as typeof fetch;

    try {
      const layer = new MapImageLayerCompat({
        url: "/honua/rest/services/default/MapServer",
      });
      await layer.getLegend();
    } finally {
      globalThis.fetch = originalFetch;
    }

    expect(requestedUrls[0]).toContain("/honua/rest/services/default/MapServer/legend?");
    expect(requestedUrls[0]).not.toContain("honua.invalid");
  });

  it("supports load/when/refresh lifecycle", async () => {
    let metadataCalls = 0;
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          metadataCalls += 1;
          return Promise.resolve({ mapName: "default" });
        }

        public exportMap(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.loaded).toBe(false);

    let callbackLayer: MapImageLayerCompat | undefined;
    const resolved = await layer.when((resolvedLayer) => {
      callbackLayer = resolvedLayer;
    });
    expect(resolved).toBe(layer);
    expect(callbackLayer).toBe(layer);
    expect(layer.loaded).toBe(true);
    expect(layer.metadata).toEqual({ mapName: "default" });
    expect(metadataCalls).toBe(1);

    layer.refresh();
    expect(layer.loaded).toBe(false);
    expect(layer.metadata).toBeUndefined();

    await layer.load();
    expect(metadataCalls).toBe(2);
  });

  it("hydrates sublayers from metadata when none are provided", async () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({
            layers: [
              { id: 0, name: "Roads" },
              { id: "1", title: "Parcels" },
              { id: "bad", name: "invalid" },
            ],
          });
        }
      })() as any,
    });

    await layer.load();

    expect(layer.sublayers).toHaveLength(2);
    expect(layer.sublayers.map((sublayer) => sublayer.id)).toEqual([0, 1]);
    expect(layer.allSublayers.map((sublayer) => sublayer.id)).toEqual([0, 1]);
    expect(layer.findSublayerById(0)?.title).toBe("Roads");
    expect(layer.findSublayerById(1)?.title).toBe("Parcels");
  });

  it("hydrates nested sublayer hierarchies from metadata and preserves wrapper identity", async () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({
            layers: [
              { id: 0, name: "Utilities" },
              { id: 1, name: "Water", parentLayerId: 0 },
              { id: 2, name: "Sewer", parentLayerId: 0 },
              { id: 3, name: "Roads" },
            ],
          });
        }
      })() as any,
    });

    await layer.load();

    expect(layer.sublayers).toHaveLength(2);
    expect(layer.sublayers.map((sublayer) => sublayer.id)).toEqual([0, 3]);
    expect(layer.allSublayers.map((sublayer) => sublayer.id)).toEqual([0, 1, 2, 3]);

    const utilities = layer.sublayer(0);
    expect(utilities).toBeDefined();
    if (!utilities) {
      throw new Error("expected utilities sublayer");
    }

    expect(utilities.sublayers.map((sublayer) => sublayer.id)).toEqual([1, 2]);
    expect(utilities.allSublayers.map((sublayer) => sublayer.id)).toEqual([1, 2]);
    expect(utilities.findSublayerById(2)?.id).toBe(2);
    expect(layer.findSublayerById(2)).toBe(utilities.findSublayerById(2));
  });

  it("preserves explicitly configured sublayers when metadata is loaded", async () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 9, title: "Configured" }],
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({
            layers: [{ id: 0, name: "Roads" }],
          });
        }
      })() as any,
    });

    await layer.load();

    expect(layer.allSublayers.map((sublayer) => sublayer.id)).toEqual([9]);
    expect(layer.findSublayerById(9)?.title).toBe("Configured");
    expect(layer.findSublayerById(0)).toBeUndefined();
  });

  it("sets failed loadStatus when metadata loading fails", async () => {
    const failures: unknown[] = [];
    const eventBus = new CompatEventBus();
    eventBus.on("map-image-layer.failed", (event) => {
      failures.push(event.payload);
    });

    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      eventBus,
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.reject(new Error("metadata-failed"));
        }
      })() as any,
    });

    await expect(layer.load()).rejects.toThrow("metadata-failed");
    expect(layer.loaded).toBe(false);
    expect(layer.metadata).toBeUndefined();
    expect(layer.loadStatus).toBe("failed");
    expect(failures).toHaveLength(1);
  });

  it("maps exportImage to map export request with serviceId", async () => {
    let exportRequest: unknown;
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public exportMap(request: unknown): Promise<unknown> {
          exportRequest = request;
          return Promise.resolve({ href: "/tmp/map.png" });
        }
      })() as any,
    });

    const result = await layer.exportImage({
      bbox: [-180, -90, 180, 90],
      size: [256, 256],
      format: "png32",
    });

    expect(result).toEqual({ href: "/tmp/map.png" });
    expect(JSON.stringify(exportRequest)).toContain('"serviceId":"default"');
    expect(JSON.stringify(exportRequest)).toContain('"bbox":[-180,-90,180,90]');
    expect(JSON.stringify(exportRequest)).toContain('"size":[256,256]');
    expect(JSON.stringify(exportRequest)).toContain('"format":"png32"');
  });

  it("maps legend/find/identify/query helpers with serviceId and emits visibility events", async () => {
    const requests: Array<{ kind: string; payload: unknown }> = [];
    const events: string[] = [];
    const eventBus = new CompatEventBus();
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      id: "default-map",
      title: "Default Map",
      sublayers: [{ id: 1, title: "Parcels" }],
      minScale: 12000,
      maxScale: 0,
      listMode: "hide",
      legendEnabled: false,
      eventBus,
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public exportMap(): Promise<unknown> {
          return Promise.resolve({});
        }

        public getMapLegend(request: unknown): Promise<unknown> {
          requests.push({ kind: "legend", payload: request });
          return Promise.resolve({ layers: [] });
        }

        public identifyMap(request: unknown): Promise<unknown> {
          requests.push({ kind: "identify", payload: request });
          return Promise.resolve({ results: [] });
        }

        public findMap(request: unknown): Promise<unknown> {
          requests.push({ kind: "find", payload: request });
          return Promise.resolve({ results: [{ value: "Parcels" }] });
        }

        public queryMapLayer(request: unknown): Promise<unknown> {
          requests.push({ kind: "query", payload: request });
          const candidate = request as {
            extraParams?: { returnCountOnly?: boolean; returnIdsOnly?: boolean; returnExtentOnly?: boolean };
          };
          if (candidate.extraParams?.returnCountOnly) {
            return Promise.resolve({ count: 4 });
          }
          if (candidate.extraParams?.returnIdsOnly) {
            return Promise.resolve({ objectIds: [1, 2, "bad", 3] });
          }
          if (candidate.extraParams?.returnExtentOnly) {
            return Promise.resolve({ extent: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 }, count: 4 });
          }
          return Promise.resolve({ features: [{ attributes: { OBJECTID: 1 } }] });
        }

        public queryMapRelatedRecords(request: unknown): Promise<unknown> {
          requests.push({ kind: "related", payload: request });
          return Promise.resolve({ relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }] });
        }
      })() as any,
    });

    expect(await layer.getLegend({ size: 18 })).toEqual({ layers: [] });
    expect(await layer.legend({ dynamicLayers: '[{"id":1}]' })).toEqual({ layers: [] });
    expect(
      await layer.find({
        searchText: "Parcels",
        searchFields: ["NAME"],
        layers: "all:0,1",
      }),
    ).toEqual({ results: [{ value: "Parcels" }] });
    expect(
      await layer.identify({
        geometry: { x: 1, y: 2 },
        mapExtent: [0, 0, 10, 10],
        imageDisplay: [256, 256, 96],
      }),
    ).toEqual({ results: [] });
    expect(
      await layer.queryFeatures({
        layerId: 1,
        where: "1=1",
        outFields: ["OBJECTID"],
        returnGeometry: false,
      }),
    ).toEqual({ features: [{ attributes: { OBJECTID: 1 } }] });
    expect(layer.createQuery(9)).toEqual({
      layerId: 9,
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    });
    expect(
      await layer.queryFeatureCount({
        layerId: 1,
        where: "1=1",
      }),
    ).toBe(4);
    expect(
      await layer.queryObjectIds({
        layerId: 1,
        where: "1=1",
      }),
    ).toEqual([1, 2, 3]);
    expect(
      await layer.queryExtent({
        layerId: 1,
        where: "1=1",
      }),
    ).toEqual({
      extent: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
      count: 4,
    });
    expect(
      await layer.queryRelatedFeatures({
        layerId: 1,
        relationshipId: 2,
        objectIds: [10, 11],
      }),
    ).toEqual({
      relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }],
    });
    expect(
      await layer.queryRelatedRecords({
        layerId: 1,
        relationshipId: 3,
        objectIds: [12],
      }),
    ).toEqual({
      relatedRecordGroups: [{ objectId: 1, relatedRecords: [] }],
    });

    expect(layer.allSublayers.map((sublayer) => sublayer.id)).toEqual([1]);
    expect(layer.allSublayers[0]?.title).toBe("Parcels");
    expect(layer.allSublayers[0]).toBe(layer.allSublayers[0]);
    expect(layer.findSublayerById(1)?.id).toBe(1);
    expect(layer.findSublayerById(1)?.title).toBe("Parcels");
    expect(layer.findSublayerById(1)).toBe(layer.findSublayerById(1));
    expect(layer.findSublayerById("1")?.id).toBe(1);
    expect(layer.findSublayerById("missing")).toBeUndefined();

    layer.setVisibility(false);
    layer.setOpacity(0.5);
    layer.setSublayers([{ id: 2 }, { id: "5" }]);
    layer.setScaleRange(8000, 0);
    layer.setListMode("show");
    layer.setLegendEnabled(true);
    expect(layer.visible).toBe(false);
    expect(layer.opacity).toBe(0.5);
    expect(layer.sublayers.map((sublayer) => sublayer.id)).toEqual([2, 5]);
    expect(layer.allSublayers.map((sublayer) => sublayer.id)).toEqual([2, 5]);
    expect(layer.findSublayerById(2)?.id).toBe(2);
    expect(layer.findSublayerById(5)?.id).toBe(5);
    expect(layer.findSublayerById(999)).toBeUndefined();
    expect(layer.id).toBe("default-map");
    expect(layer.title).toBe("Default Map");
    expect(layer.minScale).toBe(8000);
    expect(layer.maxScale).toBe(0);
    expect(layer.listMode).toBe("show");
    expect(layer.legendEnabled).toBe(true);

    layer.setOpacity(Number.NaN);
    expect(layer.opacity).toBe(1);
    layer.setOpacity(-4);
    expect(layer.opacity).toBe(0);
    layer.setOpacity(8);
    expect(layer.opacity).toBe(1);

    expect(requests).toHaveLength(10);
    expect(requests[0]).toMatchObject({
      kind: "legend",
      payload: { serviceId: "default", size: 18 },
    });
    expect(requests[1]).toMatchObject({
      kind: "legend",
      payload: { serviceId: "default", dynamicLayers: '[{"id":1}]' },
    });
    expect(requests[2]).toMatchObject({
      kind: "find",
      payload: {
        serviceId: "default",
        searchText: "Parcels",
        searchFields: ["NAME"],
        layers: "all:0,1",
      },
    });
    expect(requests[3]).toMatchObject({
      kind: "identify",
      payload: {
        serviceId: "default",
        geometry: { x: 1, y: 2 },
        mapExtent: [0, 0, 10, 10],
        imageDisplay: [256, 256, 96],
      },
    });
    expect(requests[4]).toMatchObject({
      kind: "query",
      payload: {
        serviceId: "default",
        layerId: 1,
        where: "1=1",
        outFields: ["OBJECTID"],
        returnGeometry: false,
      },
    });
    expect(requests[5]).toMatchObject({
      kind: "query",
      payload: {
        serviceId: "default",
        layerId: 1,
        where: "1=1",
        returnGeometry: false,
        outFields: "OBJECTID",
        extraParams: { returnCountOnly: true },
      },
    });
    expect(requests[6]).toMatchObject({
      kind: "query",
      payload: {
        serviceId: "default",
        layerId: 1,
        where: "1=1",
        returnGeometry: false,
        outFields: "OBJECTID",
        extraParams: { returnIdsOnly: true },
      },
    });
    expect(requests[7]).toMatchObject({
      kind: "query",
      payload: {
        serviceId: "default",
        layerId: 1,
        where: "1=1",
        returnGeometry: false,
        extraParams: { returnExtentOnly: true },
      },
    });
    expect(requests[8]).toMatchObject({
      kind: "related",
      payload: {
        serviceId: "default",
        layerId: 1,
        relationshipId: 2,
        objectIds: [10, 11],
      },
    });
    expect(requests[9]).toMatchObject({
      kind: "related",
      payload: {
        serviceId: "default",
        layerId: 1,
        relationshipId: 3,
        objectIds: [12],
      },
    });
    expect(events).toContain("layer.visibility-changed");
    expect(events).toContain("layer.opacity-changed");
    expect(events).toContain("map-image-layer.sublayers-changed");
    expect(events).toContain("map-image-layer.scale-range-changed");
    expect(events).toContain("map-image-layer.list-mode-changed");
    expect(events).toContain("map-image-layer.legend-enabled-changed");
  });

  it("builds sublayer wrappers with query helpers", async () => {
    const requests: unknown[] = [];
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 2, title: "Roads" }],
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryMapLayer(request: unknown): Promise<unknown> {
          requests.push(request);
          const candidate = request as {
            extraParams?: { returnCountOnly?: boolean; returnIdsOnly?: boolean; returnExtentOnly?: boolean };
          };
          if (candidate.extraParams?.returnCountOnly) {
            return Promise.resolve({ count: 12 });
          }
          if (candidate.extraParams?.returnIdsOnly) {
            return Promise.resolve({ objectIds: [7, "8", "bad", 9] });
          }
          if (candidate.extraParams?.returnExtentOnly) {
            return Promise.resolve({ extent: { xmin: 2, ymin: 3, xmax: 4, ymax: 5 }, count: 12 });
          }
          return Promise.resolve({ features: [{ attributes: { OBJECTID: 7 } }] });
        }

        public queryMapRelatedRecords(request: unknown): Promise<unknown> {
          requests.push(request);
          return Promise.resolve({ relatedRecordGroups: [{ objectId: 7, relatedRecords: [{ id: 70 }] }] });
        }
      })() as any,
    });

    const sublayer = layer.sublayer(2);
    expect(sublayer).toBeInstanceOf(MapImageSublayerCompat);
    if (!sublayer) {
      throw new Error("expected sublayer wrapper");
    }

    expect(sublayer.id).toBe(2);
    expect(sublayer.layerId).toBe(2);
    expect(sublayer.title).toBe("Roads");
    expect((sublayer.source as { id?: unknown }).id).toBe(2);
    expect(sublayer.createQuery()).toEqual({
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
      method: undefined,
      extraParams: undefined,
    });
    expect(await sublayer.queryFeatures({ where: "1=1", outFields: ["OBJECTID"], returnGeometry: false })).toEqual({
      features: [{ attributes: { OBJECTID: 7 } }],
    });
    expect(await sublayer.queryFeatureCount({ where: "1=1" })).toBe(12);
    expect(await sublayer.queryObjectIds({ where: "1=1" })).toEqual([7, 8, 9]);
    expect(await sublayer.queryExtent({ where: "1=1" })).toEqual({
      extent: { xmin: 2, ymin: 3, xmax: 4, ymax: 5 },
      count: 12,
    });
    expect(
      await sublayer.queryRelatedFeatures({
        relationshipId: 5,
        objectIds: [7],
      }),
    ).toEqual({
      relatedRecordGroups: [{ objectId: 7, relatedRecords: [{ id: 70 }] }],
    });
    expect(
      await sublayer.queryRelatedRecords({
        relationshipId: 6,
        objectIds: [8],
      }),
    ).toEqual({
      relatedRecordGroups: [{ objectId: 7, relatedRecords: [{ id: 70 }] }],
    });

    expect(requests).toHaveLength(6);
    expect(requests[0]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      where: "1=1",
      outFields: ["OBJECTID"],
      returnGeometry: false,
    });
    expect(requests[1]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      extraParams: { returnCountOnly: true },
    });
    expect(requests[2]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      extraParams: { returnIdsOnly: true },
    });
    expect(requests[3]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      extraParams: { returnExtentOnly: true },
    });
    expect(requests[4]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      relationshipId: 5,
      objectIds: [7],
    });
    expect(requests[5]).toMatchObject({
      serviceId: "default",
      layerId: 2,
      relationshipId: 6,
      objectIds: [8],
    });
    expect(layer.sublayer("missing")).toBeUndefined();
  });

  it("uses sublayer definitionExpression as default where clause for query helpers", async () => {
    const requests: unknown[] = [];
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 2, title: "Roads", definitionExpression: "status = 1" }],
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryMapLayer(request: unknown): Promise<unknown> {
          requests.push(request);
          const candidate = request as {
            extraParams?: { returnCountOnly?: boolean; returnIdsOnly?: boolean; returnExtentOnly?: boolean };
          };
          if (candidate.extraParams?.returnCountOnly) {
            return Promise.resolve({ count: 2 });
          }
          if (candidate.extraParams?.returnIdsOnly) {
            return Promise.resolve({ objectIds: [1, 2] });
          }
          if (candidate.extraParams?.returnExtentOnly) {
            return Promise.resolve({ extent: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 }, count: 2 });
          }
          return Promise.resolve({ features: [{ id: 1 }] });
        }

        public queryMapRelatedRecords(request: unknown): Promise<unknown> {
          requests.push(request);
          return Promise.resolve({ relatedRecordGroups: [] });
        }
      })() as any,
    });

    const sublayer = layer.sublayer(2);
    expect(sublayer).toBeDefined();
    if (!sublayer) {
      throw new Error("expected sublayer wrapper");
    }

    expect(sublayer.definitionExpression).toBe("status = 1");
    await sublayer.queryFeatures();
    await sublayer.queryFeatureCount();
    await sublayer.queryObjectIds();
    await sublayer.queryExtent();
    await sublayer.queryRelatedRecords({ relationshipId: 7, objectIds: [10] });

    sublayer.definitionExpression = undefined;
    expect(sublayer.definitionExpression).toBeUndefined();

    expect(requests).toHaveLength(5);
    expect(requests[0]).toMatchObject({ layerId: 2, where: "status = 1" });
    expect(requests[1]).toMatchObject({ layerId: 2, where: "status = 1", extraParams: { returnCountOnly: true } });
    expect(requests[2]).toMatchObject({ layerId: 2, where: "status = 1", extraParams: { returnIdsOnly: true } });
    expect(requests[3]).toMatchObject({ layerId: 2, where: "status = 1", extraParams: { returnExtentOnly: true } });
    expect(requests[4]).toMatchObject({
      layerId: 2,
      where: "status = 1",
      relationshipId: 7,
      objectIds: [10],
    });
  });

  it("reuses sublayer wrapper instances across reads and source updates", () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 2, title: "Roads" }],
    });

    const first = layer.sublayer(2);
    const second = layer.sublayer(2);
    expect(first).toBeDefined();
    expect(second).toBe(first);

    layer.setSublayers([{ id: 2, title: "Updated Roads" }]);
    const updated = layer.sublayer(2);
    expect(updated).toBe(first);
    expect(updated?.title).toBe("Updated Roads");
  });

  it("supports sublayer visibility getters/setters and visibility events", () => {
    const eventBus = new CompatEventBus();
    const events: unknown[] = [];
    eventBus.on("layer.visibility-changed", (event) => {
      events.push(event.payload);
    });

    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 2, title: "Roads" }],
      eventBus,
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const sublayer = layer.sublayer(2);
    expect(sublayer).toBeDefined();
    if (!sublayer) {
      throw new Error("expected sublayer wrapper");
    }

    expect(sublayer.visible).toBe(true);
    sublayer.visible = false;
    expect(sublayer.visible).toBe(false);
    expect((sublayer.source as { visible?: unknown }).visible).toBe(false);

    sublayer.setVisibility(true);
    expect(sublayer.visible).toBe(true);
    expect((sublayer.source as { visible?: unknown }).visible).toBe(true);
    expect(events).toEqual([{ layerId: 2, visible: true }]);
  });

  it("supports assigning sublayers through writable property and preserves wrappers", () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 1, title: "One" }],
    });

    const first = layer.sublayer(1);
    expect(first).toBeDefined();
    if (!first) {
      throw new Error("expected first sublayer wrapper");
    }

    layer.sublayers = layer.sublayers;
    expect(layer.sublayer(1)).toBe(first);

    layer.sublayers = [{ id: 5, title: "Five" }];
    expect(layer.sublayer(1)).toBeUndefined();
    expect(layer.sublayer(5)?.title).toBe("Five");
    expect(layer.sublayers.map((sublayer) => sublayer.id)).toEqual([5]);
  });

  it("supports queryFeaturesAll pagination for layer and sublayer wrappers", async () => {
    const requestedOffsets: string[] = [];
    const requestedRecordCounts: string[] = [];
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 2, title: "Roads" }],
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryMapLayer(request: unknown): Promise<unknown> {
          const extraParams = (request as { extraParams?: Record<string, unknown> }).extraParams ?? {};
          requestedOffsets.push(String(extraParams.resultOffset ?? ""));
          requestedRecordCounts.push(String(extraParams.resultRecordCount ?? ""));

          const offset = Number(extraParams.resultOffset ?? 0);
          if (offset === 0) {
            return Promise.resolve({ features: [{ id: 1 }, { id: 2 }] });
          }
          if (offset === 2) {
            return Promise.resolve({ features: [{ id: 3 }] });
          }
          return Promise.resolve({ features: [] });
        }
      })() as any,
    });

    const allLayerFeatures = await layer.queryFeaturesAll({
      layerId: 2,
      pageSize: 2,
      extraParams: {
        resultOffset: 9999,
        resultRecordCount: 1,
      },
    });
    const sublayer = layer.sublayer(2);
    expect(sublayer).toBeDefined();
    if (!sublayer) {
      throw new Error("expected sublayer wrapper");
    }
    const allSublayerFeatures = await sublayer.queryFeaturesAll({
      pageSize: 2,
      extraParams: {
        resultOffset: 42,
        resultRecordCount: 1,
      },
    });

    expect(allLayerFeatures).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(allSublayerFeatures).toEqual([{ id: 1 }, { id: 2 }, { id: 3 }]);
    expect(requestedOffsets).toEqual(["0", "2", "0", "2"]);
    expect(requestedRecordCounts).toEqual(["2", "2", "2", "2"]);
  });
});
