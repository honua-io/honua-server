/**
 * Cross-Client Certification Evidence collector for OpenLayers tests.
 *
 * Follows the envelope schema defined in:
 *   docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md
 *
 * Accumulates test results and writes .cert.json files in afterAll hooks.
 */

import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { execSync } from 'node:child_process';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export type CertStatus = 'pass' | 'fail' | 'skip' | 'not-applicable';

export interface CertResult {
  test_case_id: string;
  status: CertStatus;
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
  summary: {
    total: number;
    passed: number;
    failed: number;
    skipped: number;
    not_applicable: number;
  };
  cite_results: null;
  extensions: CertResult[];
}

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const CORE_TEST_IDS = [
  'CERT-CONN-01', 'CERT-CONN-02',
  'CERT-AUTH-01', 'CERT-AUTH-02',
  'CERT-DISC-01', 'CERT-DISC-02',
  'CERT-SCHM-01', 'CERT-SCHM-02',
  'CERT-QFLT-01', 'CERT-QFLT-02',
  'CERT-PAGE-01', 'CERT-PAGE-02',
  'CERT-GEOM-01', 'CERT-GEOM-02',
  'CERT-ERRH-01', 'CERT-ERRH-02',
  'CERT-RNDR-01', 'CERT-RNDR-02',
] as const;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function getServerVersion(): string {
  try {
    return execSync('git rev-parse HEAD', { encoding: 'utf-8' }).trim();
  } catch {
    return 'unknown';
  }
}

function getOlVersion(): string {
  try {
    const pkg = require('ol/package.json');
    return pkg.version ?? 'unknown';
  } catch {
    return 'unknown';
  }
}

function makeRunId(): string {
  return new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
}

// ---------------------------------------------------------------------------
// Collector
// ---------------------------------------------------------------------------

export class EvidenceCollector {
  private readonly results = new Map<string, CertResult>();
  private readonly extensions: CertResult[] = [];
  readonly protocol: string;

  constructor(protocol: string) {
    this.protocol = protocol;
  }

  /** Record a core CERT-* result. */
  record(
    testCaseId: string,
    status: CertStatus,
    opts: {
      durationMs?: number;
      measuredCount?: number;
      measuredDelta?: number;
      notes?: string;
      evidenceRef?: string;
    } = {},
  ): void {
    this.results.set(testCaseId, {
      test_case_id: testCaseId,
      status,
      duration_ms: opts.durationMs ?? null,
      measured_count: opts.measuredCount ?? null,
      measured_delta: opts.measuredDelta ?? null,
      notes: opts.notes ?? '',
      evidence_ref: opts.evidenceRef ?? '',
    });
  }

  /** Record an extension result (JS-EXT-*, OL-EXT-*, etc.). */
  recordExtension(
    testCaseId: string,
    status: CertStatus,
    opts: {
      durationMs?: number;
      measuredCount?: number;
      notes?: string;
    } = {},
  ): void {
    this.extensions.push({
      test_case_id: testCaseId,
      status,
      duration_ms: opts.durationMs ?? null,
      measured_count: opts.measuredCount ?? null,
      measured_delta: null,
      notes: opts.notes ?? '',
      evidence_ref: '',
    });
  }

  /** Build the full certification envelope. */
  build(): CertEnvelope {
    const runId = makeRunId();
    const allResults: CertResult[] = CORE_TEST_IDS.map(id => {
      const recorded = this.results.get(id);
      if (recorded) return recorded;
      return {
        test_case_id: id,
        status: 'not-applicable' as CertStatus,
        duration_ms: null,
        measured_count: null,
        measured_delta: null,
        notes: '',
        evidence_ref: '',
      };
    });

    const counts = allResults.reduce(
      (acc, r) => {
        acc.total++;
        if (r.status === 'pass') acc.passed++;
        else if (r.status === 'fail') acc.failed++;
        else if (r.status === 'skip') acc.skipped++;
        else acc.not_applicable++;
        return acc;
      },
      { total: 0, passed: 0, failed: 0, skipped: 0, not_applicable: 0 },
    );

    return {
      schema_version: '1.0',
      run_id: runId,
      run_date: new Date().toISOString(),
      server_version: getServerVersion(),
      client_lane: 'js-openlayers',
      client_version: getOlVersion(),
      protocol: this.protocol,
      environment: process.env.CI ? 'ci' : 'local',
      results: allResults,
      summary: counts,
      cite_results: null,
      extensions: this.extensions,
    };
  }

  /** Write the evidence file to the tests/js/ directory. */
  write(): string {
    const envelope = this.build();
    const filename = `openlayers-${this.protocol}.cert.json`;
    const filepath = resolve(__dirname, '..', '..', filename);
    writeFileSync(filepath, JSON.stringify(envelope, null, 2) + '\n', 'utf-8');
    return filepath;
  }
}
