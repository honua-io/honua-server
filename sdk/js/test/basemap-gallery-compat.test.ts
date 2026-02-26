import { describe, expect, it } from "vitest";

import { BasemapGalleryCompat, CompatEventBus, MapCompat, MapViewCompat } from "../src/index.js";

describe("BasemapGalleryCompat", () => {
  it("selects basemap by id/title and updates map basemap", () => {
    const eventBus = new CompatEventBus();
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
  });

  it("updates source and returns undefined for unknown ids", () => {
    const gallery = new BasemapGalleryCompat();
    const source = [{ id: "a" }, { id: "b" }];

    gallery.setBasemaps(source);
    expect(gallery.basemaps).toEqual(source);
    expect(gallery.select("missing")).toBeUndefined();
  });
});
