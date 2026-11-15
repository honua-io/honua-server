/** Coalesce analysis runs without executing pull-request code. */
const CODEQL_PATH = '.github/workflows/codeql.yml';
const ANALYSIS_PATHS = new Set([CODEQL_PATH, 'dynamic/github-code-quality/codeql']);
const ACTIVE = ['queued', 'in_progress', 'waiting', 'pending', 'requested'];

function prNumber(run) {
  const branch = /^refs\/pull\/([1-9]\d*)\/head$/.exec(run.head_branch || '');
  return run.pull_requests?.[0]?.number || (branch ? Number(branch[1]) : undefined);
}

function shouldCancel(run, peers, pull) {
  if (!ANALYSIS_PATHS.has(run.path) || !ACTIVE.includes(run.status)) return false;
  if (pull.state !== 'open' || pull.head.sha !== run.head_sha) return true;
  // Managed analysis has no documented ready-for-review trigger; retain its draft coverage.
  if (pull.draft && run.path === CODEQL_PATH) return true;
  return peers.some(peer => peer.id !== run.id && peer.path === run.path &&
    prNumber(peer) === prNumber(run) && peer.head_sha === run.head_sha &&
    // Native CodeQL concurrency may already be cancelling an active peer when
    // an older run ID is retried; only completed evidence can replace that retry.
    (peer.evidenceComplete || (run.path !== CODEQL_PATH &&
      peer.id > run.id && ACTIVE.includes(peer.status))));
}

async function completedEvidence(client, run) {
  if (run.status !== 'completed' || run.conclusion !== 'success') return false;
  const jobs = await client.jobs(run.id);
  const analyses = jobs.filter(job => /analy[sz]e/i.test(job.name));
  if (!analyses.length || !analyses.every(job => job.conclusion === 'success')) return false;
  const fresh = await client.run(run.id);
  return fresh.status === 'completed' && fresh.conclusion === 'success' &&
    fresh.run_attempt === run.run_attempt && fresh.head_sha === run.head_sha;
}

function apiClient(github, repo) {
  return {
    run: async id => (await github.rest.actions.getWorkflowRun({ ...repo, run_id: id })).data,
    pull: async number => {
      const {data: pull} = await github.rest.pulls.get({ ...repo, pull_number: number });
      if (pull.base.repo.full_name !== `${repo.owner}/${repo.repo}`) {
        throw new Error('Analysis subject belongs to a different repository');
      }
      return pull;
    },
    runs: options => github.paginate(github.rest.actions.listWorkflowRuns, { ...repo, per_page: 100, ...options }),
    jobs: id => github.paginate(github.rest.actions.listJobsForWorkflowRun, {
      ...repo, run_id: id, filter: 'latest', per_page: 100,
    }),
    cancel: id => github.rest.actions.cancelWorkflowRun({ ...repo, run_id: id }),
  };
}

async function replacementRuns(client, candidate, peers) {
  const replacements = [];
  for (const peer of peers) {
    if (peer.id === candidate.id || peer.path !== candidate.path ||
        peer.head_sha !== candidate.head_sha || prNumber(peer) !== prNumber(candidate)) continue;
    const fresh = await client.run(peer.id);
    fresh.evidenceComplete = await completedEvidence(client, fresh);
    replacements.push(fresh);
  }
  return replacements;
}

async function reconcile(client, source, core) {
  if (!ANALYSIS_PATHS.has(source.path)) return;
  const run = await client.run(source.id);
  if (run.path !== source.path) throw new Error('Analysis workflow changed identity');
  const number = prNumber(run);
  if (!number) return; // Keep trunk, weekly and manual analysis.
  const options = { workflow_id: run.workflow_id, head_sha: run.head_sha };
  const successful = await client.runs({ ...options, status: 'success' });
  const active = [];
  for (const status of ACTIVE) {
    active.push(...await client.runs({ workflow_id: run.workflow_id, branch: run.head_branch, status }));
  }
  const peers = [...new Map([...successful, ...active].map(peer => [peer.id, peer])).values()];
  for (const candidate of active) {
    if (prNumber(candidate) !== number || candidate.path !== run.path) continue;
    const replacements = await replacementRuns(client, candidate, peers);
    const fresh = await client.run(candidate.id);
    const pull = await client.pull(number);
    if (!shouldCancel(fresh, replacements, pull)) continue;
    try {
      await client.cancel(fresh.id);
      core.notice(`Cancelled superseded analysis run ${fresh.id}`);
    } catch (error) {
      if (error.status !== 409) throw error;
    }
  }
}

async function admit({ github, context, core }) {
  await reconcile(apiClient(github, context.repo), context.payload.workflow_run, core);
}

module.exports = { ANALYSIS_PATHS, prNumber, shouldCancel, completedEvidence, reconcile, admit };
