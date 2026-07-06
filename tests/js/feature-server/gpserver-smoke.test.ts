/**
 * GPServer smoke tests.
 *
 * Covers the currently shipped GPServer surface:
 * - service root is reachable and lists catalog-backed task names
 * - known task metadata is discoverable on the task route
 */

import { describe, expect, it } from 'vitest';
import { config } from '../shared/client';

const pointWkbBase64 = 'AQEAAAAAAAAAAAAAAAAAAAAAAAAA';

describe('GPServer Smoke', () => {
  it('should expose the catalog-backed service root', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/GPServer?f=json`,
      {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
      },
    );

    expect(response.status).toBe(200);

    const data = await response.json() as {
      capabilities?: string;
      currentVersion?: number;
      executionType?: string;
      resultMapServerName?: string;
      serviceDescription?: string;
      tasks?: unknown[];
    };

    expect(data.capabilities).toBe('');
    // currentVersion is an Esri/ArcGIS field; our SDK tests must not depend on
    // it (the endpoint still emits it for ArcGIS-client compat).
    expect(data.executionType).toBe('esriExecutionTypeAsynchronous');
    expect(data.resultMapServerName).toBe('');
    expect(Array.isArray(data.tasks)).toBe(true);
    expect(data.tasks).toContain('geometry.buffer');
    expect(data.serviceDescription).toContain(config.serviceId);
  });

  it('should expose known task metadata from the process catalog', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/GPServer/geometry.buffer`,
      {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
      },
    );

    expect(response.status).toBe(200);

    const data = await response.json() as {
      category?: string;
      description?: string;
      displayName?: string;
      executionType?: string;
      helpUrl?: string;
      name?: string;
      parameters?: Array<{ name?: string; description?: string; direction?: string }>;
    };

    expect(data.name).toBe('geometry.buffer');
    expect(data.displayName).toBe('Buffer');
    expect(data.description).toContain('Creates a polygon');
    expect(data.category).toBe('geometry');
    expect(data.helpUrl).toBe('');
    expect(data.executionType).toBe('esriExecutionTypeAsynchronous');
    expect(data.parameters?.some(
      parameter => parameter.name === 'distance'
        && parameter.description === 'Buffer distance in meters.'
        && parameter.direction === 'esriGPParameterDirectionInput',
    )).toBe(true);
  });

  it('should accept canonical submitJob inputs before job-store availability is evaluated', async () => {
    const response = await fetch(
      `${config.baseUrl}/rest/services/${config.serviceId}/GPServer/geometry.buffer/submitJob`,
      {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/x-www-form-urlencoded',
          ...(config.apiKey ? { 'X-API-Key': config.apiKey } : {}),
        },
        body: new URLSearchParams({
          f: 'json',
          wkb: pointWkbBase64,
          srid: '4326',
          distance: '10',
        }),
      },
    );

    expect([200, 503]).toContain(response.status);

    const data = await response.json() as {
      error?: { code?: number; details?: string[]; message?: string };
      jobId?: string;
      jobStatus?: string;
    };

    // PA-070/PA-117 (#2418): the job-store-unavailable error is now signalled as
    // HTTP 200 + {"error":{"code":503}} rather than a 503 status, so branch on the
    // body error member instead of the HTTP status.
    if (response.status === 200 && !data.error) {
      expect(data.jobId).toBeTruthy();
      expect(data.jobStatus).toBe('esriJobSubmitted');
      return;
    }

    expect(data.error?.code).toBe(503);
    expect(data.error?.message).toBe('Service Unavailable');
    expect((data.error?.details ?? []).join(' ')).toContain('Redis-backed durable storage');
  });
});
