import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureTableCompat } from "../src/index.js";
import { FeatureLayerCompat } from "../src/esri-compat/feature-layer.js";

describe("FeatureTableCompat", () => {
  it("supports when() and watch() for state/rows/filter changes", async () => {
    const layer = {
      queryFeatures: async () => ({
        features: [{ attributes: { OBJECTID: 10, name: "Sample" }, geometry: { x: 0, y: 0 } }],
      }),
    } as unknown as FeatureLayerCompat;

    const table = new FeatureTableCompat({ layer, where: "1=1" });
    const stateValues: unknown[] = [];
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const sizeValues: unknown[] = [];
    const whereValues: unknown[] = [];

    const stateHandle = table.watch("state", (value) => {
      stateValues.push(value);
    });
    const loadStatusHandle = table.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = table.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const sizeHandle = table.watch("size", (value) => {
      sizeValues.push(value);
    });
    const whereHandle = table.watch("where", (value) => {
      whereValues.push(value);
    });

    let callbackTable: FeatureTableCompat | undefined;
    const readyTable = await table.when((resolvedTable) => {
      callbackTable = resolvedTable;
    });
    table.setWhere("status = 'active'");

    stateHandle.remove();
    loadStatusHandle.remove();
    loadedHandle.remove();
    sizeHandle.remove();
    whereHandle.remove();

    table.setWhere("status = 'inactive'");

    expect(readyTable).toBe(table);
    expect(callbackTable).toBe(table);
    expect(table.loaded).toBe(true);
    expect(table.loadStatus).toBe("loaded");
    expect(stateValues).toEqual(["loading", "loaded"]);
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(sizeValues).toEqual([1]);
    expect(whereValues).toEqual(["status = 'active'"]);
    expect(table.where).toBe("status = 'inactive'");
  });

  it("refreshes rows from layer query and emits state/refresh events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const layer = {
      queryFeatures: async () => ({
        features: [
          { attributes: { OBJECTID: 1, name: "A" }, geometry: { x: -157.85, y: 21.30 } },
          { attributes: { OBJECTID: 2, name: "B" }, geometry: { x: -157.90, y: 21.31 } },
        ],
      }),
    } as unknown as FeatureLayerCompat;

    const table = new FeatureTableCompat({ layer, where: "1=1", eventBus, multiSortEnabled: true });
    const rows = await table.refresh();

    expect(table.state).toBe("loaded");
    expect(table.loadStatus).toBe("loaded");
    expect(table.size).toBe(2);
    expect(table.multiSortEnabled).toBe(true);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ objectId: 1 });
    expect(rows[1]).toMatchObject({ objectId: 2 });
    expect(seenTypes).toContain("feature-table.loading");
    expect(seenTypes).toContain("feature-table.loaded");
    expect(seenTypes).toContain("feature-table.state-changed");
    expect(seenTypes).toContain("feature-table.refreshed");
  });

  it("tracks highlightIds collection changes and selection state", () => {
    const eventBus = new CompatEventBus();
    const seenChanges: unknown[] = [];
    const table = new FeatureTableCompat({ eventBus, highlightIds: [2] });
    table.rows = [
      { objectId: 1, attributes: { name: "A" }, geometry: null },
      { objectId: 2, attributes: { name: "B" }, geometry: null },
    ];

    table.highlightIds.on("change", (event) => {
      seenChanges.push(event);
    });

    table.highlightIds.push(1, 2);
    expect(table.highlightIds.length).toBe(2);
    expect(table.highlightIds.indexOf(2)).toBe(0);
    expect(table.highlightIds.indexOf(1)).toBe(1);
    expect(table.getSelectedRows()).toHaveLength(2);
    expect(seenChanges[0]).toEqual({ added: [1], removed: [] });

    table.highlightIds.splice(1, 1);
    expect(table.getSelectedObjectIds()).toEqual([2]);
    expect(seenChanges[1]).toEqual({ added: [], removed: [1] });

    table.selectRows([2, 1, Number.NaN]);
    expect(table.getSelectedObjectIds()).toEqual([2, 1]);
    expect(table.getSelectedRows()).toHaveLength(2);

    table.clearSelection();
    expect(table.getSelectedObjectIds()).toEqual([]);
    expect(table.getSelectedRows()).toEqual([]);
  });

  it("supports related-records queries via the attached layer", async () => {
    const calls: unknown[] = [];
    const layer = {
      queryRelatedFeatures: async (options: unknown) => {
        calls.push(options);
        return {
          relatedRecordGroups: [
            { objectId: 1, relatedRecords: [{ attributes: { OBJECTID: 101 } }] },
          ],
        };
      },
    } as unknown as FeatureLayerCompat;

    const table = new FeatureTableCompat({
      layer,
      where: "status = 'active'",
      highlightIds: [1],
    });

    const response = await table.queryRelatedRecords({
      relationshipId: 0,
    });

    expect(calls).toEqual([
      {
        relationshipId: 0,
        objectIds: [1],
        where: "status = 'active'",
        outFields: undefined,
        returnGeometry: undefined,
        method: undefined,
        extraParams: undefined,
      },
    ]);
    expect(response).toMatchObject({
      relatedRecordGroups: [{ objectId: 1 }],
    });
  });
});
