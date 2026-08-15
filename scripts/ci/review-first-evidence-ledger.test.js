'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  INDEX_CONTRACT,
  POLICY_CONTRACT,
  QUERY_PARTITIONS_CONTRACT,
  REVIEW_GATE_WORKFLOW,
  REVIEW_GATE_WORKFLOW_NAME,
  createReviewFirstObservation,
  combineRunPartitions,
  discover,
  loadPolicy,
  retentionWindow,
  summarizeReceipts,
} = require('./review-first-evidence-ledger');

const policyDigest = 'd'.repeat(64);
const policySha = 'f'.repeat(40);
const head = 'a'.repeat(40);
const prNumber = 3216;

function policy(overrides = {}) {
  return {
    contract: POLICY_CONTRACT,
    observation_started_at: '2026-08-14T07:45:26Z',
    receipt_retention_days: 30,
    query_partition_hours: 24,
    maximum_runs_per_partition: 999,
    minimum_countable_heads: 2,
    require_zero_integrity_failures: true,
    ...overrides,
  };
}

function admissionRun(overrides = {}) {
  return {
    id: 100,
    name: 'PR Gate',
    path: '.github/workflows/pr-gate.yml',
    event: 'pull_request',
    head_sha: head,
    status: 'completed',
    conclusion: 'success',
    run_attempt: 1,
    created_at: '2026-08-14T08:00:00Z',
    updated_at: '2026-08-14T08:10:00Z',
    pull_requests: [{ number: prNumber }],
    ...overrides,
  };
}

function admissionJob(overrides = {}) {
  return {
    id: 200,
    name: 'PR Gate',
    status: 'completed',
    conclusion: 'success',
    started_at: '2026-08-14T08:00:10Z',
    completed_at: '2026-08-14T08:09:50Z',
    steps: [
      { name: 'Admission receipt', status: 'completed', conclusion: 'success', number: 1 },
    ],
    ...overrides,
  };
}

function observation(overrides = {}) {
  const observedHead = overrides.head ?? head;
  const observedPr = overrides.prNumber ?? prNumber;
  const runs = overrides.runs ?? [admissionRun({
    head_sha: observedHead,
    pull_requests: [{ number: observedPr }],
  })];
  const jobs = overrides.jobs ?? [admissionJob()];
  return createReviewFirstObservation({
    measurementPolicyDigest: overrides.measurementPolicyDigest ?? policyDigest,
    policySha,
    producerRunId: overrides.producerRunId ?? 300,
    producerRunAttempt: 1,
    producerEvent: 'workflow_run',
    observedAt: overrides.observedAt ?? '2026-08-14T08:11:00Z',
    prNumber: observedPr,
    head: observedHead,
    associatedPullNumbers: overrides.associatedPullNumbers ?? [observedPr],
    runs,
    jobs,
    decision: overrides.decision ?? {
      action: 'observe',
      reason: 'exact-head review would release expensive verification in enforce mode',
      runId: Number(runs.at(-1).id),
    },
    reviewRevalidated: overrides.reviewRevalidated ?? true,
  });
}

function indexEntry(receipt, artifactId = 400) {
  return {
    artifact_id: artifactId,
    artifact_name: `review-first-observation-${receipt.pull_request}-${receipt.head_sha}` +
      `-review-run-${receipt.producer.run_id}-attempt-${receipt.producer.run_attempt}`,
    artifact_created_at: receipt.observed_at,
    artifact_size_bytes: 1024,
    pull_request: receipt.pull_request,
    head_sha: receipt.head_sha,
    producer_run_id: receipt.producer.run_id,
    producer_run_attempt: receipt.producer.run_attempt,
    producer_event: receipt.producer.event,
    producer_created_at: '2026-08-14T08:10:30Z',
    producer_completed_at: '2026-08-14T08:12:00Z',
    producer_url: `https://github.example/actions/runs/${receipt.producer.run_id}`,
  };
}

function index(entries) {
  return {
    contract: INDEX_CONTRACT,
    workflow: { name: REVIEW_GATE_WORKFLOW_NAME, path: REVIEW_GATE_WORKFLOW },
    artifacts: entries,
    exclusions: [],
    integrity_failures: [],
  };
}

