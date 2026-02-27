import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, TableListCompat } from "../src/index.js";

describe("TableListCompat", () => {
  it("supports when() and watch() lifecycle and table updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const list = new TableListCompat({ eventBus, tables: [{ id: "t1" }] });
    const loadStatusValues: unknown[] = [];
    const tableCounts: number[] = [];
    const loadStatusHandle = list.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const tablesHandle = list.watch("tables", (value) => {
      tableCounts.push(Array.isArray(value) ? value.length : -1);
    });

    let callbackWidget: TableListCompat | undefined;
    const widget = await list.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    list.setTables([{ id: "t2" }, { id: "t3" }]);

    loadStatusHandle.remove();
    tablesHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      tables: tableCounts.length,
    };
    list.setTables([{ id: "t4" }]);

    expect(widget).toBe(list);
    expect(callbackWidget).toBe(list);
    expect(list.loaded).toBe(true);
    expect(list.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(tableCounts).toEqual([1, 2]);
    expect(seenTypes).toContain("table-list.loading");
    expect(seenTypes).toContain("table-list.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(tableCounts).toHaveLength(watchSnapshot.tables);
  });

  it("stores and updates table entries", () => {
    const list = new TableListCompat({ tables: [{ id: "tbl-1" }] });
    expect(list.tables).toHaveLength(1);

    list.setTables([{ id: "tbl-2" }, { id: "tbl-3" }]);
    expect(list.tables).toHaveLength(2);
  });

  it("emits table update events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const list = new TableListCompat({ eventBus });
    list.setTables([{}, {}]);

    expect(seenTypes).toContain("table-list.tables-changed");
  });

  it("hydrates from map tables and auto-refreshes on map table changes", () => {
    const eventBus = new CompatEventBus();
    const map = new MapCompat({
      tables: [{ id: "parcels" }],
      eventBus,
    });
    const list = new TableListCompat({ map, eventBus });

    expect(list.tables).toEqual([{ id: "parcels" }]);

    map.setTables([{ id: "roads" }, { id: "zoning" }]);
    expect(list.tables).toEqual([{ id: "roads" }, { id: "zoning" }]);

    list.setTables([{ id: "manual" }]);
    map.setTables([{ id: "ignored" }]);
    expect(list.tables).toEqual([{ id: "manual" }]);

    list.useMapTables();
    expect(list.tables).toEqual([{ id: "ignored" }]);

    list.destroy();
  });
});
