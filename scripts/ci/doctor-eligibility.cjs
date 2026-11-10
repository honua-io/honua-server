// Read-only pre-activation checks. API failures stop activation rather than spending on uncertainty.
const { getDoctorPushDeferral } = require('./doctor-push-guard.cjs');
const checkoutFailures = ["couldn't fetch the PR branch", 'pr head commit was not available', "isn't available to the doctor's checkout"];
const knownFlakes = [/The hosted runner.*lost communication/i, /Failed to download action.*(502|503|504)/i, /The runner has received a shutdown signal/i];

function handled(body, sha) {
  const text = (body || '').toLowerCase();
  return text.includes(`ci-doctor: ${sha}`.toLowerCase()) && !checkoutFailures.some(message => text.includes(message.toLowerCase()));
}

async function eligiblePull(github, repo, pr) {
  return !pr.draft && !(await getDoctorPushDeferral(github, { repo, pullNumber: pr.number, expectedHead: pr.head.sha }));
}

async function failedSource(github, context) {
  const repo = context.repo;
  const sourceId = context.payload.inputs?.source_run_id;
  const run = sourceId ? (await github.rest.actions.getWorkflowRun({ ...repo, run_id: Number(sourceId) })).data : context.payload.workflow_run;
  if (!run || run.head_repository?.full_name !== `${repo.owner}/${repo.repo}`) {
    return { reason: 'No same-repository failed run' };
  }
  if (!sourceId && run.path === '.github/workflows/ci.yml' && run.head_branch === 'trunk') {
    return { reason: 'Direct CI callback owns trunk CI' };
  }
  // The final CI callback runs before the containing workflow has a conclusion.
  // Accept that narrow case only after its actual aggregate gate has failed.
  let jobs;
  if (run.conclusion !== 'failure') {
    if (!sourceId || run.status !== 'in_progress' || run.path !== '.github/workflows/ci.yml' || run.head_branch !== 'trunk') {
      return { reason: 'Source run has not failed' };
    }
    jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRun, { ...repo, run_id: run.id, per_page: 100 });
    if (!jobs.some(job => job.name === 'CI Gate' && job.conclusion === 'failure')) {
      return { reason: 'CI Gate has not failed' };
    }
  }
  return { run, jobs };
}

async function ciEligibility(github, context) {
  const repo = context.repo;
  const { run, jobs: sourceJobs, reason } = await failedSource(github, context);
  if (reason) return { decision: 'not-actionable', reason };
  if (Object.hasOwn(context.payload.inputs || {}, 'head_sha') && context.payload.inputs.head_sha !== run.head_sha) {
    return { decision: 'not-actionable', reason: 'Dispatch head does not match source run' };
  }
  if (run.head_branch === 'trunk') {
    const { data: branch } = await github.rest.repos.getBranch({ ...repo, branch: 'trunk' });
    if (branch.commit.sha !== run.head_sha) return { decision: 'already-handled', reason: 'Newer trunk head' };
  }
  const pulls = await github.paginate(github.rest.repos.listPullRequestsAssociatedWithCommit, { ...repo, commit_sha: run.head_sha, per_page: 100 });
  const open = pulls.filter(pr => pr.state === 'open' && pr.head.ref === run.head_branch);
  if (run.head_branch !== 'trunk' && open.length === 0) return { decision: 'not-actionable', reason: 'No open pull request' };
  for (const pr of open) {
    if (pr.head.sha !== run.head_sha) return { decision: 'already-handled', reason: 'Newer pull request head' };
    if (!(await eligiblePull(github, repo, pr))) return { decision: 'not-actionable', reason: 'Draft, fork, moved head or unfinished PR Gate' };
    const comments = await github.paginate(github.rest.issues.listComments, { ...repo, issue_number: pr.number, per_page: 100 });
    if (comments.some(comment => handled(comment.body, run.head_sha))) return { decision: 'already-handled', reason: 'Existing attempt marker' };
  }
  const existing = await github.rest.search.issuesAndPullRequests({ q: `repo:${repo.owner}/${repo.repo} is:open "${run.head_sha}" "ci-doctor"` });
  if (existing.data.items.some(item => handled(item.body, run.head_sha) || item.title.startsWith('fix(ci):'))) return { decision: 'already-handled', reason: 'Existing issue or draft fix' };
  const jobs = sourceJobs ?? await github.paginate(github.rest.actions.listJobsForWorkflowRun, { ...repo, run_id: run.id, per_page: 100 });
  const failed = jobs.filter(job => job.conclusion === 'failure').sort((a, b) => (a.completed_at || '').localeCompare(b.completed_at || ''));
  const first = failed[0];
  // Only skip exact infrastructure signatures. Unknown failures go to Haiku.
  if (first) {
    const response = await github.rest.actions.downloadJobLogsForWorkflowRun({ ...repo, job_id: first.id });
    const log = typeof response.data === 'string' ? response.data : Buffer.from(response.data).toString();
    if (knownFlakes.some(pattern => pattern.test(log))) return { decision: 'flake-rerun', reason: 'Known infrastructure signature', run_id: run.id };
  }
  return { decision: 'needs-triage', run_id: run.id, sha: run.head_sha, pull: open[0]?.number, failed_jobs: failed.map(job => ({ id: job.id, name: job.name, step: job.steps?.find(step => step.conclusion === 'failure')?.name })) };
}