test('retention starts at observer rollout until the rolling window overtakes it', () => {
  const initial = retentionWindow(policy(), new Date('2026-08-15T00:00:00Z'));
  assert.equal(initial.receiptRetentionDays, 30);
  assert.equal(initial.runCreatedAfter, '2026-08-14T07:45:26Z');
  assert.equal(initial.runCreatedFilter, '>=2026-08-14T07:45:26Z');
  assert.deepEqual(initial.queryPartitions.partitions, [{
    index: 0,
    from: '2026-08-14T07:45:26Z',
    to: '2026-08-15T00:00:00Z',
    created_filter: '2026-08-14T07:45:26Z..2026-08-15T00:00:00Z',
  }]);
  const rolling = retentionWindow(policy(), new Date('2026-10-01T00:00:00Z'));
  assert.equal(rolling.runCreatedFilter, '>=2026-09-01T00:00:00Z');
  assert.equal(rolling.queryPartitions.partitions.length, 30);
});

test('invalid policy bounds fail closed', () => {
  assert.throws(() => loadPolicy(policy({ receipt_retention_days: 91 })), /policy bound/);
  assert.throws(() => loadPolicy(policy({ require_zero_integrity_failures: false })),
    /zero integrity/);
  assert.throws(() => loadPolicy(policy({ query_partition_hours: 25 })), /one day/);
  assert.throws(() => loadPolicy(policy({ maximum_runs_per_partition: 1_000 })),
    /search cap/);
});

