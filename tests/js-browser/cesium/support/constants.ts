// Shared constants for Cesium browser compatibility specs.
// Seed data: tests/seed/browser-compat.yaml (browser_compat service, layers 2000-2002).

/** Base URL of the Honua server under test. */
export const BASE_URL = process.env.HONUA_BASE_URL ?? 'http://localhost:5000';

/** Browser-compat service identifier. */
export const SERVICE_NAME = 'browser_compat';

/** Layer IDs provisioned by browser-compat.yaml. */
export const POINT_LAYER_ID = 2000;
export const POLYGON_LAYER_ID = 2002;

/** Seed feature centers (San Francisco area). */
export const POINT_CENTER: [number, number] = [-122.4194, 37.7749];

/** Default seeded extent in EPSG:4326 (lon,lat,lon,lat). */
export const SEED_BBOX_4326 = '-122.44,37.76,-122.40,37.79';

/** Default seeded extent in EPSG:3857 meters. */
export const SEED_BBOX_3857 = '-13629760,4544000,-13625300,4548200';
