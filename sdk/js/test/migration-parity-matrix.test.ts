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
    const graphic = matrix.find((row) => row.kind === "graphic");
    const point = matrix.find((row) => row.kind === "point-geometry");
    const polyline = matrix.find((row) => row.kind === "polyline-geometry");
    const polygon = matrix.find((row) => row.kind === "polygon-geometry");
    const extent = matrix.find((row) => row.kind === "extent-geometry");
    const spatialReference = matrix.find((row) => row.kind === "spatial-reference");
    const color = matrix.find((row) => row.kind === "color");
    const simpleLineSymbol = matrix.find((row) => row.kind === "simple-line-symbol");
    const simpleMarkerSymbol = matrix.find((row) => row.kind === "simple-marker-symbol");
    const pictureMarkerSymbol = matrix.find((row) => row.kind === "picture-marker-symbol");
    const textSymbol = matrix.find((row) => row.kind === "text-symbol");
    const labelClass = matrix.find((row) => row.kind === "label-class");
    const simpleFillSymbol = matrix.find((row) => row.kind === "simple-fill-symbol");
    const classBreaksRenderer = matrix.find((row) => row.kind === "class-breaks-renderer");
    const simpleRenderer = matrix.find((row) => row.kind === "simple-renderer");
    const uniqueValueRenderer = matrix.find((row) => row.kind === "unique-value-renderer");
    const basemap = matrix.find((row) => row.kind === "basemap");
    const map = matrix.find((row) => row.kind === "map");
    const mapView = matrix.find((row) => row.kind === "map-view");
    const homeWidget = matrix.find((row) => row.kind === "home-widget");
    const basemapGalleryWidget = matrix.find((row) => row.kind === "basemap-gallery-widget");
    const layerList = matrix.find((row) => row.kind === "layer-list");
    const legendWidget = matrix.find((row) => row.kind === "legend-widget");
    const popupWidget = matrix.find((row) => row.kind === "popup-widget");
    const searchWidget = matrix.find((row) => row.kind === "search-widget");
    const track = matrix.find((row) => row.kind === "track-widget");
    const routeTask = matrix.find((row) => row.kind === "route-task");
    const swipe = matrix.find((row) => row.kind === "swipe-widget");
    const featureWidget = matrix.find((row) => row.kind === "feature-widget");
    const featureSet = matrix.find((row) => row.kind === "feature-set");
    const featureFormWidget = matrix.find((row) => row.kind === "feature-form-widget");
    const tableListWidget = matrix.find((row) => row.kind === "table-list-widget");
    const featureTemplatesWidget = matrix.find((row) => row.kind === "feature-templates-widget");
    const basemapLayerListWidget = matrix.find((row) => row.kind === "basemap-layer-list-widget");
    const distanceMeasurement2dWidget = matrix.find(
      (row) => row.kind === "distance-measurement-2d-widget",
    );
    const areaMeasurement2dWidget = matrix.find((row) => row.kind === "area-measurement-2d-widget");
    const query = matrix.find((row) => row.kind === "query");
    const oauthInfo = matrix.find((row) => row.kind === "oauth-info");
    const identityManager = matrix.find((row) => row.kind === "identity-manager");
    const esriRequest = matrix.find((row) => row.kind === "esri-request");
    const esriConfig = matrix.find((row) => row.kind === "esri-config");
    const reactiveUtils = matrix.find((row) => row.kind === "reactive-utils");

    expect(featureLayer).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(graphic).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(point).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(polyline).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(polygon).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(extent).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(spatialReference).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(color).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(simpleLineSymbol).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(simpleMarkerSymbol).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(pictureMarkerSymbol).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(textSymbol).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(labelClass).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(simpleFillSymbol).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(classBreaksRenderer).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(simpleRenderer).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(uniqueValueRenderer).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(basemap).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(map).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(mapView).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(homeWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(basemapGalleryWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(layerList).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(legendWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(popupWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
    });
    expect(searchWidget).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "compat",
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
    expect(featureSet).toMatchObject({
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
    expect(query).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(oauthInfo).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(identityManager).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(esriRequest).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(esriConfig).toMatchObject({
      honuaCompat: "compat",
      esriLeaflet: "assisted",
    });
    expect(reactiveUtils).toMatchObject({
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
