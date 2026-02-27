import { describe, expect, it } from "vitest";

import { CompatEventBus, IdentifyCompat, MapCompat, MapViewCompat } from "../src/index.js";

describe("IdentifyCompat", () => {
  it("runs identify across visible layers and opens popup with merged features", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const layerA = {
      id: "a",
      title: "Layer A",
      visible: true,
      identify: () =>
        Promise.resolve({
          results: [{ feature: { id: 1 } }, { feature: { id: 2 } }],
        }),
    };
    const layerB = {
      id: "b",
      title: "Layer B",
      visible: false,
      identify: () =>
        Promise.resolve({
          results: [{ feature: { id: 3 } }],
        }),
    };

    const map = new MapCompat({ eventBus, layers: [layerA, layerB] });
    const view = new MapViewCompat({ eventBus, map, center: [5, 6] });
    const identify = new IdentifyCompat({ view, eventBus });

    const result = await identify.identify({
      geometry: { x: 5, y: 6 },
      mapExtent: [0, 0, 10, 10],
      imageDisplay: [800, 600, 96],
    });

    expect(result.totalResultCount).toBe(2);
    expect(result.features).toEqual([{ id: 1 }, { id: 2 }]);
    expect(result.errors).toEqual([]);
    expect(result.layers).toHaveLength(1);
    expect(result.layers[0]?.layer).toBe(layerA);
    expect(result.layers[0]?.features).toEqual([{ id: 1 }, { id: 2 }]);
    expect(identify.lastResult).toBe(result);

    expect(view.popup.visible).toBe(true);
    expect(view.popup.features).toEqual([{ id: 1 }, { id: 2 }]);
    expect(view.popup.location).toEqual({ x: 5, y: 6 });
    expect(events).toContain("identify.started");
    expect(events).toContain("identify.layer-completed");
    expect(events).toContain("identify.popup-opened");
    expect(events).toContain("identify.completed");
  });

  it("supports identifyAt defaults from view dimensions/extent and includeHidden", async () => {
    const callArgs: unknown[] = [];
    const layerVisible = {
      id: "visible",
      visible: true,
      identify: (options: unknown) => {
        callArgs.push(options);
        return Promise.resolve({ results: [{ feature: { id: "v" } }] });
      },
    };
    const layerHidden = {
      id: "hidden",
      visible: false,
      identify: (options: unknown) => {
        callArgs.push(options);
        return Promise.resolve({ results: [{ feature: { id: "h" } }] });
      },
    };

    const eventBus = new CompatEventBus();
    const view = new MapViewCompat({
      eventBus,
      map: new MapCompat({ eventBus, layers: [layerVisible, layerHidden] }),
    }) as MapViewCompat & {
      width?: number;
      height?: number;
      extent?: { xmin: number; ymin: number; xmax: number; ymax: number };
    };
    view.width = 1024;
    view.height = 512;
    view.extent = { xmin: -1, ymin: -2, xmax: 3, ymax: 4 };

    const identify = new IdentifyCompat({
      view,
      includeHidden: true,
      autoOpenPopup: false,
    });

    const result = await identify.identifyAt({ x: 10, y: 11 });

    expect(result.totalResultCount).toBe(2);
    expect(callArgs).toHaveLength(2);
    expect(callArgs[0]).toMatchObject({
      geometry: { x: 10, y: 11 },
      mapExtent: [-1, -2, 3, 4],
      imageDisplay: [1024, 512, 96],
    });
    expect(callArgs[1]).toMatchObject({
      geometry: { x: 10, y: 11 },
      mapExtent: [-1, -2, 3, 4],
      imageDisplay: [1024, 512, 96],
    });
    expect(view.popup.visible).toBe(false);
  });
});
