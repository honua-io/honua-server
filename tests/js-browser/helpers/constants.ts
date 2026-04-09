// Shared constants for MapLibre browser compatibility specs.
// Seed data: tests/seed/browser-compat.yaml

/** Base URL of the Honua server under test. */
export const BASE_URL = process.env.HONUA_BASE_URL ?? 'http://localhost:5000';

/** Layer IDs provisioned by browser-compat.yaml. */
export const POINT_LAYER_ID = 2000;
export const LINE_LAYER_ID = 2001;
export const POLYGON_LAYER_ID = 2002;

/** Seed feature centers (San Francisco area). */
export const POINT_CENTER: [number, number] = [-122.4194, 37.7749];
export const LINE_CENTER: [number, number] = [-122.4200, 37.7750];
export const POLYGON_CENTER: [number, number] = [-122.4200, 37.7750];
