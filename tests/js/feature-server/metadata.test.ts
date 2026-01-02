/**
 * Tests for GeoServices REST service and layer metadata endpoints.
 *
 * Endpoints:
 * - GET /rest/services/{serviceId}/FeatureServer
 * - GET /rest/services/{serviceId}/FeatureServer/{layerId}
 *
 * Tests cover:
 * - Service metadata structure
 * - Layer metadata structure
 * - Field definitions
 * - Spatial reference information
 * - Capabilities
 * - Error handling
 */

import { describe, it, expect, beforeAll } from 'vitest';
import { FeatureServerClient } from '../shared/client';
import { VALID_ESRI_GEOMETRY_TYPES } from '../shared/constants';

// =============================================================================
// Test Setup
// =============================================================================

let client: FeatureServerClient;

beforeAll(() => {
  client = new FeatureServerClient();
});

// =============================================================================
// Service Metadata Tests
// =============================================================================

describe('Service Metadata', () => {
  describe('Basic Response', () => {
    it('should return 200 for valid service', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
    });

    it('should return JSON response', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toBeDefined();
      expect(typeof response.data).toBe('object');
    });
  });

  describe('Required Properties', () => {
    it('should contain layers array', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      expect(response.data.layers).toBeDefined();
      expect(Array.isArray(response.data.layers)).toBe(true);
    });

    it('should contain currentVersion', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      // May be named currentVersion or version
      const hasVersion = 'currentVersion' in response.data || 'version' in response.data;
      expect(hasVersion).toBe(true);
    });

    it('should contain serviceDescription', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      // May be named serviceDescription or description
      const hasDescription = 'serviceDescription' in response.data || 'description' in response.data;
      expect(hasDescription).toBe(true);
    });
  });

  describe('Layer References', () => {
    it('should list layers with id and name', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);

      for (const layer of response.data.layers) {
        expect(layer).toHaveProperty('id');
        expect(layer).toHaveProperty('name');
        expect(typeof layer.id).toBe('number');
        expect(typeof layer.name).toBe('string');
      }
    });

    it('should have at least one layer', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      expect(response.data.layers.length).toBeGreaterThan(0);
    });
  });

  describe('Optional Properties', () => {
    it('should include tables array if present', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);

      if ('tables' in response.data) {
        expect(Array.isArray(response.data.tables)).toBe(true);
      }
    });
  });

  describe('Error Cases', () => {
    it('should return 404 for nonexistent service', async () => {
      const customClient = new FeatureServerClient({
        serviceId: 'nonexistent_service_xyz',
      });

      const response = await customClient.getServiceMetadata();
      expect(response.status).toBe(404);
    });
  });
});

// =============================================================================
// Layer Metadata Tests
// =============================================================================

