import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { runEsriCompatCodemod } from "../src/migration/codemod.js";

const tempDirs: string[] = [];

function makeTempProject(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "honua-arcgis-codemod-"));
  tempDirs.push(dir);
  return dir;
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("runEsriCompatCodemod", () => {
  it("rewrites safe FeatureLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "app.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const serviceUrl = 'https://example.test/rest/services/default/FeatureServer/0';",
        "const layer = new FeatureLayer({ url: serviceUrl });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new FeatureLayerCompat({ url: serviceUrl })");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
  });

  it("rewrites FeatureLayer shorthand url options", () => {
    const root = makeTempProject();
    const file = path.join(root, "shorthand.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const url = 'https://example.test/rest/services/default/FeatureServer/0';",
        "const layer = new FeatureLayer({ url });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayerCompat({ url })");
  });

  it("rewrites FeatureLayer constructors with supported options", () => {
    const root = makeTempProject();
    const file = path.join(root, "supported-options.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({",
        "  url: serviceUrl,",
        "  id: 'parcels',",
        "  title: 'Parcels',",
        "  outFields: ['*'],",
        "  definitionExpression: 'status = 1',",
        "  renderer: customRenderer,",
        "  popupTemplate,",
        "  labelingInfo: [labels],",
        "  labelsVisible: true,",
        "  opacity: 0.75,",
        "  visible: true,",
        "  minScale: 0,",
        "  maxScale: 0,",
        "  legendEnabled: true,",
        "  listMode: 'show',",
        "});",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayerCompat({");
    expect(nextSource).toContain("renderer: customRenderer");
    expect(nextSource).toContain("popupTemplate");
    expect(nextSource).toContain("labelingInfo: [labels]");
    expect(nextSource).toContain("legendEnabled: true");
    expect(nextSource).toContain("listMode: 'show'");
  });

  it("rewrites safe Graphic constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "graphic.ts");
    fs.writeFileSync(
      file,
      [
        "import Graphic from '@arcgis/core/Graphic';",
        "const graphic = new Graphic({",
        "  geometry: { x: -157.81, y: 21.30 },",
        "  symbol: { type: 'simple-marker' },",
        "  attributes: { OBJECTID: 10 },",
        "  popupTemplate: { title: '{OBJECTID}' },",
        "});",
        "void graphic;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.graphic).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { GraphicCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new GraphicCompat({");
    expect(nextSource).not.toContain("@arcgis/core/Graphic");
  });

  it("rewrites safe Query constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "query.ts");
    fs.writeFileSync(
      file,
      [
        "import Query from '@arcgis/core/rest/support/Query';",
        "const query = new Query({",
        "  where: \"status = 'active'\",",
        "  outFields: ['OBJECTID'],",
        "  returnGeometry: false,",
        "  orderByFields: ['OBJECTID DESC'],",
        "  num: 20,",
        "  start: 0,",
        "});",
        "void query;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.query).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { QueryCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new QueryCompat({");
    expect(nextSource).not.toContain("@arcgis/core/rest/support/Query");
  });

  it("rewrites safe geometry/symbol constructors and removes ArcGIS imports", () => {
    const root = makeTempProject();
    const file = path.join(root, "geometry-symbol.ts");
    fs.writeFileSync(
      file,
      [
        "import Point from '@arcgis/core/geometry/Point';",
        "import SimpleLineSymbol from '@arcgis/core/symbols/SimpleLineSymbol';",
        "import SimpleMarkerSymbol from '@arcgis/core/symbols/SimpleMarkerSymbol';",
        "const point = new Point({ x: -157.81, y: 21.30, spatialReference: { wkid: 4326 } });",
        "const outline = new SimpleLineSymbol({ style: 'solid', color: 'white', width: 1 });",
        "const symbol = new SimpleMarkerSymbol({ style: 'circle', color: 'orange', size: 12, outline });",
        "void point; void outline; void symbol;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["point-geometry"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["simple-line-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["simple-marker-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { PointCompat, SimpleLineSymbolCompat, SimpleMarkerSymbolCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain("new PointCompat({ x: -157.81, y: 21.30, spatialReference: { wkid: 4326 } })");
    expect(nextSource).toContain(
      "new SimpleLineSymbolCompat({ style: 'solid', color: 'white', width: 1 })",
    );
    expect(nextSource).toContain(
      "new SimpleMarkerSymbolCompat({ style: 'circle', color: 'orange', size: 12, outline })",
    );
    expect(nextSource).not.toContain("@arcgis/core/geometry/Point");
    expect(nextSource).not.toContain("@arcgis/core/symbols/SimpleLineSymbol");
    expect(nextSource).not.toContain("@arcgis/core/symbols/SimpleMarkerSymbol");
  });

  it("rewrites safe geometry primitive constructors and removes ArcGIS imports", () => {
    const root = makeTempProject();
    const file = path.join(root, "geometry-primitives.ts");
    fs.writeFileSync(
      file,
      [
        "import SpatialReference from '@arcgis/core/geometry/SpatialReference';",
        "import Extent from '@arcgis/core/geometry/Extent';",
        "import Polyline from '@arcgis/core/geometry/Polyline';",
        "import Polygon from '@arcgis/core/geometry/Polygon';",
        "const sr = new SpatialReference({ wkid: 4326 });",
        "const extent = new Extent({ xmin: -10, ymin: -5, xmax: 30, ymax: 15, spatialReference: sr });",
        "const polyline = new Polyline({ paths: [[[0, 0], [1, 1]]], spatialReference: sr });",
        "const polygon = new Polygon({ rings: [[[0, 0], [10, 0], [10, 10], [0, 0]]], spatialReference: sr });",
        "void sr; void extent; void polyline; void polygon;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(result.metrics.autoMigratedCallSites).toBe(4);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["spatial-reference"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["extent-geometry"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["polyline-geometry"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["polygon-geometry"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("SpatialReferenceCompat");
    expect(nextSource).toContain("ExtentCompat");
    expect(nextSource).toContain("PolylineCompat");
    expect(nextSource).toContain("PolygonCompat");
    expect(nextSource).toContain("new SpatialReferenceCompat({ wkid: 4326 })");
    expect(nextSource).toContain("new ExtentCompat({ xmin: -10, ymin: -5, xmax: 30, ymax: 15, spatialReference: sr })");
    expect(nextSource).toContain("new PolylineCompat({ paths: [[[0, 0], [1, 1]]], spatialReference: sr })");
    expect(nextSource).toContain("new PolygonCompat({ rings: [[[0, 0], [10, 0], [10, 10], [0, 0]]], spatialReference: sr })");
    expect(nextSource).not.toContain("@arcgis/core/geometry/SpatialReference");
    expect(nextSource).not.toContain("@arcgis/core/geometry/Extent");
    expect(nextSource).not.toContain("@arcgis/core/geometry/Polyline");
    expect(nextSource).not.toContain("@arcgis/core/geometry/Polygon");
  });

  it("creates manual TODO for unsupported extent options", () => {
    const root = makeTempProject();
    const file = path.join(root, "extent-manual.ts");
    fs.writeFileSync(
      file,
      [
        "import Extent from '@arcgis/core/geometry/Extent';",
        "const extent = new Extent({ xmin: 0, ymin: 0, xmax: 10, ymax: 10, type: 'extent' });",
        "void extent;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: false,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind["extent-geometry"]).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "extent-geometry",
          reason: "Extent options include unsupported properties; requires manual migration.",
        }),
      ]),
    );
  });

  it("rewrites safe labeling/symbol constructors and removes ArcGIS imports", () => {
    const root = makeTempProject();
    const file = path.join(root, "labeling.ts");
    fs.writeFileSync(
      file,
      [
        "import PictureMarkerSymbol from '@arcgis/core/symbols/PictureMarkerSymbol';",
        "import TextSymbol from '@arcgis/core/symbols/TextSymbol';",
        "import LabelClass from '@arcgis/core/layers/support/LabelClass';",
        "const marker = new PictureMarkerSymbol({ url: 'https://example.test/marker.png', width: 20, height: 20, opacity: 0.9 });",
        "const text = new TextSymbol({ text: 'Parcel', color: '#111', haloColor: '#fff', haloSize: 1 });",
        "const labels = new LabelClass({ labelExpressionInfo: { expression: '$feature.NAME' }, symbol: text, where: \"status = 'active'\" });",
        "void marker; void text; void labels;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["picture-marker-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["text-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["label-class"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("PictureMarkerSymbolCompat");
    expect(nextSource).toContain("TextSymbolCompat");
    expect(nextSource).toContain("LabelClassCompat");
    expect(nextSource).not.toContain("@arcgis/core/symbols/PictureMarkerSymbol");
    expect(nextSource).not.toContain("@arcgis/core/symbols/TextSymbol");
    expect(nextSource).not.toContain("@arcgis/core/layers/support/LabelClass");
  });

  it("creates manual TODO for unsupported label class options", () => {
    const root = makeTempProject();
    const file = path.join(root, "label-class-manual.ts");
    fs.writeFileSync(
      file,
      [
        "import LabelClass from '@arcgis/core/layers/support/LabelClass';",
        "const labels = new LabelClass({ labelExpressionInfo: { expression: '$feature.NAME' }, deconflictionStrategy: 'none' });",
        "void labels;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: false,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind["label-class"]).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "label-class",
          reason: "LabelClass options include unsupported properties; requires manual migration.",
        }),
      ]),
    );
  });

  it("rewrites safe color/symbol/renderer constructors and removes ArcGIS imports", () => {
    const root = makeTempProject();
    const file = path.join(root, "renderers.ts");
    fs.writeFileSync(
      file,
      [
        "import Color from '@arcgis/core/Color';",
        "import SimpleFillSymbol from '@arcgis/core/symbols/SimpleFillSymbol';",
        "import ClassBreaksRenderer from '@arcgis/core/renderers/ClassBreaksRenderer';",
        "import SimpleRenderer from '@arcgis/core/renderers/SimpleRenderer';",
        "import UniqueValueRenderer from '@arcgis/core/renderers/UniqueValueRenderer';",
        "const color = new Color([255, 102, 0, 0.8]);",
        "const fill = new SimpleFillSymbol({ style: 'solid', color, outline: { color: 'white', width: 1 } });",
        "const simple = new SimpleRenderer({ symbol: fill });",
        "const classBreaks = new ClassBreaksRenderer({",
        "  field: 'population',",
        "  minValue: 0,",
        "  classBreakInfos: [{ minValue: 0, maxValue: 1000, symbol: fill, label: '0-1000' }],",
        "});",
        "const unique = new UniqueValueRenderer({",
        "  field: 'status',",
        "  uniqueValueInfos: [",
        "    { value: 'open', label: 'Open', symbol: { type: 'simple-fill', color: 'green' } },",
        "  ],",
        "});",
        "void color; void fill; void simple; void classBreaks; void unique;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(5);
    expect(result.metrics.autoMigratedCallSites).toBe(5);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.color).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["simple-fill-symbol"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["class-breaks-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["simple-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["unique-value-renderer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("ColorCompat");
    expect(nextSource).toContain("SimpleFillSymbolCompat");
    expect(nextSource).toContain("ClassBreaksRendererCompat");
    expect(nextSource).toContain("SimpleRendererCompat");
    expect(nextSource).toContain("UniqueValueRendererCompat");
    expect(nextSource).toContain("new ColorCompat([255, 102, 0, 0.8])");
    expect(nextSource).toContain(
      "new SimpleFillSymbolCompat({ style: 'solid', color, outline: { color: 'white', width: 1 } })",
    );
    expect(nextSource).toContain("new SimpleRendererCompat({ symbol: fill })");
    expect(nextSource).toContain("new ClassBreaksRendererCompat({");
    expect(nextSource).toContain("new UniqueValueRendererCompat({");
    expect(nextSource).not.toContain("@arcgis/core/Color");
    expect(nextSource).not.toContain("@arcgis/core/symbols/SimpleFillSymbol");
    expect(nextSource).not.toContain("@arcgis/core/renderers/ClassBreaksRenderer");
    expect(nextSource).not.toContain("@arcgis/core/renderers/SimpleRenderer");
    expect(nextSource).not.toContain("@arcgis/core/renderers/UniqueValueRenderer");
  });

  it("creates manual TODO for unsupported renderer options", () => {
    const root = makeTempProject();
    const file = path.join(root, "renderer-manual.ts");
    fs.writeFileSync(
      file,
      [
        "import SimpleRenderer from '@arcgis/core/renderers/SimpleRenderer';",
        "const renderer = new SimpleRenderer({ symbol: { type: 'simple-fill' }, authoringInfo: { foo: true } });",
        "void renderer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: false,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind["simple-renderer"]).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "simple-renderer",
          reason: "SimpleRenderer options include unsupported properties; requires manual migration.",
        }),
      ]),
    );

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("@arcgis/core/renderers/SimpleRenderer");
    expect(nextSource).toContain("new SimpleRenderer({ symbol: { type: 'simple-fill' }, authoringInfo: { foo: true } })");
  });

  it("rewrites safe FeatureSet constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-set.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureSet from '@arcgis/core/rest/support/FeatureSet';",
        "const set = new FeatureSet({",
        "  fields: [{ name: 'OBJECTID', type: 'oid' }],",
        "  features: [{ attributes: { OBJECTID: 1 } }],",
        "  geometryType: 'esriGeometryPoint',",
        "  objectIdFieldName: 'OBJECTID',",
        "});",
        "void set;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-set"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { FeatureSetCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new FeatureSetCompat({");
    expect(nextSource).not.toContain("@arcgis/core/rest/support/FeatureSet");
  });

  it("rewrites safe MapImageLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "map-image-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "const layer = new MapImageLayer({",
        "  url: serviceUrl,",
        "  id: 'map-image',",
        "  title: 'Map Image',",
        "  sublayers: [{ id: 0 }],",
        "  opacity: 0.8,",
        "  visible: true,",
        "  minScale: 0,",
        "  maxScale: 0,",
        "  listMode: 'show',",
        "  legendEnabled: false,",
        "});",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapImageLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new MapImageLayerCompat({");
    expect(nextSource).toContain("id: 'map-image'");
    expect(nextSource).toContain("title: 'Map Image'");
    expect(nextSource).toContain("legendEnabled: false");
    expect(nextSource).not.toContain("@arcgis/core/layers/MapImageLayer");
  });

  it("rewrites safe TileLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "tile-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import TileLayer from '@arcgis/core/layers/TileLayer';",
        "const layer = new TileLayer({",
        "  url: serviceUrl,",
        "  id: 'tiles',",
        "  title: 'Tiles',",
        "  opacity: 0.6,",
        "  visible: true,",
        "  minScale: 25000,",
        "  maxScale: 0,",
        "  listMode: 'show',",
        "});",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { TileLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new TileLayerCompat({");
    expect(nextSource).toContain("id: 'tiles'");
    expect(nextSource).toContain("title: 'Tiles'");
    expect(nextSource).toContain("listMode: 'show'");
    expect(nextSource).not.toContain("@arcgis/core/layers/TileLayer");
  });

  it("rewrites safe RouteTask constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "route-task.ts");
    fs.writeFileSync(
      file,
      [
        "import RouteTask from '@arcgis/core/rest/route/RouteTask';",
        "const routeTask = new RouteTask({ url: routeUrl, apiKey: routeApiKey });",
        "void routeTask;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["route-task"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { RouteTaskCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new RouteTaskCompat({ url: routeUrl, apiKey: routeApiKey })");
    expect(nextSource).not.toContain("@arcgis/core/rest/route/RouteTask");
  });

  it("rewrites safe Basemap constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "basemap.ts");
    fs.writeFileSync(
      file,
      [
        "import Basemap from '@arcgis/core/Basemap';",
        "import Map from '@arcgis/core/Map';",
        "const basemap = new Basemap({ id: 'streets-vector' });",
        "const map = new Map({ basemap });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(2);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.basemap).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { BasemapCompat, MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const basemap = new BasemapCompat({ id: 'streets-vector' });");
    expect(nextSource).toContain("const map = new MapCompat({ basemap });");
    expect(nextSource).not.toContain("@arcgis/core/Basemap");
  });

  it("rewrites safe FeatureTable constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-table.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import FeatureTable from '@arcgis/core/widgets/FeatureTable';",
        "const layer = new FeatureLayer({ url: layerUrl });",
        "const table = new FeatureTable({ layer, container: 'feature-table', where: '1=1', objectIdField: 'OBJECTID' });",
        "void table;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(2);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-table-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { FeatureLayerCompat, FeatureTableCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain("new FeatureTableCompat({ layer, container: 'feature-table', where: '1=1', objectIdField: 'OBJECTID' })");
    expect(nextSource).not.toContain("@arcgis/core/widgets/FeatureTable");
  });

  it("rewrites FeatureTable with advanced options used by migration samples", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-table-advanced.ts");
    fs.writeFileSync(
      file,
      [
        "import MapView from '@arcgis/core/views/MapView';",
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import FeatureTable from '@arcgis/core/widgets/FeatureTable';",
        "const view = new MapView({ map, container: 'viewDiv', popup: { dockEnabled: true, dockOptions: { breakpoint: false } } });",
        "const layer = new FeatureLayer({ url: layerUrl });",
        "const table = new FeatureTable({",
        "  view,",
        "  layer,",
        "  container: 'tableDiv',",
        "  title: () => 'Rows',",
        "  description: 'Hydrants',",
        "  actionColumnConfig: { label: 'Go', icon: 'zoom-to-object', callback: (params) => view.goTo(params.feature) },",
        "  attachmentsEnabled: true,",
        "  paginationEnabled: true,",
        "  editingEnabled: true,",
        "  relatedRecordsEnabled: true,",
        "  where: '1=1',",
        "  filterGeometry: { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },",
        "  filterBySelectionEnabled: false,",
        "  highlightIds: [1, 2],",
        "  tableTemplate: { columnTemplates: [{ type: 'field', fieldName: 'FACILITYID', autoWidth: true }] },",
        "  multiSortEnabled: true,",
        "});",
        "void table;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["feature-table-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { FeatureLayerCompat, FeatureTableCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain("new MapViewCompat({ map, container: 'viewDiv', popup: { dockEnabled: true, dockOptions: { breakpoint: false } } })");
    expect(nextSource).toContain("const table = new FeatureTableCompat({");
    expect(nextSource).toContain("relatedRecordsEnabled: true");
    expect(nextSource).toContain("filterBySelectionEnabled: false");
    expect(nextSource).toContain("highlightIds: [1, 2]");
    expect(nextSource).toContain("multiSortEnabled: true");
    expect(nextSource).not.toContain("@arcgis/core/widgets/FeatureTable");
  });

  it("rewrites safe PopupTemplate constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "popup-template.ts");
    fs.writeFileSync(
      file,
      [
        "import PopupTemplate from '@arcgis/core/PopupTemplate';",
        "const template = new PopupTemplate({ title: '{NAME}', content: 'Parcel details', outFields: ['OBJECTID', 'NAME'] });",
        "void template;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["popup-template"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { PopupTemplateCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain(
      "const template = new PopupTemplateCompat({ title: '{NAME}', content: 'Parcel details', outFields: ['OBJECTID', 'NAME'] });",
    );
    expect(nextSource).not.toContain("@arcgis/core/PopupTemplate");
  });

  it("rewrites LayerList constructor when listItemCreatedFunction is present", () => {
    const root = makeTempProject();
    const file = path.join(root, "layer-list-actions.ts");
    fs.writeFileSync(
      file,
      [
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "const view = {};",
        "const layerList = new LayerList({",
        "  view,",
        "  container: 'layer-list',",
        "  includeHidden: true,",
        "  autoRefresh: false,",
        "  listItemCreatedFunction: (event) => {",
        "    event.item.actionsSections = [[{ id: 'zoom-to', title: 'Zoom To' }]];",
        "  },",
        "});",
        "void layerList;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["layer-list"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { LayerListCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const layerList = new LayerListCompat({");
    expect(nextSource).toContain("includeHidden: true");
    expect(nextSource).toContain("autoRefresh: false");
    expect(nextSource).toContain("listItemCreatedFunction: (event) => {");
    expect(nextSource).not.toContain("@arcgis/core/widgets/LayerList");
  });

  it("rewrites safe Feature constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-widget.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import Feature from '@arcgis/core/widgets/Feature';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const feature = new Feature({ view, container: 'feature-div', title: 'Selected', graphic: { attributes: { OBJECTID: 1 } } });",
        "void feature;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { FeatureCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new FeatureCompat({ view, container: 'feature-div', title: 'Selected', graphic: { attributes: { OBJECTID: 1 } } })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/Feature");
  });

  it("rewrites safe FeatureForm constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-form.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import FeatureForm from '@arcgis/core/widgets/FeatureForm';",
        "const layer = new FeatureLayer({ url: layerUrl });",
        "const form = new FeatureForm({ layer, container: 'feature-form', feature: { attributes: { OBJECTID: 1 } }, fieldConfig: [{ name: 'status' }], groupDisplay: 'all', headingLevel: 3, visibleElements: { description: true } });",
        "void form;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(2);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-form-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { FeatureFormCompat, FeatureLayerCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new FeatureFormCompat({ layer, container: 'feature-form', feature: { attributes: { OBJECTID: 1 } }, fieldConfig: [{ name: 'status' }], groupDisplay: 'all', headingLevel: 3, visibleElements: { description: true } })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/FeatureForm");
  });

  it("rewrites safe FeatureTemplates constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "feature-templates.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import FeatureTemplates from '@arcgis/core/widgets/FeatureTemplates';",
        "const layer = new FeatureLayer({ url: layerUrl });",
        "const templates = new FeatureTemplates({ layerInfos: [{ layer }], container: 'feature-templates', filterFunction: (item) => item.name !== 'Restricted', groupBy: 'layer' });",
        "void templates;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(2);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-templates-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { FeatureLayerCompat, FeatureTemplatesCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new FeatureTemplatesCompat({ layerInfos: [{ layer }], container: 'feature-templates', filterFunction: (item) => item.name !== 'Restricted', groupBy: 'layer' })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/FeatureTemplates");
  });

  it("rewrites safe Print constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "print.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import Print from '@arcgis/core/widgets/Print';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const printer = new Print({ view, container: 'print-div', printServiceUrl: printUrl, templateOptions: { format: 'pdf', layout: 'a4-landscape' } });",
        "void printer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["print-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { MapCompat, MapViewCompat, PrintCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain("new PrintCompat({ view, container: 'print-div', printServiceUrl: printUrl, templateOptions: { format: 'pdf', layout: 'a4-landscape' } })");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Print");
  });

  it("rewrites safe Swipe constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "swipe.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import Swipe from '@arcgis/core/widgets/Swipe';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const swipe = new Swipe({ view, container: 'swipe-div', position: 40, leadingLayers: [], trailingLayers: [] });",
        "void swipe;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["swipe-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { MapCompat, MapViewCompat, SwipeCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new SwipeCompat({ view, container: 'swipe-div', position: 40, leadingLayers: [], trailingLayers: [] })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/Swipe");
  });

  it("rewrites safe DistanceMeasurement2D and AreaMeasurement2D constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "measurement-2d.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import DistanceMeasurement2D from '@arcgis/core/widgets/DistanceMeasurement2D';",
        "import AreaMeasurement2D from '@arcgis/core/widgets/AreaMeasurement2D';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const distance = new DistanceMeasurement2D({ view, container: 'distance-2d', unit: 'kilometers' });",
        "const area = new AreaMeasurement2D({ view, container: 'area-2d', unit: 'square-kilometers' });",
        "void distance;",
        "void area;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(result.metrics.autoMigratedCallSites).toBe(4);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["distance-measurement-2d-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["area-measurement-2d-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { AreaMeasurement2DCompat, DistanceMeasurement2DCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "const distance = new DistanceMeasurement2DCompat({ view, container: 'distance-2d', unit: 'kilometers' });",
    );
    expect(nextSource).toContain(
      "const area = new AreaMeasurement2DCompat({ view, container: 'area-2d', unit: 'square-kilometers' });",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/DistanceMeasurement2D");
    expect(nextSource).not.toContain("@arcgis/core/widgets/AreaMeasurement2D");
  });

  it("rewrites safe TableList constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "table-list.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import TableList from '@arcgis/core/widgets/TableList';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const tableList = new TableList({ view, container: 'table-list', tables: [{ id: 'parcels' }], autoRefresh: false });",
        "void tableList;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["table-list-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { MapCompat, MapViewCompat, TableListCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new TableListCompat({ view, container: 'table-list', tables: [{ id: 'parcels' }], autoRefresh: false })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/TableList");
  });

  it("rewrites safe BasemapLayerList constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "basemap-layer-list.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import BasemapLayerList from '@arcgis/core/widgets/BasemapLayerList';",
        "const map = new Map({ basemap: 'streets' });",
        "const view = new MapView({ map, center: [0, 0], zoom: 2 });",
        "const basemapLayerList = new BasemapLayerList({ view, container: 'basemap-layer-list', autoRefresh: false });",
        "void basemapLayerList;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["basemap-layer-list-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { BasemapLayerListCompat, MapCompat, MapViewCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "new BasemapLayerListCompat({ view, container: 'basemap-layer-list', autoRefresh: false })",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/BasemapLayerList");
  });

  it("rewrites safe GraphicsLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "graphics-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import GraphicsLayer from '@arcgis/core/layers/GraphicsLayer';",
        "const graphics = new GraphicsLayer({ id: 'graphics', visible: true, opacity: 0.9 });",
        "void graphics;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["graphics-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { GraphicsLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const graphics = new GraphicsLayerCompat({ id: 'graphics', visible: true, opacity: 0.9 });");
    expect(nextSource).not.toContain("@arcgis/core/layers/GraphicsLayer");
  });

  it("rewrites safe GroupLayer constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "group-layer.ts");
    fs.writeFileSync(
      file,
      [
        "import GroupLayer from '@arcgis/core/layers/GroupLayer';",
        "const group = new GroupLayer({ id: 'group-1', layers: [{ id: 'child' }], visibilityMode: 'independent' });",
        "void group;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["group-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { GroupLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain(
      "const group = new GroupLayerCompat({ id: 'group-1', layers: [{ id: 'child' }], visibilityMode: 'independent' });",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/GroupLayer");
  });

  it("rewrites safe widget/control constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "widgets.ts");
    fs.writeFileSync(
      file,
      [
        "import LayerList from '@arcgis/core/widgets/LayerList';",
        "import Legend from '@arcgis/core/widgets/Legend';",
        "import Popup from '@arcgis/core/widgets/Popup';",
        "import Home from '@arcgis/core/widgets/Home';",
        "import BasemapToggle from '@arcgis/core/widgets/BasemapToggle';",
        "import Locate from '@arcgis/core/widgets/Locate';",
        "import ScaleBar from '@arcgis/core/widgets/ScaleBar';",
        "import Search from '@arcgis/core/widgets/Search';",
        "import BasemapGallery from '@arcgis/core/widgets/BasemapGallery';",
        "import Compass from '@arcgis/core/widgets/Compass';",
        "import Expand from '@arcgis/core/widgets/Expand';",
        "import Bookmarks from '@arcgis/core/widgets/Bookmarks';",
        "import Fullscreen from '@arcgis/core/widgets/Fullscreen';",
        "import Zoom from '@arcgis/core/widgets/Zoom';",
        "import Attribution from '@arcgis/core/widgets/Attribution';",
        "import Sketch from '@arcgis/core/widgets/Sketch';",
        "import Editor from '@arcgis/core/widgets/Editor';",
        "import Track from '@arcgis/core/widgets/Track';",
        "import Measurement from '@arcgis/core/widgets/Measurement';",
        "import TimeSlider from '@arcgis/core/widgets/TimeSlider';",
        "import RouteLayer from '@arcgis/core/layers/RouteLayer';",
        "import Directions from '@arcgis/core/widgets/Directions';",
        "import CoordinateConversion from '@arcgis/core/widgets/CoordinateConversion';",
        "const view = {};",
        "const routeLayer = new RouteLayer({ stops: [{ name: 'Start', location: [-157.0, 21.3] }, { name: 'End', location: [-157.01, 21.31] }] });",
        "const layerList = new LayerList({ view, container: 'layer-list-div' });",
        "const legend = new Legend({ view, container: 'legend-div', includeHidden: true, autoRefresh: false });",
        "const popup = new Popup({ view, container: 'popup-div', dockEnabled: true });",
        "const home = new Home({ view, container: 'home-div' });",
        "const basemapToggle = new BasemapToggle({ view, container: 'basemap-div', nextBasemap: 'satellite' });",
        "const locate = new Locate({ view, container: 'locate-div' });",
        "const scaleBar = new ScaleBar({ view, container: 'scale-div', unit: 'dual' });",
        "const search = new Search({ view, container: 'search-div', includeDefaultSources: false });",
        "const basemapGallery = new BasemapGallery({ view, container: 'gallery-div', autoRefresh: false });",
        "const compass = new Compass({ view });",
        "const expand = new Expand({ view, content: legend, expanded: false });",
        "const bookmarks = new Bookmarks({ view, bookmarks: [{ name: 'Home', target: { center: [0, 0], zoom: 2 } }] });",
        "const fullscreen = new Fullscreen({ view, container: 'full-div' });",
        "const zoom = new Zoom({ view, container: 'zoom-div', layout: 'vertical' });",
        "const attribution = new Attribution({ view, container: 'attrib-div', itemDelimiter: ' | ', attributions: ['Source A'] });",
        "const sketch = new Sketch({ view, layer: undefined, creationMode: 'update' });",
        "const editor = new Editor({ view, layerInfos: [], allowedWorkflows: ['create', 'update'] });",
        "const track = new Track({ view, container: 'track-div', goToLocationEnabled: true, useHeadingEnabled: true, rotationEnabled: true });",
        "const measurement = new Measurement({ view, container: 'measurement-div', activeTool: 'distance', linearUnit: 'kilometers', areaUnit: 'square-kilometers' });",
        "const timeSlider = new TimeSlider({ view, container: 'time-slider-div', mode: 'instant', stops: { values: ['2024-01-01T00:00:00.000Z', '2024-02-01T00:00:00.000Z'] } });",
        "const directions = new Directions({ view, layer: routeLayer, useDefaultRouteLayer: false, showSaveAsButton: false });",
        "const coordinateConversion = new CoordinateConversion({ view, container: 'coords-div', mode: 'live', multipleConversionsEnabled: true, formats: ['lonlat', 'dms'] });",
        "void layerList;",
        "void legend;",
        "void popup;",
        "void home;",
        "void basemapToggle;",
        "void locate;",
        "void scaleBar;",
        "void search;",
        "void basemapGallery;",
        "void compass;",
        "void expand;",
        "void bookmarks;",
        "void fullscreen;",
        "void zoom;",
        "void attribution;",
        "void sketch;",
        "void editor;",
        "void track;",
        "void measurement;",
        "void timeSlider;",
        "void routeLayer;",
        "void directions;",
        "void coordinateConversion;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(23);
    expect(result.metrics.autoMigratedCallSites).toBe(23);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["layer-list"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["route-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["legend-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["popup-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["home-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["basemap-toggle-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["locate-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["scale-bar-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["search-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["basemap-gallery-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["expand-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["compass-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["bookmarks-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["fullscreen-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["zoom-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["attribution-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["sketch-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["editor-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["track-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["measurement-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["time-slider-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["directions-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["coordinate-conversion-widget"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { AttributionCompat, BasemapGalleryCompat, BasemapToggleCompat, BookmarksCompat, CompassCompat, CoordinateConversionCompat, DirectionsCompat, EditorCompat, ExpandCompat, FullscreenCompat, HomeCompat, LayerListCompat, LegendCompat, LocateCompat, MeasurementCompat, PopupCompat, RouteLayerCompat, ScaleBarCompat, SearchCompat, SketchCompat, TimeSliderCompat, TrackCompat, ZoomCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).toContain(
      "const routeLayer = new RouteLayerCompat({ stops: [{ name: 'Start', location: [-157.0, 21.3] }, { name: 'End', location: [-157.01, 21.31] }] });",
    );
    expect(nextSource).toContain("const layerList = new LayerListCompat({ view, container: 'layer-list-div' });");
    expect(nextSource).toContain("const legend = new LegendCompat({ view, container: 'legend-div', includeHidden: true, autoRefresh: false });");
    expect(nextSource).toContain("const popup = new PopupCompat({ view, container: 'popup-div', dockEnabled: true });");
    expect(nextSource).toContain("const home = new HomeCompat({ view, container: 'home-div' });");
    expect(nextSource).toContain(
      "const basemapToggle = new BasemapToggleCompat({ view, container: 'basemap-div', nextBasemap: 'satellite' });",
    );
    expect(nextSource).toContain("const locate = new LocateCompat({ view, container: 'locate-div' });");
    expect(nextSource).toContain("const scaleBar = new ScaleBarCompat({ view, container: 'scale-div', unit: 'dual' });");
    expect(nextSource).toContain(
      "const search = new SearchCompat({ view, container: 'search-div', includeDefaultSources: false });",
    );
    expect(nextSource).toContain(
      "const basemapGallery = new BasemapGalleryCompat({ view, container: 'gallery-div', autoRefresh: false });",
    );
    expect(nextSource).toContain("const compass = new CompassCompat({ view });");
    expect(nextSource).toContain("const expand = new ExpandCompat({ view, content: legend, expanded: false });");
    expect(nextSource).toContain(
      "const bookmarks = new BookmarksCompat({ view, bookmarks: [{ name: 'Home', target: { center: [0, 0], zoom: 2 } }] });",
    );
    expect(nextSource).toContain("const fullscreen = new FullscreenCompat({ view, container: 'full-div' });");
    expect(nextSource).toContain("const zoom = new ZoomCompat({ view, container: 'zoom-div', layout: 'vertical' });");
    expect(nextSource).toContain(
      "const attribution = new AttributionCompat({ view, container: 'attrib-div', itemDelimiter: ' | ', attributions: ['Source A'] });",
    );
    expect(nextSource).toContain(
      "const sketch = new SketchCompat({ view, layer: undefined, creationMode: 'update' });",
    );
    expect(nextSource).toContain(
      "const editor = new EditorCompat({ view, layerInfos: [], allowedWorkflows: ['create', 'update'] });",
    );
    expect(nextSource).toContain(
      "const track = new TrackCompat({ view, container: 'track-div', goToLocationEnabled: true, useHeadingEnabled: true, rotationEnabled: true });",
    );
    expect(nextSource).toContain(
      "const measurement = new MeasurementCompat({ view, container: 'measurement-div', activeTool: 'distance', linearUnit: 'kilometers', areaUnit: 'square-kilometers' });",
    );
    expect(nextSource).toContain(
      "const timeSlider = new TimeSliderCompat({ view, container: 'time-slider-div', mode: 'instant', stops: { values: ['2024-01-01T00:00:00.000Z', '2024-02-01T00:00:00.000Z'] } });",
    );
    expect(nextSource).toContain(
      "const directions = new DirectionsCompat({ view, layer: routeLayer, useDefaultRouteLayer: false, showSaveAsButton: false });",
    );
    expect(nextSource).toContain(
      "const coordinateConversion = new CoordinateConversionCompat({ view, container: 'coords-div', mode: 'live', multipleConversionsEnabled: true, formats: ['lonlat', 'dms'] });",
    );
    expect(nextSource).not.toContain("@arcgis/core/widgets/LayerList");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Legend");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Popup");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Home");
    expect(nextSource).not.toContain("@arcgis/core/widgets/BasemapToggle");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Locate");
    expect(nextSource).not.toContain("@arcgis/core/widgets/ScaleBar");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Search");
    expect(nextSource).not.toContain("@arcgis/core/widgets/BasemapGallery");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Compass");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Expand");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Bookmarks");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Fullscreen");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Zoom");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Attribution");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Sketch");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Editor");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Track");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Measurement");
    expect(nextSource).not.toContain("@arcgis/core/widgets/TimeSlider");
    expect(nextSource).not.toContain("@arcgis/core/layers/RouteLayer");
    expect(nextSource).not.toContain("@arcgis/core/widgets/Directions");
    expect(nextSource).not.toContain("@arcgis/core/widgets/CoordinateConversion");
  });

  it("rewrites deterministic constructors for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "import TileLayer from '@arcgis/core/layers/TileLayer';",
        "const fl = new FeatureLayer({ url: serviceUrl });",
        "const mil = new MapImageLayer({ url: mapUrl, visible: true });",
        "const tiled = new TileLayer({ url: tileUrl, opacity: 0.4 });",
        "void fl; void mil; void tiled;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(3);
    expect(result.metrics.autoMigratedCallSites).toBe(3);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(nextSource).toContain("const fl = HonuaEsriLeaflet.featureLayer({ url: serviceUrl });");
    expect(nextSource).toContain(
      "const mil = HonuaEsriLeaflet.dynamicMapLayer({ url: mapUrl, visible: true });",
    );
    expect(nextSource).toContain(
      "const tiled = HonuaEsriLeaflet.tiledMapLayer({ url: tileUrl, opacity: 0.4 });",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).not.toContain("@arcgis/core/layers/MapImageLayer");
    expect(nextSource).not.toContain("@arcgis/core/layers/TileLayer");
  });

  it("keeps extended map image/tile options as manual TODOs for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-leaflet-extended-options.ts");
    fs.writeFileSync(
      file,
      [
        "import MapImageLayer from '@arcgis/core/layers/MapImageLayer';",
        "import TileLayer from '@arcgis/core/layers/TileLayer';",
        "const mil = new MapImageLayer({ url: mapUrl, legendEnabled: false });",
        "const tiled = new TileLayer({ url: tileUrl, listMode: 'hide' });",
        "void mil; void tiled;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: false,
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(2);
    expect(result.metrics.byKind["map-image-layer"]).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.metrics.byKind["tile-layer"]).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: "map-image-layer",
          reason: "MapImageLayer options include unsupported properties; requires manual migration.",
        }),
        expect.objectContaining({
          kind: "tile-layer",
          reason: "TileLayer options include unsupported properties; requires manual migration.",
        }),
      ]),
    );
  });

  it("keeps unsupported constructors as manual TODOs for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "unsupported-for-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "const map = new Map({ basemap: 'streets' });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
      annotateTodos: true,
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.kind).toBe("map");
    expect(result.manualTodos[0]?.reason).toContain("esri-leaflet mapping");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new Map({ basemap: 'streets' })");
    expect(nextSource).toContain("// TODO(honua-migrate)[map]:");
    expect(nextSource).not.toContain("HonuaEsriLeaflet.");
    expect(nextSource).toContain("@arcgis/core/Map");
  });

  it("rewrites constructors imported via named default alias", () => {
    const root = makeTempProject();
    const file = path.join(root, "default-alias.ts");
    fs.writeFileSync(
      file,
      [
        "import { default as FeatureLayer } from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const layer = new FeatureLayerCompat({ url: serviceUrl });");
  });

  it("rewrites constructors imported via namespace default access", () => {
    const root = makeTempProject();
    const file = path.join(root, "namespace-default.ts");
    fs.writeFileSync(
      file,
      [
        "import * as FeatureLayerModule from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayerModule.default({ url: serviceUrl });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const layer = new FeatureLayerCompat({ url: serviceUrl });");
  });

  it("rewrites namespace default map constructors and drops unused arcgis import", () => {
    const root = makeTempProject();
    const file = path.join(root, "namespace-map.ts");
    fs.writeFileSync(
      file,
      [
        "import * as MapModule from '@arcgis/core/Map';",
        "const map = new MapModule.default({ basemap: 'streets' });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).not.toContain("@arcgis/core/Map");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
  });

  it("rewrites require-default constructor expressions", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-default.ts");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map').default;",
        "const map = new Map({ basemap: 'streets' });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).not.toContain("require('@arcgis/core/Map').default");
    expect(nextSource).not.toContain("const Map = require('@arcgis/core/Map').default;");
  });

  it("rewrites destructured require default constructor expressions", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-destructured.ts");
    fs.writeFileSync(
      file,
      [
        "const { default: MapCtor } = require('@arcgis/core/Map');",
        "const map = new MapCtor({ basemap: 'streets' });",
        "void map;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { MapCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets' });");
    expect(nextSource).not.toContain("require('@arcgis/core/Map')");
    expect(nextSource).not.toContain("const { default: MapCtor } = require('@arcgis/core/Map');");
  });

  it("keeps require constructor in .cjs and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-map.cjs");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map');",
        "const map = new Map({ basemap: 'streets' });",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.reason).toContain("CommonJS require constructors");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("const Map = require('@arcgis/core/Map');");
    expect(nextSource).toContain("const map = new Map({ basemap: 'streets' });");
    expect(nextSource).not.toContain("@honua/sdk-esri-compat");
  });

  it("keeps require constructor in CommonJS .js modules and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "require-map.js");
    fs.writeFileSync(
      file,
      [
        "const Map = require('@arcgis/core/Map');",
        "const map = new Map({ basemap: 'streets' });",
        "module.exports = { map };",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 0,
      manual: 1,
    });
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0]?.reason).toContain("CommonJS require constructors");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("const Map = require('@arcgis/core/Map');");
    expect(nextSource).toContain("const map = new Map({ basemap: 'streets' });");
    expect(nextSource).toContain("module.exports = { map };");
    expect(nextSource).not.toContain("@honua/sdk-esri-compat");
  });

  it("rewrites safe Map, MapView, SceneView, and WebMap constructors", () => {
    const root = makeTempProject();
    const file = path.join(root, "view.ts");
    fs.writeFileSync(
      file,
      [
        "import Map from '@arcgis/core/Map';",
        "import MapView from '@arcgis/core/views/MapView';",
        "import SceneView from '@arcgis/core/views/SceneView';",
        "import WebMap from '@arcgis/core/WebMap';",
        "const map = new Map({ basemap: 'streets', ground: 'world-elevation', tables: [tableLayer], spatialReference: { wkid: 3857 } });",
        "const view = new MapView({ map, zoom: 4, scale: 5000000, rotation: 10, extent: initialExtent, constraints: { minZoom: 2 }, padding: { left: 8 }, highlightOptions: { color: '#ff0' }, spatialReference: { wkid: 4326 } });",
        "const scene = new SceneView({ map, viewingMode: 'global', qualityProfile: 'high', scale: 4000000, rotation: 15, spatialReference: { wkid: 3857 }, popup: { dockEnabled: true } });",
        "const webMap = new WebMap({ portalItem: { id: 'abc123' }, basemap: 'satellite', layers: [layerA], tables: [tableLayer], ground: 'world-elevation', spatialReference: { wkid: 3857 } });",
        "void map; void view; void scene; void webMap;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(4);
    expect(result.metrics.autoMigratedCallSites).toBe(4);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["scene-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["web-map"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { MapCompat, MapViewCompat, SceneViewCompat, WebMapCompat } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).not.toContain("@arcgis/core/Map");
    expect(nextSource).not.toContain("@arcgis/core/views/MapView");
    expect(nextSource).not.toContain("@arcgis/core/views/SceneView");
    expect(nextSource).not.toContain("@arcgis/core/WebMap");
    expect(nextSource).toContain("const map = new MapCompat({ basemap: 'streets'");
    expect(nextSource).toContain("ground: 'world-elevation'");
    expect(nextSource).toContain("tables: [tableLayer]");
    expect(nextSource).toContain("spatialReference: { wkid: 3857 }");
    expect(nextSource).toContain("const view = new MapViewCompat({ map, zoom: 4, scale: 5000000, rotation: 10");
    expect(nextSource).toContain("extent: initialExtent");
    expect(nextSource).toContain("constraints: { minZoom: 2 }");
    expect(nextSource).toContain("padding: { left: 8 }");
    expect(nextSource).toContain("highlightOptions: { color: '#ff0' }");
    expect(nextSource).toContain("spatialReference: { wkid: 4326 }");
    expect(nextSource).toContain(
      "const scene = new SceneViewCompat({ map, viewingMode: 'global', qualityProfile: 'high', scale: 4000000, rotation: 15",
    );
    expect(nextSource).toContain("spatialReference: { wkid: 3857 }");
    expect(nextSource).toContain("popup: { dockEnabled: true }");
    expect(nextSource).toContain("const webMap = new WebMapCompat({ portalItem: { id: 'abc123' }");
    expect(nextSource).toContain("basemap: 'satellite'");
    expect(nextSource).toContain("layers: [layerA]");
    expect(nextSource).toContain("tables: [tableLayer]");
    expect(nextSource).toContain("ground: 'world-elevation'");
  });

  it("keeps complex constructor and reports manual TODO", () => {
    const root = makeTempProject();
    const file = path.join(root, "map.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, portalItem: { id: 'abc123' } });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: false,
    });

    expect(result.filesChanged).toBe(0);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos).toHaveLength(1);
    expect(result.manualTodos[0].kind).toBe("feature-layer");
    expect(result.manualTodos[0].reason).toContain("unsupported properties");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("new FeatureLayer({ url: serviceUrl, portalItem: { id: 'abc123' } })");
  });

  it("keeps ArcGIS import when mixed auto and manual call sites exist", () => {
    const root = makeTempProject();
    const file = path.join(root, "mixed.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const a = new FeatureLayer({ url: serviceUrl });",
        "const b = new FeatureLayer({ url: serviceUrl, portalItem: { id: 'abc123' } });",
        "void a; void b;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 2,
      autoMigrated: 1,
      manual: 1,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("@arcgis/core/layers/FeatureLayer");
    expect(nextSource).toContain('import { FeatureLayerCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("const a = new FeatureLayerCompat({ url: serviceUrl });");
    expect(nextSource).toContain(
      "const b = new FeatureLayer({ url: serviceUrl, portalItem: { id: 'abc123' } });",
    );
  });

  it("can annotate manual todos inline without duplicating markers on rerun", () => {
    const root = makeTempProject();
    const file = path.join(root, "annotated.ts");
    fs.writeFileSync(
      file,
      [
        "import FeatureLayer from '@arcgis/core/layers/FeatureLayer';",
        "const layer = new FeatureLayer({ url: serviceUrl, portalItem: { id: 'abc123' } });",
        "void layer;",
      ].join("\n"),
      "utf8",
    );

    runEsriCompatCodemod({
      rootDir: root,
      write: true,
      annotateTodos: true,
    });
    runEsriCompatCodemod({
      rootDir: root,
      write: true,
      annotateTodos: true,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    const marker = "// TODO(honua-migrate)[feature-layer]:";
    expect(nextSource.includes(marker)).toBe(true);
    expect(nextSource.split(marker)).toHaveLength(2);
  });

  it("rewrites supported dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadScene() {",
        "  const module = await import('@arcgis/core/views/SceneView');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["scene-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.SceneViewCompat }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/views/SceneView");
  });

  it("rewrites esriConfig static imports to compat helpers", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-config.ts");
    fs.writeFileSync(
      file,
      [
        "import esriConfig from '@arcgis/core/config';",
        "esriConfig.apiKey = token;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["esri-config"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { esriConfig } from "@honua/sdk-esri-compat";');
    expect(nextSource).not.toContain("@arcgis/core/config");
  });

  it("rewrites esriConfig dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-config-dynamic.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadConfig() {",
        "  const module = await import('@arcgis/core/config.js');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["esri-config"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.esriConfig }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/config");
  });

  it("rewrites esriRequest static imports to compat helpers", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-request.ts");
    fs.writeFileSync(
      file,
      [
        "import request from '@arcgis/core/request';",
        "void request('https://example.test/rest/services/demo', { responseType: 'json' });",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["esri-request"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { esriRequest as request } from "@honua/sdk-esri-compat";');
    expect(nextSource).not.toContain("@arcgis/core/request");
  });

  it("rewrites esriRequest dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "esri-request-dynamic.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadRequest() {",
        "  const module = await import('@arcgis/core/request.js');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["esri-request"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.esriRequest }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/request");
  });

  it("rewrites safe OAuthInfo constructor and removes ArcGIS import", () => {
    const root = makeTempProject();
    const file = path.join(root, "oauth-info.ts");
    fs.writeFileSync(
      file,
      [
        "import OAuthInfo from '@arcgis/core/identity/OAuthInfo';",
        "const info = new OAuthInfo({",
        "  appId: 'client-id',",
        "  portalUrl: 'https://portal.example.test',",
        "  popup: true,",
        "});",
        "void info;",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["oauth-info"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import { OAuthInfoCompat } from "@honua/sdk-esri-compat";');
    expect(nextSource).toContain("new OAuthInfoCompat({");
    expect(nextSource).not.toContain("@arcgis/core/identity/OAuthInfo");
  });

  it("rewrites IdentityManager static imports to compat helpers", () => {
    const root = makeTempProject();
    const file = path.join(root, "identity-manager.ts");
    fs.writeFileSync(
      file,
      [
        "import IdentityManager from '@arcgis/core/identity/IdentityManager';",
        "IdentityManager.destroyCredentials();",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["identity-manager"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { identityManager as IdentityManager } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).not.toContain("@arcgis/core/identity/IdentityManager");
  });

  it("rewrites IdentityManager dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "identity-manager-dynamic.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadIdentityManager() {",
        "  const module = await import('@arcgis/core/identity/IdentityManager.js');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["identity-manager"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.identityManager }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/identity/IdentityManager");
  });

  it("rewrites reactiveUtils static imports to compat helpers", () => {
    const root = makeTempProject();
    const file = path.join(root, "reactive-utils.ts");
    fs.writeFileSync(
      file,
      [
        "import * as reactiveUtils from '@arcgis/core/core/reactiveUtils';",
        "let ready = false;",
        "reactiveUtils.whenOnce(() => ready);",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["reactive-utils"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'import { reactiveUtils } from "@honua/sdk-esri-compat";',
    );
    expect(nextSource).not.toContain("@arcgis/core/core/reactiveUtils");
  });

  it("rewrites reactiveUtils dynamic imports to compat dynamic bridge", () => {
    const root = makeTempProject();
    const file = path.join(root, "reactive-utils-dynamic.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadReactive() {",
        "  const module = await import('@arcgis/core/core/reactiveUtils.js');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["reactive-utils"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.reactiveUtils }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/core/reactiveUtils");
  });

  it("rewrites map and map-view dynamic imports including .js module paths", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-map.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadMapPieces() {",
        "  const mapModule = await import('@arcgis/core/Map.js');",
        "  const mapViewModule = await import('@arcgis/core/views/MapView');",
        "  return [mapModule.default, mapViewModule.default];",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      write: true,
      compatImportPath: "@honua/sdk-esri-compat",
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(2);
    expect(result.metrics.autoMigratedCallSites).toBe(2);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind.map).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });
    expect(result.metrics.byKind["map-view"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapCompat }))',
    );
    expect(nextSource).toContain(
      'await import("@honua/sdk-esri-compat").then((m) => ({ default: m.MapViewCompat }))',
    );
    expect(nextSource).not.toContain("@arcgis/core/Map.js");
    expect(nextSource).not.toContain("@arcgis/core/views/MapView");
  });

  it("rewrites supported dynamic imports for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadLayerFactory() {",
        "  const module = await import('@arcgis/core/layers/FeatureLayer');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
    });

    expect(result.filesChanged).toBe(1);
    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(1);
    expect(result.metrics.manualCallSites).toBe(0);
    expect(result.metrics.byKind["feature-layer"]).toEqual({
      total: 1,
      autoMigrated: 1,
      manual: 0,
    });

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain('import * as HonuaEsriLeaflet from "esri-leaflet";');
    expect(nextSource).toContain(
      "await Promise.resolve({ default: HonuaEsriLeaflet.featureLayer })",
    );
    expect(nextSource).not.toContain("@arcgis/core/layers/FeatureLayer");
  });

  it("keeps unsupported dynamic imports as manual TODOs for esri-leaflet target", () => {
    const root = makeTempProject();
    const file = path.join(root, "lazy-unsupported-esri-leaflet.ts");
    fs.writeFileSync(
      file,
      [
        "export async function loadMapCtor() {",
        "  const module = await import('@arcgis/core/Map');",
        "  return module.default;",
        "}",
      ].join("\n"),
      "utf8",
    );

    const result = runEsriCompatCodemod({
      rootDir: root,
      target: "esri-leaflet",
      write: true,
      annotateTodos: true,
    });

    expect(result.metrics.totalCodemodScopedCallSites).toBe(1);
    expect(result.metrics.autoMigratedCallSites).toBe(0);
    expect(result.metrics.manualCallSites).toBe(1);
    expect(result.manualTodos[0]).toMatchObject({
      kind: "map",
    });
    expect(result.manualTodos[0]?.reason).toContain("Dynamic import");

    const nextSource = fs.readFileSync(file, "utf8");
    expect(nextSource).toContain("@arcgis/core/Map");
    expect(nextSource).toContain("// TODO(honua-migrate)[map]:");
  });
});
