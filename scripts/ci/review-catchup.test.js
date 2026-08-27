'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { CANONICAL_GATE_APP_ID, currentPrGate, dispatchTitle, latestBy } = require('./review-catchup');

const gateApp = { id: CANONICAL_GATE_APP_ID };

const source = fs.readFileSync(path.join(__dirname, 'review-catchup.js'), 'utf8');

test('selector contract uses latest-only APIs and carries every bounded exclusion', () => {
  assert.match(source, /getCombinedStatusForRef/);
  assert.match(source, /checks\.listForRef/);
  assert.match(source, /started_at/);
  assert.doesNotMatch(source, /\/commits\/.*\/statuses/);
  for (const exclusion of ['draft', 'hold', 'train:hold', 'train:escalated', 'clean exact-head attestation',
    'attested-and-superseded', 'already triggered for this head']) {
    assert.ok(source.includes(exclusion), `missing catch-up exclusion: ${exclusion}`);
  }
  assert.equal(require('./review-catchup').DEFAULT_LIMIT, 3);
});

test('check runs are reduced by name to latest started_at', () => {
  const latest = latestBy([
    { name: 'PR Gate', started_at: '2026-08-27T01:00:00Z', conclusion: 'failure' },
    { name: 'PR Gate', started_at: '2026-08-27T02:00:00Z', conclusion: 'success' },
  ], item => item.name, item => item.started_at);
  assert.equal(latest.get('PR Gate').conclusion, 'success');
});

test('an old failed check attempt does not override the latest green attempt', () => {
  const gate = currentPrGate({ statuses: [] }, [
    { name: 'PR Gate', app: gateApp, status: 'completed', conclusion: 'failure', started_at: '2026-08-27T01:00:00Z' },
    { name: 'PR Gate', app: gateApp, status: 'completed', conclusion: 'success', started_at: '2026-08-27T02:00:00Z', completed_at: '2026-08-27T02:05:00Z' },
  ]);
  assert.deepEqual(gate, { kind: 'check', at: '2026-08-27T02:05:00Z' });
});

test('the latest failing check is not green even if an older attempt passed', () => {
  const gate = currentPrGate({ statuses: [] }, [
    { name: 'PR Gate', app: gateApp, status: 'completed', conclusion: 'success', started_at: '2026-08-27T01:00:00Z' },
    { name: 'PR Gate', app: gateApp, status: 'completed', conclusion: 'failure', started_at: '2026-08-27T02:00:00Z' },
  ]);
  assert.equal(gate, null);
});

test('dispatch title is deterministic per PR head', () => {
  assert.equal(dispatchTitle(3541, 'abc123'), 'Claude catch-up #3541 @ abc123');
});

test('a foreign producer publishing the PR Gate context is not accepted as green', () => {
  // `PR Gate` is a context string any app can publish. Accepting it by name let a
  // success from another producer mask a failing canonical gate, and the catch-up
  // would spend a paid review on a head that is not green.
  const foreign = currentPrGate(
    { statuses: [{ context: 'PR Gate', state: 'success', app: { id: 999 }, updated_at: '2026-08-27T02:00:00Z' }] },
    [{ name: 'PR Gate', app: gateApp, status: 'completed', conclusion: 'failure', started_at: '2026-08-27T02:30:00Z' }],
  );
  assert.equal(foreign, null);
});

test('a status with no app metadata fails closed rather than counting as green', () => {
  const unattributable = currentPrGate(
    { statuses: [{ context: 'PR Gate', state: 'success', updated_at: '2026-08-27T02:00:00Z' }] },
    [],
  );
  assert.equal(unattributable, null);
});

test('the canonical producer is still accepted from either source', () => {
  const viaStatus = currentPrGate(
    { statuses: [{ context: 'PR Gate', state: 'success', app: gateApp, updated_at: '2026-08-27T02:00:00Z' }] },
    [],
  );
  assert.deepEqual(viaStatus, { kind: 'status', at: '2026-08-27T02:00:00Z' });
});

test('the scheduler identity is allowed to start dispatched reviews', () => {
  // Catch-up dispatches claude-review.yml with GITHUB_TOKEN, so the run is
  // initiated by github-actions[bot]. If the action does not allow that identity
  // it refuses every dispatched run as a non-human actor and the lane reviews
  // nothing while reporting success.
  const workflow = fs.readFileSync(
    path.join(__dirname, '..', '..', '.github', 'workflows', 'claude-review.yml'), 'utf8');
  const allowed = workflow.match(/allowed_bots:\s*'([^']*)'/);
  assert.ok(allowed, 'claude-review.yml must configure allowed_bots');
  assert.ok(allowed[1].split(',').map(s => s.trim()).includes('github-actions[bot]'),
    'the scheduled catch-up dispatch identity must be allowed to start a review');
});
