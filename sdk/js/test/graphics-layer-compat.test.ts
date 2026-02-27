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
  });
});
