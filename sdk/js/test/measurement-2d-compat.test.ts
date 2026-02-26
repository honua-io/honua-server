import { describe, expect, it } from "vitest";

import {
  AreaMeasurement2DCompat,
  CompatEventBus,
  DistanceMeasurement2DCompat,
} from "../src/index.js";

describe("measurement 2d compat", () => {
  it("computes distance measurements and emits updates", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const distance = new DistanceMeasurement2DCompat({
      eventBus,
      unit: "kilometers",
    });
    const result = distance.measure([
      [-157.8583, 21.3069],
      [-157.9076, 21.3035],
    ]);

    expect(result.tool).toBe("distance");
    expect(result.unit).toBe("kilometers");
    expect(result.value).toBeGreaterThan(0);
    expect(seenTypes).toContain("distance-measurement-2d.updated");
  });

  it("computes area measurements and emits updates", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const area = new AreaMeasurement2DCompat({
      eventBus,
      unit: "square-kilometers",
    });
    const result = area.measure([
      [-157.8583, 21.3069],
      [-157.8583, 21.3169],
      [-157.8483, 21.3169],
      [-157.8483, 21.3069],
      [-157.8583, 21.3069],
    ]);

    expect(result.tool).toBe("area");
    expect(result.unit).toBe("square-kilometers");
    expect(result.value).toBeGreaterThan(0);
    expect(seenTypes).toContain("area-measurement-2d.updated");
  });
});
