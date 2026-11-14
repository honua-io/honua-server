'use strict';

const SHUTDOWN = /^\d{4}-\d{2}-\d{2}T[\d:.]+Z ##\[error\]The runner has received a shutdown signal\. This can happen when the runner service is stopped, or a manually started runner is canceled\.\r?$/m;
const SUMMARY_STEPS = new Map([
  ['Test Suite Summary', new Set(['Verify server shard execution receipts', 'Check required test job results'])],
  ['CI Gate', new Set(['Check required job results'])],
]);

function eligibleRun(run, repository) {
  return run.head_repository?.full_name === repository &&
    run.path === '.github/workflows/ci.yml' && run.head_branch === 'trunk' &&
    run.status === 'completed' && run.conclusion === 'failure' && run.run_attempt === 1 &&
    (run.event === 'schedule' || (run.event === 'workflow_dispatch' &&
      /^CI full(?:\s|$)/.test(run.display_title)));
}

async function shutdownJob({ github, repo, job }) {
  if (!job.labels?.includes('honua-heavy') || job.conclusion !== 'failure') return false;
  const response = await github.rest.actions.downloadJobLogsForWorkflowRun({ ...repo, job_id: job.id });
  if (typeof response.data !== 'string') throw new Error(`Unreadable logs for job ${job.id}`);
  return SHUTDOWN.test(response.data);
}

function dependentSummary(job) {
  const allowed = SUMMARY_STEPS.get(job.name);
  const failed = job.steps?.filter(step => step.conclusion === 'failure') || [];
  return allowed && failed.length > 0 && failed.every(step => allowed.has(step.name));
}

function sameRun(run, source, repository) {
  return eligibleRun(run, repository) && run.head_sha === source.head_sha;
}

async function retryShutdown({ github, context, core }) {
  const repo = context.repo;
  const repository = `${repo.owner}/${repo.repo}`;
  const event = context.payload.workflow_run;
  if (!eligibleRun(event, repository)) return core.info('Not a first-attempt full trunk failure.');
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: event.id });
  if (!sameRun(run, event, repository)) {
    return core.info('Source run changed before inspection.');
  }
  const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRunAttempt,
    { ...repo, run_id: run.id, attempt_number: 1, per_page: 100 });
  const failed = jobs.filter(job => job.conclusion === 'failure');
  const interrupted = [];
  for (const job of failed) {
    if (dependentSummary(job)) continue;
    if (!await shutdownJob({ github, repo, job })) {
      return core.info(`No retry: ${job.id} (${job.name}) is not a proven heavy-runner shutdown.`);
    }
    interrupted.push(job.id);
  }
  if (!interrupted.length || jobs.some(job => job.conclusion === 'cancelled' || job.status !== 'completed')) {
    return core.info('No retry: no interrupted jobs, or attempt contains cancellation/incomplete jobs.');
  }
  const { data: current } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: run.id });
  if (!sameRun(current, run, repository)) {
    return core.info('Source attempt changed before retry.');
  }
  await github.rest.actions.reRunWorkflowFailedJobs({ ...repo, run_id: run.id });
  core.info(`Requested one retry for shutdown jobs ${interrupted.join(', ')} and failed dependents; successful jobs retained.`);
}

module.exports = { eligibleRun, retryShutdown, shutdownJob };
