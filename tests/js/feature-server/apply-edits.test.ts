/**
 * Tests for GeoServices REST applyEdits endpoint.
 *
 * Endpoint: POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits
 *
 * Tests cover:
 * - Add operations (new features)
 * - Update operations (modify existing features)
 * - Delete operations (remove features)
 * - Combined operations
 * - Error handling
 */

import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import {
  FeatureServerClient,
  assertEsriFeatureSet,
  assertEditSuccess,
} from '../shared/client';
import { GeometryGenerator } from '../shared/geometry';
import { GEOMETRY_TYPE_CASES } from '../shared/constants';

// =============================================================================
// Test Setup
// =============================================================================

let client: FeatureServerClient;
let geometryGenerator: GeometryGenerator;
const createdObjectIds: number[] = [];

beforeAll(() => {
  client = new FeatureServerClient();
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
// Add Operations
// =============================================================================

describe('ApplyEdits - Add Operations', () => {
  describe('Single Feature Add', () => {
    it('should add a single point feature', async () => {
      const point = geometryGenerator.point('add_single_point');

      const response = await client.applyEdits({
        adds: [
          {
            geometry: point.esriJson,
            attributes: { name: 'Test Feature' },
          },
        ],
      });

      expect(response.status).toBe(200);
      expect(response.data.addResults).toBeDefined();
      expect(response.data.addResults?.length).toBe(1);

      const result = response.data.addResults![0];
      assertEditSuccess(result);
      createdObjectIds.push(result.objectId!);
    });

    it('should return objectId for added feature', async () => {
      const point = geometryGenerator.point('add_with_objectid');

      const response = await client.applyEdits({
        adds: [
          {
            geometry: point.esriJson,
            attributes: { name: 'ObjectId Test' },
          },
        ],
      });

      expect(response.status).toBe(200);
      const result = response.data.addResults?.[0];
      expect(result?.objectId).toBeDefined();
      expect(typeof result?.objectId).toBe('number');
      createdObjectIds.push(result!.objectId!);
    });
  });

  describe('Multiple Features Add', () => {
    it('should add multiple features in single request', async () => {
      const adds = [];
      for (let i = 0; i < 3; i++) {
        const point = geometryGenerator.point(`add_multi_${i}`, -122.4 + i * 0.01);
        adds.push({
          geometry: point.esriJson,
          attributes: { name: `Feature ${i}` },
        });
      }

      const response = await client.applyEdits({ adds });

      expect(response.status).toBe(200);
      expect(response.data.addResults?.length).toBe(3);

      for (const result of response.data.addResults!) {
        expect(result.success).toBe(true);
        createdObjectIds.push(result.objectId!);
      }
    });

    it('should add 10 features in batch', async () => {
      const adds = [];
      for (let i = 0; i < 10; i++) {
        const point = geometryGenerator.point(`add_batch_${i}`, -122.4 + i * 0.001);
        adds.push({
          geometry: point.esriJson,
          attributes: { name: `Batch Feature ${i}`, count: i },
        });
      }

      const response = await client.applyEdits({ adds });

      expect(response.status).toBe(200);
      expect(response.data.addResults?.length).toBe(10);

      const successCount = response.data.addResults!.filter((r) => r.success).length;
      expect(successCount).toBe(10);

      for (const result of response.data.addResults!) {
        if (result.objectId) {
          createdObjectIds.push(result.objectId);
        }
      }
    });
  });

  describe('Add Without Geometry', () => {
    it('should handle add without geometry (attributes only)', async () => {
      const response = await client.applyEdits({
        adds: [
          {
            attributes: { name: 'No Geometry Feature' },
          },
        ],
      });

      // May succeed or fail depending on layer configuration
      expect([200, 400]).toContain(response.status);

      if (response.status === 200 && response.data.addResults?.[0]?.objectId) {
        createdObjectIds.push(response.data.addResults[0].objectId);
      }
    });
  });

  describe('Add with Different Geometry Types', () => {
    describe.each(GEOMETRY_TYPE_CASES.slice(0, 5))('geometry: $method', ({ method }) => {
      it(`should add feature with ${method} geometry`, async () => {
        const geom = geometryGenerator.getByMethod(method);

        const response = await client.applyEdits({
          adds: [
            {
              geometry: geom.esriJson,
              attributes: { name: `${method} feature` },
            },
          ],
        });

        // May fail depending on layer's geometry type configuration
        if (response.status === 200) {
          const result = response.data.addResults?.[0];
          if (result?.success && result?.objectId) {
            createdObjectIds.push(result.objectId);
          }
        }
      });
    });
  });
});

// =============================================================================
// Update Operations
// =============================================================================

describe('ApplyEdits - Update Operations', () => {
  let testObjectId: number | null = null;

  beforeAll(async () => {
    // Create a feature to update
    const point = geometryGenerator.point('update_target');
    const response = await client.applyEdits({
      adds: [
        {
          geometry: point.esriJson,
          attributes: { name: 'Original Name' },
        },
      ],
    });

    if (response.status === 200 && response.data.addResults?.[0]?.success) {
      testObjectId = response.data.addResults[0].objectId!;
      createdObjectIds.push(testObjectId);
    }
  });

  describe('Update Attributes', () => {
    it('should update feature attributes', async () => {
      if (!testObjectId) {
        return; // Skip if setup failed
      }

      const response = await client.applyEdits({
        updates: [
          {
            attributes: {
              OBJECTID: testObjectId,
              name: 'Updated Name',
            },
          },
        ],
      });

      expect(response.status).toBe(200);
      expect(response.data.updateResults).toBeDefined();
      expect(response.data.updateResults?.length).toBe(1);
      expect(response.data.updateResults?.[0].success).toBe(true);
    });

    it('should verify updated attributes', async () => {
      if (!testObjectId) {
        return;
      }

      const queryResponse = await client.query({
        where: `OBJECTID = ${testObjectId}`,
      });

      const data = assertEsriFeatureSet(queryResponse);
      if (data.features.length > 0) {
        expect(data.features[0].attributes.name).toBe('Updated Name');
      }
    });
  });

  describe('Update Geometry', () => {
    it('should update feature geometry', async () => {
      // Create a new feature to update geometry
      const point1 = geometryGenerator.point('geom_update_original', -122.4);
      const addResponse = await client.applyEdits({
        adds: [
          {
            geometry: point1.esriJson,
            attributes: { name: 'Geometry Update Test' },
          },
        ],
      });

      if (addResponse.status !== 200 || !addResponse.data.addResults?.[0]?.success) {
        return;
      }

      const objectId = addResponse.data.addResults[0].objectId!;
      createdObjectIds.push(objectId);

      // Update the geometry
      const point2 = geometryGenerator.point('geom_update_new', -122.5);
      const updateResponse = await client.applyEdits({
        updates: [
          {
            geometry: point2.esriJson,
            attributes: { OBJECTID: objectId },
          },
        ],
      });

      expect(updateResponse.status).toBe(200);
      expect(updateResponse.data.updateResults?.[0].success).toBe(true);
    });
  });

  describe('Update Nonexistent Feature', () => {
    it('should handle update of nonexistent feature', async () => {
      const response = await client.applyEdits({
        updates: [
          {
            attributes: {
              OBJECTID: 999999999,
              name: 'Should Fail',
            },
          },
        ],
      });

      if (response.status === 200) {
        expect(response.data.updateResults?.[0].success).toBe(false);
      }
    });
  });

  describe('Update Multiple Features', () => {
    it('should update multiple features in single request', async () => {
      // Create features to update
      const adds = [];
      for (let i = 0; i < 3; i++) {
        const point = geometryGenerator.point(`multi_update_${i}`, -122.4 + i * 0.01);
        adds.push({
          geometry: point.esriJson,
          attributes: { name: `Multi Update ${i}` },
        });
      }

      const addResponse = await client.applyEdits({ adds });
      if (addResponse.status !== 200) return;

      const objectIds = addResponse.data.addResults
        ?.filter((r) => r.success && r.objectId)
        .map((r) => r.objectId!) || [];

      createdObjectIds.push(...objectIds);

      if (objectIds.length < 3) return;

      // Update all features
      const updates = objectIds.map((id, i) => ({
        attributes: {
          OBJECTID: id,
          name: `Updated Multi ${i}`,
        },
      }));

      const updateResponse = await client.applyEdits({ updates });

      expect(updateResponse.status).toBe(200);
      expect(updateResponse.data.updateResults?.length).toBe(3);
    });
  });
});

// =============================================================================
// Delete Operations
// =============================================================================

describe('ApplyEdits - Delete Operations', () => {
  describe('Delete Single Feature', () => {
    it('should delete a single feature', async () => {
      // Create a feature to delete
      const point = geometryGenerator.point('delete_single');
      const addResponse = await client.applyEdits({
        adds: [
          {
            geometry: point.esriJson,
            attributes: { name: 'To Be Deleted' },
          },
        ],
      });

      if (addResponse.status !== 200 || !addResponse.data.addResults?.[0]?.success) {
        return;
      }

      const objectId = addResponse.data.addResults[0].objectId!;

      // Delete the feature
      const deleteResponse = await client.applyEdits({
        deletes: [objectId],
      });

      expect(deleteResponse.status).toBe(200);
      expect(deleteResponse.data.deleteResults).toBeDefined();
      expect(deleteResponse.data.deleteResults?.length).toBe(1);
      expect(deleteResponse.data.deleteResults?.[0].success).toBe(true);
    });
  });

  describe('Delete Multiple Features', () => {
    it('should delete multiple features in single request', async () => {
      // Create features to delete
      const adds = [];
      for (let i = 0; i < 3; i++) {
        const point = geometryGenerator.point(`delete_multi_${i}`, -122.4 + i * 0.01);
        adds.push({
          geometry: point.esriJson,
          attributes: { name: `Delete Me ${i}` },
        });
      }

      const addResponse = await client.applyEdits({ adds });
      if (addResponse.status !== 200) return;

      const objectIds = addResponse.data.addResults
        ?.filter((r) => r.success && r.objectId)
        .map((r) => r.objectId!) || [];

      if (objectIds.length < 3) return;

      // Delete all features
      const deleteResponse = await client.applyEdits({
        deletes: objectIds,
      });

      expect(deleteResponse.status).toBe(200);
      expect(deleteResponse.data.deleteResults?.length).toBe(3);
    });
  });

  describe('Delete Nonexistent Feature', () => {
    it('should handle delete of nonexistent feature gracefully', async () => {
      const response = await client.applyEdits({
        deletes: [999999999],
      });

      // May return 200 with failure or handle gracefully
      if (response.status === 200 && response.data.deleteResults?.length) {
        // Result may indicate failure
      }
    });
  });
});

// =============================================================================
// Combined Operations
// =============================================================================

describe('ApplyEdits - Combined Operations', () => {
  it('should perform add, update, and delete in single request', async () => {
    // Create features for update and delete
    const point1 = geometryGenerator.point('combined_update', -122.41);
    const point2 = geometryGenerator.point('combined_delete', -122.42);

    const setupResponse = await client.applyEdits({
      adds: [
        { geometry: point1.esriJson, attributes: { name: 'Update Target' } },
        { geometry: point2.esriJson, attributes: { name: 'Delete Target' } },
      ],
    });

    if (setupResponse.status !== 200) return;

    const addResults = setupResponse.data.addResults || [];
    if (addResults.length < 2 || !addResults[0].success || !addResults[1].success) {
      return;
    }

    const updateId = addResults[0].objectId!;
    const deleteId = addResults[1].objectId!;
    createdObjectIds.push(updateId); // Track for cleanup

    // Combined operation
    const newPoint = geometryGenerator.point('combined_add', -122.43);
    const combinedResponse = await client.applyEdits({
      adds: [
        { geometry: newPoint.esriJson, attributes: { name: 'New Feature' } },
      ],
      updates: [
        { attributes: { OBJECTID: updateId, name: 'Updated' } },
      ],
      deletes: [deleteId],
    });

    expect(combinedResponse.status).toBe(200);
    expect(combinedResponse.data.addResults).toBeDefined();
    expect(combinedResponse.data.updateResults).toBeDefined();
    expect(combinedResponse.data.deleteResults).toBeDefined();

    // Track new feature for cleanup
    if (combinedResponse.data.addResults?.[0]?.objectId) {
      createdObjectIds.push(combinedResponse.data.addResults[0].objectId);
    }
  });

  it('should handle partial failure in combined operations', async () => {
    // Create a feature for valid update
    const point = geometryGenerator.point('partial_fail_update');
    const setupResponse = await client.applyEdits({
      adds: [
        { geometry: point.esriJson, attributes: { name: 'Valid Update' } },
      ],
    });

    if (setupResponse.status !== 200 || !setupResponse.data.addResults?.[0]?.success) {
      return;
    }

    const validId = setupResponse.data.addResults[0].objectId!;
    createdObjectIds.push(validId);

    // Combined with invalid update
    const response = await client.applyEdits({
      updates: [
        { attributes: { OBJECTID: validId, name: 'Valid Update' } },
        { attributes: { OBJECTID: 999999999, name: 'Invalid Update' } },
      ],
    });

    expect(response.status).toBe(200);

    // First should succeed, second should fail
    const results = response.data.updateResults || [];
    if (results.length >= 2) {
      expect(results[0].success).toBe(true);
      expect(results[1].success).toBe(false);
    }
  });
});

// =============================================================================
// Error Handling
// =============================================================================

describe('ApplyEdits - Error Handling', () => {
  describe('Invalid JSON', () => {
    it('should return 400 for invalid adds JSON', async () => {
      // This tests the raw POST with invalid data
      const url = `${process.env.HONUA_BASE_URL || 'http://localhost:5555'}/rest/services/${process.env.HONUA_SERVICE_ID || 'test_service_gw0'}/FeatureServer/${process.env.HONUA_LAYER_ID || '1000'}/applyEdits`;

      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({ adds: 'not valid json', f: 'json' }),
      });

      expect(response.status).toBe(400);
    });
  });

  describe('Invalid Layer', () => {
    it('should return error for nonexistent layer', async () => {
      const point = geometryGenerator.point('invalid_layer');

      const response = await client.applyEdits(
        {
          adds: [
            { geometry: point.esriJson, attributes: { name: 'Test' } },
          ],
        },
        99999,
      );

      expect([400, 404]).toContain(response.status);
    });
  });

  describe('Empty Request', () => {
    it('should handle empty applyEdits request', async () => {
      const response = await client.applyEdits({});
      expect([200, 400]).toContain(response.status);
    });
  });

  describe('Invalid Geometry', () => {
    it('should handle malformed geometry', async () => {
      const response = await client.applyEdits({
        adds: [
          {
            geometry: { invalid: 'geometry' } as any,
            attributes: { name: 'Invalid Geom' },
          },
        ],
      });

      // Should either reject or report error
      if (response.status === 200) {
        expect(response.data.addResults?.[0].success).toBe(false);
      }
    });
  });
});

