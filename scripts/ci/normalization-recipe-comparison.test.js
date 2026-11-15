'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const workflow = fs.readFileSync('.github/workflows/normalize-derived-artifacts-consumer.yml', 'utf8');
const step = workflow.split('      - name: Compare prospective blobs without checking out PR code\n')[1];
const script = step.split('          script: |\n')[1].split('\n      # Enforce mode only.')[0]
  .split('\n').map(line => line.replace(/^ {12}/, '')).join('\n');
const recipes = ['.github/workflows/normalize-derived-artifacts.yml',
  '.github/actions/setup-dotnet-ci/action.yml', 'scripts/ci/normalization-envelope.py'];

async function compare(mismatch) {
  const outputs = {};
  const reads = [];
  const notices = [];
  const plan = {source: {tree_sha: 'tree'}, generators: [{path: 'generator', sha256: 'fabricated'}], outputs: []};
  const summary = {addRaw() {return this;}, async write() {}};
  const github = {rest: {
    pulls: {get: async () => ({data: {state: 'open', draft: false, head: {sha: 'head'}}})},
    git: {
      getCommit: async () => ({data: {tree: {sha: 'tree'}}}),
      getTree: async () => ({data: {tree: [...recipes, 'generator'].map(path => ({path, type: 'blob', sha: path}))}}),
      getBlob: async ({file_sha}) => {
        reads.push(file_sha);
        const content = file_sha === mismatch ? Buffer.from('old recipe') : Buffer.from('trusted recipe');
        return {data: {encoding: 'base64', content: content.toString('base64')}};
      },
    },
  }};
  const fakeFs = {lstatSync: () => ({isFile: () => true}),
    readFileSync: path => path.endsWith('normalization-plan.json') ? JSON.stringify(plan) : Buffer.from('trusted recipe')};
  const run = vm.runInNewContext(`(async () => {${script}\n})`, {
    require: name => name === 'fs' ? fakeFs : require(name), Buffer,
    github, context: {repo: {owner: 'honua-io', repo: 'honua-server'}},
    process: {env: {RUNNER_TEMP: '/unused', HEAD_SHA: 'head', PR_NUMBER: '1'}},
    core: {setOutput: (key, value) => {outputs[key] = value;}, notice: value => notices.push(value), summary},
  });
  await run();
  return {outputs, reads, notices};
}

for (const recipe of recipes) {
  test(`compare_recipeMismatch_rejectsCandidateWithoutFailure: ${recipe}`, async () => {
    const result = await compare(recipe);
    assert.deepEqual(result.outputs, {superseded: 'true'});
    assert.equal(result.reads.includes('generator'), false);
    assert.match(result.notices[0], /does not match the trusted generation recipe/);
  });
}

test('compare_matchingRecipes_fabricatedGeneratorStillFails', async () => {
  await assert.rejects(compare(null), /generator digest does not match the exact PR head/);
});

test('compare_ineligibleCandidate_blocksBothCredentialAndMutationSteps', () => {
  const guards = workflow.match(/steps\.compare\.outputs\.superseded != 'true'/g);
  assert.equal(guards.length, 2);
});
