'use strict';

// Opens, updates and closes one issue per failing scheduled workflow job across the
// organisation's repositories. Called by .github/workflows/scheduled-failure-issues.yml.
// Plain REST calls only; no model is involved. This repository is public, so logs and the
// job summary never name a private repository.

const LABEL = 'scheduled-failure';
const FAILED = new Set(['failure', 'timed_out', 'startup_failure']);
const SETTLED = new Set(['success', ...FAILED]);
const NO_JOB = '(no job ran)';
const HOUR = 3600 * 1000;
const NOT_FOUND = 404;
const DUE_HOURS = 24;
const DEFAULT_ISSUES_PER_RUN = 5;
const DEFAULT_RUNS_PER_WORKFLOW = 6;
const DEFAULT_LOOKBACK_HOURS = 72;
const SHORT_SHA = 10;
const PRIVATE_LABEL = 'a private repository';

const markerFor = key => `<!-- ${LABEL} key=${key} -->`;
const runMarkerFor = run => `<!-- ${LABEL} run=${run.id} -->`;
const keyOf = body => (String(body || '').match(/<!-- scheduled-failure key=(\S+) -->/) || [])[1];
const fileOf = path => path.split('/').pop();
const isoSeconds = date => date.toISOString().replace(/\.\d+Z$/, 'Z');
const shortSha = run => run.head_sha.slice(0, SHORT_SHA);
const byCreated = (a, b) => Date.parse(a.created_at) - Date.parse(b.created_at);
const ref = (ctx, issueNumber) => `${ctx.label}#${ctx.isPrivate ? '' : issueNumber}`;

function ownerFor(config, repo, path) {
  const entry = config.repos?.[repo] || {};
  return { ...config.defaultOwner, ...(entry.default || {}), ...(entry.workflows?.[fileOf(path)] || {}) };
}

// Automation dispatches that carry a workflow's scheduled work. Each rule names the ref
// (`default` for the default branch, otherwise a pattern) and optionally the run-title pattern of
// a complete run, so partial runs (honua-server "CI affected") never count.
function isRuledBotDispatch({ config, repo, defaultBranch }, run) {
  const rule = config.dispatchWorkflows?.[repo]?.[fileOf(run.path)];
  if (run.event !== 'workflow_dispatch' || !rule) return false;
  if (!/\[bot\]$/.test(run.triggering_actor?.login || '')) return false;
  const onRef = rule.ref === 'default' ? run.head_branch === defaultBranch : new RegExp(rule.ref).test(run.head_branch || '');
  return onRef && new RegExp(rule.title || '').test(run.display_title || '');
}

function isSweptRun(ctx, run) {
  const ignored = ctx.config.ignore?.[ctx.repo] || [];
  if (ignored.includes(fileOf(run.path))) return false;
  return run.event === 'schedule' || isRuledBotDispatch(ctx, run);
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
  for (const list of byPath.values()) list.sort(byCreated);
  return byPath;
}

async function jobsOf(ctx, run) {
  const { github, owner, repo } = ctx;
  return github.paginate('GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs', {
    owner, repo, run_id: run.id, filter: 'latest', per_page: 100,
  });
}

// A workflow with a successJobs pattern (honua-server's nightly container build hands off to a
// candidate-tag run) is green only when a matching job succeeded; a hand-off-only success is ignored.
async function withoutHandoffs(ctx, path, runs) {
  const pattern = ctx.config.successJobs?.[ctx.repo]?.[fileOf(path)];
  if (!pattern) return runs;
  const kept = [];
  for (const run of runs) {
    const proof = run.conclusion !== 'success'
      || (await jobsOf(ctx, run)).some(job => job.conclusion === 'success' && new RegExp(pattern).test(job.name));
    if (proof) kept.push(run);
  }
  return kept;
}

async function ensureLabel(ctx, name) {
  const { github, owner, repo, labelCache } = ctx;
  const id = `${repo}/${name}`;
  if (labelCache.has(id)) return;
  try {
    await github.request('GET /repos/{owner}/{repo}/labels/{name}', { owner, repo, name });
  } catch (error) {
    if (error.status !== NOT_FOUND) throw error;
    await github.request('POST /repos/{owner}/{repo}/labels', {
      owner, repo, name, color: name === LABEL ? 'b60205' : 'ededed',
    });
  }
  labelCache.add(id);
}

