import { describe, expect, it } from "vitest";

import { CompatEventBus, RouteLayerCompat } from "../src/index.js";

describe("RouteLayerCompat", () => {
  it("solves a route from stops and emits lifecycle events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const layer = new RouteLayerCompat({
      eventBus,
      stops: [
        { name: "A", location: [-157.0, 21.3] },
        { name: "B", location: [-157.01, 21.31] },
      ],
    });

    const route = await layer.solve();
    expect(route).toBeDefined();
    expect(route?.path).toHaveLength(2);
    expect(route?.totalLengthMeters).toBeGreaterThan(0);
    expect(route?.totalTimeSeconds).toBeGreaterThan(0);
    expect(seenTypes).toContain("route-layer.solve-started");
    expect(seenTypes).toContain("route-layer.solve-completed");
  });

  it("supports stop mutation helpers", () => {
    const layer = new RouteLayerCompat();
    layer.addStop({ location: [0, 0] });
    layer.addStops([{ location: [1, 1] }, { location: [2, 2] }]);
    expect(layer.stops).toHaveLength(3);
    layer.clearStops();
    expect(layer.stops).toHaveLength(0);
  });
});
