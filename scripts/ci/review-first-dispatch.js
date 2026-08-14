'use strict';

const PR_GATE_WORKFLOW = '.github/workflows/pr-gate.yml';
const PR_GATE_JOB = 'PR Gate';
const ADMISSION_RECEIPT_STEP = 'Admission receipt';
const WAIT_FOR_REVIEW_STEP = 'Await exact-head review';
const EXPENSIVE_STEPS = new Set([
  'Free disk space',
  'Setup .NET',
  'Lean gate (build + format + fast unit/architecture smoke)',
  'Test serving-image boundary detector',
]);

function runAttempt(run) {
  return Number(run.run_attempt ?? run.runAttempt ?? 0);
}

function runHead(run) {
  return run.head_sha ?? run.headSha ?? '';
}

function runPath(run) {
  return run.path ?? '';
}

function runPullNumbers(run) {
  return (run.pull_requests ?? run.pullRequests ?? [])
    .map(pull => Number(pull.number))
    .filter(Number.isInteger);
}

function uniqueNumbers(values) {
  return [...new Set((values ?? []).map(Number).filter(Number.isInteger))];
}

function selectExactAdmissionRun({ runs, prNumber, head, associatedPullNumbers = [] }) {
  const pr = Number(prNumber);
  const associated = uniqueNumbers(associatedPullNumbers);
  const candidates = (runs ?? []).filter(run => {
    if (run.name !== 'PR Gate' || run.event !== 'pull_request') return false;
    if (runHead(run) !== head || runPath(run) !== PR_GATE_WORKFLOW) return false;

    const pullNumbers = runPullNumbers(run);
    if (pullNumbers.length > 0) return pullNumbers.includes(pr);

    // GitHub omits workflow_run.pull_requests for forks. In that case the
    // caller must independently resolve the commit association, and it must
    // identify exactly this one open PR. Anything ambiguous fails closed.
    return associated.length === 1 && associated[0] === pr;
  });

  if (candidates.length === 0) {
    return { action: 'wait', reason: 'no exact-head pull_request PR Gate run is visible yet' };
  }
  if (candidates.length !== 1) {
    return {
      action: 'block',
      reason: `expected one exact-head pull_request PR Gate run; found ${candidates.length}`,
    };
  }
  return { action: 'selected', run: candidates[0] };
}

function stepByName(job, name) {
  return (job.steps ?? []).filter(step => step.name === name);
}

function evaluateReviewFirstDispatch({
  mode,
  reviewReady,
  snapshotTruncated = false,
  runs,
  jobs = [],
  prNumber,
  head,
  associatedPullNumbers = [],
}) {
  if (!['observe', 'enforce'].includes(mode)) {
    return { action: 'block', reason: `unsupported review-first mode: ${mode}` };
  }
  if (snapshotTruncated) {
    return { action: 'block', reason: 'review snapshot is truncated' };
  }
  if (!reviewReady) {
    return { action: 'noop', reason: 'exact-head review is not ready' };
  }

  const selection = selectExactAdmissionRun({
    runs, prNumber, head, associatedPullNumbers,
  });
  if (selection.action !== 'selected') return selection;

  const run = selection.run;
  const runId = Number(run.id);
  if (run.status !== 'completed') {
    return { action: 'wait', reason: 'exact-head admission run has not completed', runId };
  }
  if (runAttempt(run) > 1) {
    return { action: 'noop', reason: `verification already reached attempt ${runAttempt(run)}`, runId };
  }
  if (runAttempt(run) !== 1) {
    return { action: 'block', reason: `invalid admission run attempt ${runAttempt(run)}`, runId };
  }

  const gateJobs = jobs.filter(job => job.name === PR_GATE_JOB);
  if (gateJobs.length !== 1) {
    return {
      action: 'block',
      reason: `expected one ${PR_GATE_JOB} job in the latest attempt; found ${gateJobs.length}`,
      runId,
    };
  }
  const job = gateJobs[0];
  const receipts = stepByName(job, ADMISSION_RECEIPT_STEP);
  if (receipts.length !== 1 || receipts[0].conclusion !== 'success') {
    return { action: 'block', reason: 'admission receipt is missing or unsuccessful', runId };
  }

  if (mode === 'observe') {
    return {
      action: 'observe',
      reason: 'exact-head review would release expensive verification in enforce mode',
      runId,
    };
  }

  if (run.conclusion !== 'failure' || job.conclusion !== 'failure') {
    return {
      action: 'block',
      reason: 'enforced admission attempt did not fail closed while awaiting review',
      runId,
    };
  }
  const waitSteps = stepByName(job, WAIT_FOR_REVIEW_STEP);
  if (waitSteps.length !== 1 || waitSteps[0].conclusion !== 'failure') {
    return { action: 'block', reason: 'review wait receipt is missing or unsuccessful', runId };
  }

  for (const step of job.steps ?? []) {
    if (EXPENSIVE_STEPS.has(step.name) && step.conclusion !== 'skipped') {
      return {
        action: 'block',
        reason: `expensive step ran during admission: ${step.name}`,
        runId,
      };
    }
  }

  return {
    action: 'rerun',
    reason: 'exact-head review released the completed admission run',
    runId,
  };
}

module.exports = {
  ADMISSION_RECEIPT_STEP,
  EXPENSIVE_STEPS,
  PR_GATE_JOB,
  PR_GATE_WORKFLOW,
  WAIT_FOR_REVIEW_STEP,
  evaluateReviewFirstDispatch,
  selectExactAdmissionRun,
};
