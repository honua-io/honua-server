const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const workflow = fs.readFileSync('.github/workflows/cancelled-check-rerun.yml', 'utf8');
const script = workflow.split('          script: |\n')[1].replace(/^            /gm, '');
const HEAD = 'a'.repeat(40);
const cancelled = (changes = {}) => ({
  id: 2, name: 'Repository hygiene', workflow_id: 7, check_suite_id: 20, head_sha: HEAD, head_branch: 'fix/x',
  head_repository: { owner: { login: 'honua-io' } },
  status: 'completed', conclusion: 'cancelled', run_attempt: 1, ...changes,
});
const success = (changes = {}) => cancelled({ id: 1, check_suite_id: 10, conclusion: 'success', ...changes });

async function run({ trigger = cancelled(), runs = [success(), cancelled()], pulls = [{ number: 42, head: { sha: HEAD } }] } = {}) {
  const reruns = [];
  const calls = [];
  const listWorkflowRuns = Symbol('listWorkflowRuns');
  const listPulls = Symbol('listPulls');
  await vm.runInNewContext(`(async () => {${script}})()`, {
    context: { payload: { workflow_run: trigger }, repo: { owner: 'honua-io', repo: 'honua-server' } },
    core: { notice() {} },
    github: {
      rest: {
        actions: {
          listWorkflowRuns,
          async reRunWorkflow(parameters) { reruns.push(parameters.run_id); },
        },
        pulls: { list: listPulls },
      },
      async paginate(method, parameters) {
        calls.push(parameters);
        if (method === listWorkflowRuns) return runs;
        if (method === listPulls) return pulls;
        throw new Error('unexpected paginate');
      },
    },
  });
  return { reruns, calls };
}

test('a cancelled run in the newest suite of an open PR head is rerun once', async () => {
  const { reruns, calls } = await run();
  assert.deepEqual(reruns, [2]);
  assert.equal(calls[0].head_sha, HEAD);
  assert.equal(calls[0].event, 'pull_request');
  assert.equal(calls[1].head, 'honua-io:fix/x');
  assert.equal(calls[1].state, 'open');
});

test('suite order decides, not run id or creation order', async () => {
  const trigger = cancelled({ id: 1, check_suite_id: 30 });
  assert.deepEqual((await run({ trigger, runs: [trigger, success({ id: 2, check_suite_id: 20 })] })).reruns, [1]);
});

test('supersession, stale heads, closed PRs and later attempts are left alone', async () => {
  // A newer suite exists: the cancellation was normal supersession.
  assert.deepEqual((await run({ runs: [cancelled(), success({ id: 3, check_suite_id: 30 })] })).reruns, []);
  // The listing shows the run already rerun or running again.
  for (const changes of [{ run_attempt: 2 }, { status: 'in_progress', conclusion: null }]) {
    assert.deepEqual((await run({ runs: [success(), cancelled(changes)] })).reruns, []);
  }
  // The PR moved to another head, closed, or the head repository is gone.
  assert.deepEqual((await run({ pulls: [{ number: 42, head: { sha: 'b'.repeat(40) } }] })).reruns, []);
  assert.deepEqual((await run({ pulls: [] })).reruns, []);
  assert.deepEqual((await run({ trigger: cancelled({ head_repository: null }) })).reruns, []);
});

test('Clean code runs are handled with the same rules, per workflow', async () => {
  const trigger = cancelled({ name: 'Clean code', workflow_id: 9 });
  const { reruns, calls } = await run({ trigger, runs: [success({ workflow_id: 9 }), trigger] });
  assert.deepEqual(reruns, [2]);
  assert.equal(calls[0].workflow_id, 9);
});

test('a run missing from the listing falls back to the event, and only the PR with this head counts', async () => {
  const pulls = [{ number: 41, head: { sha: 'b'.repeat(40) } }, { number: 42, head: { sha: HEAD } }];
  assert.deepEqual((await run({ runs: [success()], pulls })).reruns, [2]);
});

test('fork heads are matched by owner and branch', async () => {
  const trigger = cancelled({ head_repository: { owner: { login: 'someone' } } });
  const { reruns, calls } = await run({ trigger, runs: [success(), trigger] });
  assert.deepEqual(reruns, [2]);
  assert.equal(calls[1].head, 'someone:fix/x');
});

test('API errors stay failures', async () => {
  await assert.rejects(vm.runInNewContext(`(async () => {${script}})()`, {
    context: { payload: { workflow_run: cancelled() }, repo: { owner: 'honua-io', repo: 'honua-server' } },
    core: { notice() {} },
    github: { rest: { actions: {}, pulls: {} }, async paginate() { throw new Error('rate limited'); } },
  }), /rate limited/);
});

test('only cancelled first attempts start a job, with least privilege and no checkout', () => {
  assert.match(workflow, /^on:\n  workflow_run:\n    workflows: \[Repository hygiene, Clean code\]\n    types: \[completed\]\n/m);
  assert.match(workflow, /^permissions: \{\}$/m);
  assert.match(workflow, /    if: >-\n      github.event.workflow_run.event == 'pull_request'\n      && github.event.workflow_run.conclusion == 'cancelled'\n      && github.event.workflow_run.run_attempt == 1\n    runs-on:/);
  assert.match(workflow, /permissions:\n      actions: write\n      pull-requests: read\n    timeout-minutes: 5/);
  assert.doesNotMatch(workflow, /actions\/checkout/);
  assert.doesNotMatch(workflow, /secrets\./);
  assert.doesNotMatch(workflow, /\brun: /);
});
