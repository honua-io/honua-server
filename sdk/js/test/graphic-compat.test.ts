import { describe, expect, it } from "vitest";

import { GraphicCompat } from "../src/index.js";

describe("GraphicCompat", () => {
  it("supports when() and watch() for lifecycle state", async () => {
    const graphic = new GraphicCompat();
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = graphic.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = graphic.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackGraphic: GraphicCompat | undefined;
    const resolved = await graphic.when((readyGraphic) => {
      callbackGraphic = readyGraphic;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await graphic.load();

    expect(resolved).toBe(graphic);
    expect(callbackGraphic).toBe(graphic);
    expect(graphic.loaded).toBe(true);
    expect(graphic.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("stores construction options and supports updates", () => {
    const graphic = new GraphicCompat({
      geometry: { x: -157.8, y: 21.3 },
      symbol: { type: "simple-marker" },
      attributes: { OBJECTID: 7, status: "active" },
      popupTemplate: { title: "{status}" },
    });
    const geometries: unknown[] = [];
    const symbols: unknown[] = [];
    const attributes: unknown[] = [];
    const popupTemplates: unknown[] = [];
    const layers: unknown[] = [];
    const geometryHandle = graphic.watch("geometry", (value) => {
      geometries.push(value);
    });
    const symbolHandle = graphic.watch("symbol", (value) => {
      symbols.push(value);
    });
    const attributesHandle = graphic.watch("attributes", (value) => {
      attributes.push(value);
    });
    const popupTemplateHandle = graphic.watch("popupTemplate", (value) => {
      popupTemplates.push(value);
    });
    const layerHandle = graphic.watch("layer", (value) => {
      layers.push(value);
    });

    graphic.setGeometry({ x: -157.7, y: 21.4 });
    graphic.setSymbol({ type: "picture-marker" });
    graphic.setAttributes({ OBJECTID: 8, status: "inactive" });
    graphic.setPopupTemplate({ title: "{OBJECTID}" });
    graphic.setLayer({ id: "layer-a" });
    geometryHandle.remove();
    symbolHandle.remove();
    attributesHandle.remove();
    popupTemplateHandle.remove();
    layerHandle.remove();
    const watchSnapshot = {
      geometries: geometries.length,
      symbols: symbols.length,
      attributes: attributes.length,
      popupTemplates: popupTemplates.length,
      layers: layers.length,
    };

    graphic.setGeometry({ x: -157.6, y: 21.5 });
    graphic.setLayer({ id: "layer-b" });

    expect(graphic.geometry).toEqual({ x: -157.6, y: 21.5 });
    expect(graphic.symbol).toEqual({ type: "picture-marker" });
    expect(graphic.attributes).toEqual({ OBJECTID: 8, status: "inactive" });
    expect(graphic.popupTemplate).toEqual({ title: "{OBJECTID}" });
    expect(graphic.layer).toEqual({ id: "layer-b" });
    expect(geometries).toEqual([{ x: -157.7, y: 21.4 }]);
    expect(symbols).toEqual([{ type: "picture-marker" }]);
    expect(attributes).toEqual([{ OBJECTID: 8, status: "inactive" }]);
    expect(popupTemplates).toEqual([{ title: "{OBJECTID}" }]);
    expect(layers).toEqual([{ id: "layer-a" }]);
    expect(geometries).toHaveLength(watchSnapshot.geometries);
    expect(symbols).toHaveLength(watchSnapshot.symbols);
    expect(attributes).toHaveLength(watchSnapshot.attributes);
    expect(popupTemplates).toHaveLength(watchSnapshot.popupTemplates);
    expect(layers).toHaveLength(watchSnapshot.layers);
  });

  it("clones state and serializes to JSON", () => {
    const graphic = new GraphicCompat({
      geometry: { type: "point", x: 10, y: 5 },
      symbol: { type: "simple-marker", color: "blue" },
      attributes: { OBJECTID: 99 },
      popupTemplate: { title: "Parcel 99" },
      layer: { id: "parcels" },
    });

    const clone = graphic.clone();
    expect(clone).not.toBe(graphic);
    expect(clone.toJSON()).toEqual(graphic.toJSON());

    clone.setAttributes({ OBJECTID: 100 });
    expect(graphic.attributes).toEqual({ OBJECTID: 99 });
  });
});
