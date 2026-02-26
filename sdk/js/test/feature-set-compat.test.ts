import { describe, expect, it } from "vitest";

import { FeatureSetCompat } from "../src/index.js";

describe("FeatureSetCompat", () => {
  it("stores feature collection payloads", () => {
    const set = new FeatureSetCompat({
      fields: [{ name: "OBJECTID", type: "oid" }],
      features: [{ attributes: { OBJECTID: 1 } }],
      geometryType: "esriGeometryPoint",
      objectIdFieldName: "OBJECTID",
    });

    expect(set.features).toHaveLength(1);
    expect(set.fields).toHaveLength(1);
    expect(set.geometryType).toBe("esriGeometryPoint");
    expect(set.objectIdFieldName).toBe("OBJECTID");
  });

  it("clones and serializes", () => {
    const set = new FeatureSetCompat({
      features: [{ attributes: { OBJECTID: 10 } }],
      displayFieldName: "name",
    });

    const clone = set.clone();
    expect(clone).not.toBe(set);
    expect(clone.toJSON()).toEqual(set.toJSON());
  });
});
