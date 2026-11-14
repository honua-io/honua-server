'use strict';

// GitHub-managed Code Quality has no repository YAML concurrency setting.
const QUALITY_PATH = 'dynamic/github-code-quality/codeql';
const ACTIVE = new Set(['queued', 'in_progress', 'waiting', 'pending', 'requested']);

function subject(payload) {
  if (payload.pull_request) return payload.pull_request.number;
  const run = payload.workflow_run;
  if (run?.path !== QUALITY_PATH) return null;
  const match = /^refs\/pull\/([1-9]\d*)\/head$/.exec(run.head_branch || '');
  return match ? Number(match[1]) : null;
}

function superseded(run, branch, head) {
  return run.path === QUALITY_PATH && run.head_branch === branch &&
    ACTIVE.has(run.status) && Boolean(run.head_sha) && run.head_sha !== head;
}

async function cancelSuperseded({github, context, core}) {
  const number = subject(context.payload);
  if (!Number.isSafeInteger(number) || number < 1) return;
  const branch = `refs/pull/${number}/head`;
  const request = {...context.repo, pull_number: number};
  const readHead = async () => {
    const {data: pull} = await github.rest.pulls.get(request);
    if (pull.base.repo.full_name !== `${context.repo.owner}/${context.repo.repo}`) {
      throw new Error('Code Quality subject belongs to a different repository');
    }
    return pull.head.sha;
  };
  let head = await readHead();
  const runs = await github.paginate(github.rest.actions.listWorkflowRunsForRepo,
    {...context.repo, branch, per_page: 100});
  for (const run of runs) {
    if (!superseded(run, branch, head)) continue;
    // Events can arrive out of order; protect the live head, not the event SHA.
    head = await readHead();
    if (!superseded(run, branch, head)) continue;
    try {
      await github.rest.actions.cancelWorkflowRun({...context.repo, run_id: run.id});
      core.info(`Cancelled superseded Code Quality run ${run.id} for PR #${number}`);
    } catch (error) {
      if (error.status !== 409) throw error; // A completed run needs no cancellation.
      core.info(`Code Quality run ${run.id} already finished`);
    }
  }
}

module.exports = {QUALITY_PATH, subject, superseded, cancelSuperseded};
