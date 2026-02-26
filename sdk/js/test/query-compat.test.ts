import { describe, expect, it } from "vitest";

import { QueryCompat } from "../src/index.js";

describe("QueryCompat", () => {
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
