'use strict';

const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const {
  evaluateReviewFirstDispatch,
} = require('./review-first-dispatch');

const ARTIFACT_PATTERN = /^review-first-observation-(?<pr>[1-9][0-9]*)-(?<head>[0-9a-f]{40})-review-run-(?<run>[1-9][0-9]*)-attempt-(?<attempt>[1-9][0-9]*)$/;
const INDEX_CONTRACT = 'honua.review-first-evidence-index/v1';
const LEDGER_CONTRACT = 'honua.review-first-evidence-ledger/v1';
const OBSERVATION_CONTRACT = 'honua.review-first-observation/v1';
const POLICY_CONTRACT = 'honua.review-first-promotion-policy/v1';
const EXTRACTION_CONTRACT = 'honua.review-first-evidence-extraction/v1';
const QUERY_PARTITIONS_CONTRACT = 'honua.review-first-query-partitions/v1';
const REVIEW_GATE_WORKFLOW = '.github/workflows/review-gate.yml';
const REVIEW_GATE_WORKFLOW_NAME = 'Review Gate Attestation';
const MAX_RECEIPT_BYTES = 2 * 1024 * 1024;
const MAX_ARCHIVE_BYTES = 20 * 1024 * 1024;
const SHA = /^[0-9a-f]{40}$/;
const DIGEST = /^[0-9a-f]{64}$/;
const POLICY_INPUTS = Object.freeze([
  '.github/review-first-promotion.json',
  '.github/workflows/pr-gate.yml',
  '.github/workflows/review-gate.yml',
  'scripts/ci/review-first-dispatch.js',
  'scripts/ci/review-first-evidence-ledger.js',
  'scripts/ci/review-gate-evidence.js',
  'scripts/ci/review-gate-snapshot.js',
]);
const ALLOWED_REVIEW_GATE_EVENTS = new Set([
  'issue_comment',
  'pull_request_target',
  'repository_dispatch',
  'workflow_run',
]);

function requireObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object`);
  }
  return value;
}

function requireArray(value, label) {
  if (!Array.isArray(value)) throw new Error(`${label} must be an array`);
  return value;
}

function positiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) throw new Error(`${label} is invalid`);
  return value;
}

function exactString(value, expected, label) {
  if (value !== expected) throw new Error(`${label} is invalid`);
  return value;
}

function sha(value, label) {
  if (typeof value !== 'string' || !SHA.test(value)) throw new Error(`${label} is invalid`);
  return value;
}

function digest(value, label) {
  if (typeof value !== 'string' || !DIGEST.test(value)) throw new Error(`${label} is invalid`);
  return value;
}

function timestamp(value, label) {
  if (typeof value !== 'string' || Number.isNaN(Date.parse(value))) {
    throw new Error(`${label} is invalid`);
  }
  return value;
}

function uniquePositiveIntegers(values, label) {
  const normalized = requireArray(values, label).map((value, index) =>
    positiveInteger(value, `${label}[${index}]`));
  return [...new Set(normalized)];
}

function readJson(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

function loadPolicy(value) {
  const policy = requireObject(value, 'promotion policy');
  exactString(policy.contract, POLICY_CONTRACT, 'promotion policy contract');
  timestamp(policy.observation_started_at, 'observation start');
  const retentionDays = positiveInteger(
    policy.receipt_retention_days, 'receipt retention days');
  if (retentionDays > 90) {
    throw new Error('receipt retention days exceeds GitHub policy bound');
  }
  positiveInteger(policy.minimum_countable_heads, 'minimum countable heads');
  const partitionHours = positiveInteger(
    policy.query_partition_hours, 'query partition hours');
  if (partitionHours > 24) throw new Error('query partition hours exceeds one day');
  const maximumRuns = positiveInteger(
    policy.maximum_runs_per_partition, 'maximum runs per partition');
  if (maximumRuns >= 1_000) {
    throw new Error('maximum runs per partition must remain below GitHub search cap');
  }
  if (policy.require_zero_integrity_failures !== true) {
    throw new Error('promotion policy must require zero integrity failures');
  }
  return { ...policy };
}

function retentionWindow(policyValue, now = new Date()) {
  const policy = loadPolicy(policyValue);
  if (!(now instanceof Date) || Number.isNaN(now.getTime())) throw new Error('current time is invalid');
  const retentionStart = new Date(now.getTime() - policy.receipt_retention_days * 86_400_000);
  const observationStart = new Date(policy.observation_started_at);
  const start = retentionStart > observationStart ? retentionStart : observationStart;
  if (start.getTime() > now.getTime()) throw new Error('observation start is in the future');
  const partitions = [];
  let cursor = start;
  do {
    const end = new Date(Math.min(
      cursor.getTime() + policy.query_partition_hours * 3_600_000,
      now.getTime()));
    const from = cursor.toISOString().replace('.000Z', 'Z');
    const to = end.toISOString().replace('.000Z', 'Z');
    partitions.push({
      index: partitions.length,
      from,
      to,
      created_filter: `${from}..${to}`,
    });
    if (end.getTime() === now.getTime()) break;
    cursor = end;
  } while (partitions.length <= 90 * 24);
  if (partitions.at(-1)?.to !== now.toISOString().replace('.000Z', 'Z')) {
    throw new Error('query partition generation exceeded its safety bound');
  }
  return {
    receiptRetentionDays: policy.receipt_retention_days,
    runCreatedAfter: start.toISOString().replace('.000Z', 'Z'),
    runCreatedFilter: `>=${start.toISOString().replace('.000Z', 'Z')}`,
    queryPartitions: {
      contract: QUERY_PARTITIONS_CONTRACT,
      partition_hours: policy.query_partition_hours,
      maximum_runs_per_partition: policy.maximum_runs_per_partition,
      partitions,
    },
  };
}

function measurementPolicyDigest(root) {
  const hash = crypto.createHash('sha256');
  for (const relative of POLICY_INPUTS) {
    const bytes = fs.readFileSync(path.join(root, relative));
    hash.update(relative, 'utf8');
    hash.update('\0');
    hash.update(String(bytes.length), 'utf8');
    hash.update('\0');
    hash.update(bytes);
    hash.update('\0');
  }
  return hash.digest('hex');
}

function sanitizeRun(value) {
  const run = requireObject(value, 'admission run');
  const pullRequests = requireArray(run.pull_requests ?? [], 'admission run pull requests')
    .map((pull, index) => {
      const item = requireObject(pull, `admission run pull request ${index}`);
      return { number: positiveInteger(Number(item.number), `admission run pull request ${index}`) };
    });
  return {
    id: positiveInteger(Number(run.id), 'admission run id'),
    name: String(run.name ?? ''),
    path: String(run.path ?? ''),
    event: String(run.event ?? ''),
    head_sha: sha(run.head_sha, 'admission run head'),
    status: String(run.status ?? ''),
    conclusion: run.conclusion === null ? null : String(run.conclusion ?? ''),
    run_attempt: positiveInteger(Number(run.run_attempt), 'admission run attempt'),
    created_at: timestamp(run.created_at, 'admission run created time'),
    updated_at: timestamp(run.updated_at, 'admission run updated time'),
    pull_requests: pullRequests,
  };
}

function sanitizeJob(value) {
  const job = requireObject(value, 'admission job');
  return {
    id: positiveInteger(Number(job.id), 'admission job id'),
    name: String(job.name ?? ''),
    status: String(job.status ?? ''),
    conclusion: job.conclusion === null ? null : String(job.conclusion ?? ''),
    started_at: timestamp(job.started_at, 'admission job start time'),
    completed_at: timestamp(job.completed_at, 'admission job completion time'),
    steps: requireArray(job.steps ?? [], 'admission job steps').map((step, index) => {
      const item = requireObject(step, `admission job step ${index}`);
      return {
        name: String(item.name ?? ''),
        status: String(item.status ?? ''),
        conclusion: item.conclusion === null ? null : String(item.conclusion ?? ''),
        number: positiveInteger(Number(item.number), `admission job step ${index} number`),
      };
    }),
  };
}

function sameDecision(left, right) {
  return left?.action === right?.action && left?.reason === right?.reason &&
    Number(left?.runId ?? 0) === Number(right?.runId ?? 0);
}

function createReviewFirstObservation({
  measurementPolicyDigest: policyDigest,
  policySha,
  producerRunId,
  producerRunAttempt,
  producerEvent,
  observedAt,
  prNumber,
  head,
  associatedPullNumbers,
  runs,
  jobs,
  decision,
  reviewRevalidated,
  admissionRevalidated,
}) {
  digest(policyDigest, 'measurement policy digest');
  sha(policySha, 'policy SHA');
  positiveInteger(producerRunId, 'producer run id');
  positiveInteger(producerRunAttempt, 'producer run attempt');
  if (!ALLOWED_REVIEW_GATE_EVENTS.has(producerEvent)) throw new Error('producer event is invalid');
  timestamp(observedAt, 'observation time');
  positiveInteger(prNumber, 'pull request');
  sha(head, 'observed head');
  if (reviewRevalidated !== true) {
    throw new Error('final review state was not revalidated');
  }
  if (admissionRevalidated !== true) {
    throw new Error('final admission state was not revalidated');
  }
  const cleanRuns = requireArray(runs, 'admission runs').map(sanitizeRun);
  const cleanJobs = requireArray(jobs, 'admission jobs').map(sanitizeJob);
  const cleanAssociations = uniquePositiveIntegers(
    associatedPullNumbers, 'associated pull request numbers');
  const replay = evaluateReviewFirstDispatch({
    mode: 'observe',
    reviewReady: true,
    snapshotTruncated: false,
    runs: cleanRuns,
    jobs: cleanJobs,
    prNumber,
    head,
    associatedPullNumbers: cleanAssociations,
  });
  if (replay.action !== 'observe' || !sameDecision(replay, decision)) {
    throw new Error('observation does not replay to the production observe decision');
  }
  const selected = cleanRuns.find(run => run.id === replay.runId);
  if (!selected || Date.parse(selected.updated_at) > Date.parse(observedAt)) {
    throw new Error('observation predates its completed admission run');
  }
  return {
    contract: OBSERVATION_CONTRACT,
    mode: 'observe',
    mutation: 'none',
    measurement_policy_digest: policyDigest,
    policy_sha: policySha,
    producer: {
      workflow_name: REVIEW_GATE_WORKFLOW_NAME,
      workflow_path: REVIEW_GATE_WORKFLOW,
      event: producerEvent,
      run_id: producerRunId,
      run_attempt: producerRunAttempt,
    },
    observed_at: observedAt,
    pull_request: prNumber,
    head_sha: head,
    review: {
      ready: true,
      snapshot_truncated: false,
      final_review_state_revalidated: true,
    },
    admission: {
      final_state_revalidated: true,
      associated_pull_numbers: cleanAssociations,
      runs: cleanRuns,
      jobs: cleanJobs,
    },
    decision: {
      action: replay.action,
      reason: replay.reason,
      run_id: replay.runId,
    },
  };
}

function flattenRunPages(payload) {
  const pages = requireArray(payload, 'workflow-run pages');
  if (pages.length === 0) throw new Error('workflow-run pages are empty');
  const totals = new Set();
  const runs = [];
  for (const [index, value] of pages.entries()) {
    const page = requireObject(value, `workflow-run page ${index}`);
    if (!Number.isInteger(page.total_count) || page.total_count < 0) {
      throw new Error(`workflow-run page ${index} total is invalid`);
    }
    totals.add(page.total_count);
    runs.push(...requireArray(page.workflow_runs, `workflow-run page ${index} runs`));
  }
  if (totals.size !== 1) throw new Error('workflow-run page totals disagree');
  const ids = runs.map(run => positiveInteger(Number(run.id), 'workflow run id'));
  if (new Set(ids).size !== ids.length) throw new Error('workflow-run pages contain duplicates');
  const [total] = totals;
  if (runs.length !== total) throw new Error('workflow-run catalog is truncated');
  return runs;
}

function combineRunPartitions(specValue, pagesRoot) {
  const spec = requireObject(specValue, 'query partition specification');
  exactString(spec.contract, QUERY_PARTITIONS_CONTRACT, 'query partition contract');
  const maximumRuns = positiveInteger(
    spec.maximum_runs_per_partition, 'maximum runs per partition');
  if (maximumRuns >= 1_000) {
    throw new Error('maximum runs per partition must remain below GitHub search cap');
  }
  const partitions = requireArray(spec.partitions, 'query partitions');
  if (partitions.length === 0) throw new Error('query partitions are empty');
  const partitionHours = positiveInteger(spec.partition_hours, 'query partition hours');
  if (partitionHours > 24) throw new Error('query partition hours exceeds one day');
  const runsById = new Map();
  let priorEnd = null;
  for (const [expectedIndex, rawPartition] of partitions.entries()) {
    const partition = requireObject(rawPartition, `query partition ${expectedIndex}`);
    if (partition.index !== expectedIndex) throw new Error('query partition order is invalid');
    const fromMs = Date.parse(timestamp(partition.from, 'query partition start'));
    const toMs = Date.parse(timestamp(partition.to, 'query partition end'));
    if (fromMs > toMs || partition.created_filter !== `${partition.from}..${partition.to}`) {
      throw new Error(`query partition ${expectedIndex} bounds are invalid`);
    }
    if (toMs - fromMs > partitionHours * 3_600_000 ||
        (priorEnd !== null && partition.from !== priorEnd)) {
      throw new Error(`query partition ${expectedIndex} continuity is invalid`);
    }
    priorEnd = partition.to;
    const file = path.join(pagesRoot, `${expectedIndex}.json`);
    if (!fs.existsSync(file)) throw new Error(`query partition ${expectedIndex} is missing`);
    const runs = flattenRunPages(readJson(file));
    if (runs.length > maximumRuns) {
      throw new Error(`query partition ${expectedIndex} reached GitHub's search cap`);
    }
    for (const run of runs) {
      const createdMs = Date.parse(timestamp(run.created_at, 'workflow run creation time'));
      if (createdMs < fromMs || createdMs > toMs) {
        throw new Error(`query partition ${expectedIndex} returned an out-of-range run`);
      }
      const runId = positiveInteger(Number(run.id), 'workflow run id');
      const prior = runsById.get(runId);
      if (prior && JSON.stringify(prior) !== JSON.stringify(run)) {
        throw new Error(`workflow run ${runId} changed across query partitions`);
      }
      runsById.set(runId, run);
    }
  }
  const workflowRuns = [...runsById.values()].sort((left, right) =>
    Date.parse(left.created_at) - Date.parse(right.created_at) || Number(left.id) - Number(right.id));
  return [{ total_count: workflowRuns.length, workflow_runs: workflowRuns }];
}

