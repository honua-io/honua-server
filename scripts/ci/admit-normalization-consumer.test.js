'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const {admitNormalization} = require('./admit-normalization-consumer');
const repository = 'honua-io/honua-server';

function fixture(head = 'current') {
  const pr = {number: 42, state: 'open', draft: false, head: {sha: head}, base: {repo: {full_name: repository}}};
  const run = {name: 'Derived Artifact Normalization', path: '.github/workflows/normalize-derived-artifacts.yml',
    event: 'pull_request', conclusion: 'success', repository: {full_name: repository}, head_sha: 'current', pull_requests: [{number: 42}]};
  return {pr, run, context: {repo: {owner: 'honua-io', repo: 'honua-server'}, payload: {workflow_run: run}},
    github: {rest: {pulls: {get: async () => ({data: pr})}, repos: {
      listPullRequestsAssociatedWithCommit: async () => ({data: [pr]}),
    }}}};
}

test('delayed old completion is rejected before entering current consumer cancellation group', async () => {
  assert.equal(await admitNormalization(fixture()), 42);
  const delayed = fixture();
  delayed.run.head_sha = 'superseded';
  assert.equal(await admitNormalization(delayed), null);
});

test('missing associations use live API, but ambiguity and closed or draft heads do not admit', async () => {
  const state = fixture();
  state.run.pull_requests = [];
  assert.equal(await admitNormalization(state), 42);
  state.pr.draft = true;
  assert.equal(await admitNormalization(state), null);
  state.pr.draft = false;
  state.pr.state = 'closed';
  assert.equal(await admitNormalization(state), null);
  state.run.pull_requests = [{number: 42}, {number: 43}];
  assert.equal(await admitNormalization(state), null);
});

test('other producers and base repositories cannot enter cancellation group', async () => {
  const state = fixture();
  state.run.path = 'other.yml';
  assert.equal(await admitNormalization(state), null);
  state.run.path = '.github/workflows/normalize-derived-artifacts.yml';
  state.pr.base.repo.full_name = 'other/repo';
  assert.equal(await admitNormalization(state), null);
});

test('metadata edits and drafts have isolated groups; head changes and base retargets share the PR group', () => {
  const source = fs.readFileSync('.github/workflows/normalize-derived-artifacts.yml', 'utf8');
  const expression = source.match(/group: normalization-producer-\$\{\{ (.+) \}\}/)[1];
  function group({action, changes = {}, draft = false, runId = 1}) {
    const github = {event: {action, changes, pull_request: {number: 42, draft}}, run_id: runId};
    return vm.runInNewContext(expression, {github, format: (_, id) => `ignored-${id}`});
  }
  assert.equal(group({action: 'synchronize'}), 42);
  assert.equal(group({action: 'edited', changes: {base: {ref: {from: 'old-base'}}}}), 42);
  // GitHub resolves absent properties to null, modeled for a title/body edit.
  const metadata = {base: {ref: {from: null}}};
  assert.equal(group({action: 'edited', changes: metadata}), 'ignored-1');
  assert.equal(group({action: 'edited', changes: metadata, runId: 2}), 'ignored-2');
  assert.equal(group({action: 'ready_for_review', draft: true}), 'ignored-1');
});
