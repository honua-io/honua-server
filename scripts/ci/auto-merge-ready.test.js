const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const workflow = fs.readFileSync('.github/workflows/auto-merge-ready.yml', 'utf8');
const script = workflow.split('          script: |\n')[1].replace(/^            /gm, '');
const ready = () => ({
  id: 'PR_1', state: 'OPEN', isDraft: false,
  headRepository: { nameWithOwner: 'honua-io/honua-server' },
  labels: { nodes: [], pageInfo: { hasNextPage: false } },
  commits: { totalCount: 1 }, autoMergeRequest: null,
});
async function run(pr, failMutation = false) {
  const mutations = [];
  const summary = { addHeading() { return this; }, addRaw() { return this; }, async write() {} };
  await vm.runInNewContext(`(async () => {${script}})()`, {
    context: { repo: { owner: 'honua-io', repo: 'honua-server' }, issue: { number: 42 } },
    core: { notice() {}, summary },
    github: { async graphql(query, variables) {
      if (query.includes('query(')) return { repository: { pullRequest: pr } };
      if (failMutation) throw new Error('publication rejected');
      assert.match(query, /enablePullRequestAutoMerge/);
      assert.doesNotMatch(query, /(?<!Auto)mergePullRequest/);
      mutations.push(variables);
      return {};
    } },
  });
  return mutations;
}
test('single-commit PR enables squash; bundle enables merge', async () => {
  assert.equal((await run(ready()))[0].mergeMethod, 'SQUASH');
  const bundle = ready(); bundle.commits.totalCount = 3;
  assert.equal((await run(bundle))[0].mergeMethod, 'MERGE');
});
test('live draft, hold, fork, closed, deleted, or existing admission is untouched', async () => {
  for (const changes of [
    { isDraft: true }, { state: 'CLOSED' }, { headRepository: null },
    { headRepository: { nameWithOwner: 'fork/repo' } },
    { labels: { nodes: [{ name: 'hold' }], pageInfo: { hasNextPage: false } } },
    { labels: { nodes: [], pageInfo: { hasNextPage: true } } },
    { autoMergeRequest: { enabledAt: '2026-11-10' } },
  ]) assert.equal((await run({ ...ready(), ...changes })).length, 0);
  assert.equal((await run(null)).length, 0);
});
test('real API errors remain failures', async () => {
  await assert.rejects(run(ready(), true), /publication rejected/);
});
test('bot credentials are scoped and contributor code is never executed', () => {
  assert.match(workflow, /secrets.BOT_APP_ID/);
  assert.match(workflow, /secrets.BOT_APP_PRIVATE_KEY/);
  assert.match(workflow, /types: \[opened, ready_for_review, reopened\]/);
  assert.doesNotMatch(workflow, /actions\/checkout|pull_request_target/);
  assert.match(workflow, /cancel-in-progress: false\n  queue: max/);
});
