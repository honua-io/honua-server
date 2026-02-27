import { describe, expect, it } from "vitest";

import { CompatEventBus, LegendCompat } from "../src/index.js";

describe("LegendCompat", () => {
  it("supports when() and watch() for load and item updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const layer = {
      id: "layer-1",
      title: "Layer 1",
      getLegend: async () => ({
        layers: [
          {
            layerId: 0,
            layerName: "Layer 1",
            legend: [{ label: "Entry A" }],
          },
        ],
      }),
    };

    const legend = new LegendCompat({
      eventBus,
      layers: [layer],
      autoRefresh: false,
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const itemCounts: number[] = [];
    const loadStatusHandle = legend.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = legend.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const itemsHandle = legend.watch("items", (value) => {
      if (Array.isArray(value)) {
        itemCounts.push(value.length);
      }
    });

    let callbackWidget: LegendCompat | undefined;
    const widget = await legend.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });

    await legend.refresh();

    loadStatusHandle.remove();
    loadedHandle.remove();
    itemsHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      itemCounts: itemCounts.length,
    };
    await legend.refresh();

    expect(widget).toBe(legend);
    expect(callbackWidget).toBe(legend);
    expect(legend.loaded).toBe(true);
    expect(legend.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(itemCounts).toEqual([1, 1]);
    expect(seenTypes).toContain("legend.loading");
    expect(seenTypes).toContain("legend.loaded");
    expect(seenTypes).toContain("legend.updated");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(itemCounts).toHaveLength(watchSnapshot.itemCounts);
  });

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

  it("keeps the latest refresh result when concurrent refresh calls race", async () => {
    let callCount = 0;
    let resolveFirst: ((value: unknown) => void) | undefined;
    let resolveSecond: ((value: unknown) => void) | undefined;

    const layer = {
      getLegend: () =>
        new Promise((resolve) => {
          callCount += 1;
          if (callCount === 1) {
            resolveFirst = resolve;
            return;
          }
          resolveSecond = resolve;
        }),
    };

    const legend = new LegendCompat({ layers: [layer], autoRefresh: false });
    const firstRefresh = legend.refresh();
    const secondRefresh = legend.refresh();

    resolveSecond?.({
      layers: [{ layerId: 1, layerName: "Layer", legend: [{ label: "newest" }] }],
    });
    await secondRefresh;

    resolveFirst?.({
      layers: [{ layerId: 1, layerName: "Layer", legend: [{ label: "stale" }] }],
    });
    await firstRefresh;

    expect(legend.items).toHaveLength(1);
    expect(legend.items[0].entries[0].label).toBe("newest");
  });
});
