/**
 * OGC API Features item access tests via OpenLayers GeoJSON format.
 *
 * Exercises: items list, GeoJSON parsing via ol/format/GeoJSON,
 * pagination (limit/offset), and single item fetch.
 */

import GeoJSON from 'ol/format/GeoJSON.js';
import { config, ogcFeaturesUrl, discoverCollectionId } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';
import { disjointPageIds, pageFeatureIds } from '../shared/pagination.js';

const format = new GeoJSON();
const evidence = new EvidenceCollector('ogc-features');
let collectionId: string;

beforeAll(async () => {
  collectionId = await discoverCollectionId();
});

afterAll(() => {
  evidence.write();
});

describe('OGC API Features Items', () => {
  it('items list returns GeoJSON FeatureCollection', async () => {
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=10`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body.type).toBe('FeatureCollection');
    expect(Array.isArray(body.features)).toBe(true);
    expect(body.features.length).toBeGreaterThan(0);

    evidence.recordExtension('JS-EXT-OL-ITEMS-01', 'pass', {
      durationMs: duration,
      measuredCount: body.features.length,
      notes: `${body.features.length} features returned (unfiltered listing)`,
    });
  });

  it('ol/format/GeoJSON parses features from items response', async () => {
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=10`);
    expect(resp.ok).toBe(true);
    const text = await resp.text();

    const start = Date.now();
    const features = format.readFeatures(text);
    const duration = Date.now() - start;

    expect(features.length).toBeGreaterThan(0);

    // Each feature should have geometry
    const withGeom = features.filter(f => f.getGeometry() != null);
    expect(withGeom.length).toBeGreaterThan(0);

    // Each feature should have properties
    const firstProps = features[0].getProperties();
    expect(Object.keys(firstProps).length).toBeGreaterThan(0);

    evidence.recordExtension('JS-EXT-OL-GEOJSON-01', 'pass', {
      durationMs: duration,
      measuredCount: features.length,
      notes: `ol/format/GeoJSON parsed ${features.length} features, ${withGeom.length} with geometry`,
    });
  });

  it('pagination with limit', async () => {
    evidence.attempt('CERT-PAGE-01');
    // base-schema.sql and client-compat-v1.sql seed ten rows on layer0;
    // js_test_server.py seeds the same ten-row profile on the configured layer.
    // Browser-only layers2000–2002 have fewer than four rows and cannot replace it.
    expect(collectionId).toBe(config.layerId);
    const fixture = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=4`);
    expect(fixture.ok).toBe(true);
    pageFeatureIds(await fixture.json(), 4);
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=2`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    pageFeatureIds(body, 2);

    evidence.record('CERT-PAGE-01', 'pass', {
      durationMs: duration,
      measuredCount: body.features.length,
      notes: 'Populated configured fixture returned exactly2 unique identified features for limit=2',
    });
  });

  it('pagination with offset', async () => {
    evidence.attempt('CERT-PAGE-02');
    expect(collectionId).toBe(config.layerId);
    // First get items without offset
    const resp1 = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=2`);
    expect(resp1.ok).toBe(true);
    const page1 = await resp1.json();

    // Then get items with offset
    const start = Date.now();
    const resp2 = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=2&offset=2`);
    const duration = Date.now() - start;

    expect(resp2.ok).toBe(true);
    const page2 = await resp2.json();
    disjointPageIds(page1, page2, 2);

    evidence.record('CERT-PAGE-02', 'pass', {
      durationMs: duration,
      measuredCount: page2.features.length,
      notes: 'Two nonempty2-feature pages have unique IDs and zero overlap at offset=2',
    });
  });

  it('single item by ID', async () => {
    // First discover an item ID
    const listResp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items?limit=1`);
    expect(listResp.ok).toBe(true);
    const listBody = await listResp.json();
    expect(listBody.features.length).toBeGreaterThan(0);

    const featureId = listBody.features[0].id;
    expect(featureId).toBeDefined();

    // Fetch single item
    const start = Date.now();
    const resp = await fetch(`${ogcFeaturesUrl}/collections/${collectionId}/items/${featureId}`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body.type).toBe('Feature');
    expect(body.id).toBe(featureId);

    // Parse with OpenLayers
    const text = JSON.stringify(body);
    const features = format.readFeatures(text);
    expect(features.length).toBe(1);

    evidence.recordExtension('JS-EXT-OL-GEOJSON-02', 'pass', {
      durationMs: duration,
      notes: `Single item '${featureId}' fetched and parsed by ol/format/GeoJSON`,
    });
  });
});
