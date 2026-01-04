/**
 * Tests for GeoServices REST query endpoint.
 *
 * Endpoints:
 * - GET /rest/services/{serviceId}/FeatureServer/{layerId}/query
 * - POST /rest/services/{serviceId}/FeatureServer/{layerId}/query
 *
 * Tests cover:
 * - WHERE clause filtering (comparison, logical, LIKE, IN, BETWEEN, IS NULL)
 * - Spatial queries (geometry, geometryType, spatialRel)
 * - Output control (outFields, returnGeometry)
 * - Pagination (resultOffset, resultRecordCount)
 * - Response formats (json, geojson)
 */

import { describe, it, expect, beforeAll } from 'vitest';
import {
  FeatureServerClient,
  assertEsriFeatureSet,
  assertGeoJsonFeatureCollection,
} from '../shared/client';
import { GeometryGenerator } from '../shared/geometry';
import {
  WHERE_CLAUSE_CASES,
  INVALID_WHERE_CASES,
  OUTPUT_FORMATS,
  PAGINATION_CASES,
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
// Basic Query Tests
// =============================================================================

describe('Query Basic', () => {
  describe('HTTP Methods', () => {
    it('should return 200 for GET request', async () => {
      const response = await client.query({ where: '1=1' });
      expect(response.status).toBe(200);
    });

    it('should return 200 for POST request', async () => {
      const response = await client.queryPost({ where: '1=1' });
      expect(response.status).toBe(200);
    });

    it('should return features array', async () => {
      const response = await client.query({ where: '1=1' });
      const data = assertEsriFeatureSet(response);
      expect(Array.isArray(data.features)).toBe(true);
    });

    it('should include attributes in each feature', async () => {
      const response = await client.query({
        where: '1=1',
        resultRecordCount: 10,
      });
      const data = assertEsriFeatureSet(response);

      for (const feature of data.features) {
        expect(feature).toHaveProperty('attributes');
        expect(typeof feature.attributes).toBe('object');
      }
    });
  });

  describe('Invalid Layer', () => {
    it('should return error for nonexistent layer', async () => {
      const response = await client.query({ where: '1=1' }, 99999);
      expect([400, 404]).toContain(response.status);
    });
  });
});

// =============================================================================
// WHERE Clause Tests
// =============================================================================

describe('WHERE Clause', () => {
  describe('Valid WHERE Clauses', () => {
    describe.each(WHERE_CLAUSE_CASES)('$name', ({ where }) => {
      it(`should accept: ${where}`, async () => {
        const response = await client.query({ where });
        expect(response.status).toBe(200);
        assertEsriFeatureSet(response);
      });
    });
  });

  describe('Invalid WHERE Clauses', () => {
    describe.each(INVALID_WHERE_CASES)('$name', ({ where }) => {
      it(`should reject: ${where}`, async () => {
        const response = await client.query({ where });
        expect(response.status).toBe(400);
      });
    });
  });

  describe('Complex WHERE Combinations', () => {
    it('should handle deeply nested parentheses', async () => {
      const response = await client.query({
        where: "((name = 'a' OR name = 'b') AND count > 0) OR status = 'active'",
      });
      expect(response.status).toBe(200);
    });

    it('should handle multiple AND conditions', async () => {
      const response = await client.query({
        where: 'count > 0 AND count < 100 AND name IS NOT NULL',
      });
      expect(response.status).toBe(200);
    });

    it('should handle multiple OR conditions', async () => {
      const response = await client.query({
        where: "name = 'a' OR name = 'b' OR name = 'c' OR name = 'd'",
      });
      expect(response.status).toBe(200);
    });

    it('should handle LIKE with wildcards', async () => {
      const patterns = ['test%', '%test', '%test%', 'te_t'];
      for (const pattern of patterns) {
        const response = await client.query({
          where: `name LIKE '${pattern}'`,
        });
        expect(response.status).toBe(200);
      }
    });

    it('should handle IN with multiple values', async () => {
      const response = await client.query({
        where: "name IN ('a', 'b', 'c', 'd', 'e')",
      });
      expect(response.status).toBe(200);
    });

    it('should handle NOT IN', async () => {
      const response = await client.query({
        where: "name NOT IN ('excluded1', 'excluded2')",
      });
      expect(response.status).toBe(200);
    });

    it('should handle BETWEEN with numbers', async () => {
      const response = await client.query({
        where: 'count BETWEEN 10 AND 50',
      });
      expect(response.status).toBe(200);
    });
  });
});

// =============================================================================
// Spatial Query Tests
// =============================================================================

describe('Spatial Queries', () => {
  describe('Envelope Geometry', () => {
    it('should filter by bounding box', async () => {
      const envelope = geometryGenerator.envelope();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(envelope),
        geometryType: 'esriGeometryEnvelope',
        spatialRel: 'esriSpatialRelIntersects',
      });

      expect(response.status).toBe(200);
      assertEsriFeatureSet(response);
    });

    it('should support esriSpatialRelContains with envelope', async () => {
      const envelope = geometryGenerator.envelope();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(envelope),
        geometryType: 'esriGeometryEnvelope',
        spatialRel: 'esriSpatialRelContains',
      });

      expect(response.status).toBe(200);
    });
  });

  describe('Point Geometry', () => {
    it('should filter by point location', async () => {
      const point = geometryGenerator.point();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(point.esriJson),
        geometryType: 'esriGeometryPoint',
        spatialRel: 'esriSpatialRelIntersects',
      });

      expect(response.status).toBe(200);
    });
  });

  describe('Polygon Geometry', () => {
    it('should filter by polygon', async () => {
      const polygon = geometryGenerator.polygonSimple();

      const response = await client.query({
        where: '1=1',
        geometry: JSON.stringify(polygon.esriJson),
        geometryType: 'esriGeometryPolygon',
        spatialRel: 'esriSpatialRelIntersects',
      });

      expect(response.status).toBe(200);
    });
  });

  describe('Combined Spatial and Attribute', () => {
    it('should combine WHERE and spatial filter', async () => {
      const envelope = geometryGenerator.envelope();

      const response = await client.query({
        where: 'count > 0',
        geometry: JSON.stringify(envelope),
        geometryType: 'esriGeometryEnvelope',
        spatialRel: 'esriSpatialRelIntersects',
      });

      expect(response.status).toBe(200);
    });
  });
});

