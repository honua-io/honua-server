/**
 * Cross-Client Certification Evidence collector for OpenLayers tests.
 *
 * Follows the envelope schema defined in:
 *   docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md
 *
 * Accumulates test results and writes .cert.json files in afterAll hooks.
 * Uses merge-on-write so that sibling test suites targeting the same protocol
 * (e.g. oapif-discovery + oapif-features both targeting 'ogc-features')
 * accumulate into one file rather than overwriting each other.
 */

import { writeFileSync, readFileSync, mkdirSync, existsSync } from 'node:fs';
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

/**
 * Per-protocol applicability derived from CROSS_CLIENT_CERTIFICATION_MATRIX.md.
 * IDs present are applicable; unrecorded applicable IDs emit 'skip'.
 * IDs absent from the set emit 'not-applicable'.
 */
const PROTOCOL_APPLICABILITY: Record<string, ReadonlySet<string>> = {
  'ogc-features': new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01', 'CERT-SCHM-02',
    'CERT-QFLT-01', 'CERT-QFLT-02', 'CERT-PAGE-01', 'CERT-PAGE-02',
    'CERT-GEOM-01', 'CERT-GEOM-02', 'CERT-ERRH-01', 'CERT-ERRH-02',
    'CERT-RNDR-01', 'CERT-RNDR-02',
  ]),
  mvt: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  featureserver: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01', 'CERT-SCHM-02',
    'CERT-QFLT-01', 'CERT-QFLT-02', 'CERT-PAGE-01', 'CERT-PAGE-02',
    'CERT-GEOM-01', 'CERT-GEOM-02', 'CERT-ERRH-01', 'CERT-ERRH-02',
    'CERT-RNDR-01', 'CERT-RNDR-02',
  ]),
  mapserver: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01', 'CERT-RNDR-01', 'CERT-RNDR-02',
  ]),
  odata: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01',
    'CERT-QFLT-01', 'CERT-PAGE-01', 'CERT-PAGE-02',
    'CERT-ERRH-01', 'CERT-ERRH-02', 'CERT-RNDR-01', 'CERT-RNDR-02',
  ]),
  wms: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  wmts: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
  ]),
  'admin-api': new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01',
  ]),
  // Pending formal addition to CROSS_CLIENT_CERTIFICATION_MATRIX.md
  wfs20: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01',
    'CERT-QFLT-01', 'CERT-GEOM-01',
    'CERT-ERRH-01', 'CERT-ERRH-02',
  ]),
};

/**
 * Protocols defined in the certification evidence spec.
 * wfs20 is NOT yet in the spec — excluded until formally added to
 * CROSS_CLIENT_CERTIFICATION_MATRIX.md. WFS tests still run but
 * write() returns null for non-spec protocols.
 */
const VALID_PROTOCOLS = new Set([
  'featureserver', 'mapserver', 'ogc-features', 'odata',
  'mvt', 'wms', 'wmts', 'admin-api',
]);

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
    const olPkgPath = resolve(__dirname, '..', '..', 'node_modules', 'ol', 'package.json');
    const pkg = JSON.parse(readFileSync(olPkgPath, 'utf-8'));
    return pkg.version ?? 'unknown';
  } catch {
    return 'unknown';
  }
}

function formatRunId(): string {
  return new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d+Z$/, 'Z');
}

/**
 * Return a run ID that is stable across all Vitest forks in one test run.
 * Priority: CERT_RUN_ID env → GITHUB_RUN_ID env → file sentinel in output dir.
 */
function getStableRunId(): string {
  if (process.env.CERT_RUN_ID) return process.env.CERT_RUN_ID;
  if (process.env.GITHUB_RUN_ID) return process.env.GITHUB_RUN_ID;

  const sentinel = resolve(certOutputDir(), '.cert-run-id');
  try {
    if (existsSync(sentinel)) return readFileSync(sentinel, 'utf-8').trim();
  } catch { /* fall through */ }

  const id = formatRunId();
  try {
    writeFileSync(sentinel, id + '\n', 'utf-8');
  } catch { /* best effort */ }
  return id;
}

/** Resolve the cert output directory (tests/js/). */
function certOutputDir(): string {
  return resolve(__dirname, '..', '..');
}

/** Deterministic merge filename (stable across forks for merge-on-write). */
function mergeFilename(protocol: string): string {
  return `js-${protocol}.cert.json`;
}

/** Spec-compliant filename: <run-id>-<client-lane>-<protocol>.cert.json */
function specFilename(runId: string, protocol: string): string {
  return `${runId}-js-${protocol}.cert.json`;
}

