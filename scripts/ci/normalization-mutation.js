'use strict';

/**
 * Decision and mutation logic for the trusted derived-artifact normalization
 * consumer (#3219).
 *
 * `planNormalizationMutation` is pure: the workflow supplies already-validated
 * envelope/plan data plus the exact-head facts it read through the GitHub API,
 * and this module decides whether a normalization commit is admissible. Every
 * branch fails closed, and Git objects are only written after a `commit`
 * decision.
 *
 * `applyNormalizationMutation` performs the write against an injected Octokit
 * so the ordering, the compare-and-swap, and the post-update verification are
 * testable offline (scripts/ci/normalization-mutation.test.js).
 */

const NORMALIZATION_COMMIT_TRAILER = 'Normalization-Source-Sha:';

// The authoritative allowlist lives in scripts/ci/normalization-envelope.py and
// is enforced by the trusted validator before a plan exists, so the admissible
// paths are taken from the validated plan rather than copied again here. This
// pattern is only a structural backstop against a future plan that widens the
// contract to an executable or out-of-tree location.
const SAFE_OUTPUT_PATH = /^docs\/gis\/data\/[a-z0-9][a-z0-9._-]*\.json$/;

const SHA_PATTERN = /^[0-9a-f]{40}$/;
const DIGEST_PATTERN = /^[0-9a-f]{64}$/;

const REPOSITORY_ID_QUERY = `
  query($owner: String!, $name: String!) {
    repository(owner: $owner, name: $name) { id }
  }
`;

// GraphQL updateRefs is the only GitHub API with a true compare-and-swap:
// `beforeOid` must still be the ref's value when the update is applied. REST
// updateRef with force:false only guarantees a fast-forward from whatever the
// ref happens to be at update time, so a backward force-push inside the window
// would be silently reinstated.
const UPDATE_REFS_MUTATION = `
  mutation($input: UpdateRefsInput!) {
    updateRefs(input: $input) { clientMutationId }
  }
`;

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
 * Read the normalization trailer out of a commit message. Returns the source
 * SHA the commit normalized, or null when the commit carries no trailer.
 */
function parseNormalizationTrailer(message) {
  if (typeof message !== 'string') return null;
  const match = message.match(/^Normalization-Source-Sha:[ \t]*([0-9a-f]{40})[ \t]*$/m);
  return match ? match[1] : null;
}

/**
 * True only for a commit this workflow itself created on top of the SHA named
 * by its own trailer. A squashed, amended, or cherry-picked commit can inherit
 * the trailer text without being a normalization replay, so the trailer alone
 * must never block later pushes.
 */
function isNormalizationReplay(headCommit) {
  const trailerSha = parseNormalizationTrailer((headCommit || {}).message);
  if (trailerSha === null) return false;
  const parents = Array.isArray((headCommit || {}).parents) ? headCommit.parents : [];
  return parents.length === 1 && parents[0] === trailerSha;
}

/**
 * Build the normalization commit message. The subject stays conventional so the
 * merge train's own tooling reads it like any other commit; the trailers bind
 * the commit to the exact source head and producing run and make a replay
 * auditable.
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
    allowedPaths,
  } = input || {};

  if (!Array.isArray(allowedPaths) || allowedPaths.length === 0
    || !allowedPaths.every((path) => typeof path === 'string' && SAFE_OUTPUT_PATH.test(path))) {
    return decision('fail', 'invalid-allowlist');
  }
  if (!Array.isArray(changes)) {
    return decision('fail', 'changes-not-an-array');
  }
  const paths = changes.map((change) => (change || {}).path);
  if (paths.some((path) => !allowedPaths.includes(path))) {
    return decision('fail', 'path-outside-allowlist');
  }
  if (new Set(paths).size !== paths.length) {
    return decision('fail', 'duplicate-change-path');
  }
  if (changes.some((change) => !isDigest((change || {}).sha256))) {
    return decision('fail', 'change-missing-digest');
  }

  if (mode !== 'enforce') {
    return decision('observe', 'observe-mode');
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
    return decision('skip', 'fork-read-only');
  }
  if (changes.length === 0) {
    return decision('skip', 'no-delta');
  }
  if (isNormalizationReplay(headCommit)) {
    // This workflow already normalized this exact parent and the replay still
    // produces a delta, so generation is not converging. Never chain a second
    // commit onto the first.
    return decision('fail', 'non-converging-normalization');
  }
  if (credentialPresent !== true) {
    return decision('fail', 'missing-normalization-credential');
  }

  return decision('commit', 'derived-artifact-drift', { changes });
}

/**
 * Prove the minted credential can read what the mutation depends on before any
 * write is attempted, so a misprovisioned App fails with a legible reason
 * instead of a mid-sequence 403 after blobs already exist.
 */
