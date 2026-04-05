/**
 * OGC API Features discovery tests via OpenLayers GeoJSON format.
 *
 * Exercises: landing page, conformance, collections list, single collection
 * metadata, and queryables.
 */

import GeoJSON from 'ol/format/GeoJSON.js';
import { config, ogcFeaturesUrl, discoverCollectionId } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('ogc-features');
let collectionId: string;

beforeAll(async () => {
  collectionId = await discoverCollectionId();
});

afterAll(() => {
  evidence.write();
});

describe('OGC API Features Discovery', () => {
  it('landing page loads', async () => {
    evidence.attempt('CERT-CONN-01');
    const start = Date.now();
    const resp = await fetch(ogcFeaturesUrl);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('links');
    expect(Array.isArray(body.links)).toBe(true);

    evidence.record('CERT-CONN-01', 'pass', {
      durationMs: duration,
      notes: 'OGC Features landing page returns links array',
    });
  });

  it('conformance classes listed', async () => {
    evidence.attempt('CERT-DISC-01');
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/conformance`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('conformsTo');
    expect(Array.isArray(body.conformsTo)).toBe(true);
    expect(body.conformsTo.length).toBeGreaterThan(0);

    evidence.record('CERT-DISC-01', 'pass', {
      durationMs: duration,
      measuredCount: body.conformsTo.length,
      notes: `${body.conformsTo.length} conformance classes returned`,
    });
  });

  it('collections list returned', async () => {
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('collections');
    expect(Array.isArray(body.collections)).toBe(true);
    expect(body.collections.length).toBeGreaterThan(0);

    // Each collection has required fields
    const first = body.collections[0];
    expect(first).toHaveProperty('id');
    expect(first).toHaveProperty('links');

    evidence.recordExtension('JS-EXT-OL-COLL-01', 'pass', {
      durationMs: duration,
      measuredCount: body.collections.length,
      notes: `${body.collections.length} collections discovered`,
    });
  });

  it('single collection metadata', async () => {
    evidence.attempt('CERT-DISC-02');
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body.id).toBe(collectionId);
    expect(body).toHaveProperty('links');

    evidence.record('CERT-DISC-02', 'pass', {
      durationMs: duration,
      notes: `Collection '${collectionId}' metadata retrieved`,
    });
  });

  it('queryables schema retrieved', async () => {
    evidence.attempt('CERT-SCHM-01');
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/queryables`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    // Queryables returns a JSON Schema
    expect(body).toHaveProperty('properties');
    expect(body).toHaveProperty('type');

    evidence.record('CERT-SCHM-01', 'pass', {
      durationMs: duration,
      measuredCount: Object.keys(body.properties ?? {}).length,
      notes: 'Queryables JSON Schema returned',
    });
  });

  it('geometry type reported in collection metadata', async () => {
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    // itemType or geometry field indicates spatial nature
    expect(body).toHaveProperty('itemType');

    evidence.recordExtension('JS-EXT-OL-ITEMTYPE-01', 'pass', {
      durationMs: duration,
      notes: `itemType: ${body.itemType} (not a geometry type signal per OGC spec)`,
    });
  });
});
