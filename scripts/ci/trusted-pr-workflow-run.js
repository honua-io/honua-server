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

async function resolveTrustedPullRequestWorkflowRun({
  github,
  owner,
  repo,
  runId,
  runAttempt,
  runConclusion,
  workflowPath,
  workflowName,
  workflowEvent = 'pull_request',
  jobName = workflowName,
  jobConclusion = runConclusion,
  defaultBranch,
  repositoryId,
}) {
  if (!github || !owner || !repo || !workflowPath || !workflowName ||
      !workflowEvent || !jobName || !defaultBranch ||
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
  if (canonicalJobs.length !== 1) {
    throw new Error('source run does not identify exactly one canonical workflow job');
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
    throw new Error('canonical workflow check does not identify exactly one pull request');
  }
  const associated = associations[0];
  const associatedBase = associated.base?.sha;
  const associatedHead = associated.head?.sha;
  if (
    associated.base?.ref !== defaultBranch ||
    associated.base?.repo?.id !== repositoryId ||
    associated.head?.repo?.id !== repositoryId ||
    !SHA.test(associatedBase || '') ||
    associatedHead !== run.head_sha
  ) {
    throw new Error('canonical workflow check pull-request identity is inconsistent');
  }

  const { data: pullRequest } = await github.rest.pulls.get({
    owner,
    repo,
    pull_number: associated.number,
  });
  if (
    pullRequest?.number !== associated.number ||
    pullRequest?.state !== 'open' ||
    pullRequest.base?.ref !== defaultBranch ||
    pullRequest.base?.repo?.full_name !== repository ||
    pullRequest.base?.repo?.id !== repositoryId ||
    pullRequest.base?.sha !== associatedBase ||
    pullRequest.head?.sha !== associatedHead ||
    pullRequest.head?.repo?.full_name !== repository ||
    pullRequest.head?.repo?.id !== repositoryId
  ) {
    throw new Error('pull request moved after the canonical workflow run');
  }

  return {
    run,
    job,
    checkRun,
    pullRequest,
    pullRequestNumber: associated.number,
    baseSha: associatedBase,
    headSha: associatedHead,
  };
}

module.exports = {
  TERMINAL_CONCLUSIONS,
  parseRunId,
  resolveTrustedPullRequestWorkflowRun,
};
