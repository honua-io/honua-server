'use strict';

const SHA = /^[0-9a-f]{40}$/;
const TERMINAL_CONCLUSIONS = new Set([
  'success',
  'failure',
  'cancelled',
  'timed_out',
  'action_required',
  'neutral',
  'skipped',
  'stale',
  'startup_failure',
]);
const WORKFLOW_SHA_ROLES = new Set([
  'pull-request-head',
  'pull-request-target-associated',
]);

/**
 * The source run itself was superseded: it is cancelled, its head no longer
 * matches the pull-request association, the association is gone, or the pull
 * request moved or closed after the run. This is the deliberate fail-closed
 * outcome of the trusted resolver (#3226) for an ordinary force-push, and it is
 * the ONLY class an observation-only consumer may downgrade to a skip.
 *
 * Misconfiguration and API drift (wrong workflow path/name/event, a fork head,
 * a dispatch-input run_attempt/conclusion mismatch, an ambiguous job or pull
 * request, an inconsistent check-run shape, a cross-repository association)
 * stay plain `Error`s under every behavior. Silencing those would hide a broken
 * observer as if the source had merely moved on.
 *
 * `code` is a bounded, filename-safe token so a consumer can report WHICH
 * superseded shape it saw without transporting free text.
 */
class UnresolvedTrustedWorkflowRunError extends Error {
  constructor(code, reason) {
    super(reason);
    this.name = 'UnresolvedTrustedWorkflowRunError';
    this.code = code;
    this.reason = reason;
  }
}

const SKIP_CODE = /^[a-z][a-z0-9-]{0,47}$/;

function parsePositiveSafeInteger(value, label) {
  const text = String(value ?? '');
  if (!/^[1-9][0-9]*$/.test(text)) throw new Error(`invalid ${label}`);
  const number = Number(text);
  if (!Number.isSafeInteger(number)) throw new Error(`unsafe ${label}`);
  return number;
}

function parseRunId(value) {
  return parsePositiveSafeInteger(value, 'workflow run id');
}