// =============================================================================
// Output Control Tests
// =============================================================================

describe('Output Control', () => {
  describe('outFields Parameter', () => {
    it('should return all fields with outFields=*', async () => {
      const response = await client.query({
        where: '1=1',
        outFields: '*',
      });

      const data = assertEsriFeatureSet(response);
      if (data.features.length > 0) {
        expect(Object.keys(data.features[0].attributes).length).toBeGreaterThan(0);
      }
    });

    it('should return specific field with outFields=name', async () => {
      const response = await client.query({
        where: '1=1',
        outFields: 'name',
      });

      expect(response.status).toBe(200);
      // Note: OBJECTID may also be included
    });

    it('should return multiple specific fields', async () => {
      const response = await client.query({
        where: '1=1',
        outFields: 'name,count',
      });

      expect(response.status).toBe(200);
    });
  });

  describe('returnGeometry Parameter', () => {
    it('should include geometry when returnGeometry=true', async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: true,
      });

      const data = assertEsriFeatureSet(response);
      for (const feature of data.features) {
        expect(feature).toHaveProperty('geometry');
      }
    });

    it('should exclude geometry when returnGeometry=false', async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: false,
      });

      const data = assertEsriFeatureSet(response);
      for (const feature of data.features) {
        expect(feature.geometry === undefined || feature.geometry === null).toBe(true);
      }
    });
  });

  describe('returnCountOnly Parameter', () => {
    it('should return only count when returnCountOnly=true', async () => {
      const response = await client.query({
        where: '1=1',
        returnCountOnly: true,
      });

      expect(response.status).toBe(200);
      const data = response.data as any;
      expect(data).toHaveProperty('count');
      expect(typeof data.count).toBe('number');
    });
  });

  describe('returnIdsOnly Parameter', () => {
    it('should return only IDs when returnIdsOnly=true', async () => {
      const response = await client.query({
        where: '1=1',
        returnIdsOnly: true,
      });

      expect(response.status).toBe(200);
      const data = response.data as any;
      expect(data).toHaveProperty('objectIds');
      expect(Array.isArray(data.objectIds)).toBe(true);
    });
  });

  describe('returnExtentOnly Parameter', () => {
    it('should return only extent when returnExtentOnly=true', async () => {
      const response = await client.query({
        where: '1=1',
        returnExtentOnly: true,
      });

      expect(response.status).toBe(200);
      const data = response.data as any;
      expect(data).toHaveProperty('extent');
    });
  });
});

