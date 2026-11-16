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
  const finding = { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1', PkgName: 'test-package',
    Severity: 'HIGH', firstDetected: '2026-11-01', dueBy: '2026-12-01' };
  const title = '[release image] test-release linux/amd64: TEST-1 in test-package';
  const github = {
    rest: {
      repos: { get: async args => {
        calls.push(['lookup', args]);
        if (lookupError) throw lookupError;
        return { data: repository };
      } },
      issues: { listForRepo: 'list', create: async args => { calls.push(['create', args]); return { data: { number: 42, ...args } }; },
        update: async args => { calls.push(['update', args]); return { data: args }; } },
    },
    paginate: async () => { calls.push(['list']); return existing || (duplicate ? [{ title, number: 42, created_at: '2026-11-01T00:00:00Z',
      body: 'Advisory: TEST-1 (HIGH)\nFirst detected: 2026-11-01\nFix due by: 2026-12-01 (SECURITY.md: critical 7 days, high 30 days from detection)' }] : []); },
  };
  const requireMock = name => {
    if (name === './scripts/security/private-findings.js') return require('./private-findings.js');
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


test('severity escalation tightens the existing deadline from first detection and stays idempotent', async () => {
  const initial = await report();
  const issue = { ...initial.calls.find(call => call[0] === 'create')[1], number: 42,
    created_at: '2026-11-01T12:00:00Z' };
  const finding = { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1',
    PkgName: 'test-package', Severity: 'CRITICAL', firstDetected: '2026-11-15', dueBy: '2026-11-22' };
  const escalated = await report({ existing: [issue], findings: [finding, finding] });
  assert.equal(escalated.error, undefined);
  const updates = escalated.calls.filter(call => call[0] === 'update');
  assert.equal(updates.length, 1);
  const body = updates[0][1].body;
  assert.match(body, /First detected: 2026-11-01/);
  assert.match(body, /Fix due by: 2026-11-08/);
  assert.match(body, /Advisory: TEST-1 \(CRITICAL\)/);
  assert.equal(updates[0][1].issue_number, 42);
  const again = await report({ existing: [{ ...issue, body }], findings: [finding] });
  assert.equal(again.error, undefined);
  assert.equal(again.calls.filter(call => ['update', 'create'].includes(call[0])).length, 0);
});

test('legacy findings use issue creation as detection and never relax an earlier deadline', async () => {
  const issue = { title: '[release image] test-release linux/amd64: TEST-1 in test-package', number: 42,
    created_at: '2026-11-01T12:00:00Z',
    body: 'Advisory: TEST-1 (CRITICAL)\nFix due by: 2026-11-05 (policy)\nHuman notes: retain this' };
  const { calls, error } = await report({ existing: [issue] });
  assert.equal(error, undefined);
  const update = calls.find(call => call[0] === 'update')[1];
  assert.match(update.body, /First detected: 2026-11-01/);
  assert.match(update.body, /Fix due by: 2026-11-05/);
  assert.match(update.body, /Advisory: TEST-1 \(CRITICAL\)/);
  assert.match(update.body, /Human notes: retain this/);
});
