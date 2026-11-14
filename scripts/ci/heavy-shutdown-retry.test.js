'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { eligibleRun, retryShutdown } = require('./heavy-shutdown-retry');

function fixture() {
  const run = { id: 42, head_sha: 'abc', head_repository: { full_name: 'honua-io/honua-server' },
    path: '.github/workflows/ci.yml', head_branch: 'trunk', status: 'completed',
    conclusion: 'failure', run_attempt: 1, event: 'schedule', display_title: 'CI full ' };
  const jobs = [{ id: 10, name: 'Server Tests (SensorThings)', labels: ['honua-heavy'],
    conclusion: 'failure', status: 'completed' },
  { id: 11, name: 'CI Gate', conclusion: 'failure', status: 'completed',
    steps: [{ name: 'Check required job results', conclusion: 'failure' }] },
  { id: 12, name: 'Build', conclusion: 'success', status: 'completed' }];
  const calls = [];
  const logs = new Map([[10, '2026-11-14T02:15:21.4030389Z ##[error]The runner has received a shutdown signal. This can happen when the runner service is stopped, or a manually started runner is canceled.']]);
  const actions = {
    getWorkflowRun: async () => ({ data: run }),
    listJobsForWorkflowRunAttempt: Symbol('paginated jobs'),
    downloadJobLogsForWorkflowRun: async ({ job_id }) => ({ data: logs.get(job_id) || '' }),
    reRunWorkflowFailedJobs: async args => calls.push(args),
  };
  const github = { rest: { actions }, paginate: async (method, args) => {
    assert.equal(method, actions.listJobsForWorkflowRunAttempt);
    assert.equal(args.attempt_number, 1);
    assert.equal(args.per_page, 100);
    return jobs;
  } };
  const context = { repo: { owner: 'honua-io', repo: 'honua-server' },
    payload: { workflow_run: structuredClone(run) } };
  return { run, jobs, logs, calls, github, context, core: { info() {} } };
}

for (const [name, patch] of Object.entries({
  fork: { head_repository: { full_name: 'attacker/server' } },
  branch: { head_branch: 'feature' }, secondAttempt: { run_attempt: 2 },
  selective: { event: 'workflow_dispatch', display_title: 'CI affected abc..def' },
  manualAmbiguous: { event: 'workflow_dispatch', display_title: 'CI ' },
  cancellation: { conclusion: 'cancelled' }, workflow: { path: '.github/workflows/other.yml' },
  pullRequest: { event: 'pull_request' }, active: { status: 'in_progress' },
})) {
  test(`rejects ${name}`, async () => {
    const f = fixture();
    Object.assign(f.context.payload.workflow_run, patch);
    await retryShutdown(f);
    assert.deepEqual(f.calls, []);
  });
}

test('retries simultaneous shutdowns once with dependent aggregates and no successful jobs', async () => {
  const f = fixture();
  for (let id = 20; id < 26; id++) {
    f.jobs.push({ ...f.jobs[0], id });
    f.logs.set(id, f.logs.get(10));
  }
  await retryShutdown(f);
  assert.deepEqual(f.calls, [{ owner: 'honua-io', repo: 'honua-server', run_id: 42 }]);
  f.context.payload.workflow_run.run_attempt = 2;
  await retryShutdown(f);
  assert.equal(f.calls.length, 1);
});

test('explicit full manual dispatch qualifies', () => {
  const f = fixture();
  assert.equal(eligibleRun({ ...f.run, event: 'workflow_dispatch', display_title: 'CI full nonce' },
    'honua-io/honua-server'), true);
});

for (const [name, patch] of Object.entries({
  productFailure: { labels: ['honua-heavy'] }, hostedFailure: { labels: ['ubuntu-latest'] },
  cancelledJob: { conclusion: 'cancelled' }, incompleteJob: { status: 'in_progress' },
})) {
  test(`does not retry mixed ${name}`, async () => {
    const f = fixture();
    f.jobs.push({ ...f.jobs[0], id: 99, ...patch });
    await retryShutdown(f);
    assert.deepEqual(f.calls, []);
  });
}

test('does not retry summary-only failures or a changed source head', async () => {
  const f = fixture();
  f.jobs.shift();
  await retryShutdown(f);
  assert.deepEqual(f.calls, []);
  f.run.head_sha = 'new';
  await retryShutdown(f);
  assert.deepEqual(f.calls, []);
});

test('rechecks attempt immediately before mutation', async () => {
  const f = fixture();
  let reads = 0;
  f.github.rest.actions.getWorkflowRun = async () => ({ data: { ...f.run, run_attempt: ++reads } });
  await retryShutdown(f);
  assert.equal(reads, 2);
  assert.deepEqual(f.calls, []);
});

test('unreadable or missing logs never request a retry', async () => {
  const f = fixture();
  f.github.rest.actions.downloadJobLogsForWorkflowRun = async () => { throw new Error('403'); };
  await assert.rejects(retryShutdown(f), /403/);
  assert.deepEqual(f.calls, []);
});

test('observer executes trusted trunk code with bounded permissions and no PR trigger', () => {
  const workflow = fs.readFileSync('.github/workflows/heavy-shutdown-retry.yml', 'utf8');
  assert.match(workflow, /ref: refs\/heads\/trunk/);
  assert.match(workflow, /persist-credentials: false/);
  assert.doesNotMatch(workflow, /pull_request:|contents: write|secrets\./);
});

for (const name of ['.NET Foundation Tests', 'Test Suite Summary', 'CI Gate']) {
  test(`rejects independent failure in ${name}`, async () => {
    const f = fixture();
    f.jobs.push({ id: 99, name, conclusion: 'failure', status: 'completed',
      steps: [{ name: 'Download artifacts', conclusion: 'failure' }] });
    await retryShutdown(f);
    assert.deepEqual(f.calls, []);
  });
}

test('accepts only known dependent summary verdict steps', async () => {
  const f = fixture();
  f.jobs.push({ id: 99, name: 'Test Suite Summary', conclusion: 'failure', status: 'completed',
    steps: [{ name: 'Verify server shard execution receipts', conclusion: 'failure' }] });
  await retryShutdown(f);
  assert.equal(f.calls.length, 1);
});

for (const log of [
  'A test quotes: The runner has received a shutdown signal.',
  '2026-11-14T02:15:21.4030389Z ordinary output: The runner has received a shutdown signal.',
  '2026-11-14T02:15:21.4030389Z ##[error]The runner has received a shutdown signal. Extra text',
]) {
  test(`does not retry ordinary or incomplete shutdown text: ${log}`, async () => {
    const f = fixture();
    f.logs.set(10, log);
    await retryShutdown(f);
    assert.deepEqual(f.calls, []);
  });
}

test('selective dispatch nonce cannot imitate the full-mode prefix', () => {
  const f = fixture();
  assert.equal(eligibleRun({ ...f.run, event: 'workflow_dispatch',
    display_title: 'CI selective full nonce' }, 'honua-io/honua-server'), false);
  const workflow = fs.readFileSync('.github/workflows/ci.yml', 'utf8');
  assert.match(workflow, /'full ' \|\| github.ref == 'refs\/heads\/trunk' && 'selective '/);
});
