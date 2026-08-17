'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');

const {
  SKIP_CODE,
  UnresolvedTrustedWorkflowRunError,
  parseRunId,
  recordObservationSkip,
  resolveForObservation,
  resolveTrustedPullRequestWorkflowRun,
} = require('./trusted-pr-workflow-run');

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

const HEAD = 'a'.repeat(40);
const BASE = 'b'.repeat(40);
const POLICY = 'c'.repeat(40);

function fixtures(overrides = {}) {
  const run = {
    id: 123,
    path: '.github/workflows/pr-gate.yml',
    name: 'PR Gate',
    event: 'pull_request',
    status: 'completed',
    conclusion: 'success',
    repository: { id: 1, full_name: 'honua-io/honua-server' },
    head_repository: { id: 1, full_name: 'honua-io/honua-server' },
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
      repo: { id: 1, full_name: 'honua-io/honua-server' },
    },
    head: {
      sha: HEAD,
      repo: { id: 1, full_name: 'honua-io/honua-server' },
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

function options(github, overrides = {}) {
  return {
    github,
    owner: 'honua-io',
    repo: 'honua-server',
    runId: '123',
    runAttempt: '2',
    runConclusion: 'success',
    workflowPath: '.github/workflows/pr-gate.yml',
    workflowName: 'PR Gate',
    defaultBranch: 'trunk',
    repositoryId: 1,
    ...overrides,
  };
}

function resolve(github, overrides = {}) {
  return resolveTrustedPullRequestWorkflowRun(options(github, overrides));
}

function skipResolve(github, overrides = {}) {
  return resolveTrustedPullRequestWorkflowRun(options(github, { unresolved: 'skip', ...overrides }));
}

function reviewGateFixtures({ runSha = HEAD, baseSha = BASE } = {}) {
  const association = {
    number: 42,
    base: { ref: 'trunk', sha: baseSha, repo: { id: 1 } },
    head: { sha: HEAD, repo: { id: 1 } },
  };
  return fixtures({
    run: {
      path: '.github/workflows/review-gate.yml',
      name: 'Review Gate Attestation',
      event: 'pull_request_target',
      conclusion: 'failure',
      head_sha: runSha,
    },
    job: {
      workflow_name: 'Review Gate Attestation',
      name: 'Resolve pull request identity',
      head_sha: runSha,
    },
    checkRun: {
      name: 'Resolve pull request identity',
      head_sha: runSha,
      pull_requests: [association],
    },
    pullRequest: {
      base: {
        ref: 'trunk',
        sha: baseSha,
        repo: { id: 1, full_name: 'honua-io/honua-server' },
      },
    },
  });
}

function resolveReviewGate(
  github,
  { workflowShaRole = 'pull-request-target-associated' } = {},
) {
  return resolveTrustedPullRequestWorkflowRun({
    github,
    owner: 'honua-io',
    repo: 'honua-server',
    runId: '123',
    runAttempt: '2',
    runConclusion: 'failure',
    workflowPath: '.github/workflows/review-gate.yml',
    workflowName: 'Review Gate Attestation',
    workflowEvent: 'pull_request_target',
    workflowShaRole,
    jobName: 'Resolve pull request identity',
    jobConclusion: 'success',
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

test('derives a pull-request-target candidate from the association, not run SHA', async () => {
  const { github } = reviewGateFixtures();
  const result = await resolveReviewGate(github);
  assert.equal(result.run.conclusion, 'failure');
  assert.equal(result.job.conclusion, 'success');
  assert.equal(result.pullRequestNumber, 42);
  assert.equal(result.baseSha, BASE);
  assert.equal(result.headSha, HEAD);
  assert.equal(result.workflowSha, HEAD);
  assert.equal(result.workflowShaRole, 'association-head');
});

test('accepts a pull-request-target run represented by its associated base', async () => {
  const { github } = reviewGateFixtures({ runSha: POLICY, baseSha: POLICY });
  const result = await resolveReviewGate(github);
  assert.equal(result.baseSha, POLICY);
  assert.equal(result.headSha, HEAD);
  assert.equal(result.workflowSha, POLICY);
  assert.equal(result.workflowShaRole, 'association-base');
});

test('requires pull-request-target workflows to declare their associated SHA role', async () => {
  const { github } = reviewGateFixtures();
  await assert.rejects(
    resolveReviewGate(github, { workflowShaRole: 'pull-request-head' }),
    /input is incomplete/,
  );
});

test('rejects a pull-request-target run outside both associated commits', async () => {
  const { github } = reviewGateFixtures({ runSha: 'd'.repeat(40), baseSha: POLICY });
  await assert.rejects(resolveReviewGate(github), /identity is inconsistent/);
});

test('explicitly excludes fork workflow runs from the evidence denominator', async () => {
  const { github } = fixtures({
    run: { head_repository: { id: 2, full_name: 'contributor/honua-server' } },
  });
  await assert.rejects(resolve(github), /completed canonical/);
});

test('binds resolution to the triggering run attempt and conclusion', async () => {
  const { github } = fixtures();
  await assert.rejects(resolve(github, { runAttempt: '1' }), /completed canonical/);
  await assert.rejects(resolve(github, { runConclusion: 'failure' }), /completed canonical/);
  await assert.rejects(resolve(github, { runAttempt: '0' }), /workflow run attempt/);
  await assert.rejects(resolve(github, { runConclusion: 'in_progress' }), /workflow run conclusion/);
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
        repo: { id: 1, full_name: 'honua-io/honua-server' },
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

// Observation-only consumers (pr-gate-impact-observe, native-image-impact-observe,
// server-test-prebuild-observe) wake on EVERY completed source run, including the
// cancelled/superseded ones a fresh push produces. Fail-closed is still correct,
// but for them the correct fail-closed outcome is a clean skip, not ~93 red runs.
// Only the superseded class may be downgraded, and each shape reports its code.
test('skip mode downgrades exactly the superseded-source shapes', async () => {
  const supersededAssociation = {
    number: 42,
    base: { ref: 'trunk', sha: BASE, repo: { id: 1 } },
    head: { sha: 'e'.repeat(40), repo: { id: 1 } },
  };
  const cases = [
    // A cancelled source never reached the terminal job the caller pinned.
    ['source-run-job-absent', { jobs: [] },
      /exactly one canonical workflow job/],
    // A cancelled check run routinely retains no pull-request association.
    ['no-pull-request-association', { checkRun: { pull_requests: [] } },
      /exactly one pull request/],
    // The run head is outside the association: the push was superseded.
    ['pull-request-identity-superseded',
      { checkRun: { pull_requests: [supersededAssociation] } },
      /identity is inconsistent/],
    // The pull request advanced while the observer was waking.
    ['pull-request-moved', {
      pullRequest: {
        head: { sha: 'd'.repeat(40), repo: { id: 1, full_name: 'honua-io/honua-server' } },
      },
    }, /moved after/],
    // The pull request closed before the observer ran.
    ['pull-request-moved', { pullRequest: { state: 'closed' } }, /moved after/],
  ];
  for (const [code, overrides, pattern] of cases) {
    const { github } = fixtures(overrides);
    await assert.rejects(resolve(github), pattern, code);
    const skipped = await skipResolve(github);
    assert.equal(skipped.skipped, true, code);
    assert.equal(skipped.code, code);
    assert.match(skipped.reason, pattern, code);
    assert.match(skipped.code, SKIP_CODE);
  }
});

test('skip mode never silences misconfiguration or API drift', async () => {
  const twoAssociations = [
    { number: 41, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD, repo: { id: 1 } } },
    { number: 42, base: { ref: 'trunk', sha: BASE, repo: { id: 1 } }, head: { sha: HEAD, repo: { id: 1 } } },
  ];
  const duplicateJob = {
    id: 458,
    run_id: 123,
    run_attempt: 2,
    workflow_name: 'PR Gate',
    name: 'PR Gate',
    head_sha: HEAD,
    status: 'completed',
    conclusion: 'success',
  };
  const cases = [
    // Wrong workflow identity / fork head / dispatch-input mismatch.
    [{ run: { path: '.github/workflows/lookalike.yml' } }, {}, /completed canonical/],
    [{ run: { name: 'Lookalike' } }, {}, /completed canonical/],
    [{ run: { event: 'workflow_dispatch' } }, {}, /completed canonical/],
    [{ run: { head_repository: { id: 2, full_name: 'contributor/honua-server' } } }, {},
      /completed canonical/],
    [{}, { runAttempt: '1' }, /completed canonical/],
    [{}, { runConclusion: 'failure' }, /completed canonical/],
    // Ambiguous job / check-run shape drift / ambiguous association.
    [{ jobs: [{ ...duplicateJob, id: 456 }, duplicateJob] }, {}, /exactly one canonical/],
    [{ checkRun: { head_sha: 'c'.repeat(40) } }, {}, /check identity is inconsistent/],
    [{ checkRun: { pull_requests: twoAssociations } }, {}, /exactly one pull request/],
    // Cross-repository association identity.
    [{ checkRun: { pull_requests: [{
      number: 42,
      base: { ref: 'trunk', sha: BASE, repo: { id: 99 } },
      head: { sha: HEAD, repo: { id: 1 } },
    }] } }, {}, /identity is inconsistent/],
    [{ pullRequest: {
      head: { sha: HEAD, repo: { id: 99, full_name: 'contributor/honua-server' } },
    } }, {}, /moved after/],
  ];
  for (const [overrides, callOverrides, pattern] of cases) {
    const { github } = fixtures(overrides);
    await assert.rejects(skipResolve(github, callOverrides), pattern);
    await assert.rejects(
      skipResolve(github, callOverrides),
      (error) => !(error instanceof UnresolvedTrustedWorkflowRunError),
    );
  }
});

test('skip mode never masks malformed caller input or a resolved success', async () => {
  const { github } = fixtures();
  await assert.rejects(skipResolve(github, { runConclusion: 'in_progress' }), /workflow run conclusion/);
  await assert.rejects(skipResolve(github, { runId: '0' }), /workflow run id/);
  await assert.rejects(skipResolve(github, { defaultBranch: '' }), /input is incomplete/);
  await assert.rejects(resolve(github, { unresolved: 'ignore' }), /unresolved-source behavior/);
  const resolved = await skipResolve(github);
  assert.equal(resolved.skipped, false);
  assert.equal(resolved.pullRequestNumber, 42);
});

test('the default behavior stays fail-closed for trusted callers', async () => {
  const { github } = fixtures({ checkRun: { pull_requests: [] } });
  await assert.rejects(resolve(github), UnresolvedTrustedWorkflowRunError);
});

test('resolveForObservation records one skip and returns null', async () => {
  const { github } = fixtures({ checkRun: { pull_requests: [] } });
  const core = recordingCore();
  const written = {};
  const result = await resolveForObservation({
    ...options(github),
    core,
    label: 'pr-gate-impact',
    markerPath: '/tmp/marker.json',
    fs: { writeFileSync: (path, body) => { written[path] = body; } },
  });
  assert.equal(result, null);
  assert.equal(core.outputs.skip, 'true');
  assert.equal(core.outputs.skip_code, 'no-pull-request-association');
  assert.match(core.outputs.skip_reason, /exactly one pull request/);
  assert.equal(core.notices.length, 1);
  const marker = JSON.parse(written['/tmp/marker.json']);
  assert.equal(marker.contract, 'honua.ci.observation-skipped/v1');
  assert.equal(marker.code, 'no-pull-request-association');
  assert.equal(marker.observer, 'pr-gate-impact');
  assert.equal(marker.source_run_conclusion, 'success');
});

test('resolveForObservation passes a resolved source straight through', async () => {
  const { github } = fixtures();
  const core = recordingCore();
  const result = await resolveForObservation({
    ...options(github),
    core,
    label: 'pr-gate-impact',
  });
  assert.equal(result.pullRequestNumber, 42);
  assert.equal(core.outputs.skip, 'false');
  assert.deepEqual(core.notices, []);
});

test('resolveForObservation still throws drift and requires its own inputs', async () => {
  const { github } = fixtures({ run: { name: 'Lookalike' } });
  const core = recordingCore();
  await assert.rejects(
    resolveForObservation({ ...options(github), core, label: 'pr-gate-impact' }),
    /completed canonical/,
  );
  assert.equal(core.outputs.skip, undefined);
  await assert.rejects(
    resolveForObservation({ ...options(github), label: 'pr-gate-impact' }),
    /requires core and a label/,
  );
});

test('recordObservationSkip mirrors the resolver skip surface and bounds its code', () => {
  const core = recordingCore();
  const written = {};
  recordObservationSkip({
    core,
    label: 'server-test-prebuild',
    markerPath: '/tmp/draft.json',
    fs: { writeFileSync: (path, body) => { written[path] = body; } },
    code: 'pull-request-draft',
    reason: 'the pull request is a draft',
    source: { runId: '123', runAttempt: '1', runConclusion: 'success' },
  });
  assert.equal(core.outputs.skip, 'true');
  assert.equal(core.outputs.skip_code, 'pull-request-draft');
  assert.equal(JSON.parse(written['/tmp/draft.json']).code, 'pull-request-draft');
  for (const code of ['', 'Not Bounded', 'a'.repeat(49), '-leading']) {
    assert.throws(
      () => recordObservationSkip({ core, label: 'x', code, reason: 'r' }),
      /bounded code/,
    );
  }
});
