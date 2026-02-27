import { describe, expect, it } from "vitest";

import { QueryCompat } from "../src/index.js";

describe("QueryCompat", () => {
  it("supports when() and watch() lifecycle state", async () => {
    const query = new QueryCompat();
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = query.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = query.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackQuery: QueryCompat | undefined;
    const widget = await query.when((resolvedQuery) => {
      callbackQuery = resolvedQuery;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };
    await query.load();

    expect(widget).toBe(query);
    expect(callbackQuery).toBe(query);
    expect(query.loaded).toBe(true);
    expect(query.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("normalizes common query options", () => {
    const query = new QueryCompat({
      where: "status = 'active'",
      outFields: "*",
      returnGeometry: false,
      orderByFields: "OBJECTID DESC",
      objectIds: [1, 2, Number.NaN, 3],
      num: 25,
      start: 5,
    });

    expect(query.where).toBe("status = 'active'");
    expect(query.outFields).toEqual(["*"]);
    expect(query.returnGeometry).toBe(false);
    expect(query.orderByFields).toEqual(["OBJECTID DESC"]);
    expect(query.objectIds).toEqual([1, 2, 3]);
    expect(query.num).toBe(25);
    expect(query.start).toBe(5);
  });

  it("clones and serializes safely", () => {
    const query = new QueryCompat({
      outFields: ["OBJECTID", "status"],
      groupByFieldsForStatistics: ["status"],
      outStatistics: [{ statisticType: "count", onStatisticField: "OBJECTID", outStatisticFieldName: "cnt" }],
    });

    const clone = query.clone();
    expect(clone).not.toBe(query);
    expect(clone.toJSON()).toEqual(query.toJSON());

    const json = query.toJSON();
    expect(json.where).toBe("1=1");
    expect(json.returnGeometry).toBe(true);
  });
});
