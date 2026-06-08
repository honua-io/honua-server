/**
 * Test constants for Esri Feature Server tests.
 *
 * Defines spatial relationships, geometry types, distance units,
 * and other constants used in matrix testing.
 */

// =============================================================================
// Spatial Relationships
// =============================================================================

/** Non-distance spatial relationships supported by Esri */
export const NON_DISTANCE_SPATIAL_RELS = [
  'esriSpatialRelIntersects',
  'esriSpatialRelContains',
  'esriSpatialRelWithin',
  'esriSpatialRelEnvelopeIntersects',
  'esriSpatialRelCrosses',
  'esriSpatialRelTouches',
  'esriSpatialRelOverlaps',
  'esriSpatialRelDisjoint',
  'esriSpatialRelEquals',
] as const;

/** Distance-based spatial relationships */
export const DISTANCE_SPATIAL_RELS = [
  'esriSpatialRelWithinDistance',
  'esriSpatialRelBeyondDistance',
] as const;

/** All spatial relationships */
export const ALL_SPATIAL_RELS = [
  ...NON_DISTANCE_SPATIAL_RELS,
  ...DISTANCE_SPATIAL_RELS,
] as const;

// =============================================================================
// Distance Units
// =============================================================================

/** Distance units for spatial queries */
export const DISTANCE_UNITS = [
  'esriSRUnit_Meter',
  'esriSRUnit_Foot',
  'esriSRUnit_Kilometer',
  'esriSRUnit_StatuteMile',
] as const;

// =============================================================================
// Geometry Types
// =============================================================================

/** Mapping of geometry method to Esri geometry type */
export const GEOMETRY_TYPE_CASES = [
  { method: 'point', esriType: 'esriGeometryPoint' },
  { method: 'multipoint', esriType: 'esriGeometryMultipoint' },
  { method: 'linestring', esriType: 'esriGeometryPolyline' },
  { method: 'multilinestring', esriType: 'esriGeometryPolyline' },
  { method: 'polygonSimple', esriType: 'esriGeometryPolygon' },
  { method: 'polygonWithHole', esriType: 'esriGeometryPolygon' },
  { method: 'polygonWithMultipleHoles', esriType: 'esriGeometryPolygon' },
  { method: 'multipolygonSimple', esriType: 'esriGeometryPolygon' },
  { method: 'multipolygonWithHoles', esriType: 'esriGeometryPolygon' },
] as const;

/** All geometry methods for roundtrip testing */
export const ALL_GEOMETRY_METHODS = [
  'point',
  'multipoint',
  'linestring',
  'multilinestring',
  'polygonSimple',
  'polygonWithHole',
  'polygonWithMultipleHoles',
  'multipolygonSimple',
  'multipolygonWithHoles',
  'geometryCollection',
  'nullGeometry',
] as const;

// =============================================================================
// Esri Geometry Types
// =============================================================================

/** Valid Esri geometry types for layer metadata */
export const VALID_ESRI_GEOMETRY_TYPES = [
  'esriGeometryPoint',
  'esriGeometryMultipoint',
  'esriGeometryPolyline',
  'esriGeometryPolygon',
  'esriGeometryEnvelope',
  // Honua reports Mixed / None / GeometryCollection layers' geometryType as esriGeometryNull.
  'esriGeometryNull',
] as const;

// =============================================================================
// WHERE Clause Test Cases
// =============================================================================

/** Test cases for WHERE clause operators */
export const WHERE_CLAUSE_CASES = [
  { name: 'equals', where: "name = 'test'" },
  { name: 'notEquals', where: "name <> 'excluded'" },
  { name: 'lessThan', where: 'count < 100' },
  { name: 'greaterThan', where: 'count > 0' },
  { name: 'lessThanOrEqual', where: 'count <= 100' },
  { name: 'greaterThanOrEqual', where: 'count >= 0' },
  { name: 'like', where: "name LIKE 'test%'" },
  { name: 'in', where: "name IN ('test1', 'test2')" },
  { name: 'between', where: 'count BETWEEN 1 AND 100' },
  { name: 'isNull', where: 'description IS NULL' },
  { name: 'isNotNull', where: 'name IS NOT NULL' },
  { name: 'and', where: 'count > 0 AND name IS NOT NULL' },
  { name: 'or', where: "name = 'a' OR name = 'b'" },
  { name: 'not', where: "NOT (name = 'excluded')" },
  { name: 'parentheses', where: "(name = 'a' OR name = 'b') AND count > 0" },
  { name: 'allRows', where: '1=1' },
] as const;

/** Invalid WHERE clause test cases */
export const INVALID_WHERE_CASES = [
  { name: 'invalidSyntax', where: 'invalid !!! syntax' },
  { name: 'unclosedParens', where: '(name = "test"' },
  { name: 'unknownOperator', where: 'name === "test"' },
] as const;

// =============================================================================
// Output Format Cases
// =============================================================================

/** Response format options */
export const OUTPUT_FORMATS = ['json', 'geojson'] as const;

// =============================================================================
// Spatial Reference Cases
// =============================================================================

/** Common spatial references for testing */
export const SPATIAL_REFERENCES = [
  { wkid: 4326, name: 'WGS84' },
  { wkid: 3857, name: 'Web Mercator' },
] as const;

// =============================================================================
// Pagination Cases
// =============================================================================

/** Pagination test parameters */
export const PAGINATION_CASES = [
  { offset: 0, count: 1 },
  { offset: 0, count: 5 },
  { offset: 0, count: 10 },
  { offset: 5, count: 5 },
  { offset: 10, count: 10 },
] as const;

// =============================================================================
// Type Exports
// =============================================================================

export type SpatialRel = typeof ALL_SPATIAL_RELS[number];
export type NonDistanceSpatialRel = typeof NON_DISTANCE_SPATIAL_RELS[number];
export type DistanceSpatialRel = typeof DISTANCE_SPATIAL_RELS[number];
export type DistanceUnit = typeof DISTANCE_UNITS[number];
export type GeometryMethod = typeof ALL_GEOMETRY_METHODS[number];
export type OutputFormat = typeof OUTPUT_FORMATS[number];
export type EsriGeometryType = typeof VALID_ESRI_GEOMETRY_TYPES[number];
