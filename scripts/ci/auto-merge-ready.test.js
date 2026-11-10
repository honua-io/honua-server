const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const workflow = fs.readFileSync('.github/workflows/auto-merge-ready.yml', 'utf8');
const script = workflow.split('          script: |\n')[1].replace(/^            /gm, '');
const ready = () => ({
  id: 'PR_1', state: 'OPEN', isDraft: false, baseRefName: 'trunk',
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
      assert.match(query, /mutation\([^)]*\)\s*\{\s*(enable|disable)PullRequestAutoMerge\(/);
      mutations.push({ ...variables, action: query.includes('disablePullRequestAutoMerge') ? 'disable' : 'enable' });
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
    { baseRefName: 'feature' }, { isDraft: true }, { state: 'CLOSED' }, { headRepository: null },
    { headRepository: { nameWithOwner: 'fork/repo' } },
    { labels: { nodes: [{ name: 'hold' }], pageInfo: { hasNextPage: false } } },
    { labels: { nodes: [], pageInfo: { hasNextPage: true } } },
    { labels: { nodes: [{ name: 'train:hold' }], pageInfo: { hasNextPage: false } } },
    { labels: { nodes: [{ name: 'train:escalated' }], pageInfo: { hasNextPage: false } } },
    { autoMergeRequest: { mergeMethod: 'SQUASH' } },
  ]) assert.equal((await run({ ...ready(), ...changes })).length, 0);
  assert.equal((await run(null)).length, 0);
});
test('real API errors remain failures', async () => {
  await assert.rejects(run(ready(), true), /publication rejected/);
});
test('bot credentials are scoped and contributor code is never executed', () => {
  assert.match(workflow, /secrets.BOT_APP_ID/);
  assert.match(workflow, /secrets.BOT_APP_PRIVATE_KEY/);
  assert.match(workflow, /types: \[opened, ready_for_review, reopened, synchronize, labeled, unlabeled, converted_to_draft, edited\]/);
  assert.doesNotMatch(workflow, /branches:/);
  assert.match(workflow, /github.event.pull_request.head.repo.full_name == github.repository/);
  assert.match(workflow, /repositories: \$\{\{ github.event.repository.name \}\}/);
  assert.match(workflow, /permission-contents: write/);
  assert.match(workflow, /permission-pull-requests: write/);
  assert.match(workflow, /permission-workflows: write/);
  assert.match(workflow, /permissions: \{\}/);
  assert.doesNotMatch(workflow, /actions\/checkout|pull_request_target/);
  assert.match(workflow, /cancel-in-progress: false\n  queue: max/);
});

test('live ineligibility disables existing auto-merge', async () => {
  for (const changes of [
    { baseRefName: 'feature' },
    { headRepository: { nameWithOwner: 'fork/repo' } },
    { isDraft: true },
    { labels: { nodes: [{ name: 'hold' }], pageInfo: { hasNextPage: false } } },
    { labels: { nodes: [{ name: 'train:hold' }], pageInfo: { hasNextPage: false } } },
    { labels: { nodes: [{ name: 'train:escalated' }], pageInfo: { hasNextPage: false } } },
  ]) {
    const actions = await run({ ...ready(), autoMergeRequest: { mergeMethod: 'SQUASH' }, ...changes });
    assert.equal(actions.length, 1);
    assert.equal(actions[0].action, 'disable');
  }
});
test('live commit-count changes replace the previous merge method', async () => {
  const actions = await run({ ...ready(), commits: { totalCount: 2 },
    autoMergeRequest: { mergeMethod: 'SQUASH' } });
  assert.equal(actions.length, 2);
  assert.equal(actions[0].action, 'disable');
  assert.equal(actions[1].action, 'enable');
  assert.equal(actions[1].mergeMethod, 'MERGE');
  const single = await run({ ...ready(), autoMergeRequest: { mergeMethod: 'MERGE' } });
  assert.equal(single.length, 2);
  assert.equal(single[0].action, 'disable');
  assert.equal(single[1].action, 'enable');
  assert.equal(single[1].mergeMethod, 'SQUASH');
});
