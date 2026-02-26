import { describe, expect, it } from "vitest";

import { CompatEventBus, FeatureTableCompat } from "../src/index.js";
import { FeatureLayerCompat } from "../src/esri-compat/feature-layer.js";

describe("FeatureTableCompat", () => {
  it("refreshes rows from layer query and emits events", async () => {
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

    const table = new FeatureTableCompat({ layer, where: "1=1", eventBus });
    const rows = await table.refresh();

    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ objectId: 1 });
    expect(rows[1]).toMatchObject({ objectId: 2 });
    expect(seenTypes).toContain("feature-table.refreshed");
  });

  it("tracks row selection state", () => {
    const table = new FeatureTableCompat();
    table.rows = [
      { objectId: 1, attributes: { name: "A" }, geometry: null },
      { objectId: 2, attributes: { name: "B" }, geometry: null },
    ];

    table.selectRows([2, 1, Number.NaN]);
    expect(table.getSelectedObjectIds()).toEqual([2, 1]);
    expect(table.getSelectedRows()).toHaveLength(2);

    table.clearSelection();
    expect(table.getSelectedObjectIds()).toEqual([]);
    expect(table.getSelectedRows()).toEqual([]);
  });
});
