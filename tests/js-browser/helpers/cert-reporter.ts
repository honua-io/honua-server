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

// CERT IDs that this reporter tracks. Results not matching these are ignored.
const CERT_IDS = [
  'CERT-CONN-01', 'CERT-RNDR-01',
];

const EXTENSION_IDS = [
  'JS-EXT-01', 'JS-EXT-02',
];

// Regex to extract cert IDs from test titles, e.g. "[CERT-RNDR-01]".
const CERT_ID_REGEX = /\[(CERT-[A-Z]+-\d+|JS-EXT-\d+)]/g;

class CertReporter implements Reporter {
  // Map<certId, Map<testId, result>> — inner map keyed by Playwright test ID
  // so retries (same test) overwrite while different tests aggregate correctly.
  private resultsByTest = new Map<string, Map<string, { status: CertResult['status']; duration_ms: number; notes: string }>>();

  onTestEnd(test: TestCase, result: TestResult): void {
    const ids = [...test.title.matchAll(CERT_ID_REGEX)].map((m) => m[1]);
    if (ids.length === 0) return;

    const status: CertResult['status'] =
      result.status === 'passed' ? 'pass'
      : result.status === 'skipped' ? 'skip'
      : 'fail';

    const notes = result.status !== 'passed' && result.status !== 'skipped'
      ? result.errors.map((e) => e.message ?? '').join('; ').slice(0, 500)
      : '';

    for (const id of ids) {
      if (!this.resultsByTest.has(id)) this.resultsByTest.set(id, new Map());
      // Last attempt per test wins (handles Playwright retries).
      this.resultsByTest.get(id)!.set(test.id, { status, duration_ms: result.duration, notes });
    }
  }

  async onEnd(result: FullResult): Promise<void> {
    const hasObservedResults = [...this.resultsByTest.values()].some((byTest) => byTest.size > 0);
    if (result.status !== 'passed' && !hasObservedResults) {
      return;
    }

    const runDate = new Date();
    const runId = runDate.toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');

    let clientVersion: string;
    try {
      clientVersion = getMapLibreVersion();
    } catch {
      clientVersion = 'unknown';
    }

    const buildResult = (id: string): CertResult => {
      const byTest = this.resultsByTest.get(id);
      if (!byTest || byTest.size === 0) {
        return {
          test_case_id: id, status: 'skip', duration_ms: null,
          measured_count: null, measured_delta: null, notes: '', evidence_ref: '',
        };
      }
      const entries = [...byTest.values()];
      // Aggregate: any fail across different tests → fail; else any pass → pass.
      const hasFail = entries.some((e) => e.status === 'fail');
      const hasPass = entries.some((e) => e.status === 'pass');
      const status: CertResult['status'] = hasFail ? 'fail' : hasPass ? 'pass' : 'skip';
      const totalDuration = entries.reduce((sum, e) => sum + e.duration_ms, 0);
      const failNotes = entries.filter((e) => e.status === 'fail').map((e) => e.notes).filter(Boolean).join('; ');
      return {
        test_case_id: id, status, duration_ms: totalDuration,
        measured_count: null, measured_delta: null,
        notes: hasFail ? failNotes : '', evidence_ref: '',
      };
    };

    const certResults = CERT_IDS.map(buildResult);
    const extResults = EXTENSION_IDS.map(buildResult);

    // Fill non-applicable CERT IDs that aren't relevant to MVT browser tests.
    const allCertIds = [
      'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
      'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01', 'CERT-SCHM-02',
      'CERT-QFLT-01', 'CERT-QFLT-02', 'CERT-PAGE-01', 'CERT-PAGE-02',
      'CERT-GEOM-01', 'CERT-GEOM-02', 'CERT-ERRH-01', 'CERT-ERRH-02',
      'CERT-RNDR-01', 'CERT-RNDR-02',
    ];

    const fullResults: CertResult[] = allCertIds.map((id) => {
      const tracked = certResults.find((r) => r.test_case_id === id);
      if (tracked) return tracked;
      // IDs covered by JS/featureserver automated tests.
      const coveredByVitest = [
        'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02', 'CERT-ERRH-01',
      ];
      if (coveredByVitest.includes(id)) {
        return {
          test_case_id: id,
          status: 'skip' as const,
          duration_ms: null,
          measured_count: null,
          measured_delta: null,
          notes: 'Covered by JS/featureserver automated tests.',
          evidence_ref: '',
        };
      }
      return {
        test_case_id: id,
        status: 'not-applicable' as const,
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

    const outDir = resolve(import.meta.dirname, '..', 'test-results');
    mkdirSync(outDir, { recursive: true });
    const filename = `${runId}-js-mvt.cert.json`;
    writeFileSync(resolve(outDir, filename), JSON.stringify(envelope, null, 2) + '\n');
  }
}

export default CertReporter;
