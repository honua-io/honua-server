/**
 * WMTS 1.0.0 client compatibility tests via OpenLayers.
 *
 * Exercises: GetCapabilities parsing through ol/format/WMTSCapabilities,
 * WMTS source option construction, and GetTile PNG retrieval from the real
 * WMTS route surface.
 */

import '../shared/ol-node-setup.js';
import WMTSCapabilities from 'ol/format/WMTSCapabilities.js';
import WMTS, { optionsFromCapabilities } from 'ol/source/WMTS.js';
import { wmtsUrl } from '../shared/config.js';
import { EvidenceCollector } from '../shared/evidence.js';

const evidence = new EvidenceCollector('wmts');

type WmtsLayer = {
  Identifier?: string;
  Title?: string;
  Format?: string[];
  TileMatrixSetLink?: Array<{
    TileMatrixSet?: string;
  }>;
};

type WmtsTileMatrixSet = {
  Identifier?: string;
};

type WmtsCapabilitiesDocument = {
  version?: string;
  ServiceIdentification?: {
    ServiceType?: string;
  };
  Contents?: {
    Layer?: WmtsLayer[];
    TileMatrixSet?: WmtsTileMatrixSet[];
  };
};

let capabilitiesXmlPromise: Promise<string> | undefined;
let parsedCapabilities: WmtsCapabilitiesDocument | undefined;

afterAll(() => {
  evidence.write();
});

async function fetchCapabilitiesXml(): Promise<string> {
  capabilitiesXmlPromise ??= (async () => {
    const params = new URLSearchParams({
      SERVICE: 'WMTS',
      VERSION: '1.0.0',
      REQUEST: 'GetCapabilities',
    });
    const resp = await fetch(`${wmtsUrl}?${params}`);
    if (!resp.ok) {
      throw new Error(`WMTS GetCapabilities returned ${resp.status}`);
    }

    return resp.text();
  })();

  return capabilitiesXmlPromise;
}

async function readCapabilities(): Promise<WmtsCapabilitiesDocument> {
  if (parsedCapabilities) return parsedCapabilities;

  const xml = await fetchCapabilitiesXml();
  const parsed = new WMTSCapabilities().read(xml) as WmtsCapabilitiesDocument | null;
  if (!parsed) {
    throw new Error('OpenLayers returned no parsed WMTS capabilities document');
  }

  parsedCapabilities = parsed;
  return parsedCapabilities;
}

async function discoverLayerAndMatrixSet(): Promise<{ layer: WmtsLayer; matrixSet: string }> {
  const capabilities = await readCapabilities();
  const layer = capabilities.Contents?.Layer?.find(candidate => candidate.Identifier);
  if (!layer?.Identifier) {
    throw new Error('WMTS capabilities did not advertise a layer identifier');
  }

  const matrixSet =
    layer.TileMatrixSetLink?.find(link => link.TileMatrixSet)?.TileMatrixSet ??
    capabilities.Contents?.TileMatrixSet?.find(candidate => candidate.Identifier)?.Identifier;

  if (!matrixSet) {
    throw new Error(`WMTS layer '${layer.Identifier}' did not advertise a tile matrix set`);
  }

  return { layer, matrixSet };
}

function expectPng(buffer: ArrayBuffer): void {
  const bytes = Array.from(new Uint8Array(buffer.slice(0, 8)));
  expect(bytes).toEqual([137, 80, 78, 71, 13, 10, 26, 10]);
}

describe('WMTS 1.0.0 OpenLayers compatibility', () => {
  it('GetCapabilities endpoint is reachable', async () => {
    evidence.attempt('CERT-CONN-01');
    const start = Date.now();
    const xml = await fetchCapabilitiesXml();
    const duration = Date.now() - start;

    expect(xml).toContain('Capabilities');

    evidence.record('CERT-CONN-01', 'pass', {
      durationMs: duration,
      notes: 'WMTS GetCapabilities returned XML',
    });
  });

  it('OpenLayers parses WMTS capabilities and service metadata', async () => {
    evidence.attempt('CERT-DISC-01');
    const start = Date.now();
    const capabilities = await readCapabilities();
    const duration = Date.now() - start;

    expect(capabilities.version).toBe('1.0.0');
    expect(capabilities.ServiceIdentification?.ServiceType).toBe('OGC WMTS');
    expect(capabilities.Contents).toBeDefined();

    evidence.record('CERT-DISC-01', 'pass', {
      durationMs: duration,
      notes: 'ol/format/WMTSCapabilities parsed WMTS 1.0.0 metadata',
    });
  });

  it('OpenLayers discovers WMTS layers and tile matrix sets', async () => {
    evidence.attempt('CERT-DISC-02');
    const start = Date.now();
    const capabilities = await readCapabilities();
    const layers = capabilities.Contents?.Layer ?? [];
    const tileMatrixSets = capabilities.Contents?.TileMatrixSet ?? [];
    const duration = Date.now() - start;

    expect(layers.length).toBeGreaterThan(0);
    expect(tileMatrixSets.length).toBeGreaterThan(0);
    expect(layers[0].Format).toContain('image/png');

    evidence.record('CERT-DISC-02', 'pass', {
      durationMs: duration,
      measuredCount: layers.length,
      notes: `${layers.length} WMTS layer(s), ${tileMatrixSets.length} tile matrix set(s) discovered`,
    });
  });

  it('OpenLayers WMTS source options can fetch a PNG tile', async () => {
    evidence.attempt('CERT-RNDR-01');
    const capabilities = await readCapabilities();
    const { layer, matrixSet } = await discoverLayerAndMatrixSet();
    const layerId = layer.Identifier!;
    const sourceOptions = optionsFromCapabilities(capabilities, {
      layer: layerId,
      matrixSet,
      requestEncoding: 'KVP',
      style: 'default',
      format: 'image/png',
    });

    expect(sourceOptions).not.toBeNull();
    const source = new WMTS(sourceOptions!);
    expect(source.getLayer()).toBe(layerId);
    expect(source.getMatrixSet()).toBe(matrixSet);
    expect(source.getFormat()).toBe('image/png');

    const params = new URLSearchParams({
      SERVICE: 'WMTS',
      VERSION: '1.0.0',
      REQUEST: 'GetTile',
      LAYER: layerId,
      STYLE: 'default',
      FORMAT: 'image/png',
      TILEMATRIXSET: matrixSet,
      TILEMATRIX: '0',
      TILEROW: '0',
      TILECOL: '0',
    });

    const start = Date.now();
    const resp = await fetch(`${wmtsUrl}?${params}`);
    const duration = Date.now() - start;

    expect(resp.status).toBe(200);
    expect(resp.headers.get('content-type') ?? '').toContain('image/png');
    const buffer = await resp.arrayBuffer();
    expect(buffer.byteLength).toBeGreaterThan(8);
    expectPng(buffer);

    evidence.record('CERT-RNDR-01', 'pass', {
      durationMs: duration,
      notes: `WMTS GetTile rendered image/png for layer '${layerId}' and matrix set '${matrixSet}'`,
    });
  });
});
