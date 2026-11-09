import assert from 'node:assert/strict';
import test from 'node:test';
import { inspectorRateBudget } from './inspector-rate-budget.mjs';

function fakeClock() {
  let now = 0;
  return { now: () => now, sleep: async ms => { now += ms; }, advance: ms => { now += ms; } };
}

const rosters = { default: Array(12).fill('tool'), full: Array(143).fill('tool') };

test('every invocation fits unchanged fixed-window limits without dropping calls', async () => {
  const clock = fakeClock();
  const pacing = inspectorRateBudget({ limit: 1000, resetAt: 10, pageSize: 12, rosters }, clock);
  const events = [];
  for (let call = 0; call < 200; call++) {
    const view = call < 50 ? 'default' : 'full';
    await pacing.beforeInvocation(view, 1_000_000);
    events.push({ at: clock.now(), requests: view === 'full' ? 30 : 8 });
    clock.advance(200);
  }
  assert.equal(events.length, 200);
  assert.ok(events[0].at >= 10_000);
  const windows = new Map();
  for (const event of events) {
    const window = Math.floor((event.at - 10_000) / 60_000);
    windows.set(window, (windows.get(window) ?? 0) + event.requests);
  }
  assert.ok([...windows.values()].every(requests => requests <= 1000));
  assert.ok(events.reduce((sum, event) => sum + event.requests, 0) > 1000, 'an unpaced burst must fail the same server limit');
  assert.ok(pacing.stats.waits > 0);
});

test('rejects invalid or insufficient header budgets', () => {
  for (const change of [{ limit: 0 }, { pageSize: 0 }, { resetAt: NaN }]) {
    assert.throws(() => inspectorRateBudget({ limit: 1000, resetAt: 0, pageSize: 12, rosters, ...change }), { message: 'invalid-inspector-rate-budget' });
  }
  assert.throws(() => inspectorRateBudget({ limit: 20, resetAt: 0, pageSize: 12, rosters }), { message: 'inspector-rate-budget-too-small' });
});

test('does not wait beyond the existing deadline or accept an unknown view', async () => {
  const clock = fakeClock();
  const pacing = inspectorRateBudget({ limit: 1000, resetAt: 10, pageSize: 12, rosters }, clock);
  await assert.rejects(pacing.beforeInvocation('full', 5000), { message: 'inspector-budget-exceeded' });
  assert.equal(clock.now(), 0);
  await assert.rejects(pacing.beforeInvocation('unknown', 100_000), { message: 'unknown-inspector-budget-view' });
});
