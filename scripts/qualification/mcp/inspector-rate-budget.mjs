import { CatalogContractFailure } from './catalog-channel-contract.mjs';

const minuteMs = 60_000;
const realClock = { now: () => Date.now(), sleep: ms => new Promise(resolve => setTimeout(resolve, ms)) };

export function inspectorRateBudget({ limit, resetAt, pageSize, rosters }, clock = realClock) {
  if (!Number.isInteger(limit) || limit <= 0 || !Number.isFinite(resetAt) || !Number.isInteger(pageSize) || pageSize <= 0) {
    throw new CatalogContractFailure('invalid-inspector-rate-budget');
  }
  const requestBounds = Object.fromEntries(Object.entries(rosters).map(([view, tools]) => [view,
    // The CLI refreshes discovery per invocation; allow the salvage walk's second
    // page fetch plus initialize, notification, stream, call, app lookup and close.
    2 * (view === 'full' ? Math.ceil(tools.length / pageSize) : 1) + 6,
  ]));
  const capacity = Math.max(...Object.values(requestBounds));
  if (!Number.isFinite(capacity) || capacity > limit / 2) throw new CatalogContractFailure('inspector-rate-budget-too-small');
  // Half the advertised rate leaves room for one invocation's bounded burst.
  const refillPerMs = limit / (2 * minuteMs);
  let available = 0;
  let lastRefill = resetAt * 1000;
  const stats = { limit, resetAt, pageSize, capacity, waits: 0, delayMs: 0 };

  async function pause(ms, deadline) {
    if (clock.now() + ms > deadline) throw new CatalogContractFailure('inspector-budget-exceeded');
    if (ms > 0) {
      stats.waits++;
      stats.delayMs += ms;
      await clock.sleep(ms);
    }
  }

  return {
    stats,
    async beforeInvocation(view, deadline) {
      const required = requestBounds[view];
      if (!required) throw new CatalogContractFailure('unknown-inspector-budget-view');
      await pause(Math.max(0, lastRefill - clock.now()), deadline);
      available = Math.min(capacity, available + Math.max(0, clock.now() - lastRefill) * refillPerMs);
      lastRefill = clock.now();
      const delay = Math.ceil(Math.max(0, required - available) / refillPerMs);
      await pause(delay, deadline);
      available = Math.min(capacity, available + Math.max(0, clock.now() - lastRefill) * refillPerMs) - required;
      lastRefill = clock.now();
    },
  };
}
