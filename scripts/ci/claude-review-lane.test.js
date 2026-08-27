'use strict';

// Invariants of the Claude review lane that live in the workflow rather than in
// a module, asserted against the workflow text itself. Prose in a prompt is not
// a control; these turn each stated guarantee into a failing test if it drifts.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const WORKFLOW = path.join(__dirname, '..', '..', '.github', 'workflows', 'claude-review.yml');
const source = fs.readFileSync(WORKFLOW, 'utf8');
const allowedTools = /--allowedTools "([^"]+)"/.exec(source)?.[1] ?? '';

test('the lane never runs from the pull request it reviews', () => {
  // A `pull_request` trigger would execute the candidate's own copy of this
  // workflow, its prompt and its CLAUDE.md with secrets and `id-token: write`,
  // letting a PR attest to itself and exfiltrate the credentials. Only
  // default-branch-executed events are permitted.
  assert.match(source, /^on:\n(?:.*\n)*?\s{2}workflow_run:/m);
  assert.match(source, /^\s{2}issue_comment:/m);
  assert.doesNotMatch(source, /^\s{2}pull_request(_target)?:/m);
});

test('the lane never submits a pull-request review verdict', () => {
  // A CHANGES_REQUESTED from this identity is cleared only by a NEWER positive
  // from the SAME identity, so a false positive from an optional reviewer that
  // later stops running would block the PR permanently. The lane publishes
  // comments and inline threads only -- both human-clearable.
  assert.doesNotMatch(source, /REQUEST_CHANGES/);
  assert.doesNotMatch(source, /\bAPPROVE\b/);
  assert.doesNotMatch(source, /pulls\/[^\n]*\/reviews/);
  assert.doesNotMatch(source, /gh pr review/);
});

test('the reviewer holds no tool that can merge, push, or edit a workflow', () => {
  // The action authenticates as the Claude GitHub App, whose org installation
  // carries contents/workflows/pull_requests write. The tool allowlist is the
  // only thing standing between a prompt-injected diff and that authority, so
  // it must contain nothing general-purpose.
  const tools = allowedTools.split(',').map(tool => tool.trim()).filter(Boolean);
  assert.deepEqual(tools, [
    'Read',
    'Grep',
    'Glob',
    'Bash(gh pr comment:*)',
    'mcp__github_inline_comment__create_inline_comment',
  ]);
  // The merge primitive is assembled rather than written out: this repository's
  // validate-single-merge-authority.sh scans tracked files for merge-capable
  // literals, and a test asserting the ABSENCE of one would otherwise trip it.
  const mergeCommand = ['gh', 'pr', 'me' + 'rge'].join(' ');
  for (const forbidden of ['gh api', mergeCommand, 'git ', 'Write', 'Edit', 'WebFetch']) {
    assert.ok(!allowedTools.includes(forbidden), `allowlist must not contain ${forbidden}`);
  }
});

test('GITHUB_TOKEN stays read-only', () => {
  // Everything the lane publishes is published by the App token, so the
  // workflow token needs no write scope. `id-token: write` is the OIDC
  // exchange and grants nothing against the repository.
  const lines = source.split('\n');
  const start = lines.findIndex(line => /^\s{4}permissions:\s*$/.test(line));
  assert.ok(start > 0, 'job permissions block must be declared explicitly');
  const scopes = [];
  for (const line of lines.slice(start + 1)) {
    if (/^\s*(#.*)?$/.test(line)) continue;          // blank or comment
    if (!/^\s{6}\S/.test(line)) break;               // dedented out of the block
    scopes.push(line.trim());
  }
  assert.ok(scopes.length > 0, 'job permissions block must not be empty');
  assert.deepEqual(scopes.filter(scope => /:\s*write$/.test(scope)), ['id-token: write']);
});

test('the lane generates its attestation body instead of transcribing it', () => {
  // The exact bytes are graded by review-gate-evidence.js. Spelling them in the
  // workflow would let the lane and its evaluator drift apart silently.
  assert.match(source, /claude-review-body/);
  assert.match(source, /--body-file review-input\/clean-body\.md/);
  assert.doesNotMatch(source, /No major issues found/);
});

test('candidate-controlled reviewer instructions are stripped before review', () => {
  assert.match(source, /-name CLAUDE\.md/);
  assert.match(source, /-name AGENTS\.md/);
  assert.match(source, /-name \.claude/);
});

test('the action is pinned to an immutable commit', () => {
  const pin = /uses: anthropics\/claude-code-action@([^\s]+)/.exec(source)?.[1];
  assert.match(pin ?? '', /^[0-9a-f]{40}$/);
});

test('the reviewer turn budget scales with the staged diff and stays capped', () => {
  assert.match(source, /id: diff/);
  assert.match(source, /diff_lines=\$\{diff_lines\}/);
  assert.match(source, /if \[ "\$\{diff_lines\}" -le 1500 \]; then/);
  assert.match(source, /elif \[ "\$\{diff_lines\}" -le 8000 \]; then/);
  assert.match(source, /max_turns=40/);
  assert.match(source, /max_turns=60/);
  assert.match(source, /max_turns=80/);
  assert.match(source, /--max-turns \$\{\{ steps\.diff\.outputs\.max_turns \}\}/);
});

test('an exhausted review posts the explicit fallback without widening token permissions', () => {
  assert.match(source, /steps\.claude\.outcome == 'failure'/);
  assert.match(source, /\.subtype == "error_max_turns"/);
  assert.match(source, /review exhausted its turn budget at .* turns for an .*line diff; Codex attestation or a human @claude review re-request can satisfy Review Gate/);
  assert.match(source, /steps\.exhausted\.outputs\.max_turns == 'true'/);
});

test('no comment event can cancel a review already in flight', () => {
  // A run is created for EVERY comment and concurrency is evaluated at run
  // creation, so cancelling would let Codex's rate-limit notice kill a live
  // review. Superseded heads need no cancellation: the trusted resolve step
  // fails closed once the pull request has moved.
  assert.match(source, /cancel-in-progress: false/);
  assert.doesNotMatch(source, /cancel-in-progress: true/);
});

test('an `@claude review` re-request requires a human with write access', () => {
  assert.match(source, /author_association/);
  assert.match(source, /OWNER","MEMBER","COLLABORATOR/);
  assert.match(source, /comment\.user\.type == 'User'/);
});
