/**
 * HTTP client utilities for Esri Feature Server tests.
 *
 * Provides typed request/response handling for FeatureServer endpoints.
 */

import type { FeatureCollection, Geometry } from 'geojson';

// =============================================================================
// Configuration
// =============================================================================

/**
 * Test configuration from environment variables.
 */
export const config = {
  baseUrl: process.env.HONUA_BASE_URL || 'http://localhost:5555',
  serviceId: process.env.HONUA_SERVICE_ID || 'test_service_gw0',
  layerId: parseInt(process.env.HONUA_LAYER_ID || '1000', 10),
  timeout: parseInt(process.env.HONUA_TEST_TIMEOUT || '30000', 10),
};

// =============================================================================
// Types
// =============================================================================

/** Esri feature with attributes and geometry */
export interface EsriFeature {
  attributes: Record<string, unknown>;
  geometry?: Record<string, unknown> | null;
}

/** Esri FeatureSet response */
export interface EsriFeatureSet {
  objectIdFieldName?: string;
  globalIdFieldName?: string;
  geometryType?: string;
  spatialReference?: { wkid: number; latestWkid?: number };
  fields?: Array<{
    name: string;
    type: string;
    alias?: string;
    length?: number;
    editable?: boolean;
    nullable?: boolean;
  }>;
  features: EsriFeature[];
  exceededTransferLimit?: boolean;
}

/** GeoJSON FeatureCollection response */
export type GeoJsonFeatureCollection = FeatureCollection<Geometry | null, Record<string, unknown>>;

/** ApplyEdits result for a single operation */
export interface EditResult {
  objectId?: number;
  globalId?: string;
  success: boolean;
  error?: {
    code: number;
    description: string;
  };
}

/** ApplyEdits response */
export interface ApplyEditsResponse {
  addResults?: EditResult[];
  updateResults?: EditResult[];
  deleteResults?: EditResult[];
}

/** Layer metadata response */
export interface LayerMetadata {
  id: number;
  name: string;
  type?: string;
  geometryType?: string;
  fields?: Array<{
    name: string;
    type: string;
    alias?: string;
  }>;
  extent?: {
    xmin: number;
    ymin: number;
    xmax: number;
    ymax: number;
    spatialReference?: { wkid: number };
  } | null;
  capabilities?: string | string[];
  spatialReference?: { wkid: number };
}

/** Service metadata response */
export interface ServiceMetadata {
  currentVersion?: number;
  serviceDescription?: string;
  layers: Array<{
    id: number;
    name: string;
  }>;
  tables?: Array<{
    id: number;
    name: string;
  }>;
}

/** Query parameters for feature queries */
export interface QueryParams {
  where?: string;
  geometry?: string;
  geometryType?: string;
  spatialRel?: string;
  inSR?: string | number;
  outSR?: string | number;
  outFields?: string;
  returnGeometry?: boolean | string;
  returnCountOnly?: boolean | string;
  returnIdsOnly?: boolean | string;
  returnExtentOnly?: boolean | string;
  resultOffset?: number;
  resultRecordCount?: number;
  orderByFields?: string;
  distance?: number;
  units?: string;
  f?: 'json' | 'geojson';
}

// =============================================================================
// HTTP Client
// =============================================================================

/**
 * HTTP client for Esri Feature Server endpoints.
 */
export class FeatureServerClient {
  private readonly baseUrl: string;
  private readonly serviceId: string;
  private readonly layerId: number;
  private readonly timeout: number;

  constructor(options?: {
    baseUrl?: string;
    serviceId?: string;
    layerId?: number;
    timeout?: number;
  }) {
    this.baseUrl = options?.baseUrl ?? config.baseUrl;
    this.serviceId = options?.serviceId ?? config.serviceId;
    this.layerId = options?.layerId ?? config.layerId;
    this.timeout = options?.timeout ?? config.timeout;
  }

  /**
   * Get the service base URL.
   */
  private serviceUrl(): string {
    return `${this.baseUrl}/rest/services/${this.serviceId}/FeatureServer`;
  }

  /**
   * Get the layer URL.
   */
  private layerUrl(layerId?: number): string {
    return `${this.serviceUrl()}/${layerId ?? this.layerId}`;
  }

