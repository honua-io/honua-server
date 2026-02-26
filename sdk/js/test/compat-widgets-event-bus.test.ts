import { describe, expect, it } from "vitest";

import {
  CompatEventBus,
  GraphicsLayerCompat,
  GroupLayerCompat,
  LayerListCompat,
  LegendCompat,
  MapCompat,
} from "../src/index.js";

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe("CompatEventBus", () => {
  it("supports typed and catch-all subscriptions", () => {
    const bus = new CompatEventBus();
    const seenTypes: string[] = [];
    const seenPayloads: unknown[] = [];

    const byType = bus.on<{ id: number }>("layer.visibility-changed", (event) => {
      seenTypes.push(event.type);
      seenPayloads.push(event.payload.id);
    });
    bus.onAny((event) => {
      seenTypes.push(`any:${event.type}`);
    });

    bus.emit("layer.visibility-changed", { id: 42 }, { source: "test" });
    byType.remove();
    bus.emit("layer.visibility-changed", { id: 77 });

    expect(seenTypes).toEqual([
      "layer.visibility-changed",
      "any:layer.visibility-changed",
      "any:layer.visibility-changed",
    ]);
    expect(seenPayloads).toEqual([42]);
  });

  it("isolates listener failures", () => {
    const bus = new CompatEventBus();
    let invoked = 0;

    bus.on("test", () => {
      throw new Error("boom");
    });
    bus.on("test", () => {
      invoked += 1;
    });

    bus.emit("test", { ok: true });
    expect(invoked).toBe(1);
  });
});

describe("GraphicsLayerCompat", () => {
  it("tracks graphics, supports queries, and emits lifecycle events", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const g0 = { id: "g0" };
    const g1 = { id: "g1" };
    const g2 = { id: "g2" };

    const layer = new GraphicsLayerCompat({ id: "graphics", eventBus, graphics: [g0] });
    layer.add(g1);
    layer.addMany([g2], 1);

    expect((await layer.queryFeatures()).features).toEqual([g0, g2, g1]);
    expect(await layer.queryFeatureCount()).toBe(3);

    expect(layer.remove(g2)).toBe(g2);
    layer.setVisibility(false);
    layer.setOpacity(0.5);
    layer.removeAll();

    expect(layer.graphics).toEqual([]);
    expect(layer.visible).toBe(false);
    expect(layer.opacity).toBe(0.5);
    expect(eventTypes).toContain("graphics-layer.graphic-added");
    expect(eventTypes).toContain("graphics-layer.graphics-added");
    expect(eventTypes).toContain("graphics-layer.graphic-removed");
    expect(eventTypes).toContain("graphics-layer.graphics-cleared");
    expect(eventTypes).toContain("layer.visibility-changed");
    expect(eventTypes).toContain("layer.opacity-changed");
  });
});

describe("GroupLayerCompat", () => {
  it("supports nested layer lookup and mutation helpers", () => {
    const eventBus = new CompatEventBus();
    const parent = new GroupLayerCompat({
      id: "group-root",
      eventBus,
      layers: [{ id: "a" }, new GroupLayerCompat({ id: "child-group", layers: [{ id: "nested" }] })],
    });

    expect(parent.findLayerById("nested")).toMatchObject({ id: "nested" });
    expect(parent.findLayerById("missing")).toBeUndefined();

    const layerB = { id: "b" };
    parent.add(layerB, 1);
    expect(parent.layers[1]).toBe(layerB);

    expect(parent.remove(layerB)).toBe(true);
    expect(parent.remove(layerB)).toBe(false);

    parent.setVisibility(false);
    parent.setOpacity(0.7);
    parent.removeAll();

    expect(parent.layers).toEqual([]);
    expect(parent.visible).toBe(false);
    expect(parent.opacity).toBe(0.7);
  });
});

describe("LayerListCompat", () => {
  it("builds a TOC model, toggles visibility, and auto-refreshes on map events", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });
    const map = new MapCompat({ eventBus });

    const layerA = { id: "a", title: "A", visible: true };
    const layerB = { id: "b", title: "B", visible: true };
    const nested = new GroupLayerCompat({ id: "group", eventBus, layers: [{ id: "nested", title: "Nested", visible: true }] });

    map.addMany([layerA, nested]);

    const layerList = new LayerListCompat({
      map,
      eventBus,
      listItemCreatedFunction: ({ item }) => {
        if (item.id === "a") {
          item.actionsSections = [[{ id: "zoom-to", title: "Zoom To" }]];
        }
        if (item.id === "nested") {
          item.actionsSections = [[{ id: "open-details", title: "Open Details" }]];
        }
      },
    });
    const triggered: unknown[] = [];
    layerList.on("trigger-action", (event) => {
      triggered.push(event);
    });
    await layerList.load();

    expect(layerList.items).toHaveLength(2);
    expect(layerList.items[1]?.children[0]).toMatchObject({ id: "nested", title: "Nested" });
    expect(layerList.triggerAction("zoom-to", "a")).toBe(true);
    expect(layerList.triggerAction("open-details", "nested")).toBe(true);

    expect(layerList.toggle("a", false)).toBe(true);
    expect(layerA.visible).toBe(false);

    map.add(layerB);
    expect(layerList.items.some((item) => item.id === "b")).toBe(true);
    expect(layerList.setItemActions("b", [[{ id: "open-metadata", title: "Open Metadata" }]])).toBe(true);
    expect(layerList.triggerAction("open-metadata", "b")).toBe(true);
    expect(layerList.triggerAction("missing-action", "b")).toBe(false);

    expect(triggered).toHaveLength(3);
    expect(triggered[0]).toMatchObject({ actionId: "zoom-to", layer: layerA });
    expect(triggered[1]).toMatchObject({
      actionId: "open-details",
      item: { id: "nested" },
    });
    expect(eventTypes).toContain("layer-list.trigger-action");

    layerList.destroy();
  });
});

describe("LegendCompat", () => {
  it("resolves legend entries and auto-refreshes on visibility changes", async () => {
    const eventBus = new CompatEventBus();
    const layerA = {
      id: "a",
      title: "Layer A",
      visible: true,
      getLegend: () =>
        Promise.resolve({
          layers: [
            {
              layerId: 0,
              layerName: "Layer A",
              legend: [
                {
                  label: "Road",
                  imageData: "abc",
                  contentType: "image/png",
                  width: 20,
                  height: 20,
                },
              ],
            },
          ],
        }),
    };
    const layerB = {
      id: "b",
      title: "Layer B",
      visible: false,
      legend: () =>
        Promise.resolve({
          layers: [
            {
              layerId: 1,
              layerName: "Layer B",
              legend: [{ label: "Parcel", imageData: "def", contentType: "image/png", width: 20, height: 20 }],
            },
          ],
        }),
    };

    const map = new MapCompat({ layers: [layerA, layerB], eventBus });
    const legend = new LegendCompat({ map, eventBus });

    await legend.load();
    expect(legend.items).toHaveLength(1);
    expect(legend.items[0]).toMatchObject({ title: "Layer A" });
    expect(legend.items[0]?.entries[0]).toMatchObject({ label: "Road", layerName: "Layer A" });

    layerB.visible = true;
    eventBus.emit("layer.visibility-changed", { layerId: "b", visible: true }, map);
    for (let i = 0; i < 10 && legend.items.length < 2; i += 1) {
      await flushMicrotasks();
    }

    expect(legend.items.map((item) => item.title)).toEqual(["Layer A", "Layer B"]);

    legend.destroy();
  });
});
