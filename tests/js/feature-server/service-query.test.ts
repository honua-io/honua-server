/**
 * Tests for the service-level FeatureServer query endpoint.
 */

import { describe, expect, it } from 'vitest';
import { config } from '../shared/client';

describe('Service Query', () => {
  it('should return per-layer results for GET requests', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/FeatureServer/query?where=1%3D1&f=json`,
      {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
      },
    );

    expect(response.status).toBe(200);

    const data = await response.json() as { layers?: Array<{ id: number }> };
    expect(Array.isArray(data.layers)).toBe(true);
    expect((data.layers ?? []).length).toBeGreaterThan(0);
    expect(typeof data.layers?.[0]?.id).toBe('number');
  });

  it('should support POST requests on the service query route', async () => {
    // POST is intentionally supported (#1847/#1825) so Esri clients can submit large
    // layerDefs/layers arrays that exceed URL length limits — it returns 200, not 405.
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/FeatureServer/query`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          Accept: 'application/json',
        },
        body: 'where=1%3D1&f=json',
      },
    );

    expect(response.status).toBe(200);
  });
});
