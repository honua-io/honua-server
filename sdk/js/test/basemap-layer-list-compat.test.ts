import { describe, expect, it } from "vitest";

import { BasemapLayerListCompat, CompatEventBus } from "../src/index.js";

describe("BasemapLayerListCompat", () => {
  it("emits refresh events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const list = new BasemapLayerListCompat({ eventBus });
    list.refresh();

    expect(seenTypes).toContain("basemap-layer-list.refreshed");
  });
});