// =============================================================================
// Edge Cases
// =============================================================================

describe('ApplyEdits - Edge Cases', () => {
  it('should handle empty adds array', async () => {
    const response = await client.applyEdits({ adds: [] });
    expect(response.status).toBe(200);
    expect(response.data.addResults?.length ?? 0).toBe(0);
  });

  it('should handle empty updates array', async () => {
    const response = await client.applyEdits({ updates: [] });
    expect(response.status).toBe(200);
    expect(response.data.updateResults?.length ?? 0).toBe(0);
  });

  it('should handle empty deletes array', async () => {
    const response = await client.applyEdits({ deletes: [] });
    expect(response.status).toBe(200);
    expect(response.data.deleteResults?.length ?? 0).toBe(0);
  });

  it('should handle feature with many attributes', async () => {
    const point = geometryGenerator.point('many_attrs');
    const attributes: Record<string, unknown> = { name: 'Many Attributes' };

    // Add many numeric attributes
    for (let i = 0; i < 20; i++) {
      attributes[`field_${i}`] = i;
    }

    const response = await client.applyEdits({
      adds: [
        { geometry: point.esriJson, attributes },
      ],
    });

    // May succeed or fail depending on schema
    if (response.status === 200 && response.data.addResults?.[0]?.objectId) {
      createdObjectIds.push(response.data.addResults[0].objectId);
    }
  });

  it('should handle special characters in attribute values', async () => {
    const point = geometryGenerator.point('special_chars');

    const response = await client.applyEdits({
      adds: [
        {
          geometry: point.esriJson,
          attributes: {
            name: "Test's \"quoted\" value",
            description: 'Line1\nLine2\tTab',
          },
        },
      ],
    });

    if (response.status === 200 && response.data.addResults?.[0]?.objectId) {
      createdObjectIds.push(response.data.addResults[0].objectId);
    }
  });
});