describe('Layer Metadata', () => {
  describe('Basic Response', () => {
    it('should return 200 for valid layer', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
    });

    it('should return JSON response', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toBeDefined();
      expect(typeof response.data).toBe('object');
    });
  });

  describe('Required Properties', () => {
    it('should contain layer id', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('id');
      expect(typeof response.data.id).toBe('number');
    });

    it('should contain layer name', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('name');
      expect(typeof response.data.name).toBe('string');
    });

    it('should contain geometryType', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('geometryType');

      // Should be a valid Esri geometry type or null
      const geomType = response.data.geometryType;
      if (geomType !== null) {
        expect(VALID_ESRI_GEOMETRY_TYPES).toContain(geomType);
      }
    });

    it('should contain fields array', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('fields');
      expect(Array.isArray(response.data.fields)).toBe(true);
    });

    it('should contain extent', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('extent');
    });

    it('should contain capabilities', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toHaveProperty('capabilities');
    });
  });

  describe('Field Definitions', () => {
    it('should have name and type for each field', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const fields = response.data.fields || [];
      for (const field of fields) {
        expect(field).toHaveProperty('name');
        expect(field).toHaveProperty('type');
        expect(typeof field.name).toBe('string');
        expect(typeof field.type).toBe('string');
      }
    });

    it('should include OBJECTID field', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const fields = response.data.fields || [];
      const objectIdField = fields.find(
        (f) => f.name.toUpperCase() === 'OBJECTID' || f.type === 'esriFieldTypeOID',
      );
      expect(objectIdField).toBeDefined();
    });

    it('should use valid Esri field types', async () => {
      const validFieldTypes = [
        'esriFieldTypeSmallInteger',
        'esriFieldTypeInteger',
        'esriFieldTypeSingle',
        'esriFieldTypeDouble',
        'esriFieldTypeString',
        'esriFieldTypeDate',
        'esriFieldTypeOID',
        'esriFieldTypeGeometry',
        'esriFieldTypeBlob',
        'esriFieldTypeRaster',
        'esriFieldTypeGUID',
        'esriFieldTypeGlobalID',
        'esriFieldTypeXML',
      ];

      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const fields = response.data.fields || [];
      for (const field of fields) {
        expect(validFieldTypes).toContain(field.type);
      }
    });

    it('should include field aliases if present', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const fields = response.data.fields || [];
      // Alias is optional but common
      for (const field of fields) {
        if ('alias' in field) {
          expect(typeof field.alias).toBe('string');
        }
      }
    });
  });

  describe('Spatial Reference', () => {
    it('should include spatial reference information', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      // Can be in spatialReference or extent.spatialReference
      const hasSR = 'spatialReference' in response.data;
      const hasExtentSR = response.data.extent && 'spatialReference' in response.data.extent;
      expect(hasSR || hasExtentSR).toBe(true);
    });

    it('should include wkid in spatial reference', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      let sr = response.data.spatialReference;
      if (!sr && response.data.extent) {
        sr = response.data.extent.spatialReference;
      }

      if (sr) {
        expect(sr).toHaveProperty('wkid');
        expect(typeof sr.wkid).toBe('number');
      }
    });
  });

  describe('Extent Structure', () => {
    it('should have xmin, ymin, xmax, ymax in extent', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const extent = response.data.extent;
      if (extent != null) {
        expect(extent).toHaveProperty('xmin');
        expect(extent).toHaveProperty('ymin');
        expect(extent).toHaveProperty('xmax');
        expect(extent).toHaveProperty('ymax');
      }
    });

    it('should have valid coordinate values in extent', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const extent = response.data.extent;
      if (extent != null) {
        expect(typeof extent.xmin).toBe('number');
        expect(typeof extent.ymin).toBe('number');
        expect(typeof extent.xmax).toBe('number');
        expect(typeof extent.ymax).toBe('number');

        // xmax should be >= xmin
        expect(extent.xmax).toBeGreaterThanOrEqual(extent.xmin);
        // ymax should be >= ymin
        expect(extent.ymax).toBeGreaterThanOrEqual(extent.ymin);
      }
    });
  });

  describe('Capabilities', () => {
    it('should declare layer capabilities', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const capabilities = response.data.capabilities;
      expect(capabilities).toBeDefined();

      // Capabilities can be string or array
      if (typeof capabilities === 'string') {
        expect(capabilities.length).toBeGreaterThan(0);
      } else if (Array.isArray(capabilities)) {
        expect(capabilities.length).toBeGreaterThan(0);
      }
    });

    it('should include Query capability', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      const capabilities = response.data.capabilities;
      if (typeof capabilities === 'string') {
        expect(capabilities.toLowerCase()).toContain('query');
      } else if (Array.isArray(capabilities)) {
        expect(capabilities.some((c) => c.toLowerCase().includes('query'))).toBe(true);
      }
    });
  });

  describe('Error Cases', () => {
    it('should return 404 for nonexistent layer', async () => {
      const response = await client.getLayerMetadata(99999);
      expect(response.status).toBe(404);
    });
  });
});

// =============================================================================
// Cross-Endpoint Consistency Tests
// =============================================================================

