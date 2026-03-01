import type { EsriGeometryType, EsriSpatialRel, HonuaSpatialReference } from "./types.js";

/**
 * A spatial filter fragment that can be spread into a `QueryFeaturesRequest`
 * or applied via `QueryBuilder.geometry()` / `.geometryType()` / `.spatialRel()`.
 */
export interface SpatialFilter {
  geometry: Record<string, unknown>;
  geometryType: EsriGeometryType;
  spatialRel?: EsriSpatialRel;
}

/**
 * Create an envelope (bounding box) spatial filter.
 *
 * @example
 * ```ts
 * const req: QueryFeaturesRequest = {
 *   serviceId: "svc", layerId: 0,
 *   ...envelope(-118.5, 33.7, -117.5, 34.2),
 * };
 * ```
 */
export function envelope(
  xmin: number,
  ymin: number,
  xmax: number,
  ymax: number,
  spatialReference?: HonuaSpatialReference,
): SpatialFilter {
  const geometry: Record<string, unknown> = { xmin, ymin, xmax, ymax };
  if (spatialReference) geometry.spatialReference = spatialReference;
  return {
    geometry,
    geometryType: "esriGeometryEnvelope",
    spatialRel: "esriSpatialRelIntersects",
  };
}

/**
 * Create a point spatial filter.
 *
 * @example
 * ```ts
 * const req: QueryFeaturesRequest = {
 *   serviceId: "svc", layerId: 0,
 *   ...point(-118.24, 34.05),
 * };
 * ```
 */
export function point(x: number, y: number, spatialReference?: HonuaSpatialReference): SpatialFilter {
  const geometry: Record<string, unknown> = { x, y };
  if (spatialReference) geometry.spatialReference = spatialReference;
  return {
    geometry,
    geometryType: "esriGeometryPoint",
    spatialRel: "esriSpatialRelIntersects",
  };
}

/**
 * Create a polygon spatial filter from an array of rings.
 *
 * @example
 * ```ts
 * const req: QueryFeaturesRequest = {
 *   serviceId: "svc", layerId: 0,
 *   ...polygon([[[-118, 34], [-117, 34], [-117, 35], [-118, 35], [-118, 34]]]),
 * };
 * ```
 */
export function polygon(rings: number[][][], spatialReference?: HonuaSpatialReference): SpatialFilter {
  const geometry: Record<string, unknown> = { rings };
  if (spatialReference) geometry.spatialReference = spatialReference;
  return {
    geometry,
    geometryType: "esriGeometryPolygon",
    spatialRel: "esriSpatialRelIntersects",
  };
}

/**
 * Create an envelope spatial filter centered on a point, approximating a
 * circular buffer as a bounding box.
 *
 * @param x - Center x coordinate
 * @param y - Center y coordinate
 * @param distance - Half-width of the envelope in coordinate units
 * @param spatialReference - Optional spatial reference
 *
 * @example
 * ```ts
 * // 0.5-degree bounding box around a point
 * const req: QueryFeaturesRequest = {
 *   serviceId: "svc", layerId: 0,
 *   ...buffer(-118.24, 34.05, 0.5),
 * };
 * ```
 */
export function buffer(
  x: number,
  y: number,
  distance: number,
  spatialReference?: HonuaSpatialReference,
): SpatialFilter {
  return envelope(x - distance, y - distance, x + distance, y + distance, spatialReference);
}

/**
 * Wrap an existing geometry with `esriSpatialRelIntersects`.
 *
 * @example
 * ```ts
 * const filter = spatialIntersects({ xmin: -180, ymin: -90, xmax: 180, ymax: 90 });
 * ```
 */
export function spatialIntersects(geometry: Record<string, unknown>): SpatialFilter {
  return {
    geometry,
    geometryType: detectGeometryType(geometry),
    spatialRel: "esriSpatialRelIntersects",
  };
}

/**
 * Wrap an existing geometry with `esriSpatialRelContains`.
 *
 * @example
 * ```ts
 * const filter = spatialContains({ rings: [[[-118, 34], [-117, 34], [-117, 35], [-118, 35], [-118, 34]]] });
 * ```
 */
export function spatialContains(geometry: Record<string, unknown>): SpatialFilter {
  return {
    geometry,
    geometryType: detectGeometryType(geometry),
    spatialRel: "esriSpatialRelContains",
  };
}

/**
 * Wrap an existing geometry with `esriSpatialRelWithin`.
 *
 * @example
 * ```ts
 * const filter = spatialWithin({ rings: [[[-118, 34], [-117, 34], [-117, 35], [-118, 35], [-118, 34]]] });
 * ```
 */
export function spatialWithin(geometry: Record<string, unknown>): SpatialFilter {
  return {
    geometry,
    geometryType: detectGeometryType(geometry),
    spatialRel: "esriSpatialRelWithin",
  };
}

/**
 * Detect the Esri geometry type from a plain geometry object by inspecting
 * its shape (duck-typing).
 *
 * @internal
 */
function detectGeometryType(geometry: Record<string, unknown>): EsriGeometryType {
  if ("xmin" in geometry && "ymin" in geometry && "xmax" in geometry && "ymax" in geometry) {
    return "esriGeometryEnvelope";
  }
  if ("rings" in geometry) {
    return "esriGeometryPolygon";
  }
  if ("paths" in geometry) {
    return "esriGeometryPolyline";
  }
  if ("points" in geometry) {
    return "esriGeometryMultipoint";
  }
  if ("x" in geometry && "y" in geometry) {
    return "esriGeometryPoint";
  }
  return "esriGeometryPoint";
}
