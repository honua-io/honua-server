const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

// Exercise the actual github-script body so workflow edits retain the boundary.
const workflow = fs.readFileSync(path.join(__dirname, '../../.github/workflows/security-nightly.yml'), 'utf8');
const step = workflow.split('      - name: File findings in the private security tracker\n')[1]
  .split('      - name: Open a public maintenance issue')[0];
const script = step.split('          script: |\n')[1]
  .split('\n').map(line => line.replace(/^ {12}/, '')).join('\n');
const execute = new (Object.getPrototypeOf(async function () {}).constructor)('require', 'process', 'github', script);

async function report({ destination = 'owner/tracker', repository = { private: true }, lookupError, duplicate = false, existing, findings } = {}) {
  const calls = [];
  const finding = { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1', PkgName: 'test-package', Severity: 'HIGH', dueBy: '2026-12-01' };
  const title = '[release image] test-release linux/amd64: TEST-1 in test-package';
  const github = {
    rest: {
      repos: { get: async args => {
        calls.push(['lookup', args]);
        if (lookupError) throw lookupError;
        return { data: repository };
      } },
      issues: { listForRepo: 'list',
        create: async args => { calls.push(['create', args]); return { data: { ...args, number: 7, created_at: '2026-11-01T12:00:00Z' } }; },
        update: async args => { calls.push(['update', args]); return { data: args }; } },
    },
    paginate: async () => { calls.push(['list']); return existing || (duplicate ? [{ title, number: 7, created_at: '2026-11-01T12:00:00Z', body: 'Advisory: TEST-1 (HIGH)\nFix due by: 2026-12-01 (SECURITY.md: critical 7 days, high 30 days from detection)\nFirst detected: 2026-11-01' }] : []); },
  };
  const requireMock = name => {
    assert.equal(name, 'fs');
    return { readFileSync: () => {
      calls.push(['read']);
      return JSON.stringify({ findings: findings || [finding, finding] });
    } };
  };
  let error;
  try {
    await execute(requireMock, { env: { SECURITY_FINDINGS_REPO: destination } }, github);
  } catch (caught) { error = caught; }
  return { calls, error };
}

test('private destination receives one issue per finding, including within-run deduplication', async () => {
  const { calls, error } = await report();
  assert.equal(error, undefined);
  assert.deepEqual(calls.map(call => call[0]), ['lookup', 'read', 'list', 'create']);
  assert.equal(calls[3][1].owner, 'owner');
  assert.equal(calls[3][1].repo, 'tracker');
});

test('existing private findings are not filed again', async () => {
  const { calls, error } = await report({ duplicate: true });
  assert.equal(error, undefined);
  assert.deepEqual(calls.map(call => call[0]), ['lookup', 'read', 'list']);
});

test('public or unconfirmed visibility fails before reading findings or accessing issues', async () => {
  for (const repository of [{ private: false }, {}, { private: 'true' }]) {
    const { calls, error } = await report({ repository });
    assert.match(error.message, /private destination/);
    assert.deepEqual(calls.map(call => call[0]), ['lookup']);
  }
});

test('repository lookup failure fails closed', async () => {
  const { calls, error } = await report({ lookupError: new Error('lookup failed') });
  assert.match(error.message, /lookup failed/);
  assert.deepEqual(calls.map(call => call[0]), ['lookup']);
});

test('malformed destination fails before any API request or finding read', async () => {
  for (const destination of ['', 'owner', 'owner/repo/extra', '/repo', 'owner/repo\n']) {
    const { calls, error } = await report({ destination });
    assert.match(error.message, /owner\/repository/);
    assert.deepEqual(calls, []);
  }
});

const existingTitle = '[release image] test-release linux/amd64: TEST-1 in test-package';
function priorFinding(severity = 'HIGH', due = '2026-12-01') {
  return { title: existingTitle, number: 7, created_at: '2026-11-01T12:00:00Z',
    body: `Advisory: TEST-1 (${severity})\nFix due by: ${due} (SECURITY.md: critical 7 days, high 30 days from detection)\nDetected by: original-run` };
}
function currentFinding(severity, dueBy) {
  return { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1',
    PkgName: 'test-package', Severity: severity, dueBy };
}

test('severity escalation tightens the existing deadline from original detection', async () => {
  const { calls, error } = await report({ existing: [priorFinding()],
    findings: [currentFinding('CRITICAL', '2026-11-20')] });
  assert.equal(error, undefined);
  const updates = calls.filter(c => c[0] === 'update');
  assert.equal(updates.length, 1);
  assert.equal(updates[0][1].issue_number, 7);
  assert.match(updates[0][1].body, /Advisory: TEST-1 \(CRITICAL\)/);
  assert.match(updates[0][1].body, /Fix due by: 2026-11-08/);
  assert.match(updates[0][1].body, /First detected: 2026-11-01/);
  assert.match(updates[0][1].body, /Detected by: original-run/);
  assert.equal(calls.filter(c => c[0] === 'create').length, 0);
});

test('repeated or lower severity scans never extend an earlier deadline', async () => {
  const prior = priorFinding('CRITICAL', '2026-11-05');
  prior.body += '\nFirst detected: 2026-11-01';
  for (const severity of ['CRITICAL', 'HIGH']) {
    const { calls, error } = await report({ existing: [prior],
      findings: [currentFinding(severity, '2026-12-20')] });
    assert.equal(error, undefined);
    assert.equal(calls.filter(c => ['create', 'update'].includes(c[0])).length, 0);
  }
});

test('new issue records first detection and duplicate escalation updates that issue once', async () => {
  const { calls, error } = await report({ findings: [currentFinding('HIGH', '2026-12-01'),
    currentFinding('CRITICAL', '2026-11-08'), currentFinding('CRITICAL', '2026-11-08')] });
  assert.equal(error, undefined);
  assert.equal(calls.filter(c => c[0] === 'create').length, 1);
  const updates = calls.filter(c => c[0] === 'update');
  assert.equal(updates.length, 1);
  assert.match(updates[0][1].body, /Fix due by: 2026-11-08/);
});


test('replaying an escalated issue is idempotent across runs', async () => {
  const first = await report({ existing: [priorFinding()],
    findings: [currentFinding('CRITICAL', '2026-11-20')] });
  const body = first.calls.find(c => c[0] === 'update')[1].body;
  const replay = await report({ existing: [{ ...priorFinding(), body }],
    findings: [currentFinding('CRITICAL', '2026-11-21')] });
  assert.equal(replay.error, undefined);
  assert.equal(replay.calls.filter(c => ['create', 'update'].includes(c[0])).length, 0);
});

test('malformed stored deadlines fail without mutating the tracker', async () => {
  for (const due of ['2026-02-30', 'missing']) {
    const { calls, error } = await report({ existing: [priorFinding('HIGH', due)],
      findings: [currentFinding('CRITICAL', '2026-11-20')] });
    assert.match(error.message, /invalid detection or deadline/);
    assert.equal(calls.filter(c => ['create', 'update'].includes(c[0])).length, 0);
  }
});

test('a pull request with a matching title does not suppress a finding', async () => {
  const { calls, error } = await report({ existing: [{ ...priorFinding(), pull_request: {} }],
    findings: [currentFinding('HIGH', '2026-12-01')] });
  assert.equal(error, undefined);
  assert.equal(calls.filter(c => c[0] === 'create').length, 1);
});
