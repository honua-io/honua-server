'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { currentPrGate, dispatchTitle, latestBy } = require('./review-catchup');

const source = fs.readFileSync(path.join(__dirname, 'review-catchup.js'), 'utf8');

test('selector contract uses latest-only APIs and carries every bounded exclusion', () => {
  assert.match(source, /getCombinedStatusForRef/);
  assert.match(source, /checks\.listForRef/);
  assert.match(source, /started_at/);
  assert.doesNotMatch(source, /\/commits\/.*\/statuses/);
  for (const exclusion of ['draft', 'hold', 'train:escalated', 'clean exact-head attestation',
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
    { name: 'PR Gate', status: 'completed', conclusion: 'failure', started_at: '2026-08-27T01:00:00Z' },
    { name: 'PR Gate', status: 'completed', conclusion: 'success', started_at: '2026-08-27T02:00:00Z', completed_at: '2026-08-27T02:05:00Z' },
  ]);
  assert.deepEqual(gate, { kind: 'check', at: '2026-08-27T02:05:00Z' });
});

test('the latest failing check is not green even if an older attempt passed', () => {
  const gate = currentPrGate({ statuses: [] }, [
    { name: 'PR Gate', status: 'completed', conclusion: 'success', started_at: '2026-08-27T01:00:00Z' },
    { name: 'PR Gate', status: 'completed', conclusion: 'failure', started_at: '2026-08-27T02:00:00Z' },
  ]);
  assert.equal(gate, null);
});

test('dispatch title is deterministic per PR head', () => {
  assert.equal(dispatchTitle(3541, 'abc123'), 'Claude catch-up #3541 @ abc123');
});
