import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, MapViewCompat } from "../src/index.js";

describe("MapCompat", () => {
  it("supports map load/when lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const map = new MapCompat({
      eventBus,
      layers: [{ id: "layer-1" }],
    });

    expect(map.loaded).toBe(false);
    expect(map.loadStatus).toBe("not-loaded");

    let callbackMap: MapCompat | undefined;
    const loadedMap = await map.when((readyMap) => {
      callbackMap = readyMap;
    });

    expect(loadedMap).toBe(map);
    expect(callbackMap).toBe(map);
    expect(map.loaded).toBe(true);
    expect(map.loadStatus).toBe("loaded");
    expect(eventTypes).toContain("map.loading");
    expect(eventTypes).toContain("map.loaded");
  });

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

  it("supports watch handles for map property changes", async () => {
    const map = new MapCompat({
      basemap: "streets",
      layers: [],
    });

    const basemapValues: unknown[] = [];
    const layerCounts: number[] = [];
    const loadStatusValues: unknown[] = [];

    const basemapHandle = map.watch("basemap", (value) => {
      basemapValues.push(value);
    });
    const layersHandle = map.watch("layers", (value) => {
      layerCounts.push(Array.isArray(value) ? value.length : -1);
    });
    const loadStatusHandle = map.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });

    map.setBasemap("satellite");
    map.add({ id: "layer-1" });
    await map.load();

    basemapHandle.remove();
    layersHandle.remove();
    loadStatusHandle.remove();

    map.setBasemap("topographic");
    map.add({ id: "layer-2" });

    expect(basemapValues).toEqual(["satellite"]);
    expect(layerCounts).toEqual([1]);
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(map.loadStatus).toBe("loaded");
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

  it("preserves map metadata options and emits basemap/ground/table/spatial events", () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const map = new MapCompat({
      basemap: "streets",
      ground: "world-elevation",
      tables: [{ id: "table-1" }],
      portalItem: { id: "webmap-1" },
      spatialReference: { wkid: 3857 },
      eventBus,
    });

    expect(map.basemap).toBe("streets");
    expect(map.ground).toBe("world-elevation");
    expect(map.tables).toEqual([{ id: "table-1" }]);
    expect(map.portalItem).toEqual({ id: "webmap-1" });
    expect(map.spatialReference).toEqual({ wkid: 3857 });

    map.setBasemap("satellite");
    map.setGround("custom-ground");
    map.setTables([{ id: "table-2" }]);
    map.setSpatialReference({ wkid: 4326 });

    expect(map.basemap).toBe("satellite");
    expect(map.ground).toBe("custom-ground");
    expect(map.tables).toEqual([{ id: "table-2" }]);
    expect(map.spatialReference).toEqual({ wkid: 4326 });
    expect(eventTypes).toContain("map.basemap-changed");
    expect(eventTypes).toContain("map.ground-changed");
    expect(eventTypes).toContain("map.tables-changed");
    expect(eventTypes).toContain("map.spatial-reference-changed");
  });
});

