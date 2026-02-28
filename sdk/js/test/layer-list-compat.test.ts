import { describe, expect, it } from "vitest";

import { CompatEventBus, LayerListCompat, MapCompat, MapImageLayerCompat } from "../src/index.js";

describe("LayerListCompat", () => {
  it("supports when() and watch() for load and item updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const map = new MapCompat({
      eventBus,
      layers: [{ id: "a", title: "A", visible: true }],
    });
    const layerList = new LayerListCompat({
      map,
      eventBus,
      autoRefresh: false,
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const itemCounts: number[] = [];
    const loadStatusHandle = layerList.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = layerList.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const itemsHandle = layerList.watch("items", (value) => {
      if (Array.isArray(value)) {
        itemCounts.push(value.length);
      }
    });

    let callbackWidget: LayerListCompat | undefined;
    const widget = await layerList.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });

    map.add({ id: "b", title: "B", visible: true });
    layerList.refresh();

    loadStatusHandle.remove();
    loadedHandle.remove();
    itemsHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      itemCounts: itemCounts.length,
    };

    map.add({ id: "c", title: "C", visible: true });
    layerList.refresh();

    expect(widget).toBe(layerList);
    expect(callbackWidget).toBe(layerList);
    expect(layerList.loaded).toBe(true);
    expect(layerList.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(itemCounts).toEqual([1, 2]);
    expect(seenTypes).toContain("layer-list.loading");
    expect(seenTypes).toContain("layer-list.loaded");
    expect(seenTypes).toContain("layer-list.updated");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(itemCounts).toHaveLength(watchSnapshot.itemCounts);
  });

  it("includes map-image sublayers in TOC and supports numeric id actions", async () => {
    const eventBus = new CompatEventBus();
    const mapImage = new MapImageLayerCompat({
      url: "https://example.test/rest/services/default/MapServer",
      sublayers: [{ id: 0, title: "Roads" }, { id: 1 }],
      eventBus,
      client: new (class {
        public getMapServiceMetadata(): Promise<unknown> {
          return Promise.resolve({});
        }
      })() as any,
    });
    const map = new MapCompat({
      eventBus,
      layers: [mapImage],
    });
    const layerList = new LayerListCompat({
      map,
      eventBus,
      autoRefresh: false,
    });

    await layerList.load();

    expect(layerList.items).toHaveLength(1);
    expect(layerList.items[0]?.children.map((child) => child.id)).toEqual([0, 1]);
    expect(layerList.items[0]?.children.map((child) => child.title)).toEqual(["Roads", "1"]);

    const actionEvents: unknown[] = [];
    const visibilityEvents: unknown[] = [];
    eventBus.on("layer.visibility-changed", (event) => {
      visibilityEvents.push(event.payload);
    });
    layerList.on("trigger-action", (event) => {
      actionEvents.push(event);
    });
    expect(
      layerList.setItemActions(1, [[{ id: "zoom-to", title: "Zoom To Layer" }]]),
    ).toBe(true);
    expect(layerList.triggerAction("zoom-to", 1)).toBe(true);
    expect(layerList.toggle(1, false)).toBe(true);
    expect(mapImage.sublayer(1)?.visible).toBe(false);
    expect(actionEvents).toHaveLength(1);
    expect(visibilityEvents).toEqual([{ layerId: 1, visible: false }]);
    expect((actionEvents[0] as { item?: { id?: unknown } }).item?.id).toBe(1);
  });

  it("isolates watcher errors so later watchers still run", () => {
    const map = new MapCompat({
      layers: [{ id: "a", title: "A", visible: true }],
    });
    const layerList = new LayerListCompat({
      map,
      autoRefresh: false,
    });

    let safeWatcherCalls = 0;
    layerList.watch("items", () => {
      throw new Error("watcher-failure");
    });
    layerList.watch("items", () => {
      safeWatcherCalls += 1;
    });

    expect(() => layerList.refresh()).not.toThrow();
    expect(safeWatcherCalls).toBe(1);
  });
});
