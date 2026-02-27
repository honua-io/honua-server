import { describe, expect, it } from "vitest";

import {
  getJsRuntimeParityMatrix,
  summarizeJsRuntimeParity,
} from "../src/migration/runtime-matrix.js";

describe("JS runtime parity matrix", () => {
  it("includes migration-critical runtime capabilities", () => {
    const matrix = getJsRuntimeParityMatrix();

    const featureLayerQuery = matrix.find(
      (entry) => entry.surface === "feature-layer" && entry.capability === "query-features",
    );
    const mapImageFind = matrix.find(
      (entry) => entry.surface === "map-image-layer" && entry.capability === "find",
    );
    const mapViewGoTo = matrix.find(
      (entry) => entry.surface === "map-view" && entry.capability === "navigation-go-to",
    );

    expect(featureLayerQuery).toMatchObject({
      arcGisJsApi: "FeatureLayer.queryFeatures",
      honuaCompat: "compat",
    });
    expect(mapImageFind).toMatchObject({
      arcGisJsApi: "MapImageLayer.find",
      honuaCompat: "compat",
    });
    expect(mapViewGoTo).toMatchObject({
      arcGisJsApi: "MapView.goTo",
      honuaCompat: "compat",
    });
  });

  it("summarizes runtime parity counts for both targets", () => {
    const matrix = getJsRuntimeParityMatrix();
    const summary = summarizeJsRuntimeParity(matrix);

    const totalHonua = Object.values(summary.honuaCompat).reduce((acc, value) => acc + value, 0);
    const totalEsriLeaflet = Object.values(summary.esriLeaflet).reduce(
      (acc, value) => acc + value,
      0,
    );

    expect(totalHonua).toBe(matrix.length);
    expect(totalEsriLeaflet).toBe(matrix.length);
    expect(summary.honuaCompat.compat).toBeGreaterThan(0);
    expect(summary.esriLeaflet.assisted).toBeGreaterThan(0);
  });
});