test('partition catalogs combine boundary duplicates and fail below GitHub search cap', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'review-partitions-'));
  try {
    const boundaryRun = { id: 1, created_at: '2026-08-15T00:00:00Z' };
    const spec = {
      contract: QUERY_PARTITIONS_CONTRACT,
      partition_hours: 24,
      maximum_runs_per_partition: 999,
      partitions: [
        {
          index: 0,
          from: '2026-08-14T00:00:00Z',
          to: '2026-08-15T00:00:00Z',
          created_filter: '2026-08-14T00:00:00Z..2026-08-15T00:00:00Z',
        },
        {
          index: 1,
          from: '2026-08-15T00:00:00Z',
          to: '2026-08-16T00:00:00Z',
          created_filter: '2026-08-15T00:00:00Z..2026-08-16T00:00:00Z',
        },
      ],
    };
    for (const index of [0, 1]) {
      fs.writeFileSync(path.join(directory, `${index}.json`), JSON.stringify([
        { total_count: 1, workflow_runs: [boundaryRun] },
      ]));
    }
    const combined = combineRunPartitions(spec, directory);
    assert.equal(combined[0].total_count, 1);

    fs.writeFileSync(path.join(directory, '0.json'), JSON.stringify([{
      total_count: 2,
      workflow_runs: [boundaryRun, { id: 2, created_at: '2026-08-14T12:00:00Z' }],
    }]));
    assert.throws(() => combineRunPartitions({ ...spec, maximum_runs_per_partition: 1 },
      directory), /search cap/);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test('trusted observation replays the production observe decision', () => {
  const receipt = observation();
  assert.equal(receipt.decision.action, 'observe');
  assert.equal(receipt.decision.run_id, 100);
  assert.equal(receipt.mutation, 'none');
  assert.equal(receipt.review.final_review_state_revalidated, true);
});

test('observation requires final full review revalidation', () => {
  assert.throws(() => observation({ reviewRevalidated: false }),
    /final review state was not revalidated/);
});

test('workflow identities must be positive safe integers', () => {
  assert.throws(() => observation({ producerRunId: Number.MAX_SAFE_INTEGER + 1 }),
    /producer run id is invalid/);
});

test('attempt two and missing admission evidence are never observed', () => {
  assert.throws(() => observation({
    runs: [admissionRun({ run_attempt: 2 })],
    decision: { action: 'noop', reason: 'verification already reached attempt 2', runId: 100 },
  }), /production observe decision/);
  assert.throws(() => observation({
    jobs: [admissionJob({ steps: [] })],
    decision: { action: 'block', reason: 'admission receipt is missing or unsuccessful', runId: 100 },
  }), /production observe decision/);
});

test('observation must follow terminal admission evidence', () => {
  assert.throws(() => observation({ observedAt: '2026-08-14T08:09:59Z' }),
    /predates its completed admission run/);
});

test('reopened unchanged head binds the newest canonical attempt-one run', () => {
  const runs = [
    admissionRun({ id: 99, run_attempt: 2, created_at: '2026-08-14T07:00:00Z' }),
    admissionRun({ id: 101, created_at: '2026-08-14T08:01:00Z' }),
  ];
  const receipt = observation({
    runs,
    decision: {
      action: 'observe',
      reason: 'exact-head review would release expensive verification in enforce mode',
      runId: 101,
    },
  });
  assert.equal(receipt.decision.run_id, 101);
});

test('ledger counts distinct current-policy heads and never promotes itself', () => {
  const first = observation();
  const second = observation({
    head: 'b'.repeat(40),
    prNumber: 3217,
    producerRunId: 301,
    runs: [admissionRun({
      id: 101,
      head_sha: 'b'.repeat(40),
      pull_requests: [{ number: 3217 }],
    })],
    decision: {
      action: 'observe',
      reason: 'exact-head review would release expensive verification in enforce mode',
      runId: 101,
    },
  });
  const entries = [indexEntry(first, 400), indexEntry(second, 401)];
  const ledger = summarizeReceipts({
    index: index(entries),
    receiptsByArtifact: new Map([[400, first], [401, second]]),
    policy: policy(),
    currentPolicyDigest: policyDigest,
    generatedAt: '2026-08-15T00:00:00Z',
  });
  assert.equal(ledger.counts.distinct_countable_heads, 2);
  assert.equal(ledger.counts.distinct_countable_pull_requests, 2);
  assert.equal(ledger.recommendation, 'eligible-for-human-promotion-review');
  assert.equal(ledger.mutation, 'none');
  assert.equal(ledger.promotion_authority, 'none');
});

test('duplicate events count once while policy drift starts a separate cohort', () => {
  const first = observation();
  const duplicate = observation({ producerRunId: 302, observedAt: '2026-08-14T08:12:00Z' });
  const oldPolicy = observation({
    producerRunId: 303,
    observedAt: '2026-08-14T08:13:00Z',
    measurementPolicyDigest: 'e'.repeat(64),
  });
  const entries = [indexEntry(first, 400), indexEntry(duplicate, 402), indexEntry(oldPolicy, 403)];
  const ledger = summarizeReceipts({
    index: index(entries),
    receiptsByArtifact: new Map([[400, first], [402, duplicate], [403, oldPolicy]]),
    policy: policy(),
    currentPolicyDigest: policyDigest,
  });
  assert.equal(ledger.counts.distinct_countable_heads, 1);
  assert.equal(ledger.counts.duplicate_current_policy_receipts, 1);
  assert.equal(ledger.counts.noncurrent_policy_receipts, 1);
  assert.deepEqual(ledger.duplicate_heads, [head]);
  assert.equal(ledger.recommendation, 'observe-more');
});

test('a head associated with two pull requests is an integrity failure', () => {
  const first = observation();
  const second = observation({ prNumber: 4000, producerRunId: 304 });
  const entries = [indexEntry(first, 400), indexEntry(second, 404)];
  const ledger = summarizeReceipts({
    index: index(entries),
    receiptsByArtifact: new Map([[400, first], [404, second]]),
    policy: policy(),
    currentPolicyDigest: policyDigest,
  });
  assert.equal(ledger.gates.integrity_clean, false);
  assert.match(ledger.integrity_failures.at(-1).reason, /multiple pull requests/);
  assert.equal(ledger.counts.distinct_countable_heads, 0);
});

test('discovery proves pagination and selects one exact receipt artifact', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'review-ledger-'));
  try {
    const run = {
      id: 300,
      name: REVIEW_GATE_WORKFLOW_NAME,
      path: REVIEW_GATE_WORKFLOW,
      status: 'completed',
      conclusion: 'success',
      event: 'workflow_run',
      run_attempt: 1,
      created_at: '2026-08-14T08:10:30Z',
      updated_at: '2026-08-14T08:12:00Z',
      head_sha: policySha,
      html_url: 'https://github.example/actions/runs/300',
    };
    fs.writeFileSync(path.join(directory, '300.json'), JSON.stringify({
      total_count: 1,
      artifacts: [{
        id: 400,
        name: `review-first-observation-${prNumber}-${head}-review-run-300-attempt-1`,
        expired: false,
        size_in_bytes: 1024,
        created_at: '2026-08-14T08:11:30Z',
        workflow_run: { id: 300, head_sha: policySha },
      }],
    }));
    const result = discover([{ total_count: 1, workflow_runs: [run] }], directory,
      '2026-08-14T07:45:26Z');
    assert.equal(result.artifacts.length, 1);
    assert.equal(result.integrity_failures.length, 0);
    assert.throws(() => discover([
      { total_count: 2, workflow_runs: [run] },
    ], directory, '2026-08-14T07:45:26Z'), /truncated/);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
