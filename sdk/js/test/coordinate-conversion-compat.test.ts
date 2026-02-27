import { describe, expect, it } from "vitest";

import { CompatEventBus, CoordinateConversionCompat } from "../src/index.js";

describe("CoordinateConversionCompat", () => {
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
