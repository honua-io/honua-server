import { describe, expect, it } from "vitest";

import type {
  EsriEnvelope,
  EsriFieldType,
  EsriGeometry,
  EsriGeometryType,
  EsriMultipoint,
  EsriPoint,
  EsriPolygon,
  EsriPolyline,
  EsriSpatialRel,
  GeoJsonFeature,
  HonuaFeature,
  HonuaFieldInfo,
  HonuaLayerMetadata,
  HonuaQueryResponse,
  HonuaServicesResponse,
  OgcCreateItemRequest,
  OgcPatchItemRequest,
  OgcReplaceItemRequest,
  QueryFeaturesRequest,
} from "../src/index.js";

describe("Geometry & enum type narrowing (Direction 12)", () => {
  describe("EsriGeometryType literal union", () => {
    it("accepts known geometry type literals", () => {
      const types: EsriGeometryType[] = [
        "esriGeometryPoint",
        "esriGeometryPolyline",
        "esriGeometryPolygon",
        "esriGeometryEnvelope",
        "esriGeometryMultipoint",
      ];
      expect(types).toHaveLength(5);
    });

    it("accepts arbitrary strings via open-ended union", () => {
      const custom: EsriGeometryType = "esriGeometryCustom";
      expect(custom).toBe("esriGeometryCustom");
    });
  });

  describe("EsriSpatialRel literal union", () => {
    it("accepts known spatial relationship literals", () => {
      const rels: EsriSpatialRel[] = [
        "esriSpatialRelIntersects",
        "esriSpatialRelContains",
        "esriSpatialRelCrosses",
        "esriSpatialRelWithin",
      ];
      expect(rels).toHaveLength(4);
    });
  });

  describe("EsriFieldType literal union", () => {
    it("accepts known field type literals", () => {
      const fieldType: EsriFieldType = "esriFieldTypeOID";
      expect(fieldType).toBe("esriFieldTypeOID");
    });
  });

  describe("Esri geometry interfaces", () => {
    it("EsriPoint shape is assignable", () => {
      const point: EsriPoint = { x: -117.2, y: 32.7 };
      expect(point.x).toBe(-117.2);
    });

    it("EsriPoint with optional z/m", () => {
      const point: EsriPoint = { x: 0, y: 0, z: 100, m: 1.5 };
      expect(point.z).toBe(100);
      expect(point.m).toBe(1.5);
    });

    it("EsriPolyline shape is assignable", () => {
      const polyline: EsriPolyline = {
        paths: [
          [
            [0, 0],
            [1, 1],
            [2, 2],
          ],
        ],
      };
      expect(polyline.paths).toHaveLength(1);
    });

    it("EsriPolygon shape is assignable", () => {
      const polygon: EsriPolygon = {
        rings: [
          [
            [0, 0],
            [1, 0],
            [1, 1],
            [0, 0],
          ],
        ],
      };
      expect(polygon.rings).toHaveLength(1);
    });

    it("EsriEnvelope shape is assignable", () => {
      const envelope: EsriEnvelope = {
        xmin: -180,
        ymin: -90,
        xmax: 180,
        ymax: 90,
      };
      expect(envelope.xmin).toBe(-180);
    });

    it("EsriMultipoint shape is assignable", () => {
      const mp: EsriMultipoint = {
        points: [
          [0, 0],
          [1, 1],
        ],
      };
      expect(mp.points).toHaveLength(2);
    });

    it("EsriGeometry union accepts all shapes", () => {
      const geoms: EsriGeometry[] = [
        { x: 0, y: 0 },
        {
          paths: [
            [
              [0, 0],
              [1, 1],
            ],
          ],
        },
        {
          rings: [
            [
              [0, 0],
              [1, 0],
              [0, 0],
            ],
          ],
        },
        { xmin: 0, ymin: 0, xmax: 1, ymax: 1 },
        { points: [[0, 0]] },
      ];
      expect(geoms).toHaveLength(5);
    });
  });

  describe("GeoJsonFeature", () => {
    it("accepts a well-formed GeoJSON feature", () => {
      const feature: GeoJsonFeature = {
        type: "Feature",
        id: "abc",
        geometry: { type: "Point", coordinates: [0, 0] },
        properties: { name: "test" },
      };
      expect(feature.type).toBe("Feature");
    });

    it("accepts null geometry", () => {
      const feature: GeoJsonFeature = {
        type: "Feature",
        geometry: null,
        properties: null,
      };
      expect(feature.geometry).toBeNull();
    });
  });

  describe("Narrowed field types in existing interfaces", () => {
    it("QueryFeaturesRequest accepts narrowed geometryType", () => {
      const req: Pick<QueryFeaturesRequest, "geometryType" | "spatialRel"> = {
        geometryType: "esriGeometryPoint",
        spatialRel: "esriSpatialRelIntersects",
      };
      expect(req.geometryType).toBe("esriGeometryPoint");
    });

    it("HonuaFieldInfo.type accepts narrowed field type", () => {
      const field: HonuaFieldInfo = {
        name: "OBJECTID",
        type: "esriFieldTypeOID",
      };
      expect(field.type).toBe("esriFieldTypeOID");
    });

    it("HonuaQueryResponse.geometryType is narrowed", () => {
      const resp: HonuaQueryResponse = {
        geometryType: "esriGeometryPolygon",
        features: [],
      };
      expect(resp.geometryType).toBe("esriGeometryPolygon");
    });

    it("HonuaLayerMetadata.geometryType is narrowed", () => {
      const meta: HonuaLayerMetadata = {
        id: 0,
        name: "Layer0",
        geometryType: "esriGeometryPolyline",
      };
      expect(meta.geometryType).toBe("esriGeometryPolyline");
    });

    it("HonuaFeature.geometry accepts EsriGeometry", () => {
      const feat: HonuaFeature = {
        attributes: { id: 1 },
        geometry: { x: 0, y: 0 },
      };
      expect(feat.geometry).toEqual({ x: 0, y: 0 });
    });

    it("OgcCreateItemRequest.feature accepts GeoJsonFeature", () => {
      const req: OgcCreateItemRequest = {
        collectionId: "test",
        feature: {
          type: "Feature",
          geometry: { type: "Point", coordinates: [0, 0] },
          properties: {},
        } satisfies GeoJsonFeature,
      };
      expect(req.feature).toBeDefined();
    });

    it("OgcPatchItemRequest.patch is Record<string, unknown>", () => {
      const req: OgcPatchItemRequest = {
        collectionId: "test",
        featureId: "1",
        patch: { properties: { name: "updated" } },
      };
      expect(req.patch).toBeDefined();
    });
  });

  describe("HonuaServicesResponse", () => {
    it("has correct shape", () => {
      const resp: HonuaServicesResponse = {
        currentVersion: 11.2,
        folders: ["Utilities"],
        services: [{ name: "SampleData", type: "FeatureServer" }],
      };
      expect(resp.services).toHaveLength(1);
    });
  });
});
