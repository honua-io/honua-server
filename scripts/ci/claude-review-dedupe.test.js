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

  assert.deepEqual(result, { completed: true, clean: true, findings: false, hasReceipt: false });
});

test('inline Claude findings complete review only with a successful receipt', () => {
  const result = completedClaudeReviewAtHead({
    head,
    reviewComments: [{ user: bot, commit_id: head, body: 'Blocking finding' }],
    hasReceipt: true,
  });

  assert.deepEqual(result, { completed: true, clean: false, findings: true, hasReceipt: true });
});

test('partial Claude findings do not suppress a retry', () => {
  const result = completedClaudeReviewAtHead({
    head,
    reviewComments: [{ user: bot, commit_id: head, body: 'Finding before exhaustion' }],
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: true, hasReceipt: false });
});

test('evidence for a different head never suppresses review', () => {
  const result = completedClaudeReviewAtHead({
    head,
    comments: [{ user: bot, body: buildCleanCommentBody(staleHead) }],
    reviewComments: [{ user: bot, commit_id: staleHead, body: 'Old finding' }],
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: false, hasReceipt: false });
});

test('non-Claude comments never suppress review', () => {
  const result = completedClaudeReviewAtHead({
    head,
    comments: [{ user: { login: 'someone' }, body: buildCleanCommentBody(head) }],
    reviewComments: [{ user: { login: 'someone' }, commit_id: head }],
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: false, hasReceipt: false });
});

test('Codex findings never suppress the independent Claude lane', () => {
  const result = completedClaudeReviewAtHead({
    head,
    reviewComments: [{
      user: { login: 'chatgpt-codex-connector[bot]' },
      commit_id: head,
    }],
    hasReceipt: true,
  });

  assert.deepEqual(result, { completed: false, clean: false, findings: false, hasReceipt: true });
});
