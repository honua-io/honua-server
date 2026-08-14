'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateReviewFirstDispatch } = require('./review-first-dispatch');

const head = 'a'.repeat(40);
const prNumber = 3216;

function run(overrides = {}) {
  return {
    id: 100,
    name: 'PR Gate',
    path: '.github/workflows/pr-gate.yml',
    event: 'pull_request',
    head_sha: head,
    status: 'completed',
    conclusion: 'failure',
    run_attempt: 1,
    created_at: '2026-08-14T00:00:00Z',
    pull_requests: [{ number: prNumber }],
    ...overrides,
  };
}

function job(overrides = {}) {
  return {
    name: 'PR Gate',
    conclusion: 'failure',
    steps: [
      { name: 'Admission receipt', conclusion: 'success' },
      { name: 'Await exact-head review', conclusion: 'failure' },
      { name: 'Free disk space', conclusion: 'skipped' },
      { name: 'Setup .NET', conclusion: 'skipped' },
      { name: 'Lean gate (build + format + fast unit/architecture smoke)', conclusion: 'skipped' },
      { name: 'Test serving-image boundary detector', conclusion: 'skipped' },
    ],
    ...overrides,
  };
}

function evaluate(overrides = {}) {
  return evaluateReviewFirstDispatch({
    mode: 'enforce',
    reviewReady: true,
    runs: [run()],
    jobs: [job()],
    prNumber,
    head,
    ...overrides,
  });
}

test('releases exactly one completed fail-closed admission attempt', () => {
  assert.deepEqual(evaluate(), {
    action: 'rerun',
    reason: 'exact-head review released the completed admission run',
    runId: 100,
  });
});

test('waits when review finishes before admission completes', () => {
  assert.equal(evaluate({ runs: [run({ status: 'in_progress', conclusion: null })] }).action, 'wait');
});

test('repeated review events do not create attempt three', () => {
  assert.equal(evaluate({
    runs: [run({ run_attempt: 2, conclusion: 'success' })],
    jobs: [job({ conclusion: 'success' })],
  }).action, 'noop');
});

test('admission failure without a receipt is never promoted', () => {
  const failedJob = job({ steps: [{ name: 'Verify admission policy', conclusion: 'failure' }] });
  assert.match(evaluate({ jobs: [failedJob] }).reason, /admission receipt/);
  assert.equal(evaluate({ jobs: [failedJob] }).action, 'block');
});

test('expensive attempt-one work fails closed', () => {
  const badJob = job();
  badJob.steps.find(step => step.name === 'Setup .NET').conclusion = 'success';
  assert.equal(evaluate({ jobs: [badJob] }).action, 'block');
});

test('a reopened PR selects the newest canonical run for the unchanged head', () => {
  const decision = evaluate({
    runs: [
      run({ id: 100, run_attempt: 2, conclusion: 'success' }),
      run({ id: 101, created_at: '2026-08-14T01:00:00Z' }),
    ],
  });
  assert.equal(decision.action, 'rerun');
  assert.equal(decision.runId, 101);
});

test('a moved head cannot release old admission evidence', () => {
  assert.equal(evaluate({ runs: [run({ head_sha: 'b'.repeat(40) })] }).action, 'wait');
});

test('fork run is accepted only with one independently-associated PR', () => {
  const forkRun = run({ pull_requests: [] });
  assert.equal(evaluate({
    runs: [forkRun], associatedPullNumbers: [prNumber],
  }).action, 'rerun');
  assert.equal(evaluate({
    runs: [forkRun], associatedPullNumbers: [prNumber, 4000],
  }).action, 'block');
});

test('malformed same-head run metadata fails closed', () => {
  assert.equal(evaluate({ runs: [run({ created_at: null })] }).action, 'block');
});

test('truncated review evidence cannot release verification', () => {
  assert.equal(evaluate({ snapshotTruncated: true }).action, 'block');
});

test('observe mode records the decision without a rerun', () => {
  assert.equal(evaluate({
    mode: 'observe',
    runs: [run({ conclusion: 'success' })],
    jobs: [job({ conclusion: 'success' })],
  }).action, 'observe');
});

test('review evidence remains a mandatory defense in depth', () => {
  assert.equal(evaluate({ reviewReady: false }).action, 'noop');
});
