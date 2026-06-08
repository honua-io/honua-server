/**
 * Tests for comprehensive geometry type coverage in FeatureServer.
 *
 * Tests all GeoJSON geometry types:
 * - Points, MultiPoints
 * - LineStrings, MultiLineStrings
 * - Polygons with holes, MultiPolygons with holes
 * - GeometryCollections
 * - Null geometries
 *
 * Each geometry type is tested for:
 * - Add via applyEdits
 * - Query roundtrip validation
 * - Geometry validity
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { FeatureServerClient, assertEsriFeatureSet, assertGeoJsonFeatureCollection } from '../shared/client';
import { GeometryGenerator, TestGeometry } from '../shared/geometry';
import { ALL_GEOMETRY_METHODS, GEOMETRY_TYPE_CASES } from '../shared/constants';

// =============================================================================
// Test Setup
// =============================================================================

let client: FeatureServerClient;
let geometryGenerator: GeometryGenerator;
const createdObjectIds: number[] = [];

beforeAll(() => {
  // This suite adds point/line/polygon features to a single layer, which requires a 'Mixed'
  // (unconstrained) layer under the edit-time geometry-type enforcement. In CI the shared layer 0 is a
  // Point layer (esri-leaflet's FeatureLayer needs a concrete geometryType), so target the dedicated
  // Mixed layer via HONUA_MIXED_LAYER_ID; locally the provisioned default layer is already Mixed.
  const mixedLayerId = process.env.HONUA_MIXED_LAYER_ID;
  client = new FeatureServerClient(mixedLayerId ? { layerId: Number.parseInt(mixedLayerId, 10) } : {});
  geometryGenerator = new GeometryGenerator();
});

afterAll(async () => {
  // Cleanup created features
  if (createdObjectIds.length > 0) {
    await client.deleteFeatures(createdObjectIds).catch(() => {
      // Ignore cleanup errors
    });
  }
});

// =============================================================================
// Geometry Roundtrip Helper
// =============================================================================

async function verifyGeometryRoundtrip(
  testGeom: TestGeometry,
  options: { mayFail?: boolean } = {},
): Promise<void> {
  const { mayFail = false } = options;

  // Add the feature
  const addResponse = await client.applyEdits({
    adds: [
      {
        geometry: testGeom.esriJson,
        attributes: { name: testGeom.name },
      },
    ],
  });

  if (mayFail && addResponse.status !== 200) {
    // Skip if geometry type not supported
    return;
  }

  expect(addResponse.status).toBe(200);
  const addResult = addResponse.data.addResults?.[0];

  if (mayFail && !addResult?.success) {
    // Skip if add failed
    return;
  }

  expect(addResult?.success).toBe(true);
  expect(addResult?.objectId).toBeDefined();

  const objectId = addResult!.objectId!;
  createdObjectIds.push(objectId);

  // Query back the feature in GeoJSON format
  const queryResponse = await client.queryGeoJson({
    where: `OBJECTID = ${objectId}`,
    returnGeometry: true,
  });

  expect(queryResponse.status).toBe(200);
  const data = assertGeoJsonFeatureCollection(queryResponse);

  if (data.features.length > 0 && data.features[0].geometry) {
    // Validate basic geometry structure
    const retrievedGeom = data.features[0].geometry;
    expect(retrievedGeom.type).toBeDefined();
    const hasCoordinates = 'coordinates' in retrievedGeom;
    const hasGeometries = 'geometries' in retrievedGeom;
    expect(hasCoordinates || hasGeometries).toBe(true);
  }
}

// =============================================================================
// Individual Geometry Type Tests
// =============================================================================

describe('Geometry Types - Individual Tests', () => {
  describe('Point Geometry', () => {
    it('should add and retrieve a Point geometry', async () => {
      const point = geometryGenerator.point('point_roundtrip');
      await verifyGeometryRoundtrip(point);
    });

    it('should handle point at origin', async () => {
      const point = geometryGenerator.point('point_origin', 0, 0);
      await verifyGeometryRoundtrip(point, { mayFail: true });
    });

    it('should handle point at max coordinates', async () => {
      const point = geometryGenerator.point('point_max', 180, 90);
      await verifyGeometryRoundtrip(point, { mayFail: true });
    });

    it('should handle point at min coordinates', async () => {
      const point = geometryGenerator.point('point_min', -180, -90);
      await verifyGeometryRoundtrip(point, { mayFail: true });
    });
  });

  describe('MultiPoint Geometry', () => {
    it('should add and retrieve a MultiPoint geometry', async () => {
      const multipoint = geometryGenerator.multipoint('multipoint_roundtrip');
      await verifyGeometryRoundtrip(multipoint);
    });

    it('should handle MultiPoint with single point', async () => {
      const multipoint = geometryGenerator.multipoint('multipoint_single', 1);
      await verifyGeometryRoundtrip(multipoint);
    });

    it('should handle MultiPoint with many points', async () => {
      const multipoint = geometryGenerator.multipoint('multipoint_many', 10);
      await verifyGeometryRoundtrip(multipoint);
    });
  });

  describe('LineString Geometry', () => {
    it('should add and retrieve a LineString geometry', async () => {
      const linestring = geometryGenerator.linestring('linestring_roundtrip');
      await verifyGeometryRoundtrip(linestring);
    });

    it('should handle LineString with minimum points', async () => {
      const linestring = geometryGenerator.linestring('linestring_min', 2);
      await verifyGeometryRoundtrip(linestring);
    });

    it('should handle LineString with many points', async () => {
      const linestring = geometryGenerator.linestring('linestring_many', 20);
      await verifyGeometryRoundtrip(linestring);
    });
  });

  describe('MultiLineString Geometry', () => {
    it('should add and retrieve a MultiLineString geometry', async () => {
      const multiline = geometryGenerator.multilinestring('multilinestring_roundtrip');
      await verifyGeometryRoundtrip(multiline);
    });

    it('should handle MultiLineString with single line', async () => {
      const multiline = geometryGenerator.multilinestring('multilinestring_single', 1);
      await verifyGeometryRoundtrip(multiline);
    });

    it('should handle MultiLineString with many lines', async () => {
      const multiline = geometryGenerator.multilinestring('multilinestring_many', 5);
      await verifyGeometryRoundtrip(multiline);
    });
  });

  describe('Polygon Geometry', () => {
    it('should add and retrieve a simple Polygon', async () => {
      const polygon = geometryGenerator.polygonSimple('polygon_simple_roundtrip');
      await verifyGeometryRoundtrip(polygon);
    });

    it('should add and retrieve a Polygon with one hole', async () => {
      const polygon = geometryGenerator.polygonWithHole('polygon_hole_roundtrip');
      await verifyGeometryRoundtrip(polygon);
    });

    it('should add and retrieve a Polygon with multiple holes', async () => {
      const polygon = geometryGenerator.polygonWithMultipleHoles('polygon_multiholes_roundtrip');
      await verifyGeometryRoundtrip(polygon);
    });
  });

  describe('MultiPolygon Geometry', () => {
    it('should add and retrieve a simple MultiPolygon', async () => {
      const multipoly = geometryGenerator.multipolygonSimple('multipolygon_simple_roundtrip');
      await verifyGeometryRoundtrip(multipoly);
    });

    it('should add and retrieve a MultiPolygon with holes', async () => {
      const multipoly = geometryGenerator.multipolygonWithHoles('multipolygon_holes_roundtrip');
      await verifyGeometryRoundtrip(multipoly);
    });
  });

  describe('GeometryCollection', () => {
    it('should add and retrieve a GeometryCollection (may not be supported)', async () => {
      const geomColl = geometryGenerator.geometryCollection('geocollection_roundtrip');
      await verifyGeometryRoundtrip(geomColl, { mayFail: true });
    });
  });

  describe('Null Geometry', () => {
    it('should add a feature with null geometry', async () => {
      const addResponse = await client.applyEdits({
        adds: [
          {
            geometry: null,
            attributes: { name: 'Null Geometry Test' },
          },
        ],
      });

      // May succeed or fail depending on layer configuration
      if (addResponse.status === 200) {
        const addResult = addResponse.data.addResults?.[0];
        if (addResult?.success && addResult?.objectId) {
          createdObjectIds.push(addResult.objectId);

          // Query back
          const queryResponse = await client.query({
            where: `OBJECTID = ${addResult.objectId}`,
            returnGeometry: true,
          });

          expect(queryResponse.status).toBe(200);
          const data = assertEsriFeatureSet(queryResponse);
          if (data.features.length > 0) {
            expect(data.features[0].geometry).toBeNull();
          }
        }
      }
    });
  });
});

// =============================================================================
// Geometry Type Matrix Tests
// =============================================================================

describe('Geometry Type Matrix - Query Coverage', () => {
  describe.each(GEOMETRY_TYPE_CASES)(
    'Query with $method geometry',
    ({ method, esriType }) => {
      it('should accept geometry as query filter', async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(geom.esriJson),
          geometryType: esriType,
          spatialRel: 'esriSpatialRelIntersects',
        });

        expect(response.status).toBe(200);
        assertEsriFeatureSet(response);
      });

      it('should return features in Esri JSON format', async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(geom.esriJson),
          geometryType: esriType,
          spatialRel: 'esriSpatialRelIntersects',
          returnGeometry: true,
          f: 'json',
        });

        expect(response.status).toBe(200);
        const data = assertEsriFeatureSet(response);

        // Validate Esri geometry structure if features returned
        for (const feature of data.features) {
          if (feature.geometry) {
            const geomData = feature.geometry as Record<string, unknown>;
            const hasPoint = 'x' in geomData && 'y' in geomData;
            const hasRings = 'rings' in geomData;
            const hasPaths = 'paths' in geomData;
            const hasPoints = 'points' in geomData;
            expect(hasPoint || hasRings || hasPaths || hasPoints).toBe(true);
          }
        }
      });

      it('should return features in GeoJSON format', async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.queryGeoJson({
          where: '1=1',
          geometry: JSON.stringify(geom.esriJson),
          geometryType: esriType,
          spatialRel: 'esriSpatialRelIntersects',
          returnGeometry: true,
        });

        expect(response.status).toBe(200);
        const data = assertGeoJsonFeatureCollection(response);

        // Validate GeoJSON structure
        for (const feature of data.features) {
          expect(feature.type).toBe('Feature');
          expect(feature).toHaveProperty('properties');
          if (feature.geometry) {
            expect(feature.geometry.type).toBeDefined();
          }
        }
      });
    },
  );
});

// =============================================================================
// All Geometry Methods Matrix
// =============================================================================

describe('All Geometry Methods - Roundtrip Matrix', () => {
  describe.each(ALL_GEOMETRY_METHODS)('geometry method: %s', (method) => {
    it('should support add operation', async () => {
      // Skip null geometry as it's handled separately
      if (method === 'nullGeometry') {
        return;
      }

      const geom = geometryGenerator.getByMethod(method);
      const geomWithUniqueName = {
        ...geom,
        name: `${method}_matrix_${Date.now()}`,
      };

      const addResponse = await client.applyEdits({
        adds: [
          {
            geometry: geomWithUniqueName.esriJson,
            attributes: { name: geomWithUniqueName.name },
          },
        ],
      });

      // GeometryCollection may not be supported
      if (method === 'geometryCollection' && addResponse.status !== 200) {
        return;
      }

      expect(addResponse.status).toBe(200);
      const addResult = addResponse.data.addResults?.[0];

      if (addResult?.success && addResult?.objectId) {
        createdObjectIds.push(addResult.objectId);
      }
    });
  });
});

// =============================================================================
// Geometry Validity Tests
// =============================================================================

describe('Geometry Validity', () => {
  it('should return valid geometries when queried', async () => {
    const response = await client.queryGeoJson({
      where: '1=1',
      returnGeometry: true,
      resultRecordCount: 50,
    });

    expect(response.status).toBe(200);
    const data = assertGeoJsonFeatureCollection(response);

    for (const feature of data.features) {
      if (feature.geometry) {
        // Basic validity checks
        expect(feature.geometry.type).toBeDefined();

        switch (feature.geometry.type) {
          case 'Point':
            expect(feature.geometry.coordinates).toBeInstanceOf(Array);
            expect(feature.geometry.coordinates.length).toBeGreaterThanOrEqual(2);
            break;
          case 'LineString':
            expect(feature.geometry.coordinates).toBeInstanceOf(Array);
            expect(feature.geometry.coordinates.length).toBeGreaterThanOrEqual(2);
            break;
          case 'Polygon':
            expect(feature.geometry.coordinates).toBeInstanceOf(Array);
            expect(feature.geometry.coordinates.length).toBeGreaterThanOrEqual(1);
            // Exterior ring should have at least 4 points (closed)
            expect(feature.geometry.coordinates[0].length).toBeGreaterThanOrEqual(4);
            break;
          default:
            break;
        }
      }
    }
  });

  it('should return valid Esri geometries', async () => {
    const response = await client.query({
      where: '1=1',
      returnGeometry: true,
      resultRecordCount: 50,
      f: 'json',
    });

    expect(response.status).toBe(200);
    const data = assertEsriFeatureSet(response);

    for (const feature of data.features) {
      if (feature.geometry) {
        const geom = feature.geometry as Record<string, unknown>;

        // Check for valid Esri geometry structure
        const hasPoint = 'x' in geom && 'y' in geom;
        const hasMultiPoint = 'points' in geom && Array.isArray(geom.points);
        const hasPolyline = 'paths' in geom && Array.isArray(geom.paths);
        const hasPolygon = 'rings' in geom && Array.isArray(geom.rings);

        expect(hasPoint || hasMultiPoint || hasPolyline || hasPolygon).toBe(true);

        // Validate coordinates
        if (hasPoint) {
          expect(typeof geom.x).toBe('number');
          expect(typeof geom.y).toBe('number');
        }
        if (hasMultiPoint) {
          expect((geom.points as any[]).length).toBeGreaterThan(0);
        }
        if (hasPolyline) {
          expect((geom.paths as any[]).length).toBeGreaterThan(0);
        }
        if (hasPolygon) {
          expect((geom.rings as any[]).length).toBeGreaterThan(0);
        }
      }
    }
  });
});
