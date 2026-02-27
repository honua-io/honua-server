import { describe, expect, it } from "vitest";

import { BasemapGalleryCompat, CompatEventBus, MapCompat, MapViewCompat } from "../src/index.js";

describe("BasemapGalleryCompat", () => {
  it("supports when() and watch() for lifecycle and active basemap updates", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });
    const map = new MapCompat({ basemap: "streets", eventBus });
    const gallery = new BasemapGalleryCompat({
      map,
      eventBus,
      source: [{ id: "streets" }, { id: "imagery" }],
      autoRefresh: false,
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const activeBasemapValues: unknown[] = [];
    const loadStatusHandle = gallery.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = gallery.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const activeBasemapHandle = gallery.watch("activeBasemap", (value) => {
      activeBasemapValues.push(value);
    });

    let callbackWidget: BasemapGalleryCompat | undefined;
    const widget = await gallery.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    gallery.select("imagery");

    loadStatusHandle.remove();
    loadedHandle.remove();
    activeBasemapHandle.remove();

    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      activeBasemap: activeBasemapValues.length,
    };
    gallery.select("streets");

    expect(widget).toBe(gallery);
    expect(callbackWidget).toBe(gallery);
    expect(gallery.loaded).toBe(true);
    expect(gallery.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(activeBasemapValues).toEqual(["streets", { id: "imagery" }]);
    expect(seenTypes).toContain("basemap-gallery.loading");
    expect(seenTypes).toContain("basemap-gallery.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(activeBasemapValues).toHaveLength(watchSnapshot.activeBasemap);
  });

  it("selects basemap by id/title and updates map basemap", () => {
    const eventBus = new CompatEventBus();
    const events: string[] = [];
    eventBus.onAny((event) => {
      events.push(event.type);
    });
    const map = new MapCompat({ basemap: "streets", eventBus }) as MapCompat & { basemap: unknown };
    const view = new MapViewCompat({ map, eventBus });

    const streets = { id: "streets", title: "Streets" };
    const imagery = { id: "imagery", title: "Imagery" };

    const gallery = new BasemapGalleryCompat({
      view,
      eventBus,
      source: [streets, imagery],
    });

    expect(gallery.activeBasemap).toBe("streets");
    expect(gallery.select("imagery")).toBe(imagery);
    expect(map.basemap).toBe(imagery);
    expect(gallery.activeBasemap).toBe(imagery);

    expect(gallery.select("Streets")).toBe(streets);
    expect(map.basemap).toBe(streets);
    expect(gallery.activeBasemap).toBe(streets);
    expect(events).toContain("map.basemap-changed");
    expect(events).toContain("basemap-gallery.selected");

    gallery.destroy();
  });

  it("updates source and returns undefined for unknown ids", () => {
    const gallery = new BasemapGalleryCompat();
    const source = [{ id: "a" }, { id: "b" }];

    gallery.setBasemaps(source);
    expect(gallery.basemaps).toEqual(source);
    expect(gallery.select("missing")).toBeUndefined();

    gallery.destroy();
  });

  it("auto-refreshes active basemap when map basemap changes externally", () => {
    const eventBus = new CompatEventBus();
    const map = new MapCompat({ basemap: "streets", eventBus });
    const gallery = new BasemapGalleryCompat({ map, eventBus });

    expect(gallery.activeBasemap).toBe("streets");
    map.setBasemap("topographic");
    expect(gallery.activeBasemap).toBe("topographic");

    gallery.destroy();
  });
});
