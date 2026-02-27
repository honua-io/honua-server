import { describe, expect, it } from "vitest";

import {
  PointCompat,
  SimpleFillSymbolCompat,
  SimpleLineSymbolCompat,
  SimpleMarkerSymbolCompat,
} from "../src/index.js";

describe("geometry/symbol compat", () => {
  it("creates point geometry payloads", () => {
    const point = new PointCompat({
      x: -157.81,
      y: 21.30,
      spatialReference: { wkid: 4326 },
    });

    expect(point.toJSON()).toEqual({
      x: -157.81,
      y: 21.3,
      z: undefined,
      m: undefined,
      spatialReference: { wkid: 4326 },
    });
  });

  it("creates marker/line symbols with clone support", () => {
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

    expect(outline.clone().toJSON()).toEqual(outline.toJSON());
    expect(marker.clone().toJSON()).toEqual(marker.toJSON());

    const fill = new SimpleFillSymbolCompat({
      style: "solid",
      color: [255, 102, 0, 0.5],
      outline,
    });
    expect(fill.clone().toJSON()).toEqual(fill.toJSON());
  });
});
