import { describe, expect, it } from "vitest";

import { CompatEventBus, DirectionsCompat } from "../src/index.js";

describe("DirectionsCompat", () => {
  it("supports when() and watch() lifecycle plus route updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const directions = new DirectionsCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const routeValues: unknown[] = [];
    const loadStatusHandle = directions.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = directions.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const routeHandle = directions.watch("route", (value) => {
      routeValues.push(value);
    });

    let callbackWidget: DirectionsCompat | undefined;
    const widget = await directions.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    directions.setStops([
      { name: "Start", location: [-157.0, 21.3] },
      { name: "End", location: [-157.01, 21.31] },
    ]);
    await directions.solve();
    directions.clearStops();

    loadStatusHandle.remove();
    loadedHandle.remove();
    routeHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      route: routeValues.length,
    };
    directions.setStops([
      { name: "Start", location: [-157.0, 21.3] },
      { name: "End", location: [-157.01, 21.31] },
    ]);
    await directions.solve();

    expect(widget).toBe(directions);
    expect(callbackWidget).toBe(directions);
    expect(directions.loaded).toBe(true);
    expect(directions.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(routeValues.length).toBe(2);
    expect(routeValues[1]).toBeUndefined();
    expect(seenTypes).toContain("directions.loading");
    expect(seenTypes).toContain("directions.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(routeValues).toHaveLength(watchSnapshot.route);
  });

  it("solves directions using route layer and exposes summary", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const directions = new DirectionsCompat({ eventBus });
    directions.setStops([
      { name: "Start", location: [-157.0, 21.3] },
      { name: "End", location: [-157.01, 21.31] },
    ]);

    const route = await directions.solve();
    const summary = directions.getSummary();
    expect(route).toBeDefined();
    expect(summary).toBeDefined();
    expect(summary?.distanceMeters).toBeGreaterThan(0);
    expect(summary?.durationSeconds).toBeGreaterThan(0);
    expect(summary?.stopCount).toBe(2);
    expect(seenTypes).toContain("directions.solve-started");
    expect(seenTypes).toContain("directions.solve-completed");
  });

  it("supports adding and clearing stops", () => {
    const directions = new DirectionsCompat();
    directions.addStop({ location: [0, 0] });
    directions.addStop({ location: [1, 1] });
    expect(directions.layer.stops).toHaveLength(2);
    directions.clearStops();
    expect(directions.layer.stops).toHaveLength(0);
  });
});
