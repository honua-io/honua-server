import { describe, expect, it } from "vitest";

import { HonuaClient, MapLayerQueryBuilder, OgcQueryBuilder } from "../src/index.js";

describe("MapLayerQueryBuilder fluent DSL", () => {
  describe("standalone builder (MapLayerQueryBuilder.from)", () => {
    it("builds minimal request with only serviceId and layerId", () => {
      const req = MapLayerQueryBuilder.from("myService", 0).build();
      expect(req).toEqual({ serviceId: "myService", layerId: 0 });
    });

    it("supports full chaining", () => {
      const req = MapLayerQueryBuilder.from("cities", 0)
        .where("STATE = 'CA'")
        .outFields("NAME", "POP")
        .geometry({ x: -118, y: 34 })
        .geometryType("esriGeometryPoint")
        .spatialRel("esriSpatialRelWithin")
        .returnGeometry(true)
        .orderBy("POP DESC")
        .limit(10)
        .offset(0)
        .objectIds([1, 2, 3])
        .distinct()
        .method("POST")
        .extraParams({ token: "abc" })
        .build();

      expect(req).toEqual({
        serviceId: "cities",
        layerId: 0,
        where: "STATE = 'CA'",
        outFields: ["NAME", "POP"],
        geometry: { x: -118, y: 34 },
        geometryType: "esriGeometryPoint",
        spatialRel: "esriSpatialRelWithin",
        returnGeometry: true,
        orderByFields: "POP DESC",
        resultRecordCount: 10,
        resultOffset: 0,
        objectIds: [1, 2, 3],
        returnDistinctValues: true,
        method: "POST",
        extraParams: { token: "abc" },
      });
    });
  });

  describe("bound builder (MapLayerQueryBuilder.for)", () => {
    it("runs query via bound client", async () => {
      const client = new HonuaClient({
        baseUrl: "https://example.test",
        fetchFn: async () => new Response(JSON.stringify({ features: [{ attributes: { id: 1 } }] })),
      });

      const result = await MapLayerQueryBuilder.for(client, "svc", 0).where("1=1").limit(1).run();

      expect(result.features).toHaveLength(1);
      expect(result.features![0].attributes).toEqual({ id: 1 });
    });

    it("throws when run() is called on standalone builder", async () => {
      const builder = MapLayerQueryBuilder.from("svc", 0);
      await expect(builder.run()).rejects.toThrow(/bound client/);
    });
  });

  describe("statistics and centroid methods", () => {
    it("supports returnCentroid on MapLayerQueryBuilder", () => {
      const req = MapLayerQueryBuilder.from("svc", 0).returnCentroid().build();
      expect(req.returnCentroid).toBe(true);
    });

    it("supports groupBy on MapLayerQueryBuilder", () => {
      const req = MapLayerQueryBuilder.from("svc", 0).groupBy("category").build();
      expect(req.groupByFieldsForStatistics).toBe("category");
    });

    it("supports outStatistics on MapLayerQueryBuilder", () => {
      const stats = [{ statisticType: "count", onStatisticField: "OBJECTID", outStatisticFieldName: "cnt" }];
      const req = MapLayerQueryBuilder.from("svc", 0).outStatistics(stats).build();
      expect(req.outStatistics).toEqual(stats);
    });
  });
});

describe("OgcQueryBuilder fluent DSL", () => {
  describe("standalone builder (OgcQueryBuilder.from)", () => {
    it("builds minimal request with only collectionId", () => {
      const req = OgcQueryBuilder.from("rivers").build();
      expect(req).toEqual({ collectionId: "rivers" });
    });

    it("supports full chaining with all options", () => {
      const controller = new AbortController();
      const req = OgcQueryBuilder.from("rivers")
        .limit(50)
        .offset(10)
        .bbox("-180,-90,180,90")
        .datetime("2020-01-01T00:00:00Z/..")
        .filter("name LIKE 'Miss%'")
        .ids("river-1", "river-2")
        .properties("name", "length")
        .sortby("+name")
        .crs("http://www.opengis.net/def/crs/OGC/1.3/CRS84")
        .signal(controller.signal)
        .responseFormat("geojson")
        .build();

      expect(req).toEqual({
        collectionId: "rivers",
        limit: 50,
        offset: 10,
        bbox: "-180,-90,180,90",
        datetime: "2020-01-01T00:00:00Z/..",
        filter: "name LIKE 'Miss%'",
        ids: ["river-1", "river-2"],
        properties: ["name", "length"],
        sortby: "+name",
        crs: "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
        signal: controller.signal,
        responseFormat: "geojson",
      });
    });
  });

  describe("bound builder (OgcQueryBuilder.for)", () => {
    it("runs query via bound client", async () => {
      const featureCollection = {
        type: "FeatureCollection",
        features: [{ type: "Feature", id: "1", geometry: null, properties: { name: "Mississippi" } }],
        numberReturned: 1,
      };

      const client = new HonuaClient({
        baseUrl: "https://example.test",
        fetchFn: async () => new Response(JSON.stringify(featureCollection)),
      });

      const result = await OgcQueryBuilder.for(client, "rivers").limit(1).run();

      expect(result.features).toHaveLength(1);
      expect(result.features[0].properties).toEqual({ name: "Mississippi" });
    });

    it("throws when run() is called on standalone builder", async () => {
      const builder = OgcQueryBuilder.from("rivers");
      await expect(builder.run()).rejects.toThrow(/bound client/);
    });
  });
});