// =============================================================================
// Pagination Tests
// =============================================================================

describe('Pagination', () => {
  describe('resultRecordCount Parameter', () => {
    describe.each(PAGINATION_CASES)('offset=$offset, count=$count', ({ offset, count }) => {
      it(`should limit results to ${count} features`, async () => {
        const response = await client.query({
          where: '1=1',
          resultRecordCount: count,
          resultOffset: offset,
        });

        const data = assertEsriFeatureSet(response);
        expect(data.features.length).toBeLessThanOrEqual(count);
      });
    });
  });

  describe('Pagination Consistency', () => {
    it('should return different features for different offsets', async () => {
      const page1Response = await client.query({
        where: '1=1',
        resultRecordCount: 2,
        resultOffset: 0,
      });

      const page2Response = await client.query({
        where: '1=1',
        resultRecordCount: 2,
        resultOffset: 2,
      });

      const data1 = assertEsriFeatureSet(page1Response);
      const data2 = assertEsriFeatureSet(page2Response);

      if (data1.features.length > 0 && data2.features.length > 0) {
        // Get OBJECTIDs
        const getIds = (features: any[]) =>
          features.map((f) => f.attributes.OBJECTID || f.attributes.id);

        const ids1 = new Set(getIds(data1.features));
        const ids2 = new Set(getIds(data2.features));

        // Pages should not overlap
        const overlap = [...ids1].filter((id) => ids2.has(id));
        expect(overlap.length).toBe(0);
      }
    });

    it('should reject large offset beyond limits', async () => {
      const response = await client.query({
        where: '1=1',
        resultRecordCount: 10,
        resultOffset: 1000001,
      });

      expect(response.status).toBe(400);
    });
  });

  describe('orderByFields with Pagination', () => {
    it('should order results before pagination', async () => {
      const response = await client.query({
        where: '1=1',
        orderByFields: 'OBJECTID ASC',
        resultRecordCount: 5,
      });

      const data = assertEsriFeatureSet(response);

      const ids = data.features
        .map((f) => Number(f.attributes.OBJECTID ?? f.attributes.id))
        .filter((id) => Number.isFinite(id));
      if (ids.length > 1) {
        // Check ascending order
        for (let i = 1; i < ids.length; i++) {
          expect(ids[i]).toBeGreaterThanOrEqual(ids[i - 1]);
        }
      }
    });

    it('should support DESC order', async () => {
      const response = await client.query({
        where: '1=1',
        orderByFields: 'OBJECTID DESC',
        resultRecordCount: 5,
      });

      const data = assertEsriFeatureSet(response);

      const ids = data.features
        .map((f) => Number(f.attributes.OBJECTID ?? f.attributes.id))
        .filter((id) => Number.isFinite(id));
      if (ids.length > 1) {
        // Check descending order
        for (let i = 1; i < ids.length; i++) {
          expect(ids[i]).toBeLessThanOrEqual(ids[i - 1]);
        }
      }
    });
  });
});

