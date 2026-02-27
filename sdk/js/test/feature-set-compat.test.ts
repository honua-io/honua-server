import { describe, expect, it } from "vitest";

import { FeatureSetCompat } from "../src/index.js";

describe("FeatureSetCompat", () => {
  it("supports when() and watch() lifecycle state transitions", async () => {
    const set = new FeatureSetCompat({
      features: [{ attributes: { OBJECTID: 1 } }],
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = set.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = set.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackSet: FeatureSetCompat | undefined;
    const resolved = await set.when((featureSet) => {
      callbackSet = featureSet;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await set.load();

    expect(resolved).toBe(set);
    expect(callbackSet).toBe(set);
    expect(set.loaded).toBe(true);
    expect(set.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

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