async function reviewEligibility(github, context) {
  const number = context.payload.inputs?.pull_number;
  const pr = number ? (await github.rest.pulls.get({ ...context.repo, pull_number: Number(number) })).data : context.payload.pull_request;
  if (!pr || !(await eligiblePull(github, context.repo, pr))) return { decision: 'not-actionable', reason: 'Draft, fork, moved head or unfinished PR Gate' };
  const threads = [];
  let cursor = null;
  do {
    const data = await github.graphql(`query($owner:String!,$repo:String!,$number:Int!,$cursor:String) {
      repository(owner:$owner,name:$repo) { pullRequest(number:$number) {
        reviewThreads(first:100,after:$cursor) { pageInfo { hasNextPage endCursor }
          nodes { isResolved isOutdated path line comments(last:100) { nodes { databaseId author { login } authorAssociation body createdAt } } }
        }
      } }
    }`, { ...context.repo, number: pr.number, cursor });
    const page = data.repository.pullRequest.reviewThreads;
    threads.push(...page.nodes);
    cursor = page.pageInfo.hasNextPage ? page.pageInfo.endCursor : null;
  } while (cursor);
  const bots = ['chatgpt-codex-connector', 'copilot-pull-request-reviewer', 'github-actions', 'honua-io-bot'];
  const trusted = comment => ['OWNER', 'MEMBER', 'COLLABORATOR'].includes(comment.authorAssociation) || bots.includes(comment.author?.login);
  const actionable = threads.filter(thread => !thread.isResolved && !thread.isOutdated)
    .map(thread => ({ ...thread, comments: thread.comments.nodes.filter(trusted).map(comment => ({ ...comment, author: comment.author?.login })) }))
    .filter(thread => thread.comments.length > 0);
  return actionable.length ? { decision: 'needs-triage', pull: pr.number, sha: pr.head.sha, threads: actionable } : { decision: 'not-actionable', reason: 'No unresolved current trusted review threads' };
}

async function syncEligibility(github, context) {
  const pulls = await github.paginate(github.rest.pulls.list, { ...context.repo, state: 'open', per_page: 100 });
  const candidates = [];
  for (const pr of pulls) {
    if (pr.labels.some(label => label.name === 'conflict-stuck') || !(await eligiblePull(github, context.repo, pr))) continue;
    const { data: detail } = await github.rest.pulls.get({ ...context.repo, pull_number: pr.number });
    if (['dirty', 'behind'].includes(detail.mergeable_state)) candidates.push({ number: pr.number, sha: pr.head.sha, branch: pr.head.ref, mergeable_state: detail.mergeable_state });
  }
  return candidates.length ? { decision: 'needs-triage', candidates } : { decision: 'not-actionable', reason: 'No eligible conflicting or behind pull requests' };
}

async function main({ github, context, core, kind }) {
  const sourceId = context.payload.inputs?.source_run_id || context.payload.workflow_run?.id;
  if (sourceId) core.info(`Doctor source run: ${Number(sourceId)}`);
  const result = await ({ ci: ciEligibility, review: reviewEligibility, sync: syncEligibility })[kind](github, context);
  core.info(`Doctor eligibility: ${result.decision}; ${result.reason || 'Actionable evidence found'}`);
  core.setOutput('eligible', result.decision === 'needs-triage' ? 'true' : 'false');
  core.setOutput('context', JSON.stringify(result));
  await core.summary.addHeading('Doctor deterministic triage').addCodeBlock(JSON.stringify(result, null, 2), 'json').write();
  return result;
}
module.exports = { main, handled, knownFlakes, ciEligibility, reviewEligibility, syncEligibility };
