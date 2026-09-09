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
const ATTEST_SHED = extractStepScript('review-gate.yml', 'Shed superseded review subject');
const CLAUDE_SHED = extractStepScript('claude-review.yml', 'Shed superseded review subject');

// Minimal stand-ins for the github-script globals the shed touches.
const runShed = async (script, { payload = {}, env = {}, pull, associated = [] }) => {
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
  const context = {
    payload,
    eventName: payload.eventName,
    repo: { owner: 'honua-io', repo: 'honua-server' },
  };

  // The attest/claude-review sheds run before any checkout, so they read their
  // subject from `env:` rather than from the webhook payload.
  const sandbox = { github, core, context, console, require, process: { env } };
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

// -- The second staleness window: `attest` queues, and the subject moves. ----
//
// `resolve` decides staleness when the event ARRIVES; `attest` then serializes
// behind every other event for the same pull request. These tests cover the gap
// between those two moments, which is where the measured waste lives: 27 of 63
// sampled `attest` failures on trunk were `pull request is not open`.

test('attest sheds a pull request that merged while the attestation queued', async () => {
  const result = await runShed(ATTEST_SHED, {
    env: { PR: '42', SUBJECT_HEAD: 'a'.repeat(40) },
    pull: MERGED_PR,
  });
  assert.equal(result.superseded, true);
  assert.match(result.notices.join('\n'), /PR #42 became closed while this attestation queued/);
});

test('attest NEVER sheds a moved head: it is the live-head fallback', async () => {
  // Regression guard for the one thing this shed must not do. `resolve` uses a
  // single PR-wide `cancel-in-progress: true` group and GitHub guarantees no
  // ordering inside it, so a late-delivered event for an OLD head can cancel
  // the resolver for the NEWEST head and then shed itself. The attestation
  // already queued behind it is then the only thing left that will stamp the
  // live head -- and `Review Gate` is required at the exact current head.
  // Shedding on a moved subject here would leave that head permanently
  // unattested and the PR unmergeable until an unrelated event woke the gate.
  const result = await runShed(ATTEST_SHED, { env: { PR: '42' }, pull: OPEN_PR });
  assert.equal(result.superseded, false);
  assert.equal(result.failure, null);
  // The shed may not grow a head comparison, and the job must not be handed a
  // bound subject to compare against.
  assert.doesNotMatch(ATTEST_SHED, /head\.sha|SUBJECT_HEAD|headRefOid/);
  const gate = fs.readFileSync(path.join(WORKFLOWS, 'review-gate.yml'), 'utf8');
  const attestJob = gate.slice(gate.indexOf('\n  attest:'));
  assert.doesNotMatch(attestJob, /needs\.resolve\.outputs\.head/);
});

test('attest admits an open pull request', async () => {
  const result = await runShed(ATTEST_SHED, { env: { PR: '42' }, pull: OPEN_PR });
  assert.equal(result.superseded, false);
  assert.equal(result.failure, null);
  assert.equal(result.calls.pullsGet, 1);
});

test('the attest shed is neutral: no failure, no commit status, one summary line', async () => {
  // The whole safety argument. This shed only ever WITHHOLDS an attestation, so
  // it cannot admit a pull request that would otherwise have been blocked. A
  // `createCommitStatus` here would break that, and a `setFailed` would turn a
  // merged pull request into a red run instead of a quiet one.
  const result = await runShed(ATTEST_SHED, {
    env: { PR: '42', SUBJECT_HEAD: 'a'.repeat(40) },
    pull: MERGED_PR,
  });
  assert.equal(result.failure, null);
  assert.equal(result.summaryLines.length, 1);
  assert.match(result.summaryLines[0], /^Shed superseded review subject: /);
  assert.doesNotMatch(ATTEST_SHED, /createCommitStatus/);
  assert.doesNotMatch(ATTEST_SHED, /setFailed/);
});

test('every paid step of the trusted attestation is behind the shed', () => {
  // A new step added above or beside the guard would silently reopen the window.
  const gate = fs.readFileSync(path.join(WORKFLOWS, 'review-gate.yml'), 'utf8');
  const attestJob = gate.slice(gate.indexOf('\n  attest:'));
  const shedAt = attestJob.indexOf('- name: Shed superseded review subject');
  assert.notEqual(shedAt, -1, 'attest must shed before it works');
  const steps = attestJob.split('\n      - ').slice(1);
  for (const marker of [
    'uses: actions/checkout@',
    'name: Bind the immutable review-first measurement policy',
    'name: Attest current PR head',
  ]) {
    const step = steps.find(candidate => candidate.includes(marker));
    assert.ok(step, `attest lost its "${marker}" step`);
    assert.match(
      step,
      /if: steps\.subject\.outputs\.superseded != 'true'/,
      `"${marker}" runs before the shed decides`,
    );
  }
});

// -- claude-review: decide the subject before paying for the trusted tree. ---

test('the Claude lane sheds closed, draft and moved subjects', async () => {
  const cases = [
    ['workflow_run', { RUN_PR: '42', RUN_HEAD: 'a'.repeat(40) }, MERGED_PR, /PR #42 is closed\./],
    ['workflow_run', { RUN_PR: '42', RUN_HEAD: 'b'.repeat(40) }, OPEN_PR, /moved past b{40}/],
    ['issue_comment', { COMMENT_ISSUE: '42' }, { ...OPEN_PR, draft: true }, /PR #42 is a draft\./],
    ['workflow_dispatch', { CATCHUP_PR: '42', CATCHUP_HEAD: 'b'.repeat(40) }, OPEN_PR,
      /moved past b{40}/],
  ];
  for (const [eventName, env, pull, expected] of cases) {
    const result = await runShed(CLAUDE_SHED, { payload: { eventName }, env, pull });
    assert.equal(result.superseded, true, `${eventName} ${JSON.stringify(env)} was not shed`);
    assert.match(result.notices.join('\n'), expected);
  }
});

test('the Claude lane admits a live exact head and falls through when unidentifiable', async () => {
  const live = await runShed(CLAUDE_SHED, {
    payload: { eventName: 'workflow_run' },
    env: { RUN_PR: '42', RUN_HEAD: 'a'.repeat(40) },
    pull: OPEN_PR,
  });
  assert.equal(live.superseded, false);

  // Fork and association gaps must reach the trusted resolver, which fails
  // closed itself -- shedding them here would hide that decision.
  const unidentified = await runShed(CLAUDE_SHED, {
    payload: { eventName: 'workflow_run' },
    env: { RUN_PR: '', RUN_HEAD: 'a'.repeat(40) },
  });
  assert.equal(unidentified.superseded, false);
  assert.equal(unidentified.calls.pullsGet, 0);
});

test('the Claude lane sheds before it checks out the trusted reviewer policy', () => {
  const lane = fs.readFileSync(path.join(WORKFLOWS, 'claude-review.yml'), 'utf8');
  const resolveJob = lane.slice(lane.indexOf('\n  resolve:'), lane.indexOf('\n  review:'));
  const shedAt = resolveJob.indexOf('- name: Shed superseded review subject');
  const checkoutAt = resolveJob.indexOf('uses: actions/checkout@');
  assert.ok(shedAt >= 0 && checkoutAt > shedAt, 'the shed must precede the trusted checkout');
  assert.match(
    resolveJob,
    /- name: Check out trusted reviewer policy\n\s+if: steps\.stale\.outputs\.superseded != 'true'/);
  assert.match(resolveJob, /id: target\n\s+if: steps\.stale\.outputs\.superseded != 'true'/);
  // Shedding may only withhold a review, never grant one.
  assert.doesNotMatch(CLAUDE_SHED, /setOutput\('skip', 'false'\)/);
});
