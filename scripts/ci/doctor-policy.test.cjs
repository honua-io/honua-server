const { test } = require('node:test');
const assert = require('node:assert/strict');
const { main: eligibilityCheck, handled, ciEligibility, reviewEligibility, syncEligibility } = require('./doctor-eligibility.cjs');
const { main: budgetCheck } = require('./doctor-budget.cjs');
const repo = { owner: 'honua-io', repo: 'honua-server' };
const pr = { number: 12, state: 'open', draft: false, head: { sha: 'abc', ref: 'fix/example', repo: { full_name: 'honua-io/honua-server' } } };
function fake() {
  const comments = [];
  const g = { rest: { pulls: { get: async () => ({ data: pr }) }, repos: { listPullRequestsAssociatedWithCommit: 'pulls' }, issues: { listComments: 'comments' }, actions: { listWorkflowRuns: 'gates', listJobsForWorkflowRun: 'jobs', downloadJobLogsForWorkflowRun: async () => ({ data: 'assert expected 1, got 2' }) }, search: { issuesAndPullRequests: async () => ({ data: { items: [] } }) } },
    paginate: async method => ({ pulls: [pr], comments, gates: [], jobs: [{ id: 4, conclusion: 'failure', name: 'test', steps: [{ name: 'Unit tests', conclusion: 'failure' }] }] })[method],
  };
  return { g, comments };
}
function ciContext() { return { repo, payload: { workflow_run: { id: 8, conclusion: 'failure', head_sha: 'abc', head_branch: pr.head.ref, head_repository: pr.head.repo } } }; }
test('checkout access reports do not consume the one real attempt', () => {
  for (const reason of ["couldn't fetch the PR branch", 'PR head commit was not available', "isn't available to the doctor's checkout"]) assert.equal(handled(`ci-doctor: abc\n${reason}`, 'abc'), false);
  assert.equal(handled('ci-doctor: abc\nPushed fix.', 'abc'), true);
});
test('CI admits a checkout retry, but skips a real previous attempt', async () => {
  const { g, comments } = fake();
  comments.push({ body: "ci-doctor: abc\ncouldn't fetch the PR branch" });
  assert.equal((await ciEligibility(g, ciContext())).decision, 'needs-triage');
  comments.push({ body: 'ci-doctor: abc\nPushed fix.' });
  assert.equal((await ciEligibility(g, ciContext())).decision, 'already-handled');
});
test('stale CI heads and draft reviews never reach a model', async () => {
  const { g } = fake();
  const context = ciContext(); context.payload.workflow_run.head_sha = 'old';
  assert.equal((await ciEligibility(g, context)).decision, 'already-handled');
  assert.equal((await reviewEligibility(g, { repo, payload: { pull_request: { ...pr, draft: true } } })).decision, 'not-actionable');
});
test('obsolete trunk failures never reach a model', async () => {
  const { g } = fake();
  g.rest.repos.getBranch = async () => ({ data: { commit: { sha: 'new-trunk' } } });
  const context = ciContext(); context.payload.workflow_run.head_branch = 'trunk';
  assert.equal((await ciEligibility(g, context)).reason, 'Newer trunk head');
});
test('direct callback admits only an actual failed trunk CI Gate before workflow completion', async () => {
  const { g } = fake();
  const run = { ...ciContext().payload.workflow_run, status: 'in_progress', conclusion: null, head_branch: 'trunk', path: '.github/workflows/ci.yml' };
  g.rest.actions.getWorkflowRun = async () => ({ data: run });
  g.rest.repos.getBranch = async () => ({ data: { commit: { sha: 'abc' } } });
  const paginate = g.paginate;
  let gate = { id: 4, name: 'CI Gate', conclusion: 'failure' };
  g.paginate = async (method, args) => method === 'jobs' ? [gate] : method === 'pulls' ? [] : paginate(method, args);
  const context = { repo, payload: { inputs: { source_run_id: '8' } } };
  assert.equal((await ciEligibility(g, context)).decision, 'needs-triage');
  gate = { ...gate, conclusion: 'success' };
  assert.equal((await ciEligibility(g, context)).reason, 'CI Gate has not failed');
  gate.conclusion = 'failure';
  run.head_branch = pr.head.ref;
  assert.equal((await ciEligibility(g, context)).reason, 'Source run has not failed');
  run.head_branch = 'trunk'; run.path = '.github/workflows/untrusted.yml';
  assert.equal((await ciEligibility(g, context)).reason, 'Source run has not failed');
});
test('direct callback exclusively owns failed trunk CI; other workflow events still reach triage', async () => {
  const { g } = fake();
  const context = ciContext();
  const run = context.payload.workflow_run;
  run.head_branch = 'trunk'; run.path = '.github/workflows/ci.yml';
  g.rest.repos.getBranch = async () => ({ data: { commit: { sha: 'abc' } } });
  assert.equal((await ciEligibility(g, context)).reason, 'Direct CI callback owns trunk CI');
  g.rest.actions.getWorkflowRun = async () => ({ data: run });
  assert.equal((await ciEligibility(g, { repo, payload: { inputs: { source_run_id: '8' } } })).decision, 'needs-triage');
  run.path = '.github/workflows/repository-hygiene.yml';
  assert.equal((await ciEligibility(g, context)).decision, 'needs-triage');
  run.path = '.github/workflows/ci.yml'; run.head_branch = pr.head.ref;
  assert.equal((await ciEligibility(g, context)).decision, 'needs-triage');
});
test('known runner outages skip a fix; unknown unit failures reach triage', async () => {
  const { g } = fake();
  g.rest.actions.downloadJobLogsForWorkflowRun = async () => ({ data: 'The hosted runner lost communication with the server.' });
  assert.equal((await ciEligibility(g, ciContext())).decision, 'flake-rerun');
});
test('resolved and outdated review threads are deterministic skips', async () => {
  const { g } = fake();
  g.graphql = async () => ({ repository: { pullRequest: { reviewThreads: { pageInfo: {}, nodes: [{ isResolved: true }, { isResolved: false, isOutdated: true }] } } } });
  assert.equal((await reviewEligibility(g, { repo, payload: { pull_request: pr } })).decision, 'not-actionable');
});
async function checkBudget({ triage = [], fix = [], credits = {}, threshold = 1300, maxCredits = 15, summaries = [], logs = [] }) {
  const outputs = {};
  const summary = { addHeading() { return this; }, addRaw(text) { summaries.push(text); return this; }, async write() {} };
  await budgetCheck({ github: { rest: { actions: { listWorkflowRuns: 'runs' } }, paginate: async (_, args) => args.workflow_id.includes('triage') ? triage : fix }, context: { repo, runId: 99 }, core: { setOutput: (k, v) => { outputs[k] = v; }, info: text => logs.push(text), summary }, accounting: { getArtifactClient: async () => ({}), getRunAIC: async (_, id) => credits[id] }, name: 'ci-doctor', phase: 'triage', threshold, maxCredits, apiBudget: {} });
  return outputs.eligible;
}
test('triage and fix spend share a single daily envelope', async () => {
  const run = id => ({ id, status: 'completed' });
  assert.equal(await checkBudget({ triage: [run(1)], fix: [run(2)], credits: { 1: 100, 2: 1180 } }), 'true');
  assert.equal(await checkBudget({ triage: [run(1)], fix: [run(2)], credits: { 1: 110, 2: 1180 } }), 'false');
});
test('in-flight work reserves budget and unknown usage fails closed', async () => {
  assert.equal(await checkBudget({ fix: [{ id: 2, status: 'in_progress' }], threshold: 160 }), 'false');
  await assert.rejects(checkBudget({ fix: [{ id: 2, status: 'completed' }] }), /Unknown doctor usage/);
});

