import { describe, expect, it } from "vitest";

import { CompatEventBus, TableListCompat } from "../src/index.js";

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
});
