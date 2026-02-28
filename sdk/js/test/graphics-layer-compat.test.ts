import { describe, expect, it } from "vitest";

import { CompatEventBus, GraphicsLayerCompat } from "../src/index.js";

describe("GraphicsLayerCompat", () => {
  it("supports lifecycle loading and graphic collection helpers", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const graphicA = { id: "g-a" };
    const graphicB = { id: "g-b" };
    const layer = new GraphicsLayerCompat({
      id: "graphics-layer",
      graphics: [graphicA],
      eventBus,
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const graphicCounts: number[] = [];
    const loadStatusHandle = layer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = layer.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const graphicsHandle = layer.watch("graphics", (value) => {
      graphicCounts.push(Array.isArray(value) ? value.length : -1);
    });

    expect(layer.loaded).toBe(false);
    expect(layer.loadStatus).toBe("not-loaded");

    let callbackLayer: GraphicsLayerCompat | undefined;
    const readyLayer = await layer.when((resolvedLayer) => {
      callbackLayer = resolvedLayer;
    });
    expect(readyLayer).toBe(layer);
    expect(callbackLayer).toBe(layer);
    expect(layer.loaded).toBe(true);
    expect(layer.loadStatus).toBe("loaded");
    expect(eventTypes).toContain("graphics-layer.loading");
    expect(eventTypes).toContain("graphics-layer.loaded");

    layer.add(graphicB);
    expect(layer.graphics).toEqual([graphicA, graphicB]);
    expect(await layer.queryFeatureCount()).toBe(2);

    expect(layer.remove(graphicA)).toBe(graphicA);
    expect(layer.graphics).toEqual([graphicB]);
    expect(await layer.queryFeatures()).toEqual({ features: [graphicB] });

    loadStatusHandle.remove();
    loadedHandle.remove();
    graphicsHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      graphics: graphicCounts.length,
    };
    layer.add({ id: "g-c" });

    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(graphicCounts).toEqual([2, 1]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(graphicCounts).toHaveLength(watchSnapshot.graphics);

    layer.setOpacity(Number.NaN);
    expect(layer.opacity).toBe(1);
    layer.setOpacity(-3);
    expect(layer.opacity).toBe(0);
    layer.setOpacity(3);
    expect(layer.opacity).toBe(1);
  });

  it(".on() fires for graphic-added and handle.remove() stops delivery", () => {
    const eventBus = new CompatEventBus();
    const layer = new GraphicsLayerCompat({ eventBus });

    const received: unknown[] = [];
    const handle = layer.on("graphic-added", (event) => {
      received.push(event);
    });

    layer.add({ id: 1 });
    expect(received).toHaveLength(1);

    handle.remove();
    layer.add({ id: 2 });
    expect(received).toHaveLength(1);
  });

  it("upgraded destroy() clears watchers, event listeners, and emits graphics-layer.destroyed", () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const layer = new GraphicsLayerCompat({ eventBus });

    const watchValues: unknown[] = [];
    layer.watch("visible", (v) => watchValues.push(v));

    const eventPayloads: unknown[] = [];
    layer.on("graphic-added", (e) => eventPayloads.push(e));

    layer.destroy();

    expect(eventTypes).toContain("graphics-layer.destroyed");

    layer.setVisibility(false);
    expect(watchValues).toHaveLength(0);
  });
});
