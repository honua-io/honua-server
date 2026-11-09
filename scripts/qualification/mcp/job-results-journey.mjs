import { createHash, randomUUID } from 'node:crypto';
import { setTimeout } from 'node:timers/promises';
import { pathToFileURL } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { collectPages, discoverInstalledTools, openInstalledClient, sourceSha256 } from './installed-client.mjs';

const distance = 25.5;
const srid = 3857;
const maxBytes = 512 * 1024;

class JobJourneyFailure extends Error {}

function payload(result) {
  const structured = result.structuredContent;
  const text = (result.content ?? []).filter(block => block.type === 'text').flatMap(block => {
    try { return [JSON.parse(block.text)]; } catch { return []; }
  });
  if (!structured || text.length !== 1 || !isDeepStrictEqual(structured, text[0])) throw new JobJourneyFailure('content-channel-mismatch');
  return structured;
}

async function call(client, name, args) {
  const result = await client.callTool({ name, arguments: args }, undefined, { timeout: 5_000 });
  if ((result.isError ?? false) !== false) throw new JobJourneyFailure('unexpected-tool-status');
  return payload(result);
}

async function deniedResults(client, jobId) {
  const result = await client.callTool({ name: 'honua_get_job_results', arguments: { jobId } }, undefined, { timeout: 5_000 });
  if (result.isError !== true) throw new JobJourneyFailure('unexpected-tool-status');
  return payload(result);
}

async function listJobs(client) {
  return collectPages(async cursor => {
    const result = await call(client, 'honua_list_jobs', { limit: 200, ...(cursor ? { cursor } : {}) });
    if (!Array.isArray(result.jobs) || result.jobCount !== result.jobs.length || result.jobs.length > 200) throw new JobJourneyFailure('invalid-job-page');
    return { items: result.jobs, nextCursor: result.nextCursor };
  }, 'incomplete-job-pagination');
}

