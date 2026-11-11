const test = require('node:test');
const assert = require('node:assert/strict');
const revalidate = require('./revalidate-exact-head-review.cjs');

function harness(head, statuses) {
  const failures = [];
  const queries = [];
  return {
    failures, queries,
    input: {
      expectedHead: 'expected',
      context: { repo: { owner: 'owner', repo: 'repo' }, payload: { pull_request: { head: { sha: head } } } },
      core: { setFailed: message => failures.push(message) },
      github: {
        rest: { repos: { listCommitStatusesForRef: 'statuses' } },
        paginate: async (route, query) => { queries.push({ route, query }); return statuses; },
      },
    },
  };
}

for (const head of [undefined, 'wrong']) {
  test(`rejects untrusted head ${head} before API access`, async () => {
    const value = harness(head, [{ context: 'Review Gate', state: 'success' }]);
    await revalidate(value.input);
    assert.equal(value.failures.length, 1);
    assert.equal(value.queries.length, 0);
  });
}
for (const state of [undefined, 'pending', 'failure', 'error', 'success']) {
  test(`review status ${state} is fail closed`, async () => {
    const statuses = state ? [{ context: 'Review Gate', state }] : [{ context: 'another gate', state: 'success' }];
    const value = harness('expected', statuses);
    await revalidate(value.input);
    assert.equal(value.failures.length, state === 'success' ? 0 : 1);
    assert.deepEqual(value.queries, [{ route: 'statuses', query: { owner: 'owner', repo: 'repo', ref: 'expected', per_page: 100 } }]);
  });
}
test('latest status controls and API failures propagate', async () => {
  const value = harness('expected', [{ context: 'Review Gate', state: 'pending' }, { context: 'Review Gate', state: 'success' }]);
  await revalidate(value.input);
  assert.equal(value.failures.length, 1);
  value.input.github.paginate = async () => { throw new Error('unavailable'); };
  await assert.rejects(revalidate(value.input), /unavailable/);
});
