'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateCodexEvidence } = require('./review-gate-evidence');
const head = 'abc123';
const review = state => ({ author: { login: 'chatgpt-codex-connector' }, body: '### Codex Review\nReviewed commit', submittedAt: '2026-01-02T00:00:00Z', commit: { oid: head }, state });
test('active exact-head Codex review attests', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('COMMENTED')], reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, true);
});
test('dismissed exact-head Codex review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('DISMISSED')], reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, false);
});
test('unresolved finding overrides clean reaction on same head', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactions, unresolvedCount: 1, head, observedAt: Date.parse('2026-01-01T00:00:00Z') }).freshCleanReaction, false);
});

