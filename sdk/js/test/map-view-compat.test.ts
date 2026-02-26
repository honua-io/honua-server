import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, MapViewCompat } from "../src/index.js";

describe("MapCompat", () => {
  it("tracks layers through add and remove", () => {
    const layerA = { id: "a" };
    const layerB = { id: "b" };
    const map = new MapCompat({ layers: [layerA] });

    map.add(layerB);
    expect(map.layers).toHaveLength(2);
    expect(map.layers[0]).toBe(layerA);
    expect(map.layers[1]).toBe(layerB);

    expect(map.remove(layerA)).toBe(true);
    expect(map.layers).toEqual([layerB]);
    expect(map.remove(layerA)).toBe(false);
  });

  it("supports indexed layer operations and id-based lookup", () => {
    const layerA = { id: "a" };
    const layerB = { id: "b" };
    const layerC = { id: "c" };
    const layerD = { id: "d" };
    const map = new MapCompat({ layers: [layerA, layerC] });

    map.add(layerB, 1);
    expect(map.layers).toEqual([layerA, layerB, layerC]);

    map.addMany([layerD], 0);
    expect(map.allLayers).toEqual([layerD, layerA, layerB, layerC]);
    expect(map.findLayerById("b")).toBe(layerB);
    expect(map.findLayerById("missing")).toBeUndefined();

    expect(map.reorder(layerC, 1)).toBe(true);
    expect(map.layers).toEqual([layerD, layerC, layerA, layerB]);
    expect(map.reorder({ id: "not-present" }, 0)).toBe(false);

    expect(map.removeMany([layerD, layerB])).toBe(2);
    expect(map.layers).toEqual([layerC, layerA]);

    map.removeAll();
    expect(map.layers).toEqual([]);
  });
});

