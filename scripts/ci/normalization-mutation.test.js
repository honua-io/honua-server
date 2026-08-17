'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  ALLOWED_OUTPUT_PATHS,
  NORMALIZATION_COMMIT_TRAILER,
  buildNormalizationCommitMessage,
  planNormalizationMutation,
} = require('./normalization-mutation');

const head = 'a'.repeat(40);
const tree = 'b'.repeat(40);
function change(path = 'docs/gis/data/feature-catalog.json') {
  return { path, before: 'd'.repeat(64), sha256: 'e'.repeat(64) };
}

function input(overrides = {}) {
  return {
    mode: 'enforce',
    credentialPresent: true,
    sameRepository: true,
    pullRequest: { state: 'open', draft: false, headSha: head, headRef: 'feat/example' },
    envelopeSourceSha: head,
    planTreeSha: tree,
    headCommit: { sha: head, treeSha: tree, message: 'feat: example' },
    changes: [change()],
    ...overrides,
  };
}

test('observe mode never mutates', () => {
  const result = planNormalizationMutation(input({ mode: 'observe' }));
  assert.equal(result.action, 'observe');
});

test('an unknown mode never mutates', () => {
  assert.equal(planNormalizationMutation(input({ mode: 'enforce-later' })).action, 'observe');
});

test('a three-file update commits', () => {
  const result = planNormalizationMutation(input({
    changes: ALLOWED_OUTPUT_PATHS.map((path) => change(path)),
  }));
  assert.equal(result.action, 'commit');
  assert.equal(result.changes.length, 3);
});

test('a one-file update commits', () => {
  assert.equal(planNormalizationMutation(input()).action, 'commit');
});

test('an empty delta emits no commit', () => {
  const result = planNormalizationMutation(input({ changes: [] }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'no-delta');
});

test('a fork is read-only even with a delta', () => {
  const result = planNormalizationMutation(input({ sameRepository: false }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'fork-read-only');
});

test('a moved head fails closed', () => {
  const moved = planNormalizationMutation(input({
    pullRequest: { state: 'open', draft: false, headSha: 'f'.repeat(40), headRef: 'feat/example' },
  }));
  assert.equal(moved.action, 'fail');
  assert.equal(moved.reason, 'head-moved');
});

test('a head commit that does not match the envelope fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: { sha: 'f'.repeat(40), treeSha: tree, message: 'feat: example' },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'head-commit-mismatch');
});

test('a tree that does not match the envelope fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: { sha: head, treeSha: 'f'.repeat(40), message: 'feat: example' },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'tree-mismatch');
});

test('a closed or draft pull request fails closed', () => {
  for (const pullRequest of [
    { state: 'closed', draft: false, headSha: head, headRef: 'feat/example' },
    { state: 'open', draft: true, headSha: head, headRef: 'feat/example' },
  ]) {
    const result = planNormalizationMutation(input({ pullRequest }));
    assert.equal(result.action, 'fail');
    assert.equal(result.reason, 'pull-request-not-open');
  }
});

test('an unsafe head ref fails closed', () => {
  for (const headRef of ['', '-x', 'feat/..hidden', 'feat/a b', 'feat/a^', 'refs/heads/a:b']) {
    const result = planNormalizationMutation(input({
      pullRequest: { state: 'open', draft: false, headSha: head, headRef },
    }));
    assert.equal(result.action, 'fail', headRef);
    assert.equal(result.reason, 'unsafe-head-ref', headRef);
  }
});

test('a path outside the allowlist fails closed', () => {
  const result = planNormalizationMutation(input({
    changes: [change('.github/workflows/ci.yml')],
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'path-outside-allowlist');
});

test('a duplicate change path fails closed', () => {
  const result = planNormalizationMutation(input({ changes: [change(), change()] }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'duplicate-change-path');
});

test('a change without a validated digest fails closed', () => {
  const result = planNormalizationMutation(input({
    changes: [{ ...change(), sha256: 'not-a-digest' }],
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'change-missing-digest');
});

test('non-array changes fail closed', () => {
  assert.equal(planNormalizationMutation(input({ changes: null })).action, 'fail');
  assert.equal(planNormalizationMutation(undefined).action, 'fail');
});

test('a replayed normalization commit that still drifts fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: {
      sha: head,
      treeSha: tree,
      message: `chore(ci): normalize governed derived artifacts\n\n${NORMALIZATION_COMMIT_TRAILER} ${'9'.repeat(40)}`,
    },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'non-converging-normalization');
});

test('a converged normalization commit emits no second commit', () => {
  const result = planNormalizationMutation(input({
    changes: [],
    headCommit: {
      sha: head,
      treeSha: tree,
      message: `chore(ci): normalize governed derived artifacts\n\n${NORMALIZATION_COMMIT_TRAILER} ${'9'.repeat(40)}`,
    },
  }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'no-delta');
});

test('enforce without the scoped credential fails closed', () => {
  const result = planNormalizationMutation(input({ credentialPresent: false }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'missing-normalization-credential');
});

test('the commit message binds paths, source SHA, run, and attempt', () => {
  const message = buildNormalizationCommitMessage({
    changes: [change('docs/gis/data/capability-matrix.v1.json'), change()],
    sourceSha: head,
    runId: 42,
    runAttempt: 2,
  });
  assert.match(message, /^chore\(ci\): normalize governed derived artifacts\n/);
  assert.ok(message.includes('- docs/gis/data/capability-matrix.v1.json'));
  assert.ok(message.includes('- docs/gis/data/feature-catalog.json'));
  assert.ok(message.includes(`${NORMALIZATION_COMMIT_TRAILER} ${head}`));
  assert.ok(message.includes('Normalization-Run-Id: 42'));
  assert.ok(message.includes('Normalization-Run-Attempt: 2'));
});

test('a commit message requires a delta and an exact source SHA', () => {
  assert.throws(() => buildNormalizationCommitMessage({
    changes: [], sourceSha: head, runId: 1, runAttempt: 1,
  }));
  assert.throws(() => buildNormalizationCommitMessage({
    changes: [change()], sourceSha: 'nope', runId: 1, runAttempt: 1,
  }));
});