function discover(runsPayload, catalogRoot, cutoff) {
  const cutoffMs = Date.parse(timestamp(cutoff, 'receipt cutoff'));
  const artifacts = [];
  const exclusions = [];
  const integrityFailures = [];
  for (const rawRun of flattenRunPages(runsPayload)) {
    const runId = Number(rawRun.id);
    const runCreatedAt = Date.parse(String(rawRun.created_at ?? ''));
    if (runCreatedAt < cutoffMs) continue;
    if (
      rawRun.name !== REVIEW_GATE_WORKFLOW_NAME ||
      rawRun.path !== REVIEW_GATE_WORKFLOW ||
      rawRun.status !== 'completed' ||
      rawRun.conclusion !== 'success' ||
      !ALLOWED_REVIEW_GATE_EVENTS.has(rawRun.event) ||
      !SHA.test(String(rawRun.head_sha ?? '')) ||
      !Number.isInteger(Number(rawRun.run_attempt)) || Number(rawRun.run_attempt) <= 0 ||
      Number.isNaN(runCreatedAt) || Number.isNaN(Date.parse(String(rawRun.updated_at ?? '')))
    ) {
      integrityFailures.push({ producer_run_id: runId, reason: 'producer workflow run is invalid' });
      continue;
    }
    const catalogFile = path.join(catalogRoot, `${runId}.json`);
    if (!fs.existsSync(catalogFile)) {
      integrityFailures.push({ producer_run_id: runId, reason: 'artifact-catalog-missing' });
      continue;
    }
    let catalog;
    try {
      catalog = requireObject(readJson(catalogFile), `artifact catalog ${runId}`);
      requireArray(catalog.artifacts, `artifact catalog ${runId} artifacts`);
      if (Number(catalog.total_count) !== catalog.artifacts.length) {
        throw new Error('artifact catalog is truncated');
      }
    } catch (error) {
      integrityFailures.push({ producer_run_id: runId, reason: error.message });
      continue;
    }
    const matches = catalog.artifacts.filter(artifact => {
      if (!artifact || artifact.expired !== false || typeof artifact.name !== 'string') return false;
      const match = ARTIFACT_PATTERN.exec(artifact.name);
      return match && Number(match.groups.run) === runId &&
        Number(match.groups.attempt) === Number(rawRun.run_attempt);
    });
    if (matches.length === 0) {
      exclusions.push({ producer_run_id: runId, reason: 'observation-receipt-not-emitted' });
      continue;
    }
    if (matches.length !== 1) {
      integrityFailures.push({ producer_run_id: runId, reason: 'observation-receipt-ambiguous' });
      continue;
    }
    const artifact = matches[0];
    const match = ARTIFACT_PATTERN.exec(artifact.name);
    try {
      positiveInteger(Number(artifact.id), 'artifact id');
      if (!Number.isInteger(artifact.size_in_bytes) || artifact.size_in_bytes <= 0 ||
          artifact.size_in_bytes > MAX_ARCHIVE_BYTES) {
        throw new Error('artifact size is invalid');
      }
      const artifactCreatedAt = timestamp(artifact.created_at, 'artifact creation time');
      if (Number(artifact.workflow_run?.id) !== runId) {
        throw new Error('artifact workflow run does not match producer');
      }
      if (artifact.workflow_run?.head_sha !== rawRun.head_sha) {
        throw new Error('artifact workflow head does not match producer');
      }
      const artifactCreatedMs = Date.parse(artifactCreatedAt);
      if (artifactCreatedMs < runCreatedAt ||
          artifactCreatedMs > Date.parse(rawRun.updated_at) + 300_000) {
        throw new Error('artifact creation time is outside producer run');
      }
    } catch (error) {
      integrityFailures.push({ producer_run_id: runId, reason: error.message });
      continue;
    }
    artifacts.push({
      artifact_id: Number(artifact.id),
      artifact_name: artifact.name,
      artifact_created_at: artifact.created_at,
      artifact_size_bytes: artifact.size_in_bytes,
      pull_request: Number(match.groups.pr),
      head_sha: match.groups.head,
      producer_run_id: runId,
      producer_run_attempt: Number(rawRun.run_attempt),
      producer_event: rawRun.event,
      producer_created_at: rawRun.created_at,
      producer_completed_at: rawRun.updated_at,
      producer_url: rawRun.html_url,
    });
  }
  return {
    contract: INDEX_CONTRACT,
    workflow: { name: REVIEW_GATE_WORKFLOW_NAME, path: REVIEW_GATE_WORKFLOW },
    artifacts,
    exclusions,
    integrity_failures: integrityFailures,
  };
}

