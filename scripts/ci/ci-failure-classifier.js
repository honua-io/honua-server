// Shared CI-failure classifier — single source of truth for the leaf-vs-aggregator
// and FLAKY-shard-vs-SOLID-gate distinction used by BOTH the auto-rerun-flaky
// workflow and the event-driven ci-failure-triage workflow (#2021).
//
// Why this exists: auto-rerun-flaky.yml (#1967) and ci-failure-triage.yml must
// agree, byte-for-byte, on what counts as a "real" failure vs a known flake. If
// the two carried independent copies of these regex sets they would drift, and a
// shard reclassified in one place but not the other would either get auto-rerun
// forever (storm) or get AI-triaged when it is a known flake (noise). Keeping the
// regexes here — consumed by both — makes that class of bug impossible.
//
// Pure, dependency-free CommonJS so it loads inside actions/github-script (which
// can `require()` a workspace file after actions/checkout) and runs under plain
// `node` in unit tests with no install step (honoring the build-lock rules: no
// npm install needed).

'use strict';

// Aggregator roll-ups, NOT independent signals: these fail whenever ANY
// downstream job fails, so they carry no information about real-vs-flake.
// They must be excluded before classification — otherwise a pure shard flake
// that trips `CI Gate` (which it always does) would look like a deterministic-
// gate failure and never get its retry. Classify only the LEAF jobs.
const AGGREGATORS = [/CI Gate/i, /Test Suite Summary/i];

// Deterministic gates — a failure in a LEAF gate is real, never auto-rerun.
// (CI Gate / Test Suite Summary are aggregators, handled above — not here.)
const SOLID = [
  /Build & Format/i, /\bFormat\b/i, /Analyze/i, /CodeQL/i,
  /PR Template/i, /Router/i, /Mergeability/i,
  /AOT/i, /Package Compatibility/i, /Type Check/i,
];

// Known-flaky integration/infra shards — transient (Postgres 40P01 deadlock,
// Testcontainers/Docker hiccups), eligible for ONE retry.
const FLAKY = [
  /Server Tests/i, /OGC API/i, /Postgres Compatibility/i,
  /Docker Build/i, /Integration Tests/i, /Browser/i, /MCP/i,
];

/**
 * Classify a CI run's failed job names into a coordination verdict.
 *
 * @param {string[]} failedJobNames - names of jobs whose conclusion === 'failure'.
 * @returns {{
 *   verdict: 'clean' | 'flake-only' | 'real-failure',
 *   leafFailed: string[],
 *   solidFailed: string[],
 *   flakyFailed: string[],
 *   unknownFailed: string[],
 *   reason: string,
 * }}
 *
 * Verdicts:
 *   'clean'        — no failed leaf jobs (only aggregator roll-ups, or nothing).
 *   'flake-only'   — every failed leaf is in the known-flaky set; safe to rerun once.
 *   'real-failure' — a SOLID gate failed, OR a leaf failed that is not in the
 *                    known-flaky set (unknown). Either way: do NOT silently rerun;
 *                    hand to AI triage.
 */
function classifyFailures(failedJobNames) {
  const failed = (failedJobNames || []).filter(n => typeof n === 'string' && n.length > 0);

  const leafFailed = failed.filter(n => !AGGREGATORS.some(r => r.test(n)));
  if (leafFailed.length === 0) {
    return {
      verdict: 'clean',
      leafFailed: [],
      solidFailed: [],
      flakyFailed: [],
      unknownFailed: [],
      reason: failed.length === 0
        ? 'No failed jobs.'
        : `Only aggregator roll-ups failed (${failed.join(', ')}); no leaf job.`,
    };
  }

  const solidFailed = leafFailed.filter(n => SOLID.some(r => r.test(n)));
  const flakyFailed = leafFailed.filter(n => FLAKY.some(r => r.test(n)) && !SOLID.some(r => r.test(n)));
  const unknownFailed = leafFailed.filter(
    n => !SOLID.some(r => r.test(n)) && !FLAKY.some(r => r.test(n)),
  );

  if (solidFailed.length > 0) {
    return {
      verdict: 'real-failure',
      leafFailed, solidFailed, flakyFailed, unknownFailed,
      reason: `Deterministic leaf gate failed (${solidFailed.join(', ')}).`,
    };
  }
  if (unknownFailed.length > 0) {
    return {
      verdict: 'real-failure',
      leafFailed, solidFailed, flakyFailed, unknownFailed,
      reason: `Leaf failure(s) not in the known-flaky set (${unknownFailed.join(', ')}).`,
    };
  }
  return {
    verdict: 'flake-only',
    leafFailed, solidFailed, flakyFailed, unknownFailed,
    reason: `All leaf failures are known-flaky shards (${flakyFailed.join(', ')}).`,
  };
}

module.exports = { classifyFailures, AGGREGATORS, SOLID, FLAKY };
