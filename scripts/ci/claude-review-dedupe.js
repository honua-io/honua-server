'use strict';

// Detect whether the Claude lane has already completed its only two successful
// publication paths for an exact head: a canonical clean comment, or one or
// more inline findings anchored to that commit. This is dispatch policy only;
// Review Gate remains the authority that decides whether evidence attests.

const { ATTESTING_LOGINS } = require('./review-gate-evidence');
const { buildCleanCommentBody } = require('./claude-review-body');

const SHA = /^[0-9a-f]{40}$/;

function normalise(text) {
  return String(text || '').replace(/\r/g, '').trimEnd();
}

function isAttestingLogin(login) {
  return ATTESTING_LOGINS.includes(login);
}

function completedClaudeReviewAtHead({ head, comments = [], reviewComments = [] }) {
  if (!SHA.test(String(head || ''))) {
    throw new Error('head must be a full 40-character lowercase commit sha');
  }

  const cleanBody = normalise(buildCleanCommentBody(head));
  const clean = comments.some(comment =>
    isAttestingLogin(comment.user?.login) && normalise(comment.body) === cleanBody);
  const findings = reviewComments.some(comment =>
    isAttestingLogin(comment.user?.login) && comment.commit_id === head);

  return { completed: clean || findings, clean, findings };
}

async function findCompletedClaudeReview({ github, repo, pullNumber, head }) {
  const [comments, reviewComments] = await Promise.all([
    github.paginate(github.rest.issues.listComments, {
      ...repo,
      issue_number: pullNumber,
      per_page: 100,
    }),
    github.paginate(github.rest.pulls.listReviewComments, {
      ...repo,
      pull_number: pullNumber,
      per_page: 100,
    }),
  ]);

  return completedClaudeReviewAtHead({ head, comments, reviewComments });
}

module.exports = { completedClaudeReviewAtHead, findCompletedClaudeReview };
