'use strict';

const ATTESTING_REVIEW_STATES = new Set(['APPROVED', 'COMMENTED']);
const NEGATIVE_REVIEW_STATES = new Set(['CHANGES_REQUESTED', 'DISMISSED']);

// Reviewers whose review evidence can satisfy the gate. Each entry is a bot
// identity distinct from the PR author: a human PAT cannot attest to its own
// work, which is the property that makes this gate a control rather than a
// rubber stamp. Widening this list is a supply-chain decision, not a
// convenience one -- add an entry only for an identity that reviews
// independently of whoever wrote the change.
//
// `reviewMarker` recognises a review body as a review at all; `cleanMarker`
// recognises the reviewer's specific "no findings" phrasing. The phrasings are
// per-reviewer on purpose: a generic match would let unrelated prose attest.
const ATTESTING_REVIEWERS = [
  {
    id: 'codex',
    logins: ['chatgpt-codex-connector', 'chatgpt-codex-connector[bot]'],
    reviewMarker: /Codex Review|Reviewed commit/i,
    cleanMarker: /Codex Review:\s+Didn't find any major issues\./i,
  },
  {
    id: 'claude',
    logins: ['claude', 'claude[bot]'],
    reviewMarker: /Claude Review|Reviewed commit/i,
    cleanMarker: /Claude Review:\s+No major issues found\./i,
  },
  {
    // GitHub's Copilot code review (repo ruleset `copilot_code_review`). Unlike
    // the others it posts reviews with an EMPTY body, so there is no phrasing to
    // match: `reviewMarker: null` means "any body, including none".
    //
    // That makes its review a weaker statement -- it proves a reviewer examined
    // this exact commit, not that it declared the commit clean. Two things carry
    // the weight instead: the review is still bound to the head SHA, and because
    // this login is an attesting reviewer, its own unresolved inline threads are
    // counted by the gate's unresolvedCount and block the merge. So Copilot
    // finding something still stops the PR; only "looked and said nothing" passes.
    //
    // It has no cleanMarker on purpose: a body-less comment must never satisfy
    // the clean-comment path, which exists to accept an explicit no-findings
    // statement. Copilot can only attest through the exact-head review path.
    id: 'copilot',
    logins: ['github-code-quality', 'github-code-quality[bot]'],
    reviewMarker: null,
    cleanMarker: null,
  },
];

function reviewerFor(login) {
  if (!login) return null;
  return ATTESTING_REVIEWERS.find(reviewer => reviewer.logins.includes(login)) || null;
}

function isAttestingReviewer(login) {
  return reviewerFor(login) !== null;
}

// Retained name: review-gate.yml and any external caller import `isCodex`.
// The gate is no longer Codex-only, but the exported symbol stays stable.
const isCodex = isAttestingReviewer;

function cleanCommentMatchesHead(comment, head) {
  const reviewer = reviewerFor(comment.author?.login);
  // A reviewer with no cleanMarker (Copilot) cannot attest via a comment at all:
  // this path exists to accept an explicit "no findings" statement, and a
  // body-less comment makes no such statement.
  if (!reviewer || !reviewer.cleanMarker || comment.includesCreatedEdit ||
      comment.createdAt !== comment.updatedAt ||
      !reviewer.cleanMarker.test(comment.body || '')) {
    return false;
  }
  const reviewedCommits = [...(comment.body || '').matchAll(
    /(?:\*\*Reviewed commit:\*\*|Reviewed commit:)\s*`([0-9a-f]{10,40})`/gi
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
  const attestingReviews = reviews
    .filter(review => {
      const reviewer = reviewerFor(review.author?.login);
      if (reviewer === null) return false;
      // reviewMarker === null means the reviewer posts body-less reviews and is
      // recognised by identity + head binding alone (see ATTESTING_REVIEWERS).
      return reviewer.reviewMarker === null || reviewer.reviewMarker.test(review.body || '');
    })
    .sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
  const latest = attestingReviews[0];
  // A negative verdict from ANY attesting reviewer suppresses evidence from
  // every attesting reviewer until something newer supersedes it. Scoping this
  // per-reviewer would let a second reviewer's stale approval paper over a
  // first reviewer's open objection.
  const negativeAt = attestingReviews
    .filter(review => NEGATIVE_REVIEW_STATES.has(review.state))
    .reduce((max, review) => Math.max(max, Date.parse(review.updatedAt || review.submittedAt)), 0);
  const exactReview = unresolvedCount === 0 && latest?.commit?.oid === head &&
    ATTESTING_REVIEW_STATES.has(latest.state) && Date.parse(latest.submittedAt) > negativeAt;
  const exactCleanComment = unresolvedCount === 0 && cleanComments.some(comment =>
    cleanCommentMatchesHead(comment, head) && Date.parse(comment.createdAt) > negativeAt);
  // Generic reactions remain insufficient because they carry no reviewed SHA.
  // A clean reviewer comment is accepted only when it is unedited, names one
  // commit whose uniquely resolved full OID equals the head, and no reviewer
  // finding is open.
  const freshCleanReaction = false;
  return { exactReview, exactCleanComment, freshCleanReaction };
}

if (require.main === module) {
  const fs = require('node:fs');
  const input = JSON.parse(fs.readFileSync(0, 'utf8'));
  process.stdout.write(JSON.stringify(evaluateCodexEvidence(input)));
}

module.exports = {
  evaluateCodexEvidence,
  isCodex,
  isAttestingReviewer,
  reviewerFor,
  ATTESTING_REVIEWERS,
};