function validateIndex(indexValue) {
  const index = requireObject(indexValue, 'evidence index');
  exactString(index.contract, INDEX_CONTRACT, 'evidence index contract');
  const workflow = requireObject(index.workflow, 'evidence index workflow');
  exactString(workflow.name, REVIEW_GATE_WORKFLOW_NAME, 'evidence index workflow name');
  exactString(workflow.path, REVIEW_GATE_WORKFLOW, 'evidence index workflow path');
  index.artifacts = requireArray(index.artifacts, 'evidence index artifacts')
    .map((entry, itemIndex) => {
      const value = requireObject(entry, `evidence index artifact ${itemIndex}`);
      const artifactId = positiveInteger(
        Number(value.artifact_id), `evidence index artifact ${itemIndex} id`);
      const producerRunId = positiveInteger(
        Number(value.producer_run_id), `evidence index artifact ${itemIndex} producer run`);
      const producerRunAttempt = positiveInteger(
        Number(value.producer_run_attempt),
        `evidence index artifact ${itemIndex} producer attempt`);
      const match = ARTIFACT_PATTERN.exec(String(value.artifact_name ?? ''));
      if (!match || Number(match.groups.run) !== producerRunId ||
          Number(match.groups.attempt) !== producerRunAttempt) {
        throw new Error(`evidence index artifact ${itemIndex} name is invalid`);
      }
      if (Number(match.groups.pr) !== Number(value.pull_request) ||
          match.groups.head !== value.head_sha) {
        throw new Error(`evidence index artifact ${itemIndex} identity is inconsistent`);
      }
      if (!ALLOWED_REVIEW_GATE_EVENTS.has(value.producer_event)) {
        throw new Error(`evidence index artifact ${itemIndex} event is invalid`);
      }
      timestamp(value.producer_created_at,
        `evidence index artifact ${itemIndex} producer creation time`);
      timestamp(value.producer_completed_at,
        `evidence index artifact ${itemIndex} producer completion time`);
      return {
        ...value,
        artifact_id: artifactId,
        producer_run_id: producerRunId,
        producer_run_attempt: producerRunAttempt,
        pull_request: positiveInteger(
          Number(value.pull_request), `evidence index artifact ${itemIndex} pull request`),
        head_sha: sha(value.head_sha, `evidence index artifact ${itemIndex} head`),
      };
    });
  if (new Set(index.artifacts.map(entry => entry.artifact_id)).size !== index.artifacts.length) {
    throw new Error('evidence index artifact IDs are duplicated');
  }
  requireArray(index.exclusions, 'evidence index exclusions');
  requireArray(index.integrity_failures, 'evidence index integrity failures');
  return index;
}

