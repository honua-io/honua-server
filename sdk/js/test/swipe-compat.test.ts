import { describe, expect, it } from "vitest";

import { CompatEventBus, SwipeCompat } from "../src/index.js";

describe("SwipeCompat", () => {
  it("supports when() and watch() lifecycle and position updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });
    const swipe = new SwipeCompat({ eventBus, position: 40 });
    const loadStatusValues: unknown[] = [];
    const positionValues: unknown[] = [];
    const loadStatusHandle = swipe.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const positionHandle = swipe.watch("position", (value) => {
      positionValues.push(value);
    });

    let callbackWidget: SwipeCompat | undefined;
    const widget = await swipe.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    swipe.setPosition(60);

    loadStatusHandle.remove();
    positionHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      position: positionValues.length,
    };
    swipe.setPosition(80);

    expect(widget).toBe(swipe);
    expect(callbackWidget).toBe(swipe);
    expect(swipe.loaded).toBe(true);
    expect(swipe.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(positionValues).toEqual([60]);
    expect(seenTypes).toContain("swipe.loading");
    expect(seenTypes).toContain("swipe.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(positionValues).toHaveLength(watchSnapshot.position);
  });

  it("normalizes position and manages leading/trailing layers", () => {
    const swipe = new SwipeCompat({
      leadingLayers: [{ id: "leading-1" }],
      trailingLayers: [{ id: "trailing-1" }],
      position: 125,
    });

    expect(swipe.position).toBe(100);
    expect(swipe.leadingLayers).toHaveLength(1);
    expect(swipe.trailingLayers).toHaveLength(1);

    swipe.setPosition(-10);
    swipe.setLeadingLayers([{ id: "leading-2" }, { id: "leading-3" }]);
    swipe.setTrailingLayers([]);

    expect(swipe.position).toBe(0);
    expect(swipe.leadingLayers).toHaveLength(2);
    expect(swipe.trailingLayers).toHaveLength(0);
  });

  it("emits events for position and layer updates", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const swipe = new SwipeCompat({ eventBus });
    swipe.setPosition(42);
    swipe.setLeadingLayers([{}]);
    swipe.setTrailingLayers([{}, {}]);

    expect(seenTypes).toContain("swipe.position-changed");
    expect(seenTypes).toContain("swipe.layers-changed");
  });
});
