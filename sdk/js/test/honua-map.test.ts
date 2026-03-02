import { describe, expect, it, vi } from "vitest";

import {
  HonuaMap,
  HonuaClient,
  HonuaFeatureLayer,
  HonuaMapService,
} from "../src/index.js";
import { HonuaOgcFeatureCollection } from "../src/core/surfaces.js";
import type {
  HonuaStyleSpecification,
  HonuaMapEvent,
} from "../src/index.js";

const client = new HonuaClient({ baseUrl: "https://gis.example.com" });

describe("HonuaMap — source management", () => {
  it("adds a feature-service source and resolves to HonuaFeatureLayer", () => {
    const map = new HonuaMap({ client });
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
    });

    expect(map.hasSource("parcels")).toBe(true);
    expect(map.sourceIds).toEqual(["parcels"]);

    const source = map.getSource("parcels");
    expect(source).toBeInstanceOf(HonuaFeatureLayer);
    expect((source as HonuaFeatureLayer).serviceId).toBe("parcels");
    expect((source as HonuaFeatureLayer).layerId).toBe(0);
  });

  it("adds a map-service source and resolves to HonuaMapService", () => {
    const map = new HonuaMap({ client });
    map.addSource("imagery", {
      type: "honua-map-service",
      url: "https://gis.example.com/rest/services/imagery/MapServer",
    });

    const source = map.getSource("imagery");
    expect(source).toBeInstanceOf(HonuaMapService);
    expect((source as HonuaMapService).serviceId).toBe("imagery");
  });

  it("adds an OGC features source and resolves to HonuaOgcFeatureCollection", () => {
    const map = new HonuaMap({ client });
    map.addSource("boundaries", {
      type: "honua-ogc-features",
      url: "https://gis.example.com/ogc/collections/admin-boundaries",
    });

    const source = map.getSource("boundaries");
    expect(source).toBeInstanceOf(HonuaOgcFeatureCollection);
    expect((source as HonuaOgcFeatureCollection).collectionId).toBe(
      "admin-boundaries",
    );
  });

  it("adds a native MapLibre source and resolves to null", () => {
    const map = new HonuaMap({ client });
    map.addSource("osm", {
      type: "vector",
      tiles: ["https://tiles.example.com/{z}/{x}/{y}.pbf"],
    } as any);

    expect(map.hasSource("osm")).toBe(true);
    expect(map.getSource("osm")).toBeNull();
  });

  it("throws when adding a duplicate source name", () => {
    const map = new HonuaMap({ client });
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
    });

    expect(() =>
      map.addSource("parcels", {
        type: "honua-feature-service",
        url: "https://gis.example.com/rest/services/other/FeatureServer/1",
      }),
    ).toThrow('Source "parcels" already exists');
  });

  it("removes a source and its dependent layers", () => {
    const map = new HonuaMap({ client });
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
    });
    map.addLayer({ id: "parcel-fill", type: "fill", source: "parcels" });
    map.addLayer({ id: "parcel-labels", type: "symbol", source: "parcels" });

    const removed = map.removeSource("parcels");
    expect(removed).toEqual(["parcel-fill", "parcel-labels"]);
    expect(map.hasSource("parcels")).toBe(false);
    expect(map.layerIds).toEqual([]);
  });

  it("returns undefined for non-existent source", () => {
    const map = new HonuaMap({ client });
    expect(map.getSource("nope")).toBeUndefined();
  });

  it("getSourceSpec returns the raw specification", () => {
    const map = new HonuaMap({ client });
    const spec = {
      type: "honua-feature-service" as const,
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
      definitionExpression: "status = 'active'",
    };
    map.addSource("parcels", spec);
    expect(map.getSourceSpec("parcels")).toEqual(spec);
  });
});

