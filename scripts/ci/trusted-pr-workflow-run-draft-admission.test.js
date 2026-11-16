'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');

const { resolvePrGateForObservation, resolveTrustedPullRequestWorkflowRun } = require('./trusted-pr-workflow-run');
const { HEAD, POLICY, fixtures, options, recordingCore } = require('./trusted-pr-workflow-run.fixtures');

const HEAVY = 'PR Gate / Build and tests';
const RUNNER_SELECT = 'PR Gate / Runner select';

function heavyAttempt(runAttempt, conclusion, name = HEAVY) {
  return {
    run_id: 123, run_attempt: runAttempt, workflow_name: 'PR Gate',
    name, head_sha: HEAD, status: 'completed', conclusion,
  };
}

function draftAdmissionFixtures(conclusion, overrides = {}) {
  const fixture = fixtures({
    run: { conclusion: 'failure' },
    job: { conclusion: 'failure' },
    checkRun: { conclusion: 'failure' },
    pullRequest: { draft: overrides.draft ?? false },
  });
  const heavyJob = { ...heavyAttempt(2, conclusion), ...overrides.job };
  const runnerSelect = heavyAttempt(2, conclusion === 'skipped' ? 'skipped' : 'success', RUNNER_SELECT);
  const paginate = fixture.github.paginate;
  fixture.github.paginate = async (requested, input) => {
    if (input.filter !== 'all') return paginate(requested, input);
    assert.equal(requested, fixture.github.rest.actions.listJobsForWorkflowRun);
    assert.equal(input.run_id, 123);
    assert.equal(input.per_page, 100);
    return overrides.jobs ?? [heavyJob, runnerSelect];
  };
  return fixture;
}

for (const draft of [true, false]) {
  test(`draft admission is skipped even when current PR draft=${draft}`, async () => {
    const { github } = draftAdmissionFixtures('skipped', { draft });
    const core = recordingCore();
    const result = await resolvePrGateForObservation({
      ...options(github, { runConclusion: 'failure' }), core, label: 'impact',
    });
    assert.equal(result, null);
    assert.equal(core.outputs.skip_code, 'source-run-draft-admission');
  });
}

for (const conclusion of ['success', 'failure', 'cancelled']) {
  test(`heavy ${conclusion} remains observable despite current draft status`, async () => {
    const { github } = draftAdmissionFixtures(conclusion, { draft: true });
    const core = recordingCore();
    const result = await resolvePrGateForObservation({
      ...options(github, { runConclusion: 'failure' }), core, label: 'impact',
    });
    assert.equal(result.pullRequestNumber, 42);
    assert.equal(core.outputs.skip, 'false');
  });
}

for (const [name, overrides] of [
  ['missing', { jobs: [] }],
  ['wrong attempt', { job: { run_attempt: 3 } }],
  ['wrong head', { job: { head_sha: POLICY } }],
  ['unfinished', { job: { status: 'in_progress' } }],
  ['unknown conclusion', { job: { conclusion: null } }],
]) {
  test(`draft admission rejects ${name} job evidence`, async () => {
    const { github } = draftAdmissionFixtures('skipped', overrides);
    await assert.rejects(resolvePrGateForObservation({
      ...options(github, { runConclusion: 'failure' }),
      core: recordingCore(), label: 'impact',
    }), /canonical PR Gate \/ Build and tests job/);
  });
}

test('partial rerun observes the heavy outcome retained from an earlier attempt', async () => {
  const { github } = draftAdmissionFixtures('success', {
    jobs: [heavyAttempt(1, 'success'), heavyAttempt(1, 'success', RUNNER_SELECT)],
  });
  const core = recordingCore();
  const result = await resolvePrGateForObservation({
    ...options(github, { runConclusion: 'failure' }), core, label: 'impact',
  });
  assert.equal(result.pullRequestNumber, 42);
  assert.equal(core.outputs.skip, 'false');
});

test('the latest heavy attempt at or before the source decides draft admission', async () => {
  const { github } = draftAdmissionFixtures('skipped', {
    jobs: [heavyAttempt(1, 'failure'), heavyAttempt(2, 'skipped'), heavyAttempt(3, 'success'),
      heavyAttempt(2, 'skipped', RUNNER_SELECT)],
  });
  const core = recordingCore();
  const result = await resolvePrGateForObservation({
    ...options(github, { runConclusion: 'failure' }), core, label: 'impact',
  });
  assert.equal(result, null);
  assert.equal(core.outputs.skip_code, 'source-run-draft-admission');
});

for (const conclusion of ['failure', 'cancelled']) {
  test(`ready run with runner select ${conclusion} stays observable despite a skipped heavy job`, async () => {
    const { github } = draftAdmissionFixtures('skipped', {
      jobs: [heavyAttempt(2, 'skipped'), heavyAttempt(2, conclusion, RUNNER_SELECT)],
    });
    const core = recordingCore();
    const result = await resolvePrGateForObservation({
      ...options(github, { runConclusion: 'failure' }), core, label: 'impact',
    });
    assert.equal(result.pullRequestNumber, 42);
    assert.equal(core.outputs.skip, 'false');
  });
}

test('draft admission rejects missing runner select evidence', async () => {
  const { github } = draftAdmissionFixtures('skipped', { jobs: [heavyAttempt(2, 'skipped')] });
  await assert.rejects(resolvePrGateForObservation({
    ...options(github, { runConclusion: 'failure' }), core: recordingCore(), label: 'impact',
  }), /Runner select/);
});

test('draft admission rejects duplicate heavy jobs', async () => {
  const { github } = draftAdmissionFixtures('skipped', {
    jobs: [heavyAttempt(1, 'success'), heavyAttempt(2, 'skipped'), heavyAttempt(2, 'skipped'),
      heavyAttempt(2, 'skipped', RUNNER_SELECT)],
  });
  await assert.rejects(resolvePrGateForObservation({
    ...options(github, { runConclusion: 'failure' }), core: recordingCore(), label: 'impact',
  }), /canonical PR Gate \/ Build and tests job/);
});

function draftAggregateFixtures(name) {
  const fixture = fixtures({});
  const draftJob = { ...heavyAttempt(2, 'success', name), id: 456 };
  const paginate = fixture.github.paginate;
  fixture.github.paginate = async (requested, input) => (
    input.filter === 'latest' ? [draftJob] : paginate(requested, input));
  return fixture;
}

test('a run that reported only the draft aggregate is draft admission', async () => {
  const { github } = draftAggregateFixtures('PR Gate (draft)');
  const core = recordingCore();
  const result = await resolvePrGateForObservation({
    ...options(github, { runConclusion: 'success' }), core, label: 'impact',
  });
  assert.equal(result, null);
  assert.equal(core.outputs.skip_code, 'source-run-draft-admission');
  await assert.rejects(resolveTrustedPullRequestWorkflowRun(options(github, { runConclusion: 'success' })),
    /only the draft aggregate/);
});

test('a draft aggregate never resolves as the canonical PR Gate job', async () => {
  for (const name of ['PR Gate (draft) ', 'Other (draft)']) {
    const { github } = draftAggregateFixtures(name);
    const core = recordingCore();
    const result = await resolvePrGateForObservation({
      ...options(github, { runConclusion: 'success' }), core, label: 'impact',
    });
    assert.equal(result, null);
    assert.equal(core.outputs.skip_code, 'source-run-job-absent');
  }
});
