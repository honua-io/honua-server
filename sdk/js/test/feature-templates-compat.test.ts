import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureTemplatesCompat } from "../src/index.js";

describe("FeatureTemplatesCompat", () => {
  it("supports when() and watch() for lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const templates = new FeatureTemplatesCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = templates.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = templates.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackWidget: FeatureTemplatesCompat | undefined;
    const resolved = await templates.when((widget) => {
      callbackWidget = widget;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await templates.load();

    expect(resolved).toBe(templates);
    expect(callbackWidget).toBe(templates);
    expect(templates.loaded).toBe(true);
    expect(templates.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(seenTypes).toContain("feature-templates.loading");
    expect(seenTypes).toContain("feature-templates.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("stores templates and selects by id", () => {
    const templates = new FeatureTemplatesCompat();
    templates.setTemplates([
      { id: "residential", name: "Residential" },
      { id: "commercial", name: "Commercial" },
    ]);

    const selected = templates.selectTemplate("commercial");
    expect(templates.templates).toHaveLength(2);
    expect(selected).toMatchObject({ id: "commercial", name: "Commercial" });
  });

  it("applies filter function and emits events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    const templateCounts: number[] = [];
    const selections: unknown[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const templates = new FeatureTemplatesCompat({
      eventBus,
      filterFunction: (item) => item.id !== "skip",
      groupBy: "layer",
    });
    const templateHandle = templates.watch("templates", (value) => {
      if (Array.isArray(value)) {
        templateCounts.push(value.length);
      }
    });
    const selectedHandle = templates.watch("selectedTemplate", (value) => {
      selections.push(value);
    });
    templates.setTemplates([
      { id: "keep", name: "Keep" },
      { id: "skip", name: "Skip" },
    ]);
    templates.selectTemplate("keep");
    templateHandle.remove();
    selectedHandle.remove();
    const watchSnapshot = {
      templateCounts: templateCounts.length,
      selections: selections.length,
    };

    templates.setTemplates([{ id: "after-remove", name: "After Remove" }]);
    templates.selectTemplate("after-remove");

    expect(templates.templates).toHaveLength(1);
    expect(templates.groupBy).toBe("layer");
    expect(seenTypes).toContain("feature-templates.updated");
    expect(seenTypes).toContain("feature-templates.selected");
    expect(templateCounts).toEqual([1]);
    expect(selections).toEqual([{ id: "keep", name: "Keep" }]);
    expect(templateCounts).toHaveLength(watchSnapshot.templateCounts);
    expect(selections).toHaveLength(watchSnapshot.selections);
  });
});