function extractReceipts(indexValue, archivesRoot, receiptsRoot) {
  const index = validateIndex(indexValue);
  fs.mkdirSync(receiptsRoot, { recursive: true });
  const failures = [];
  const extracted = [];
  for (const entry of index.artifacts) {
    const archive = path.join(archivesRoot, `${entry.artifact_id}.zip`);
    try {
      const archiveSize = fs.statSync(archive).size;
      if (archiveSize <= 0 || archiveSize > MAX_ARCHIVE_BYTES) {
        throw new Error('receipt archive size is invalid');
      }
      const listing = execFileSync('unzip', ['-Z1', archive], {
        encoding: 'utf8',
        maxBuffer: 64 * 1024,
      }).split(/\r?\n/).filter(Boolean);
      if (listing.length !== 1 || listing[0] !== 'review-first-observation.json') {
        throw new Error('receipt archive member set is invalid');
      }
      const bytes = execFileSync(
        'unzip', ['-p', archive, 'review-first-observation.json'],
        { encoding: null, maxBuffer: MAX_RECEIPT_BYTES + 1 });
      if (bytes.length <= 0 || bytes.length > MAX_RECEIPT_BYTES) {
        throw new Error('observation receipt size is invalid');
      }
      JSON.parse(bytes.toString('utf8'));
      const output = path.join(receiptsRoot, `${entry.artifact_id}.json`);
      fs.writeFileSync(output, bytes);
      extracted.push({ artifact_id: entry.artifact_id, receipt: path.basename(output) });
    } catch (error) {
      failures.push({
        artifact_id: entry.artifact_id,
        producer_run_id: entry.producer_run_id,
        reason: error.message,
      });
    }
  }
  return {
    contract: EXTRACTION_CONTRACT,
    extracted,
    integrity_failures: failures,
  };
}

