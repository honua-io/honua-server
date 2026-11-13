'use strict';

const assert = require('node:assert/strict');

const SHA_LENGTH = 40;
const HEAD = 'a'.repeat(SHA_LENGTH);
const BASE = 'b'.repeat(SHA_LENGTH);
const POLICY = 'c'.repeat(SHA_LENGTH);
const RUN_ID = 123;
const JOB_ID = 456;
const RUN_ATTEMPT = 2;
const PULL_REQUEST_NUMBER = 42;
const JOBS_PAGE_SIZE = 100;
const REPOSITORY = { id: 1, full_name: 'honua-io/honua-server' };
const WORKFLOW_PATH = '.github/workflows/pr-gate.yml';
const WORKFLOW_NAME = 'PR Gate';
const COMPLETED_SUCCESS = { status: 'completed', conclusion: 'success' };

function recordingCore() {
  const outputs = {};
  const notices = [];
  return {
    outputs,
    notices,
    setOutput: (name, value) => { outputs[name] = value; },
    notice: (message) => notices.push(message),
    warning: (message) => notices.push(message),
  };
}

function workflowRun(overrides) {
  return {
    id: RUN_ID,
    path: WORKFLOW_PATH,
    name: WORKFLOW_NAME,
    event: 'pull_request',
    ...COMPLETED_SUCCESS,
    repository: { ...REPOSITORY },
    head_repository: { ...REPOSITORY },
    head_sha: HEAD,
    run_attempt: RUN_ATTEMPT,
    pull_requests: [],
    ...overrides,
  };
}

function workflowJob(overrides) {
  return {
    id: JOB_ID,
    run_id: RUN_ID,
    run_attempt: RUN_ATTEMPT,
    workflow_name: WORKFLOW_NAME,
    name: WORKFLOW_NAME,
    head_sha: HEAD,
    ...COMPLETED_SUCCESS,
    ...overrides,
  };
}

function checkRunWithAssociation(overrides) {
  const associated = {
    number: PULL_REQUEST_NUMBER,
    base: { ref: 'trunk', sha: BASE, repo: { id: REPOSITORY.id } },
    head: { sha: HEAD, repo: { id: REPOSITORY.id } },
  };
  return {
    id: JOB_ID,
    name: WORKFLOW_NAME,
    ...COMPLETED_SUCCESS,
    head_sha: HEAD,
    pull_requests: [associated],
    ...overrides,
  };
}

function openPullRequest(overrides) {
  return {
    number: PULL_REQUEST_NUMBER,
    state: 'open',
    base: { ref: 'trunk', sha: BASE, repo: { ...REPOSITORY } },
    head: { sha: HEAD, repo: { ...REPOSITORY } },
    ...overrides,
  };
}

function githubClient({ run, checkRun, pullRequest, jobs }) {
  const listJobs = Symbol('list-jobs');
  return {
    rest: {
      actions: {
        getWorkflowRun: async () => ({ data: run }),
        listJobsForWorkflowRun: listJobs,
      },
      checks: {
        get: async (input) => {
          assert.equal(input.check_run_id, JOB_ID);
          return { data: checkRun };
        },
      },
      pulls: {
        get: async () => ({ data: pullRequest }),
      },
    },
    paginate: async (method, input) => {
      assert.equal(method, listJobs);
      assert.equal(input.run_id, RUN_ID);
      assert.equal(input.filter, 'latest');
      assert.equal(input.per_page, JOBS_PAGE_SIZE);
      return jobs;
    },
  };
}

function fixtures(overrides = {}) {
  return {
    github: githubClient({
      run: workflowRun(overrides.run),
      checkRun: checkRunWithAssociation(overrides.checkRun),
      pullRequest: openPullRequest(overrides.pullRequest),
      jobs: overrides.jobs || [workflowJob(overrides.job)],
    }),
  };
}

function options(github, overrides = {}) {
  return {
    github,
    owner: 'honua-io',
    repo: 'honua-server',
    runId: String(RUN_ID),
    runAttempt: String(RUN_ATTEMPT),
    runConclusion: 'success',
    workflowPath: WORKFLOW_PATH,
    workflowName: WORKFLOW_NAME,
    defaultBranch: 'trunk',
    repositoryId: REPOSITORY.id,
    ...overrides,
  };
}

module.exports = { BASE, HEAD, POLICY, fixtures, options, recordingCore };
