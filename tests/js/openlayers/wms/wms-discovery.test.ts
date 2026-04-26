/**
 * WMS 1.3.0 client compatibility tests via OpenLayers.
 *
 * Exercises: GetCapabilities parsing through ol/format/WMSCapabilities,
 * ImageWMS source configuration, and GetMap PNG retrieval from the real WMS
 * route surface.
 */

import '../shared/ol-node-setup.js';
import WMSCapabilities from 'ol/format/WMSCapabilities.js';
import ImageWMS from 'ol/source/ImageWMS.js';
import { wmsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('wms');

type WmsLayer = {
  Name?: string;
  Title?: string;
  CRS?: string[];
  Layer?: WmsLayer[];
};

type WmsCapabilitiesDocument = {
  version?: string;
  Service?: {
    Name?: string;
    Title?: string;
  };
  Capability?: {
    Request?: {
      GetMap?: {
        Format?: string[];
      };
    };
    Layer?: WmsLayer;
  };
};

let capabilitiesXmlPromise: Promise<string> | undefined;
let parsedCapabilities: WmsCapabilitiesDocument | undefined;

afterAll(() => {
  evidence.write();
});

async function fetchCapabilitiesXml(): Promise<string> {
  capabilitiesXmlPromise ??= (async () => {
    const params = new URLSearchParams({
      SERVICE: 'WMS',
      VERSION: '1.3.0',
      REQUEST: 'GetCapabilities',
    });
    const resp = await fetch(`${wmsUrl}?${params}`);
    if (!resp.ok) {
      throw new Error(`WMS GetCapabilities returned ${resp.status}`);
    }

    return resp.text();
  })();

  return capabilitiesXmlPromise;
}

async function readCapabilities(): Promise<WmsCapabilitiesDocument> {
  if (parsedCapabilities) return parsedCapabilities;

  const xml = await fetchCapabilitiesXml();
  const parsed = new WMSCapabilities().read(xml) as WmsCapabilitiesDocument | null;
  if (!parsed) {
    throw new Error('OpenLayers returned no parsed WMS capabilities document');
  }

  parsedCapabilities = parsed;
  return parsedCapabilities;
}

function flattenLayers(layer: WmsLayer | undefined): WmsLayer[] {
  if (!layer) return [];
  return [layer, ...(layer.Layer ?? []).flatMap(flattenLayers)];
}

async function discoverNamedLayer(): Promise<string> {
  const capabilities = await readCapabilities();
  const layer = flattenLayers(capabilities.Capability?.Layer)
    .find(candidate => typeof candidate.Name === 'string' && candidate.Name.length > 0);

  if (!layer?.Name) {
    throw new Error('WMS capabilities did not advertise a named layer');
  }

  return layer.Name;
}

function expectPng(buffer: ArrayBuffer): void {
  const bytes = Array.from(new Uint8Array(buffer.slice(0, 8)));
  expect(bytes).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
}

describe('WMS 1.3.0 OpenLayers compatibility', () => {
  it('GetCapabilities endpoint is reachable', async () => {
    evidence.attempt('CERT-CONN-01');
    const start = Date.now();
    const xml = await fetchCapabilitiesXml();
    const duration = Date.now() - start;

    expect(xml).toContain('WMS_Capabilities');

    evidence.record('CERT-CONN-01', 'pass', {
      durationMs: duration,
      notes: 'WMS GetCapabilities returned XML',
    });
  });

  it('OpenLayers parses WMS capabilities and service metadata', async () => {
    evidence.attempt('CERT-DISC-01');
    const start = Date.now();
    const capabilities = await readCapabilities();
    const duration = Date.now() - start;

    expect(capabilities.version).toBe('1.3.0');
    expect(capabilities.Service?.Name).toBe('WMS');
    expect(capabilities.Capability?.Request?.GetMap).toBeDefined();

    evidence.record('CERT-DISC-01', 'pass', {
      durationMs: duration,
      notes: 'ol/format/WMSCapabilities parsed WMS 1.3.0 metadata',
    });
  });

  it('OpenLayers discovers named WMS layers', async () => {
    evidence.attempt('CERT-DISC-02');
    const start = Date.now();
    const capabilities = await readCapabilities();
    const namedLayers = flattenLayers(capabilities.Capability?.Layer)
      .filter(layer => typeof layer.Name === 'string' && layer.Name.length > 0);
    const duration = Date.now() - start;

    expect(namedLayers.length).toBeGreaterThan(0);
    expect(capabilities.Capability?.Request?.GetMap?.Format).toContain('image/png');

    evidence.record('CERT-DISC-02', 'pass', {
      durationMs: duration,
      measuredCount: namedLayers.length,
      notes: `${namedLayers.length} named WMS layer(s) discovered`,
    });
  });

  it('OpenLayers ImageWMS configuration can render a PNG map', async () => {
    evidence.attempt('CERT-RNDR-01');
    const layerName = await discoverNamedLayer();
    const source = new ImageWMS({
      url: wmsUrl,
      params: {
        LAYERS: layerName,
        STYLES: '',
        VERSION: '1.3.0',
        FORMAT: 'image/png',
      },
      ratio: 1,
      hidpi: false,
    });

    expect(source.getUrl()).toBe(wmsUrl);
    expect(source.getParams().LAYERS).toBe(layerName);

    const params = new URLSearchParams({
      SERVICE: 'WMS',
      VERSION: '1.3.0',
      REQUEST: 'GetMap',
      LAYERS: layerName,
      STYLES: '',
      CRS: 'EPSG:3857',
      BBOX: '-20037508.342789244,-20037508.342789244,20037508.342789244,20037508.342789244',
      WIDTH: '256',
      HEIGHT: '256',
      FORMAT: 'image/png',
      TRANSPARENT: 'TRUE',
    });

    const start = Date.now();
    const resp = await fetch(`${wmsUrl}?${params}`);
    const duration = Date.now() - start;

    expect(resp.status).toBe(200);
    expect(resp.headers.get('content-type') ?? '').toContain('image/png');
    const buffer = await resp.arrayBuffer();
    expect(buffer.byteLength).toBeGreaterThan(8);
    expectPng(buffer);

    evidence.record('CERT-RNDR-01', 'pass', {
      durationMs: duration,
      notes: `WMS GetMap rendered image/png for layer '${layerName}'`,
    });
  });
});
