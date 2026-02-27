import { describe, expect, it } from "vitest";

import { CompatEventBus, MapViewCompat, PopupCompat } from "../src/index.js";

describe("PopupCompat", () => {
  it("supports when() and watch() for lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const widget = new PopupCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = widget.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = widget.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackWidget: PopupCompat | undefined;
    const resolved = await widget.when((popup) => {
      callbackWidget = popup;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await widget.load();

    expect(resolved).toBe(widget);
    expect(callbackWidget).toBe(widget);
    expect(widget.loaded).toBe(true);
    expect(widget.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(seenTypes).toContain("popup.loading");
    expect(seenTypes).toContain("popup.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("syncs with MapView popup bridge over the shared event bus", () => {
    const eventBus = new CompatEventBus();
    const view = new MapViewCompat({ eventBus });
    const widget = new PopupCompat({ view, eventBus });

    view.openPopup({
      location: [1, 2],
      title: "Feature",
      content: "Details",
      features: [{ id: 101 }, { id: 202 }],
    });

    expect(widget.visible).toBe(true);
    expect(widget.title).toBe("Feature");
    expect(widget.content).toBe("Details");
    expect(widget.location).toEqual([1, 2]);
    expect(widget.features).toEqual([{ id: 101 }, { id: 202 }]);
    expect(widget.selectedFeature).toEqual({ id: 101 });
    expect(widget.selectedFeatureIndex).toBe(0);

    expect(widget.next()).toEqual({ id: 202 });
    expect(widget.selectedFeature).toEqual({ id: 202 });
    expect(widget.selectedFeatureIndex).toBe(1);
    expect(widget.previous()).toEqual({ id: 101 });
    expect(widget.selectedFeature).toEqual({ id: 101 });
    expect(widget.selectedFeatureIndex).toBe(0);

    widget.close();
    expect(view.popup.visible).toBe(false);
    expect(widget.visible).toBe(false);
    expect(widget.features).toEqual([]);
    expect(widget.selectedFeatureIndex).toBe(-1);
  });

  it("supports standalone open/close with watch listeners and event emissions", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    const visibility: unknown[] = [];
    const selectedIndexes: unknown[] = [];

    eventBus.onAny((event) => {
      events.push(event.type);
    });

    const widget = new PopupCompat({ eventBus });
    widget.watch("visible", (value) => {
      visibility.push(value);
    });
    widget.watch("selectedFeatureIndex", (value) => {
      selectedIndexes.push(value);
    });

    widget.open({
      location: { x: 0, y: 1 },
      title: "Standalone",
      content: "Popup",
      features: [{ id: "a" }, { id: "b" }],
    });

    expect(widget.visible).toBe(true);
    expect(widget.selectedFeature).toEqual({ id: "a" });
    expect(widget.selectedFeatureIndex).toBe(0);
    expect(widget.selectFeature(1)).toEqual({ id: "b" });
    expect(widget.selectedFeatureIndex).toBe(1);
    expect(widget.previous()).toEqual({ id: "a" });
    expect(widget.selectedFeatureIndex).toBe(0);

    widget.close();
    expect(widget.visible).toBe(false);
    expect(widget.selectedFeature).toBeUndefined();
    expect(widget.selectedFeatureIndex).toBe(-1);
    expect(visibility).toEqual([true, false]);
    expect(selectedIndexes).toEqual([0, 1, 0, -1]);
    expect(events).toContain("popup.open");
    expect(events).toContain("popup.close");
    expect(events).toContain("popup.selected-feature-changed");

    widget.destroy();
  });
});
