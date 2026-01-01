/**
 * Geometry generator for comprehensive spatial testing.
 *
 * Supports all GeoJSON geometry types with conversion to Esri JSON format.
 * Mirrors the Python GeometryGenerator for test consistency.
 */

import type { Feature, Geometry, Point, MultiPoint, LineString, MultiLineString, Polygon, MultiPolygon, GeometryCollection, Position } from '@turf/helpers';

// =============================================================================
// Types
// =============================================================================

/** Esri JSON geometry types */
export interface EsriPoint {
  x: number;
  y: number;
  spatialReference: { wkid: number };
}

export interface EsriMultiPoint {
  points: Position[];
  spatialReference: { wkid: number };
}

export interface EsriPolyline {
  paths: Position[][];
  spatialReference: { wkid: number };
}

export interface EsriPolygon {
  rings: Position[][];
  spatialReference: { wkid: number };
}

export interface EsriEnvelope {
  xmin: number;
  ymin: number;
  xmax: number;
  ymax: number;
  spatialReference: { wkid: number };
}

export type EsriGeometry = EsriPoint | EsriMultiPoint | EsriPolyline | EsriPolygon | EsriEnvelope | null;

/** Test geometry with metadata */
export interface TestGeometry {
  name: string;
  geojson: Geometry | null;
  esriJson: EsriGeometry;
  geometryType: string;
  hasHoles: boolean;
  isMulti: boolean;
  isNull: boolean;
}

// =============================================================================
// GeoJSON to Esri JSON Conversion
// =============================================================================

/**
 * Convert GeoJSON geometry to Esri JSON format.
 */
export function geojsonToEsri(geojson: Geometry | null): EsriGeometry {
  if (!geojson) return null;

  const wkid = 4326;

  switch (geojson.type) {
    case 'Point':
      return {
        x: geojson.coordinates[0],
        y: geojson.coordinates[1],
        spatialReference: { wkid },
      };

    case 'MultiPoint':
      return {
        points: geojson.coordinates,
        spatialReference: { wkid },
      };

    case 'LineString':
      return {
        paths: [geojson.coordinates],
        spatialReference: { wkid },
      };

    case 'MultiLineString':
      return {
        paths: geojson.coordinates,
        spatialReference: { wkid },
      };

    case 'Polygon':
      return {
        rings: geojson.coordinates,
        spatialReference: { wkid },
      };

    case 'MultiPolygon':
      // Flatten all rings from all polygons
      const rings: Position[][] = [];
      for (const polygon of geojson.coordinates) {
        rings.push(...polygon);
      }
      return {
        rings,
        spatialReference: { wkid },
      };

    case 'GeometryCollection':
      // Esri doesn't support GeometryCollection directly
      // Return the first geometry or null
      if (geojson.geometries.length > 0) {
        return geojsonToEsri(geojson.geometries[0] as Geometry);
      }
      return null;

    default:
      return null;
  }
}

// =============================================================================
// Geometry Generator
// =============================================================================

/**
 * Generates test geometries for comprehensive spatial coverage.
 *
 * Usage:
 * ```ts
 * const gen = new GeometryGenerator();
 * const point = gen.point();
 * const allGeoms = gen.allGeometries();
 * ```
 */
export class GeometryGenerator {
  // Base coordinates for test data (San Francisco area)
  private readonly baseLon = -122.4194;
  private readonly baseLat = 37.7749;

