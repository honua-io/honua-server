'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const {admitNormalization} = require('./admit-normalization-consumer');
const {cancelOlderConsumers} = require('./cancel-older-normalization-consumers');
const path = '.github/workflows/normalize-derived-artifacts-consumer.yml';

function fixture() {
  const cancelled = [];
  const producer = {id: 123, run_attempt: 2, name: 'Derived Artifact Normalization',
    path: '.github/workflows/normalize-derived-artifacts.yml', event: 'pull_request',
    conclusion: 'success', head_sha: 'old', repository: {full_name: 'honua-io/honua-server'}, pull_requests: [{number: 42}]};
  const candidates = [];
  const state = {cancelled, producer, candidates,
    context: {repo: {owner: 'honua-io', repo: 'honua-server'}, payload: {workflow_run: producer}},
    core: {info() {}}, github: {paginate: async () => candidates, rest: {actions: {
      listWorkflowRuns() {},
      getWorkflowRun: async () => ({data: producer}),
      cancelWorkflowRun: async ({run_id}) => {cancelled.push(run_id);},
    }, pulls: {get: async () => ({data: {number: 42, state: 'open', draft: false, head: {sha: 'old'}, base: {repo: producer.repository}}})}}}};
  return state;
}
function candidate(id, generation, attempt = 1) {
  return {id, path, status: 'in_progress', display_title: `Normalization producer ${generation} attempt ${attempt}`};
}

test('cancel only strictly older generations for the same PR, including prior attempts', async () => {
  const state = fixture();
  state.candidates.push(candidate(1, 122), candidate(2, 123, 1), candidate(3, 123, 2), candidate(4, 124));
  await cancelOlderConsumers(state, 42);
  assert.deepEqual(state.cancelled, [1, 2]);
});

test('other PRs, unrelated workflows, completed runs and unparseable titles cannot be cancelled', async () => {
  const state = fixture();
  state.producer.pull_requests = [{number: 43}];
  state.candidates.push(candidate(1, 122), {...candidate(2, 122), path: 'other.yml'},
    {...candidate(3, 122), status: 'completed'}, {...candidate(4, 122), display_title: 'unknown'});
  await cancelOlderConsumers(state, 42);
  assert.deepEqual(state.cancelled, []);
  state.producer.pull_requests = [];
  await cancelOlderConsumers(state, 42);
  assert.deepEqual(state.cancelled, []);
});

test('delayed packaging API completion cannot cancel a newer admitted consumer', async () => {
  const old = fixture();
  let finishPackaging;
  old.github.rest.actions.listJobsForWorkflowRunAttempt = () => new Promise(resolve => {finishPackaging = resolve;});
  const admission = admitNormalization(old);
  await new Promise(resolve => setImmediate(resolve));
  const newer = fixture();
  newer.producer.id = 124;
  newer.candidates.push(candidate(8, 123));
  await cancelOlderConsumers(newer, 42);
  assert.deepEqual(newer.cancelled, [8]);
  old.candidates.push(candidate(9, 124));
  finishPackaging({data: {total_count: 1, jobs: [{name: 'Produce data-only normalization envelope', conclusion: 'success'}]}});
  assert.equal(await admission, 42);
  await cancelOlderConsumers(old, 42);
  assert.deepEqual(old.cancelled, []);
  const workflow = fs.readFileSync(path, 'utf8');
  assert.ok(workflow.includes('group: normalization-consumer-${{ needs.admit.outputs.pr }}-${{ github.event.workflow_run.id }}-${{ github.event.workflow_run.run_attempt }}'));
});

test('already-completed conflicts are harmless but API failures surface', async () => {
  const state = fixture();
  state.candidates.push(candidate(1, 122));
  state.github.rest.actions.cancelWorkflowRun = async () => {throw Object.assign(new Error('done'), {status: 409});};
  await cancelOlderConsumers(state, 42);
  state.github.rest.actions.cancelWorkflowRun = async () => {throw new Error('API unavailable');};
  await assert.rejects(cancelOlderConsumers(state, 42), /unavailable/);
});
