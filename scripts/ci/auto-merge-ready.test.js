const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const workflow = fs.readFileSync('.github/workflows/auto-merge-ready.yml', 'utf8');
const script = workflow.split('          script: |\n')[1].replace(/^            /gm, '');
const ready = () => ({
  id: 'PR_1', state: 'OPEN', isDraft: false, baseRefName: 'trunk', mergeStateStatus: 'BLOCKED',
  headRepository: { nameWithOwner: 'honua-io/honua-server' },
  labels: { nodes: [], pageInfo: { hasNextPage: false } },
  commits: { totalCount: 1 }, autoMergeRequest: null,
});
async function run(pr, mutationError = null) {
  const mutations = [];
  const summary = { addHeading() { return this; }, addRaw() { return this; }, async write() {} };
  await vm.runInNewContext(`(async () => {${script}})()`, {
    context: { repo: { owner: 'honua-io', repo: 'honua-server' }, issue: { number: 42 } },
    core: { notice() {}, summary },
    github: { async graphql(query, variables) {
      if (query.includes('query(')) return { repository: { pullRequest: pr } };
      if (mutationError) {
        if (mutationError instanceof Error) throw mutationError;
        const messages = Array.isArray(mutationError) ? mutationError : [mutationError];
        throw Object.assign(new Error(`Request failed: ${messages.join('; ')}`), {
          errors: messages.map(message => ({ type: 'UNPROCESSABLE', message })),
        });
      }
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
  await assert.rejects(run(ready(), 'publication rejected'), /publication rejected/);
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

test('already mergeable pull requests stay neutral without direct merging', async () => {
  for (const mergeStateStatus of ['CLEAN', 'UNSTABLE', 'HAS_HOOKS']) {
    assert.equal((await run({ ...ready(), mergeStateStatus })).length, 0);
  }
});
test('Dependabot PRs are skipped before requesting unavailable App secrets', () => {
  assert.match(workflow, /github.event.pull_request.user.login != 'dependabot\[bot\]'/);
});

test('retargeting away from trunk still revokes admission', () => {
  assert.match(workflow, /\(github\.event\.pull_request\.base\.ref == 'trunk' \|\| github\.event\.action == 'edited'\)/);
});
test('already mergeable method changes revoke stale auto-merge', async () => {
  const actions = await run({ ...ready(), mergeStateStatus: 'CLEAN', commits: { totalCount: 3 },
    autoMergeRequest: { mergeMethod: 'SQUASH' } });
  assert.equal(actions.length, 1);
  assert.equal(actions[0].action, 'disable');
});
test('mergeability computation race stays neutral only for the clean-status API error', async () => {
  const pr = { ...ready(), mergeStateStatus: 'UNKNOWN' };
  await run(pr, 'Pull request is in clean status');
  await assert.rejects(run(pr, 'publication rejected'), /publication rejected/);
  await assert.rejects(run(pr, ['Pull request is in clean status', 'publication rejected']), /publication rejected/);
  await assert.rejects(run(pr, new Error('network failure')), /network failure/);
  await assert.rejects(run(pr, new Error('Pull request is in clean status')), /clean status/);
  const emptyErrors = new Error('Pull request is in clean status');
  emptyErrors.errors = [];
  await assert.rejects(run(pr, emptyErrors), /clean status/);
});
