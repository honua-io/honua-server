'use strict';

const ACTIVE_REVIEW_STATES = new Set(['APPROVED', 'CHANGES_REQUESTED', 'COMMENTED']);
function isCodex(login) {
  return login === 'chatgpt-codex-connector' || login === 'chatgpt-codex-connector[bot]';
}
function evaluateCodexEvidence({ reviews, reactions, unresolvedCount, head, observedAt }) {
  const codexReviews = reviews
    .filter(review => isCodex(review.author?.login) &&
      (/Codex Review/i.test(review.body || '') || /Reviewed commit/i.test(review.body || '')))
    .sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
  const latest = codexReviews[0];
  const exactReview = unresolvedCount === 0 && latest?.commit?.oid === head &&
    ACTIVE_REVIEW_STATES.has(latest.state);
  const freshCleanReaction = unresolvedCount === 0 && observedAt !== null && reactions.some(reaction =>
    isCodex(reaction.user?.login) && reaction.content === '+1' &&
    new Date(reaction.created_at).getTime() >= observedAt);
  return { exactReview, freshCleanReaction };
}
module.exports = { evaluateCodexEvidence, isCodex };