// Gate and summary jobs fail because a job they need failed; they get an issue only when nothing else failed.
async function failedJobs(ctx, run) {
  const failed = (await jobsOf(ctx, run))
    .filter(job => FAILED.has(job.conclusion))
    .sort((a, b) => Date.parse(a.started_at || 0) - Date.parse(b.started_at || 0));
  const downstream = new RegExp(ctx.config.downstreamJobPattern || '^$');
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
    `- Owner lane: \`${lane.lane}\``,
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
    report.closed.push(ref(ctx, issue.number));
  }
}

// The issues list returns `comments` as a count, so fetched comment lists are cached per sweep.
async function commentsOf(ctx, issueNumber) {
  const { github, owner, repo, commentCache } = ctx;
  if (!commentCache.has(issueNumber)) {
    commentCache.set(issueNumber, await github.paginate('GET /repos/{owner}/{repo}/issues/{issue_number}/comments', {
      owner, repo, issue_number: issueNumber, per_page: 100,
    }));
  }
  return commentCache.get(issueNumber);
}

async function mentionsRun(ctx, issue, run) {
  const marker = runMarkerFor(run);
  if (issue.body?.includes(marker)) return true;
  return (await commentsOf(ctx, issue.number)).some(item => item.body?.includes(marker));
}

const claimText = (ctx, { lane, run, due }) =>
  `claimed: ${lane.lane} ${isoSeconds(ctx.now)} - auto-assigned owner of ${fileOf(run.path)} (due ${due})`;

// An issue whose claim comment failed after it was opened gets the claim on a later sweep.
async function recordFailure(ctx, { issue, run, job, lane, due }) {
  const comments = await commentsOf(ctx, issue.number);
  if (!comments.some(item => item.body?.startsWith('claimed: '))) {
    const claim = claimText(ctx, { lane, run, due });
    await comment(ctx, issue.number, claim);
    comments.push({ body: claim });
  }
  if (await mentionsRun(ctx, issue, run)) return;
  const body = `Failed again: ${run.html_url} (${run.created_at}, \`${shortSha(run)}\`), job ${job.html_url}.\n\n${runMarkerFor(run)}`;
  await comment(ctx, issue.number, body);
  comments.push({ body });
  ctx.report.updated.push(ref(ctx, issue.number));
}

async function openIssue(ctx, args) {
  const { github, owner, repo, report } = ctx;
  for (const name of args.labels) await ensureLabel(ctx, name);
  const body = issueBody(args);
  const { data: issue } = await github.request('POST /repos/{owner}/{repo}/issues', {
    owner, repo, labels: args.labels, body,
    title: `Scheduled run failing: ${args.workflowName} / ${args.job.name}`,
  });
  report.opened.push(ref(ctx, issue.number));
  const claim = claimText(ctx, args);
  await comment(ctx, issue.number, claim);
  ctx.commentCache.set(issue.number, [{ body: claim }]);
  return { number: issue.number, body, state: 'open' };
}

async function workflowNameOf(ctx, run) {
  const { github, owner, repo, workflowNames } = ctx;
  if (!workflowNames.has(run.workflow_id)) {
    const { data } = await github.request('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}', {
      owner, repo, workflow_id: run.workflow_id,
    });
    workflowNames.set(run.workflow_id, data.name || run.path);
  }
  return workflowNames.get(run.workflow_id);
}

// A run already recorded on a closed issue (closed by hand or by a fix PR) is not filed again.
async function recordedOnClosed(ctx, { issues, key, run }) {
  for (const issue of issues.filter(item => item.state !== 'open' && keyOf(item.body) === key)) {
    if (await mentionsRun(ctx, issue, run)) return true;
  }
  return false;
}

