// Scripted-peer controls exercise the published SDK; they do not qualify Honua.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { jobResultsJourney } from './job-results-journey.mjs';

const names = ['honua_execute_plan', 'honua_list_jobs', 'honua_get_job_results'];
const polygon = Array.from({ length: 33 }, (_, i) => {
  const angle = (i % 32) * Math.PI / 16;
  return [25.5 * Math.cos(angle), 25.5 * Math.sin(angle)];
});

function bufferFeature(mode) {
  const geometry = mode === 'wrong-geometry' ? polygon.map((point, i) => i === 1 ? [...point, 99] : point) : polygon;
  return { type: 'Feature', properties: { processId: 'geometry.buffer', inputSrid: 3857, bufferDistance: mode === 'wrong-distance' ? 1 : 25.5 },
    geometry: { type: 'Polygon', coordinates: [geometry] } };
}

function toolResult({ mode, params, foreign, state }) {
  const { name, arguments: args } = params;
  let payload;
  let isError = false;
  if (name === 'honua_execute_plan') payload = { jobId: 'job-1', status: mode === 'submission-completed' ? 'Succeeded' : 'Queued' };
  else if (name === 'honua_list_jobs') {
    const jobs = foreign ? mode === 'foreign-list-leak' ? [{ jobId: 'job-1', status: 'Succeeded' }] : []
      : [{ jobId: 'job-1', status: mode === 'failed' ? 'Failed' : ++state.polls === 1 ? 'Running' : 'Succeeded' }];
    payload = { jobs, jobCount: jobs.length };
  } else if (foreign || args.jobId === 'missing-job-control') {
    isError = mode !== 'foreign-read-leak';
    payload = isError ? { code: mode === 'foreign-forbidden' && foreign ? 'permission_denied' : 'not_found', status: 'error' }
      : { jobId: 'job-1', status: 'Completed', artifacts: [] };
  } else {
    const uri = mode.startsWith('download') ? '/api/geoprocessing/jobs/job-1/artifacts/0/content'
      : mode === 'external-artifact' ? 'https://external.invalid/content'
      : `data:application/geo+json;base64,${Buffer.from(JSON.stringify(bufferFeature(mode))).toString('base64')}`;
    payload = { jobId: mode === 'wrong-job' ? 'job-2' : 'job-1', resultPackageId: 'package-1', status: mode === 'incomplete-result' ? 'Executing' : 'Completed',
      artifacts: [{ artifactId: 'artifact-1', kind: 'FeatureLayer', label: 'outputFeatureLayer', contentType: 'application/geo+json', uri }],
      totalArtifactCount: 1, artifactsTruncated: mode === 'truncated-result', errors: [] };
  }
  const result = { isError, structuredContent: payload, content: [{ type: 'text', text: 'Human-readable summary.' },
    { type: 'text', text: JSON.stringify(mode === 'contradictory-text' && name === 'honua_get_job_results' && !foreign ? { ...payload, jobId: 'wrong' } : payload) }] };
  if (mode === 'missing-text') result.content = [];
  if (mode === 'missing-structured') delete result.structuredContent;
  return result;
}

