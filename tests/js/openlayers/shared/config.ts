/**
 * Shared configuration for OpenLayers compatibility tests.
 * Reads from the same environment variables set by vitest.global.ts.
 */

export const config = {
  baseUrl: process.env.HONUA_BASE_URL || 'http://localhost:5555',
  serviceId: process.env.HONUA_SERVICE_ID || 'test_service_gw0',
  layerId: process.env.HONUA_LAYER_ID || '0',
  apiKey: process.env.HONUA_API_KEY,
  timeout: parseInt(process.env.HONUA_TEST_TIMEOUT || '30000', 10),
};

/** OGC API Features base path */
export const ogcFeaturesUrl = `${config.baseUrl}/ogc/features`;

/** OGC API Maps base path */
export const ogcMapsUrl = `${config.baseUrl}/ogc/maps`;

/** WFS 2.0 endpoint */
export const wfsUrl = `${config.baseUrl}/wfs`;

/** WMS 1.3.0 endpoint */
export const wmsUrl = `${config.baseUrl}/ogc/services/${encodeURIComponent(config.serviceId)}/wms`;

/** WMTS 1.0.0 endpoint */
export const wmtsUrl = `${config.baseUrl}/ogc/services/${encodeURIComponent(config.serviceId)}/wmts`;

/** OGC API Tiles base path */
export const ogcTilesUrl = `${config.baseUrl}/ogc/tiles`;

/**
 * Discover the first available collection ID from the OGC Features endpoint.
 * Caches the result after first call.
 */
let _cachedCollectionId: string | undefined;

export async function discoverCollectionId(): Promise<string> {
  if (_cachedCollectionId) return _cachedCollectionId;

  const resp = await fetch(`${ogcFeaturesUrl}/collections`);
  if (!resp.ok) throw new Error(`Collections endpoint returned ${resp.status}`);
  const data = await resp.json() as { collections: Array<{ id: string }> };
  if (!data.collections?.length) throw new Error('No collections available');
  _cachedCollectionId =
    data.collections.find(collection => collection.id === config.layerId)?.id ??
    data.collections[0].id;
  return _cachedCollectionId;
}
