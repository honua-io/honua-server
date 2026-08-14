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

function parseRunId(value) {
  const text = String(value ?? '');
  if (!/^[1-9][0-9]*$/.test(text)) throw new Error('invalid workflow run id');
  const number = Number(text);
  if (!Number.isSafeInteger(number)) throw new Error('unsafe workflow run id');
  return number;
}

async function resolveTrustedPullRequestWorkflowRun({
  github,
  owner,
  repo,
  runId,
  workflowPath,
  workflowName,
  defaultBranch,
  repositoryId,
}) {
  if (!github || !owner || !repo || !workflowPath || !workflowName ||
      !defaultBranch || !Number.isInteger(repositoryId)) {
    throw new Error('trusted workflow-run resolver input is incomplete');
  }
  const repository = `${owner}/${repo}`;
  const id = parseRunId(runId);
  const { data: run } = await github.rest.actions.getWorkflowRun({
    owner,
    repo,
    run_id: id,
  });
  if (
    run?.id !== id ||
    run.path !== workflowPath ||
    run.name !== workflowName ||
    run.event !== 'pull_request' ||
    run.status !== 'completed' ||
    !TERMINAL_CONCLUSIONS.has(run.conclusion) ||
    run.repository?.full_name !== repository ||
    !SHA.test(run.head_sha || '') ||
    !Number.isInteger(run.run_attempt) ||
    run.run_attempt < 1
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
    job.name === workflowName &&
    job.head_sha === run.head_sha &&
    job.status === 'completed' &&
    job.conclusion === run.conclusion,
  );
  if (canonicalJobs.length !== 1) {
    throw new Error('source run does not identify exactly one canonical workflow job');
  }

  const job = canonicalJobs[0];
  const { data: checkRun } = await github.rest.checks.get({
    owner,
    repo,
    check_run_id: job.id,
  });
  if (
    checkRun?.id !== job.id ||
    checkRun.name !== workflowName ||
    checkRun.status !== 'completed' ||
    checkRun.conclusion !== run.conclusion ||
    checkRun.head_sha !== run.head_sha
  ) {
    throw new Error('canonical workflow job check identity is inconsistent');
  }

  const associations = checkRun.pull_requests || [];
  if (associations.length !== 1 || !Number.isInteger(associations[0]?.number)) {
    throw new Error('canonical workflow check does not identify exactly one pull request');
  }
  const associated = associations[0];
  const associatedBase = associated.base?.sha;
  const associatedHead = associated.head?.sha;
  if (
    associated.base?.ref !== defaultBranch ||
    associated.base?.repo?.id !== repositoryId ||
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
    pullRequest?.state !== 'open' ||
    pullRequest.base?.ref !== defaultBranch ||
    pullRequest.base?.repo?.full_name !== repository ||
    pullRequest.base?.sha !== associatedBase ||
    pullRequest.head?.sha !== associatedHead ||
    typeof pullRequest.head?.repo?.full_name !== 'string'
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
