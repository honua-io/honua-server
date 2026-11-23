'use strict';

// Opens, updates and closes one issue per failing scheduled workflow job across the
// organisation's repositories. Called by .github/workflows/scheduled-failure-issues.yml.
// Plain REST calls only; no model is involved.

const LABEL = 'scheduled-failure';
const FAILED = new Set(['failure', 'timed_out', 'startup_failure']);
const SETTLED = new Set(['success', ...FAILED]);
const NO_JOB = '(no job ran)';

const markerFor = key => `<!-- ${LABEL} key=${key} -->`;
const keyOf = body => (String(body || '').match(/<!-- scheduled-failure key=(\S+) -->/) || [])[1];

function ownerFor(config, repo, path) {
  const file = path.split('/').pop();
  const entry = config.repos?.[repo] || {};
  return { ...config.defaultOwner, ...(entry.default || {}), ...(entry.workflows?.[file] || {}) };
}

// Scheduled runs, plus automation dispatches on the default branch for workflows whose
// scheduled work arrives as workflow_dispatch (honua-server ci.yml via full-ci-dispatch.yml).
function isSweptRun(config, repo, run, defaultBranch) {
  const file = run.path.split('/').pop();
  if ((config.ignore?.[repo] || []).includes(file)) return false;
  if (run.event === 'schedule') return true;
  return run.event === 'workflow_dispatch'
    && run.head_branch === defaultBranch
    && /\[bot\]$/.test(run.triggering_actor?.login || '')
    && (config.botDispatchWorkflows?.[repo] || []).includes(file);
}

// Latest settled run per workflow path, newest first from the API.
function latestByWorkflow(config, repo, runs, defaultBranch) {
  const latest = new Map();
  for (const run of runs) {
    if (run.status !== 'completed' || !SETTLED.has(run.conclusion)) continue;
    if (!isSweptRun(config, repo, run, defaultBranch)) continue;
    const seen = latest.get(run.path);
    if (!seen || Date.parse(run.created_at) > Date.parse(seen.created_at)) latest.set(run.path, run);
  }
  return latest;
}

async function ensureLabel(github, owner, repo, name, cache) {
  const id = `${repo}/${name}`;
  if (cache.has(id)) return;
  try {
    await github.request('GET /repos/{owner}/{repo}/labels/{name}', { owner, repo, name });
  } catch (error) {
    if (error.status !== 404) throw error;
    await github.request('POST /repos/{owner}/{repo}/labels', {
      owner, repo, name, color: name === LABEL ? 'b60205' : 'ededed',
    });
  }
  cache.add(id);
}

// Gate and summary jobs fail because a job they need failed; they get an issue only when nothing else failed.
async function failedJobs(github, owner, repo, run, config) {
  const jobs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs', {
    owner, repo, run_id: run.id, filter: 'latest', per_page: 100,
  });
  const failed = jobs
    .filter(job => FAILED.has(job.conclusion))
    .sort((a, b) => Date.parse(a.started_at || 0) - Date.parse(b.started_at || 0));
  const downstream = new RegExp(config.downstreamJobPattern || '^$');
  const causes = failed.filter(job => !downstream.test(job.name));
  return causes.length ? causes : failed.length ? failed : [{ name: NO_JOB, html_url: run.html_url }];
}

function issueBody({ run, job, jobs, owner: lane, key, due, workflowName }) {
  const others = jobs.filter(other => other.name !== job.name).map(other => `\`${other.name}\``);
  return [
    `The scheduled workflow **${workflowName}** failed in job \`${job.name}\`.`,
    '',
    `- Run: ${run.html_url} (${run.created_at}, ${run.event}, \`${run.head_sha.slice(0, 10)}\`)`,
    `- Job: ${job.html_url}`,
    `- Owner lane: \`${lane.lane}\` (scripts/ci/scheduled-failure-owners.json in honua-server)`,
    `- Due: ${due}`,
    others.length ? `- Other failed jobs in this run: ${others.join(', ')}` : '',
    '',
    'Fix the cause, or quarantine it with a ticket. This issue closes itself when a later scheduled',
    'run of the workflow succeeds; each new failing run adds a comment.',
    '',
    markerFor(key),
  ].filter((line, index, all) => line !== '' || all[index - 1] !== '').join('\n');
}

async function closeIssues(github, owner, repo, issues, run, report) {
  for (const issue of issues) {
    await github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/comments', {
      owner, repo, issue_number: issue.number,
      body: `Scheduled run ${run.html_url} succeeded on \`${run.head_sha.slice(0, 10)}\`; closing.`,
    });
    await github.request('PATCH /repos/{owner}/{repo}/issues/{issue_number}', {
      owner, repo, issue_number: issue.number, state: 'closed', state_reason: 'completed',
    });
    report.closed.push(`${repo}#${issue.number}`);
  }
}

