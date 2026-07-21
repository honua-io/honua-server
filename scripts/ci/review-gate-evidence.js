'use strict';

const ATTESTING_REVIEW_STATES = new Set(['APPROVED', 'COMMENTED']);
const NEGATIVE_REVIEW_STATES = new Set(['CHANGES_REQUESTED', 'DISMISSED']);
function isCodex(login) {
  return login === 'chatgpt-codex-connector' || login === 'chatgpt-codex-connector[bot]';
}
function evaluateCodexEvidence({ reviews, reactions, unresolvedCount, head, observedAt }) {
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
  const freshCleanReaction = unresolvedCount === 0 && observedAt !== null && reactions.some(reaction =>
    isCodex(reaction.user?.login) && reaction.content === '+1' &&
    new Date(reaction.created_at).getTime() >= observedAt &&
    new Date(reaction.created_at).getTime() > negativeAt);
  return { exactReview, freshCleanReaction };
}
module.exports = { evaluateCodexEvidence, isCodex };
