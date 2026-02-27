import { describe, expect, it } from "vitest";

import { CompatEventBus, LegendCompat } from "../src/index.js";

describe("LegendCompat", () => {
  it("builds legend entries from visible layers by default", async () => {
    const layerVisible = {
      id: "visible",
      title: "Visible Layer",
      visible: true,
      getLegend: async () => ({
        layers: [
          {
            layerId: 0,
            layerName: "Visible Layer",
            legend: [{ label: "A", imageData: "img-a", contentType: "image/png", width: 20, height: 20 }],
          },
        ],
      }),
    };
    const layerHidden = {
      id: "hidden",
      title: "Hidden Layer",
      visible: false,
      getLegend: async () => ({
        layers: [
          {
            layerId: 1,
            layerName: "Hidden Layer",
            legend: [{ label: "B", imageData: "img-b", contentType: "image/png", width: 20, height: 20 }],
          },
        ],
      }),
    };
    const map = { layers: [layerVisible, layerHidden] };

    const legend = new LegendCompat({ map });
    const items = await legend.refresh();

    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({
      title: "Visible Layer",
      entries: [{ label: "A" }],
    });
  });

  it("supports includeHidden and emits legend.updated events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const layer = {
      id: "layer-1",
      visible: false,
      legend: async () => ({
        layers: [
          {
            layerId: 2,
            layerName: "Layer 1",
            legend: [{ label: "Legend Entry" }],
          },
        ],
      }),
    };

    const legend = new LegendCompat({
      layers: [layer],
      includeHidden: true,
      autoRefresh: false,
      eventBus,
    });

    const items = await legend.refresh();
    expect(legend.includeHidden).toBe(true);
    expect(items).toHaveLength(1);
    expect(items[0]).toMatchObject({
      title: "layer-1",
      entries: [{ label: "Legend Entry" }],
    });
    expect(seenTypes).toContain("legend.updated");
  });
});
