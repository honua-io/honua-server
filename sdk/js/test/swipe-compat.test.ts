import { describe, expect, it } from "vitest";

import { CompatEventBus, SwipeCompat } from "../src/index.js";

describe("SwipeCompat", () => {
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