describe("MapViewCompat", () => {
  it("supports when() and goTo() state updates", async () => {
    const map = new MapCompat();
    const view = new MapViewCompat({
      map,
      container: "viewDiv",
      zoom: 3,
      center: [-157.8, 21.3],
    });

    let callbackView: MapViewCompat | undefined;
    const ready = await view.when((resolvedView) => {
      callbackView = resolvedView;
    });
    expect(ready).toBe(view);
    expect(callbackView).toBe(view);
    expect(view.map).toBe(map);
    expect(view.zoom).toBe(3);

    await view.goTo({ zoom: 8, center: [-155, 19.5] });
    expect(view.zoom).toBe(8);
    expect(view.center).toEqual([-155, 19.5]);

    view.destroy();
    expect(view.map).toBeUndefined();
  });

  it("supports watch and on handles", async () => {
    const view = new MapViewCompat({
      zoom: 2,
      center: [0, 0],
    });

    const zoomValues: unknown[] = [];
    const centerValues: unknown[] = [];
    const events: unknown[] = [];

    const zoomHandle = view.watch("zoom", (value) => {
      zoomValues.push(value);
    });
    const centerHandle = view.watch("center", (value) => {
      centerValues.push(value);
    });
    const eventHandle = view.on("go-to", (event) => {
      events.push(event);
    });

    await view.goTo({ zoom: 4, center: [10, 20] });
    expect(zoomValues).toEqual([4]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20] }]);

    zoomHandle.remove();
    centerHandle.remove();
    eventHandle.remove();

    await view.goTo({ zoom: 6, center: [30, 40] });
    expect(zoomValues).toEqual([4]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20] }]);
  });

  it("supports popup bridge helpers and popup watchers", () => {
    const view = new MapViewCompat({
      popup: {
        dockEnabled: true,
        dockOptions: { breakpoint: false },
      },
    });
    const popupVisibility: unknown[] = [];
    const popupActive: unknown[] = [];
    const popupEvents: unknown[] = [];

    view.watch("popup.visible", (value) => {
      popupVisibility.push(value);
    });
    view.watch("popup.viewModel.active", (value) => {
      popupActive.push(value);
    });
    view.on("popup-open", (event) => {
      popupEvents.push({ type: "open", event });
    });
    view.on("popup-close", (event) => {
      popupEvents.push({ type: "close", event });
    });

    view.openPopup({
      title: "Layer Info",
      content: "Details",
      location: [1, 2],
      features: [{ id: 123 }],
    });
    expect(view.popup.visible).toBe(true);
    expect(view.popup.title).toBe("Layer Info");
    expect(view.popup.content).toBe("Details");
    expect(view.popup.location).toEqual([1, 2]);
    expect(view.popup.features).toEqual([{ id: 123 }]);
    expect(view.popup.selectedFeature).toEqual({ id: 123 });
    expect(view.popup.viewModel.active).toBe(true);
    expect(view.popup.dockEnabled).toBe(true);
    expect(view.popup.dockOptions).toEqual({ breakpoint: false });

    view.closePopup();
    expect(view.popup.visible).toBe(false);
    expect(view.popup.features).toEqual([]);
    expect(view.popup.selectedFeature).toBeUndefined();
    expect(view.popup.viewModel.active).toBe(false);
    expect(view.popup.title).toBeUndefined();
    expect(view.popup.content).toBeUndefined();

    expect(popupVisibility).toEqual([true, false]);
    expect(popupActive).toEqual([true, false]);
    expect(popupEvents).toHaveLength(2);
    expect(popupEvents[0]).toEqual({
      type: "open",
      event: {
        title: "Layer Info",
        content: "Details",
        location: [1, 2],
        features: [{ id: 123 }],
      },
    });
    expect(popupEvents[1]).toEqual({ type: "close", event: undefined });
  });

  it("supports ui component add/remove/move/find helpers", () => {
    const view = new MapViewCompat();
    const componentCountSnapshots: number[] = [];
    view.watch("ui.components", (value) => {
      componentCountSnapshots.push(Array.isArray(value) ? value.length : -1);
    });

    const layerList = { id: "layer-list" };
    const legend = { id: "legend" };
    const popup = { id: "popup" };

    view.ui.add(layerList, "top-right");
    view.ui.add([legend, popup], { position: "top-left" });

    expect(view.ui.getComponents()).toEqual([layerList, legend, popup]);
    expect(view.ui.getComponents("top-right")).toEqual([layerList]);
    expect(view.ui.getComponents("top-left")).toEqual([legend, popup]);
    expect(view.ui.find(layerList)).toBe(layerList);
    expect(view.ui.find("legend")).toBe(legend);
    expect(view.ui.find("missing")).toBeUndefined();

    expect(view.ui.move("layer-list", { position: "bottom-left", index: 0 })).toBe(true);
    expect(view.ui.getComponents("bottom-left")).toEqual([layerList]);

    expect(view.ui.remove("popup")).toBe(true);
    expect(view.ui.remove("popup")).toBe(false);
    expect(view.ui.getComponents("top-left")).toEqual([legend]);

    view.ui.empty("top-left");
    expect(view.ui.getComponents("top-left")).toEqual([]);

    view.ui.removeAll();
    expect(view.ui.getComponents()).toEqual([]);
    expect(componentCountSnapshots).toEqual([1, 2, 3, 3, 2, 1, 0]);
  });

  it("supports whenLayerView bridge and layer view query/watch helpers", async () => {
    const layer = {
      id: "layer-1",
      queryFeatures(options: unknown) {
        return Promise.resolve({
          features: [{ id: "f-1", options, attributes: { OBJECTID: 11 } }],
        });
      },
      queryFeatureCount() {
        return Promise.resolve(7);
      },
      queryObjectIds() {
        return Promise.resolve([11, 12, 13]);
      },
    };
    const view = new MapViewCompat({ map: new MapCompat({ layers: [layer] }) });

    const createdEvents: unknown[] = [];
    view.on("layerview-create", (event) => {
      createdEvents.push(event);
    });

    const layerViewA = await view.whenLayerView(layer);
    const layerViewB = await view.whenLayerView(layer);

    expect(layerViewA).toBe(layerViewB);
    expect(createdEvents).toHaveLength(1);
    expect(createdEvents[0]).toMatchObject({ layer });
    expect(layerViewA.layer).toBe(layer);

    const updatingValues: unknown[] = [];
    const updatingHandle = layerViewA.watch("updating", (value) => {
      updatingValues.push(value);
    });
    layerViewA.setUpdating(true);
    layerViewA.setUpdating(false);
    expect(updatingValues).toEqual([true, false]);

    updatingHandle.remove();
    layerViewA.setUpdating(true);
    expect(updatingValues).toEqual([true, false]);

    const hasAllFeaturesValues: unknown[] = [];
    const hasAllFeaturesInViewValues: unknown[] = [];
    layerViewA.watch("hasAllFeatures", (value) => {
      hasAllFeaturesValues.push(value);
    });
    layerViewA.watch("hasAllFeaturesInView", (value) => {
      hasAllFeaturesInViewValues.push(value);
    });
    layerViewA.setHasAllFeatures(false);
    layerViewA.setHasAllFeaturesInView(false);
    expect(hasAllFeaturesValues).toEqual([false]);
    expect(hasAllFeaturesInViewValues).toEqual([false]);

    const queryResult = await layerViewA.queryFeatures({ where: "1=1" });
    expect(queryResult).toEqual({
      features: [{ id: "f-1", options: { where: "1=1" }, attributes: { OBJECTID: 11 } }],
    });
    expect(await layerViewA.queryFeatureCount()).toBe(7);
    expect(await layerViewA.queryObjectIds()).toEqual([11, 12, 13]);

    const fallbackLayerView = await view.whenLayerView({
      id: "layer-no-query",
      queryFeatures() {
        return Promise.resolve({
          features: [{ attributes: { OBJECTID: 44 } }, { attributes: { objectId: 45 } }],
        });
      },
    });
    expect(await fallbackLayerView.queryFeatures()).toEqual({
      features: [{ attributes: { OBJECTID: 44 } }, { attributes: { objectId: 45 } }],
    });
    expect(await fallbackLayerView.queryFeatureCount()).toBe(2);
    expect(await fallbackLayerView.queryObjectIds()).toEqual([44, 45]);
  });

  it("supports toMap/toScreen and hitTest popup result bridge", async () => {
    const featureA = { id: "a", layer: { id: "layer-a" } };
    const featureB = { id: "b" };
    const view = new MapViewCompat();

    const mapPoint = view.toMap({ x: 100, y: 200 });
    expect(mapPoint).toEqual({ x: 100, y: 200 });
    expect(view.toScreen(mapPoint)).toEqual({ x: 100, y: 200 });

    view.openPopup({
      location: mapPoint,
      features: [featureA, featureB],
    });

    const hit = await view.hitTest({ x: 100, y: 200 });
    expect(hit.results).toEqual([
      {
        type: "graphic",
        graphic: featureA,
        layer: { id: "layer-a" },
        mapPoint: { x: 100, y: 200 },
      },
      {
        type: "graphic",
        graphic: featureB,
        layer: undefined,
        mapPoint: { x: 100, y: 200 },
      },
    ]);
  });

  it("publishes popup/goTo/layer-view/destroy events to the shared event bus", async () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const layer = { id: "layer-1" };
    const view = new MapViewCompat({
      map: new MapCompat({ layers: [layer], eventBus }),
      eventBus,
    });

    await view.goTo({ center: [1, 2], zoom: 4 });
    view.openPopup({ title: "Popup" });
    view.ui.add({ id: "layer-list" }, "top-right");
    view.ui.remove("layer-list");
    view.ui.add({ id: "legend" }, "top-right");
    view.ui.move("legend", "bottom-left");
    const layerView = await view.whenLayerView(layer);
    layerView.setUpdating(true);
    layerView.setSuspended(true);
    layerView.setHasAllFeatures(false);
    layerView.setHasAllFeaturesInView(false);
    view.closePopup();
    view.destroy();

    expect(events).toContain("view.go-to");
    expect(events).toContain("popup.open");
    expect(events).toContain("popup.close");
    expect(events).toContain("view.layer-view-created");
    expect(events).toContain("view.layer-view-updating-changed");
    expect(events).toContain("view.layer-view-suspended-changed");
    expect(events).toContain("view.layer-view-has-all-features-changed");
    expect(events).toContain("view.layer-view-has-all-features-in-view-changed");
    expect(events).toContain("view.ui.component-added");
    expect(events).toContain("view.ui.component-removed");
    expect(events).toContain("view.ui.component-moved");
    expect(events).toContain("view.ui.components-cleared");
    expect(events).toContain("view.destroy");
  });
});
