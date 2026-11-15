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

async function report({ destination = 'owner/tracker', repository = { private: true }, lookupError, duplicate = false } = {}) {
  const calls = [];
  const finding = { release: 'test-release', platform: 'linux/amd64', VulnerabilityID: 'TEST-1', PkgName: 'test-package' };
  const title = '[release image] test-release linux/amd64: TEST-1 in test-package';
  const github = {
    rest: {
      repos: { get: async args => {
        calls.push(['lookup', args]);
        if (lookupError) throw lookupError;
        return { data: repository };
      } },
      issues: { listForRepo: 'list', create: async args => calls.push(['create', args]) },
    },
    paginate: async () => { calls.push(['list']); return duplicate ? [{ title }] : []; },
  };
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
