import { describe, expect, it } from "vitest";

import { HonuaClient, HonuaHttpError } from "../src/honua.js";
import {
  BasemapToggleCompat,
  BasemapGalleryCompat,
  CompatEventBus,
  createArcGisTokenInterceptor,
  createEsriRequestInterceptors,
  EsriRequestInterceptorRegistry,
  FeatureLayerCompat,
  GraphicsLayerCompat,
  GroupLayerCompat,
  HomeCompat,
  IdentifyCompat,
  LayerListCompat,
  LegendCompat,
  LocateCompat,
  MapCompat,
  MapImageLayerCompat,
  MapViewCompat,
  MapViewUiCompat,
  PopupCompat,
  SearchCompat,
  ScaleBarCompat,
  TileLayerCompat,
  parseMapServiceUrl,
  SceneViewCompat,
  WebMapCompat,
} from "../src/esri-compat-entry.js";
import {
  buildJsMigrationReport,
  evaluateMigrationGates,
  runLayerReconciliation,
  runEsriCompatCodemod,
  scanArcGisUsage,
} from "../src/migration-entry.js";

describe("entrypoint modules", () => {
  it("exposes honua-first core entrypoint", () => {
    expect(HonuaClient).toBeTypeOf("function");
    expect(HonuaHttpError).toBeTypeOf("function");
  });

  it("exposes esri-compat entrypoint", () => {
    expect(FeatureLayerCompat).toBeTypeOf("function");
    expect(HomeCompat).toBeTypeOf("function");
    expect(BasemapToggleCompat).toBeTypeOf("function");
    expect(BasemapGalleryCompat).toBeTypeOf("function");
    expect(LocateCompat).toBeTypeOf("function");
    expect(ScaleBarCompat).toBeTypeOf("function");
    expect(CompatEventBus).toBeTypeOf("function");
    expect(createEsriRequestInterceptors).toBeTypeOf("function");
    expect(createArcGisTokenInterceptor).toBeTypeOf("function");
    expect(EsriRequestInterceptorRegistry).toBeTypeOf("function");
    expect(GraphicsLayerCompat).toBeTypeOf("function");
    expect(GroupLayerCompat).toBeTypeOf("function");
    expect(IdentifyCompat).toBeTypeOf("function");
    expect(LayerListCompat).toBeTypeOf("function");
    expect(LegendCompat).toBeTypeOf("function");
    expect(MapCompat).toBeTypeOf("function");
    expect(MapImageLayerCompat).toBeTypeOf("function");
    expect(MapViewCompat).toBeTypeOf("function");
    expect(MapViewUiCompat).toBeTypeOf("function");
    expect(PopupCompat).toBeTypeOf("function");
    expect(TileLayerCompat).toBeTypeOf("function");
    expect(parseMapServiceUrl).toBeTypeOf("function");
    expect(SceneViewCompat).toBeTypeOf("function");
    expect(SearchCompat).toBeTypeOf("function");
    expect(WebMapCompat).toBeTypeOf("function");
  });

  it("exposes migration tooling entrypoint", () => {
    expect(scanArcGisUsage).toBeTypeOf("function");
    expect(runEsriCompatCodemod).toBeTypeOf("function");
    expect(buildJsMigrationReport).toBeTypeOf("function");
    expect(evaluateMigrationGates).toBeTypeOf("function");
    expect(runLayerReconciliation).toBeTypeOf("function");
  });
});
