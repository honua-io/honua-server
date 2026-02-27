import { describe, expect, it } from "vitest";

import {
  PointCompat,
  SimpleFillSymbolCompat,
  SimpleLineSymbolCompat,
  SimpleMarkerSymbolCompat,
} from "../src/index.js";

describe("geometry/symbol compat", () => {
  it("creates point geometry payloads", async () => {
    const point = new PointCompat({
      x: -157.81,
      y: 21.30,
      spatialReference: { wkid: 4326 },
    });
    const xValues: unknown[] = [];
    const xHandle = point.watch("x", (value) => {
      xValues.push(value);
    });
    await point.when();
    point.update({ x: -157.82 });
    xHandle.remove();
    const watchSnapshot = xValues.length;
    point.update({ x: -157.83 });

    expect(point.toJSON()).toEqual({
      x: -157.83,
      y: 21.3,
      z: undefined,
      m: undefined,
      spatialReference: { wkid: 4326 },
    });
    expect(point.loaded).toBe(true);
    expect(point.loadStatus).toBe("loaded");
    expect(xValues).toEqual([-157.82]);
    expect(xValues).toHaveLength(watchSnapshot);
  });

  it("creates marker/line symbols with clone support", async () => {
    const outline = new SimpleLineSymbolCompat({
      style: "dash",
      color: "white",
      width: 2,
    });
    const marker = new SimpleMarkerSymbolCompat({
      style: "square",
      color: "orange",
      size: 14,
      outline,
    });
    const outlineWidths: unknown[] = [];
    const markerSizes: unknown[] = [];
    const outlineWidthHandle = outline.watch("width", (value) => {
      outlineWidths.push(value);
    });
    const markerSizeHandle = marker.watch("size", (value) => {
      markerSizes.push(value);
    });

    await outline.when();
    await marker.when();
    outline.update({ width: 3 });
    marker.update({ size: 16 });
    outlineWidthHandle.remove();
    markerSizeHandle.remove();
    const watchSnapshot = {
      outlineWidths: outlineWidths.length,
      markerSizes: markerSizes.length,
    };

    outline.update({ width: 4 });
    marker.update({ size: 18 });

    expect(outline.clone().toJSON()).toEqual(outline.toJSON());
    expect(marker.clone().toJSON()).toEqual(marker.toJSON());

    const fill = new SimpleFillSymbolCompat({
      style: "solid",
      color: [255, 102, 0, 0.5],
      outline,
    });
    await fill.when();
    fill.update({ style: "diagonal-cross" });
    expect(fill.clone().toJSON()).toEqual(fill.toJSON());
    expect(outlineWidths).toEqual([3]);
    expect(markerSizes).toEqual([16]);
    expect(outlineWidths).toHaveLength(watchSnapshot.outlineWidths);
    expect(markerSizes).toHaveLength(watchSnapshot.markerSizes);
  });
});
