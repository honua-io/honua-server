import { describe, expect, it } from "vitest";

import { CompatEventBus, WebMapCompat } from "../src/index.js";

describe("WebMapCompat", () => {
  it("supports portalItem and when/load lifecycle", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const map = new WebMapCompat({
      eventBus,
      portalItem: { id: "abc123" },
    });

    expect(map.loaded).toBe(false);
    expect(map.loadStatus).toBe("not-loaded");
    expect(map.portalItem).toEqual({ id: "abc123" });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const portalItemValues: unknown[] = [];
    const loadStatusHandle = map.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = map.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const portalItemHandle = map.watch("portalItem", (value) => {
      portalItemValues.push(value);
    });

    let callbackMap: WebMapCompat | undefined;
    const resolved = await map.when((readyMap) => {
      callbackMap = readyMap;
    });

    map.setPortalItem({ id: "abc124" });

    loadStatusHandle.remove();
    loadedHandle.remove();
    portalItemHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      portalItem: portalItemValues.length,
    };

    await map.load();
    map.setPortalItem({ id: "abc125" });

    expect(resolved).toBe(map);
    expect(callbackMap).toBe(map);
    expect(map.loaded).toBe(true);
    expect(map.loadStatus).toBe("loaded");
    expect(map.portalItem).toEqual({ id: "abc125" });
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(portalItemValues).toEqual([{ id: "abc124" }]);
    expect(seenTypes).toContain("map.loading");
    expect(seenTypes).toContain("map.loaded");
    expect(seenTypes).toContain("map.portal-item-changed");
    expect(seenTypes).toContain("web-map.portal-item-changed");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(portalItemValues).toHaveLength(watchSnapshot.portalItem);
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
    map.setPortalItem({ id: "abc124" });
    map.setSpatialReference({ wkid: 4326 });

    expect(map.basemap).toBe("streets");
    expect(map.ground).toBe("custom-ground");
    expect(map.tables).toEqual([{ id: "table-b" }]);
    expect(map.portalItem).toEqual({ id: "abc124" });
    expect(map.spatialReference).toEqual({ wkid: 4326 });
  });
});
