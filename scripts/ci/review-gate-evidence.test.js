'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateCodexEvidence } = require('./review-gate-evidence');
const head = 'abc123';
const review = (state, submittedAt = '2026-01-02T00:00:00Z', updatedAt = submittedAt) => ({ author: { login: 'chatgpt-codex-connector' }, body: '### Codex Review\nReviewed commit', submittedAt, updatedAt, commit: { oid: head }, state });
const cleanComment = (commit = head, overrides = {}) => ({
  author: { login: 'chatgpt-codex-connector' },
  body: `Codex Review: Didn't find any major issues. Hooray!\n\n**Reviewed commit:** \`${commit}\``,
  createdAt: '2026-01-02T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  includesCreatedEdit: false,
  ...overrides,
});
test('active exact-head Codex review attests', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('COMMENTED')], unresolvedCount: 0, head }).exactReview, true);
});
test('unedited Codex clean comment for exact head attests', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      resolvedCommitOid: fullHead,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, true);
});
test('unedited clean comment with a plain commit anchor attests', () => {
  const fullHead = 'b'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      body: `Codex Review: Didn't find any major issues. Clear!\n\nReviewed commit: \`${fullHead.slice(0, 10)}\``,
      resolvedCommitOid: fullHead,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, true);
});
test('unresolved short SHA cannot attest', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10))],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, false);
});
test('short SHA resolving to another full commit cannot attest', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      resolvedCommitOid: `${'a'.repeat(10)}${'f'.repeat(30)}`,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, false);
});
test('clean comment for another head cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('def4567890')],
    unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('edited clean comment cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      updatedAt: '2026-01-03T00:00:00Z',
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('clean comment created with an edit cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      includesCreatedEdit: true,
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('non-Codex clean comment cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      author: { login: 'contributor' },
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('clean comment with multiple reviewed SHAs cannot attest', () => {
  const comment = cleanComment('abc1234567');
  comment.body += '\n**Reviewed commit:** `abc1234568`';
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [comment],
    unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('unresolved finding overrides an exact-head clean comment', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567')],
    unresolvedCount: 1, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('dismissed exact-head Codex review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('DISMISSED')], unresolvedCount: 0, head }).exactReview, false);
});
test('changes-requested exact-head review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED')], unresolvedCount: 0, head }).exactReview, false);
});
test('clean reaction before a negative review cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('clean reaction after a negative review cannot attest without a commit-bound review', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('later nonnegative exact-head review supersedes negative review', () => {
  const reviews = [review('CHANGES_REQUESTED', '2026-01-02T00:00:00Z'), review('COMMENTED', '2026-01-03T00:00:00Z')];
  assert.equal(evaluateCodexEvidence({ reviews, unresolvedCount: 0, head }).exactReview, true);
});
test('unresolved finding overrides clean reaction on same head', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 1, head }).freshCleanReaction, false);
});
test('generic PR reaction cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactions, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('late old-head reaction after a new suite cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-05T00:00:00Z' }];
  const reactionArtifacts = [{ body: '@codex review old123', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('editing an old reacted artifact to the new head cannot rebind it', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-03T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('multi-SHA reaction artifact cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:01Z' }];
  const reactionArtifacts = [{ body: `@codex review old123 ${head}`, createdAt: '2026-01-04T00:00:00Z', updatedAt: '2026-01-04T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('pre-staged current-SHA reaction artifact cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-05T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('same-second edit and reaction cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-04T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
