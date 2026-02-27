import { describe, expect, it } from "vitest";

import {
  ExtentCompat,
  PointCompat,
  PolygonCompat,
  PolylineCompat,
  SpatialReferenceCompat,
} from "../src/index.js";

describe("geometry primitives compat", () => {
  it("supports when() and watch() lifecycle for point and spatial reference", async () => {
    const point = new PointCompat({ x: 1, y: 2 });
    const pointLoadStatusValues: unknown[] = [];
    const pointLoadedValues: unknown[] = [];
    const pointLoadStatusHandle = point.watch("loadStatus", (value) => {
      pointLoadStatusValues.push(value);
    });
    const pointLoadedHandle = point.watch("loaded", (value) => {
      pointLoadedValues.push(value);
    });

    let callbackPoint: PointCompat | undefined;
    const resolvedPoint = await point.when((readyPoint) => {
      callbackPoint = readyPoint;
    });
    pointLoadStatusHandle.remove();
    pointLoadedHandle.remove();
    await point.load();

    const spatialReference = new SpatialReferenceCompat({ wkid: 3857 });
    const spatialReferenceLoadStatusValues: unknown[] = [];
    const spatialReferenceLoadedValues: unknown[] = [];
    const spatialReferenceLoadStatusHandle = spatialReference.watch("loadStatus", (value) => {
      spatialReferenceLoadStatusValues.push(value);
    });
    const spatialReferenceLoadedHandle = spatialReference.watch("loaded", (value) => {
      spatialReferenceLoadedValues.push(value);
    });

    let callbackSpatialReference: SpatialReferenceCompat | undefined;
    const resolvedSpatialReference = await spatialReference.when((readySpatialReference) => {
      callbackSpatialReference = readySpatialReference;
    });
    spatialReferenceLoadStatusHandle.remove();
    spatialReferenceLoadedHandle.remove();
    await spatialReference.load();

    expect(resolvedPoint).toBe(point);
    expect(callbackPoint).toBe(point);
    expect(point.loaded).toBe(true);
    expect(point.loadStatus).toBe("loaded");
    expect(pointLoadStatusValues).toEqual(["loading", "loaded"]);
    expect(pointLoadedValues).toEqual([true]);
    expect(resolvedSpatialReference).toBe(spatialReference);
    expect(callbackSpatialReference).toBe(spatialReference);
    expect(spatialReference.loaded).toBe(true);
    expect(spatialReference.loadStatus).toBe("loaded");
    expect(spatialReferenceLoadStatusValues).toEqual(["loading", "loaded"]);
    expect(spatialReferenceLoadedValues).toEqual([true]);
  });

  it("supports spatial reference and extent payloads", () => {
    const spatialReference = new SpatialReferenceCompat({ wkid: 3857, latestWkid: 102100 });
    const extent = new ExtentCompat({
      xmin: -10,
      ymin: -5,
      xmax: 30,
      ymax: 15,
      spatialReference,
    });
    const extentCenters: unknown[] = [];
    const extentSpatialReferences: unknown[] = [];
    const extentCenterHandle = extent.watch("center", (value) => {
      extentCenters.push(value);
    });
    const extentSpatialReferenceHandle = extent.watch("spatialReference", (value) => {
      extentSpatialReferences.push(value);
    });
    extent.update({
      xmax: 40,
      ymax: 25,
      spatialReference: { wkid: 4326 },
    });
    extentCenterHandle.remove();
    extentSpatialReferenceHandle.remove();
    const watchSnapshot = {
      centers: extentCenters.length,
      spatialReferences: extentSpatialReferences.length,
    };

    extent.update({
      xmax: 50,
      spatialReference: { wkid: 3857 },
    });

    spatialReference.update({
      latestWkid: 3857,
    });

    expect(spatialReference.toJSON()).toEqual({
      wkid: 3857,
      latestWkid: 3857,
      wkt: undefined,
      vcsWkid: undefined,
      latestVcsWkid: undefined,
    });
    expect(extent.center).toEqual({ x: 20, y: 10 });
    expect(extent.clone().toJSON()).toEqual(extent.toJSON());
    expect(extentCenters).toEqual([{ x: 15, y: 10 }]);
    expect(extentSpatialReferences).toEqual([{ wkid: 4326 }]);
    expect(extentCenters).toHaveLength(watchSnapshot.centers);
    expect(extentSpatialReferences).toHaveLength(watchSnapshot.spatialReferences);
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
    const pathCounts: number[] = [];
    const pathHandle = polyline.watch("paths", (value) => {
      if (Array.isArray(value)) {
        pathCounts.push(value.length);
      }
    });
    polyline.addPath([
      [2, 2],
      [3, 3],
    ]);
    expect(polyline.paths).toHaveLength(2);
    expect(polyline.removePath(0)).toBe(true);
    expect(polyline.removePath(99)).toBe(false);
    pathHandle.remove();
    const pathWatchCount = pathCounts.length;

    polyline.addPath([
      [4, 4],
      [5, 5],
    ]);

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
    const ringCounts: number[] = [];
    const ringHandle = polygon.watch("rings", (value) => {
      if (Array.isArray(value)) {
        ringCounts.push(value.length);
      }
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
    ringHandle.remove();
    const ringWatchCount = ringCounts.length;

    polygon.addRing([
      [1, 1],
      [2, 1],
      [1, 1],
    ]);

    expect(polyline.clone().toJSON()).toEqual(polyline.toJSON());
    expect(polygon.clone().toJSON()).toEqual(polygon.toJSON());
    expect(pathCounts).toEqual([2, 1]);
    expect(ringCounts).toEqual([2, 1]);
    expect(pathCounts).toHaveLength(pathWatchCount);
    expect(ringCounts).toHaveLength(ringWatchCount);
  });
});