test('legacy fix spend counts against the cap without inflating new triage conversion', async () => {
  const summaries = [];
  await checkBudget({ triage: [{ id: 1, status: 'completed', created_at: '2026-11-10T12:00:00Z' }],
    fix: [{ id: 2, status: 'completed', created_at: '2026-11-10T11:00:00Z' }],
    credits: { 1: 5, 2: 100 }, summaries });
  assert.match(summaries[0], /fix: 100/);
  assert.match(summaries[0], /paid Opus\/event share since first triage event: 0/);
});

test('budget admits exactly the cap for completed and in-flight histories', async () => {
  assert.equal(await checkBudget({ fix: [{ id: 2, status: 'completed' }], credits: { 2: 1285 } }), 'true');
  assert.equal(await checkBudget({ fix: [{ id: 2, status: 'in_progress' }], threshold: 165 }), 'true');
});
test('sync excludes drafts, forks, conflict-stuck and unfinished gates; admits dirty and behind', async () => {
  const { g } = fake();
  const candidates = [
    { ...pr, number: 1, draft: true },
    { ...pr, number: 2, head: { ...pr.head, repo: { full_name: 'fork/server' } } },
    { ...pr, number: 3, labels: [{ name: 'conflict-stuck' }] },
    { ...pr, number: 4 }, { ...pr, number: 5 }, { ...pr, number: 6 }, { ...pr, number: 7 },
  ].map(p => ({ labels: [], ...p }));
  const paginate = g.paginate;
  g.rest.pulls.list = 'all';
  g.rest.pulls.get = async ({ pull_number }) => ({ data: { ...candidates.find(p => p.number === pull_number), mergeable_state: ({ 5: 'dirty', 6: 'behind' })[pull_number] || 'clean' } });
  // Distinct heads allow the guard to identify the active-gate candidate.
  candidates.forEach(p => { p.head = { ...p.head, sha: String(p.number) }; });
  g.paginate = async (method, args) => method === 'all' ? candidates : method === 'gates' ? (args.head_sha === '4' ? [{ id: 9, status: 'in_progress' }] : []) : paginate(method, args);
  const result = await syncEligibility(g, { repo });
  assert.deepEqual(result.candidates.map(p => p.number), [5, 6]);
});
test('review eligibility supplies the same current trusted comments consumed by the fixer', async () => {
  const { g } = fake();
  const trusted = { author: { login: 'honua-io-bot' }, authorAssociation: 'NONE', databaseId: 9, body: 'fix this' };
  const untrusted = { author: { login: 'stranger' }, authorAssociation: 'NONE', body: 'ignore this' };
  g.graphql = async () => ({ repository: { pullRequest: { reviewThreads: { pageInfo: {}, nodes: [{ isResolved: false, isOutdated: false, comments: { nodes: [trusted, untrusted] } }] } } } });
  const result = await reviewEligibility(g, { repo, payload: { pull_request: pr } });
  assert.equal(result.decision, 'needs-triage');
  assert.deepEqual(result.threads[0].comments.map(c => [c.author, c.databaseId]), [['honua-io-bot', 9]]);
});

