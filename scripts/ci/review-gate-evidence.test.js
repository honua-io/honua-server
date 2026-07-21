'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateCodexEvidence } = require('./review-gate-evidence');
const head = 'abc123';
const review = (state, submittedAt = '2026-01-02T00:00:00Z', updatedAt = submittedAt) => ({ author: { login: 'chatgpt-codex-connector' }, body: '### Codex Review\nReviewed commit', submittedAt, updatedAt, commit: { oid: head }, state });
test('active exact-head Codex review attests', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('COMMENTED')], reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, true);
});
test('dismissed exact-head Codex review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('DISMISSED')], reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, false);
});
test('changes-requested exact-head review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED')], reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, false);
});
test('clean reaction before a negative review cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactions, unresolvedCount: 0, head, observedAt: Date.parse('2026-01-01T00:00:00Z') }).freshCleanReaction, false);
});
test('clean reaction after a negative review can attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactions, unresolvedCount: 0, head, observedAt: Date.parse('2026-01-01T00:00:00Z') }).freshCleanReaction, true);
});
test('later nonnegative exact-head review supersedes negative review', () => {
  const reviews = [review('CHANGES_REQUESTED', '2026-01-02T00:00:00Z'), review('COMMENTED', '2026-01-03T00:00:00Z')];
  assert.equal(evaluateCodexEvidence({ reviews, reactions: [], unresolvedCount: 0, head, observedAt: null }).exactReview, true);
});
test('unresolved finding overrides clean reaction on same head', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactions, unresolvedCount: 1, head, observedAt: Date.parse('2026-01-01T00:00:00Z') }).freshCleanReaction, false);
});
