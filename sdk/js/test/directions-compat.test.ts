import { describe, expect, it } from "vitest";

import { CompatEventBus, DirectionsCompat } from "../src/index.js";

describe("DirectionsCompat", () => {
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