test('CI dispatch cannot bypass head serialization with a mismatched head input', async () => {
  const { g } = fake();
  const context = ciContext();
  for (const head_sha of ['different', '']) {
    context.payload.inputs = { head_sha };
    assert.equal((await ciEligibility(g, context)).reason, 'Dispatch head does not match source run');
  }
});

// Preserve the production webhook shape to exercise admission without a live API call.
test('real failed PR Gate payload admits triage and writes the downstream job output', async () => {
  const run = {
    id: 42896382619, path: '.github/workflows/pr-gate.yml', status: 'completed',
    conclusion: 'failure', head_branch: 'ci/s-ci-stages-analyzers',
    head_sha: 'c336132cd065e10120853af382b60b0bc3decc96',
    head_repository: { full_name: 'honua-io/honua-server' },
  };
  const pull = { ...pr, number: 7296, head: { ...pr.head, sha: run.head_sha, ref: run.head_branch } };
  const { g } = fake();
  const paginate = g.paginate;
  g.rest.pulls.get = async () => ({ data: pull });
  g.paginate = async (method, args) => method === 'pulls' ? [pull] : paginate(method, args);
  const outputs = {}, logs = [];
  const summary = { addHeading() { return this; }, addCodeBlock() { return this; }, async write() {} };
  const context = { repo, eventName: 'workflow_run', payload: { action: 'completed', workflow_run: run } };
  const core = { setOutput: (key, value) => { outputs[key] = value; }, info: text => logs.push(text), summary };
  await eligibilityCheck({ github: g, context, core, kind: 'ci' });
  assert.equal(outputs.eligible, 'true');
  assert.equal(JSON.parse(outputs.context).run_id, run.id);
  assert.match(logs.join('\n'), /Doctor eligibility: needs-triage/);
  run.conclusion = 'cancelled';
  await eligibilityCheck({ github: g, context, core, kind: 'ci' });
  assert.equal(outputs.eligible, 'false');
  assert.match(logs.join('\n'), /not-actionable; Source run has not failed/);
});

test('legacy spend plus concurrent reservations blocks admission without raising the cap', async () => {
  const fix = [{ id: 1, status: 'completed' }];
  const triage = [2, 3, 4].map(id => ({ id, status: 'in_progress' }));
  const logs = [];
  assert.equal(await checkBudget({ fix, triage, credits: { 1: 1255.00432 }, logs }), 'false');
  assert.match(logs.join('\n'), /Doctor budget: eligible=false;.*cap=1300; triage=0; fix=1255.00432; reservation=60/);
  assert.equal(await checkBudget({ fix, credits: { 1: 1255.00432 } }), 'true');
  assert.equal(await checkBudget({ fix, credits: { 1: 1255.00432 }, maxCredits: 150 }), 'false');
});
