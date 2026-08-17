'use strict';

/**
 * Pure decision logic for the trusted derived-artifact normalization consumer
 * (#3219). It never performs I/O: the workflow supplies already-validated
 * envelope/plan data plus the exact-head facts it read through the GitHub API,
 * and this module decides whether a normalization commit is admissible.
 *
 * Every branch fails closed. The caller may only mutate a branch for the
 * `commit` action, and only with the blobs listed in `changes`.
 */

const NORMALIZATION_COMMIT_TRAILER = 'Normalization-Source-Sha:';

const ALLOWED_OUTPUT_PATHS = Object.freeze([
  'docs/gis/data/feature-catalog.json',
  'docs/gis/data/geoservices-rest-parity.json',
  'docs/gis/data/capability-matrix.v1.json',
]);

const SHA_PATTERN = /^[0-9a-f]{40}$/;
const DIGEST_PATTERN = /^[0-9a-f]{64}$/;

function decision(action, reason, extra = {}) {
  return { action, reason, ...extra };
}

function isExactSha(value) {
  return typeof value === 'string' && SHA_PATTERN.test(value);
}

function isDigest(value) {
  return typeof value === 'string' && DIGEST_PATTERN.test(value);
}

/**
 * Build the normalization commit message. The subject stays conventional so the
 * merge train's own tooling reads it like any other commit; the trailers bind
 * the commit to the exact source head and producing run and act as the
 * auditable loop marker (tree equality remains the primary loop guard).
 */
function buildNormalizationCommitMessage({ changes, sourceSha, runId, runAttempt }) {
  if (!Array.isArray(changes) || changes.length === 0) {
    throw new Error('a normalization commit message requires at least one change');
  }
  if (!isExactSha(sourceSha)) {
    throw new Error('a normalization commit message requires the exact source SHA');
  }
  const paths = changes.map((change) => change.path).sort();
  return [
    'chore(ci): normalize governed derived artifacts',
    '',
    ...paths.map((path) => `- ${path}`),
    '',
    `${NORMALIZATION_COMMIT_TRAILER} ${sourceSha}`,
    `Normalization-Run-Id: ${runId}`,
    `Normalization-Run-Attempt: ${runAttempt}`,
  ].join('\n');
}

/**
 * Decide whether the validated normalization plan may advance the pull-request
 * head.
 *
 * Actions:
 * - `observe`: mode is not `enforce`; record the candidate only.
 * - `skip`: enforcement is on but nothing may be written (fork, empty delta).
 * - `commit`: the caller may create the listed blobs and advance the ref.
 * - `fail`: the observation is inadmissible; the caller must fail the run.
 */
function planNormalizationMutation(input) {
  const {
    mode,
    credentialPresent,
    sameRepository,
    pullRequest,
    envelopeSourceSha,
    planTreeSha,
    headCommit,
    changes,
  } = input || {};

  if (!Array.isArray(changes)) {
    return decision('fail', 'changes-not-an-array');
  }
  const paths = changes.map((change) => (change || {}).path);
  if (paths.some((path) => !ALLOWED_OUTPUT_PATHS.includes(path))) {
    return decision('fail', 'path-outside-allowlist');
  }
  if (new Set(paths).size !== paths.length) {
    return decision('fail', 'duplicate-change-path');
  }
  if (changes.some((change) => !isDigest((change || {}).sha256))) {
    return decision('fail', 'change-missing-digest');
  }

  if (mode !== 'enforce') {
    return decision('observe', 'observe-mode', { changes });
  }

  if (!pullRequest || pullRequest.state !== 'open' || pullRequest.draft === true) {
    return decision('fail', 'pull-request-not-open');
  }
  if (!isExactSha(envelopeSourceSha) || !isExactSha(pullRequest.headSha)
    || pullRequest.headSha !== envelopeSourceSha) {
    return decision('fail', 'head-moved');
  }
  if (!headCommit || headCommit.sha !== envelopeSourceSha) {
    return decision('fail', 'head-commit-mismatch');
  }
  if (!isExactSha(planTreeSha) || headCommit.treeSha !== planTreeSha) {
    return decision('fail', 'tree-mismatch');
  }
  if (typeof pullRequest.headRef !== 'string' || pullRequest.headRef.length === 0
    || pullRequest.headRef.startsWith('-') || pullRequest.headRef.includes('..')
    || /[\s~^:?*[\\]/.test(pullRequest.headRef)) {
    return decision('fail', 'unsafe-head-ref');
  }

  if (sameRepository !== true) {
    return decision('skip', 'fork-read-only', { changes });
  }
  if (changes.length === 0) {
    return decision('skip', 'no-delta');
  }
  if (typeof headCommit.message === 'string'
    && headCommit.message.includes(NORMALIZATION_COMMIT_TRAILER)) {
    // A normalization commit that still produces a delta means generation is
    // not converging. Never chain a second commit onto the first.
    return decision('fail', 'non-converging-normalization', { changes });
  }
  if (credentialPresent !== true) {
    return decision('fail', 'missing-normalization-credential', { changes });
  }

  return decision('commit', 'derived-artifact-drift', { changes });
}

module.exports = {
  ALLOWED_OUTPUT_PATHS,
  NORMALIZATION_COMMIT_TRAILER,
  buildNormalizationCommitMessage,
  planNormalizationMutation,
};
