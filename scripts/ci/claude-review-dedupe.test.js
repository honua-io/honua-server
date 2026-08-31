'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');

const { buildCleanCommentBody } = require('./claude-review-body');
const { completedClaudeReviewAtHead } = require('./claude-review-dedupe');

const head = 'a'.repeat(40);
const staleHead = 'b'.repeat(40);
const bot = { login: 'claude[bot]' };

test('canonical clean evidence completes review for the exact head', () => {
  const result = completedClaudeReviewAtHead({
    head,
    comments: [{ user: bot, body: buildCleanCommentBody(head) }],
  });

  assert.deepEqual(result, { completed: true, clean: true, findings: false });
});

test('inline Claude findings complete review for the exact head', () => {
  const result = completedClaudeReviewAtHead({
    head,
    reviewComments: [{ user: bot, commit_id: head, body: 'Blocking finding' }],
  });

  assert.deepEqual(result, { completed: true, clean: false, findings: true });
});

test('evidence for a different head never suppresses review', () => {
  const result = completedClaudeReviewAtHead({
    head,
    comments: [{ user: bot, body: buildCleanCommentBody(staleHead) }],
    reviewComments: [{ user: bot, commit_id: staleHead, body: 'Old finding' }],
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: false });
});

test('non-Claude comments never suppress review', () => {
  const result = completedClaudeReviewAtHead({
    head,
    comments: [{ user: { login: 'someone' }, body: buildCleanCommentBody(head) }],
    reviewComments: [{ user: { login: 'someone' }, commit_id: head }],
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: false });
});