// ---------------------------------------------------------------------------
// Collector
// ---------------------------------------------------------------------------

export class EvidenceCollector {
  private readonly results = new Map<string, CertResult>();
  private readonly extensionMap = new Map<string, CertResult>();
  private readonly attempted = new Set<string>();
  readonly protocol: string;

  constructor(protocol: string) {
    this.protocol = protocol;
  }

  /**
   * Mark a core CERT ID as attempted. Call at the start of a test so that
   * if an assertion throws before record() is reached, build() emits 'fail'
   * instead of 'skip'.
   */
  attempt(testCaseId: string): void {
    this.attempted.add(testCaseId);
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
    this.extensionMap.set(testCaseId, {
      test_case_id: testCaseId,
      status,
      duration_ms: opts.durationMs ?? null,
      measured_count: opts.measuredCount ?? null,
      measured_delta: null,
      notes: opts.notes ?? '',
      evidence_ref: '',
    });
  }

  /**
   * Build the certification envelope.
   * Reads any existing cert file for this protocol and merges previously
   * recorded results so sibling suites (running in separate forks) accumulate
   * rather than overwrite.
   */
  build(): CertEnvelope {
    const runId = getStableRunId();
    const applicable = PROTOCOL_APPLICABILITY[this.protocol];

    // Read existing file from a sibling suite that ran in an earlier fork
    const priorResults = new Map<string, CertResult>();
    const priorExtensions = new Map<string, CertResult>();
    try {
      const filepath = resolve(certOutputDir(), mergeFilename(this.protocol));
      const existing: CertEnvelope = JSON.parse(readFileSync(filepath, 'utf-8'));
      for (const r of existing.results) {
        if (r.status === 'pass' || r.status === 'fail') {
          priorResults.set(r.test_case_id, r);
        }
      }
      for (const e of existing.extensions) {
        priorExtensions.set(e.test_case_id, e);
      }
    } catch {
      // No existing file yet
    }

    // Current results take precedence over prior
    const merged = new Map([...priorResults, ...this.results]);
    const mergedExt = new Map([...priorExtensions, ...this.extensionMap]);

    const allResults: CertResult[] = CORE_TEST_IDS.map(id => {
      const isApplicable = applicable?.has(id) ?? false;
      const recorded = merged.get(id);
      // Defense-in-depth: coerce non-applicable recorded IDs to not-applicable
      if (recorded && isApplicable) return recorded;
      // Attempted but not recorded means the test threw before record() — emit fail
      const wasAttempted = this.attempted.has(id);
      const status: CertStatus = isApplicable
        ? (wasAttempted ? 'fail' : 'skip')
        : 'not-applicable';
      const notes = wasAttempted
        ? 'Test attempted but assertion failed before evidence was recorded'
        : isApplicable ? 'Not covered by this test suite' : '';
      return {
        test_case_id: id,
        status,
        duration_ms: null,
        measured_count: null,
        measured_delta: null,
        notes,
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
      client_lane: 'js',
      client_version: getOlVersion(),
      protocol: this.protocol,
      environment: process.env.CI ? 'ci' : 'local',
      results: allResults,
      summary: counts,
      cite_results: null,
      extensions: [...mergedExt.values()],
    };
  }

  /**
   * Write the evidence file to tests/js/.
   * Produces two outputs:
   *   1. Merge file (`js-<protocol>.cert.json`) for cross-fork accumulation.
   *   2. Spec-compliant file (`<run-id>-js-<protocol>.cert.json`) matching
   *      the naming convention in CROSS_CLIENT_CERTIFICATION_EVIDENCE.md.
   * Skips writing for protocols not in the certification spec.
   */
  write(): string | null {
    if (!VALID_PROTOCOLS.has(this.protocol)) {
      return null;
    }
    const envelope = this.build();
    const outDir = certOutputDir();
    const content = JSON.stringify(envelope, null, 2) + '\n';

    // 1. Merge-on-write file (stable name for accumulation across Vitest forks)
    const mergePath = resolve(outDir, mergeFilename(this.protocol));
    writeFileSync(mergePath, content, 'utf-8');

    // 2. Spec-compliant file (run-id prefixed)
    const specDir = resolve(outDir, 'certification-evidence');
    mkdirSync(specDir, { recursive: true });
    const specPath = resolve(specDir, specFilename(envelope.run_id, this.protocol));
    writeFileSync(specPath, content, 'utf-8');

    return specPath;
  }
}
