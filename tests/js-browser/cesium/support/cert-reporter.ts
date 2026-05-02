// Custom Playwright reporter that produces .cert.json certification evidence
// for the Cesium browser lane. Maps test titles containing CERT-* or
// JS-CES-* IDs to the evidence envelope described in
// docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md.

import type {
  FullResult,
  Reporter,
  TestCase,
  TestResult,
} from '@playwright/test/reporter';
import { writeFileSync, mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { getCesiumVersion } from './cesium-harness.js';

interface CertResult {
  test_case_id: string;
  status: 'pass' | 'fail' | 'skip' | 'not-applicable';
  duration_ms: number | null;
  measured_count: number | null;
  measured_delta: number | null;
  notes: string;
  evidence_ref: string;
}

interface CertEnvelope {
  schema_version: string;
  run_id: string;
  run_date: string;
  server_version: string;
  client_lane: string;
  client_version: string;
  protocol: string;
  environment: string;
  results: CertResult[];
  summary: { total: number; passed: number; failed: number; skipped: number; not_applicable: number };
  cite_results: null;
  extensions: CertResult[];
}

// 24-ID common-core matrix (18 base + 6 visual / style slice IDs).
const COMMON_CORE_IDS = [
  'CERT-CONN-01', 'CERT-CONN-02',
  'CERT-AUTH-01', 'CERT-AUTH-02',
  'CERT-DISC-01', 'CERT-DISC-02',
  'CERT-SCHM-01', 'CERT-SCHM-02',
  'CERT-QFLT-01', 'CERT-QFLT-02',
  'CERT-PAGE-01', 'CERT-PAGE-02',
  'CERT-GEOM-01', 'CERT-GEOM-02',
  'CERT-ERRH-01', 'CERT-ERRH-02',
  'CERT-RNDR-01', 'CERT-RNDR-02',
  'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
  'CERT-RNDR-LBL-01', 'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
] as const;

// Cesium imagery providers consume WMS, WMTS, OGC API Tiles, and OGC API Maps.
// Discovery / connection / rendering / error categories apply; query, schema,
// pagination, and geometry-fidelity categories do not (Cesium does not expose
// vector-feature query semantics for these protocols).
const APPLICABLE_PER_PROTOCOL: Record<string, ReadonlySet<string>> = {
  wms: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  wmts: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  'ogc-tiles': new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  'ogc-maps': new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  // 3D Tiles is a tile-tree primitive in Cesium's scene graph, not an
  // imagery layer. Cesium loads tileset.json over HTTP and resolves nested
  // tile content URIs from it; query/schema/pagination/geometry-fidelity
  // categories do not apply because there is no vector-feature semantics
  // exposed by the harness.
  '3d-tiles': new Set([
    'CERT-CONN-01', 'CERT-AUTH-01',
    'CERT-DISC-01',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
};

const CERT_ID_REGEX = /\[((?:[A-Z]+-)+\d+)]/g;

// Maps the test spec filename to a protocol value. Each spec file targets a
// single protocol, so file-based mapping is more reliable than parsing
// titles.
const PROTOCOL_BY_FILE: Record<string, string> = {
  'wms-imagery.spec.ts': 'wms',
  'wmts-imagery.spec.ts': 'wmts',
  'ogc-api-tiles.spec.ts': 'ogc-tiles',
  'ogc-api-maps.spec.ts': 'ogc-maps',
  '3d-tiles-scene.spec.ts': '3d-tiles',
};

function deriveProtocol(test: TestCase): string {
  const path = test.location?.file ?? '';
  for (const [suffix, protocol] of Object.entries(PROTOCOL_BY_FILE)) {
    if (path.endsWith(suffix)) return protocol;
  }
  return 'wms';
}

interface RecordedResult {
  status: CertResult['status'];
  duration_ms: number;
  notes: string;
}

// Extract notes for a test result. Failed tests carry the error messages so
// the cert envelope explains why the assertion broke. Skipped tests must
// carry the skip reason — the evidence contract
// (docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) requires intentional
// skips to record their reason in `notes`. Prefer a runtime or static
// `skip`/`fixme` annotation description (set by `test.skip(condition,
// description)`), and fall back to the test title (set by the
// `test.skip(title, body)` declaration form) so a permanently-deferred
// case like `[CERT-AUTH-01] ... — DEFERRED to honua-server-849` carries
// its deferral reason into the cert envelope.
function notesFor(test: TestCase, result: TestResult): string {
  if (result.status === 'failed') {
    return result.errors.map((e) => e.message ?? '').join('; ').slice(0, 500);
  }
  if (result.status === 'skipped') {
    const annotations = [
      ...(result.annotations ?? []),
      ...test.annotations,
    ];
    const description = annotations
      .filter((a) => a.type === 'skip' || a.type === 'fixme')
      .map((a) => a.description ?? '')
      .find((d) => d.length > 0);
    return (description ?? test.title).slice(0, 500);
  }
  return '';
}

class CertReporter implements Reporter {
  // Per-protocol result map.
  private byProtocol = new Map<string, Map<string, RecordedResult>>();
  private extByProtocol = new Map<string, Map<string, RecordedResult>>();

  onTestEnd(test: TestCase, result: TestResult): void {
    const ids = [...test.title.matchAll(CERT_ID_REGEX)].map((m) => m[1]);
    if (ids.length === 0) return;

    const protocol = deriveProtocol(test);

    const status: CertResult['status'] =
      result.status === 'passed' ? 'pass'
      : result.status === 'failed' ? 'fail'
      : result.status === 'skipped' ? 'skip'
      : 'skip';

    const notes = notesFor(test, result);

    const protocolMap = this.byProtocol.get(protocol) ?? new Map<string, RecordedResult>();
    const protocolExtMap = this.extByProtocol.get(protocol) ?? new Map<string, RecordedResult>();

    for (const id of ids) {
      const target = id.startsWith('JS-CES-') ? protocolExtMap : protocolMap;
      const prev = target.get(id);
      if (prev) {
        if (prev.status === 'fail') continue;
        if (prev.status === 'pass' && status !== 'fail') continue;
      }
      target.set(id, { status, duration_ms: result.duration, notes });
    }

    this.byProtocol.set(protocol, protocolMap);
    this.extByProtocol.set(protocol, protocolExtMap);
  }

  async onEnd(_result: FullResult): Promise<void> {
    const runDate = new Date();
    const runId = process.env.CERT_RUN_ID
      ?? process.env.GITHUB_RUN_ID
      ?? runDate.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
    const clientVersion = getCesiumVersion();
    const outDir = resolve(import.meta.dirname, '..', '..', 'test-results');
    mkdirSync(outDir, { recursive: true });

    // If no tests ran for a protocol but one is defined in APPLICABLE_PER_PROTOCOL,
    // still emit an envelope with all-applicable IDs as 'skip' so the baseline
    // diff has a deterministic comparison target.
    const protocolsToEmit = new Set<string>([
      ...this.byProtocol.keys(),
      ...this.extByProtocol.keys(),
    ]);

    if (protocolsToEmit.size === 0) {
      // Emit a single empty placeholder envelope so the baseline diff sees
      // no successful run rather than missing data.
      protocolsToEmit.add('wms');
    }

    for (const protocol of protocolsToEmit) {
      const recorded = this.byProtocol.get(protocol) ?? new Map<string, RecordedResult>();
      const recordedExt = this.extByProtocol.get(protocol) ?? new Map<string, RecordedResult>();
      const applicable = APPLICABLE_PER_PROTOCOL[protocol] ?? new Set<string>();

      const fullResults: CertResult[] = COMMON_CORE_IDS.map((id) => {
        const tracked = recorded.get(id);
        if (tracked) {
          return {
            test_case_id: id,
            status: tracked.status,
            duration_ms: tracked.duration_ms,
            measured_count: null,
            measured_delta: null,
            notes: tracked.notes,
            evidence_ref: '',
          };
        }
        if (applicable.has(id)) {
          return {
            test_case_id: id,
            status: 'skip',
            duration_ms: null,
            measured_count: null,
            measured_delta: null,
            notes: 'Not exercised by Cesium imagery lane for this protocol.',
            evidence_ref: '',
          };
        }
        return {
          test_case_id: id,
          status: 'not-applicable',
          duration_ms: null,
          measured_count: null,
          measured_delta: null,
          notes: '',
          evidence_ref: '',
        };
      });

      const summary = {
        total: fullResults.length,
        passed: fullResults.filter((r) => r.status === 'pass').length,
        failed: fullResults.filter((r) => r.status === 'fail').length,
        skipped: fullResults.filter((r) => r.status === 'skip').length,
        not_applicable: fullResults.filter((r) => r.status === 'not-applicable').length,
      };

      const extResults: CertResult[] = [...recordedExt.entries()].map(([id, r]) => ({
        test_case_id: id,
        status: r.status,
        duration_ms: r.duration_ms,
        measured_count: null,
        measured_delta: null,
        notes: r.notes,
        evidence_ref: '',
      }));

      const envelope: CertEnvelope = {
        schema_version: '1.0',
        run_id: runId,
        run_date: runDate.toISOString(),
        server_version: process.env.GITHUB_SHA ?? 'local',
        client_lane: 'js-cesium',
        client_version: clientVersion,
        protocol,
        environment: process.env.CI ? 'ci' : 'local',
        results: fullResults,
        summary,
        cite_results: null,
        extensions: extResults,
      };

      const filename = `${runId}-js-cesium-${protocol}.cert.json`;
      writeFileSync(resolve(outDir, filename), JSON.stringify(envelope, null, 2) + '\n');
    }
  }
}

export default CertReporter;
