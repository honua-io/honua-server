'use strict';

const ATTESTING_REVIEW_STATES = new Set(['APPROVED', 'COMMENTED']);
const NEGATIVE_REVIEW_STATES = new Set(['CHANGES_REQUESTED', 'DISMISSED']);
function isCodex(login) {
  return login === 'chatgpt-codex-connector' || login === 'chatgpt-codex-connector[bot]';
}
function artifactNamesHead(body, head) {
  const escaped = head.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`(^|[^0-9a-f])${escaped}([^0-9a-f]|$)`, 'i').test(body || '');
}
function evaluateCodexEvidence({ reviews, reactionArtifacts = [], unresolvedCount, head }) {
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
  // A PR-level reaction has no commit identity. Only accept a Codex +1 attached
  // to a request/evidence comment that explicitly names the full current SHA.
  // Requiring the reaction after the artifact's last edit prevents rebinding an
  // old reaction by editing its comment to name a newer head.
  const freshCleanReaction = unresolvedCount === 0 && reactionArtifacts.some(artifact => {
    if (!artifactNamesHead(artifact.body, head)) return false;
    const artifactAt = Date.parse(artifact.updatedAt || artifact.createdAt);
    return artifact.reactions.some(reaction =>
      isCodex(reaction.user?.login) && reaction.content === '+1' &&
      Date.parse(reaction.created_at) >= artifactAt &&
      Date.parse(reaction.created_at) > negativeAt);
  });
  return { exactReview, freshCleanReaction };
}
if (require.main === module) {
  const fs = require('node:fs');
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  process.stdout.write(JSON.stringify(evaluateCodexEvidence(input)));
}
module.exports = { evaluateCodexEvidence, isCodex, artifactNamesHead };
