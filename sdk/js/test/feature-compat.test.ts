import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureCompat } from "../src/index.js";

describe("FeatureCompat", () => {
  it("supports when() and watch() lifecycle state transitions", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const feature = new FeatureCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = feature.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = feature.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackFeature: FeatureCompat | undefined;
    const resolved = await feature.when((widget) => {
      callbackFeature = widget;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await feature.load();

    expect(resolved).toBe(feature);
    expect(callbackFeature).toBe(feature);
    expect(feature.loaded).toBe(true);
    expect(feature.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(seenTypes).toContain("feature-widget.loading");
    expect(seenTypes).toContain("feature-widget.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("sets and clears graphic state", () => {
    const feature = new FeatureCompat({
      graphic: { attributes: { OBJECTID: 1 } },
      title: "Initial",
    });

    feature.setGraphic({ attributes: { OBJECTID: 2 } }, "Updated");
    expect(feature.title).toBe("Updated");
    expect(feature.graphic).toMatchObject({ attributes: { OBJECTID: 2 } });

    feature.clear();
    expect(feature.graphic).toBeUndefined();
  });

  it("emits update and clear events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    const graphics: unknown[] = [];
    const titles: unknown[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const feature = new FeatureCompat({ eventBus });
    const graphicHandle = feature.watch("graphic", (value) => {
      graphics.push(value);
    });
    const titleHandle = feature.watch("title", (value) => {
      titles.push(value);
    });
    feature.setGraphic({ attributes: { OBJECTID: 10 } }, "Parcel");
    feature.clear();
    graphicHandle.remove();
    titleHandle.remove();
    const watchSnapshot = {
      graphics: graphics.length,
      titles: titles.length,
    };

    feature.setGraphic({ attributes: { OBJECTID: 11 } }, "Parcel 11");

    expect(seenTypes).toContain("feature-widget.updated");
    expect(seenTypes).toContain("feature-widget.cleared");
    expect(graphics).toEqual([{ attributes: { OBJECTID: 10 } }, undefined]);
    expect(titles).toEqual(["Parcel"]);
    expect(graphics).toHaveLength(watchSnapshot.graphics);
    expect(titles).toHaveLength(watchSnapshot.titles);
  });
});
