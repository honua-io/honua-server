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

import { writeFileSync, readFileSync, mkdirSync, existsSync, unlinkSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));

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
  // Visual / style certification slice (ticket #478) — append-only.
  // See docs/gis/visual-style-certification-slice.md.
  'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
  'CERT-RNDR-LBL-01', 'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
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
    // Visual / style slice — applicable to all geometry-capable protocols.
    'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
    'CERT-RNDR-LBL-01',
  ]),
  mvt: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-ERRH-01', 'CERT-RNDR-01',
    // Visual / style slice — MVT covers symbol/line/fill/sprite/style URL.
    'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
    'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
  ]),
  featureserver: new Set([
    'CERT-CONN-01', 'CERT-CONN-02', 'CERT-AUTH-01', 'CERT-AUTH-02',
    'CERT-DISC-01', 'CERT-DISC-02', 'CERT-SCHM-01', 'CERT-SCHM-02',
    'CERT-QFLT-01', 'CERT-QFLT-02', 'CERT-PAGE-01', 'CERT-PAGE-02',
    'CERT-GEOM-01', 'CERT-GEOM-02', 'CERT-ERRH-01', 'CERT-ERRH-02',
    'CERT-RNDR-01', 'CERT-RNDR-02',
    // Visual / style slice — drawingInfo covers all categories except sprite.
    'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
    'CERT-RNDR-LBL-01', 'CERT-RNDR-URL-01',
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

const runIdSentinel = resolve(certOutputDir(), '.cert-run-id');
if (!process.env.CERT_RUN_ID && !process.env.GITHUB_RUN_ID) {
  process.on('exit', () => {
    try {
      if (existsSync(runIdSentinel)) {
        unlinkSync(runIdSentinel);
      }
    } catch {
      // Best-effort cleanup only.
    }
  });
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
  private readonly pendingAttempts = new Map<string, number>();
  private readonly pendingExtAttempts = new Map<string, number>();
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
    this.pendingAttempts.set(testCaseId, (this.pendingAttempts.get(testCaseId) ?? 0) + 1);
  }

  /**
   * Record a core CERT-* result.
   * Fail-wins: once a 'fail' is recorded for an ID, later non-fail calls
   * for the same ID are ignored so that duplicate CERT-ID mappings cannot
   * mask a failure.
   */
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
    // Close one pending attempt regardless of whether the result is accepted
    // (the test reached record(), so the attempt is no longer "in flight").
    const pending = this.pendingAttempts.get(testCaseId);
    if (pending !== undefined && pending > 0) {
      this.pendingAttempts.set(testCaseId, pending - 1);
    }
    const existing = this.results.get(testCaseId);
    if (existing?.status === 'fail' && status !== 'fail') return;
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

  /**
   * Mark an extension ID as attempted. Mirrors attempt() for core IDs:
   * if a test throws before recordExtension() is reached, build() emits
   * 'fail' instead of preserving a stale 'pass' from an earlier test.
   */
  attemptExtension(testCaseId: string): void {
    this.pendingExtAttempts.set(testCaseId, (this.pendingExtAttempts.get(testCaseId) ?? 0) + 1);
  }

  /**
   * Record an extension result (JS-EXT-*, OL-EXT-*, etc.).
   * Fail-wins: once a 'fail' is recorded for an ID, later non-fail calls
   * are ignored. A 'pass' also takes precedence over 'skip'/'not-applicable'
   * so that a subsequent lower-priority status cannot downgrade a result.
   */
  recordExtension(
    testCaseId: string,
    status: CertStatus,
    opts: {
      durationMs?: number;
      measuredCount?: number;
      notes?: string;
    } = {},
  ): void {
    // Close one pending attempt (mirrors record() logic).
    const pendingExt = this.pendingExtAttempts.get(testCaseId);
    if (pendingExt !== undefined && pendingExt > 0) {
      this.pendingExtAttempts.set(testCaseId, pendingExt - 1);
    }
    const existing = this.extensionMap.get(testCaseId);
    if (existing) {
      // fail > pass > skip > not-applicable
      if (existing.status === 'fail' && status !== 'fail') return;
      if (existing.status === 'pass' && status !== 'fail' && status !== 'pass') return;
    }
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

    const merged = new Map(priorResults);
    for (const [id, result] of this.results) {
      const prior = merged.get(id);
      if (!prior || prior.status !== 'fail' || result.status === 'fail') {
        merged.set(id, result);
      }
    }
    const mergedExt = new Map(priorExtensions);
    for (const [id, ext] of this.extensionMap) {
      const prior = mergedExt.get(id);
      if (!prior) {
        mergedExt.set(id, ext);
        continue;
      }
      // fail > pass > skip > not-applicable (mirrors recordExtension precedence)
      if (prior.status === 'fail' && ext.status !== 'fail') continue;
      if (prior.status === 'pass' && ext.status !== 'fail' && ext.status !== 'pass') continue;
      mergedExt.set(id, ext);
    }

    // Attempted-but-not-recorded extensions → fail (mirrors core CERT logic).
    // If a test called attemptExtension() but threw before recordExtension(),
    // override any prior 'pass' so a stale result cannot mask a failure.
    for (const [id, pending] of this.pendingExtAttempts) {
      if (pending > 0) {
        mergedExt.set(id, {
          test_case_id: id,
          status: 'fail',
          duration_ms: null,
          measured_count: null,
          measured_delta: null,
          notes: 'Test attempted but assertion failed before evidence was recorded',
          evidence_ref: '',
        });
      }
    }

    const allResults: CertResult[] = CORE_TEST_IDS.map(id => {
      const isApplicable = applicable?.has(id) ?? false;
      const recorded = merged.get(id);
      // If current run attempted this ID but didn't record it, a test threw
      // before record() was reached — treat as fail even if a prior pass exists.
      const attemptedNotRecorded = (this.pendingAttempts.get(id) ?? 0) > 0;
      if (recorded && isApplicable && !attemptedNotRecorded) return recorded;
      // Attempted but not recorded means the test threw before record() — emit fail
      const wasAttempted = this.pendingAttempts.has(id);
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
   *
   * SAFETY: Merge-on-write assumes sequential file execution. The vitest
   * config sets `fileParallelism: false` + `pool: 'forks'`, so each test
   * file's afterAll writes complete before the next file starts. Do not
   * enable parallel file execution without adding file-level locking here.
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
