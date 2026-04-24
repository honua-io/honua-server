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

  it('should reject POST requests with 405', async () => {
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

    expect(response.status).toBe(405);
    expect(response.headers.get('allow') ?? '').toContain('GET');
  });
});