function validateExtraction(value) {
  const extraction = requireObject(value, 'extraction report');
  exactString(extraction.contract, EXTRACTION_CONTRACT, 'extraction report contract');
  requireArray(extraction.extracted, 'extracted receipts');
  requireArray(extraction.integrity_failures, 'extraction integrity failures');
  return extraction;
}

function validateObservation(receiptValue, entry, currentPolicyDigest) {
  const receipt = requireObject(receiptValue, 'observation receipt');
  exactString(receipt.contract, OBSERVATION_CONTRACT, 'observation contract');
  exactString(receipt.mode, 'observe', 'observation mode');
  exactString(receipt.mutation, 'none', 'observation mutation');
  digest(receipt.measurement_policy_digest, 'observation policy digest');
  sha(receipt.policy_sha, 'observation policy SHA');
  timestamp(receipt.observed_at, 'observation time');
  positiveInteger(receipt.pull_request, 'observation pull request');
  sha(receipt.head_sha, 'observation head');
  if (receipt.pull_request !== entry.pull_request || receipt.head_sha !== entry.head_sha) {
    throw new Error('observation identity does not match artifact name');
  }
  const producer = requireObject(receipt.producer, 'observation producer');
  exactString(producer.workflow_name, REVIEW_GATE_WORKFLOW_NAME, 'producer workflow name');
  exactString(producer.workflow_path, REVIEW_GATE_WORKFLOW, 'producer workflow path');
  if (producer.event !== entry.producer_event ||
      producer.run_id !== entry.producer_run_id ||
      producer.run_attempt !== entry.producer_run_attempt) {
    throw new Error('observation producer does not match workflow run');
  }
  const review = requireObject(receipt.review, 'review evidence');
  if (review.ready !== true || review.snapshot_truncated !== false ||
      review.final_review_state_revalidated !== true) {
    throw new Error('review evidence is not an exact complete snapshot');
  }
  const admission = requireObject(receipt.admission, 'admission evidence');
  if (admission.final_state_revalidated !== true) {
    throw new Error('admission evidence is not a final revalidated snapshot');
  }
  const replay = evaluateReviewFirstDispatch({
    mode: 'observe',
    reviewReady: true,
    snapshotTruncated: false,
    runs: requireArray(admission.runs, 'admission runs').map(sanitizeRun),
    jobs: requireArray(admission.jobs, 'admission jobs').map(sanitizeJob),
    prNumber: receipt.pull_request,
    head: receipt.head_sha,
    associatedPullNumbers: uniquePositiveIntegers(
      admission.associated_pull_numbers, 'associated pull request numbers'),
  });
  const decision = requireObject(receipt.decision, 'observation decision');
  const recorded = {
    action: decision.action,
    reason: decision.reason,
    runId: Number(decision.run_id),
  };
  if (replay.action !== 'observe' || !sameDecision(replay, recorded)) {
    throw new Error('receipt does not replay to the production observe decision');
  }
  const observedMs = Date.parse(receipt.observed_at);
  const producerCreatedMs = Date.parse(entry.producer_created_at);
  const producerCompletedMs = Date.parse(entry.producer_completed_at);
  if (observedMs < producerCreatedMs || observedMs > producerCompletedMs + 300_000) {
    throw new Error('observation time is outside its producer run');
  }
  const selected = admission.runs.find(run => Number(run.id) === replay.runId);
  if (!selected || observedMs < Date.parse(selected.updated_at)) {
    throw new Error('observation predates its completed admission run');
  }
  return {
    pull_request: receipt.pull_request,
    head_sha: receipt.head_sha,
    observed_at: receipt.observed_at,
    admission_run_id: replay.runId,
    admission_run_attempt: Number(selected.run_attempt),
    admission_created_at: selected.created_at,
    admission_completed_at: selected.updated_at,
    admission_to_observation_ms: observedMs - Date.parse(selected.created_at),
    producer_run_id: entry.producer_run_id,
    producer_run_attempt: entry.producer_run_attempt,
    producer_policy_sha: receipt.policy_sha,
    measurement_policy_digest: receipt.measurement_policy_digest,
    current_policy: receipt.measurement_policy_digest === currentPolicyDigest,
  };
}

function nearestRank(values, percentile) {
  if (values.length === 0) return null;
  const sorted = [...values].sort((left, right) => left - right);
  return sorted[Math.max(0, Math.ceil(percentile * sorted.length) - 1)];
}