async function resolveOrThrow({
  github,
  owner,
  repo,
  runId,
  runAttempt,
  runConclusion,
  workflowPath,
  workflowName,
  workflowEvent = 'pull_request',
  workflowShaRole = 'pull-request-head',
  jobName = workflowName,
  jobConclusion = runConclusion,
  defaultBranch,
  repositoryId,
}) {
  if (!github || !owner || !repo || !workflowPath || !workflowName ||
      !workflowEvent || !WORKFLOW_SHA_ROLES.has(workflowShaRole) ||
      (workflowEvent === 'pull_request_target') !==
        (workflowShaRole === 'pull-request-target-associated') ||
      !jobName || !defaultBranch ||
      !Number.isSafeInteger(repositoryId) || repositoryId <= 0) {
    throw new Error('trusted workflow-run resolver input is incomplete');
  }
  const repository = `${owner}/${repo}`;
  const id = parseRunId(runId);
  const expectedAttempt = parsePositiveSafeInteger(runAttempt, 'workflow run attempt');
  if (typeof runConclusion !== 'string' || !TERMINAL_CONCLUSIONS.has(runConclusion)) {
    throw new Error('invalid workflow run conclusion');
  }
  if (typeof jobConclusion !== 'string' || !TERMINAL_CONCLUSIONS.has(jobConclusion)) {
    throw new Error('invalid workflow job conclusion');
  }
  const { data: run } = await github.rest.actions.getWorkflowRun({
    owner,
    repo,
    run_id: id,
  });
  if (
    run?.id !== id ||
    run.path !== workflowPath ||
    run.name !== workflowName ||
    run.event !== workflowEvent ||
    run.status !== 'completed' ||
    !TERMINAL_CONCLUSIONS.has(run.conclusion) ||
    run.repository?.full_name !== repository ||
    run.repository?.id !== repositoryId ||
    run.head_repository?.full_name !== repository ||
    run.head_repository?.id !== repositoryId ||
    !SHA.test(run.head_sha || '') ||
    run.run_attempt !== expectedAttempt ||
    run.conclusion !== runConclusion
  ) {
    // Path/name/event/repository drift and a dispatch-input attempt or
    // conclusion mismatch are misconfiguration, never a superseded source.
    throw new Error('source run is not a completed canonical pull-request workflow');
  }

  // workflow_run.pull_requests is routinely empty, especially for forks. The
  // GitHub-managed job check run retains the immutable event-time PR base/head
  // association. Never reconstruct the gate base from the mutable current PR.
  const jobs = await github.paginate(
    github.rest.actions.listJobsForWorkflowRun,
    { owner, repo, run_id: id, filter: 'latest', per_page: 100 },
  );
  const canonicalJobs = jobs.filter((job) =>
    job.run_id === id &&
    job.run_attempt === run.run_attempt &&
    job.workflow_name === workflowName &&
    job.name === jobName &&
    job.head_sha === run.head_sha &&
    job.status === 'completed' &&
    job.conclusion === jobConclusion,
  );
  // Zero matches is the superseded shape: a cancelled source never reached the
  // terminal job the caller pinned. More than one is ambiguity or API drift and
  // must never be silenced.
  if (canonicalJobs.length !== 1) {
    const message = 'source run does not identify exactly one canonical workflow job';
    if (canonicalJobs.length === 0) {
      throw new UnresolvedTrustedWorkflowRunError('source-run-job-absent', message);
    }
    throw new Error(message);
  }

  const job = canonicalJobs[0];
  if (!Number.isSafeInteger(job.id) || job.id <= 0) {
    throw new Error('canonical workflow job identity is invalid');
  }
  const { data: checkRun } = await github.rest.checks.get({
    owner,
    repo,
    check_run_id: job.id,
  });
  if (
    checkRun?.id !== job.id ||
    checkRun.name !== jobName ||
    checkRun.status !== 'completed' ||
    checkRun.conclusion !== job.conclusion ||
    checkRun.head_sha !== run.head_sha
  ) {
    throw new Error('canonical workflow job check identity is inconsistent');
  }

  const associations = checkRun.pull_requests || [];
  if (associations.length !== 1 ||
      !Number.isSafeInteger(associations[0]?.number) || associations[0].number <= 0) {
    // A cancelled check run routinely retains no association at all; two
    // associations is ambiguity that must stay loud.
    const message = 'canonical workflow check does not identify exactly one pull request';
    if (associations.length === 0) {
      throw new UnresolvedTrustedWorkflowRunError('no-pull-request-association', message);
    }
    throw new Error(message);
  }
  const associated = associations[0];
  const associatedBase = associated.base?.sha;
  const associatedHead = associated.head?.sha;
  // Never infer the candidate from run.head_sha. GitHub currently reports the
  // associated head for pull_request_target checks, but it has also exposed
  // the event-time base commit in this position. Both representations are
  // authenticated by the unique GitHub-managed PR association; the candidate
  // always comes from associated.head.sha below.
  const resolvedWorkflowShaRole = run.head_sha === associatedHead
    ? 'association-head'
    : run.head_sha === associatedBase
      ? 'association-base'
      : null;
  const identityMessage = 'canonical workflow check pull-request identity is inconsistent';
  // Cross-repository or malformed association identity is drift, not a
  // superseded push, and is never skippable.
  if (
    associated.base?.repo?.id !== repositoryId ||
    associated.head?.repo?.id !== repositoryId ||
    !SHA.test(associatedBase || '') ||
    !SHA.test(associatedHead || '')
  ) {
    throw new Error(identityMessage);
  }
  // A retargeted base or a run head outside both associated commits means the
  // observed source no longer describes this pull request.
  if (
    associated.base?.ref !== defaultBranch ||
    (workflowShaRole === 'pull-request-head'
      ? run.head_sha !== associatedHead
      : resolvedWorkflowShaRole === null)
  ) {
    throw new UnresolvedTrustedWorkflowRunError(
      'pull-request-identity-superseded', identityMessage);
  }

  const { data: pullRequest } = await github.rest.pulls.get({
    owner,
    repo,
    pull_number: associated.number,
  });
  const movedMessage = 'pull request moved after the canonical workflow run';
  // A cross-repository or mismatched-number pull request is drift.
  if (
    pullRequest?.number !== associated.number ||
    pullRequest.base?.repo?.full_name !== repository ||
    pullRequest.base?.repo?.id !== repositoryId ||
    pullRequest.head?.repo?.full_name !== repository ||
    pullRequest.head?.repo?.id !== repositoryId
  ) {
    throw new Error(movedMessage);
  }
  // Closed, retargeted, or advanced since the run: ordinary supersession.
  if (
    pullRequest?.state !== 'open' ||
    pullRequest.base?.ref !== defaultBranch ||
    pullRequest.base?.sha !== associatedBase ||
    pullRequest.head?.sha !== associatedHead
  ) {
    throw new UnresolvedTrustedWorkflowRunError('pull-request-moved', movedMessage);
  }

  return {
    skipped: false,
    run,
    job,
    checkRun,
    pullRequest,
    pullRequestNumber: associated.number,
    baseSha: associatedBase,
    headSha: associatedHead,
    workflowSha: run.head_sha,
    workflowShaRole: workflowShaRole === 'pull-request-head'
      ? 'association-head'
      : resolvedWorkflowShaRole,
  };
}

