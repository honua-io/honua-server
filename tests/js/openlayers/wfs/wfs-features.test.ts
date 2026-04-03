/**
 * WFS 2.0 feature retrieval tests via OpenLayers WFS format.
 *
 * Exercises: GetFeature consumption via ol/format/WFS, GML parsing,
 * feature property access, and geometry extraction.
 */

import '../shared/ol-node-setup.js';
import WFS from 'ol/format/WFS.js';
import GML from 'ol/format/GML.js';
import { wfsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('wfs20');
let typeName: string;

beforeAll(async () => {
  // Discover first available type name from capabilities
  const resp = await fetch(
    `${wfsUrl}?service=WFS&version=2.0.0&request=GetCapabilities`,
  );
  expect(resp.ok).toBe(true);
  const xml = await resp.text();
  const parser = new DOMParser();
  const doc = parser.parseFromString(xml, 'text/xml');
  const featureTypes = doc.getElementsByTagNameNS('*', 'FeatureType');
  expect(featureTypes.length).toBeGreaterThan(0);

  const nameEls = featureTypes[0].getElementsByTagNameNS('*', 'Name');
  expect(nameEls.length).toBeGreaterThan(0);
  typeName = nameEls[0].textContent!;
});

afterAll(() => {
  evidence.write();
});

describe('WFS 2.0 GetFeature', () => {
  it('GetFeature returns parseable response', async () => {
    evidence.attempt('CERT-QFLT-01');
    const start = Date.now();
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typeNames=${encodeURIComponent(typeName)}&count=5`,
    );
    const duration = Date.now() - start;

    expect(resp.ok).toBe(true);
    const text = await resp.text();
    expect(text.length).toBeGreaterThan(0);

    evidence.record('CERT-QFLT-01', 'pass', {
      durationMs: duration,
      notes: `GetFeature for '${typeName}' returned ${text.length} bytes`,
    });
  });

  it('ol/format/WFS reads features from GetFeature response', async () => {
    evidence.attempt('CERT-GEOM-01');
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typeNames=${encodeURIComponent(typeName)}&count=5`,
    );
    expect(resp.ok).toBe(true);
    const text = await resp.text();

    const start = Date.now();
    const wfsFormat = new WFS();
    const features = wfsFormat.readFeatures(text);
    const duration = Date.now() - start;

    expect(features.length).toBeGreaterThan(0);

    evidence.record('CERT-GEOM-01', 'pass', {
      durationMs: duration,
      measuredCount: features.length,
      notes: `ol/format/WFS parsed ${features.length} features from GML`,
    });
  });

  it('parsed features have accessible properties', async () => {
    evidence.attempt('CERT-SCHM-01');
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typeNames=${encodeURIComponent(typeName)}&count=5`,
    );
    expect(resp.ok).toBe(true);
    const text = await resp.text();

    const wfsFormat = new WFS();
    const features = wfsFormat.readFeatures(text);
    expect(features.length).toBeGreaterThan(0);

    const start = Date.now();
    const props = features[0].getProperties();
    const duration = Date.now() - start;

    // Should have at least the geometry property
    expect(Object.keys(props).length).toBeGreaterThan(0);

    evidence.record('CERT-SCHM-01', 'pass', {
      durationMs: duration,
      measuredCount: Object.keys(props).length,
      notes: `Feature has ${Object.keys(props).length} properties: ${Object.keys(props).join(', ')}`,
    });
  });

  it('parsed features have geometry', async () => {
    evidence.attempt('CERT-GEOM-01');
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typeNames=${encodeURIComponent(typeName)}&count=5`,
    );
    expect(resp.ok).toBe(true);
    const text = await resp.text();

    const wfsFormat = new WFS();
    const features = wfsFormat.readFeatures(text);
    expect(features.length).toBeGreaterThan(0);

    const withGeom = features.filter(f => f.getGeometry() != null);
    expect(withGeom.length).toBeGreaterThan(0);

    const geom = withGeom[0].getGeometry()!;
    expect(geom.getType()).toBeTruthy();

    evidence.record('CERT-GEOM-01', 'pass', {
      measuredCount: withGeom.length,
      notes: `${withGeom.length}/${features.length} features have geometry, first type: ${geom.getType()}`,
    });
  });
});
