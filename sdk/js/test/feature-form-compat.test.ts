import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureFormCompat } from "../src/index.js";

describe("FeatureFormCompat", () => {
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
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const form = new FeatureFormCompat({ eventBus });
    form.setFeature({ attributes: { OBJECTID: 10 } });
    await form.submit({ name: "Parcel 10" });

    expect(seenTypes).toContain("feature-form.feature-changed");
    expect(seenTypes).toContain("feature-form.submitted");
  });
});