function summarizeReceipts({ index: indexValue, receiptsByArtifact, policy: policyValue,
  currentPolicyDigest, extractionFailures = [], generatedAt = new Date().toISOString() }) {
  const index = validateIndex(indexValue);
  const policy = loadPolicy(policyValue);
  digest(currentPolicyDigest, 'current measurement policy digest');
  timestamp(generatedAt, 'ledger generation time');
  const integrityFailures = [...index.integrity_failures, ...extractionFailures];
  const extractionFailureArtifacts = new Set(
    extractionFailures.map(failure => Number(failure?.artifact_id)).filter(Number.isSafeInteger));
  const observations = [];
  for (const entry of index.artifacts) {
    const receipt = receiptsByArtifact.get(entry.artifact_id);
    if (receipt === undefined) {
      if (!extractionFailureArtifacts.has(entry.artifact_id)) {
        integrityFailures.push({
          artifact_id: entry.artifact_id,
          producer_run_id: entry.producer_run_id,
          reason: 'observation receipt was not extracted',
        });
      }
      continue;
    }
    try {
      observations.push(validateObservation(receipt, entry, currentPolicyDigest));
    } catch (error) {
      integrityFailures.push({
        artifact_id: entry.artifact_id,
        producer_run_id: entry.producer_run_id,
        reason: error.message,
      });
    }
  }
  const current = observations.filter(item => item.current_policy);
  const grouped = new Map();
  for (const observation of current) {
    const values = grouped.get(observation.head_sha) ?? [];
    values.push(observation);
    grouped.set(observation.head_sha, values);
  }
  const countable = [];
  const duplicateHeads = [];
  for (const [head, values] of grouped) {
    const prs = new Set(values.map(item => item.pull_request));
    if (prs.size !== 1) {
      integrityFailures.push({ head_sha: head, reason: 'exact head maps to multiple pull requests' });
      continue;
    }
    values.sort((left, right) =>
      Date.parse(left.observed_at) - Date.parse(right.observed_at) ||
      left.producer_run_id - right.producer_run_id);
    countable.push(values[0]);
    if (values.length > 1) duplicateHeads.push(head);
  }
  countable.sort((left, right) => left.head_sha.localeCompare(right.head_sha));
  const sampleReady = countable.length >= policy.minimum_countable_heads;
  const integrityClean = integrityFailures.length === 0;
  return {
    contract: LEDGER_CONTRACT,
    mode: 'report-only',
    mutation: 'none',
    promotion_authority: 'none',
    generated_at: generatedAt,
    measurement_policy_digest: currentPolicyDigest,
    measurement_policy_inputs: [...POLICY_INPUTS],
    recommendation: sampleReady && integrityClean
      ? 'eligible-for-human-promotion-review'
      : 'observe-more',
    thresholds: policy,
    counts: {
      discovered_artifacts: index.artifacts.length,
      validated_receipts: observations.length,
      current_policy_receipts: current.length,
      noncurrent_policy_receipts: observations.length - current.length,
      distinct_countable_heads: countable.length,
      distinct_countable_pull_requests: new Set(
        countable.map(item => item.pull_request)).size,
      duplicate_current_policy_receipts: current.length - countable.length,
      excluded_successful_review_runs: index.exclusions.length,
      integrity_failures: integrityFailures.length,
    },
    latency: {
      p50_admission_to_observation_ms: nearestRank(
        countable.map(item => item.admission_to_observation_ms), 0.50),
      p90_admission_to_observation_ms: nearestRank(
        countable.map(item => item.admission_to_observation_ms), 0.90),
    },
    gates: {
      sample_ready: sampleReady,
      integrity_clean: integrityClean,
    },
    countable_observations: countable,
    duplicate_heads: duplicateHeads.sort(),
    noncurrent_policy_observations: observations.filter(item => !item.current_policy),
    discovery_exclusions: index.exclusions,
    integrity_failures: integrityFailures,
  };
}

