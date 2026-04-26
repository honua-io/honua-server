/**
 * WFS 2.0 feature retrieval tests via OpenLayers WFS format.
 *
 * Exercises: GetFeature consumption via ol/format/WFS, GML parsing,
 * feature property access, and geometry extraction.
 */

import '../shared/ol-node-setup.js';
import type { Feature } from 'ol';
import WFS from 'ol/format/WFS.js';
import { wfsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('wfs');
const wfsFormat = new WFS({ version: '2.0.0' });

interface DiscoveredType {
  typeName: string;
  responseText: string;
  features: Feature[];
}

let discoveredType: DiscoveredType;

/**
 * Discover a WFS type name whose GetFeature response actually yields parsed
 * features. The previous version always picked the first `<FeatureType>`
 * from GetCapabilities, which let three tests fail with "expected 0 to be
 * greater than 0" whenever the first advertised type happened to be empty
 * in the test fixture. Mirrors the discoverTileWithFeatures() fix in
 * tiles-mvt.test.ts: scan candidate types, fetch a small sample for each,
 * cache the first one with parsed features, and throw a clear error if no
 * advertised type has any features so the failure is loud rather than a
 * silent skip.
 */
async function discoverTypeWithFeatures(): Promise<DiscoveredType> {
  const capsResp = await fetch(
    `${wfsUrl}?service=WFS&version=2.0.0&request=GetCapabilities`,
  );
  expect(capsResp.ok).toBe(true);
  const capsXml = await capsResp.text();
  const parser = new DOMParser();
  const capsDoc = parser.parseFromString(capsXml, 'text/xml');
  const featureTypeEls = capsDoc.getElementsByTagNameNS('*', 'FeatureType');
  expect(featureTypeEls.length).toBeGreaterThan(0);

  const candidateNames: string[] = [];
  for (let i = 0; i < featureTypeEls.length; i += 1) {
    const nameEls = featureTypeEls[i].getElementsByTagNameNS('*', 'Name');
    if (nameEls.length === 0) continue;
    const text = nameEls[0].textContent;
    if (text && text.trim()) {
      candidateNames.push(text.trim());
    }
  }
  expect(candidateNames.length).toBeGreaterThan(0);

  const tried: string[] = [];
  for (const candidate of candidateNames) {
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetFeature&typeNames=${encodeURIComponent(candidate)}&count=5`,
    );
    if (!resp.ok) {
      tried.push(`${candidate}: HTTP ${resp.status}`);
      continue;
    }
    const text = await resp.text();
    if (text.length === 0) {
      tried.push(`${candidate}: empty body`);
      continue;
    }
    const features = wfsFormat.readFeatures(text);
    if (features.length > 0) {
      return { typeName: candidate, responseText: text, features };
    }
    tried.push(`${candidate}: 0 features`);
  }
  throw new Error(
    `No WFS FeatureType produced parseable features. Tried ${candidateNames.length} ` +
      `type(s): ${tried.join('; ')}. The WFS suite cannot validate decode ` +
      `behavior without a type that has data. Verify the test fixture ingest ` +
      `populated at least one advertised feature type.`,
  );
}

beforeAll(async () => {
  discoveredType = await discoverTypeWithFeatures();
});

afterAll(() => {
  evidence.write();
});

describe('WFS 2.0 GetFeature', () => {
  it('GetFeature returns parseable response', async () => {
    evidence.attempt('CERT-QFLT-01');
    const { typeName } = discoveredType;
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
    // Reuse the cached discovery sample so we don't repeat the same query.
    // discoveredType is guaranteed to carry > 0 features by the discover
    // helper, so the strict assertion below is meaningful.
    const start = Date.now();
    const features = discoveredType.features;
    const duration = Date.now() - start;

    expect(features.length).toBeGreaterThan(0);

    evidence.record('CERT-GEOM-01', 'pass', {
      durationMs: duration,
      measuredCount: features.length,
      notes: `ol/format/WFS parsed ${features.length} features from GML for '${discoveredType.typeName}'`,
    });
  });

  it('parsed features have accessible properties', async () => {
    evidence.attempt('CERT-SCHM-01');
    const features = discoveredType.features;
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
    const features = discoveredType.features;
    expect(features.length).toBeGreaterThan(0);

    const withGeom = features.filter((f) => f.getGeometry() != null);
    expect(withGeom.length).toBeGreaterThan(0);

    const geom = withGeom[0].getGeometry()!;
    expect(geom.getType()).toBeTruthy();

    evidence.record('CERT-GEOM-01', 'pass', {
      measuredCount: withGeom.length,
      notes: `${withGeom.length}/${features.length} features have geometry, first type: ${geom.getType()}`,
    });
  });
});
