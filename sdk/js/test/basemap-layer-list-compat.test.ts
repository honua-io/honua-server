import { describe, expect, it } from "vitest";

import { BasemapLayerListCompat, CompatEventBus, MapCompat } from "../src/index.js";

describe("BasemapLayerListCompat", () => {
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
