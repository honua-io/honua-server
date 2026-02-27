import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureCompat } from "../src/index.js";

describe("FeatureCompat", () => {
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
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const feature = new FeatureCompat({ eventBus });
    feature.setGraphic({ attributes: { OBJECTID: 10 } }, "Parcel");
    feature.clear();

    expect(seenTypes).toContain("feature-widget.updated");
    expect(seenTypes).toContain("feature-widget.cleared");
  });
});
