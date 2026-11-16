'use strict';

async function admitNormalization({github, context}) {
  const run = context.payload.workflow_run;
  const repository = `${context.repo.owner}/${context.repo.repo}`;
  if (!canonicalProducer(run, repository)) return null;
  let associated = run.pull_requests || [];
  if (!associated.length) {
    const {data} = await github.rest.repos.listPullRequestsAssociatedWithCommit({
      ...context.repo, commit_sha: run.head_sha,
    });
    associated = data.filter(pr => pr.state === 'open' && pr.head?.sha === run.head_sha);
  }
  if (associated.length !== 1) return null;
  const {data: pr} = await github.rest.pulls.get({
    ...context.repo, pull_number: associated[0].number,
  });
  if (!currentReadyHead(pr, run.head_sha, repository)) return null;
  if (!await producedEnvelope({github, context, run})) return null;
  return pr.number;
}

async function producedEnvelope({github, context, run}) {
  const {data} = await github.rest.actions.listJobsForWorkflowRunAttempt({
    ...context.repo, run_id: run.id, attempt_number: run.run_attempt, per_page: 100,
  });
  if (data.total_count > data.jobs.length) throw new Error('Producer jobs exceeded bounded page.');
  const packagers = data.jobs.filter(job => job.name === 'Produce data-only normalization envelope');
  if (packagers.length !== 1) throw new Error('Expected exactly one envelope packager.');
  return packagers[0].conclusion === 'success';
}

function canonicalProducer(run, repository) {
  return run.name === 'Derived Artifact Normalization' &&
    run.path === '.github/workflows/normalize-derived-artifacts.yml' &&
    run.event === 'pull_request' && run.conclusion === 'success' &&
    run.repository?.full_name === repository;
}

function currentReadyHead(pr, head, repository) {
  return pr.state === 'open' && !pr.draft && pr.head?.sha === head &&
    pr.base?.repo?.full_name === repository;
}

module.exports = {admitNormalization};
