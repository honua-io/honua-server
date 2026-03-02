import { describe, expect, it } from "vitest";

import { HonuaClient, QueryBuilder } from "../src/index.js";

describe("QueryBuilder fluent DSL (Direction 18)", () => {
  describe("standalone builder (QueryBuilder.from)", () => {
    it("builds minimal request with only serviceId and layerId", () => {
      const req = QueryBuilder.from("myService", 0).build();
      expect(req).toEqual({ serviceId: "myService", layerId: 0 });
    });

    it("builds request with where clause", () => {
      const req = QueryBuilder.from("svc", 1).where("POP > 1000").build();
      expect(req.where).toBe("POP > 1000");
    });

    it("builds request with outFields", () => {
      const req = QueryBuilder.from("svc", 0).outFields("NAME", "POP", "STATE").build();
      expect(req.outFields).toEqual(["NAME", "POP", "STATE"]);
    });

    it("builds request with geometry filter", () => {
      const req = QueryBuilder.from("svc", 0)
        .geometry({ xmin: -180, ymin: -90, xmax: 180, ymax: 90 })
        .geometryType("esriGeometryEnvelope")
        .spatialRel("esriSpatialRelIntersects")
        .build();

      expect(req.geometry).toEqual({ xmin: -180, ymin: -90, xmax: 180, ymax: 90 });
      expect(req.geometryType).toBe("esriGeometryEnvelope");
      expect(req.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("builds request with pagination", () => {
      const req = QueryBuilder.from("svc", 0).limit(50).offset(100).build();
      expect(req.resultRecordCount).toBe(50);
      expect(req.resultOffset).toBe(100);
    });

    it("builds request with ordering", () => {
      const req = QueryBuilder.from("svc", 0).orderBy("NAME ASC").build();
      expect(req.orderByFields).toBe("NAME ASC");
    });

    it("builds request with returnGeometry false", () => {
      const req = QueryBuilder.from("svc", 0).returnGeometry(false).build();
      expect(req.returnGeometry).toBe(false);
    });

    it("builds request with objectIds", () => {
      const req = QueryBuilder.from("svc", 0).objectIds([1, 2, 3]).build();
      expect(req.objectIds).toEqual([1, 2, 3]);
    });

    it("builds request with distinct", () => {
      const req = QueryBuilder.from("svc", 0).distinct().build();
      expect(req.returnDistinctValues).toBe(true);
    });

    it("builds request with returnCentroid", () => {
      const req = QueryBuilder.from("svc", 0).returnCentroid().build();
      expect(req.returnCentroid).toBe(true);
    });

    it("builds request with statistics", () => {
      const stats = [{ statisticType: "count", onStatisticField: "OBJECTID", outStatisticFieldName: "total" }];
      const req = QueryBuilder.from("svc", 0).groupBy("STATE").outStatistics(stats).build();
      expect(req.groupByFieldsForStatistics).toBe("STATE");
      expect(req.outStatistics).toEqual(stats);
    });

    it("builds request with method override", () => {
      const req = QueryBuilder.from("svc", 0).method("POST").build();
      expect(req.method).toBe("POST");
    });

    it("builds request with AbortSignal", () => {
      const controller = new AbortController();
      const req = QueryBuilder.from("svc", 0).signal(controller.signal).build();
      expect(req.signal).toBe(controller.signal);
    });

    it("builds request with extraParams", () => {
      const req = QueryBuilder.from("svc", 0).extraParams({ token: "abc", returnZ: true }).build();
      expect(req.extraParams).toEqual({ token: "abc", returnZ: true });
    });

    it("supports full chaining", () => {
      const req = QueryBuilder.from("cities", 0)
        .where("STATE = 'CA'")
        .outFields("NAME", "POP")
        .geometry({ x: -118, y: 34 })
        .geometryType("esriGeometryPoint")
        .spatialRel("esriSpatialRelWithin")
        .returnGeometry(true)
        .orderBy("POP DESC")
        .limit(10)
        .offset(0)
        .method("POST")
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
        method: "POST",
      });
    });
  });

  describe("bound builder (QueryBuilder.for)", () => {
    it("runs query via bound client", async () => {
      const client = new HonuaClient({
        baseUrl: "https://example.test",
        fetchFn: async () => new Response(JSON.stringify({ features: [{ attributes: { id: 1 } }] })),
      });

      const result = await QueryBuilder.for(client, "svc", 0).where("1=1").limit(1).run();

      expect(result.features).toHaveLength(1);
      expect(result.features![0].attributes).toEqual({ id: 1 });
    });

    it("throws when run() is called on standalone builder", async () => {
      const builder = QueryBuilder.from("svc", 0);
      await expect(builder.run()).rejects.toThrow(/bound client/);
    });
  });
});
