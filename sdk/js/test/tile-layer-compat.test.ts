import { describe, expect, it } from "vitest";

import { CompatEventBus, TileLayerCompat } from "../src/index.js";

describe("TileLayerCompat", () => {
  it("supports watch handles for lifecycle and mutable properties", async () => {
    const layer = new TileLayerCompat({
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
    layer.setOpacity(0.4);
    layer.refresh();

    loadStatusHandle.remove();
    visibleHandle.remove();
    opacityHandle.remove();
    metadataHandle.remove();

    layer.setVisibility(true);
    layer.setOpacity(0.9);

    expect(loadStatusValues).toEqual(["loading", "loaded", "not-loaded"]);
    expect(visibleValues).toEqual([false]);
    expect(opacityValues).toEqual([0.4]);
    expect(metadataValues).toEqual([{ mapName: "default" }, undefined]);
    expect(layer.loadStatus).toBe("not-loaded");
    expect(layer.loaded).toBe(false);
  });

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
    layer.setScaleRange(6000, 0);
    layer.setListMode("show");

    expect(layer.visible).toBe(false);
    expect(layer.opacity).toBe(0.6);
    expect(layer.id).toBe("tiles-1");
    expect(layer.title).toBe("Tiles");
    expect(layer.minScale).toBe(6000);
    expect(layer.maxScale).toBe(0);
    expect(layer.listMode).toBe("show");
    expect(events).toContain("layer.visibility-changed");
    expect(events).toContain("layer.opacity-changed");
    expect(events).toContain("tile-layer.scale-range-changed");
    expect(events).toContain("tile-layer.list-mode-changed");
  });
});