/**
 * Resolve the still-current pull request behind a completed canonical workflow
 * run, failing closed. Trusted callers use this directly.
 *
 * `unresolved: 'skip'` downgrades ONLY UnresolvedTrustedWorkflowRunError (the
 * superseded-source class) to `{ skipped: true, code, reason }`. Every other
 * failure still throws, so a misconfigured observer stays loud.
 */
async function resolveTrustedPullRequestWorkflowRun(options) {
  const behavior = options?.unresolved ?? 'throw';
  if (behavior !== 'throw' && behavior !== 'skip') {
    throw new Error('invalid unresolved-source behavior');
  }
  try {
    return await resolveOrThrow(options);
  } catch (error) {
    if (behavior === 'skip' && error instanceof UnresolvedTrustedWorkflowRunError) {
      return { skipped: true, code: error.code, reason: error.reason };
    }
    throw error;
  }
}

/**
 * The single entrypoint every observation-only consumer uses, so no workflow
 * repeats skip plumbing inline.
 *
 * A superseded source is recorded once through recordObservationSkip and
 * returns null, so the caller only has to `if (!resolved) return;`.
 */
async function resolveForObservation({ core, label, markerPath, fs, ...options }) {
  if (!core || !label) throw new Error('observation resolver requires core and a label');
  const resolved = await resolveTrustedPullRequestWorkflowRun({ ...options, unresolved: 'skip' });
  if (!resolved.skipped) {
    core.setOutput('skip', 'false');
    return resolved;
  }
  recordObservationSkip({
    core,
    label,
    markerPath,
    fs,
    code: SKIP_CODE.test(resolved.code || '') ? resolved.code : 'source-unresolved',
    reason: resolved.reason,
    source: options,
  });
  return null;
}

/**
 * Record one observation skip: the resolver's superseded-source class above, or
 * an observer-local one (a draft pull request, a candidate that moved mid-run).
 *
 * The marker file is what lets the evidence ledger classify the run as
 * `observation-skipped:<code>` from the artifact catalog alone, instead of
 * lumping it into `observation-receipt-not-emitted`, which is the only signal
 * for a real receipt-emission regression.
 */
function recordObservationSkip({ core, label, markerPath, fs, code, reason, source = {} }) {
  if (!core || !label || !SKIP_CODE.test(code || '')) {
    throw new Error('observation skip requires core, a label, and a bounded code');
  }
  core.notice(`Skipping ${label}: ${reason} (${code}).`);
  core.setOutput('skip', 'true');
  core.setOutput('skip_code', code);
  core.setOutput('skip_reason', reason);
  if (markerPath && fs) {
    fs.writeFileSync(markerPath, `${JSON.stringify({
      contract: 'honua.ci.observation-skipped/v1',
      observer: label,
      code,
      reason,
      source_run_id: String(source.runId ?? ''),
      source_run_attempt: String(source.runAttempt ?? ''),
      source_run_conclusion: String(source.runConclusion ?? ''),
    }, null, 2)}\n`);
  }
}

module.exports = {
  SKIP_CODE,
  TERMINAL_CONCLUSIONS,
  UnresolvedTrustedWorkflowRunError,
  WORKFLOW_SHA_ROLES,
  parseRunId,
  recordObservationSkip,
  resolveForObservation,
  resolveTrustedPullRequestWorkflowRun,
};
