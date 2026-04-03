/**
 * WFS 2.0 discovery tests via OpenLayers WFS format.
 *
 * Exercises: GetCapabilities parsing via ol/format/WFS,
 * FeatureType listing from capabilities document.
 */

import '../shared/ol-node-setup.js';
import WFS from 'ol/format/WFS.js';
import { wfsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('wfs20');

afterAll(() => {
  evidence.write();
});

describe('WFS 2.0 Discovery', () => {
  let capabilitiesXml: string;

  beforeAll(async () => {
    const resp = await fetch(
      `${wfsUrl}?service=WFS&version=2.0.0&request=GetCapabilities`,
    );
    expect(resp.ok).toBe(true);
    capabilitiesXml = await resp.text();
  });

  it('GetCapabilities XML is parseable', () => {
    evidence.attempt('CERT-DISC-01');
    const start = Date.now();

    expect(capabilitiesXml).toBeTruthy();
    // Parse as XML to verify structure
    const parser = new DOMParser();
    const doc = parser.parseFromString(capabilitiesXml, 'text/xml');
    const duration = Date.now() - start;

    // Should not be a parseerror document
    const parseError = doc.getElementsByTagName('parsererror');
    expect(parseError.length).toBe(0);

    // Root element should be WFS_Capabilities
    const root = doc.documentElement;
    expect(root.localName).toBe('WFS_Capabilities');

    evidence.record('CERT-DISC-01', 'pass', {
      durationMs: duration,
      notes: 'GetCapabilities returned valid WFS_Capabilities XML',
    });
  });

  it('FeatureType listed in capabilities', () => {
    evidence.attempt('CERT-DISC-02');
    const start = Date.now();
    const parser = new DOMParser();
    const doc = parser.parseFromString(capabilitiesXml, 'text/xml');
    const duration = Date.now() - start;

    // Find FeatureType elements
    const featureTypes = doc.getElementsByTagNameNS('*', 'FeatureType');
    expect(featureTypes.length).toBeGreaterThan(0);

    // Each FeatureType should have a Name child
    const firstFt = featureTypes[0];
    const nameEls = firstFt.getElementsByTagNameNS('*', 'Name');
    expect(nameEls.length).toBeGreaterThan(0);

    const typeName = nameEls[0].textContent;
    expect(typeName).toBeTruthy();

    evidence.record('CERT-DISC-02', 'pass', {
      durationMs: duration,
      measuredCount: featureTypes.length,
      notes: `${featureTypes.length} FeatureType(s) found, first: ${typeName}`,
    });
  });

  it('service identification present', () => {
    const parser = new DOMParser();
    const doc = parser.parseFromString(capabilitiesXml, 'text/xml');

    const serviceId = doc.getElementsByTagNameNS('*', 'ServiceIdentification');
    expect(serviceId.length).toBeGreaterThan(0);

    const titleEls = serviceId[0].getElementsByTagNameNS('*', 'Title');
    expect(titleEls.length).toBeGreaterThan(0);
  });
});
