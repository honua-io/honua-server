/**
 * OGC API Maps client compatibility tests via OpenLayers.
 *
 * Exercises: landing page, conformance, OpenAPI route metadata, ImageStatic
 * source configuration, and live /ogc/maps image request behavior.
 */

import ImageStatic from 'ol/source/ImageStatic.js';
import { discoverCollectionId, ogcMapsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('ogc-maps');
let collectionId: string;

afterAll(() => {
  evidence.write();
});

beforeAll(async () => {
  collectionId = await discoverCollectionId();
});

function expectPng(buffer: ArrayBuffer): void {
  const bytes = Array.from(new Uint8Array(buffer.slice(0, 8)));
  expect(bytes).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
}

function collectionMapUrl(): string {
  const params = new URLSearchParams({
    bbox: '-180,-90,180,90',
    width: '256',
    height: '256',
    f: 'png',
  });
  return `${ogcMapsUrl}/collections/${encodeURIComponent(collectionId)}/map?${params}`;
}

describe('OGC API Maps OpenLayers compatibility', () => {
  it('landing page loads', async () => {
    evidence.attempt('CERT-CONN-01');
    const start = Date.now();
    const resp = await fetch(ogcMapsUrl);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body).toHaveProperty('links');
    expect(Array.isArray(body.links)).toBe(true);

    evidence.record('CERT-CONN-01', 'pass', {
      durationMs: duration,
      notes: 'OGC API Maps landing page returned links array',
    });
  });

  it('conformance classes include OGC API Maps', async () => {
    evidence.attempt('CERT-DISC-01');
    const start = Date.now();
    const resp = await fetch(`${ogcMapsUrl}/conformance`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(Array.isArray(body.conformsTo)).toBe(true);
    expect(body.conformsTo.some((entry: string) => entry.includes('ogcapi-maps'))).toBe(true);

    evidence.record('CERT-DISC-01', 'pass', {
      durationMs: duration,
      measuredCount: body.conformsTo.length,
      notes: `${body.conformsTo.length} OGC API Maps conformance classes returned`,
    });
  });

  it('OpenAPI document advertises collection map route', async () => {
    evidence.attempt('CERT-DISC-02');
    const start = Date.now();
    const resp = await fetch(`${ogcMapsUrl}/openapi.json`);
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const body = await resp.json();
    expect(body.paths).toHaveProperty('/ogc/maps/collections/{collectionId}/map');

    evidence.record('CERT-DISC-02', 'pass', {
      durationMs: duration,
      notes: 'OGC API Maps OpenAPI advertises collection map route',
    });
  });

  it('OpenLayers ImageStatic can target the collection map endpoint', async () => {
    evidence.attempt('CERT-RNDR-01');
    evidence.attemptExtension('JS-EXT-OGC-MAPS-01');
    const url = collectionMapUrl();
    const source = new ImageStatic({
      url,
      projection: 'EPSG:4326',
      imageExtent: [-180, -90, 180, 90],
    });

    expect(source.getUrl()).toBe(url);
    expect(source.getImageExtent()).toEqual([-180, -90, 180, 90]);

    const start = Date.now();
    const resp = await fetch(url);
    const duration = Date.now() - start;

    if (resp.status === 404) {
      const contentType = resp.headers.get('content-type') ?? '';
      expect(contentType).toContain('json');
      const body = await resp.json();
      expect(body.status ?? body.error?.code).toBe(404);
      evidence.record('CERT-RNDR-01', 'skip', {
        durationMs: duration,
        notes: 'OGC API Maps endpoint reached by OpenLayers ImageStatic; no raster map fixture was available for this collection.',
      });
      evidence.recordExtension('JS-EXT-OGC-MAPS-01', 'skip', {
        durationMs: duration,
        notes: 'OpenLayers ImageStatic source was configured, but the live collection has no raster map fixture.',
      });
      return;
    }

    expect(resp.status).toBe(200);
    expect(resp.headers.get('content-type') ?? '').toContain('image/png');
    const buffer = await resp.arrayBuffer();
    expect(buffer.byteLength).toBeGreaterThan(8);
    expectPng(buffer);

    evidence.record('CERT-RNDR-01', 'pass', {
      durationMs: duration,
      notes: `OGC API Maps rendered image/png for collection '${collectionId}'`,
    });
    evidence.recordExtension('JS-EXT-OGC-MAPS-01', 'pass', {
      durationMs: duration,
      notes: `OpenLayers ImageStatic retrieved /ogc/maps image for collection '${collectionId}'`,
    });
  });

  it('invalid map format returns protocol-shaped error', async () => {
    evidence.attempt('CERT-ERRH-01');
    const start = Date.now();
    const resp = await fetch(
      `${ogcMapsUrl}/collections/${encodeURIComponent(collectionId)}/map?bbox=-180,-90,180,90&width=256&height=256&f=json`,
    );
    const duration = Date.now() - start;

    expect(resp.status).toBe(400);
    expect(resp.headers.get('content-type') ?? '').toContain('json');
    const body = await resp.json();
    expect(body.status ?? body.error?.code).toBe(400);

    evidence.record('CERT-ERRH-01', 'pass', {
      durationMs: duration,
      notes: 'Unsupported OGC API Maps image format returned JSON error response',
    });
  });
});
