// Unit tests for the shared CI-failure classifier (#2021).
// Run with no install step:  node --test scripts/ci/ci-failure-classifier.test.js
//
// These prove the classifier matches the intent baked into auto-rerun-flaky.yml:
// aggregators are excluded, SOLID gates are real, the known-flaky shards are
// rerun-eligible, and anything unrecognized is treated as real (fail safe).

'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { classifyFailures } = require('./ci-failure-classifier.js');

test('no failures → clean', () => {
  const r = classifyFailures([]);
  assert.equal(r.verdict, 'clean');
});

test('only aggregator roll-ups failed → clean (no leaf to act on)', () => {
  const r = classifyFailures(['CI Gate', 'Test Suite Summary']);
  assert.equal(r.verdict, 'clean');
  assert.deepEqual(r.leafFailed, []);
});

test('flake-only: a single known-flaky shard → flake-only', () => {
  const r = classifyFailures(['Server Tests (shard 3)', 'CI Gate']);
  assert.equal(r.verdict, 'flake-only');
  assert.deepEqual(r.flakyFailed, ['Server Tests (shard 3)']);
});

test('flake-only: multiple known-flaky shards + aggregator → flake-only', () => {
  const r = classifyFailures([
    'OGC API Maps and Tiles',
    'Postgres Compatibility',
    'Integration Tests',
    'CI Gate',
  ]);
  assert.equal(r.verdict, 'flake-only');
  assert.equal(r.flakyFailed.length, 3);
});

test('real-failure: a SOLID gate (Build & Format) failed → real-failure', () => {
  const r = classifyFailures(['Build & Format', 'CI Gate']);
  assert.equal(r.verdict, 'real-failure');
  assert.deepEqual(r.solidFailed, ['Build & Format']);
});

test('real-failure: AOT gate failed → real-failure', () => {
  const r = classifyFailures(['AOT Compatibility Verify']);
  assert.equal(r.verdict, 'real-failure');
});

test('mixed SOLID + FLAKY → real-failure (the SOLID gate wins; no free rerun)', () => {
  const r = classifyFailures(['Analyze', 'Server Tests (shard 1)', 'CI Gate']);
  assert.equal(r.verdict, 'real-failure');
  assert.deepEqual(r.solidFailed, ['Analyze']);
  assert.equal(r.flakyFailed.length, 1);
});

test('unknown leaf job (not SOLID, not FLAKY) → real-failure (fail safe)', () => {
  const r = classifyFailures(['Some New Job Nobody Mapped', 'CI Gate']);
  assert.equal(r.verdict, 'real-failure');
  assert.deepEqual(r.unknownFailed, ['Some New Job Nobody Mapped']);
});

test('Format aggregator-vs-leaf: PR Template is SOLID, not a flake', () => {
  const r = classifyFailures(['Validate PR Template Compliance']);
  assert.equal(r.verdict, 'real-failure');
});

test('CodeQL is a SOLID gate', () => {
  const r = classifyFailures(['CodeQL']);
  assert.equal(r.verdict, 'real-failure');
});

test('Docker Build shard is flaky-eligible', () => {
  const r = classifyFailures(['Docker Build']);
  assert.equal(r.verdict, 'flake-only');
});

test('garbage/empty entries are ignored', () => {
  const r = classifyFailures(['', null, undefined, 'CI Gate']);
  assert.equal(r.verdict, 'clean');
});
