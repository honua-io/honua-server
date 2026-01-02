/**
 * Matrix coverage for FeatureServer query parameters.
 *
 * Tests all combinations of:
 * - Spatial relationships (9 non-distance + 2 distance × 4 units = 17 variations)
 * - Geometry types (9 variations)
 * - Input/Output spatial references
 * - Nearest count queries
 */

import { describe, it, expect, beforeAll } from 'vitest';
import { FeatureServerClient, assertEsriFeatureSet } from '../shared/client';
import { GeometryGenerator } from '../shared/geometry';
import {
  NON_DISTANCE_SPATIAL_RELS,
  DISTANCE_SPATIAL_RELS,
  DISTANCE_UNITS,
  GEOMETRY_TYPE_CASES,
} from '../shared/constants';

// =============================================================================
// Test Setup
// =============================================================================

let client: FeatureServerClient;
let geometryGenerator: GeometryGenerator;

beforeAll(() => {
  client = new FeatureServerClient();
  geometryGenerator = new GeometryGenerator();
});

// =============================================================================
// Spatial Relationship Matrix Tests
// =============================================================================

describe('Spatial Relationship Matrix', () => {
  describe('Non-Distance Spatial Relationships', () => {
    describe.each(NON_DISTANCE_SPATIAL_RELS)('spatialRel: %s', (spatialRel) => {
      it('should return 200 with envelope geometry', async () => {
        const envelope = geometryGenerator.envelope();

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(envelope),
          geometryType: 'esriGeometryEnvelope',
          spatialRel,
        });

        expect(response.status).toBe(200);
        const data = assertEsriFeatureSet(response);
        expect(data.features).toBeDefined();
      });

      it('should return 200 with point geometry', async () => {
        const point = geometryGenerator.point();

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(point.esriJson),
          geometryType: 'esriGeometryPoint',
          spatialRel,
        });

        expect(response.status).toBe(200);
      });

      it('should return 200 with polygon geometry', async () => {
        const polygon = geometryGenerator.polygonSimple();

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(polygon.esriJson),
          geometryType: 'esriGeometryPolygon',
          spatialRel,
        });

        expect(response.status).toBe(200);
      });
    });
  });

  describe('Distance-Based Spatial Relationships', () => {
    describe.each(DISTANCE_SPATIAL_RELS)('spatialRel: %s', (spatialRel) => {
      describe.each(DISTANCE_UNITS)('unit: %s', (unit) => {
        it('should return 200 with point geometry and distance', async () => {
          const point = geometryGenerator.point();

          const response = await client.query({
            where: '1=1',
            geometry: JSON.stringify(point.esriJson),
            geometryType: 'esriGeometryPoint',
            spatialRel,
            distance: 1000,
            units: unit,
          });

          expect(response.status).toBe(200);
        });

        it('should return 200 with envelope geometry and distance', async () => {
          const envelope = geometryGenerator.envelope();

          const response = await client.query({
            where: '1=1',
            geometry: JSON.stringify(envelope),
            geometryType: 'esriGeometryEnvelope',
            spatialRel,
            distance: 500,
            units: unit,
          });

          expect(response.status).toBe(200);
        });
      });
    });

    it('should return 400 when distance parameter is missing for esriSpatialRelWithinDistance', async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        spatialRel: 'esriSpatialRelWithinDistance',
        // No distance parameter
      });

      expect(response.status).toBe(400);
    });

    it('should return 400 when distance parameter is missing for esriSpatialRelBeyondDistance', async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        spatialRel: 'esriSpatialRelBeyondDistance',
        // No distance parameter
      });

      expect(response.status).toBe(400);
    });
  });
});

// =============================================================================
// Geometry Type Matrix Tests
// =============================================================================

describe('Geometry Type Matrix', () => {
  describe.each(GEOMETRY_TYPE_CASES)(
    'geometry: $method -> $esriType',
    ({ method, esriType }) => {
      it('should return 200 when used as query geometry', async () => {
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

      it('should return 200 with esriSpatialRelContains', async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(geom.esriJson),
          geometryType: esriType,
          spatialRel: 'esriSpatialRelContains',
        });

        expect(response.status).toBe(200);
      });

      it('should return 200 with esriSpatialRelWithin', async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(geom.esriJson),
          geometryType: esriType,
          spatialRel: 'esriSpatialRelWithin',
        });

        expect(response.status).toBe(200);
      });
    },
  );
});

// =============================================================================
// Input/Output Spatial Reference Matrix
// =============================================================================