async function probeNormalizationCredential({ octokit, repo, pullNumber, headRef }) {
  try {
    await octokit.rest.pulls.get({ ...repo, pull_number: pullNumber });
  } catch (error) {
    throw new Error(
      `normalization credential cannot read pull requests (needs Pull requests: read): ${error.message}`);
  }
  try {
    await octokit.rest.git.getRef({ ...repo, ref: `heads/${headRef}` });
  } catch (error) {
    throw new Error(
      `normalization credential cannot read the head ref (needs Contents: read/write): ${error.message}`);
  }
}

/**
 * Write the decided normalization commit and advance the branch with a real
 * compare-and-swap. `contents` maps each changed path to its validated base64
 * payload from the envelope.
 */
async function applyNormalizationMutation({
  octokit,
  repo,
  decision: plan,
  contents,
  headCommit,
  headRef,
  sourceSha,
  runId,
  runAttempt,
  reviewRequestBody,
  pullNumber,
  log,
}) {
  if (!plan || plan.action !== 'commit') {
    throw new Error('applyNormalizationMutation requires a commit decision');
  }
  if (!isExactSha(sourceSha) || headCommit.sha !== sourceSha) {
    throw new Error('applyNormalizationMutation requires the exact envelope source head');
  }
  const info = typeof log === 'function' ? log : () => {};

  const entries = [];
  for (const change of [...plan.changes].sort((left, right) => (left.path < right.path ? -1 : 1))) {
    const content = contents.get(change.path);
    if (typeof content !== 'string' || content.length === 0) {
      throw new Error(`${change.path} has no validated envelope payload`);
    }
    const blob = await octokit.rest.git.createBlob({ ...repo, content, encoding: 'base64' });
    entries.push({ path: change.path, mode: '100644', type: 'blob', sha: blob.data.sha });
  }

  const tree = await octokit.rest.git.createTree({
    ...repo, base_tree: headCommit.treeSha, tree: entries,
  });
  const commit = await octokit.rest.git.createCommit({
    ...repo,
    message: buildNormalizationCommitMessage({
      changes: plan.changes, sourceSha, runId, runAttempt,
    }),
    tree: tree.data.sha,
    parents: [sourceSha],
  });

  const repository = await octokit.graphql(REPOSITORY_ID_QUERY, {
    owner: repo.owner, name: repo.repo,
  });
  const repositoryId = repository && repository.repository && repository.repository.id;
  if (!repositoryId) {
    throw new Error('could not resolve the repository node id for a compare-and-swap update');
  }
  try {
    await octokit.graphql(UPDATE_REFS_MUTATION, {
      input: {
        repositoryId,
        refUpdates: [{
          name: `refs/heads/${headRef}`,
          afterOid: commit.data.sha,
          beforeOid: sourceSha,
          force: false,
        }],
      },
    });
  } catch (error) {
    throw new Error(
      `compare-and-swap ref update refused (head moved or update rejected): ${error.message}`);
  }

  // Read back: a CAS that silently did not apply, or a concurrent writer, must
  // never be reported as a successful normalization.
  const updated = await octokit.rest.git.getRef({ ...repo, ref: `heads/${headRef}` });
  if (updated.data.object.sha !== commit.data.sha) {
    throw new Error(
      `normalization ref verification failed: ${headRef} is ${updated.data.object.sha}, expected ${commit.data.sha}`);
  }
  info(`normalized ${headRef}: ${sourceSha} -> ${commit.data.sha}`);

  let reviewRequested = false;
  if (reviewRequestBody) {
    // The commit moved the head, so exact-head review evidence is void. Ask for
    // it again immediately; a failure here needs a human, not a silent pass.
    await octokit.rest.issues.createComment({
      ...repo, issue_number: pullNumber, body: reviewRequestBody,
    });
    reviewRequested = true;
  }

  return {
    commitSha: commit.data.sha,
    treeSha: tree.data.sha,
    paths: entries.map((entry) => entry.path),
    reviewRequested,
  };
}

module.exports = {
  NORMALIZATION_COMMIT_TRAILER,
  SAFE_OUTPUT_PATH,
  applyNormalizationMutation,
  buildNormalizationCommitMessage,
  isNormalizationReplay,
  parseNormalizationTrailer,
  planNormalizationMutation,
  probeNormalizationCredential,
};
