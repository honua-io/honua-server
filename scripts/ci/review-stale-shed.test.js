'use strict';

// Behavioural contract for the "Shed superseded review event" step in
// review-gate.yml and review-event-bridge.yml.
//
// The shed decides whether the trusted attestation job runs at all, so a text
// assertion is not enough: these tests lift the actual `script:` body out of
// the workflow and execute it against fake `github`/`core`/`context` objects.
// A drift in the YAML is a failing test here, not a silent behaviour change in
// production.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const WORKFLOWS = path.join(__dirname, '..', '..', '.github', 'workflows');

// Lift the first `script: |` block that follows the named step out of the
// workflow and undo its YAML block indentation.
const extractStepScript = (workflow, stepName) => {
  const source = fs.readFileSync(path.join(WORKFLOWS, workflow), 'utf8');
  const stepAt = source.indexOf(`- name: ${stepName}`);
  assert.notEqual(stepAt, -1, `${workflow} has no step named ${stepName}`);
  const rest = source.slice(stepAt);
  const blockAt = rest.indexOf('script: |\n');
  assert.notEqual(blockAt, -1, `${stepName} in ${workflow} has no script block`);
  const lines = rest.slice(blockAt + 'script: |\n'.length).split('\n');
  const indent = /^ +/.exec(lines[0])[0].length;
  const body = [];
  for (const line of lines) {
    if (line.trim() !== '' && /^ +/.exec(line)?.[0].length < indent) break;
    body.push(line.slice(indent));
  }
  const script = body.join('\n').trimEnd();
  assert.ok(script.length > 0, `${stepName} in ${workflow} yielded an empty script`);
  return script;
};

const GATE_SHED = extractStepScript('review-gate.yml', 'Shed superseded review event');
const BRIDGE_SHED = extractStepScript('review-event-bridge.yml', 'Shed superseded review event');

// Minimal stand-ins for the github-script globals the shed touches.
const runShed = async (script, { payload, pull, associated = [] }) => {
  const outputs = {};
  const notices = [];
  const summaryLines = [];
  const infos = [];
  let failure = null;
  const calls = { pullsGet: 0, listAssociated: 0 };

  const summary = {
    addRaw(text) { summaryLines.push(text); return summary; },
    async write() { return summary; },
  };
  const core = {
    setOutput(key, value) { outputs[key] = value; },
    notice(message) { notices.push(message); },
    info(message) { infos.push(message); },
    setFailed(message) { failure = message; },
    summary,
  };
  const github = {
    rest: {
      pulls: {
        async get() {
          calls.pullsGet += 1;
          assert.ok(pull, 'shed called pulls.get but the test provided no pull request');
          return { data: pull };
        },
      },
      repos: {
        async listPullRequestsAssociatedWithCommit() {
          calls.listAssociated += 1;
          return { data: associated };
        },
      },
    },
  };
  const context = { payload, repo: { owner: 'honua-io', repo: 'honua-server' } };

  const sandbox = { github, core, context, console, require };
  const runner = vm.runInNewContext(
    `(async () => {\n${script}\n})`, vm.createContext(sandbox), { timeout: 5000 });
  await runner();

  return {
    superseded: outputs.superseded === 'true',
    notices, summaryLines, infos, failure, calls,
  };
};

const OPEN_PR = { number: 42, state: 'open', head: { sha: 'a'.repeat(40) } };
const MERGED_PR = { number: 42, state: 'closed', head: { sha: 'a'.repeat(40) } };

test('review-gate sheds a workflow_run whose subject head has moved', async () => {
  const result = await runShed(GATE_SHED, {
    payload: { workflow_run: { head_sha: 'b'.repeat(40), pull_requests: [{ number: 42 }] } },
    pull: OPEN_PR,
  });
  assert.equal(result.superseded, true);
  assert.match(result.notices.join('\n'), /superseded: event subject b{40} is not current head a{40}/);
});

test('review-gate admits a workflow_run that is still the current head', async () => {
  const result = await runShed(GATE_SHED, {
    payload: { workflow_run: { head_sha: 'a'.repeat(40), pull_requests: [{ number: 42 }] } },
    pull: OPEN_PR,
  });
  assert.equal(result.superseded, false);
  assert.equal(result.failure, null);
});