describe("MapViewCompat", () => {
  it("supports when() and goTo() state updates", async () => {
    const map = new MapCompat();
    const view = new MapViewCompat({
      map,
      container: "viewDiv",
      zoom: 3,
      scale: 5000000,
      rotation: 10,
      center: [-157.8, 21.3],
      extent: { xmin: -160, ymin: 20, xmax: -155, ymax: 23 },
      constraints: { minZoom: 2 },
      padding: { left: 16, right: 16, top: 8, bottom: 8 },
      highlightOptions: { color: "#ff0" },
      spatialReference: { wkid: 4326 },
    });

    let callbackView: MapViewCompat | undefined;
    const ready = await view.when((resolvedView) => {
      callbackView = resolvedView;
    });
    expect(ready).toBe(view);
    expect(callbackView).toBe(view);
    expect(view.map).toBe(map);
    expect(view.zoom).toBe(3);
    expect(view.scale).toBe(5000000);
    expect(view.rotation).toBe(10);
    expect(view.extent).toEqual({ xmin: -160, ymin: 20, xmax: -155, ymax: 23 });
    expect(view.constraints).toEqual({ minZoom: 2 });
    expect(view.padding).toEqual({ left: 16, right: 16, top: 8, bottom: 8 });
    expect(view.highlightOptions).toEqual({ color: "#ff0" });
    expect(view.spatialReference).toEqual({ wkid: 4326 });

    await view.goTo({
      zoom: 8,
      center: [-155, 19.5],
      scale: 1200000,
      rotation: 35,
      extent: { xmin: -159, ymin: 19, xmax: -154, ymax: 24 },
    });
    expect(view.zoom).toBe(8);
    expect(view.center).toEqual([-155, 19.5]);
    expect(view.scale).toBe(1200000);
    expect(view.rotation).toBe(35);
    expect(view.extent).toEqual({ xmin: -159, ymin: 19, xmax: -154, ymax: 24 });
    expect(view.toMap({ x: 100, y: 200 })).toEqual({
      x: 100,
      y: 200,
      spatialReference: { wkid: 4326 },
    });

    view.destroy();
    expect(view.map).toBeUndefined();
    expect(view.scale).toBeUndefined();
    expect(view.rotation).toBeUndefined();
    expect(view.extent).toBeUndefined();
    expect(view.spatialReference).toBeUndefined();
  });

  it("normalizes common ArcGIS goTo target shapes", async () => {
    const view = new MapViewCompat();
    const events: unknown[] = [];
    view.on("go-to", (event) => {
      events.push(event);
    });

    await view.goTo({
      geometry: {
        x: 10,
        y: 20,
        spatialReference: { wkid: 4326 },
      },
    });
    expect(view.center).toEqual({ x: 10, y: 20, spatialReference: { wkid: 4326 } });

    await view.goTo({
      geometry: {
        paths: [
          [
            [0, 0],
            [4, 6],
            [2, -2],
          ],
        ],
      },
    });
    expect(view.extent).toEqual({
      xmin: 0,
      ymin: -2,
      xmax: 4,
      ymax: 6,
      spatialReference: undefined,
    });
    expect(view.center).toEqual({ x: 2, y: 2, spatialReference: undefined });

    await view.goTo(
      [
        { geometry: { x: -3, y: 5 } },
        { geometry: { x: 7, y: 9 } },
      ],
      { animate: false, duration: 1200 },
    );
    expect(view.extent).toEqual({
      xmin: -3,
      ymin: 5,
      xmax: 7,
      ymax: 9,
      spatialReference: undefined,
    });
    expect(view.center).toEqual({ x: 2, y: 7, spatialReference: undefined });

    await view.goTo([30, 40]);
    expect(view.center).toEqual([30, 40]);

    expect(events).toContainEqual({
      target: [
        { geometry: { x: -3, y: 5 } },
        { geometry: { x: 7, y: 9 } },
      ],
      options: { animate: false, duration: 1200 },
    });
  });

  it("supports watch and on handles", async () => {
    const view = new MapViewCompat({
      zoom: 2,
      scale: 10000000,
      rotation: 0,
      center: [0, 0],
    });

    const zoomValues: unknown[] = [];
    const scaleValues: unknown[] = [];
    const rotationValues: unknown[] = [];
    const centerValues: unknown[] = [];
    const events: unknown[] = [];

    const zoomHandle = view.watch("zoom", (value) => {
      zoomValues.push(value);
    });
    const centerHandle = view.watch("center", (value) => {
      centerValues.push(value);
    });
    const scaleHandle = view.watch("scale", (value) => {
      scaleValues.push(value);
    });
    const rotationHandle = view.watch("rotation", (value) => {
      rotationValues.push(value);
    });
    const eventHandle = view.on("go-to", (event) => {
      events.push(event);
    });

    await view.goTo({ zoom: 4, center: [10, 20], scale: 2500000, rotation: 20 });
    expect(zoomValues).toEqual([4]);
    expect(scaleValues).toEqual([2500000]);
    expect(rotationValues).toEqual([20]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20], scale: 2500000, rotation: 20 }]);

    zoomHandle.remove();
    centerHandle.remove();
    scaleHandle.remove();
    rotationHandle.remove();
    eventHandle.remove();

    await view.goTo({ zoom: 6, center: [30, 40], scale: 1000000, rotation: 35 });
    expect(zoomValues).toEqual([4]);
    expect(scaleValues).toEqual([2500000]);
    expect(rotationValues).toEqual([20]);
    expect(centerValues).toEqual([[10, 20]]);
    expect(events).toEqual([{ zoom: 4, center: [10, 20], scale: 2500000, rotation: 20 }]);
  });

  it("supports public emit dispatch for event listeners", () => {
    const view = new MapViewCompat();
    const clicks: unknown[] = [];

    const handle = view.on("click", (event) => {
      clicks.push(event);
    });

    expect(view.emit("click", { x: 10, y: 20 })).toBe(true);
    expect(clicks).toEqual([{ x: 10, y: 20 }]);

    handle.remove();
    expect(view.emit("click", { x: 30, y: 40 })).toBe(false);
    expect(clicks).toEqual([{ x: 10, y: 20 }]);
  });

  it("supports direct state mutators and emits change events", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const view = new MapViewCompat({ eventBus });

    view.setCenter([10, 20]);
    view.setZoom(7);
    view.setScale(2500000);
    view.setRotation(30);
    view.setExtent({ xmin: 0, ymin: 0, xmax: 10, ymax: 10 });
    view.setPadding({ left: 12, right: 12, top: 4, bottom: 4 });
    view.setConstraints({ minZoom: 2 });
    view.setHighlightOptions({ color: "#0ff" });
    view.setSpatialReference({ wkid: 4326 });

    expect(view.center).toEqual([10, 20]);
    expect(view.zoom).toBe(7);
    expect(view.scale).toBe(2500000);
    expect(view.rotation).toBe(30);
    expect(view.extent).toEqual({ xmin: 0, ymin: 0, xmax: 10, ymax: 10 });
    expect(view.padding).toEqual({ left: 12, right: 12, top: 4, bottom: 4 });
    expect(view.constraints).toEqual({ minZoom: 2 });
    expect(view.highlightOptions).toEqual({ color: "#0ff" });
    expect(view.spatialReference).toEqual({ wkid: 4326 });
    expect(view.toMap({ x: 1, y: 2 })).toEqual({ x: 1, y: 2, spatialReference: { wkid: 4326 } });
    expect(events).toContain("view.center-changed");
    expect(events).toContain("view.zoom-changed");
    expect(events).toContain("view.scale-changed");
    expect(events).toContain("view.rotation-changed");
    expect(events).toContain("view.extent-changed");
    expect(events).toContain("view.padding-changed");
    expect(events).toContain("view.constraints-changed");
    expect(events).toContain("view.highlight-options-changed");
    expect(events).toContain("view.spatial-reference-changed");
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
    const popupSelectionIndexes: unknown[] = [];
    const popupEvents: unknown[] = [];

    view.watch("popup.visible", (value) => {
      popupVisibility.push(value);
    });
    view.watch("popup.viewModel.active", (value) => {
      popupActive.push(value);
    });
    view.watch("popup.selectedFeatureIndex", (value) => {
      popupSelectionIndexes.push(value);
    });
    view.on("popup-open", (event) => {
      popupEvents.push({ type: "open", event });
    });
    view.on("popup-close", (event) => {
      popupEvents.push({ type: "close", event });
    });
    view.on("popup-selection-change", (event) => {
      popupEvents.push({ type: "selection", event });
    });

    view.openPopup({
      title: "Layer Info",
      content: "Details",
      location: [1, 2],
      features: [{ id: 123 }, { id: 456 }],
    });
    expect(view.popup.visible).toBe(true);
    expect(view.popup.title).toBe("Layer Info");
    expect(view.popup.content).toBe("Details");
    expect(view.popup.location).toEqual([1, 2]);
    expect(view.popup.features).toEqual([{ id: 123 }, { id: 456 }]);
    expect(view.popup.selectedFeature).toEqual({ id: 123 });
    expect(view.popup.selectedFeatureIndex).toBe(0);
    expect(view.popup.viewModel.active).toBe(true);
    expect(view.popup.dockEnabled).toBe(true);
    expect(view.popup.dockOptions).toEqual({ breakpoint: false });

    expect(view.popup.next()).toEqual({ id: 456 });
    expect(view.popup.selectedFeatureIndex).toBe(1);
    expect(view.popup.previous()).toEqual({ id: 123 });
    expect(view.popup.selectedFeatureIndex).toBe(0);

    view.closePopup();
    expect(view.popup.visible).toBe(false);
    expect(view.popup.features).toEqual([]);
    expect(view.popup.selectedFeature).toBeUndefined();
    expect(view.popup.selectedFeatureIndex).toBe(-1);
    expect(view.popup.viewModel.active).toBe(false);
    expect(view.popup.title).toBeUndefined();
    expect(view.popup.content).toBeUndefined();

    expect(popupVisibility).toEqual([true, true, true, false]);
    expect(popupActive).toEqual([true, true, true, false]);
    expect(popupSelectionIndexes).toEqual([0, 1, 0, -1]);
    expect(popupEvents).toHaveLength(4);
    expect(popupEvents[0]).toEqual({
      type: "open",
      event: {
        title: "Layer Info",
        content: "Details",
        location: [1, 2],
        features: [{ id: 123 }, { id: 456 }],
      },
    });
    expect(popupEvents[1]).toEqual({
      type: "selection",
      event: {
        selectedFeature: { id: 456 },
        selectedFeatureIndex: 1,
      },
    });
    expect(popupEvents[2]).toEqual({
      type: "selection",
      event: {
        selectedFeature: { id: 123 },
        selectedFeatureIndex: 0,
      },
    });
    expect(popupEvents[3]).toEqual({ type: "close", event: undefined });
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
