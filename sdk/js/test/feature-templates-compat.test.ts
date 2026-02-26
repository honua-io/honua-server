import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureTemplatesCompat } from "../src/index.js";

describe("FeatureTemplatesCompat", () => {
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
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const templates = new FeatureTemplatesCompat({
      eventBus,
      filterFunction: (item) => item.id !== "skip",
      groupBy: "layer",
    });
    templates.setTemplates([
      { id: "keep", name: "Keep" },
      { id: "skip", name: "Skip" },
    ]);
    templates.selectTemplate("keep");

    expect(templates.templates).toHaveLength(1);
    expect(templates.groupBy).toBe("layer");
    expect(seenTypes).toContain("feature-templates.updated");
    expect(seenTypes).toContain("feature-templates.selected");
  });
});