async function fileRunFailures(ctx, { path, run, issues }) {
  const jobs = await failedJobs(ctx, run);
  const lane = ownerFor(ctx.config, ctx.repo, path);
  const labels = ['bug', lane.area, LABEL].filter(Boolean);
  const due = isoSeconds(new Date(ctx.now.getTime() + DUE_HOURS * HOUR));
  let budget = ctx.config.maxIssuesPerRun || DEFAULT_ISSUES_PER_RUN;
  for (const job of jobs) {
    const key = `${path}::${job.name.replace(/\s+/g, '_')}`;
    const existing = issues.find(issue => issue.state === 'open' && keyOf(issue.body) === key);
    if (existing) {
      await recordFailure(ctx, { issue: existing, run, job, lane, due });
    } else if (budget > 0 && !await recordedOnClosed(ctx, { issues, key, run })) {
      budget -= 1;
      const workflowName = await workflowNameOf(ctx, run);
      issues.push(await openIssue(ctx, { run, job, jobs, lane, key, due, labels, workflowName }));
    }
  }
}

async function listRuns(ctx, since) {
  const { github, owner, repo, config } = ctx;
  const window = { owner, repo, per_page: 100, created: `>=${since}`, exclude_pull_requests: true };
  const runs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs', { ...window, event: 'schedule' });
  for (const workflow_id of Object.keys(config.dispatchWorkflows?.[repo] || {})) {
    runs.push(...await github.paginate('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs', {
      ...window, workflow_id, event: 'workflow_dispatch',
    }));
  }
  return runs;
}

// The newest failed runs after the workflow's last success are filed oldest first, so a job that
// failed only in an earlier run still gets its issue; a successful newest run closes them all.
// A failure followed by a success before any sweep is not filed.
async function sweepRepo(ctx) {
  const { github, owner, repo, config, now } = ctx;
  const since = isoSeconds(new Date(now.getTime() - (config.lookbackHours || DEFAULT_LOOKBACK_HOURS) * HOUR));
  const byPath = runsByWorkflow(ctx, await listRuns(ctx, since));
  const known = (await github.paginate('GET /repos/{owner}/{repo}/issues', {
    owner, repo, state: 'all', labels: LABEL, since, per_page: 100,
  })).filter(issue => !issue.pull_request && keyOf(issue.body));
  for (const [path, swept] of byPath) {
    const runs = await withoutHandoffs(ctx, path, swept);
    if (!runs.length) continue;
    const issues = known.filter(issue => keyOf(issue.body).startsWith(`${path}::`));
    const latest = runs[runs.length - 1];
    if (latest.conclusion === 'success') {
      await closeIssues(ctx, issues.filter(issue => issue.state === 'open'), latest);
      continue;
    }
    const lastSuccess = runs.map(run => run.conclusion).lastIndexOf('success');
    const failures = runs.slice(lastSuccess + 1).slice(-(config.maxRunsPerWorkflow || DEFAULT_RUNS_PER_WORKFLOW));
    for (const run of failures) await fileRunFailures(ctx, { path, run, issues });
  }
}

async function sweep({ github, core, config, owner, now = new Date() }) {
  const repos = (await github.paginate('GET /installation/repositories', { per_page: 100 }))
    .filter(repo => repo.owner?.login === owner && !repo.archived && !repo.disabled)
    .filter(repo => !(config.skipRepos || []).includes(repo.name))
    .sort((a, b) => a.name.localeCompare(b.name));
  const report = { opened: [], updated: [], closed: [], errors: [] };
  const shared = { github, owner, config, now, report, labelCache: new Set() };
  for (const repo of repos) {
    const label = repo.private ? PRIVATE_LABEL : repo.name;
    const ctx = {
      ...shared, repo: repo.name, defaultBranch: repo.default_branch, isPrivate: Boolean(repo.private), label,
      commentCache: new Map(), workflowNames: new Map(),
    };
    try {
      await sweepRepo(ctx);
    } catch (error) {
      const detail = repo.private ? `HTTP ${error.status || 'error'}` : `${error.status || ''} ${error.message}`.trim();
      report.errors.push(`${label}: ${detail}`);
      core.warning(`scheduled-failure sweep of ${label} failed: ${detail}`);
    }
  }
  core.info(`swept ${repos.length} repositories: ${JSON.stringify(report)}`);
  if (report.errors.length === repos.length && repos.length) throw new Error('every repository sweep failed');
  return report;
}

module.exports = { sweep, ownerFor, keyOf, markerFor, LABEL };
