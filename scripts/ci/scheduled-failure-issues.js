'use strict';

// Opens, updates and closes one issue per failing scheduled workflow job across the
// organisation's repositories. Called by .github/workflows/scheduled-failure-issues.yml.
// Plain REST calls only; no model is involved.

const LABEL = 'scheduled-failure';
const FAILED = new Set(['failure', 'timed_out', 'startup_failure']);
const SETTLED = new Set(['success', ...FAILED]);
const NO_JOB = '(no job ran)';
const HOUR = 3600 * 1000;

const markerFor = key => `<!-- ${LABEL} key=${key} -->`;
const runMarkerFor = run => `<!-- ${LABEL} run=${run.id} -->`;
const keyOf = body => (String(body || '').match(/<!-- scheduled-failure key=(\S+) -->/) || [])[1];
const fileOf = path => path.split('/').pop();
const isoSeconds = date => date.toISOString().replace(/\.\d+Z$/, 'Z');
const shortSha = run => run.head_sha.slice(0, 10);

function ownerFor(config, repo, path) {
  const entry = config.repos?.[repo] || {};
  return { ...config.defaultOwner, ...(entry.default || {}), ...(entry.workflows?.[fileOf(path)] || {}) };
}

// Scheduled runs, plus complete automation dispatches on the default branch for workflows whose
// scheduled work arrives as workflow_dispatch. botDispatchWorkflows maps a workflow file to the
// run-title pattern of a complete run, so partial runs (honua-server "CI affected") never count.
function isSweptRun({ config, repo, defaultBranch }, run) {
  if ((config.ignore?.[repo] || []).includes(fileOf(run.path))) return false;
  if (run.event === 'schedule') return true;
  const titlePattern = config.botDispatchWorkflows?.[repo]?.[fileOf(run.path)];
  return run.event === 'workflow_dispatch'
    && Boolean(titlePattern)
    && run.head_branch === defaultBranch
    && /\[bot\]$/.test(run.triggering_actor?.login || '')
    && new RegExp(titlePattern).test(run.display_title || '');
}

// Settled swept runs per workflow path, oldest first.
function runsByWorkflow(ctx, runs) {
  const byPath = new Map();
  const seen = new Set();
  for (const run of runs) {
    if (seen.has(run.id) || run.status !== 'completed' || !SETTLED.has(run.conclusion)) continue;
    if (!isSweptRun(ctx, run)) continue;
    seen.add(run.id);
    if (!byPath.has(run.path)) byPath.set(run.path, []);
    byPath.get(run.path).push(run);
  }
  for (const list of byPath.values()) list.sort((a, b) => Date.parse(a.created_at) - Date.parse(b.created_at));
  return byPath;
}

async function ensureLabel(ctx, name) {
  const { github, owner, repo, labelCache } = ctx;
  const id = `${repo}/${name}`;
  if (labelCache.has(id)) return;
  try {
    await github.request('GET /repos/{owner}/{repo}/labels/{name}', { owner, repo, name });
  } catch (error) {
    if (error.status !== 404) throw error;
    await github.request('POST /repos/{owner}/{repo}/labels', {
      owner, repo, name, color: name === LABEL ? 'b60205' : 'ededed',
    });
  }
  labelCache.add(id);
}

// Gate and summary jobs fail because a job they need failed; they get an issue only when nothing else failed.
async function failedJobs(ctx, run) {
  const { github, owner, repo, config } = ctx;
  const jobs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs', {
    owner, repo, run_id: run.id, filter: 'latest', per_page: 100,
  });
  const failed = jobs
    .filter(job => FAILED.has(job.conclusion))
    .sort((a, b) => Date.parse(a.started_at || 0) - Date.parse(b.started_at || 0));
  const downstream = new RegExp(config.downstreamJobPattern || '^$');
  const causes = failed.filter(job => !downstream.test(job.name));
  if (causes.length) return causes;
  return failed.length ? failed : [{ name: NO_JOB, html_url: run.html_url }];
}

function issueBody({ run, job, jobs, lane, key, due, workflowName }) {
  const others = jobs.filter(other => other.name !== job.name).map(other => `\`${other.name}\``);
  const lines = [
    `The scheduled workflow **${workflowName}** failed in job \`${job.name}\`.`,
    '',
    `- Run: ${run.html_url} (${run.created_at}, ${run.event}, \`${shortSha(run)}\`)`,
    `- Job: ${job.html_url}`,
    `- Owner lane: \`${lane.lane}\` (scripts/ci/scheduled-failure-owners.json in honua-server)`,
    `- Due: ${due}`,
  ];
  if (others.length) lines.push(`- Other failed jobs in this run: ${others.join(', ')}`);
  lines.push('', 'Fix the cause, or quarantine it with a ticket. This issue closes itself when a later scheduled',
    'run of the workflow succeeds; each new failing run adds a comment.', '', markerFor(key), runMarkerFor(run));
  return lines.join('\n');
}

async function comment(ctx, issueNumber, body) {
  const { github, owner, repo } = ctx;
  await github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/comments', {
    owner, repo, issue_number: issueNumber, body,
  });
}

async function closeIssues(ctx, issues, run) {
  const { github, owner, repo, report } = ctx;
  for (const issue of issues) {
    await comment(ctx, issue.number, `Scheduled run ${run.html_url} succeeded on \`${shortSha(run)}\`; closing.`);
    await github.request('PATCH /repos/{owner}/{repo}/issues/{issue_number}', {
      owner, repo, issue_number: issue.number, state: 'closed', state_reason: 'completed',
    });
    issue.state = 'closed';
    report.closed.push(`${repo}#${issue.number}`);
  }
}