async function run(mode = 'positive') {
  const calls = [];
  const state = { polls: 0 };
  const server = createServer(async (request, response) => {
    if (request.method === 'GET' && request.url === '/api/geoprocessing/jobs/job-1/artifacts/0/content') {
      if (mode === 'download-redirect') { response.writeHead(302, { Location: 'https://external.invalid' }).end(); return; }
      if (mode === 'download-unavailable') { response.writeHead(404).end(); return; }
      assert.equal(request.headers['x-api-key'], 'owner-key');
      response.writeHead(200, { 'Content-Type': 'application/geo+json' });
      response.end(mode === 'download-oversize' ? 'x'.repeat(512 * 1024 + 1) : JSON.stringify(bufferFeature(mode)));
      return;
    }
    if (request.method !== 'POST') { response.writeHead(200).end(); return; }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    const foreign = request.headers['x-api-key'] === 'other-key';
    calls.push({ body, foreign, path: request.url });
    if (!('id' in body)) { response.writeHead(202).end(); return; }
    let result;
    if (body.method === 'initialize') {
      result = { protocolVersion: '2025-06-18', capabilities: { tools: {} },
        serverInfo: { name: 'scripted-job-journey-control', version: '1' } };
    } else if (body.method === 'tools/list') {
      result = { tools: names.filter(name => mode !== 'missing-tool' || name !== 'honua_get_job_results').map(name => ({
        name, inputSchema: { type: 'object', properties: name === 'honua_get_job_results' ? { jobId: { type: 'string' } } : {} },
      })), ...(mode === 'cursor-loop' ? { nextCursor: 'repeat' } : {}) };
    } else if (body.method === 'tools/call') {
      result = toolResult({ mode, params: body.params, foreign, state });
    } else {
      response.writeHead(400).end(); return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json', 'Mcp-Session-Id': foreign ? 'other-session' : 'owner-session' });
    response.end(JSON.stringify({ jsonrpc: '2.0', id: body.id, result }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    try {
      return { result: await jobResultsJourney({ baseUrl: `http://127.0.0.1:${server.address().port}`,
        apiKey: 'owner-key', otherApiKey: 'other-key', missingJobId: 'missing-job-control' }), calls };
    } catch (error) { return { error, calls }; }
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}

test('published SDK submits, polls and checks result geometry through tools and denies another user', async () => {
  const { result, error, calls } = await run();
  assert.equal(error, undefined, error?.message);
  assert.equal(result.schema, 'honua.mcp-job-tools-journey/v1');
  assert.equal(result.sdkVersion, '1.32.1');
  assert.equal(result.jobId, 'job-1');
  assert.equal(result.status, 'Succeeded');
  assert.equal(result.resultStatus, 'Completed');
  assert.ok(result.geometry.area > Math.PI * 25.5 ** 2 * 0.9);
  assert.equal(result.crossUserRead, 'not_found');
  assert.equal(result.crossUserListed, false);
  assert.match(result.geometry.sha256, /^[a-f0-9]{64}$/);
  assert.ok(calls.every(({ body }) => !body.method.startsWith('resources/')));
  assert.ok(calls.filter(({ body }) => body.method === 'tools/list').every(({ path }) => path === '/mcp'));
  assert.ok(calls.some(({ body }) => body.method === 'tools/call' && body.params.name === 'honua_execute_plan'));
  assert.ok(calls.filter(({ body, foreign }) => !foreign && body.params?.name === 'honua_list_jobs').length >= 2);
});

test('a terminal submission response still requires polling and verified result content', async () => {
  const { result, error, calls } = await run('submission-completed');
  assert.equal(error, undefined, error?.message);
  assert.equal(result.resultStatus, 'Completed');
  assert.ok(calls.some(({ body }) => body.params?.name === 'honua_get_job_results'));
});

test('published SDK results can refer to an authorized same-origin staged artifact', async () => {
  const { result, error } = await run('download');
  assert.equal(error, undefined, error?.message);
  assert.ok(result.geometry.bytes > 0);
});

for (const [mode, reason] of [
  ['missing-tool', 'missing-job-tool'], ['cursor-loop', 'incomplete-discovery'], ['failed', 'job-did-not-succeed'],
  ['foreign-list-leak', 'cross-user-job-listed'], ['foreign-read-leak', 'unexpected-tool-status'],
  ['foreign-forbidden', 'cross-user-nondisclosure-failed'], ['wrong-geometry', 'invalid-buffer-coordinate'],
  ['wrong-distance', 'invalid-buffer-result'], ['wrong-job', 'invalid-job-results'],
  ['incomplete-result', 'invalid-job-results'], ['truncated-result', 'invalid-job-results'],
  ['contradictory-text', 'content-channel-mismatch'], ['missing-text', 'content-channel-mismatch'],
  ['missing-structured', 'content-channel-mismatch'],
  ['external-artifact', 'invalid-artifact-reference'], ['download-redirect', 'unavailable-artifact'],
  ['download-unavailable', 'unavailable-artifact'], ['download-oversize', 'oversized-artifact'],
]) {
  test(`published SDK job journey rejects ${mode}`, async () => {
    const { error, calls } = await run(mode);
    assert.ok(calls.some(({ body }) => body.method === 'tools/list'), 'control must reach SDK discovery');
    assert.equal(error?.message, reason, 'invalid result must fail at its intended boundary');
  });
}

test('job journey rejects equal caller credentials before connecting', async () => {
  await assert.rejects(jobResultsJourney({ baseUrl: 'http://127.0.0.1:1', apiKey: 'same', otherApiKey: 'same' }), /distinct-callers/);
});
