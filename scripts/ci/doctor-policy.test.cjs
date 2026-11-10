const { test } = require('node:test');
const assert = require('node:assert/strict');
const { handled, ciEligibility, reviewEligibility } = require('./doctor-eligibility.cjs');
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
async function checkBudget({ triage = [], fix = [], credits = {}, threshold = 1300, summaries = [] }) {
  const outputs = {};
  const summary = { addHeading() { return this; }, addRaw(text) { summaries.push(text); return this; }, async write() {} };
  await budgetCheck({ github: { rest: { actions: { listWorkflowRuns: 'runs' } }, paginate: async (_, args) => args.workflow_id.includes('triage') ? triage : fix }, context: { repo, runId: 99 }, core: { setOutput: (k, v) => { outputs[k] = v; }, info() {}, summary }, accounting: { getArtifactClient: async () => ({}), getRunAIC: async (_, id) => credits[id] }, name: 'ci-doctor', phase: 'triage', threshold, maxCredits: 15, apiBudget: {} });
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
