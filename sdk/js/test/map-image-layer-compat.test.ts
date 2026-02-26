import { describe, expect, it } from "vitest";

import { MapImageLayerCompat, parseMapServiceUrl } from "../src/index.js";

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
});