async function artifactBytes(artifact, owner, apiKey, jobId) {
  const prefix = 'data:application/geo+json;base64,';
  if (artifact.uri?.startsWith(prefix)) {
    if (artifact.uri.length > maxBytes * 2) throw new JobJourneyFailure('oversized-artifact');
    return Buffer.from(artifact.uri.slice(prefix.length), 'base64');
  }
  const uri = new URL(artifact.uri, owner.endpoint);
  if (uri.origin !== owner.endpoint.origin || uri.username || uri.password || uri.search || uri.hash
    || uri.pathname !== `/api/geoprocessing/jobs/${encodeURIComponent(jobId)}/artifacts/0/content`) throw new JobJourneyFailure('invalid-artifact-reference');
  let response;
  try { response = await owner.fetchOwned(uri, { headers: { 'X-API-Key': apiKey } }); }
  catch { throw new JobJourneyFailure('unavailable-artifact'); }
  if (!response.ok || !response.headers.get('content-type')?.startsWith('application/geo+json')) throw new JobJourneyFailure('unavailable-artifact');
  const chunks = [];
  let count = 0;
  for await (const chunk of response.body) {
    count += chunk.length;
    if (count > maxBytes) throw new JobJourneyFailure('oversized-artifact');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

function verifyGeometry(bytes) {
  if (!bytes.length || bytes.length > maxBytes) throw new JobJourneyFailure('invalid-artifact-size');
  const feature = JSON.parse(bytes.toString('utf8'));
  if (feature.type !== 'Feature' || feature.properties?.processId !== 'geometry.buffer'
    || feature.properties.inputSrid !== srid || feature.properties.bufferDistance !== distance
    || feature.geometry?.type !== 'Polygon' || feature.geometry.coordinates?.length !== 1) throw new JobJourneyFailure('invalid-buffer-result');
  const ring = feature.geometry.coordinates[0];
  if (!Array.isArray(ring) || ring.length < 5 || ring.length > 1000 || !isDeepStrictEqual(ring[0], ring.at(-1))) throw new JobJourneyFailure('invalid-buffer-ring');
  let area = 0;
  for (let i = 0; i < ring.length; i++) {
    const point = ring[i];
    if (!Array.isArray(point) || point.length !== 2 || !point.every(Number.isFinite)
      || Math.abs(Math.hypot(...point) - distance) > 1e-6) throw new JobJourneyFailure('invalid-buffer-coordinate');
    if (i) area += ring[i - 1][0] * point[1] - point[0] * ring[i - 1][1];
  }
  area = Math.abs(area) / 2;
  if (area < Math.PI * distance ** 2 * 0.9 || area > Math.PI * distance ** 2 * 1.01) throw new JobJourneyFailure('invalid-buffer-area');
  return { bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex'), area, srid, distance };
}

export async function jobResultsJourney({ baseUrl, apiKey, otherApiKey, missingJobId = `missing-${randomUUID()}` }) {
  if (!apiKey || !otherApiKey || apiKey === otherApiKey) throw new JobJourneyFailure('distinct-callers-required');
  const owner = await openInstalledClient({ baseUrl, apiKey, name: 'honua-job-tools-owner', timeoutMs: 60_000 });
  let other;
  try {
    other = await openInstalledClient({ baseUrl, apiKey: otherApiKey, name: 'honua-job-tools-other', timeoutMs: 60_000 });
    for (const client of [owner.client, other.client]) {
      const tools = await discoverInstalledTools(client);
      for (const name of ['honua_execute_plan', 'honua_list_jobs', 'honua_get_job_results']) {
        if (!tools.some(tool => tool.name === name)) throw new JobJourneyFailure('missing-job-tool');
      }
    }
    const nonce = randomUUID();
    const submitted = await call(owner.client, 'honua_execute_plan', {
      plan: { planId: `job-tools-${nonce}`, steps: [{ stepId: 'buffer', kind: 'Geoprocess', processId: 'geometry.buffer',
        inputs: { wkb: 'AQEAAAAAAAAAAAAAAAAAAAAAAAAA', srid: String(srid), distance: String(distance) } }], outputs: ['FeatureLayer'] },
      idempotencyKey: `job-tools-${nonce}`,
    });
    const jobId = submitted.jobId;
    if (typeof jobId !== 'string' || !/^[A-Za-z0-9_-]{1,128}$/.test(jobId)) throw new JobJourneyFailure('missing-submitted-job');
    let status;
    let polls = 0;
    for (; polls < 100; polls++) {
      const job = (await listJobs(owner.client)).find(job => job.jobId === jobId);
      status = job?.status;
      if (status === 'Succeeded') break;
      if (!['Queued', 'Provisioning', 'Running'].includes(status)) throw new JobJourneyFailure('job-did-not-succeed');
      await setTimeout(250);
    }
    if (status !== 'Succeeded') throw new JobJourneyFailure('job-poll-budget-exhausted');
    const results = await call(owner.client, 'honua_get_job_results', { jobId });
    if (results.jobId !== jobId || results.status !== 'Completed' || !results.resultPackageId
      || results.artifactsTruncated !== false || results.totalArtifactCount !== 1 || results.artifacts?.length !== 1
      || !Array.isArray(results.errors) || results.errors.length) throw new JobJourneyFailure('invalid-job-results');
    const artifact = results.artifacts[0];
    if (!artifact.artifactId || artifact.kind !== 'FeatureLayer' || artifact.contentType !== 'application/geo+json') throw new JobJourneyFailure('invalid-buffer-artifact');
    const geometry = verifyGeometry(await artifactBytes(artifact, owner, apiKey, jobId));
    if ((await listJobs(other.client)).some(job => job.jobId === jobId)) throw new JobJourneyFailure('cross-user-job-listed');
    const denied = await deniedResults(other.client, jobId);
    const missing = await deniedResults(other.client, missingJobId);
    if (denied.code !== 'not_found' || missing.code !== denied.code) throw new JobJourneyFailure('cross-user-nondisclosure-failed');
    return { schema: 'honua.mcp-job-tools-journey/v1', ...owner.metadata,
      adapterSha256: await sourceSha256(import.meta.url),
      jobId, status, polls: polls + 1, resultStatus: results.status, geometry,
      crossUserRead: denied.code, crossUserListed: false };
  } finally {
    const closed = await Promise.allSettled([owner.client.close(), other?.client.close()]);
    if (closed.some(result => result.status === 'rejected')) throw new JobJourneyFailure('client-cleanup-failed');
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = await jobResultsJourney({ baseUrl: process.argv[2], apiKey: process.env.HONUA_MCP_JOB_OWNER_KEY,
      otherApiKey: process.env.HONUA_MCP_JOB_OTHER_KEY });
    process.stdout.write(JSON.stringify(result) + '\n');
  } catch (error) {
    const reason = error instanceof JobJourneyFailure ? error.message : 'client-or-host-error';
    process.stderr.write(`Installed MCP job tools journey failed: ${reason}.\n`);
    process.exitCode = 1;
  }
}
