import { describe, expect, it } from "vitest";

import { CompatEventBus, GraphicsLayerCompat, SketchCompat } from "../src/index.js";

describe("SketchCompat", () => {
  it("tracks create/update/delete flows and emits events on the shared event bus", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const layer = new GraphicsLayerCompat({ graphics: [] });
    const sketch = new SketchCompat({
      layer,
      eventBus,
      creationMode: "update",
      defaultCreateOptions: { mode: "click" },
      defaultUpdateOptions: { tool: "transform" },
    });

    sketch.create("polygon");
    const graphic = { id: "g1" };
    const completed = sketch.complete(graphic);
    expect(completed).toMatchObject({ state: "complete", tool: "polygon", graphic });
    expect(layer.graphics).toContain(graphic);

    const activeUpdate = sketch.update(graphic, { enableScaling: true });
    expect(activeUpdate).toEqual([graphic]);
    expect(sketch.delete()).toBe(1);
    expect(layer.graphics).toHaveLength(0);

    sketch.reset();
    expect(seenTypes).toContain("sketch.create-started");
    expect(seenTypes).toContain("sketch.create-completed");
    expect(seenTypes).toContain("sketch.update-started");
    expect(seenTypes).toContain("sketch.graphics-deleted");
    expect(seenTypes).toContain("sketch.reset");
  });

  it("returns cancel result when create flow is aborted", () => {
    const sketch = new SketchCompat();
    sketch.create("circle");

    const cancelled = sketch.cancel();
    expect(cancelled).toMatchObject({ state: "cancel", tool: "circle" });
    expect(sketch.state).toBe("ready");
    expect(sketch.activeTool).toBeUndefined();
  });
});
