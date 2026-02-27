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
    const navigationWidgets = matrix.find(
      (entry) => entry.surface === "widget" && entry.capability === "navigation-widgets",
    );
    const commonControls = matrix.find(
      (entry) => entry.surface === "control" && entry.capability === "common-map-controls",
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
      esriLeaflet: "compat",
    });
    expect(navigationWidgets).toMatchObject({
      arcGisJsApi: "BasemapGallery/Bookmarks/Expand",
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(commonControls).toMatchObject({
      arcGisJsApi: "Home/BasemapToggle/Locate/ScaleBar/Compass/Fullscreen/Zoom/Attribution",
      honuaCompat: "compat",
      esriLeaflet: "compat",
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
