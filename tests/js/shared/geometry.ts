/**
 * Geometry generator for comprehensive spatial testing.
 *
 * Supports all GeoJSON geometry types with conversion to Esri JSON format.
 * Mirrors the Python GeometryGenerator for test consistency.
 */

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Geometry, Point, MultiPoint, LineString, MultiLineString, Position } from 'geojson';

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

interface GeometryFixture {
  defaultName: string;
  geometryType: string;
  hasHoles: boolean;
  isMulti: boolean;
  isNull: boolean;
  geojson: Geometry | null;
}

interface GeometryFixtures {
  base: { lon: number; lat: number };
  geometries: Record<string, GeometryFixture>;
}

const fixturesPath = resolve(
  dirname(fileURLToPath(import.meta.url)),
  '../../fixtures/geometry-fixtures.json',
);
const geometryFixtures = JSON.parse(readFileSync(fixturesPath, 'utf8')) as GeometryFixtures;

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
  private readonly baseLon = geometryFixtures.base.lon;
  private readonly baseLat = geometryFixtures.base.lat;

  private fromFixture(key: string, nameOverride?: string): TestGeometry {
    const fixture = geometryFixtures.geometries[key];
    if (!fixture) {
      throw new Error(`Unknown geometry fixture: ${key}`);
    }

    const name = nameOverride ?? fixture.defaultName;
    const geojson = fixture.geojson as Geometry | null;

    return {
      name,
      geojson,
      esriJson: geojsonToEsri(geojson),
      geometryType: fixture.geometryType,
      hasHoles: fixture.hasHoles,
      isMulti: fixture.isMulti,
      isNull: fixture.isNull,
    };
  }

  /**
   * Generate a Point geometry.
   */
  point(name = 'test_point', lon?: number, lat?: number): TestGeometry {
    if (lon === undefined && lat === undefined) {
      return this.fromFixture('point', name);
    }

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
    if (count === 3) {
      return this.fromFixture('multipoint', name);
    }

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
    if (pointCount === 4) {
      return this.fromFixture('linestring', name);
    }

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
    if (lineCount === 2) {
      return this.fromFixture('multilinestring', name);
    }

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
    return this.fromFixture('polygonSimple', name);
  }

  /**
   * Generate a Polygon with one hole.
   */
  polygonWithHole(name = 'test_polygon_with_hole'): TestGeometry {
    return this.fromFixture('polygonWithHole', name);
  }

  /**
   * Generate a Polygon with multiple holes.
   */
  polygonWithMultipleHoles(name = 'test_polygon_multiple_holes'): TestGeometry {
    return this.fromFixture('polygonWithMultipleHoles', name);
  }

  /**
   * Generate a MultiPolygon without holes.
   */
  multipolygonSimple(name = 'test_multipolygon_simple'): TestGeometry {
    return this.fromFixture('multipolygonSimple', name);
  }

  /**
   * Generate a MultiPolygon with holes in some polygons.
   */
  multipolygonWithHoles(name = 'test_multipolygon_with_holes'): TestGeometry {
    return this.fromFixture('multipolygonWithHoles', name);
  }

  /**
   * Generate a GeometryCollection with mixed geometry types.
   */
  geometryCollection(name = 'test_geometry_collection'): TestGeometry {
    return this.fromFixture('geometryCollection', name);
  }

  /**
   * Generate a null geometry representation.
   */
  nullGeometry(name = 'test_null_geometry'): TestGeometry {
    return this.fromFixture('nullGeometry', name);
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
