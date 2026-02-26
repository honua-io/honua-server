import { describe, expect, it } from "vitest";

import { CompatEventBus, MapCompat, TableListCompat } from "../src/index.js";

describe("TableListCompat", () => {
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