async function recordFailure(ctx, { issue, run, job }) {
  const { github, owner, repo, report } = ctx;
  const marker = runMarkerFor(run);
  if (!issue.comments) {
    issue.comments = await github.paginate('GET /repos/{owner}/{repo}/issues/{issue_number}/comments', {
      owner, repo, issue_number: issue.number, per_page: 100,
    });
  }
  const recorded = [issue.body, ...issue.comments.map(item => item.body)]
    .some(text => text?.includes(marker) || text?.includes(run.html_url));
  if (recorded) return;
  const body = `Failed again: ${run.html_url} (${run.created_at}, \`${shortSha(run)}\`), job ${job.html_url}.\n\n${marker}`;
  await comment(ctx, issue.number, body);
  issue.comments.push({ body });
  report.updated.push(`${repo}#${issue.number}`);
}

async function openIssue(ctx, args) {
  const { github, owner, repo, report, now } = ctx;
  for (const name of args.labels) await ensureLabel(ctx, name);
  const body = issueBody(args);
  const { data: issue } = await github.request('POST /repos/{owner}/{repo}/issues', {
    owner, repo, labels: args.labels, body,
    title: `Scheduled run failing: ${args.workflowName} / ${args.job.name}`,
  });
  const claim = `claimed: ${args.lane.lane} ${isoSeconds(now)} - auto-assigned owner of ${fileOf(args.run.path)} (due ${args.due})`;
  await comment(ctx, issue.number, claim);
  report.opened.push(`${repo}#${issue.number}`);
  return { number: issue.number, body, state: 'open', comments: [{ body: claim }] };
}

async function fileRunFailures(ctx, { path, run, issues }) {
  const { github, owner, repo, config, now } = ctx;
  const jobs = await failedJobs(ctx, run);
  const { data: workflow } = await github.request('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}', {
    owner, repo, workflow_id: run.workflow_id,
  });
  const lane = ownerFor(config, repo, path);
  const labels = ['bug', lane.area, LABEL].filter(Boolean);
  const due = isoSeconds(new Date(now.getTime() + 24 * HOUR));
  let budget = config.maxIssuesPerRun || 5;
  for (const job of jobs) {
    const key = `${path}::${job.name.replace(/\s+/g, '_')}`;
    const existing = issues.find(issue => issue.state === 'open' && keyOf(issue.body) === key);
    if (existing) {
      await recordFailure(ctx, { issue: existing, run, job });
    } else if (budget > 0) {
      budget -= 1;
      const workflowName = workflow.name || path;
      issues.push(await openIssue(ctx, { run, job, jobs, lane, key, due, labels, workflowName }));
    }
  }
}

async function listRuns(ctx, since) {
  const { github, owner, repo, config, defaultBranch } = ctx;
  const window = { owner, repo, per_page: 100, created: `>=${since}`, exclude_pull_requests: true };
  const runs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs', { ...window, event: 'schedule' });
  for (const workflow_id of Object.keys(config.botDispatchWorkflows?.[repo] || {})) {
    runs.push(...await github.paginate('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs', {
      ...window, workflow_id, event: 'workflow_dispatch', branch: defaultBranch,
    }));
  }
  return runs;
}

// Every failed run after the workflow's last success is filed oldest first, so a failure that a
// later run no longer shows still gets its issue; a newest successful run closes them all.
async function sweepRepo(ctx) {
  const { github, owner, repo, config, now } = ctx;
  const since = new Date(now.getTime() - (config.lookbackHours || 72) * HOUR).toISOString();
  const byPath = runsByWorkflow(ctx, await listRuns(ctx, since));
  const openIssues = (await github.paginate('GET /repos/{owner}/{repo}/issues', {
    owner, repo, state: 'open', labels: LABEL, per_page: 100,
  })).filter(issue => !issue.pull_request && keyOf(issue.body));
  for (const [path, runs] of byPath) {
    const issues = openIssues.filter(issue => keyOf(issue.body).startsWith(`${path}::`));
    const latest = runs[runs.length - 1];
    if (latest.conclusion === 'success') {
      await closeIssues(ctx, issues, latest);
      continue;
    }
    const lastSuccess = runs.map(run => run.conclusion).lastIndexOf('success');
    for (const run of runs.slice(lastSuccess + 1)) await fileRunFailures(ctx, { path, run, issues });
  }
}

async function sweep({ github, core, config, owner, now = new Date() }) {
  const repos = (await github.paginate('GET /installation/repositories', { per_page: 100 }))
    .filter(repo => repo.owner?.login === owner && !repo.archived && !repo.disabled)
    .filter(repo => !(config.skipRepos || []).includes(repo.name))
    .sort((a, b) => a.name.localeCompare(b.name));
  const report = { opened: [], updated: [], closed: [], errors: [] };
  const labelCache = new Set();
  for (const { name: repo, default_branch: defaultBranch } of repos) {
    try {
      await sweepRepo({ github, owner, repo, defaultBranch, config, now, labelCache, report });
    } catch (error) {
      report.errors.push(`${repo}: ${error.status || ''} ${error.message}`.trim());
      core.warning(`scheduled-failure sweep of ${repo} failed: ${error.message}`);
    }
  }
  core.info(`swept ${repos.length} repositories: ${JSON.stringify(report)}`);
  if (report.errors.length === repos.length && repos.length) throw new Error('every repository sweep failed');
  return report;
}

module.exports = { sweep, ownerFor, keyOf, markerFor, LABEL };
