import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureFormCompat } from "../src/index.js";

describe("FeatureFormCompat", () => {
  it("supports when() and watch() for lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const form = new FeatureFormCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = form.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = form.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackForm: FeatureFormCompat | undefined;
    const resolved = await form.when((widget) => {
      callbackForm = widget;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await form.load();

    expect(resolved).toBe(form);
    expect(callbackForm).toBe(form);
    expect(form.loaded).toBe(true);
    expect(form.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(seenTypes).toContain("feature-form.loading");
    expect(seenTypes).toContain("feature-form.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("updates feature state and submits values", async () => {
    const form = new FeatureFormCompat({
      feature: { attributes: { OBJECTID: 1, status: "Open" } },
      fieldConfig: [{ name: "status" }],
      groupDisplay: "all",
      headingLevel: 3,
      visibleElements: { description: true },
    });

    form.setFeature({ attributes: { OBJECTID: 2, status: "Closed" } });
    const result = await form.submit({ status: "Closed" });

    expect(result.valid).toBe(true);
    expect(result.values).toMatchObject({ status: "Closed" });
    expect(result.feature).toMatchObject({ attributes: { OBJECTID: 2 } });
    expect(form.groupDisplay).toBe("all");
    expect(form.headingLevel).toBe(3);
    expect(form.visibleElements).toEqual({ description: true });
  });

  it("emits feature change and submit events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    const features: unknown[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const form = new FeatureFormCompat({ eventBus });
    const featureHandle = form.watch("feature", (value) => {
      features.push(value);
    });
    form.setFeature({ attributes: { OBJECTID: 10 } });
    await form.submit({ name: "Parcel 10" });
    featureHandle.remove();
    const watchSnapshot = {
      features: features.length,
    };

    form.setFeature({ attributes: { OBJECTID: 11 } });

    expect(seenTypes).toContain("feature-form.feature-changed");
    expect(seenTypes).toContain("feature-form.submitted");
    expect(features).toEqual([{ attributes: { OBJECTID: 10 } }]);
    expect(features).toHaveLength(watchSnapshot.features);
  });
});