  /**
   * Generate a Point geometry.
   */
  point(name = 'test_point', lon?: number, lat?: number): TestGeometry {
    const x = lon ?? this.baseLon;
    const y = lat ?? this.baseLat;
    const geojson: Point = {
      type: 'Point',
      coordinates: [x, y],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'Point',
      hasHoles: false,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a MultiPoint geometry.
   */
  multipoint(name = 'test_multipoint', count = 3): TestGeometry {
    const points: Position[] = [];
    for (let i = 0; i < count; i++) {
      points.push([this.baseLon + i * 0.01, this.baseLat + i * 0.01]);
    }
    const geojson: MultiPoint = {
      type: 'MultiPoint',
      coordinates: points,
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'MultiPoint',
      hasHoles: false,
      isMulti: true,
      isNull: false,
    };
  }

  /**
   * Generate a LineString geometry.
   */
  linestring(name = 'test_linestring', pointCount = 4): TestGeometry {
    const coords: Position[] = [];
    for (let i = 0; i < pointCount; i++) {
      coords.push([this.baseLon + i * 0.01, this.baseLat + i * 0.005]);
    }
    const geojson: LineString = {
      type: 'LineString',
      coordinates: coords,
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'LineString',
      hasHoles: false,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a MultiLineString geometry.
   */
  multilinestring(name = 'test_multilinestring', lineCount = 2): TestGeometry {
    const lines: Position[][] = [];
    for (let lineIdx = 0; lineIdx < lineCount; lineIdx++) {
      const coords: Position[] = [];
      for (let i = 0; i < 3; i++) {
        coords.push([
          this.baseLon + i * 0.01,
          this.baseLat + lineIdx * 0.02 + i * 0.005,
        ]);
      }
      lines.push(coords);
    }
    const geojson: MultiLineString = {
      type: 'MultiLineString',
      coordinates: lines,
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'MultiLineString',
      hasHoles: false,
      isMulti: true,
      isNull: false,
    };
  }

  /**
   * Generate a simple Polygon without holes.
   */
  polygonSimple(name = 'test_polygon_simple'): TestGeometry {
    const exterior: Position[] = [
      [this.baseLon, this.baseLat],
      [this.baseLon + 0.01, this.baseLat],
      [this.baseLon + 0.01, this.baseLat + 0.01],
      [this.baseLon, this.baseLat + 0.01],
      [this.baseLon, this.baseLat], // Close the ring
    ];
    const geojson: Polygon = {
      type: 'Polygon',
      coordinates: [exterior],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'Polygon',
      hasHoles: false,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a Polygon with one hole.
   */
  polygonWithHole(name = 'test_polygon_with_hole'): TestGeometry {
    const exterior: Position[] = [
      [this.baseLon, this.baseLat],
      [this.baseLon + 0.02, this.baseLat],
      [this.baseLon + 0.02, this.baseLat + 0.02],
      [this.baseLon, this.baseLat + 0.02],
      [this.baseLon, this.baseLat],
    ];
    const hole: Position[] = [
      [this.baseLon + 0.005, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.005],
    ];
    const geojson: Polygon = {
      type: 'Polygon',
      coordinates: [exterior, hole],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'Polygon',
      hasHoles: true,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a Polygon with multiple holes.
   */
  polygonWithMultipleHoles(name = 'test_polygon_multiple_holes'): TestGeometry {
    const exterior: Position[] = [
      [this.baseLon, this.baseLat],
      [this.baseLon + 0.04, this.baseLat],
      [this.baseLon + 0.04, this.baseLat + 0.04],
      [this.baseLon, this.baseLat + 0.04],
      [this.baseLon, this.baseLat],
    ];
    const hole1: Position[] = [
      [this.baseLon + 0.005, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.005],
    ];
    const hole2: Position[] = [
      [this.baseLon + 0.025, this.baseLat + 0.025],
      [this.baseLon + 0.035, this.baseLat + 0.025],
      [this.baseLon + 0.035, this.baseLat + 0.035],
      [this.baseLon + 0.025, this.baseLat + 0.035],
      [this.baseLon + 0.025, this.baseLat + 0.025],
    ];
    const geojson: Polygon = {
      type: 'Polygon',
      coordinates: [exterior, hole1, hole2],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'Polygon',
      hasHoles: true,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a MultiPolygon without holes.
   */
  multipolygonSimple(name = 'test_multipolygon_simple'): TestGeometry {
    const poly1: Position[][] = [
      [
        [this.baseLon, this.baseLat],
        [this.baseLon + 0.01, this.baseLat],
        [this.baseLon + 0.01, this.baseLat + 0.01],
        [this.baseLon, this.baseLat + 0.01],
        [this.baseLon, this.baseLat],
      ],
    ];
    const poly2: Position[][] = [
      [
        [this.baseLon + 0.02, this.baseLat],
        [this.baseLon + 0.03, this.baseLat],
        [this.baseLon + 0.03, this.baseLat + 0.01],
        [this.baseLon + 0.02, this.baseLat + 0.01],
        [this.baseLon + 0.02, this.baseLat],
      ],
    ];
    const geojson: MultiPolygon = {
      type: 'MultiPolygon',
      coordinates: [poly1, poly2],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'MultiPolygon',
      hasHoles: false,
      isMulti: true,
      isNull: false,
    };
  }

  /**
   * Generate a MultiPolygon with holes in some polygons.
   */
  multipolygonWithHoles(name = 'test_multipolygon_with_holes'): TestGeometry {
    // First polygon with a hole
    const exterior1: Position[] = [
      [this.baseLon, this.baseLat],
      [this.baseLon + 0.02, this.baseLat],
      [this.baseLon + 0.02, this.baseLat + 0.02],
      [this.baseLon, this.baseLat + 0.02],
      [this.baseLon, this.baseLat],
    ];
    const hole1: Position[] = [
      [this.baseLon + 0.005, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.005],
      [this.baseLon + 0.015, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.015],
      [this.baseLon + 0.005, this.baseLat + 0.005],
    ];
    const poly1: Position[][] = [exterior1, hole1];

    // Second polygon without holes
    const poly2: Position[][] = [
      [
        [this.baseLon + 0.03, this.baseLat],
        [this.baseLon + 0.04, this.baseLat],
        [this.baseLon + 0.04, this.baseLat + 0.01],
        [this.baseLon + 0.03, this.baseLat + 0.01],
        [this.baseLon + 0.03, this.baseLat],
      ],
    ];

    const geojson: MultiPolygon = {
      type: 'MultiPolygon',
      coordinates: [poly1, poly2],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'MultiPolygon',
      hasHoles: true,
      isMulti: true,
      isNull: false,
    };
  }

  /**
   * Generate a GeometryCollection with mixed geometry types.
   */
  geometryCollection(name = 'test_geometry_collection'): TestGeometry {
    const point: Point = {
      type: 'Point',
      coordinates: [this.baseLon, this.baseLat],
    };
    const line: LineString = {
      type: 'LineString',
      coordinates: [
        [this.baseLon + 0.01, this.baseLat],
        [this.baseLon + 0.02, this.baseLat + 0.01],
      ],
    };
    const polygon: Polygon = {
      type: 'Polygon',
      coordinates: [
        [
          [this.baseLon + 0.03, this.baseLat],
          [this.baseLon + 0.04, this.baseLat],
          [this.baseLon + 0.04, this.baseLat + 0.01],
          [this.baseLon + 0.03, this.baseLat + 0.01],
          [this.baseLon + 0.03, this.baseLat],
        ],
      ],
    };
    const geojson: GeometryCollection = {
      type: 'GeometryCollection',
      geometries: [point, line, polygon],
    };
    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: 'GeometryCollection',
      hasHoles: false,
      isMulti: false,
      isNull: false,
    };
  }

  /**
   * Generate a null geometry representation.
   */
  nullGeometry(name = 'test_null_geometry'): TestGeometry {
    return {
      name,
      geojson: null,
      esriJson: null,
      geometryType: 'Null',
      hasHoles: false,
      isMulti: false,
      isNull: true,
    };
  }

  /**
   * Generate all supported geometry types for comprehensive testing.
   */
  allGeometries(): TestGeometry[] {
    return [
      this.point(),
      this.multipoint(),
      this.linestring(),
      this.multilinestring(),
      this.polygonSimple(),
      this.polygonWithHole(),
      this.polygonWithMultipleHoles(),
      this.multipolygonSimple(),
      this.multipolygonWithHoles(),
      this.geometryCollection(),
      this.nullGeometry(),
    ];
  }

  /**
   * Generate a grid of point geometries for pagination testing.
   */
  pointsGrid(
    namePrefix = 'grid',
    rows = 5,
    cols = 5,
    spacing = 0.01,
  ): TestGeometry[] {
    const geometries: TestGeometry[] = [];
    for (let r = 0; r < rows; r++) {
      for (let c = 0; c < cols; c++) {
        const lon = this.baseLon + c * spacing;
        const lat = this.baseLat + r * spacing;
        const name = `${namePrefix}_${r}_${c}`;
        geometries.push(this.point(name, lon, lat));
      }
    }
    return geometries;
  }

  /**
   * Get a bounding box tuple (xmin, ymin, xmax, ymax).
   */
  bbox(
    minLon?: number,
    minLat?: number,
    maxLon?: number,
    maxLat?: number,
  ): [number, number, number, number] {
    return [
      minLon ?? this.baseLon - 0.1,
      minLat ?? this.baseLat - 0.1,
      maxLon ?? this.baseLon + 0.1,
      maxLat ?? this.baseLat + 0.1,
    ];
  }

  /**
   * Get an Esri envelope from a bounding box.
   */
  envelope(
    minLon?: number,
    minLat?: number,
    maxLon?: number,
    maxLat?: number,
  ): EsriEnvelope {
    const [xmin, ymin, xmax, ymax] = this.bbox(minLon, minLat, maxLon, maxLat);
    return {
      xmin,
      ymin,
      xmax,
      ymax,
      spatialReference: { wkid: 4326 },
    };
  }

  /**
   * Get a geometry by method name for matrix testing.
   */
  getByMethod(method: string): TestGeometry {
    const methodMap: Record<string, () => TestGeometry> = {
      point: () => this.point(),
      multipoint: () => this.multipoint(),
      linestring: () => this.linestring(),
      multilinestring: () => this.multilinestring(),
      polygonSimple: () => this.polygonSimple(),
      polygonWithHole: () => this.polygonWithHole(),
      polygonWithMultipleHoles: () => this.polygonWithMultipleHoles(),
      multipolygonSimple: () => this.multipolygonSimple(),
      multipolygonWithHoles: () => this.multipolygonWithHoles(),
      geometryCollection: () => this.geometryCollection(),
      nullGeometry: () => this.nullGeometry(),
    };

    const fn = methodMap[method];
    if (!fn) {
      throw new Error(`Unknown geometry method: ${method}`);
    }
    return fn();
  }
}

// Default export for convenience
export const geometryGenerator = new GeometryGenerator();
