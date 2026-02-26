import { describe, expect, it } from "vitest";

import { SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH } from "../src/migration/codemod.js";
import { getJsParityMatrix, summarizeJsParityMatrix } from "../src/migration/parity-matrix.js";

describe("JS parity matrix", () => {
  it("covers every codemod constructor kind", () => {
    const matrix = getJsParityMatrix();
    const coveredKinds = new Set(matrix.map((row) => row.kind));
    const codemodKinds = new Set(Object.values(SUPPORTED_ARCGIS_MODULE_KIND_BY_PATH));

    for (const kind of codemodKinds) {
      expect(coveredKinds.has(kind)).toBe(true);
    }
  });

  it("tracks esri-leaflet deterministic subset as compat and others as assisted/unsupported", () => {
    const matrix = getJsParityMatrix();
    const featureLayer = matrix.find((row) => row.kind === "feature-layer");
    const basemap = matrix.find((row) => row.kind === "basemap");
    const map = matrix.find((row) => row.kind === "map");
    const track = matrix.find((row) => row.kind === "track-widget");
    const routeTask = matrix.find((row) => row.kind === "route-task");
    const swipe = matrix.find((row) => row.kind === "swipe-widget");
    const featureWidget = matrix.find((row) => row.kind === "feature-widget");
    const featureFormWidget = matrix.find((row) => row.kind === "feature-form-widget");
    const tableListWidget = matrix.find((row) => row.kind === "table-list-widget");
    const featureTemplatesWidget = matrix.find((row) => row.kind === "feature-templates-widget");
    const basemapLayerListWidget = matrix.find((row) => row.kind === "basemap-layer-list-widget");
    const distanceMeasurement2dWidget = matrix.find(
      (row) => row.kind === "distance-measurement-2d-widget",
    );
    const areaMeasurement2dWidget = matrix.find((row) => row.kind === "area-measurement-2d-widget");

    expect(featureLayer).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(basemap).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(map).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(track).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(routeTask).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(swipe).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(featureWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(featureFormWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(tableListWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(featureTemplatesWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(basemapLayerListWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(distanceMeasurement2dWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(areaMeasurement2dWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
  });

  it("summarizes tier counts for both targets", () => {
    const matrix = getJsParityMatrix();
    const summary = summarizeJsParityMatrix(matrix);
    const totalHonua = Object.values(summary.honuaCompat).reduce((acc, value) => acc + value, 0);
    const totalEsriLeaflet = Object.values(summary.esriLeaflet).reduce(
      (acc, value) => acc + value,
      0,
    );

    expect(totalHonua).toBe(matrix.length);
    expect(totalEsriLeaflet).toBe(matrix.length);
    expect(summary.honuaCompat.compat).toBeGreaterThan(0);
    expect(summary.esriLeaflet.compat).toBeGreaterThan(0);
    expect(summary.esriLeaflet.assisted).toBeGreaterThan(0);
  });
});