describe('Metadata Consistency', () => {
  it('should have matching layer id in service and layer metadata', async () => {
    const serviceResponse = await client.getServiceMetadata();
    expect(serviceResponse.status).toBe(200);

    const layers = serviceResponse.data.layers;
    if (layers.length > 0) {
      const firstLayerId = layers[0].id;

      const layerResponse = await client.getLayerMetadata(firstLayerId);
      expect(layerResponse.status).toBe(200);
      expect(layerResponse.data.id).toBe(firstLayerId);
    }
  });

  it('should have matching layer name in service and layer metadata', async () => {
    const serviceResponse = await client.getServiceMetadata();
    expect(serviceResponse.status).toBe(200);

    const layers = serviceResponse.data.layers;
    if (layers.length > 0) {
      const firstLayer = layers[0];

      const layerResponse = await client.getLayerMetadata(firstLayer.id);
      expect(layerResponse.status).toBe(200);
      expect(layerResponse.data.name).toBe(firstLayer.name);
    }
  });

  it('should return metadata for all layers listed in service', async () => {
    const serviceResponse = await client.getServiceMetadata();
    expect(serviceResponse.status).toBe(200);

    for (const layer of serviceResponse.data.layers) {
      const layerResponse = await client.getLayerMetadata(layer.id);
      expect(layerResponse.status).toBe(200);
      expect(layerResponse.data.id).toBe(layer.id);
    }
  });
});

// =============================================================================
// Format Parameter Tests
// =============================================================================

describe('Format Parameter', () => {
  describe('Service Metadata Formats', () => {
    it('should return JSON with f=json', async () => {
      const response = await client.getServiceMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toBeDefined();
      expect(response.data.layers).toBeDefined();
    });
  });

  describe('Layer Metadata Formats', () => {
    it('should return JSON with f=json', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);
      expect(response.data).toBeDefined();
      expect(response.data.id).toBeDefined();
    });
  });
});

// =============================================================================
// Advanced Layer Properties
// =============================================================================

describe('Advanced Layer Properties', () => {
  describe('Editing Capabilities', () => {
    it('should declare hasAttachments property', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      // hasAttachments is optional
      if ('hasAttachments' in response.data) {
        expect(typeof (response.data as any).hasAttachments).toBe('boolean');
      }
    });

    it('should declare supportsQuery property', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      // If capabilities includes Query, this should be true
      const capabilities = response.data.capabilities;
      const supportsQuery = Array.isArray(capabilities)
        ? capabilities.some((capability) => capability.toLowerCase().includes('query'))
        : (capabilities ?? '').toLowerCase().includes('query');
      expect(supportsQuery).toBe(true);
    });
  });

  describe('Query Limits', () => {
    it('should declare maxRecordCount if present', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      // maxRecordCount is optional but common
      if ('maxRecordCount' in response.data) {
        expect(typeof (response.data as any).maxRecordCount).toBe('number');
        expect((response.data as any).maxRecordCount).toBeGreaterThan(0);
      }
    });
  });

  describe('Relationships', () => {
    it('should include relationships array if present', async () => {
      const response = await client.getLayerMetadata();
      expect(response.status).toBe(200);

      // relationships is optional
      if ('relationships' in response.data) {
        expect(Array.isArray((response.data as any).relationships)).toBe(true);
      }
    });
  });
});

// =============================================================================
// Performance Tests
// =============================================================================

describe('Metadata Performance', () => {
  it('should return service metadata quickly', async () => {
    const start = Date.now();
    const response = await client.getServiceMetadata();
    const duration = Date.now() - start;

    expect(response.status).toBe(200);
    // Should complete within 5 seconds
    expect(duration).toBeLessThan(5000);
  });

  it('should return layer metadata quickly', async () => {
    const start = Date.now();
    const response = await client.getLayerMetadata();
    const duration = Date.now() - start;

    expect(response.status).toBe(200);
    // Should complete within 5 seconds
    expect(duration).toBeLessThan(5000);
  });
});
