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

    let callbackView: MapViewCompat | undefined;
    const ready = await view.when((resolvedView) => {
      callbackView = resolvedView;
    });
    expect(ready).toBe(view);
    expect(callbackView).toBe(view);
    expect(view.map).toBe(map);
    expect(view.zoom).toBe(3);

    await view.goTo({ zoom: 8, center: [-155, 19.5] });
    expect(view.zoom).toBe(8);
    expect(view.center).toEqual([-155, 19.5]);

    view.destroy();
    expect(view.map).toBeUndefined();
  });

  it("supports watch and on handles", async () => {
    const view = new MapViewCompat({
      zoom: 2,
      center: [0, 0],
    });

    const zoomValues: unknown[] = [];
    const centerValues: unknown[] = [];
    const events: unknown[] = [];

    const zoomHandle = view.watch("zoom", (value) => {
      zoomValues.push(value);
    });
    const centerHandle = view.watch("center", (value) => {
      centerValues.push(value);
    });
    const eventHandle = view.on("go-to", (event) => {
      events.push(event);
    });

    await view.goTo({ zoom: 4, center: [10, 20] });
    expect(zoomValues).toEqual([4]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20] }]);

    zoomHandle.remove();
    centerHandle.remove();
    eventHandle.remove();

    await view.goTo({ zoom: 6, center: [30, 40] });
    expect(zoomValues).toEqual([4]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20] }]);
  });
});
