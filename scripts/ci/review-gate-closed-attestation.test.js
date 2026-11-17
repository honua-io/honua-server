'use strict';

// Behavioural contract: the "Attest current PR head" step in review-gate.yml
// must not stamp a red `Review Gate` status on a pull request that merged or
// closed while the attestation was running. The test lifts the real script
// out of the workflow and runs it against fake github-script globals.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const ROOT = path.join(__dirname, '..', '..');
const PR_NUMBER = 42;
const HEAD = 'a'.repeat(40);

const extractAttestScript = () => {
  const source = fs.readFileSync(path.join(ROOT, '.github/workflows/review-gate.yml'), 'utf8');
  const rest = source.slice(source.indexOf('- name: Attest current PR head'));
  const lines = rest.slice(rest.indexOf('script: |\n') + 'script: |\n'.length).split('\n');
  const indent = /^ +/.exec(lines[0])[0].length;
  const body = [];
  for (const line of lines) {
    if (line.trim() !== '' && /^ +/.exec(line)?.[0].length < indent) break;
    body.push(line.slice(indent));
  }
  return body.join('\n').replace("${{ needs.resolve.outputs.pr }}", String(PR_NUMBER));
};

const ATTEST = extractAttestScript();

const page = nodes => ({ nodes, pageInfo: { hasNextPage: false } });

const snapshot = ({ state, labels = [] }) => ({
  state,
  isDraft: false,
  headRefOid: HEAD,
  labels: page(labels.map(name => ({ name }))),
  reviews: page([]),
  comments: page([]),
  reviewThreads: page([]),
});

const fakeCore = result => {
  const summary = {
    addHeading() { return summary; },
    addRaw() { return summary; },
    addTable() { return summary; },
    async write() { return summary; },
  };
  return {
    notice(message) { result.notices.push(message); },
    info() {},
    warning() {},
    setOutput() {},
    setFailed(message) { result.failure = message; },
    summary,
  };
};

const fakeGithub = (result, { liveState, liveError }) => ({
  rest: {
    pulls: {
      async get() {
        if (liveError) throw liveError;
        return { data: { number: PR_NUMBER, state: liveState } };
      },
    },
    repos: { async createCommitStatus(status) { result.statuses.push(status); } },
  },
});

const runAttest = async ({ snapshotState, liveState, labels, liveError }) => {
  const result = { statuses: [], notices: [], failure: null };
  const fakeRequire = request => {
    if (request === './scripts/ci/review-gate-snapshot') {
      return { fetchPullRequestSnapshot: async () => snapshot({ state: snapshotState, labels }) };
    }
    return require(path.join(ROOT, request));
  };
  const sandbox = {
    github: fakeGithub(result, { liveState, liveError }),
    core: fakeCore(result),
    context: { repo: { owner: 'honua-io', repo: 'honua-server' }, serverUrl: 'https://github.com' },
    console,
    require: fakeRequire,
    process: { env: {} },
  };
  const runner = vm.runInNewContext(
    `(async () => {\n${ATTEST}\n})`, vm.createContext(sandbox), { timeout: 5000 });
  await runner();
  return result;
};

test('a pull request merged during attestation gets no red Review Gate status', async () => {
  const result = await runAttest({ snapshotState: 'MERGED', liveState: 'closed' });
  assert.deepEqual(result.statuses, []);
  assert.equal(result.failure, null);
  assert.match(result.notices.join('\n'), /PR #42 became closed during attestation/);
});

test('an open pull request with a blocking reason still gets a red Review Gate status', async () => {
  const result = await runAttest({ snapshotState: 'OPEN', liveState: 'open', labels: ['hold'] });
  assert.equal(result.statuses.length, 1);
  assert.equal(result.statuses[0].state, 'failure');
  assert.equal(result.statuses[0].sha, HEAD);
  assert.match(result.statuses[0].description, /blocked by hold/);
});

test('a failed pull request re-read keeps the red Review Gate status', async () => {
  const result = await runAttest({
    snapshotState: 'MERGED', liveState: 'closed', liveError: new Error('HTTP 502'),
  });
  assert.equal(result.statuses.length, 1);
  assert.equal(result.statuses[0].state, 'failure');
  assert.match(result.statuses[0].description, /pull request is not open/);
});
