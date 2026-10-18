import { describe, it, expect } from 'vitest';
import MVT from 'ol/format/MVT.js';
import { FIXTURE_MVT_HEX } from './fixture-geometry-mvt.js';
import { AUTHORED_FEATURES, assertFixtureGeometry, FIXTURE_TILE, tilePosition, type DecodedFeature } from './fixture-geometry-oracle.js';

/** Actual canonical decode of fixed spec bytes, without extent/projection options. */
function decodedFixture(collection: string): DecodedFeature[] {
  const bytes = Uint8Array.from(FIXTURE_MVT_HEX[collection].match(/../g)!.map(value => parseInt(value, 16)));
  return new MVT().readFeatures(bytes.buffer).map(feature => ({
    name: feature.get('name'), layer: feature.get('layer'), type: feature.getType(),
    flatCoordinates: feature.getFlatCoordinates(), ends: feature.getEnds(),
  }));
}

const collections = Object.keys(AUTHORED_FEATURES);
describe('authored canonical MVT coordinate oracle', () => {
  it('pins actual OpenLayers Point ends and raw tile coordinates from independent bytes', () => {
    const point = decodedFixture('2000')[0];
    expect(point).toMatchObject({ name: 'pt-alpha', layer: 'layer', type: 'Point',
      flatCoordinates: [570, 777], ends: [2] });
    const lineBeta = decodedFixture('2001')[1];
    expect(lineBeta.name).toBe('line-beta');
    expect(lineBeta.flatCoordinates.slice(-2)).toEqual([1242, 182]);
    expect(tilePosition([-122.405, 37.785]).every(value => value > 0 && value < FIXTURE_TILE.extent)).toBe(true);
  });
  it('pins the independent projection origin, east edge and northward Y orientation', () => {
    const world = { z: 0, x: 0, y: 0, extent: 4096 };
    expect(tilePosition([0, 0], world)).toEqual([2048, 2048]);
    expect(tilePosition([180, 0], world)).toEqual([4096, 2048]);
    expect(tilePosition([0, 85.0511287798066], world)[1]).toBeCloseTo(0, 8);
    expect(tilePosition([0, -85.0511287798066], world)[1]).toBeCloseTo(4096, 8);
    // Literal point placement is independently pinned, rather than round-tripping the formula.
    const alpha = tilePosition([-122.4194, 37.7749]);
    expect(alpha[0]).toBeCloseTo(570.454, 2);
    expect(alpha[1]).toBeCloseTo(777.242, 2);
  });

  it.each(collections)('accepts integer quantization of the entire collection %s', collection => {
    expect(() => assertFixtureGeometry(collection, decodedFixture(collection))).not.toThrow();
    for (const fixture of AUTHORED_FEATURES[collection]) {
      for (const point of fixture.positions) {
        expect(tilePosition(point).every(value => value > 0 && value < FIXTURE_TILE.extent)).toBe(true);
      }
    }
  });

  it.each(collections)('rejects same-type shifted and axis-swapped coordinates in %s', collection => {
    for (const alter of [(x: number, _axis: number) => x + 16,
      (x: number, axis: number, values: readonly number[], index: number) => values[index + (axis === 0 ? 1 : -1)]]) {
      const changed = decodedFixture(collection).map(feature => ({ ...feature,
        flatCoordinates: feature.flatCoordinates.map((value, index, values) => alter(value, index % 2, values, index)),
      }));
      expect(() => assertFixtureGeometry(collection, changed)).toThrow('decoded tile coordinates');
    }
  });

  it.each(collections)('rejects missing, extra, duplicate and wrong-name features in %s', collection => {
    const good = decodedFixture(collection);
    for (const changed of [good.slice(1), [...good, good[0]], good.map(() => good[0]),
      [{ ...good[0], name: 'foreign-feature' }, ...good.slice(1)]]) {
      expect(() => assertFixtureGeometry(collection, changed)).toThrow();
    }
  });

  it.each(collections)('rejects wrong source/type, nonfinite vertices and split connectivity in %s', collection => {
    for (const mutation of [
      (feature: DecodedFeature) => ({ ...feature, layer: 'foreign-layer' }),
      (feature: DecodedFeature) => ({ ...feature, type: 'MultiPoint' }),
      (feature: DecodedFeature) => ({ ...feature, flatCoordinates: [NaN, ...feature.flatCoordinates.slice(1)] }),
      (feature: DecodedFeature) => ({ ...feature, ends: [2, feature.flatCoordinates.length] }),
      (feature: DecodedFeature) => ({ ...feature, flatCoordinates: feature.flatCoordinates.slice(2) }),
    ]) {
      const changed = decodedFixture(collection);
      changed[0] = mutation(changed[0]);
      expect(() => assertFixtureGeometry(collection, changed)).toThrow();
    }
  });

  it('accepts reverse line direction and cyclic/reversed polygon rings, without accepting a bow tie', () => {
    const lines = decodedFixture('2001').map(feature => ({ ...feature,
      flatCoordinates: [...feature.flatCoordinates.slice(2), ...feature.flatCoordinates.slice(0, 2)],
    }));
    expect(() => assertFixtureGeometry('2001', lines)).not.toThrow();
    for (const reverse of [false, true]) {
      const polygons = decodedFixture('2002').map(feature => {
        const points = Array.from({ length: 4 }, (_, i) => feature.flatCoordinates.slice(i * 2, i * 2 + 2));
        points.push(points.shift()!);
        if (reverse) points.reverse();
        return { ...feature, flatCoordinates: [...points, points[0]].flat() };
      });
      expect(() => assertFixtureGeometry('2002', polygons)).not.toThrow();
    }
    const changed = decodedFixture('2002');
    const vertices = [...changed[0].flatCoordinates];
    [vertices[2], vertices[4]] = [vertices[4], vertices[2]];
    [vertices[3], vertices[5]] = [vertices[5], vertices[3]];
    changed[0] = { ...changed[0], flatCoordinates: vertices };
    expect(() => assertFixtureGeometry('2002', changed)).toThrow('decoded tile coordinates');
  });

  it('rejects an open polygon ring even when all other vertices are correct', () => {
    const changed = decodedFixture('2002');
    const vertices = [...changed[0].flatCoordinates];
    vertices[vertices.length - 1] += 16;
    changed[0] = { ...changed[0], flatCoordinates: vertices };
    expect(() => assertFixtureGeometry('2002', changed)).toThrow('ring is not closed');
  });
});
