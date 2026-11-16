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

function mockGithub({ calls, lookupError, repository, duplicate, title, existing }) {
  return {
    rest: {
      repos: { get: async args => {
        calls.push(['lookup', args]);
        if (lookupError) throw lookupError;
        return { data: repository };
      } },
      issues: {
        listForRepo: 'list',
        create: async args => {
          calls.push(['create', args]);
          return { data: { ...args, number: 42, created_at: '2026-11-16T10:00:00Z' } };
        },
        update: async args => {
          calls.push(['update', args]);
          return { data: args };
        },
      },
    },
    paginate: async () => {
      calls.push(['list']);
      return duplicate ? [{ title, body: 'Advisory: TEST-1 (HIGH)' }] : existing;
    },
  };
}

async function report({ destination = 'owner/tracker', repository = { private: true }, lookupError,
  duplicate = false, severity = 'HIGH', existing = [] } = {}) {
  const calls = [];
  const finding = { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1',
    PkgName: 'test-package', Severity: severity, dueBy: '2026-12-16' };
  const title = '[release image] test-release linux/amd64: TEST-1 in test-package';
  const github = mockGithub({ calls, lookupError, repository, duplicate, title, existing });
  const requireMock = name => {
    assert.equal(name, 'fs');
    return { readFileSync: () => {
      calls.push(['read']);
      return JSON.stringify({ findings: [finding, finding] });
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

function trackedFinding(severity = 'HIGH', dueBy = '2026-12-01') {
  return {
    title: '[release image] test-release linux/amd64: TEST-1 in test-package',
    number: 19, created_at: '2026-11-01T23:59:59Z',
    body: `Advisory: TEST-1 (${severity})\nFix due by: ${dueBy} (policy)\nHuman triage notes remain here.`,
  };
}

test('escalation tightens the existing deadline from first detection and preserves triage notes', async () => {
  const { calls, error } = await report({ severity: 'CRITICAL', existing: [trackedFinding()] });
  assert.equal(error, undefined);
  const updates = calls.filter(call => call[0] === 'update');
  assert.equal(updates.length, 1);
  assert.equal(updates[0][1].issue_number, 19);
  assert.match(updates[0][1].body, /Advisory: TEST-1 \(CRITICAL\)/);
  assert.match(updates[0][1].body, /Fix due by: 2026-11-08/);
  assert.match(updates[0][1].body, /Human triage notes remain here/);
  assert.equal(calls.some(call => call[0] === 'create'), false);
});

test('escalation never extends a stricter deadline already recorded by triage', async () => {
  const { calls, error } = await report({ severity: 'CRITICAL', existing: [trackedFinding('HIGH', '2026-11-03')] });
  assert.equal(error, undefined);
  assert.match(calls.find(call => call[0] === 'update')[1].body, /Fix due by: 2026-11-03/);
});

test('later scans neither downgrade severity nor reset the existing deadline', async () => {
  for (const severity of ['HIGH', 'CRITICAL']) {
    const { calls, error } = await report({ severity, existing: [trackedFinding('CRITICAL', '2026-11-08')] });
    assert.equal(error, undefined);
    assert.deepEqual(calls.map(call => call[0]), ['lookup', 'read', 'list']);
  }
});

test('escalation with missing tracking metadata fails without overwriting the issue', async () => {
  for (const incomplete of [{ body: null }, { created_at: 'invalid' }, { body: 'Human notes only' }]) {
    const { calls, error } = await report({ severity: 'CRITICAL', existing: [{ ...trackedFinding(), ...incomplete }] });
    assert.match(error.message, /Existing private finding lacks/);
    assert.deepEqual(calls.map(call => call[0]), ['lookup', 'read', 'list']);
  }
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
