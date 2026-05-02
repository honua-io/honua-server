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

// 3D Tiles fixture (committed by #837 under tests/fixtures/scenes/fixture-tileset).
// Honua serves the tileset at /scenes/{SCENE_ID}/tileset.json when its
// SceneDataset configuration binds Id=SCENE_ID to the fixture asset root.

/** Public 3D Tiles fixture scene id. */
export const SCENE_ID = process.env.HONUA_SCENE_FIXTURE_ID ?? 'fixture-tileset';

/** Origin used to validate Honua's CORS configuration. The honua server under
 *  test must include this value in Cors:AllowedOrigins or the CORS test will
 *  skip itself. */
export const CORS_TEST_ORIGIN = process.env.HONUA_CORS_TEST_ORIGIN
  ?? 'http://cesium-test.honua.local';
