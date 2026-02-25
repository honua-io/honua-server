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
});
