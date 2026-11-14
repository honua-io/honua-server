'use strict';

const CONSUMER = '.github/workflows/normalize-derived-artifacts-consumer.yml';
const ACTIVE = new Set(['queued', 'in_progress', 'waiting', 'pending', 'requested']);
const TITLE = /^Normalization producer ([1-9]\d*) attempt ([1-9]\d*)$/;

function olderGeneration(candidate, producer) {
  const match = TITLE.exec(candidate.display_title || '');
  if (!match || candidate.path !== CONSUMER || !ACTIVE.has(candidate.status)) return null;
  const id = Number(match[1]), attempt = Number(match[2]);
  if (!Number.isSafeInteger(id) || !Number.isSafeInteger(attempt)) return null;
  return id < producer.id || (id === producer.id && attempt < producer.run_attempt) ? id : null;
}

async function samePullRequest({github, context}, producerId, number) {
  const {data: producer} = await github.rest.actions.getWorkflowRun({
    ...context.repo, run_id: producerId,
  });
  if (producer.path !== '.github/workflows/normalize-derived-artifacts.yml' ||
      producer.event !== 'pull_request' ||
      producer.repository?.full_name !== `${context.repo.owner}/${context.repo.repo}`) return false;
  // Ambiguous or absent associations cannot authorize cancellation.
  return producer.pull_requests?.length === 1 && producer.pull_requests[0].number === number;
}

async function cancelOlderConsumers({github, context, core}, number) {
  const producer = context.payload.workflow_run;
  const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
    ...context.repo, workflow_id: 'normalize-derived-artifacts-consumer.yml', per_page: 100,
  });
  for (const candidate of runs) {
    const producerId = olderGeneration(candidate, producer);
    if (!producerId || !await samePullRequest({github, context}, producerId, number)) continue;
    try {
      await github.rest.actions.cancelWorkflowRun({...context.repo, run_id: candidate.id});
      core.info(`Cancelled older normalization consumer ${candidate.id} for PR #${number}`);
    } catch (error) {
      if (error.status !== 409) throw error;
      core.info(`Normalization consumer ${candidate.id} already completed`);
    }
  }
}

module.exports = {olderGeneration, cancelOlderConsumers};
