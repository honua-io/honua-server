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
  });
});
