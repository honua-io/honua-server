import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { buildEnvelope, writeEvidence, type CertResult } from './evidence.js';

/**
 * Common-core CERT-* IDs from the certification matrix.
 *
 * The base set is the original 18 IDs (CERT-{CONN,AUTH,DISC,SCHM,QFLT,PAGE,
 * GEOM,ERRH,RNDR}-0{1,2}). The visual / style certification slice (ticket
 * #478) appends the six per-category CERT-RNDR-{SYM,LIN,FIL,LBL,SPR,URL}-01
 * IDs. See docs/gis/visual-style-certification-slice.md for the slice
 * contract.
 */
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

/**
 * Per the matrix footnote §, mapserver evidence files record query-focused
 * categories as not-applicable when the client only exercises the rendering
 * path (export/identify). These are the IDs that do not apply.
 *
 * The visual / style slice IDs are also not applicable to the mapserver
 * rendering-only lane: drawingInfo per-category style assertions live on
 * FeatureServer, not the MapServer export endpoint.
 */
const MAPSERVER_NOT_APPLICABLE: ReadonlySet<string> = new Set([
  'CERT-QFLT-01', 'CERT-QFLT-02',
  'CERT-PAGE-01', 'CERT-PAGE-02',
  'CERT-GEOM-01', 'CERT-GEOM-02',
  'CERT-ERRH-02',
  'CERT-RNDR-SYM-01', 'CERT-RNDR-LIN-01', 'CERT-RNDR-FIL-01',
  'CERT-RNDR-LBL-01', 'CERT-RNDR-SPR-01', 'CERT-RNDR-URL-01',
]);

/** Extract CERT IDs from test title, e.g. "[CERT-CONN-01]" or "[EL-EXT-01]". */
function extractCertIds(title: string): string[] {
  const matches = title.match(/\[([A-Z]+-[A-Z]+-\d+)\]/g);
  if (!matches) return [];
  return matches.map(m => m.slice(1, -1));
}

/** Determine protocol from suite/file path and test title. */
function getProtocol(filePath: string, testTitle: string): 'featureserver' | 'mapserver' {
  if (filePath.includes('dynamic-map-layer') || testTitle.includes('DynamicMapLayer')) return 'mapserver';
  return 'featureserver';
}

/** Build a not-applicable result. */
function notApplicable(certId: string, notes: string): CertResult {
  return { test_case_id: certId, status: 'not-applicable', duration_ms: null, measured_count: null, measured_delta: null, notes, evidence_ref: '' };
}

/** Build a skip result for untested IDs. */
function skipResult(certId: string, notes: string): CertResult {
  return { test_case_id: certId, status: 'skip', duration_ms: null, measured_count: null, measured_delta: null, notes, evidence_ref: '' };
}

/**
 * Custom Playwright reporter that collects test results and writes
 * certification evidence envelopes (.cert.json) after the suite completes.
 *
 * Seeds the full 18-ID common-core matrix per the evidence spec so every
 * protocol envelope contains all CERT-* entries even when the browser suite
 * does not exercise them.
 */
export default class CertReporter implements Reporter {
  private results: Map<string, { certIds: string[]; status: 'pass' | 'fail' | 'skip'; duration: number; protocol: 'featureserver' | 'mapserver'; notes: string; measuredCount: number | null; measuredDelta: number | null }> = new Map();

  onTestEnd(test: TestCase, result: TestResult): void {
    const certIds = extractCertIds(test.title);
    if (certIds.length === 0) return;

    const filePath = test.location.file;
    const protocol = getProtocol(filePath, test.title);
    const status = result.status === 'passed' ? 'pass'
      : result.status === 'skipped' ? 'skip'
      : 'fail';
    const notes = result.status === 'failed'
      ? (result.errors?.map(e => e.message).join('; ') ?? 'Test failed')
      : '';

    // Extract measured_count / measured_delta from test annotations
    const countAnnotation = test.annotations.find(a => a.type === 'measured_count');
    const deltaAnnotation = test.annotations.find(a => a.type === 'measured_delta');
    const measuredCount = countAnnotation?.description != null ? Number(countAnnotation.description) : null;
    const measuredDelta = deltaAnnotation?.description != null ? Number(deltaAnnotation.description) : null;

    for (const certId of certIds) {
      const key = `${protocol}:${certId}`;
      const existing = this.results.get(key);
      // Keep the worst status: fail > pass > skip
      if (existing) {
        if (existing.status === 'fail') continue;
        if (existing.status === 'pass' && status !== 'fail') continue;
      }

      this.results.set(key, {
        certIds: [certId],
        status,
        duration: result.duration,
        protocol,
        notes,
        measuredCount,
        measuredDelta,
      });
    }
  }

  async onEnd(result: FullResult): Promise<void> {
    // Do not emit evidence if the run was interrupted or timed out — the results are incomplete
    if (result.status === 'interrupted' || result.status === 'timedout') {
      console.warn(`\n⚠️  Skipping evidence emission: run ${result.status}`);
      return;
    }

    // Do not emit evidence if no test actually executed (all skips means setup likely failed)
    const hasExecutedTest = [...this.results.values()].some(r => r.status === 'pass' || r.status === 'fail');
    if (!hasExecutedTest) {
      console.warn('\n⚠️  Skipping evidence emission: no tests passed or failed (possible setup abort)');
      return;
    }

    // Determine which protocols were exercised
    const protocols = new Set<'featureserver' | 'mapserver'>();
    for (const entry of this.results.values()) {
      protocols.add(entry.protocol);
    }
    // Always emit featureserver (primary protocol for this suite)
    protocols.add('featureserver');

    for (const protocol of protocols) {
      const results: CertResult[] = [];
      const extensions: CertResult[] = [];

      // Seed full common-core matrix
      for (const certId of COMMON_CORE_IDS) {
        const key = `${protocol}:${certId}`;
        const executed = this.results.get(key);

        if (executed) {
          results.push({
            test_case_id: certId,
            status: executed.status,
            duration_ms: executed.duration,
            measured_count: executed.measuredCount,
            measured_delta: executed.measuredDelta,
            notes: executed.notes,
            evidence_ref: '',
          });
        } else if (protocol === 'mapserver' && MAPSERVER_NOT_APPLICABLE.has(certId)) {
          results.push(notApplicable(certId, 'Not applicable to MapServer rendering-only lane'));
        } else {
          results.push(skipResult(certId, 'Not exercised by esri-leaflet browser suite'));
        }
      }

      // Collect extension results for this protocol
      for (const [compositeKey, entry] of this.results) {
        if (entry.protocol !== protocol) continue;
        const certId = compositeKey.slice(compositeKey.indexOf(':') + 1);
        if (certId.startsWith('EL-EXT-')) {
          extensions.push({
            test_case_id: certId,
            status: entry.status,
            duration_ms: entry.duration,
            measured_count: entry.measuredCount,
            measured_delta: entry.measuredDelta,
            notes: entry.notes,
            evidence_ref: '',
          });
        }
      }

      const envelope = await buildEnvelope(protocol, results, extensions);
      const path = await writeEvidence(protocol, envelope);
      console.log(`\n📋 Certification evidence written: ${path}`);
    }
  }
}
