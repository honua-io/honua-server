import { describe, expect, it } from "vitest";

import { CompatEventBus, RouteLayerCompat } from "../src/index.js";

describe("RouteLayerCompat", () => {
  it("supports lifecycle loading and watch handles", async () => {
    const layer = new RouteLayerCompat({
      stops: [
        { location: [0, 0] },
        { location: [1, 1] },
      ],
    });

    const loadStatusValues: unknown[] = [];
    const solvingValues: unknown[] = [];
    const routeValues: unknown[] = [];

    const loadStatusHandle = layer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const solvingHandle = layer.watch("solving", (value) => {
      solvingValues.push(value);
    });
    const routeHandle = layer.watch("route", (value) => {
      routeValues.push(value);
    });

    await layer.when();
    const solved = await layer.solve();
    layer.clearStops();

    loadStatusHandle.remove();
    solvingHandle.remove();
    routeHandle.remove();

    expect(layer.loaded).toBe(true);
    expect(layer.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(solvingValues).toEqual([true, false]);
    expect(routeValues).toEqual([solved, undefined]);
  });

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
