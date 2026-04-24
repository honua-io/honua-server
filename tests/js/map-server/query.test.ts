/**
 * Tests for GeoServices REST MapServer query endpoints.
 *
 * Endpoints:
 * - GET /rest/services/{serviceId}/MapServer/query
 * - POST /rest/services/{serviceId}/MapServer/query
 */

import { describe, expect, it } from 'vitest';
import { config } from '../shared/client';

interface MapServerQueryResponse {
  features?: Array<{
    attributes?: Record<string, unknown>;
    geometry?: Record<string, unknown> | null;
  }>;
}

describe('MapServer Query', () => {
  it('should return features for GET service queries with layerId', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/MapServer/query?layerId=${config.layerId}&where=1%3D1&f=json`,
      {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
      },
    );

    expect(response.status).toBe(200);

    const data = await response.json() as MapServerQueryResponse;
    expect(Array.isArray(data.features)).toBe(true);
    expect((data.features ?? []).length).toBeGreaterThan(0);
    expect(data.features?.[0]?.attributes).toBeTruthy();
  });

  it('should return features for POST service queries with layerId', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/MapServer/query`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          Accept: 'application/json',
        },
        body: `layerId=${config.layerId}&where=1%3D1&f=json`,
      },
    );

    expect(response.status).toBe(200);

    const data = await response.json() as MapServerQueryResponse;
    expect(Array.isArray(data.features)).toBe(true);
    expect((data.features ?? []).length).toBeGreaterThan(0);
    expect(data.features?.[0]?.attributes).toBeTruthy();
  });

  it('should reject malformed layers delimiters', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/MapServer/query?layers=${config.layerId}%2C&where=1%3D1&f=json`,
      {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
      },
    );

    expect(response.status).toBe(400);
  });
});