test('review-gate sheds every event whose pull request is already closed', async () => {
  // The terminal stale case. Attesting a merged head spends the trusted job to
  // publish a RED `Review Gate` status on a commit no admission can consume,
  // because `pull request is not open` scores as a blocking reason.
  for (const payload of [
    { pull_request: { number: 42, head: { sha: 'a'.repeat(40) } } },      // pull_request_target
    { issue: { number: 42, pull_request: {} } },                          // issue_comment
    { workflow_run: { head_sha: 'a'.repeat(40), pull_requests: [{ number: 42 }] } },
    { client_payload: { pr: 42, head_sha: 'a'.repeat(40) } },             // repository_dispatch
  ]) {
    const result = await runShed(GATE_SHED, { payload, pull: MERGED_PR });
    assert.equal(result.superseded, true, `not shed: ${JSON.stringify(payload)}`);
    assert.match(result.notices.join('\n'), /PR #42 is closed/);
  }
});

test('an issue_comment carries no subject SHA but still reaches the closed check', async () => {
  // Regression guard. The shed used to return early on a missing subject SHA,
  // which exempted issue_comment -- the single highest-volume review-gate
  // trigger -- from the shed entirely.
  const shed = await runShed(GATE_SHED, {
    payload: { issue: { number: 42, pull_request: {} } },
    pull: MERGED_PR,
  });
  assert.equal(shed.calls.pullsGet, 1, 'issue_comment must resolve its pull request');
  assert.equal(shed.superseded, true);

  const admitted = await runShed(GATE_SHED, {
    payload: { issue: { number: 42, pull_request: {} } },
    pull: OPEN_PR,
  });
  assert.equal(admitted.superseded, false, 'an open PR comment must still be attested');
});

test('review-gate never shells out to the association API without a subject SHA', async () => {
  const result = await runShed(GATE_SHED, {
    payload: { workflow_run: { head_sha: undefined, pull_requests: [] } },
  });
  assert.equal(result.calls.listAssociated, 0);
  assert.equal(result.calls.pullsGet, 0);
  assert.equal(result.superseded, false, 'an unidentifiable event must fall through, not shed');
});

test('review-gate sheds neutrally: a job summary line and no failure', async () => {
  // The packet contract. `attest` is gated on an empty `pr` output, so the shed
  // must never turn a superseded event into a red run.
  const result = await runShed(GATE_SHED, {
    payload: { pull_request: { number: 42, head: { sha: 'b'.repeat(40) } } },
    pull: OPEN_PR,
  });
  assert.equal(result.failure, null, 'the review-gate shed must not fail the run');
  assert.equal(result.summaryLines.length, 1);
  assert.match(result.summaryLines[0], /^Shed superseded review event: /);
});

test('the bridge sheds a moved head and a closed pull request', async () => {
  const moved = await runShed(BRIDGE_SHED, {
    payload: { pull_request: { number: 42 }, review: { commit_id: 'b'.repeat(40) } },
    pull: OPEN_PR,
  });
  assert.equal(moved.superseded, true);

  const closed = await runShed(BRIDGE_SHED, {
    payload: { pull_request: { number: 42 }, comment: { commit_id: 'a'.repeat(40) } },
    pull: MERGED_PR,
  });
  assert.equal(closed.superseded, true);
  assert.match(closed.notices.join('\n'), /PR #42 is closed/);
});

test('the bridge still publishes a non-success conclusion when it sheds', () => {
  // review-gate.yml's job-level `if` reads the bridge's CONCLUSION -- GitHub has
  // no conclusion filter on the `workflow_run` trigger and workflow_run
  // consumers cannot read step outputs. Turning this into a neutral exit would
  // silently re-admit every superseded bridge event.
  assert.match(BRIDGE_SHED, /core\.setFailed\('Superseded review event; trusted notification suppressed\.'\)/);
  const gate = fs.readFileSync(path.join(WORKFLOWS, 'review-gate.yml'), 'utf8');
  assert.match(gate, /github\.event\.workflow_run\.conclusion == 'success'/);
});

test('the bridge admits a live review on the current head', async () => {
  const result = await runShed(BRIDGE_SHED, {
    payload: { pull_request: { number: 42 }, review: { commit_id: 'a'.repeat(40) } },
    pull: OPEN_PR,
  });
  assert.equal(result.superseded, false);
  assert.equal(result.failure, null);
});

test('the unprivileged review jobs single-flight and the trusted attest does not', () => {
  const gate = fs.readFileSync(path.join(WORKFLOWS, 'review-gate.yml'), 'utf8');
  const bridge = fs.readFileSync(path.join(WORKFLOWS, 'review-event-bridge.yml'), 'utf8');
  // Unprivileged resolve/bridge shed superseded work by cancellation.
  assert.match(gate, /group: review-resolve-pr-\$\{\{[\s\S]*?\n\s*cancel-in-progress: true/);
  assert.match(bridge, /group: review-event-bridge-pr-\$\{\{[^\n]*\n\s*cancel-in-progress: true/);
  // The trusted attestation must stay serialized and uncancellable: it is
  // interrupted between accepting a rerun and recording the result otherwise.
  assert.match(gate, /group: review-gate-\$\{\{ needs\.resolve\.outputs\.pr \}\}[\s\S]*?\n\s*cancel-in-progress: false/);
});
