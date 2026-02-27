import { describe, expect, it } from "vitest";

import { BasemapLayerListCompat, CompatEventBus, MapCompat } from "../src/index.js";

describe("BasemapLayerListCompat", () => {
  it("supports when() and watch() lifecycle state and collection updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const streets = {
      id: "streets",
      baseLayers: [{ id: "streets-base" }],
      referenceLayers: [{ id: "streets-ref" }],
    };
    const topo = {
      id: "topo",
      baseLayers: [{ id: "topo-base" }],
      referenceLayers: [],
    };
    const map = new MapCompat({ basemap: streets, eventBus });
    const list = new BasemapLayerListCompat({ map, eventBus, autoRefresh: false });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const basemapValues: unknown[] = [];
    const loadStatusHandle = list.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = list.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const basemapHandle = list.watch("basemap", (value) => {
      basemapValues.push(value);
    });

    let callbackWidget: BasemapLayerListCompat | undefined;
    const widget = await list.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    list.setBasemap(topo);

    loadStatusHandle.remove();
    loadedHandle.remove();
    basemapHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      basemap: basemapValues.length,
    };
    list.setBasemap(streets);

    expect(widget).toBe(list);
    expect(callbackWidget).toBe(list);
    expect(list.loaded).toBe(true);
    expect(list.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(basemapValues).toEqual([streets, topo]);
    expect(seenTypes).toContain("basemap-layer-list.loading");
    expect(seenTypes).toContain("basemap-layer-list.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(basemapValues).toHaveLength(watchSnapshot.basemap);
  });

  it("emits refresh events and tracks basemap layer snapshots", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const streets = {
      id: "streets",
      baseLayers: [{ id: "streets-base" }],
      referenceLayers: [{ id: "streets-ref" }],
    };
    const topo = {
      id: "topo",
      baseLayers: [{ id: "topo-base" }],
      referenceLayers: [],
    };
    const map = new MapCompat({ basemap: streets, eventBus });
    const list = new BasemapLayerListCompat({ map, eventBus });

    expect(list.basemap).toBe(streets);
    expect(list.baseLayers).toEqual([{ id: "streets-base" }]);
    expect(list.referenceLayers).toEqual([{ id: "streets-ref" }]);

    map.setBasemap(topo);
    expect(list.basemap).toBe(topo);
    expect(list.baseLayers).toEqual([{ id: "topo-base" }]);
    expect(list.referenceLayers).toEqual([]);

    list.setBasemap(streets);
    expect(map.basemap).toBe(streets);

    expect(seenTypes).toContain("basemap-layer-list.refreshed");
    expect(seenTypes).toContain("map.basemap-changed");

    list.destroy();
  });
});
