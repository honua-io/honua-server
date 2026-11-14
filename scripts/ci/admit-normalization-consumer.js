'use strict';

async function admitNormalization({github, context}) {
  const run = context.payload.workflow_run;
  const repository = `${context.repo.owner}/${context.repo.repo}`;
  if (run.name !== 'Derived Artifact Normalization' ||
      run.path !== '.github/workflows/normalize-derived-artifacts.yml' ||
      run.event !== 'pull_request' || run.conclusion !== 'success' ||
      run.repository?.full_name !== repository) return null;
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
  return pr.state === 'open' && !pr.draft && pr.head?.sha === run.head_sha &&
    pr.base?.repo?.full_name === repository ? pr.number : null;
}

module.exports = {admitNormalization};
