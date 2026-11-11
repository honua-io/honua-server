// Both independently dispatched phases spend the SAME doctor's daily envelope.
// Reuse the pinned gh-aw accountant (including detection), never estimate missing usage as zero.
const path = require('path');

// Runs before the $25/day caps took effect were billed under the old default cap and must not
// consume the new envelope.
const BUDGET_POLICY_START = '2026-11-10T21:00:00Z';

async function main({ github, context, core, accounting, name, phase, threshold, maxCredits, apiBudget }) {
  core.setOutput('eligible', 'false');
  const budget = apiBudget || require(path.join(process.env.RUNNER_TEMP, 'gh-aw/actions/daily_aic_api_budget.cjs')).createAPIBudget();
  const artifactClient = await accounting.getArtifactClient(undefined, budget.request);
  const token = process.env.GH_TOKEN;
  const rollingStart = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
  const since = rollingStart > BUDGET_POLICY_START ? rollingStart : BUDGET_POLICY_START;
  const totals = { triage: 0, fix: 0 };
  const counts = { triage: 0, fix: 0 };
  let triageEvents = 0;
  let firstTriageAt;
  const paidFixStarts = [];
  let reserved = maxCredits;
  for (const stage of ['triage', 'fix']) {
    const workflow = `${name}${stage === 'triage' ? '-triage' : ''}.lock.yml`;
    const runs = await github.paginate(github.rest.actions.listWorkflowRuns, {
      ...context.repo, workflow_id: workflow, created: `>=${since}`, per_page: 100,
    });
    for (const run of runs) {
      if (stage === 'triage') {
        triageEvents++;
        if (run.created_at && (!firstTriageAt || run.created_at < firstTriageAt)) firstTriageAt = run.created_at;
      }
      if (run.id === context.runId) continue;
      if (run.status !== 'completed') {
        reserved += stage === 'triage' ? 15 : maxFixCredits(name);
        continue;
      }
      const inspection = { github, budget };
      const credits = await accounting.getRunAIC(artifactClient, run.id, token,
        context.repo.owner, context.repo.repo, run, inspection);
      if (!Number.isFinite(credits) || credits < 0) throw new Error(`Unknown doctor usage for run ${run.id}`);
      totals[stage] += credits;
      if (credits > 0) {
        counts[stage]++;
        if (stage === 'fix') paidFixStarts.push(run.created_at);
      }
      core.info(`doctor-credit run=${run.id} phase=${stage} credits=${credits}`);
      if (totals.triage + totals.fix + reserved > threshold) {
        await report(false, 'Daily envelope reached; remaining usage not scanned');
        return;
      }
    }
  }
  await report(totals.triage + totals.fix + reserved <= threshold, 'Complete rolling 24-hour accounting');

  async function report(eligible, reason) {
    core.setOutput('eligible', String(eligible));
    const cohortFixes = firstTriageAt ? paidFixStarts.filter(start => start >= firstTriageAt).length : 0;
    const share = triageEvents ? cohortFixes / triageEvents : 0;
    await core.summary.addHeading(`${name}: ${phase} shared daily budget`).addRaw(
      `${reason}. Cap: ${threshold} credits; triage: ${totals.triage}; fix: ${totals.fix}; ` +
      `current/in-flight reservation: ${reserved}. Admission: ${eligible}.\n\n` +
      `Paid triage runs: ${counts.triage}; paid fix runs: ${counts.fix}; triage events: ${triageEvents}; paid Opus/event share since first triage event: ${share}. ` +
      `Per-run credits are recorded in the job log. Deterministic skips spend zero model credits.\n`
    ).write();
  }
}

function maxFixCredits(name) {
  return { 'ci-doctor': 150, 'pr-review-doctor': 120, 'pr-sync-doctor': 100 }[name];
}
module.exports = { main, maxFixCredits, BUDGET_POLICY_START };
