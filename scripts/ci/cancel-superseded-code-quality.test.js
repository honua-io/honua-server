'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {QUALITY_PATH, subject, superseded, cancelSuperseded} = require('./cancel-superseded-code-quality');

const branch = 'refs/pull/42/head';
const candidate = {id: 1, path: QUALITY_PATH, head_branch: branch, head_sha: 'old', status: 'queued'};

function fixture(runs, heads = ['current']) {
  const cancelled = [];
  const context = {repo: {owner: 'honua-io', repo: 'honua-server'},
    payload: {pull_request: {number: 42, head: {sha: 'obsolete-event'}}}};
  let reads = 0;
  const github = {paginate: async () => runs, rest: {
    pulls: {get: async () => ({data: {base: {repo: {full_name: 'honua-io/honua-server'}},
      head: {sha: heads[Math.min(reads++, heads.length - 1)]}}})},
    actions: {listWorkflowRunsForRepo() {}, cancelWorkflowRun: async ({run_id: id}) => cancelled.push(id)},
  }};
  return {github, context, core: {info() {}}, cancelled};
}

test('resolves only managed Code Quality pull-request events', () => {
  assert.equal(subject({workflow_run: candidate}), 42);
  for (const run of [{...candidate, path: 'ci.yml'}, {...candidate, head_branch: 'trunk'},
    {...candidate, head_branch: 'refs/pull/0/head'}]) {
    assert.equal(subject({workflow_run: run}), null);
  }
});

test('protects current head, other PRs, trunk, completed runs and required workflows', async () => {
  const runs = [candidate, {...candidate, id: 2, head_sha: 'current'},
    {...candidate, id: 3, head_branch: 'refs/pull/43/head'},
    {...candidate, id: 4, head_branch: 'trunk'}, {...candidate, id: 5, status: 'completed'},
    {...candidate, id: 6, path: '.github/workflows/pr-gate.yml'}];
  const state = fixture(runs);
  await cancelSuperseded(state);
  assert.deepEqual(state.cancelled, [1]);
});

test('rechecks live head immediately before cancellation', async () => {
  const state = fixture([candidate], ['current', 'old']);
  await cancelSuperseded(state);
  assert.deepEqual(state.cancelled, []);
});

test('late workflow_run event still cancels the obsolete event head', async () => {
  const state = fixture([candidate]);
  state.context.payload = {workflow_run: candidate};
  await cancelSuperseded(state);
  assert.deepEqual(state.cancelled, [1]);
});

test('ignores unrelated events and rejects cross-repository subjects', async () => {
  const state = fixture([candidate]);
  state.context.payload = {workflow_run: {...candidate, path: 'ci.yml'}};
  state.github.paginate = async () => {throw new Error('must not list');};
  await cancelSuperseded(state);
  state.context.payload = {pull_request: {number: 42}};
  state.github.rest.pulls.get = async () => ({data: {base: {repo: {full_name: 'other/repo'}}}});
  await assert.rejects(cancelSuperseded(state), /different repository/);
});

test('tolerates completion races but surfaces permission failures', async () => {
  const state = fixture([candidate]);
  state.github.rest.actions.cancelWorkflowRun = async () => {throw Object.assign(new Error('finished'), {status: 409});};
  await cancelSuperseded(state);
  state.github.rest.actions.cancelWorkflowRun = async () => {throw Object.assign(new Error('denied'), {status: 403});};
  await assert.rejects(cancelSuperseded(state), /denied/);
});

test('all active states are superseded but missing heads are protected', () => {
  for (const status of ['queued', 'in_progress', 'waiting', 'pending', 'requested']) {
    assert.equal(superseded({...candidate, status}, branch, 'current'), true);
  }
  assert.equal(superseded({...candidate, head_sha: ''}, branch, 'current'), false);
});
