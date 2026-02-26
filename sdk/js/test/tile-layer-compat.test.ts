import { describe, expect, it } from "vitest";

import { CompatEventBus, TileLayerCompat } from "../src/index.js";

describe("TileLayerCompat", () => {
  it("supports load/when/refresh lifecycle", async () => {
    let metadataCalls = 0;
    const layer = new TileLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          metadataCalls += 1;
          return Promise.resolve({ mapName: "default" });
        }
      })() as any,
    });

    expect(layer.loaded).toBe(false);

    await layer.when();
    expect(layer.loaded).toBe(true);
    expect(layer.metadata).toEqual({ mapName: "default" });
    expect(metadataCalls).toBe(1);

    layer.refresh();
    expect(layer.loaded).toBe(false);

    await layer.load();
    expect(metadataCalls).toBe(2);
  });

  it("provides tile URL and emits visibility/opacity events", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const layer = new TileLayerCompat({
      url: "https://example.test/rest/services/tiles/MapServer",
      id: "tiles-1",
      title: "Tiles",
      minScale: 24000,
      maxScale: 1200,
      listMode: "hide-children",
      eventBus,
    });

    expect(layer.getTileUrl(3, 4, 5)).toBe(
      "https://example.test/rest/services/tiles/MapServer/tile/3/4/5",
    );

    layer.setVisibility(false);
    layer.setOpacity(0.6);

    expect(layer.visible).toBe(false);
    expect(layer.opacity).toBe(0.6);
    expect(layer.id).toBe("tiles-1");
    expect(layer.title).toBe("Tiles");
    expect(layer.minScale).toBe(24000);
    expect(layer.maxScale).toBe(1200);
    expect(layer.listMode).toBe("hide-children");
    expect(events).toContain("layer.visibility-changed");
    expect(events).toContain("layer.opacity-changed");
  });
});
