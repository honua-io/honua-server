'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');

const {
  parseRunId,
  resolveTrustedPullRequestWorkflowRun,
} = require('./trusted-pr-workflow-run');

const HEAD = 'a'.repeat(40);
const BASE = 'b'.repeat(40);

function fixtures(overrides = {}) {
  const run = {
    id: 123,
    path: '.github/workflows/pr-gate.yml',
    name: 'PR Gate',
    event: 'pull_request',
    status: 'completed',
    conclusion: 'success',
    repository: { full_name: 'honua-io/honua-server' },
    head_repository: { full_name: 'honua-io/honua-server' },
    head_sha: HEAD,
    run_attempt: 2,
    pull_requests: [],
    ...overrides.run,
  };
  const job = {
    id: 456,
    run_id: 123,
    run_attempt: 2,
    workflow_name: 'PR Gate',
    name: 'PR Gate',
    head_sha: HEAD,
    status: 'completed',
    conclusion: 'success',
    ...overrides.job,
  };
  const associated = {
    number: 42,
    base: { ref: 'trunk', sha: BASE, repo: { id: 1 } },
    head: { sha: HEAD, repo: { id: 1 } },
  };
  const checkRun = {
    id: 456,
    name: 'PR Gate',
    status: 'completed',
    conclusion: 'success',
    head_sha: HEAD,
    pull_requests: [associated],
    ...overrides.checkRun,
  };
  const pullRequest = {
    number: 42,
    state: 'open',
    base: {
      ref: 'trunk',
      sha: BASE,
      repo: { full_name: 'honua-io/honua-server' },
    },
    head: {
      sha: HEAD,
      repo: { full_name: 'honua-io/honua-server' },
    },
    ...overrides.pullRequest,
  };
  const jobs = overrides.jobs || [job];
  const listJobs = Symbol('list-jobs');
  const github = {
    rest: {
      actions: {
        getWorkflowRun: async () => ({ data: run }),
        listJobsForWorkflowRun: listJobs,
      },
      checks: {
        get: async (input) => {
          assert.equal(input.check_run_id, 456);
          return { data: checkRun };
        },
      },
      pulls: {
        get: async () => ({ data: pullRequest }),
      },
    },
    paginate: async (method, input) => {
      assert.equal(method, listJobs);
      assert.equal(input.run_id, 123);
      assert.equal(input.filter, 'latest');
      assert.equal(input.per_page, 100);
      return jobs;
    },
  };
  return { github };
}

function resolve(github) {
  return resolveTrustedPullRequestWorkflowRun({
    github,
    owner: 'honua-io',
    repo: 'honua-server',
    runId: '123',
    workflowPath: '.github/workflows/pr-gate.yml',
    workflowName: 'PR Gate',
    defaultBranch: 'trunk',
    repositoryId: 1,
  });
}

test('uses the immutable check-run association when workflow_run PRs are empty', async () => {
  const { github } = fixtures();
  const result = await resolve(github);
  assert.equal(result.pullRequestNumber, 42);
  assert.equal(result.baseSha, BASE);
  assert.equal(result.headSha, HEAD);
  assert.equal(result.pullRequest.head.repo.full_name, 'honua-io/honua-server');
});

test('explicitly excludes fork workflow runs from the evidence denominator', async () => {
  const { github } = fixtures({
    run: { head_repository: { full_name: 'contributor/honua-server' } },
  });
  await assert.rejects(resolve(github), /completed canonical/);
});

test('fails closed on missing or ambiguous check-run associations', async () => {
  for (const pull_requests of [[], [
    { number: 41, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD } },
    { number: 42, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD } },
  ]]) {
    const { github } = fixtures({ checkRun: { pull_requests } });
    await assert.rejects(resolve(github), /exactly one pull request/);
  }
});

test('rejects a current PR whose base advanced after the gate run', async () => {
  const { github } = fixtures({
    pullRequest: {
      base: {
        ref: 'trunk',
        sha: 'c'.repeat(40),
        repo: { full_name: 'honua-io/honua-server' },
      },
    },
  });
  await assert.rejects(resolve(github), /moved after/);
});

test('rejects malformed or lookalike workflow runs', async () => {
  const cases = [
    { path: '.github/workflows/lookalike.yml' },
    { name: 'Lookalike' },
    { event: 'workflow_dispatch' },
    { status: 'in_progress', conclusion: null },
    { conclusion: null },
    { repository: { full_name: 'other/repository' } },
    { head_sha: 'short' },
    { run_attempt: 0 },
  ];
  for (const run of cases) {
    const { github } = fixtures({ run });
    await assert.rejects(resolve(github), /completed canonical/);
  }
});

test('rejects missing, duplicate, or mismatched canonical jobs', async () => {
  const wrong = {
    id: 457,
    run_id: 123,
    run_attempt: 2,
    workflow_name: 'PR Gate',
    name: 'Other',
    head_sha: HEAD,
    status: 'completed',
    conclusion: 'success',
  };
  for (const jobs of [[], [wrong], [
    { ...wrong, id: 456, name: 'PR Gate' },
    { ...wrong, id: 458, name: 'PR Gate' },
  ]]) {
    const { github } = fixtures({ jobs });
    await assert.rejects(resolve(github), /exactly one canonical/);
  }
});

test('rejects inconsistent check-run and event-time association identities', async () => {
  const cases = [
    { id: 999 },
    { name: 'Lookalike' },
    { status: 'in_progress' },
    { conclusion: 'failure' },
    { head_sha: 'c'.repeat(40) },
    { pull_requests: [{ number: 42, base: { ref: 'other', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD } }] },
    { pull_requests: [{ number: 42, base: { ref: 'trunk', sha: BASE, repo: { id: 99 } }, head: { sha: HEAD } }] },
    { pull_requests: [{ number: 42, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD, repo: { id: 99 } } }] },
    { pull_requests: [{ number: 42, base: { ref: 'trunk', sha: 'short', repo: { id: 1 } }, head: { sha: HEAD } }] },
    { pull_requests: [{ number: 42, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: 'c'.repeat(40) } }] },
  ];
  for (const checkRun of cases) {
    const { github } = fixtures({ checkRun });
    await assert.rejects(resolve(github), /check|identity/);
  }
});

test('accepts only positive safe integer workflow run ids', () => {
  assert.equal(parseRunId('123'), 123);
  for (const value of ['', '0', '-1', '1.5', '01', '9007199254740992', true]) {
    assert.throws(() => parseRunId(value), /workflow run id/);
  }
});