// =============================================================================
// Response Format Tests
// =============================================================================

describe('Response Formats', () => {
  describe.each(OUTPUT_FORMATS)('format: %s', (format) => {
    it(`should return valid ${format} response`, async () => {
      const response = await client.query({
        where: '1=1',
        f: format,
      });

      expect(response.status).toBe(200);

      if (format === 'json') {
        const data = assertEsriFeatureSet(response);
        if (data.features.length > 0) {
          expect(data.features[0]).toHaveProperty('attributes');
        }
      } else {
        const data = assertGeoJsonFeatureCollection(response);
        if (data.features.length > 0) {
          expect(data.features[0]).toHaveProperty('properties');
        }
      }
    });

    it(`should include geometry in ${format} format`, async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: true,
        resultRecordCount: 5,
        f: format,
      });

      expect(response.status).toBe(200);
    });
  });

  describe('Esri JSON Format Details', () => {
    it('should use x/y for point geometries', async () => {
      const response = await client.query({
        where: '1=1',
        returnGeometry: true,
        f: 'json',
      });

      const data = assertEsriFeatureSet(response);

      for (const feature of data.features) {
        if (feature.geometry) {
          const geom = feature.geometry as Record<string, unknown>;
          const hasPoint = 'x' in geom && 'y' in geom;
          const hasRings = 'rings' in geom;
          const hasPaths = 'paths' in geom;
          const hasPoints = 'points' in geom;
          expect(hasPoint || hasRings || hasPaths || hasPoints).toBe(true);
        }
      }
    });
  });

  describe('GeoJSON Format Details', () => {
    it('should use FeatureCollection structure', async () => {
      const response = await client.queryGeoJson({
        where: '1=1',
      });

      const data = assertGeoJsonFeatureCollection(response);
      expect(data.type).toBe('FeatureCollection');
      expect(Array.isArray(data.features)).toBe(true);
    });

    it('should use properties for attributes', async () => {
      const response = await client.queryGeoJson({
        where: '1=1',
        resultRecordCount: 5,
      });

      const data = assertGeoJsonFeatureCollection(response);

      for (const feature of data.features) {
        expect(feature.type).toBe('Feature');
        expect(feature).toHaveProperty('properties');
        expect(typeof feature.properties).toBe('object');
      }
    });

    it('should use coordinates for geometries', async () => {
      const response = await client.queryGeoJson({
        where: '1=1',
        returnGeometry: true,
        resultRecordCount: 5,
      });

      const data = assertGeoJsonFeatureCollection(response);

      for (const feature of data.features) {
        if (feature.geometry) {
          expect(feature.geometry).toHaveProperty('type');
          expect(feature.geometry).toHaveProperty('coordinates');
        }
      }
    });
  });
});

// =============================================================================
// Edge Cases
// =============================================================================

describe('Edge Cases', () => {
  it('should handle empty WHERE clause', async () => {
    const response = await client.query({});
    expect([200, 400]).toContain(response.status);
  });

  it('should handle empty geometry filter', async () => {
    const response = await client.query({
      where: '1=1',
      geometry: '',
    });
    expect([200, 400]).toContain(response.status);
  });

  it('should handle special characters in WHERE clause', async () => {
    const response = await client.query({
      where: "name = 'test''s value'", // Escaped single quote
    });
    expect(response.status).toBe(200);
  });

  it('should handle unicode in WHERE clause', async () => {
    const response = await client.query({
      where: "name = 'test\u0000' OR name = 'normal'",
    });
    // May succeed or fail depending on handling
    expect([200, 400]).toContain(response.status);
  });

  it('should reject very large resultRecordCount', async () => {
    const response = await client.query({
      where: '1=1',
      resultRecordCount: 1000000,
    });
    expect(response.status).toBe(400);
  });

  it('should handle negative resultOffset', async () => {
    const response = await client.query({
      where: '1=1',
      resultOffset: -1,
    });
    expect([200, 400]).toContain(response.status);
  });
});
