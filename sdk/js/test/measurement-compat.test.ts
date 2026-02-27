import { describe, expect, it } from "vitest";

import { CompatEventBus, MeasurementCompat } from "../src/index.js";

describe("MeasurementCompat", () => {
  it("measures distance and area with unit conversions", () => {
    const measurement = new MeasurementCompat({
      linearUnit: "kilometers",
      areaUnit: "square-kilometers",
    });

    const distance = measurement.measureDistance([
      [-157.0, 21.3],
      [-157.01, 21.31],
    ]);
    const area = measurement.measureArea([
      [-157.0, 21.3],
      [-157.0, 21.31],
      [-157.01, 21.31],
      [-157.0, 21.3],
    ]);

    expect(distance.tool).toBe("distance");
    expect(distance.value).toBeGreaterThan(0);
    expect(distance.unit).toBe("kilometers");
    expect(area.tool).toBe("area");
    expect(area.value).toBeGreaterThan(0);
    expect(area.unit).toBe("square-kilometers");
  });

  it("emits lifecycle events for start/stop/clear", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const measurement = new MeasurementCompat({ eventBus });
    measurement.start("distance");
    measurement.clear();
    measurement.stop();

    expect(seenTypes).toContain("measurement.started");
    expect(seenTypes).toContain("measurement.cleared");
    expect(seenTypes).toContain("measurement.stopped");
  });
});
