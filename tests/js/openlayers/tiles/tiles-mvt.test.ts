/**
 * MVT tile fetch and decode tests via OpenLayers MVT format.
 *
 * Exercises: binary MVT tile fetch from OGC Tiles endpoint,
 * decoding via ol/format/MVT, and feature property access.
 */

import MVT from 'ol/format/MVT.js';
import { ogcTilesUrl, discoverCollectionId } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('mvt');
let collectionId: string;
let tmsId: string;

interface DiscoveredTile {
  tileMatrix: string;
  tileRow: number;
  tileCol: number;
  byteLength: number;
  status: number;
}

/**
 * Cache the first non-empty tile we find so each test in this file pays the
 * scan cost at most once. Reset between suites because `collectionId`/`tmsId`
 * are bound in `beforeAll`.
 */
let discoveredTileCache: DiscoveredTile | null = null;

function lonLatToTile(
  lon: number,
  lat: number,
  zoom: number,
): { col: number; row: number } {
  const n = Math.pow(2, zoom);
  const col = Math.floor(((lon + 180) / 360) * n);
  const latRad = (lat * Math.PI) / 180;
  const row = Math.floor(
    ((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) *
      n,
  );
  return { col, row };
}

/**
 * Walk a small spiral grid around (col0, row0) at the given zoom, fetching
 * each tile and returning the first one that comes back with binary content
 * (status 200 + non-zero byte length). Returns null if no candidate tile in
 * the search window has features.
 *
 * Search shape: a (2 * radius + 1) x (2 * radius + 1) square centered on the
 * starting tile, walked in roughly outward order so the closest candidates
 * are tried first. Bounded total fetches keep this cheap even at high zoom.
 */
async function findNonEmptyTileNear(
  zoom: number,
  col0: number,
  row0: number,
  radius: number,
): Promise<DiscoveredTile | null> {
  const tileMatrix = String(zoom);
  const n = Math.pow(2, zoom);
  const seen = new Set<string>();
  const order: Array<{ col: number; row: number }> = [];
  for (let r = 0; r <= radius; r += 1) {
    for (let dCol = -r; dCol <= r; dCol += 1) {
      for (let dRow = -r; dRow <= r; dRow += 1) {
        if (Math.max(Math.abs(dCol), Math.abs(dRow)) !== r) continue;
        const col = ((col0 + dCol) % n + n) % n;
        const row = row0 + dRow;
        if (row < 0 || row >= n) continue;
        const key = `${col},${row}`;
        if (seen.has(key)) continue;
        seen.add(key);
        order.push({ col, row });
      }
    }
  }
  for (const { col, row } of order) {
    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${row}/${col}`,
    );
    if (resp.status !== 200) continue;
    const buffer = await resp.arrayBuffer();
    if (buffer.byteLength > 0) {
      return {
        tileMatrix,
        tileRow: row,
        tileCol: col,
        byteLength: buffer.byteLength,
        status: 200,
      };
    }
  }
  return null;
}

/**
 * Discover a tile coordinate whose response is a non-empty MVT payload.
 *
 * Strategy: ask the tileset metadata for a bounding box, project its centroid
 * to tile space at a few candidate zoom levels, and scan a small spiral grid
 * around each centroid until a tile with features is found. The reviewer
 * (PR #700, discussion r3037355117) correctly flagged that the previous
 * version returned the centroid coordinate without ever checking it, so the
 * MVT decode test could pass without exercising decode behavior. The fix is
 * to make discovery actually do its job.
 *
 * Throws when the configured search budget is exhausted: that is the failure
 * case the reviewer asked us to surface, rather than silently skipping.
 */
async function discoverTileWithFeatures(): Promise<DiscoveredTile> {
  if (discoveredTileCache) {
    return discoveredTileCache;
  }
  const tilesetResp = await fetch(
    `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}`,
  );
  if (!tilesetResp.ok) {
    throw new Error(`Tileset metadata returned ${tilesetResp.status}`);
  }
  const tileset = await tilesetResp.json();

  // Try to find bounding box from tileset or collection metadata.
  let centerLon = -122.4;
  let centerLat = 37.8;
  if (tileset.boundingBox) {
    const bb = tileset.boundingBox;
    const lowerCorner = bb.lowerCorner ?? bb.lowerLeft;
    const upperCorner = bb.upperCorner ?? bb.upperRight;
    if (lowerCorner && upperCorner) {
      centerLon = (lowerCorner[0] + upperCorner[0]) / 2;
      centerLat = (lowerCorner[1] + upperCorner[1]) / 2;
    }
  }

  // Try a couple of common zoom levels for test data. Lower zoom = larger
  // tile footprint, more likely to contain features for thin/sparse fixtures.
  const candidateZooms = [10, 8, 12, 6, 4];
  for (const zoom of candidateZooms) {
    const { col, row } = lonLatToTile(centerLon, centerLat, zoom);
    const found = await findNonEmptyTileNear(zoom, col, row, 2);
    if (found) {
      discoveredTileCache = found;
      return found;
    }
  }
  throw new Error(
    `Could not locate a non-empty MVT tile for collection ${collectionId} ` +
      `under TMS ${tmsId}. The MVT decode test cannot validate decode ` +
      `behavior without a tile that contains features. ` +
      `Verify the test fixture data ingest produced renderable geometry.`,
  );
}

beforeAll(async () => {
  collectionId = await discoverCollectionId();

  // Discover available TMS ID
  const tilesetsResp = await fetch(
    `${ogcTilesUrl}/collections/${collectionId}/tiles`,
  );
  if (!tilesetsResp.ok) {
    throw new Error(`Collection tilesets returned ${tilesetsResp.status}`);
  }
  const tilesetsBody = await tilesetsResp.json();
  const tileset = tilesetsBody.tilesets[0];
  tmsId =
    tileset.tileMatrixSetId ??
    tileset.tileMatrixSetURI?.split('/').pop() ??
    'WebMercatorQuad';
});

afterAll(() => {
  evidence.write();
});

describe('OGC API Tiles MVT', () => {
  it('MVT tile fetch returns binary data', async () => {
    evidence.attemptExtension('JS-EXT-01');
    const start = Date.now();
    const discovered = await discoverTileWithFeatures();
    const { tileMatrix, tileRow, tileCol } = discovered;

    // Re-fetch so we can validate response headers (the discovery scan only
    // recorded byte length and status). Discovery already proved the tile is
    // non-empty, so this fetch must return 200 with binary payload.
    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${tileRow}/${tileCol}`,
    );
    const duration = Date.now() - start;

    expect(resp.status).toBe(200);

    const contentType = resp.headers.get('content-type') ?? '';
    // MVT may be served as application/vnd.mapbox-vector-tile or application/x-protobuf
    expect(
      contentType.includes('mapbox-vector-tile') ||
        contentType.includes('protobuf') ||
        contentType.includes('octet-stream'),
    ).toBe(true);

    const buffer = await resp.arrayBuffer();
    expect(buffer.byteLength).toBeGreaterThan(0);

    evidence.recordExtension('JS-EXT-01', 'pass', {
      durationMs: duration,
      notes: `MVT tile ${tileMatrix}/${tileRow}/${tileCol} fetched: ${buffer.byteLength} bytes`,
    });
  });

  it('ol/format/MVT decodes features from tile', async () => {
    evidence.attemptExtension('JS-EXT-01');
    const { tileMatrix, tileRow, tileCol } = await discoverTileWithFeatures();

    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${tileRow}/${tileCol}`,
    );

    expect(resp.status).toBe(200);
    const buffer = await resp.arrayBuffer();

    const start = Date.now();
    const mvtFormat = new MVT();
    const features = mvtFormat.readFeatures(buffer);
    const duration = Date.now() - start;

    expect(features.length).toBeGreaterThan(0);

    // Each decoded feature should have a geometry type
    const firstGeom = features[0].getGeometry();
    expect(firstGeom).toBeTruthy();

    evidence.recordExtension('JS-EXT-01', 'pass', {
      durationMs: duration,
      measuredCount: features.length,
      notes: `ol/format/MVT decoded ${features.length} features, first geometry: ${firstGeom?.getType()}`,
    });
  });

  it('decoded MVT features have accessible properties', async () => {
    const { tileMatrix, tileRow, tileCol } = await discoverTileWithFeatures();

    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${tileRow}/${tileCol}`,
    );

    expect(resp.status).toBe(200);
    const buffer = await resp.arrayBuffer();

    const mvtFormat = new MVT();
    const features = mvtFormat.readFeatures(buffer);
    expect(features.length).toBeGreaterThan(0);

    const props = features[0].getProperties();
    // MVT features should have at least a layer name and geometry
    expect(Object.keys(props).length).toBeGreaterThan(0);
  });
});