describe("HonuaMap — layer management", () => {
  it("adds layers referencing a source", () => {
    const map = new HonuaMap({ client });
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
    });

    map.addLayer({
      id: "parcel-fill",
      source: "parcels",
      type: "fill",
      paint: { "fill-color": "#088" },
    });
    map.addLayer({
      id: "parcel-labels",
      source: "parcels",
      type: "symbol",
      layout: { "text-field": ["get", "parcel_id"] },
    });

    expect(map.layerIds).toEqual(["parcel-fill", "parcel-labels"]);
    expect(map.layerCount).toBe(2);
  });

  it("throws when referencing a non-existent source", () => {
    const map = new HonuaMap({ client });
    expect(() =>
      map.addLayer({ id: "bad", source: "missing", type: "fill" }),
    ).toThrow('source "missing" does not exist');
  });

  it("throws when adding a duplicate layer ID", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.addLayer({ id: "l", source: "s", type: "fill" });
    expect(() => map.addLayer({ id: "l", source: "s", type: "line" })).toThrow(
      'Layer "l" already exists',
    );
  });

  it("inserts a layer before another", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.addLayer({ id: "bottom", source: "s", type: "fill" });
    map.addLayer({ id: "top", source: "s", type: "symbol" });
    map.addLayer({ id: "middle", source: "s", type: "line" }, "top");

    expect(map.layerIds).toEqual(["bottom", "middle", "top"]);
  });

  it("throws when inserting before a non-existent layer", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    expect(() =>
      map.addLayer({ id: "l", source: "s", type: "fill" }, "ghost"),
    ).toThrow('Cannot insert before "ghost"');
  });

  it("removes a layer by ID", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.addLayer({ id: "l", source: "s", type: "fill" });

    expect(map.removeLayer("l")).toBe(true);
    expect(map.layerIds).toEqual([]);
    expect(map.removeLayer("l")).toBe(false);
  });

  it("getLayer returns the spec", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    const spec = { id: "l", source: "s", type: "fill" as const, paint: { "fill-color": "#f00" } };
    map.addLayer(spec);
    expect(map.getLayer("l")).toEqual(spec);
  });

  it("moveLayer reorders layers", () => {
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.addLayer({ id: "a", source: "s", type: "fill" });
    map.addLayer({ id: "b", source: "s", type: "line" });
    map.addLayer({ id: "c", source: "s", type: "symbol" });

    map.moveLayer("c", "a"); // c before a
    expect(map.layerIds).toEqual(["c", "a", "b"]);

    map.moveLayer("a"); // a to end
    expect(map.layerIds).toEqual(["c", "b", "a"]);
  });

  it("getLayersForSource filters by source ID", () => {
    const map = new HonuaMap({ client });
    map.addSource("s1", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s1/FeatureServer/0",
    });
    map.addSource("s2", {
      type: "honua-map-service",
      url: "https://gis.example.com/rest/services/s2/MapServer",
    });
    map.addLayer({ id: "l1", source: "s1", type: "fill" });
    map.addLayer({ id: "l2", source: "s2", type: "raster" });
    map.addLayer({ id: "l3", source: "s1", type: "symbol" });

    const s1Layers = map.getLayersForSource("s1");
    expect(s1Layers).toHaveLength(2);
    expect(s1Layers.map((l) => l.id)).toEqual(["l1", "l3"]);
  });
});

describe("HonuaMap — style initialization", () => {
  it("loads sources and layers from an initial style", () => {
    const style: HonuaStyleSpecification = {
      version: 8,
      sources: {
        parcels: {
          type: "honua-feature-service",
          url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
        },
        imagery: {
          type: "honua-map-service",
          url: "https://gis.example.com/rest/services/imagery/MapServer",
        },
      },
      layers: [
        { id: "parcel-fill", source: "parcels", type: "fill" },
        { id: "imagery-layer", source: "imagery", type: "raster" },
      ],
    };

    const map = new HonuaMap({ client, style });

    expect(map.sourceCount).toBe(2);
    expect(map.layerCount).toBe(2);
    expect(map.getSource("parcels")).toBeInstanceOf(HonuaFeatureLayer);
    expect(map.getSource("imagery")).toBeInstanceOf(HonuaMapService);
    expect(map.layerIds).toEqual(["parcel-fill", "imagery-layer"]);
  });
});

