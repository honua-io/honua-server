import { describe, expect, it } from "vitest";

import { CompatEventBus, CoordinateConversionCompat } from "../src/index.js";

describe("CoordinateConversionCompat", () => {
  it("supports when() and watch() lifecycle and conversion updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const conversion = new CoordinateConversionCompat({
      eventBus,
      formats: ["lonlat"],
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const conversionCounts: number[] = [];
    const loadStatusHandle = conversion.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = conversion.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const conversionsHandle = conversion.watch("conversions", (value) => {
      conversionCounts.push(Array.isArray(value) ? value.length : -1);
    });

    let callbackWidget: CoordinateConversionCompat | undefined;
    const widget = await conversion.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    conversion.setLocation([-157.8583, 21.3069]);

    loadStatusHandle.remove();
    loadedHandle.remove();
    conversionsHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      conversions: conversionCounts.length,
    };
    conversion.setLocation([-157.9, 21.3]);

    expect(widget).toBe(conversion);
    expect(callbackWidget).toBe(conversion);
    expect(conversion.loaded).toBe(true);
    expect(conversion.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(conversionCounts).toEqual([1]);
    expect(seenTypes).toContain("coordinate-conversion.loading");
    expect(seenTypes).toContain("coordinate-conversion.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(conversionCounts).toHaveLength(watchSnapshot.conversions);
  });

  it("converts location into configured formats", () => {
    const conversion = new CoordinateConversionCompat({
      formats: ["lonlat", "dms"],
    });

    const results = conversion.setLocation([-157.8583, 21.3069]);
    expect(results).toHaveLength(2);
    expect(results[0]).toMatchObject({ format: "lonlat" });
    expect(results[1]).toMatchObject({ format: "dms" });
  });

  it("updates formats and emits events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const conversion = new CoordinateConversionCompat({ eventBus, formats: ["lonlat"] });
    conversion.addFormat("dms");
    expect(conversion.formats).toEqual(["lonlat", "dms"]);
    expect(conversion.removeFormat("lonlat")).toBe(true);
    expect(conversion.formats).toEqual(["dms"]);
    expect(seenTypes).toContain("coordinate-conversion.formats-updated");
  });
});
