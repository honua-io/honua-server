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
    // `claude[bot]` only -- the bare `claude` account is a real GitHub User and
    // a User can hold a PAT, which would break the bot-distinct-from-author
    // property this list depends on.
    id: 'claude',
    logins: ['claude[bot]'],
    reviewMarker: /Claude Review|Reviewed commit/i,
    cleanMarker: /Claude Review:\s+No major issues found\./i,
  },
];

// DELIBERATELY NOT ACCEPTED: GitHub Copilot code review (`github-code-quality[bot]`,
// repo ruleset 19481638 / `copilot_code_review`). It was added here and then
// removed, because a body-less reviewer cannot safely attest under this design:
//
//   * Its review states no verdict. `COMMENTED` with an empty body does not
//     distinguish "looked and found nothing" from "looked and commented inline".
//   * The only compensating control would be `unresolvedCount`, and that filter
//     is head-scoped: a thread is counted only when its comment sits on the
//     CURRENT head. Observed on PR #3197 -- the body-less review was on head
//     `ac365415` while its own findings were anchored to `6f3fd7a9`/`d8a280da`.
//     With `review_on_push` re-posting a review per head, still-applicable
//     findings from earlier commits become invisible and the gate goes green
//     with open findings.
//
// Accepting it would need the unresolved filter to stop being head-scoped for
// verdict-less reviewers. Until that exists, this identity stays out.

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
  // Defensive: a reviewer without a cleanMarker cannot attest via a comment at
  // all. This path exists to accept an explicit "no findings" statement, and a
  // reviewer with no such phrasing makes no such statement. Every current entry
  // has one; this guard keeps that a precondition rather than an assumption if
  // a verdict-less identity is ever added.
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
      return reviewer !== null && reviewer.reviewMarker.test(review.body || '');
    })
    .sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
  const latest = attestingReviews[0];
  // negativeAt is scoped by IDENTITY ONLY -- deliberately NOT by reviewMarker.
  //
  // Computing it from the marker-filtered list (as this did until the reviewer
  // on #3314 caught it) means a CHANGES_REQUESTED whose body happens not to
  // match its reviewer's marker is dropped before the negative scan, leaving
  // negativeAt = 0. A reviewer writing a plain-prose objection -- "I found a
  // hardcoded key, blocking" -- would be discarded, and an older positive review
  // would then attest. An objection must count as an objection whatever its
  // wording; only POSITIVE evidence has to be well-formed.
  //
  // It also spans reviewers: one reviewer's open objection must not be papered
  // over by another reviewer's approval.
  const negativeAt = reviews
    .filter(review => reviewerFor(review.author?.login) !== null &&
      NEGATIVE_REVIEW_STATES.has(review.state))
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

// Every attesting login, flattened. This is THE source of truth for the reviewer
// identity set, exported so no other component has to restate it. The merge
// train's select.sh reads it via `--print-logins` rather than hardcoding logins
// in its own jq -- it recomputes unresolvedCount and then WRITES the required
// `Review Gate` status, so if its login set drifted from this one it could stamp
// the gate green while a reviewer's threads sat unresolved.
const ATTESTING_LOGINS = ATTESTING_REVIEWERS.flatMap(reviewer => reviewer.logins);

if (require.main === module) {
  if (process.argv.includes('--print-logins')) {
    process.stdout.write(ATTESTING_LOGINS.join('\n') + '\n');
  } else {
    const fs = require('node:fs');
    const input = JSON.parse(fs.readFileSync(0, 'utf8'));
    process.stdout.write(JSON.stringify(evaluateCodexEvidence(input)));
  }
}

module.exports = {
  evaluateCodexEvidence,
  isCodex,
  isAttestingReviewer,
  reviewerFor,
  ATTESTING_REVIEWERS,
  ATTESTING_LOGINS,
};
