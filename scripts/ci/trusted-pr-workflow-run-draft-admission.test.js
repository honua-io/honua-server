'use strict';

const assert = require('node:assert/strict');
const fsModule = require('node:fs');
const pathModule = require('node:path');
const test = require('node:test');

const { resolvePrGateForObservation } = require('./trusted-pr-workflow-run');
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

function observerCollectScript(workflow, stepName) {
  const yaml = fsModule.readFileSync(pathModule.join(__dirname, '../../.github/workflows', workflow), 'utf8');
  const step = yaml.slice(yaml.indexOf(`name: ${stepName}`));
  assert.ok(step.length < yaml.length, 'observer collect step exists');
  const lines = step.slice(step.indexOf('script: |\n') + 'script: |\n'.length).split('\n');
  const indent = lines[0].match(/^ +/)[0].length;
  const body = [];
  for (const line of lines) {
    if (line.trim() && (line.match(/^ */)[0].length < indent)) break;
    body.push(line.slice(indent));
  }
  return body.join('\n');
}

for (const [workflow, stepName] of [
  ['pr-gate-impact-observe.yml', 'Collect exact change metadata from trusted policy'],
  ['native-image-impact-observe.yml', 'Resolve canonical exact-head PR Gate subject'],
]) {
  for (const draft of [true, false]) {
    test(`${workflow} excludes exact-attempt draft admission when current draft=${draft}`, async () => {
      const { github } = draftAdmissionFixtures('skipped', { draft });
      github.rest.repos = { get: async () => ({ data: { default_branch: 'trunk', id: 1 } }) };
      const core = recordingCore();
      const files = {};
      const fakeRequire = (name) => {
        if (name === 'fs') return { writeFileSync: (file, body) => { files[file] = body; } };
        if (name === './policy/scripts/ci/trusted-pr-workflow-run') return require('./trusted-pr-workflow-run');
        return require(name);
      };
      const vm = require('node:vm');
      const script = `(async () => { ${observerCollectScript(workflow, stepName)} })()`;
      await vm.runInNewContext(script, {
        require: fakeRequire, github, core,
        context: { repo: { owner: 'honua-io', repo: 'honua-server' }, ref: 'refs/heads/trunk', sha: POLICY },
        process: { env: { RUNNER_TEMP: '/tmp/observer', SOURCE_RUN_ID: '123',
          SOURCE_RUN_ATTEMPT: '2', SOURCE_RUN_CONCLUSION: 'failure' } },
      });
      assert.equal(core.outputs.skip_code, 'source-run-draft-admission');
      assert.deepEqual(Object.keys(files), ['/tmp/observer/observation-skipped.json']);
      const marker = JSON.parse(files['/tmp/observer/observation-skipped.json']);
      assert.equal(marker.source_run_attempt, '2');
      assert.equal(marker.code, 'source-run-draft-admission');
    });
  }
}
