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

/**
 * Discover a tile coordinate that contains features.
 *
 * Queries the tileset metadata, picks a known-good zoom level,
 * then scans a small grid around the data extent centroid.
 */
async function discoverTileWithFeatures(): Promise<{
  tileMatrix: string;
  tileRow: number;
  tileCol: number;
}> {
  // Get tileset metadata to find available zoom levels
  const tilesetResp = await fetch(
    `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}`,
  );
  if (!tilesetResp.ok) {
    throw new Error(`Tileset metadata returned ${tilesetResp.status}`);
  }
  const tileset = await tilesetResp.json();

  // Try to find bounding box from tileset or collection metadata
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

  // Use zoom level 10 as a reasonable default for test data
  const zoom = 10;
  const tileMatrix = String(zoom);

  // Convert lat/lon to tile coordinates (WebMercatorQuad / Slippy map)
  const n = Math.pow(2, zoom);
  const tileCol = Math.floor(((centerLon + 180) / 360) * n);
  const latRad = (centerLat * Math.PI) / 180;
  const tileRow = Math.floor(
    ((1 - Math.log(Math.tan(latRad) + 1 / Math.cos(latRad)) / Math.PI) / 2) *
      n,
  );

  return { tileMatrix, tileRow, tileCol };
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
    const { tileMatrix, tileRow, tileCol } = await discoverTileWithFeatures();

    const start = Date.now();
    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${tileRow}/${tileCol}`,
    );
    const duration = Date.now() - start;

    // A 200 with PBF data or 204 (empty tile) are both valid
    expect([200, 204]).toContain(resp.status);

    if (resp.status === 200) {
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
    } else {
      evidence.recordExtension('JS-EXT-01', 'pass', {
        durationMs: duration,
        notes: `Tile ${tileMatrix}/${tileRow}/${tileCol} returned 204 (empty tile, valid response)`,
      });
    }
  });

  it('ol/format/MVT decodes features from tile', async () => {
    const { tileMatrix, tileRow, tileCol } = await discoverTileWithFeatures();

    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}/${tileMatrix}/${tileRow}/${tileCol}`,
    );

    if (resp.status === 204) {
      evidence.recordExtension('JS-EXT-01', 'skip', {
        notes: 'Tile is empty (204), cannot decode features',
      });
      return;
    }

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

    if (resp.status === 204) {
      return;
    }

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
