import { describe, expect, it } from "vitest";

import {
  ExtentCompat,
  PolygonCompat,
  PolylineCompat,
  SpatialReferenceCompat,
} from "../src/index.js";

describe("geometry primitives compat", () => {
  it("supports spatial reference and extent payloads", () => {
    const spatialReference = new SpatialReferenceCompat({ wkid: 3857, latestWkid: 102100 });
    const extent = new ExtentCompat({
      xmin: -10,
      ymin: -5,
      xmax: 30,
      ymax: 15,
      spatialReference,
    });

    expect(spatialReference.toJSON()).toEqual({
      wkid: 3857,
      latestWkid: 102100,
      wkt: undefined,
      vcsWkid: undefined,
      latestVcsWkid: undefined,
    });
    expect(extent.center).toEqual({ x: 10, y: 5 });
    expect(extent.clone().toJSON()).toEqual(extent.toJSON());
  });

  it("supports polyline and polygon editing semantics", () => {
    const polyline = new PolylineCompat({
      paths: [
        [
          [0, 0],
          [1, 1],
        ],
      ],
      hasM: true,
    });
    polyline.addPath([
      [2, 2],
      [3, 3],
    ]);
    expect(polyline.paths).toHaveLength(2);
    expect(polyline.removePath(0)).toBe(true);
    expect(polyline.removePath(99)).toBe(false);

    const polygon = new PolygonCompat({
      rings: [
        [
          [0, 0],
          [10, 0],
          [10, 10],
          [0, 0],
        ],
      ],
    });
    polygon.addRing([
      [2, 2],
      [3, 2],
      [3, 3],
      [2, 2],
    ]);
    expect(polygon.rings).toHaveLength(2);
    expect(polygon.removeRing(1)).toBe(true);
    expect(polygon.removeRing(-1)).toBe(false);

    expect(polyline.clone().toJSON()).toEqual(polyline.toJSON());
    expect(polygon.clone().toJSON()).toEqual(polygon.toJSON());
  });
});
