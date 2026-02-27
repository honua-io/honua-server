import { describe, expect, it } from "vitest";

import { CompatEventBus, GroupLayerCompat } from "../src/index.js";

describe("GroupLayerCompat", () => {
  it("supports lifecycle loading and nested layer operations", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const layerA = { id: "layer-a" };
    const layerB = { id: "layer-b" };
    const nestedGroup = { id: "nested", layers: [layerB] };
    const layer = new GroupLayerCompat({
      id: "group-layer",
      layers: [layerA, nestedGroup],
      eventBus,
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const layerCounts: number[] = [];
    const loadStatusHandle = layer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = layer.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const layersHandle = layer.watch("layers", (value) => {
      layerCounts.push(Array.isArray(value) ? value.length : -1);
    });

    expect(layer.loaded).toBe(false);
    expect(layer.loadStatus).toBe("not-loaded");

    let callbackLayer: GroupLayerCompat | undefined;
    const readyLayer = await layer.when((resolvedLayer) => {
      callbackLayer = resolvedLayer;
    });
    expect(readyLayer).toBe(layer);
    expect(callbackLayer).toBe(layer);
    expect(layer.loaded).toBe(true);
    expect(layer.loadStatus).toBe("loaded");
    expect(eventTypes).toContain("group-layer.loading");
    expect(eventTypes).toContain("group-layer.loaded");

    expect(layer.layers).toEqual([layerA, nestedGroup]);
    expect(layer.allLayers).toEqual([layerA, nestedGroup, layerB]);
    expect(layer.findLayerById("layer-b")).toBe(layerB);

    const layerC = { id: "layer-c" };
    layer.add(layerC, 1);
    expect(layer.layers).toEqual([layerA, layerC, nestedGroup]);
    expect(layer.remove(layerA)).toBe(true);
    expect(layer.layers).toEqual([layerC, nestedGroup]);

    loadStatusHandle.remove();
    loadedHandle.remove();
    layersHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      layers: layerCounts.length,
    };
    layer.add({ id: "layer-d" });

    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(layerCounts).toEqual([3, 2]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(layerCounts).toHaveLength(watchSnapshot.layers);

    layer.setOpacity(Number.NaN);
    expect(layer.opacity).toBe(1);
    layer.setOpacity(-1);
    expect(layer.opacity).toBe(0);
    layer.setOpacity(5);
    expect(layer.opacity).toBe(1);
  });
});
