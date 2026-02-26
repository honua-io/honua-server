import { describe, expect, it } from "vitest";

import { FeatureLayerCompat, parseFeatureLayerUrl } from "../src/index.js";

describe("parseFeatureLayerUrl", () => {
  it("parses canonical feature layer URL", () => {
    const parsed = parseFeatureLayerUrl(
      "https://example.test/rest/services/transport/FeatureServer/3",
    );
    expect(parsed.baseUrl).toBe("https://example.test");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(3);
  });

  it("parses URL with path prefix", () => {
    const parsed = parseFeatureLayerUrl(
      "https://example.test/honua/rest/services/transport/FeatureServer/8",
    );
    expect(parsed.baseUrl).toBe("https://example.test/honua");
    expect(parsed.serviceId).toBe("transport");
    expect(parsed.layerId).toBe(8);
  });

  it("throws on invalid URL shape", () => {
    expect(() =>
      parseFeatureLayerUrl("https://example.test/rest/services/transport/MapServer"),
    ).toThrow();
  });
});

describe("FeatureLayerCompat", () => {
  it("maps ArcGIS-style query to Honua query endpoint", async () => {
    let requestedUrl: string | undefined;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({ id: 1000 });
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          requestedUrl = JSON.stringify(request);
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const result = await layer.queryFeatures({
      where: "1=1",
      outFields: ["objectid", "name"],
      returnGeometry: true,
    });

    expect(result).toEqual({ features: [] });
    expect(requestedUrl).toContain("\"serviceId\":\"default\"");
    expect(requestedUrl).toContain("\"layerId\":1000");
    expect(requestedUrl).toContain("\"where\":\"1=1\"");
  });

  it("supports load/when lifecycle helpers", async () => {
    let metadataCalls = 0;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          metadataCalls += 1;
          return Promise.resolve({ id: 1000, name: "Sample Layer" });
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.loaded).toBe(false);

    let callbackLayer: FeatureLayerCompat | undefined;
    const resolved = await layer.when((resolvedLayer) => {
      callbackLayer = resolvedLayer;
    });

    expect(layer.loaded).toBe(true);
    expect(callbackLayer).toBe(layer);
    expect(resolved).toBe(layer);
    expect(layer.metadata).toEqual({ id: 1000, name: "Sample Layer" });
    expect(metadataCalls).toBe(1);

    await layer.load();
    expect(metadataCalls).toBe(1);
  });

  it("creates a default query object", () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.createQuery()).toEqual({
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    });
  });

  it("uses constructor defaults for outFields and definitionExpression", async () => {
    const capturedQueries: string[] = [];
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      outFields: ["OBJECTID", "NAME"],
      definitionExpression: "status = 1",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          capturedQueries.push(JSON.stringify(request));
          return Promise.resolve({ features: [] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    expect(layer.createQuery()).toEqual({
      where: "status = 1",
      outFields: ["OBJECTID", "NAME"],
      returnGeometry: true,
    });

    await layer.queryFeatures();
    await layer.queryObjectIds();
    await layer.queryFeatureCount();

    expect(capturedQueries[0]).toContain('"where":"status = 1"');
    expect(capturedQueries[0]).toContain('"outFields":["OBJECTID","NAME"]');
    expect(capturedQueries[1]).toContain('"where":"status = 1"');
    expect(capturedQueries[2]).toContain('"where":"status = 1"');
  });

  it("supports queryObjectIds with returnIdsOnly passthrough", async () => {
    let lastQuery: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          lastQuery = request;
          return Promise.resolve({ objectIds: [7, "8", "NaN"] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const objectIds = await layer.queryObjectIds({ where: "status = 'open'" });

    expect(objectIds).toEqual([7, 8]);
    expect(JSON.stringify(lastQuery)).toContain('"where":"status = \'open\'"');
    expect(JSON.stringify(lastQuery)).toContain('"returnIdsOnly":true');
  });

  it("supports queryObjectIds fallback parsing from feature attributes", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({
            features: [
              { attributes: { OBJECTID: 11 } },
              { attributes: { objectid: "12" } },
              { attributes: { id: 13 } },
              { attributes: { name: "missing-object-id" } },
            ],
          });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const objectIds = await layer.queryObjectIds();
    expect(objectIds).toEqual([11, 12, 13]);
  });

  it("supports queryFeatureCount with returnCountOnly and fallback feature length", async () => {
    let firstRequest: unknown;
    let queryCalls = 0;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          queryCalls += 1;
          if (queryCalls === 1) {
            firstRequest = request;
            return Promise.resolve({ count: 42 });
          }
          return Promise.resolve({ features: [{}, {}, {}] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const count = await layer.queryFeatureCount({ where: "1=1" });
    const fallbackCount = await layer.queryFeatureCount({ where: "1=0" });

    expect(count).toBe(42);
    expect(fallbackCount).toBe(3);
    expect(JSON.stringify(firstRequest)).toContain('"returnCountOnly":true');
  });

  it("supports queryExtent with returnExtentOnly passthrough", async () => {
    let lastQuery: unknown;
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(request: unknown): Promise<unknown> {
          lastQuery = request;
          return Promise.resolve({
            extent: {
              xmin: -158,
              ymin: 21,
              xmax: -157,
              ymax: 22,
              spatialReference: { wkid: 4326 },
            },
            count: 9,
          });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    const result = await layer.queryExtent({ where: "status = 'open'" });
    expect(result).toEqual({
      extent: {
        xmin: -158,
        ymin: 21,
        xmax: -157,
        ymax: 22,
        spatialReference: { wkid: 4326 },
      },
      count: 9,
    });
    expect(JSON.stringify(lastQuery)).toContain('"where":"status = \'open\'"');
    expect(JSON.stringify(lastQuery)).toContain('"returnExtentOnly":true');
  });

  it("returns null extent when queryExtent response shape is unknown", async () => {
    const layer = new FeatureLayerCompat({
      url: "https://example.test/rest/services/default/FeatureServer/1000",
      client: new (class {
        public getLayerMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }

        public queryFeatures(): Promise<unknown> {
          return Promise.resolve({ features: [{}, {}] });
        }

        public applyEdits(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });

    await expect(layer.queryExtent()).resolves.toEqual({ extent: null });
  });
});