async function recordFailure(github, owner, repo, existing, run, job, report) {
  const comments = await github.paginate('GET /repos/{owner}/{repo}/issues/{issue_number}/comments', {
    owner, repo, issue_number: existing.number, per_page: 100,
  });
  const runMarker = `<!-- ${LABEL} run=${run.id} -->`;
  if (existing.body?.includes(run.html_url) || comments.some(comment => comment.body?.includes(runMarker))) return;
  await github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/comments', {
    owner, repo, issue_number: existing.number,
    body: `Failed again: ${run.html_url} (${run.created_at}, \`${run.head_sha.slice(0, 10)}\`), job ${job.html_url}.\n\n${runMarker}`,
  });
  report.updated.push(`${repo}#${existing.number}`);
}

async function openIssue(github, owner, repo, args, labels, report) {
  const { data: issue } = await github.request('POST /repos/{owner}/{repo}/issues', {
    owner, repo, labels,
    title: `Scheduled run failing: ${args.workflowName} / ${args.job.name}`,
    body: issueBody(args),
  });
  await github.request('POST /repos/{owner}/{repo}/issues/{issue_number}/comments', {
    owner, repo, issue_number: issue.number,
    body: `claimed: ${args.owner.lane} ${args.now.toISOString().replace(/\.\d+Z$/, 'Z')} - auto-assigned owner of ${args.run.path.split('/').pop()} (due ${args.due})`,
  });
  report.opened.push(`${repo}#${issue.number}`);
}

async function listRuns(github, owner, repo, config, since, defaultBranch) {
  const window = { owner, repo, per_page: 100, created: `>=${since}`, exclude_pull_requests: true };
  const runs = await github.paginate('GET /repos/{owner}/{repo}/actions/runs', { ...window, event: 'schedule' });
  for (const workflow_id of config.botDispatchWorkflows?.[repo] || []) {
    runs.push(...await github.paginate('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}/runs', {
      ...window, workflow_id, event: 'workflow_dispatch', branch: defaultBranch,
    }));
  }
  return runs;
}

async function sweepRepo({ github, owner, repo, defaultBranch, config, now, labels: labelCache, report }) {
  const since = new Date(now.getTime() - (config.lookbackHours || 72) * 3600 * 1000).toISOString();
  const runs = await listRuns(github, owner, repo, config, since, defaultBranch);
  const latest = latestByWorkflow(config, repo, runs, defaultBranch);
  const openIssues = (await github.paginate('GET /repos/{owner}/{repo}/issues', {
    owner, repo, state: 'open', labels: LABEL, per_page: 100,
  })).filter(issue => !issue.pull_request && keyOf(issue.body));
  for (const [path, run] of latest) {
    const mine = openIssues.filter(issue => keyOf(issue.body).startsWith(`${path}::`));
    if (run.conclusion === 'success') {
      await closeIssues(github, owner, repo, mine, run, report);
      continue;
    }
    const jobs = await failedJobs(github, owner, repo, run, config);
    const { data: workflow } = await github.request('GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}', {
      owner, repo, workflow_id: run.workflow_id,
    });
    const lane = ownerFor(config, repo, path);
    const labels = ['bug', lane.area, LABEL].filter(Boolean);
    for (const job of jobs.slice(0, config.maxIssuesPerRun || 5)) {
      const key = `${path}::${job.name.replace(/\s+/g, '_')}`;
      const existing = mine.find(issue => keyOf(issue.body) === key);
      if (existing) {
        await recordFailure(github, owner, repo, existing, run, job, report);
        continue;
      }
      for (const name of labels) await ensureLabel(github, owner, repo, name, labelCache);
      const due = new Date(now.getTime() + 24 * 3600 * 1000).toISOString().replace(/\.\d+Z$/, 'Z');
      await openIssue(github, owner, repo, {
        run, job, jobs, owner: lane, key, due, now, workflowName: workflow.name || path,
      }, labels, report);
    }
  }
}

async function sweep({ github, core, config, owner, now = new Date() }) {
  const repos = (await github.paginate('GET /installation/repositories', { per_page: 100 }))
    .filter(repo => repo.owner?.login === owner && !repo.archived && !repo.disabled)
    .filter(repo => !(config.skipRepos || []).includes(repo.name))
    .sort((a, b) => a.name.localeCompare(b.name));
  const report = { opened: [], updated: [], closed: [], errors: [] };
  const labels = new Set();
  for (const { name: repo, default_branch: defaultBranch } of repos) {
    try {
      await sweepRepo({ github, owner, repo, defaultBranch, config, now, labels, report });
    } catch (error) {
      report.errors.push(`${repo}: ${error.status || ''} ${error.message}`.trim());
      core.warning(`scheduled-failure sweep of ${repo} failed: ${error.message}`);
    }
  }
  core.info(`swept ${repos.length} repositories: ${JSON.stringify(report)}`);
  if (report.errors.length === repos.length && repos.length) throw new Error('every repository sweep failed');
  return report;
}

module.exports = { sweep, sweepRepo, latestByWorkflow, ownerFor, keyOf, markerFor, LABEL };
