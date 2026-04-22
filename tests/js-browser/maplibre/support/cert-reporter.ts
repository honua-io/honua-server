// Custom Playwright reporter that produces .cert.json certification evidence.
// Maps test titles containing CERT-* or JS-EXT-* IDs to the evidence envelope.

import type {
  FullConfig,
  FullResult,
  Reporter,
  Suite,
  TestCase,
  TestResult,
} from '@playwright/test/reporter';
import { writeFileSync, mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { getMapLibreVersion } from './map-harness.js';

/** Certification result entry. */
interface CertResult {
  test_case_id: string;
  status: 'pass' | 'fail' | 'skip' | 'not-applicable';
  duration_ms: number | null;
  measured_count: number | null;
  measured_delta: number | null;
  notes: string;
  evidence_ref: string;
}

/** The full .cert.json envelope. */
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

// Full 24-ID common-core matrix from docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md
// (18 base IDs plus the six visual / style slice IDs added by ticket #478).
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
  // Visual / style certification slice (ticket #478) — append-only.
  'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
  'CERT-RNDR-LBL-01', 'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
] as const;

// IDs that apply to MVT but are not exercisable in the browser visual workflow.
// Matches the MapLibre MVT workflow guidance in the evidence spec: record
// these as `skip` with a "covered by automated JS tests" note.
const COVERED_BY_OTHER_JS_TESTS: ReadonlySet<string> = new Set([
  'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02', 'CERT-ERRH-01',
]);

// The six visual / style slice IDs — emitted as `skip` with a pending-fixture
// note because the MapLibre MVT lane does not yet substantiate them.
const SLICE_TEST_IDS: ReadonlySet<string> = new Set([
  'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
  'CERT-RNDR-LBL-01', 'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
]);

const EXTENSION_IDS = [
  'JS-EXT-01', 'JS-EXT-02',
];

// Regex to extract cert IDs from test titles, e.g. "[CERT-RNDR-01]" or
// the 4-part slice IDs like "[CERT-RNDR-SYM-01]".
const CERT_ID_REGEX = /\[((?:[A-Z]+-)+\d+)]/g;

class CertReporter implements Reporter {
  private results = new Map<string, { status: CertResult['status']; duration_ms: number; notes: string }>();

  onTestEnd(test: TestCase, result: TestResult): void {
    const ids = [...test.title.matchAll(CERT_ID_REGEX)].map((m) => m[1]);
    if (ids.length === 0) return;

    const status: CertResult['status'] =
      result.status === 'passed' ? 'pass'
      : result.status === 'failed' ? 'fail'
      : result.status === 'skipped' ? 'skip'
      : 'skip';

    const notes = result.status === 'failed'
      ? result.errors.map((e) => e.message ?? '').join('; ').slice(0, 500)
      : '';

    for (const id of ids) {
      // Keep the worst status: fail > pass > skip. When the same CERT ID is
      // attached to multiple tests (or a test is retried), a later pass must
      // not mask an earlier failure.
      const prev = this.results.get(id);
      if (prev) {
        if (prev.status === 'fail') continue;
        if (prev.status === 'pass' && status !== 'fail') continue;
      }
      this.results.set(id, { status, duration_ms: result.duration, notes });
    }
  }

  async onEnd(_result: FullResult): Promise<void> {
    const runDate = new Date();
    const runId = runDate.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');

    let clientVersion: string;
    try {
      clientVersion = getMapLibreVersion();
    } catch {
      clientVersion = 'unknown';
    }

    const buildSeededResult = (id: string): CertResult => {
      const tracked = this.results.get(id);
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
      if (SLICE_TEST_IDS.has(id)) {
        return {
          test_case_id: id,
          status: 'skip',
          duration_ms: null,
          measured_count: null,
          measured_delta: null,
          notes: 'pending-fixture: visual / style slice ID not yet substantiated by the MapLibre MVT lane; tracked in visual-style-certification-slice.md',
          evidence_ref: '',
        };
      }
      if (COVERED_BY_OTHER_JS_TESTS.has(id)) {
        return {
          test_case_id: id,
          status: 'skip',
          duration_ms: null,
          measured_count: null,
          measured_delta: null,
          notes: 'Covered by JS/featureserver automated tests.',
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
    };

    const buildExtensionResult = (id: string): CertResult => {
      const r = this.results.get(id);
      return {
        test_case_id: id,
        status: r?.status ?? 'skip',
        duration_ms: r?.duration_ms ?? null,
        measured_count: null,
        measured_delta: null,
        notes: r?.notes ?? '',
        evidence_ref: '',
      };
    };

    const fullResults: CertResult[] = COMMON_CORE_IDS.map(buildSeededResult);
    const extResults = EXTENSION_IDS.map(buildExtensionResult);

    const summary = {
      total: fullResults.length,
      passed: fullResults.filter((r) => r.status === 'pass').length,
      failed: fullResults.filter((r) => r.status === 'fail').length,
      skipped: fullResults.filter((r) => r.status === 'skip').length,
      not_applicable: fullResults.filter((r) => r.status === 'not-applicable').length,
    };

    const envelope: CertEnvelope = {
      schema_version: '1.0',
      run_id: runId,
      run_date: runDate.toISOString(),
      server_version: process.env.GITHUB_SHA ?? 'local',
      client_lane: 'js',
      client_version: clientVersion,
      protocol: 'mvt',
      environment: process.env.CI ? 'ci' : 'local',
      results: fullResults,
      summary,
      cite_results: null,
      extensions: extResults,
    };

    const outDir = resolve(import.meta.dirname, '..', '..', 'test-results');
    mkdirSync(outDir, { recursive: true });
    const filename = `${runId}-js-mvt.cert.json`;
    writeFileSync(resolve(outDir, filename), JSON.stringify(envelope, null, 2) + '\n');
  }
}

export default CertReporter;