describe("HonuaMap — toStyle", () => {
  it("produces a valid HonuaStyleSpecification", () => {
    const map = new HonuaMap({ client });
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
      definitionExpression: "status = 'active'",
    });
    map.addLayer({
      id: "parcel-fill",
      source: "parcels",
      type: "fill",
      paint: { "fill-color": "#088" },
    });

    const style = map.toStyle({ name: "Test Style" });
    expect(style.version).toBe(8);
    expect(style.name).toBe("Test Style");
    expect(style.sources.parcels).toEqual({
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
      definitionExpression: "status = 'active'",
    });
    expect(style.layers).toHaveLength(1);
    expect(style.layers[0].id).toBe("parcel-fill");
  });

  it("round-trips through constructor", () => {
    const original: HonuaStyleSpecification = {
      version: 8,
      sources: {
        parcels: {
          type: "honua-feature-service",
          url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
        },
      },
      layers: [{ id: "parcel-fill", source: "parcels", type: "fill" }],
    };

    const map = new HonuaMap({ client, style: original });
    const exported = map.toStyle();

    expect(exported.version).toBe(8);
    expect(exported.sources.parcels.type).toBe("honua-feature-service");
    expect(exported.layers[0].id).toBe("parcel-fill");
  });
});

describe("HonuaMap — events", () => {
  it("emits source-added and source-removed", () => {
    const events: HonuaMapEvent[] = [];
    const map = new HonuaMap({ client });
    map.on((e) => events.push(e));

    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.removeSource("s");

    expect(events).toEqual([
      { type: "source-added", sourceId: "s" },
      { type: "source-removed", sourceId: "s" },
    ]);
  });

  it("emits layer-added and layer-removed", () => {
    const events: HonuaMapEvent[] = [];
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.on((e) => events.push(e));

    map.addLayer({ id: "l", source: "s", type: "fill" });
    map.removeLayer("l");

    expect(events).toEqual([
      { type: "layer-added", layerId: "l" },
      { type: "layer-removed", layerId: "l" },
    ]);
  });

  it("emits layer-moved", () => {
    const events: HonuaMapEvent[] = [];
    const map = new HonuaMap({ client });
    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    map.addLayer({ id: "a", source: "s", type: "fill" });
    map.addLayer({ id: "b", source: "s", type: "line" });
    map.on((e) => events.push(e));

    map.moveLayer("b", "a");

    expect(events).toEqual([
      { type: "layer-moved", layerId: "b", beforeId: "a" },
    ]);
  });

  it("on() returns a removable handle", () => {
    const events: HonuaMapEvent[] = [];
    const map = new HonuaMap({ client });
    const handle = map.on((e) => events.push(e));

    map.addSource("s", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s/FeatureServer/0",
    });
    handle.remove();
    map.addSource("s2", {
      type: "honua-map-service",
      url: "https://gis.example.com/rest/services/s2/MapServer",
    });

    expect(events).toHaveLength(1);
  });
});

describe("HonuaMap — clear", () => {
  it("removes all sources and layers", () => {
    const map = new HonuaMap({ client });
    map.addSource("s1", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/s1/FeatureServer/0",
    });
    map.addSource("s2", {
      type: "honua-map-service",
      url: "https://gis.example.com/rest/services/s2/MapServer",
    });
    map.addLayer({ id: "l1", source: "s1", type: "fill" });
    map.addLayer({ id: "l2", source: "s2", type: "raster" });

    map.clear();
    expect(map.sourceCount).toBe(0);
    expect(map.layerCount).toBe(0);
  });
});

describe("HonuaMap — design doc example", () => {
  it("implements the source/layer separation sketch from Direction 2", () => {
    const map = new HonuaMap({ client });

    // Data source — knows how to fetch, does not know how to render
    map.addSource("parcels", {
      type: "honua-feature-service",
      url: "https://gis.example.com/rest/services/parcels/FeatureServer/0",
      definitionExpression: "status = 'active'",
    });

    // Multiple rendering passes over the same source
    map.addLayer({
      id: "parcel-fill",
      source: "parcels",
      type: "fill",
      paint: { "fill-color": "#088", "fill-opacity": 0.5 },
    });

    map.addLayer({
      id: "parcel-labels",
      source: "parcels",
      type: "symbol",
      layout: { "text-field": ["get", "parcel_id"] },
    });

    // Both layers share the same underlying source
    const source = map.getSource("parcels");
    expect(source).toBeInstanceOf(HonuaFeatureLayer);
    expect(map.getLayersForSource("parcels")).toHaveLength(2);

    // The style is serializable
    const style = map.toStyle({ name: "Parcel Analysis" });
    expect(style.version).toBe(8);
    expect(style.layers).toHaveLength(2);
    expect(style.sources.parcels.type).toBe("honua-feature-service");
  });
});
