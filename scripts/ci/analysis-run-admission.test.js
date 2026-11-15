const { test } = require('node:test');
const assert = require('node:assert/strict');
const { shouldCancel, prNumber } = require('./analysis-run-admission');

const base = { id: 10, path: '.github/workflows/codeql.yml', head_sha: 'current',
  status: 'in_progress', pull_requests: [{ number: 12 }] };
const pull = { state: 'open', draft: false, head: { sha: 'current' } };

test('retains the only analysis and retries after failure or a draft skip', () => {
  assert.equal(shouldCancel(base, [], pull), false);
  for (const conclusion of ['failure', 'cancelled', 'success']) {
    assert.equal(shouldCancel(base, [{ ...base, id: 9, status: 'completed', conclusion }], pull), false);
  }
});

test('CodeQL needs completed evidence; managed analysis coalesces newer active duplicates', () => {
  assert.equal(shouldCancel(base, [{ ...base, id: 9, evidenceComplete: true }], pull), true);
  assert.equal(shouldCancel(base, [{ ...base, id: 11 }], pull), false);
  const managed = { ...base, path: 'dynamic/github-code-quality/codeql' };
  assert.equal(shouldCancel(managed, [{ ...managed, id: 11 }], pull), true);
  assert.equal(shouldCancel(base, [{ ...base, id: 9 }], pull), false);
});

test('never borrows evidence from a different PR or commit', () => {
  assert.equal(shouldCancel(base, [{ ...base, id: 11, head_sha: 'other' }], pull), false);
  assert.equal(shouldCancel(base, [{ ...base, id: 11, pull_requests: [{ number: 13 }] }], pull), false);
});

test('sheds old commits and closed PRs while leaving completed checks untouched', () => {
  assert.equal(shouldCancel(base, [], { ...pull, head: { sha: 'new' } }), true);
  assert.equal(shouldCancel(base, [], { ...pull, state: 'closed' }), true);
  assert.equal(shouldCancel({ ...base, status: 'completed' }, [], { ...pull, state: 'closed' }), false);
});

test('recognizes managed PR refs and ignores unrelated workflows', () => {
  assert.equal(prNumber({ head_branch: 'refs/pull/123/head' }), 123);
  assert.equal(prNumber({ head_branch: 'trunk' }), undefined);
  assert.equal(shouldCancel({ ...base, path: '.github/workflows/pr-gate.yml' }, [], { ...pull, state: 'closed' }), false);
});

const { completedEvidence, reconcile } = require('./analysis-run-admission');
const quiet = { notice() {} };

function harness(runs, freshRuns = runs, jobs = []) {
  const cancelled = [];
  const client = {
    run: async id => ({ ...freshRuns.find(run => run.id === id) }),
    pull: async () => ({ ...pull, number: 12 }),
    runs: async options => runs.filter(run => options.status === 'success'
      ? run.status === 'completed' && run.conclusion === 'success' : run.status === options.status),
    jobs: async () => jobs,
    cancel: async id => cancelled.push(id),
  };
  return { client, cancelled };
}

test('managed draft analysis remains covered without a ready-for-review trigger', () => {
  assert.equal(shouldCancel({ ...base, path: 'dynamic/github-code-quality/codeql' }, [], { ...pull, draft: true }), false);
  assert.equal(shouldCancel(base, [], { ...pull, draft: true }), true);
});

test('a failed or cancelled replacement discovered after listing cannot cancel the remaining run', async () => {
  for (const conclusion of ['failure', 'cancelled']) {
    const peer = { ...base, id: 11 };
    const { client, cancelled } = harness([base, peer], [base, { ...peer, status: 'completed', conclusion }]);
    await reconcile(client, base, quiet);
    assert.deepEqual(cancelled, []);
  }
});

test('the current replacement cancels an older duplicate without cancelling itself', async () => {
  const managed = { ...base, path: 'dynamic/github-code-quality/codeql' };
  const { client, cancelled } = harness([managed, { ...managed, id: 11 }]);
  await reconcile(client, managed, quiet);
  assert.deepEqual(cancelled, [10]);
});

test('completed evidence requires all languages and a stable attempt', async () => {
  const finished = { ...base, status: 'completed', conclusion: 'success', run_attempt: 1 };
  const languages = ['csharp', 'javascript-typescript', 'python'];
  const jobs = languages.map(language => ({ name: `Analyze (${language})`, conclusion: 'success' }));
  const { client } = harness([finished], [finished], jobs);
  assert.equal(await completedEvidence(client, finished), true);
  jobs[2].conclusion = 'skipped';
  assert.equal(await completedEvidence(client, finished), false);
  jobs[2].conclusion = 'success';
  client.run = async () => ({ ...finished, status: 'in_progress', conclusion: null, run_attempt: 2 });
  assert.equal(await completedEvidence(client, finished), false);
});

test('a successful run that was rerun during job discovery cannot suppress fresh analysis', async () => {
  const completed = { ...base, id: 9, status: 'completed', conclusion: 'success', run_attempt: 1 };
  const { client, cancelled } = harness([completed, base], [completed, base], [{ name: 'Analyze (csharp)', conclusion: 'success' }]);
  const getRun = client.run;
  let reads = 0;
  client.run = async id => id === 9 && ++reads > 1
    ? { ...completed, status: 'in_progress', conclusion: null, run_attempt: 2 } : getRun(id);
  await reconcile(client, base, quiet);
  assert.deepEqual(cancelled, []);
});

test('trunk and scheduled runs do not query or cancel PR analysis', async () => {
  const trunk = { ...base, pull_requests: [], head_branch: 'trunk' };
  const { client, cancelled } = harness([trunk]);
  client.runs = async () => { throw new Error('Unexpected PR lookup'); };
  await reconcile(client, trunk, quiet);
  assert.deepEqual(cancelled, []);
});

// A retry keeps its old ID while native concurrency asynchronously cancels B.
test('CodeQL retry survives an active newer-ID run already being cancelled natively', async () => {
  const retry = { ...base, id: 10, run_attempt: 2, status: 'queued' };
  const cancelling = { ...base, id: 11, run_attempt: 1 };
  const { client, cancelled } = harness([retry, cancelling]);
  await reconcile(client, retry, quiet);
  assert.deepEqual(cancelled, []);
});
