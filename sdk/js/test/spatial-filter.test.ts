import { describe, expect, it } from "vitest";

import {
  buffer,
  QueryBuilder,
  envelope,
  point,
  polygon,
  spatialContains,
  spatialIntersects,
  spatialWithin,
} from "../src/index.js";
import type { QueryFeaturesRequest, SpatialFilter } from "../src/index.js";

describe("SpatialFilter builders", () => {
  describe("envelope", () => {
    it("produces correct geometry JSON", () => {
      const f = envelope(-118.5, 33.7, -117.5, 34.2);
      expect(f.geometry).toEqual({ xmin: -118.5, ymin: 33.7, xmax: -117.5, ymax: 34.2 });
      expect(f.geometryType).toBe("esriGeometryEnvelope");
      expect(f.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("includes spatialReference when provided", () => {
      const f = envelope(0, 0, 100, 100, { wkid: 4326 });
      expect(f.geometry).toEqual({
        xmin: 0,
        ymin: 0,
        xmax: 100,
        ymax: 100,
        spatialReference: { wkid: 4326 },
      });
    });

    it("omits spatialReference when not provided", () => {
      const f = envelope(0, 0, 1, 1);
      expect(f.geometry).not.toHaveProperty("spatialReference");
    });
  });

  describe("point", () => {
    it("produces correct geometry JSON", () => {
      const f = point(-118.24, 34.05);
      expect(f.geometry).toEqual({ x: -118.24, y: 34.05 });
      expect(f.geometryType).toBe("esriGeometryPoint");
      expect(f.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("includes spatialReference when provided", () => {
      const f = point(10, 20, { wkid: 3857 });
      expect(f.geometry).toEqual({ x: 10, y: 20, spatialReference: { wkid: 3857 } });
    });

    it("omits spatialReference when not provided", () => {
      const f = point(10, 20);
      expect(f.geometry).not.toHaveProperty("spatialReference");
    });
  });

  describe("polygon", () => {
    it("produces correct geometry JSON", () => {
      const rings = [
        [
          [-118, 34],
          [-117, 34],
          [-117, 35],
          [-118, 35],
          [-118, 34],
        ],
      ];
      const f = polygon(rings);
      expect(f.geometry).toEqual({ rings });
      expect(f.geometryType).toBe("esriGeometryPolygon");
      expect(f.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("includes spatialReference when provided", () => {
      const rings = [
        [
          [0, 0],
          [1, 0],
          [1, 1],
          [0, 0],
        ],
      ];
      const f = polygon(rings, { wkid: 4326 });
      expect(f.geometry).toEqual({ rings, spatialReference: { wkid: 4326 } });
    });

    it("omits spatialReference when not provided", () => {
      const rings = [
        [
          [0, 0],
          [1, 0],
          [1, 1],
          [0, 0],
        ],
      ];
      const f = polygon(rings);
      expect(f.geometry).not.toHaveProperty("spatialReference");
    });
  });

  describe("buffer", () => {
    it("creates correct envelope bounds centered on the point", () => {
      const f = buffer(10, 20, 5);
      expect(f.geometry).toEqual({ xmin: 5, ymin: 15, xmax: 15, ymax: 25 });
      expect(f.geometryType).toBe("esriGeometryEnvelope");
      expect(f.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("handles fractional coordinates and distance", () => {
      const f = buffer(-118.24, 34.05, 0.5);
      expect(f.geometry).toEqual({
        xmin: -118.74,
        ymin: 33.55,
        xmax: -117.74,
        ymax: 34.55,
      });
    });

    it("handles zero distance (degenerates to a point-sized envelope)", () => {
      const f = buffer(5, 10, 0);
      expect(f.geometry).toEqual({ xmin: 5, ymin: 10, xmax: 5, ymax: 10 });
    });

    it("passes through spatialReference", () => {
      const f = buffer(0, 0, 1, { wkid: 3857 });
      expect(f.geometry).toEqual({
        xmin: -1,
        ymin: -1,
        xmax: 1,
        ymax: 1,
        spatialReference: { wkid: 3857 },
      });
    });
  });

  describe("spatialIntersects", () => {
    it("sets spatialRel to esriSpatialRelIntersects", () => {
      const f = spatialIntersects({ xmin: 0, ymin: 0, xmax: 1, ymax: 1 });
      expect(f.spatialRel).toBe("esriSpatialRelIntersects");
    });

    it("detects envelope geometry type", () => {
      const f = spatialIntersects({ xmin: 0, ymin: 0, xmax: 1, ymax: 1 });
      expect(f.geometryType).toBe("esriGeometryEnvelope");
    });

    it("detects point geometry type", () => {
      const f = spatialIntersects({ x: 10, y: 20 });
      expect(f.geometryType).toBe("esriGeometryPoint");
    });

    it("detects polygon geometry type", () => {
      const f = spatialIntersects({
        rings: [
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ],
      });
      expect(f.geometryType).toBe("esriGeometryPolygon");
    });

    it("detects polyline geometry type", () => {
      const f = spatialIntersects({
        paths: [
          [
            [0, 0],
            [1, 1],
          ],
        ],
      });
      expect(f.geometryType).toBe("esriGeometryPolyline");
    });

    it("detects multipoint geometry type", () => {
      const f = spatialIntersects({
        points: [
          [0, 0],
          [1, 1],
        ],
      });
      expect(f.geometryType).toBe("esriGeometryMultipoint");
    });

    it("preserves the original geometry object", () => {
      const geom = { xmin: -1, ymin: -1, xmax: 1, ymax: 1 };
      const f = spatialIntersects(geom);
      expect(f.geometry).toBe(geom);
    });
  });

  describe("spatialContains", () => {
    it("sets spatialRel to esriSpatialRelContains", () => {
      const f = spatialContains({
        rings: [
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ],
      });
      expect(f.spatialRel).toBe("esriSpatialRelContains");
    });

    it("detects geometry type from shape", () => {
      const f = spatialContains({
        rings: [
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ],
      });
      expect(f.geometryType).toBe("esriGeometryPolygon");
    });
  });

  describe("spatialWithin", () => {
    it("sets spatialRel to esriSpatialRelWithin", () => {
      const f = spatialWithin({ xmin: 0, ymin: 0, xmax: 10, ymax: 10 });
      expect(f.spatialRel).toBe("esriSpatialRelWithin");
    });

    it("detects geometry type from shape", () => {
      const f = spatialWithin({ xmin: 0, ymin: 0, xmax: 10, ymax: 10 });
      expect(f.geometryType).toBe("esriGeometryEnvelope");
    });
  });

  describe("spread into QueryFeaturesRequest", () => {
    it("spreads envelope filter into a request", () => {
      const req: QueryFeaturesRequest = {
        serviceId: "cities",
        layerId: 0,
        where: "POP > 1000",
        ...envelope(-118.5, 33.7, -117.5, 34.2),
      };
      expect(req.geometry).toEqual({ xmin: -118.5, ymin: 33.7, xmax: -117.5, ymax: 34.2 });
      expect(req.geometryType).toBe("esriGeometryEnvelope");
      expect(req.spatialRel).toBe("esriSpatialRelIntersects");
      expect(req.serviceId).toBe("cities");
      expect(req.where).toBe("POP > 1000");
    });

    it("spreads point filter into a request", () => {
      const req: QueryFeaturesRequest = {
        serviceId: "svc",
        layerId: 1,
        ...point(10, 20),
      };
      expect(req.geometry).toEqual({ x: 10, y: 20 });
      expect(req.geometryType).toBe("esriGeometryPoint");
    });

    it("spreads buffer filter into a request", () => {
      const req: QueryFeaturesRequest = {
        serviceId: "svc",
        layerId: 0,
        ...buffer(0, 0, 5),
      };
      expect(req.geometry).toEqual({ xmin: -5, ymin: -5, xmax: 5, ymax: 5 });
      expect(req.geometryType).toBe("esriGeometryEnvelope");
    });

    it("spreads spatial relationship wrapper into a request", () => {
      const req: QueryFeaturesRequest = {
        serviceId: "svc",
        layerId: 0,
        ...spatialContains({
          rings: [
            [
              [0, 0],
              [10, 0],
              [10, 10],
              [0, 10],
              [0, 0],
            ],
          ],
        }),
      };
      expect(req.spatialRel).toBe("esriSpatialRelContains");
      expect(req.geometryType).toBe("esriGeometryPolygon");
    });
  });

  describe("integration with QueryBuilder", () => {
    it("works with QueryBuilder.from().geometry() and related methods", () => {
      const f = envelope(-118.5, 33.7, -117.5, 34.2);
      const req = QueryBuilder.from("cities", 0)
        .where("POP > 1000")
        .geometry(f.geometry)
        .geometryType(f.geometryType)
        .spatialRel(f.spatialRel!)
        .build();

      expect(req.geometry).toEqual({ xmin: -118.5, ymin: 33.7, xmax: -117.5, ymax: 34.2 });
      expect(req.geometryType).toBe("esriGeometryEnvelope");
      expect(req.spatialRel).toBe("esriSpatialRelIntersects");
      expect(req.where).toBe("POP > 1000");
    });

    it("works with point filter and QueryBuilder", () => {
      const f = point(-118.24, 34.05, { wkid: 4326 });
      const req = QueryBuilder.from("svc", 0)
        .geometry(f.geometry)
        .geometryType(f.geometryType)
        .spatialRel(f.spatialRel!)
        .build();

      expect(req.geometry).toEqual({ x: -118.24, y: 34.05, spatialReference: { wkid: 4326 } });
      expect(req.geometryType).toBe("esriGeometryPoint");
    });

    it("works with spatialWithin wrapper and QueryBuilder", () => {
      const f = spatialWithin({ xmin: 0, ymin: 0, xmax: 100, ymax: 100 });
      const req = QueryBuilder.from("svc", 0)
        .geometry(f.geometry)
        .geometryType(f.geometryType)
        .spatialRel(f.spatialRel!)
        .build();

      expect(req.spatialRel).toBe("esriSpatialRelWithin");
      expect(req.geometryType).toBe("esriGeometryEnvelope");
    });
  });

  describe("SpatialFilter type compatibility", () => {
    it("conforms to the SpatialFilter interface", () => {
      // This is a compile-time check; if it compiles, the type is correct.
      const f: SpatialFilter = envelope(0, 0, 1, 1);
      expect(f.geometry).toBeDefined();
      expect(f.geometryType).toBeDefined();
    });

    it("all builders return SpatialFilter-compatible objects", () => {
      const filters: SpatialFilter[] = [
        envelope(0, 0, 1, 1),
        point(0, 0),
        polygon([
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ]),
        buffer(0, 0, 1),
        spatialIntersects({ x: 0, y: 0 }),
        spatialContains({
          rings: [
            [
              [0, 0],
              [1, 0],
              [1, 1],
              [0, 0],
            ],
          ],
        }),
        spatialWithin({ xmin: 0, ymin: 0, xmax: 1, ymax: 1 }),
      ];
      expect(filters).toHaveLength(7);
      for (const f of filters) {
        expect(f.geometry).toBeDefined();
        expect(f.geometryType).toBeDefined();
      }
    });
  });
});
