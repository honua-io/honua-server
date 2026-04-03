import type { FullConfig, FullResult, Reporter, Suite, TestCase, TestResult } from '@playwright/test/reporter';
import { buildEnvelope, writeEvidence, type CertResult } from './evidence.js';

/** Extract CERT IDs from test title, e.g. "[CERT-CONN-01]" or "[EL-EXT-01]". */
function extractCertIds(title: string): string[] {
  const matches = title.match(/\[([A-Z]+-[A-Z]+-\d+)\]/g);
  if (!matches) return [];
  return matches.map(m => m.slice(1, -1));
}

/** Determine protocol from suite/file path. */
function getProtocol(filePath: string): 'featureserver' | 'mapserver' {
  if (filePath.includes('dynamic-map-layer')) return 'mapserver';
  return 'featureserver';
}

/**
 * Custom Playwright reporter that collects test results and writes
 * certification evidence envelopes (.cert.json) after the suite completes.
 */
export default class CertReporter implements Reporter {
  private results: Map<string, { certIds: string[]; status: 'pass' | 'fail' | 'skip'; duration: number; protocol: 'featureserver' | 'mapserver'; notes: string }> = new Map();

  onTestEnd(test: TestCase, result: TestResult): void {
    const certIds = extractCertIds(test.title);
    if (certIds.length === 0) return;

    const filePath = test.location.file;
    const protocol = getProtocol(filePath);
    const status = result.status === 'passed' ? 'pass'
      : result.status === 'skipped' ? 'skip'
      : 'fail';
    const notes = result.status === 'failed'
      ? (result.errors?.map(e => e.message).join('; ') ?? 'Test failed')
      : '';

    for (const certId of certIds) {
      const existing = this.results.get(certId);
      // If we already have a result for this CERT ID, keep the worst status
      if (existing && existing.status === 'fail') continue;

      this.results.set(certId, {
        certIds: [certId],
        status,
        duration: result.duration,
        protocol,
        notes,
      });
    }
  }

  async onEnd(_result: FullResult): Promise<void> {
    const byProtocol = new Map<string, { results: CertResult[]; extensions: CertResult[] }>();

    for (const [certId, entry] of this.results) {
      const proto = entry.protocol;
      if (!byProtocol.has(proto)) {
        byProtocol.set(proto, { results: [], extensions: [] });
      }

      const certResult: CertResult = {
        test_case_id: certId,
        status: entry.status,
        duration_ms: entry.duration,
        measured_count: null,
        measured_delta: null,
        notes: entry.notes,
        evidence_ref: '',
      };

      const bucket = byProtocol.get(proto)!;
      if (certId.startsWith('EL-EXT-')) {
        bucket.extensions.push(certResult);
      } else {
        bucket.results.push(certResult);
      }
    }

    for (const [protocol, { results, extensions }] of byProtocol) {
      const envelope = buildEnvelope(protocol, results, extensions);
      const path = await writeEvidence(protocol, envelope);
      console.log(`\n📋 Certification evidence written: ${path}`);
    }
  }
}