describe('Spatial Reference Matrix', () => {
  describe('Input Spatial Reference (inSR)', () => {
    it('should accept inSR=4326 (WGS84)', async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        inSR: '4326',
      });

      expect(response.status).toBe(200);
    });

    it('should accept inSR=3857 (Web Mercator) with transformed coordinates', async () => {
      // Web Mercator coordinates for San Francisco area
      const webMercatorPoint = {
        x: -13627665.27,
        y: 4548084.22,
        spatialReference: { wkid: 3857 },
      };

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(webMercatorPoint),
        geometryType: 'esriGeometryPoint',
        inSR: '3857',
      });

      expect(response.status).toBe(200);
    });

    it('should return 400 for invalid inSR', async () => {
      const response = await client.query({
        where: '1=1',
        inSR: 'invalid',
      });

      expect(response.status).toBe(400);
    });
  });

  describe('Output Spatial Reference (outSR)', () => {
    it('should return geometry in WGS84 when outSR=4326', async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: true,
        outSR: '4326',
      });

      expect(response.status).toBe(200);
      const data = assertEsriFeatureSet(response);

      if (data.spatialReference) {
        expect(data.spatialReference.wkid === 4326 || data.spatialReference.latestWkid === 4326).toBe(true);
      }
    });

    it('should return geometry in Web Mercator when outSR=3857', async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: true,
        outSR: '3857',
      });

      expect(response.status).toBe(200);
      const data = assertEsriFeatureSet(response);

      if (data.spatialReference) {
        expect(data.spatialReference.wkid === 3857 || data.spatialReference.latestWkid === 3857).toBe(true);
      }
    });

    it('should return 400 for invalid outSR', async () => {
      const response = await client.query({
        where: '1=1',
        outSR: 'invalid',
      });

      expect(response.status).toBe(400);
    });
  });

  describe('Combined inSR and outSR', () => {
    it('should transform from 4326 to 3857', async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        inSR: '4326',
        outSR: '3857',
        returnGeometry: true,
      });

      expect(response.status).toBe(200);
    });

    it('should transform from 3857 to 4326', async () => {
      const webMercatorPoint = {
        x: -13627665.27,
        y: 4548084.22,
        spatialReference: { wkid: 3857 },
      };

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(webMercatorPoint),
        geometryType: 'esriGeometryPoint',
        inSR: '3857',
        outSR: '4326',
        returnGeometry: true,
      });

      expect(response.status).toBe(200);
    });
  });
});

// =============================================================================
// Nearest Count Matrix
// =============================================================================

describe('Nearest Count Matrix', () => {
  it('should return 400 when nearestCount used without geometry', async () => {
    const response = await client.query({
      where: '1=1',
      nearestCount: 3,
    } as any);

    expect(response.status).toBe(400);
  });

  it('should return features with distance when returnDistance=true', async () => {
    const point = geometryGenerator.point();

    const response = await client.query({
      where: '1=1',
      geometry: JSON.stringify(point.esriJson),
      geometryType: 'esriGeometryPoint',
      nearestCount: 3,
      returnDistance: true,
    } as any);

    expect(response.status).toBe(200);
    const data = assertEsriFeatureSet(response);

    // If features are returned, they should have distance
    if (data.features.length > 0) {
      expect(data.features[0].attributes).toHaveProperty('distance');
    }
  });

  describe.each([1, 3, 5, 10])('nearestCount: %d', (count) => {
    it(`should return at most ${count} features`, async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        nearestCount: count,
      } as any);

      expect(response.status).toBe(200);
      const data = assertEsriFeatureSet(response);
      expect(data.features.length).toBeLessThanOrEqual(count);
    });
  });
});

// =============================================================================
// Combined Spatial + Attribute Filter Matrix
// =============================================================================

describe('Combined Spatial + Attribute Filters', () => {
  const spatialRels = ['esriSpatialRelIntersects', 'esriSpatialRelContains'];
  const whereConditions = [
    { name: 'simple equality', where: "name = 'test'" },
    { name: 'comparison', where: 'count > 0' },
    { name: 'compound AND', where: "name = 'test' AND count > 0" },
    { name: 'compound OR', where: "name = 'a' OR name = 'b'" },
  ];

  describe.each(spatialRels)('spatialRel: %s', (spatialRel) => {
    describe.each(whereConditions)('WHERE: $name', ({ where }) => {
      it('should combine spatial and attribute filters', async () => {
        const envelope = geometryGenerator.envelope();

        const response = await client.query({
          where,
          geometry: JSON.stringify(envelope),
          geometryType: 'esriGeometryEnvelope',
          spatialRel,
        });

        expect(response.status).toBe(200);
        assertEsriFeatureSet(response);
      });
    });
  });
});

// =============================================================================
// Response Format Matrix
// =============================================================================

describe('Response Format Matrix', () => {
  describe.each(['json', 'geojson'] as const)('format: %s', (format) => {
    describe.each(NON_DISTANCE_SPATIAL_RELS.slice(0, 3))('spatialRel: %s', (spatialRel) => {
      it(`should return ${format} format with spatial filter`, async () => {
        const envelope = geometryGenerator.envelope();

        const response = await client.query({
          where: '1=1',
          geometry: JSON.stringify(envelope),
          geometryType: 'esriGeometryEnvelope',
          spatialRel,
          f: format,
        });

        expect(response.status).toBe(200);

        if (format === 'json') {
          const data = response.data as any;
          expect(data.features).toBeDefined();
          if (data.features.length > 0) {
            expect(data.features[0]).toHaveProperty('attributes');
          }
        } else {
          const data = response.data as any;
          expect(data.type).toBe('FeatureCollection');
          if (data.features.length > 0) {
            expect(data.features[0]).toHaveProperty('properties');
          }
        }
      });
    });
  });
});
