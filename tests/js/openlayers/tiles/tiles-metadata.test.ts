/**
 * OGC API Tiles metadata tests via OpenLayers.
 *
 * Exercises: tiles landing page, tile matrix sets listing,
 * collection tilesets listing, and tileset metadata introspection.
 */

import { config, ogcTilesUrl, discoverCollectionId } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('mvt');
let collectionId: string;

beforeAll(async () => {
  collectionId = await discoverCollectionId();
});

afterAll(() => {
  evidence.write();
});

describe('OGC API Tiles Metadata', () => {
  it('tiles landing page loads', async () => {
    evidence.attempt('CERT-CONN-01');
    const start = Date.now();
    const resp = await fetch(ogcTilesUrl);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('links');
    expect(Array.isArray(body.links)).toBe(true);

    evidence.record('CERT-CONN-01', 'pass', {
      durationMs: duration,
      notes: 'OGC Tiles landing page returns links array',
    });
  });

  it('tile matrix sets listed', async () => {
    const start = Date.now();
    const resp = await fetch(`${ogcTilesUrl}/tileMatrixSets`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('tileMatrixSets');
    expect(Array.isArray(body.tileMatrixSets)).toBe(true);
    expect(body.tileMatrixSets.length).toBeGreaterThan(0);

    // Each TMS entry should have an id
    const first = body.tileMatrixSets[0];
    expect(first).toHaveProperty('id');

    evidence.recordExtension('JS-EXT-TILES-DISC-01', 'pass', {
      durationMs: duration,
      measuredCount: body.tileMatrixSets.length,
      notes: `${body.tileMatrixSets.length} tile matrix sets available`,
    });
  });

  it('collection tilesets listed', async () => {
    const start = Date.now();
    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles`,
    );
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('tilesets');
    expect(Array.isArray(body.tilesets)).toBe(true);
    expect(body.tilesets.length).toBeGreaterThan(0);

    evidence.recordExtension('JS-EXT-TILES-DISC-02', 'pass', {
      durationMs: duration,
      measuredCount: body.tilesets.length,
      notes: `${body.tilesets.length} tilesets for collection '${collectionId}'`,
    });
  });

  it('tileset metadata returned', async () => {
    // First discover available tilesets
    const tilesetsResp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles`,
    );
    expect(tilesetsResp.ok).toBe(true);
    const tilesetsBody = await tilesetsResp.json();
    expect(tilesetsBody.tilesets.length).toBeGreaterThan(0);

    // Extract tileMatrixSetId from the first tileset's links
    const tileset = tilesetsBody.tilesets[0];
    const tmsId =
      tileset.tileMatrixSetId ??
      tileset.tileMatrixSetURI?.split('/').pop() ??
      'WebMercatorQuad';

    const start = Date.now();
    const resp = await fetch(
      `${ogcTilesUrl}/collections/${collectionId}/tiles/${tmsId}`,
    );
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    // Tileset metadata should reference the tile matrix set
    expect(body).toHaveProperty('links');

    evidence.recordExtension('JS-EXT-TILES-SCHM-01', 'pass', {
      durationMs: duration,
      notes: `Tileset metadata for TMS '${tmsId}' retrieved`,
    });
  });
});
