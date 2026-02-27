import { describe, expect, it } from "vitest";

import { CompatEventBus, RouteTaskCompat } from "../src/index.js";

describe("RouteTaskCompat", () => {
  it("solves route parameters and returns routeResults payload", async () => {
    const task = new RouteTaskCompat({
      url: "https://example.test/rest/services/network/RouteServer",
    });

    const result = await task.solve({
      stops: {
        features: [
          { geometry: { x: -157.8583, y: 21.3069 }, attributes: { Name: "Start" } },
          { geometry: { x: -157.9076, y: 21.3035 }, attributes: { Name: "End" } },
        ],
      },
      returnDirections: true,
    });

    expect(result.routeResults).toHaveLength(1);
    const [first] = result.routeResults;
    expect(first.route.geometry.paths[0]).toHaveLength(2);
    expect(first.route.attributes.Total_Kilometers).toBeGreaterThan(0);
    expect(first.route.attributes.Total_TravelTime).toBeGreaterThan(0);
    expect(first.directions?.features.length).toBeGreaterThan(0);
    expect(first.stops).toEqual([
      { name: "Start", location: [-157.8583, 21.3069] },
      { name: "End", location: [-157.9076, 21.3035] },
    ]);
  });

  it("emits solve lifecycle events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const task = new RouteTaskCompat({ eventBus });
    await task.solve({
      stops: [
        { location: [-157.8583, 21.3069] },
        { location: [-157.9076, 21.3035] },
      ],
    });

    expect(seenTypes).toContain("route-task.solve-started");
    expect(seenTypes).toContain("route-task.solve-completed");
  });
});
