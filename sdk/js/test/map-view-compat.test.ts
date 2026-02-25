import { describe, expect, it } from "vitest";

import { MapCompat, MapViewCompat } from "../src/index.js";

describe("MapCompat", () => {
  it("tracks layers through add and remove", () => {
    const layerA = { id: "a" };
    const layerB = { id: "b" };
    const map = new MapCompat({ layers: [layerA] });

    map.add(layerB);
    expect(map.layers).toHaveLength(2);
    expect(map.layers[0]).toBe(layerA);
    expect(map.layers[1]).toBe(layerB);

    expect(map.remove(layerA)).toBe(true);
    expect(map.layers).toEqual([layerB]);
    expect(map.remove(layerA)).toBe(false);
  });
});

describe("MapViewCompat", () => {
  it("supports when() and goTo() state updates", async () => {
    const map = new MapCompat();
    const view = new MapViewCompat({
      map,
      container: "viewDiv",
      zoom: 3,
      center: [-157.8, 21.3],
    });

    const ready = await view.when();
    expect(ready).toBe(view);
    expect(view.map).toBe(map);
    expect(view.zoom).toBe(3);

    await view.goTo({ zoom: 8, center: [-155, 19.5] });
    expect(view.zoom).toBe(8);
    expect(view.center).toEqual([-155, 19.5]);

    view.destroy();
    expect(view.map).toBeUndefined();
  });
});
