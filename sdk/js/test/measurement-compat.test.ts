import { describe, expect, it } from "vitest";

import { CompatEventBus, MeasurementCompat } from "../src/index.js";

describe("MeasurementCompat", () => {
  it("supports when() and watch() lifecycle and measurement state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const measurement = new MeasurementCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const activeToolValues: unknown[] = [];
    const measurementValues: unknown[] = [];
    const loadStatusHandle = measurement.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = measurement.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const activeToolHandle = measurement.watch("activeTool", (value) => {
      activeToolValues.push(value);
    });
    const lastMeasurementHandle = measurement.watch("lastMeasurement", (value) => {
      measurementValues.push(value);
    });

    let callbackWidget: MeasurementCompat | undefined;
    const widget = await measurement.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    measurement.start("distance");
    measurement.measureDistance([
      [-157.0, 21.3],
      [-157.01, 21.31],
    ]);
    measurement.clear();
    measurement.stop();

    loadStatusHandle.remove();
    loadedHandle.remove();
    activeToolHandle.remove();
    lastMeasurementHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      activeTool: activeToolValues.length,
      measurement: measurementValues.length,
    };
    measurement.start("area");
    measurement.clear();

    expect(widget).toBe(measurement);
    expect(callbackWidget).toBe(measurement);
    expect(measurement.loaded).toBe(true);
    expect(measurement.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(activeToolValues).toEqual(["distance", undefined]);
    expect(measurementValues.length).toBe(2);
    expect(measurementValues[1]).toBeUndefined();
    expect(seenTypes).toContain("measurement.loading");
    expect(seenTypes).toContain("measurement.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(activeToolValues).toHaveLength(watchSnapshot.activeTool);
    expect(measurementValues).toHaveLength(watchSnapshot.measurement);
  });

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
