/** Independent coordinates from tests/seed/browser-compat.yaml, never from server output. */
type Position = readonly [number, number];
type GeometryType = 'Point' | 'LineString' | 'Polygon';
interface AuthoredFeature { name: string; type: GeometryType; positions: readonly Position[] }
export interface DecodedFeature {
  name: unknown;
  layer: unknown;
  type: string;
  flatCoordinates: readonly number[];
  ends: readonly number[] | null;
}

// All authored vertices are strictly inside this WebMercatorQuad tile, so this
// proof concerns the transform/geometry, without deriving clipping from the SUT.
export const FIXTURE_TILE = { z: 12, x: 655, y: 1583, extent: 4096 } as const;
// PostGIS quantizes MVT vertices to integer tile units. One unit covers rounding
// at either edge of a grid cell (~2.39m at z12); it does not permit a pixel shift.
export const QUANTIZATION_TOLERANCE = 1;
export const AUTHORED_FEATURES: Readonly<Record<string, readonly AuthoredFeature[]>> = {
  '2000': [
    { name: 'pt-alpha', type: 'Point', positions: [[-122.4194, 37.7749]] },
    { name: 'pt-beta', type: 'Point', positions: [[-122.4180, 37.7760]] },
    { name: 'pt-gamma', type: 'Point', positions: [[-122.4210, 37.7735]] },
  ],
  '2001': [
    { name: 'line-alpha', type: 'LineString', positions: [[-122.4300, 37.7700], [-122.4100, 37.7800]] },
    { name: 'line-beta', type: 'LineString', positions: [[-122.4250, 37.7750], [-122.4050, 37.7850]] },
  ],
  '2002': [
    { name: 'poly-alpha', type: 'Polygon', positions: [[-122.4250, 37.7700], [-122.4150, 37.7700], [-122.4150, 37.7800], [-122.4250, 37.7800]] },
    { name: 'poly-beta', type: 'Polygon', positions: [[-122.4200, 37.7720], [-122.4100, 37.7720], [-122.4100, 37.7780], [-122.4200, 37.7780]] },
  ],
};

/** Slippy-map formula; tile origin is upper-left and MVT Y increases downward. */
export function tilePosition([lon, lat]: Position, tile: { z: number; x: number; y: number; extent: number } = FIXTURE_TILE): Position {
  const scale = 2 ** tile.z;
  const latitude = lat * Math.PI / 180;
  return [
    ((lon + 180) / 360 * scale - tile.x) * tile.extent,
    ((1 - Math.asinh(Math.tan(latitude)) / Math.PI) / 2 * scale - tile.y) * tile.extent,
  ];
}

function close(a: Position, b: Position): boolean {
  return a.every((value, axis) => Number.isFinite(b[axis]) && Math.abs(value - b[axis]) <= QUANTIZATION_TOLERANCE);
}

function sequenceMatches(expected: readonly Position[], actual: readonly Position[], ring: boolean): boolean {
  const offsets = ring ? expected.map((_, i) => i) : [0];
  return [false, true].some(reverse => {
    const sequence = reverse ? [...actual].reverse() : actual;
    return offsets.some(offset => expected.every((position, i) => close(position, sequence[(i + offset) % sequence.length])));
  });
}

/** Require exact feature membership and connected geometry, allowing ring rotation/winding only. */
export function assertFixtureGeometry(collection: string, observed: readonly DecodedFeature[]): void {
  const authored = AUTHORED_FEATURES[collection];
  if (!authored) throw new Error(`Unknown authored collection ${collection}`);
  if (observed.length !== authored.length || new Set(observed.map(feature => feature.name)).size !== authored.length) {
    throw new Error(`Collection ${collection}: missing, extra or duplicate features`);
  }
  for (const expected of authored) {
    const actual = observed.find(feature => feature.name === expected.name);
    if (!actual || actual.layer !== 'layer' || actual.type !== expected.type) {
      throw new Error(`${expected.name}: wrong feature name, source layer or geometry type`);
    }
    const ring = expected.type === 'Polygon';
    const count = expected.positions.length + (ring ? 1 : 0);
    if (actual.flatCoordinates.length !== count * 2 || actual.ends?.length !== 1 || actual.ends[0] !== count * 2) {
      throw new Error(`${expected.name}: wrong vertex count or part/ring connectivity`);
    }
    const coordinates: Position[] = [];
    for (let i = 0; i < actual.flatCoordinates.length; i += 2) {
      coordinates.push([actual.flatCoordinates[i], actual.flatCoordinates[i + 1]]);
    }
    if (ring && !coordinates.pop()!.every((value, axis) => value === coordinates[0][axis])) {
      throw new Error(`${expected.name}: polygon ring is not closed`);
    }
    const positions = expected.positions.map(position => tilePosition(position));
    if (positions.some(position => position.some(value => value <= 0 || value >= FIXTURE_TILE.extent))) {
      throw new Error(`${expected.name}: authored fixture escaped the unclipped proof tile`);
    }
    if (!sequenceMatches(positions, coordinates, ring)) {
      throw new Error(`${expected.name}: decoded tile coordinates differ from authored vertices (tolerance ${QUANTIZATION_TOLERANCE} MVT unit)`);
    }
  }
}