  /**
   * Make a GET request.
   */
  async get<T>(url: string, params?: Record<string, unknown>): Promise<{ status: number; data: T }> {
    const searchParams = new URLSearchParams();
    if (params) {
      for (const [key, value] of Object.entries(params)) {
        if (value !== undefined && value !== null) {
          searchParams.set(key, String(value));
        }
      }
    }

    const fullUrl = searchParams.toString() ? `${url}?${searchParams}` : url;
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(fullUrl, {
        method: 'GET',
        headers: {
          Accept: 'application/json',
        },
        signal: controller.signal,
      });

      const data = await response.json() as T;
      return { status: response.status, data };
    } finally {
      clearTimeout(timeoutId);
    }
  }

  /**
   * Make a POST request with form data.
   */
  async post<T>(url: string, data?: Record<string, unknown>): Promise<{ status: number; data: T }> {
    const body = new URLSearchParams();
    if (data) {
      for (const [key, value] of Object.entries(data)) {
        if (value !== undefined && value !== null) {
          body.set(key, typeof value === 'object' ? JSON.stringify(value) : String(value));
        }
      }
    }

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.timeout);

    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          Accept: 'application/json',
        },
        body: body.toString(),
        signal: controller.signal,
      });

      const responseData = await response.json() as T;
      return { status: response.status, data: responseData };
    } finally {
      clearTimeout(timeoutId);
    }
  }

  // ===========================================================================
  // Service Metadata
  // ===========================================================================

  /**
   * Get service metadata.
   */
  async getServiceMetadata(): Promise<{ status: number; data: ServiceMetadata }> {
    return this.get<ServiceMetadata>(this.serviceUrl(), { f: 'json' });
  }

  /**
   * Get layer metadata.
   */
  async getLayerMetadata(layerId?: number): Promise<{ status: number; data: LayerMetadata }> {
    return this.get<LayerMetadata>(this.layerUrl(layerId), { f: 'json' });
  }

  // ===========================================================================
  // Query
  // ===========================================================================

  /**
   * Query features with GET.
   */
  async query(params: QueryParams = {}, layerId?: number): Promise<{ status: number; data: EsriFeatureSet }> {
    const url = `${this.layerUrl(layerId)}/query`;
    const queryParams: Record<string, unknown> = {
      f: params.f ?? 'json',
      ...params,
    };
    // Convert booleans to strings for query params
    if (typeof queryParams.returnGeometry === 'boolean') {
      queryParams.returnGeometry = queryParams.returnGeometry.toString();
    }
    if (typeof queryParams.returnCountOnly === 'boolean') {
      queryParams.returnCountOnly = queryParams.returnCountOnly.toString();
    }
    if (typeof queryParams.returnIdsOnly === 'boolean') {
      queryParams.returnIdsOnly = queryParams.returnIdsOnly.toString();
    }
    if (typeof queryParams.returnExtentOnly === 'boolean') {
      queryParams.returnExtentOnly = queryParams.returnExtentOnly.toString();
    }
    return this.get<EsriFeatureSet>(url, queryParams);
  }

  /**
   * Query features with POST.
   */
  async queryPost(params: QueryParams = {}, layerId?: number): Promise<{ status: number; data: EsriFeatureSet }> {
    const url = `${this.layerUrl(layerId)}/query`;
    const postParams: Record<string, unknown> = {
      f: params.f ?? 'json',
      ...params,
    };
    return this.post<EsriFeatureSet>(url, postParams);
  }

  /**
   * Query features and return GeoJSON.
   */
  async queryGeoJson(params: QueryParams = {}, layerId?: number): Promise<{ status: number; data: GeoJsonFeatureCollection }> {
    const url = `${this.layerUrl(layerId)}/query`;
    return this.get<GeoJsonFeatureCollection>(url, { ...params, f: 'geojson' });
  }

  // ===========================================================================
  // ApplyEdits
  // ===========================================================================

  /**
   * Apply edits to features.
   */
  async applyEdits(
    options: {
      adds?: Array<{ geometry?: unknown; attributes: Record<string, unknown> }>;
      updates?: Array<{ geometry?: unknown; attributes: Record<string, unknown> }>;
      deletes?: number[];
    },
    layerId?: number,
  ): Promise<{ status: number; data: ApplyEditsResponse }> {
    const url = `${this.layerUrl(layerId)}/applyEdits`;
    const data: Record<string, unknown> = { f: 'json' };

    if (options.adds) {
      data.adds = options.adds;
    }
    if (options.updates) {
      data.updates = options.updates;
    }
    if (options.deletes) {
      data.deletes = options.deletes;
    }

    return this.post<ApplyEditsResponse>(url, data);
  }

  /**
   * Add a single feature and return its object ID.
   */
  async addFeature(
    geometry: unknown,
    attributes: Record<string, unknown>,
    layerId?: number,
  ): Promise<number | null> {
    const { status, data } = await this.applyEdits(
      { adds: [{ geometry, attributes }] },
      layerId,
    );

    if (status !== 200) return null;
    const result = data.addResults?.[0];
    if (!result?.success) return null;
    return result.objectId ?? null;
  }

  /**
   * Delete features by object IDs.
   */
  async deleteFeatures(
    objectIds: number[],
    layerId?: number,
  ): Promise<{ status: number; data: ApplyEditsResponse }> {
    return this.applyEdits({ deletes: objectIds }, layerId);
  }
}

// =============================================================================
// Singleton Instance
// =============================================================================

/** Default client instance */
export const client = new FeatureServerClient();

// =============================================================================
// Assertion Helpers
// =============================================================================

/**
 * Assert that a response is a valid Esri FeatureSet.
 */
export function assertEsriFeatureSet(response: { status: number; data: unknown }): EsriFeatureSet {
  expect(response.status).toBe(200);
  const data = response.data as EsriFeatureSet;
  expect(data).toHaveProperty('features');
  expect(Array.isArray(data.features)).toBe(true);
  return data;
}

/**
 * Assert that a response is a valid GeoJSON FeatureCollection.
 */
export function assertGeoJsonFeatureCollection(
  response: { status: number; data: unknown },
): GeoJsonFeatureCollection {
  expect(response.status).toBe(200);
  const data = response.data as GeoJsonFeatureCollection;
  expect(data.type).toBe('FeatureCollection');
  expect(Array.isArray(data.features)).toBe(true);
  return data;
}

/**
 * Assert that an edit operation succeeded.
 */
export function assertEditSuccess(result: EditResult): void {
  expect(result.success).toBe(true);
  expect(result.objectId).toBeDefined();
}

/**
 * Assert that a response is an error.
 */
export function assertError(response: { status: number; data: unknown }, expectedStatus?: number): void {
  if (expectedStatus) {
    expect(response.status).toBe(expectedStatus);
  } else {
    expect(response.status).toBeGreaterThanOrEqual(400);
  }
}
