import { writeFile, mkdir, readFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const EVIDENCE_DIR = resolve(__dirname, '..', '..', 'evidence');

/** Read the installed esri-leaflet version from package.json. */
async function getEsriLeafletVersion(): Promise<string> {
  try {
    const pkgPath = resolve(__dirname, '..', '..', 'node_modules', 'esri-leaflet', 'package.json');
    const pkg = JSON.parse(await readFile(pkgPath, 'utf8'));
    return pkg.version;
  } catch {
    // Fallback to the spec range from our own package.json
    const ourPkg = JSON.parse(await readFile(resolve(__dirname, '..', '..', 'package.json'), 'utf8'));
    return ourPkg.dependencies?.['esri-leaflet'] ?? 'unknown';
  }
}

export interface CertResult {
  test_case_id: string;
  status: 'pass' | 'fail' | 'skip' | 'not-applicable';
  duration_ms: number | null;
  measured_count: number | null;
  measured_delta: number | null;
  notes: string;
  evidence_ref: string;
}

export interface CertEnvelope {
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

/** Build a certification evidence envelope from test results. */
export async function buildEnvelope(
  protocol: string,
  results: CertResult[],
  extensions: CertResult[],
  options?: {
    runId?: string;
    serverVersion?: string;
    clientVersion?: string;
    environment?: string;
  },
): Promise<CertEnvelope> {
  const runId = options?.runId ?? process.env.GITHUB_RUN_ID ?? new Date().toISOString().replace(/[^0-9T]/g, '').slice(0, 16) + 'Z';
  const clientVersion = options?.clientVersion ?? await getEsriLeafletVersion();
  const allResults = results;

  const summary = {
    total: allResults.length,
    passed: allResults.filter(r => r.status === 'pass').length,
    failed: allResults.filter(r => r.status === 'fail').length,
    skipped: allResults.filter(r => r.status === 'skip').length,
    not_applicable: allResults.filter(r => r.status === 'not-applicable').length,
  };

  return {
    schema_version: '1.0',
    run_id: runId,
    run_date: new Date().toISOString(),
    server_version: options?.serverVersion ?? process.env.GITHUB_SHA ?? 'local',
    client_lane: 'js',
    client_version: clientVersion,
    protocol,
    environment: options?.environment ?? (process.env.CI ? 'ci' : 'local'),
    results: allResults,
    summary,
    cite_results: null,
    extensions,
  };
}

/** Write a .cert.json envelope to the evidence directory. */
export async function writeEvidence(protocol: string, envelope: CertEnvelope): Promise<string> {
  await mkdir(EVIDENCE_DIR, { recursive: true });
  const filename = `${envelope.run_id}-js-${protocol}.cert.json`;
  const filepath = resolve(EVIDENCE_DIR, filename);
  await writeFile(filepath, JSON.stringify(envelope, null, 2) + '\n');
  return filepath;
}

/** Create a pass result. */
export function pass(certId: string, durationMs?: number, notes?: string): CertResult {
  return {
    test_case_id: certId,
    status: 'pass',
    duration_ms: durationMs ?? null,
    measured_count: null,
    measured_delta: null,
    notes: notes ?? '',
    evidence_ref: '',
  };
}

/** Create a fail result. */
export function fail(certId: string, notes: string, durationMs?: number): CertResult {
  return {
    test_case_id: certId,
    status: 'fail',
    duration_ms: durationMs ?? null,
    measured_count: null,
    measured_delta: null,
    notes,
    evidence_ref: '',
  };
}

/** Create a skip result. */
export function skip(certId: string, notes: string): CertResult {
  return {
    test_case_id: certId,
    status: 'skip',
    duration_ms: null,
    measured_count: null,
    measured_delta: null,
    notes,
    evidence_ref: '',
  };
}
