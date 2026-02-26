import { describe, expect, it } from "vitest";

import { HonuaClient, HonuaHttpError } from "../src/honua.js";
import {
  FeatureLayerCompat,
  MapCompat,
  MapImageLayerCompat,
  MapViewCompat,
  SceneViewCompat,
  WebMapCompat,
} from "../src/esri-compat-entry.js";
import {
  buildJsMigrationReport,
  evaluateMigrationGates,
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
    expect(MapCompat).toBeTypeOf("function");
    expect(MapImageLayerCompat).toBeTypeOf("function");
    expect(MapViewCompat).toBeTypeOf("function");
    expect(SceneViewCompat).toBeTypeOf("function");
    expect(WebMapCompat).toBeTypeOf("function");
  });

  it("exposes migration tooling entrypoint", () => {
    expect(scanArcGisUsage).toBeTypeOf("function");
    expect(runEsriCompatCodemod).toBeTypeOf("function");
    expect(buildJsMigrationReport).toBeTypeOf("function");
    expect(evaluateMigrationGates).toBeTypeOf("function");
  });
});
