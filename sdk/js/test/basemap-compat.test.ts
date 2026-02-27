import { describe, expect, it } from "vitest";

import { BasemapCompat, CompatEventBus } from "../src/index.js";

describe("BasemapCompat", () => {
  it("supports watch handles for load and layer updates", async () => {
    const basemap = new BasemapCompat({
      id: "streets",
      baseLayers: [{ id: "base-1" }],
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const baseLayerCounts: number[] = [];

    const loadStatusHandle = basemap.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = basemap.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const baseLayersHandle = basemap.watch("baseLayers", (value) => {
      baseLayerCounts.push(Array.isArray(value) ? value.length : -1);
    });

    await basemap.load();
    basemap.setBaseLayers([{ id: "base-2" }, { id: "base-3" }]);

    loadStatusHandle.remove();
    loadedHandle.remove();
    baseLayersHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      baseLayers: baseLayerCounts.length,
    };

    await basemap.load();
    basemap.setBaseLayers([{ id: "base-4" }]);

    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(baseLayerCounts).toEqual([2]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(baseLayerCounts).toHaveLength(watchSnapshot.baseLayers);
  });

  it("supports basemap load/when lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const basemap = new BasemapCompat({
      id: "streets",
      eventBus,
    });

    expect(basemap.loaded).toBe(false);
    expect(basemap.loadStatus).toBe("not-loaded");

    let callbackBasemap: BasemapCompat | undefined;
    const loadedBasemap = await basemap.when((readyBasemap) => {
      callbackBasemap = readyBasemap;
    });

    expect(loadedBasemap).toBe(basemap);
    expect(callbackBasemap).toBe(basemap);
    expect(basemap.loaded).toBe(true);
    expect(basemap.loadStatus).toBe("loaded");
    expect(eventTypes).toContain("basemap.loading");
    expect(eventTypes).toContain("basemap.loaded");
  });

  it("supports constructing and mutating basemap layers with event notifications", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const basemap = new BasemapCompat({
      id: "streets",
      baseLayers: [{ id: "base-1" }],
      eventBus,
    });
    basemap.setBaseLayers([{ id: "base-2" }, { id: "base-3" }]);
    basemap.setReferenceLayers([{ id: "ref-1" }]);

    expect(basemap.id).toBe("streets");
    expect(basemap.title).toBe("streets");
    expect(basemap.baseLayers).toHaveLength(2);
    expect(basemap.referenceLayers).toHaveLength(1);
    expect(seenTypes).toContain("basemap.base-layers-changed");
    expect(seenTypes).toContain("basemap.reference-layers-changed");
  });

  it("creates basemaps from id", () => {
    const basemap = BasemapCompat.fromId("satellite");
    expect(basemap.id).toBe("satellite");
    expect(basemap.title).toBe("satellite");
    expect(basemap.baseLayers).toEqual([]);
    expect(basemap.referenceLayers).toEqual([]);
  });
});
