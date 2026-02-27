import { describe, expect, it } from "vitest";

import { CompatEventBus, MapImageLayerCompat, parseMapServiceUrl } from "../src/index.js";

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

  it("marks loadStatus failed when metadata load throws and can recover on retry", async () => {
    let attempts = 0;
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          attempts += 1;
          if (attempts === 1) {
            return Promise.reject(new Error("map metadata unavailable"));
          }
          return Promise.resolve({ mapName: "default" });
        }
      })() as any,
    });

    await expect(layer.load()).rejects.toThrow("map metadata unavailable");
    expect(layer.loaded).toBe(false);
    expect(layer.loadStatus).toBe("failed");

    await expect(layer.load()).resolves.toBe(layer);
    expect(layer.loaded).toBe(true);
    expect(layer.loadStatus).toBe("loaded");
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

  it("maps legend/find/identify helpers with serviceId and emits visibility events", async () => {
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

    expect(layer.allSublayers).toEqual([{ id: 1, title: "Parcels" }]);
    expect(layer.findSublayerById(1)).toEqual({ id: 1, title: "Parcels" });
    expect(layer.findSublayerById("1")).toEqual({ id: 1, title: "Parcels" });
    expect(layer.findSublayerById("missing")).toBeUndefined();

    layer.setVisibility(false);
    layer.setOpacity(0.5);
    layer.setSublayers([{ id: 2 }, { id: "5" }]);
    layer.setScaleRange(8000, 0);
    layer.setListMode("show");
    layer.setLegendEnabled(true);
    expect(layer.visible).toBe(false);
    expect(layer.opacity).toBe(0.5);
    expect(layer.sublayers).toEqual([{ id: 2 }, { id: "5" }]);
    expect(layer.allSublayers).toEqual([{ id: 2 }, { id: "5" }]);
    expect(layer.findSublayerById(2)).toEqual({ id: 2 });
    expect(layer.findSublayerById(5)).toEqual({ id: "5" });
    expect(layer.findSublayerById(999)).toBeUndefined();
    expect(layer.id).toBe("default-map");
    expect(layer.title).toBe("Default Map");
    expect(layer.minScale).toBe(8000);
    expect(layer.maxScale).toBe(0);
    expect(layer.listMode).toBe("show");
    expect(layer.legendEnabled).toBe(true);

    expect(requests).toHaveLength(4);
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
    expect(events).toContain("layer.visibility-changed");
    expect(events).toContain("layer.opacity-changed");
    expect(events).toContain("map-image-layer.sublayers-changed");
    expect(events).toContain("map-image-layer.scale-range-changed");
    expect(events).toContain("map-image-layer.list-mode-changed");
    expect(events).toContain("map-image-layer.legend-enabled-changed");
  });

  it("normalizes opacity to finite values in range [0, 1]", () => {
    const layer = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      opacity: Number.POSITIVE_INFINITY,
    });

    expect(layer.opacity).toBe(1);

    layer.setOpacity(-5);
    expect(layer.opacity).toBe(0);

    layer.setOpacity(10);
    expect(layer.opacity).toBe(1);
  });
});
