import { describe, expect, it } from "vitest";

import {
  ClassBreaksRendererCompat,
  SimpleRendererCompat,
  UniqueValueRendererCompat,
} from "../src/index.js";

describe("renderer compat", () => {
  it("supports simple renderer payloads", async () => {
    const renderer = new SimpleRendererCompat({
      symbol: { type: "simple-marker", color: "orange" },
      label: "All features",
      description: "Default symbology",
      visualVariables: [{ type: "size", field: "count" }],
    });
    const labelValues: unknown[] = [];
    const labelHandle = renderer.watch("label", (value) => {
      labelValues.push(value);
    });

    await renderer.when();
    renderer.update({ label: "Updated label" });
    labelHandle.remove();
    const labelWatchCount = labelValues.length;
    renderer.update({ label: "Final label" });

    expect(renderer.toJSON()).toEqual({
      symbol: { type: "simple-marker", color: "orange" },
      label: "Final label",
      description: "Default symbology",
      visualVariables: [{ type: "size", field: "count" }],
    });
    expect(renderer.clone().toJSON()).toEqual(renderer.toJSON());
    expect(renderer.loaded).toBe(true);
    expect(renderer.loadStatus).toBe("loaded");
    expect(labelValues).toEqual(["Updated label"]);
    expect(labelValues).toHaveLength(labelWatchCount);
  });

  it("supports unique value renderer add/remove semantics", async () => {
    const renderer = new UniqueValueRendererCompat({
      field: "status",
      uniqueValueInfos: [{ value: "open", label: "Open" }],
    });
    const infoCounts: number[] = [];
    const infoHandle = renderer.watch("uniqueValueInfos", (value) => {
      if (Array.isArray(value)) {
        infoCounts.push(value.length);
      }
    });

    await renderer.when();
    renderer.addUniqueValueInfo({ value: "closed", label: "Closed" });
    expect(renderer.uniqueValueInfos).toHaveLength(2);
    expect(renderer.removeUniqueValueInfo("open")).toBe(true);
    expect(renderer.removeUniqueValueInfo("missing")).toBe(false);
    infoHandle.remove();
    const infoWatchCount = infoCounts.length;
    renderer.addUniqueValueInfo({ value: "archived", label: "Archived" });
    expect(renderer.clone().toJSON()).toEqual(renderer.toJSON());
    expect(infoCounts).toEqual([2, 1]);
    expect(infoCounts).toHaveLength(infoWatchCount);
  });

  it("supports class breaks renderer payloads", async () => {
    const renderer = new ClassBreaksRendererCompat({
      field: "population",
      minValue: 0,
      classBreakInfos: [{ minValue: 0, maxValue: 1000, label: "0-1000" }],
    });
    const breakCounts: number[] = [];
    const breakHandle = renderer.watch("classBreakInfos", (value) => {
      if (Array.isArray(value)) {
        breakCounts.push(value.length);
      }
    });

    await renderer.when();
    renderer.addClassBreakInfo({ minValue: 1000, maxValue: 5000, label: "1000-5000" });
    expect(renderer.classBreakInfos).toHaveLength(2);
    expect(renderer.removeClassBreakInfo(1000)).toBe(true);
    expect(renderer.removeClassBreakInfo(9999)).toBe(false);
    breakHandle.remove();
    const breakWatchCount = breakCounts.length;
    renderer.addClassBreakInfo({ minValue: 5000, maxValue: 9000, label: "5000-9000" });
    expect(renderer.clone().toJSON()).toEqual(renderer.toJSON());
    expect(breakCounts).toEqual([2, 1]);
    expect(breakCounts).toHaveLength(breakWatchCount);
  });
});