function markdown(ledger) {
  const counts = ledger.counts;
  const latency = ledger.latency;
  const metric = value => value === null ? '`n/a`' : `\`${value}\` ms`;
  return [
    '# Review-first promotion evidence ledger',
    '',
    `Recommendation: **${ledger.recommendation}** (report-only; no promotion authority)`,
    '',
    `- Distinct countable exact heads: \`${counts.distinct_countable_heads}\` / ` +
      `\`${ledger.thresholds.minimum_countable_heads}\``,
    `- Distinct pull requests represented: \`${counts.distinct_countable_pull_requests}\``,
    `- Current/noncurrent policy receipts: \`${counts.current_policy_receipts}\` / ` +
      `\`${counts.noncurrent_policy_receipts}\``,
    `- Successful Review Gate runs without an observation receipt: ` +
      `\`${counts.excluded_successful_review_runs}\``,
    `- Duplicate current-policy receipts: \`${counts.duplicate_current_policy_receipts}\``,
    `- Integrity failures: \`${counts.integrity_failures}\``,
    `- p50 admission-to-observation: ${metric(latency.p50_admission_to_observation_ms)}`,
    `- p90 admission-to-observation: ${metric(latency.p90_admission_to_observation_ms)}`,
    '',
    '| Gate | Ready |',
    '|---|---|',
    ...Object.entries(ledger.gates).map(([name, ready]) =>
      `| \`${name}\` | \`${String(ready)}\` |`),
    '',
    'Only immutable receipts emitted by trusted Review Gate policy are countable. ' +
      'The ledger never changes mode, statuses, workflow runs, labels, or merge state.',
    '',
  ].join('\n');
}

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const values = {};
  for (let index = 0; index < rest.length; index += 2) {
    const name = rest[index];
    if (!name?.startsWith('--') || rest[index + 1] === undefined) {
      throw new Error(`invalid argument near ${name ?? '<end>'}`);
    }
    values[name.slice(2)] = rest[index + 1];
  }
  return { command, values };
}

function appendOutput(file, values) {
  if (!file) return;
  fs.appendFileSync(file,
    Object.entries(values).map(([name, value]) => `${name}=${value}\n`).join(''), 'utf8');
}

function main(argv = process.argv.slice(2)) {
  const { command, values } = parseArgs(argv);
  if (command === 'policy-digest') {
    const root = path.resolve(values.root);
    const policySha = sha(values['policy-sha'], 'policy SHA');
    const policyDigest = measurementPolicyDigest(root);
    appendOutput(values['github-output'], {
      measurement_policy_digest: policyDigest,
      policy_sha: policySha,
    });
    process.stdout.write(`${policyDigest}\n`);
    return 0;
  }
  if (command === 'retention-window') {
    const result = retentionWindow(readJson(values.policy));
    appendOutput(values['github-output'], {
      receipt_retention_days: result.receiptRetentionDays,
      run_created_after: result.runCreatedAfter,
      run_created_filter: result.runCreatedFilter,
    });
    if (values.output) writeJson(values.output, result.queryPartitions);
    process.stdout.write(`${result.runCreatedFilter}\n`);
    return 0;
  }
  if (command === 'combine-runs') {
    writeJson(values.output,
      combineRunPartitions(readJson(values.partitions), values.pages));
    return 0;
  }
  if (command === 'discover') {
    const result = discover(
      readJson(values.runs), values.catalog, values.cutoff);
    writeJson(values.output, result);
    return 0;
  }
  if (command === 'extract') {
    const result = extractReceipts(
      readJson(values.index), values.archives, values.receipts);
    writeJson(values.output, result);
    return 0;
  }
  if (command === 'summarize') {
    const index = readJson(values.index);
    const extraction = fs.existsSync(values.extraction)
      ? validateExtraction(readJson(values.extraction))
      : { integrity_failures: [{ reason: 'extraction report is missing' }] };
    const receiptsByArtifact = new Map();
    for (const entry of index.artifacts ?? []) {
      const file = path.join(values.receipts, `${entry.artifact_id}.json`);
      if (fs.existsSync(file)) receiptsByArtifact.set(entry.artifact_id, readJson(file));
    }
    const ledger = summarizeReceipts({
      index,
      receiptsByArtifact,
      policy: readJson(values.policy),
      currentPolicyDigest: values['policy-digest'],
      extractionFailures: extraction.integrity_failures,
    });
    writeJson(values.output, ledger);
    fs.writeFileSync(values.markdown, markdown(ledger), 'utf8');
    process.stdout.write(markdown(ledger));
    return ledger.gates.integrity_clean ? 0 : 1;
  }
  throw new Error(`unsupported command: ${command ?? '<missing>'}`);
}

if (require.main === module) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

module.exports = {
  ARTIFACT_PATTERN,
  INDEX_CONTRACT,
  LEDGER_CONTRACT,
  OBSERVATION_CONTRACT,
  POLICY_CONTRACT,
  POLICY_INPUTS,
  QUERY_PARTITIONS_CONTRACT,
  REVIEW_GATE_WORKFLOW,
  REVIEW_GATE_WORKFLOW_NAME,
  createReviewFirstObservation,
  combineRunPartitions,
  discover,
  extractReceipts,
  loadPolicy,
  markdown,
  measurementPolicyDigest,
  retentionWindow,
  sanitizeJob,
  sanitizeRun,
  summarizeReceipts,
  validateObservation,
  validateExtraction,
};
