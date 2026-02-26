import { describe, expect, it } from "vitest";

import { WebMapCompat } from "../src/index.js";

describe("WebMapCompat", () => {
  it("supports portalItem and when/load lifecycle", async () => {
    const map = new WebMapCompat({
      portalItem: { id: "abc123" },
    });

    expect(map.loaded).toBe(false);
    expect(map.portalItem).toEqual({ id: "abc123" });

    let callbackMap: WebMapCompat | undefined;
    const resolved = await map.when((readyMap) => {
      callbackMap = readyMap;
    });

    expect(resolved).toBe(map);
    expect(callbackMap).toBe(map);
    expect(map.loaded).toBe(true);
  });

  it("inherits map-level options and mutators", () => {
    const map = new WebMapCompat({
      portalItem: { id: "abc123" },
      basemap: "satellite",
      layers: [{ id: "layer-a" }],
      ground: "world-elevation",
      tables: [{ id: "table-a" }],
      spatialReference: { wkid: 3857 },
    });

    expect(map.basemap).toBe("satellite");
    expect(map.layers).toEqual([{ id: "layer-a" }]);
    expect(map.ground).toBe("world-elevation");
    expect(map.tables).toEqual([{ id: "table-a" }]);
    expect(map.spatialReference).toEqual({ wkid: 3857 });

    map.setBasemap("streets");
    map.setGround("custom-ground");
    map.setTables([{ id: "table-b" }]);
    map.setSpatialReference({ wkid: 4326 });

    expect(map.basemap).toBe("streets");
    expect(map.ground).toBe("custom-ground");
    expect(map.tables).toEqual([{ id: "table-b" }]);
    expect(map.spatialReference).toEqual({ wkid: 4326 });
  });
});
