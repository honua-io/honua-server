'use strict';

const ATTESTING_REVIEW_STATES = new Set(['APPROVED', 'COMMENTED']);
const NEGATIVE_REVIEW_STATES = new Set(['CHANGES_REQUESTED', 'DISMISSED']);
function isCodex(login) {
  return login === 'chatgpt-codex-connector' || login === 'chatgpt-codex-connector[bot]';
}
function cleanCommentMatchesHead(comment, head) {
  if (!isCodex(comment.author?.login) || comment.includesCreatedEdit ||
      comment.createdAt !== comment.updatedAt ||
      !/Codex Review:\s+Didn't find any major issues\./i.test(comment.body || '')) {
    return false;
  }
  const reviewedCommits = [...(comment.body || '').matchAll(
    /\*\*Reviewed commit:\*\*\s*`([0-9a-f]{10,40})`/gi
  )];
  if (reviewedCommits.length !== 1) return false;
  const referenced = reviewedCommits[0][1].toLowerCase();
  const resolved = referenced.length === 40
    ? referenced
    : (comment.resolvedCommitOid || '').toLowerCase();
  return resolved.length === 40 &&
    resolved === head.toLowerCase() &&
    resolved.startsWith(referenced);
}
function evaluateCodexEvidence({ reviews, cleanComments = [], unresolvedCount, head }) {
  const codexReviews = reviews
    .filter(review => isCodex(review.author?.login) &&
      (/Codex Review/i.test(review.body || '') || /Reviewed commit/i.test(review.body || '')))
    .sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
  const latest = codexReviews[0];
  const negativeAt = codexReviews
    .filter(review => NEGATIVE_REVIEW_STATES.has(review.state))
    .reduce((max, review) => Math.max(max, Date.parse(review.updatedAt || review.submittedAt)), 0);
  const exactReview = unresolvedCount === 0 && latest?.commit?.oid === head &&
    ATTESTING_REVIEW_STATES.has(latest.state) && Date.parse(latest.submittedAt) > negativeAt;
  const exactCleanComment = unresolvedCount === 0 && cleanComments.some(comment =>
    cleanCommentMatchesHead(comment, head) && Date.parse(comment.createdAt) > negativeAt);
  // Generic reactions remain insufficient because they carry no reviewed SHA.
  // A clean connector comment is accepted only when it is unedited, names one
  // commit whose uniquely resolved full OID equals the head, and no connector
  // finding is open.
  const freshCleanReaction = false;
  return { exactReview, exactCleanComment, freshCleanReaction };
}
if (require.main === module) {
  const fs = require('node:fs');
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  process.stdout.write(JSON.stringify(evaluateCodexEvidence(input)));
}
module.exports = { evaluateCodexEvidence, isCodex };
