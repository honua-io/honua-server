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
//
// IDENTITY SHAPES. Evidence reaches this evaluator in two shapes and they spell
// bot logins differently:
//   * REST (`.user.login`)    -> `claude[bot]`, `chatgpt-codex-connector[bot]`
//   * GraphQL (`author.login`) -> `claude`,      `chatgpt-codex-connector`
// The gate reads GraphQL (review-gate-snapshot.js), so the suffix-less spelling
// is the one that actually shows up in production. `logins` matches on the login
// alone; `botLogins` matches ONLY when GitHub also types the author as a `Bot`.
// That distinction is load-bearing for Claude: `claude` is a real GitHub User
// too, and a User can hold a PAT, which would break the
// bot-distinct-from-author property this list depends on.
const ATTESTING_REVIEWERS = [
  {
    id: 'codex',
    // Both spellings are accepted unconditionally: no `chatgpt-codex-connector`
    // User exists, so the suffix-less form cannot be impersonated by a PAT.
    logins: ['chatgpt-codex-connector', 'chatgpt-codex-connector[bot]'],
    botLogins: [],
    reviewMarker: /Codex Review|Reviewed commit/i,
    cleanMarker: /Codex Review:\s+Didn't find any major issues\./i,
  },
  {
    id: 'claude',
    // REST spelling. The bare `claude` account is a real GitHub User, so it is
    // NEVER accepted on login alone -- only via `botLogins` below, which
    // additionally requires GitHub to type the author as a Bot.
    logins: ['claude[bot]'],
    // GraphQL spelling. Verified live: a review posted by the Claude GitHub App
    // comes back from GraphQL as `{login: "claude", __typename: "Bot"}`
    // (anthropics/claude-code-action#1650) while REST reports `claude[bot]`.
    // Without this entry every review the Claude lane posts is invisible to the
    // gate, because review-gate.yml reads the GraphQL shape.
    botLogins: ['claude'],
    reviewMarker: /Claude Review|Reviewed commit/i,
    cleanMarker: /Claude Review:\s+No major issues found\./i,
    // THE canonical body `.github/workflows/claude-review.yml` posts for a clean
    // head, kept here so the lane cannot drift from the marker that grades it.
    // `cleanCommentBody(head)` must satisfy `cleanMarker` and the reviewed-commit
    // parser below; review-gate-evidence.test.js asserts exactly that by feeding
    // the generated body back through `evaluateCodexEvidence`.
    cleanCommentBody: head =>
      `Claude Review: No major issues found.\n\n**Reviewed commit:** \`${head}\``,
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

// `typename` is GitHub's GraphQL `__typename` for the author (`Bot` / `User`),
// or undefined for REST-shaped input. A `botLogins` entry matches only when it
// is exactly `'Bot'`, so an undefined/`User` author can never reach a
// suffix-less bot identity.
function reviewerFor(login, typename) {
  if (!login) return null;
  return ATTESTING_REVIEWERS.find(reviewer =>
    reviewer.logins.includes(login) ||
    (typename === 'Bot' && (reviewer.botLogins || []).includes(login))) || null;
}

function isAttestingReviewer(login, typename) {
  return reviewerFor(login, typename) !== null;
}

// Retained name: review-gate.yml and any external caller import `isCodex`.
// The gate is no longer Codex-only, but the exported symbol stays stable.
const isCodex = isAttestingReviewer;

function cleanCommentMatchesHead(comment, head) {
  const reviewer = reviewerFor(comment.author?.login, comment.author?.__typename);
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
      const reviewer = reviewerFor(review.author?.login, review.author?.__typename);
      return reviewer !== null && reviewer.reviewMarker.test(review.body || '');
    })
    .sort((a, b) => new Date(b.submittedAt) - new Date(a.submittedAt));
  const latest = attestingReviews[0];
  // Negative verdicts are scanned by IDENTITY ONLY -- deliberately NOT by
  // reviewMarker. Computing this from the marker-filtered list (as it did until
  // the first review of #3314) drops a CHANGES_REQUESTED whose body happens not
  // to match its reviewer's marker: a plain-prose objection -- "I found a
  // hardcoded key, blocking" -- would be discarded and an older positive review
  // would attest. An objection counts whatever its wording; only POSITIVE
  // evidence has to be well-formed.
  const negatives = reviews.filter(review =>
    reviewerFor(review.author?.login, review.author?.__typename) !== null &&
    NEGATIVE_REVIEW_STATES.has(review.state));
  const negativeAt = negatives.reduce(
    (max, review) => Math.max(max, Date.parse(review.updatedAt || review.submittedAt)), 0);

  // An objection is cleared ONLY by the identity that raised it.
  //
  // The second review of #3314 caught this: with a single global cutoff, mere
  // recency cleared an objection regardless of who raised it, so Codex could
  // post CHANGES_REQUESTED and a Claude review two minutes later would attest --
  // the exact "papered over by another reviewer" case the previous comment here
  // claimed to prevent while the code did the opposite. Before this PR the
  // property held for free, because only Codex could supersede Codex (a reviewer
  // withdrawing its own objection, which is legitimate). Adding a second
  // identity made it reachable, so it now has to be enforced explicitly.
  // A non-finite timestamp is skipped rather than max'd in: Math.max(prev, NaN)
  // is NaN, which would permanently erase every valid withdrawal from that
  // reviewer. Skipping keeps an undated NEGATIVE open (nothing can be newer than
  // a negative that never enters the map only if it also never blocks -- so
  // undated negatives are recorded as Infinity, which nothing can supersede).
  const newestByReviewer = (entries, timeOf, undatedValue = null) => entries.reduce((acc, entry) => {
    const reviewer = reviewerFor(entry.author?.login, entry.author?.__typename);
    if (!reviewer) return acc;
    const at = timeOf(entry);
    const value = Number.isFinite(at) ? at : undatedValue;
    if (value === null) return acc;
    acc.set(reviewer.id, Math.max(acc.get(reviewer.id) ?? 0, value));
    return acc;
  }, new Map());

  const newestNegative = newestByReviewer(
    negatives, r => Date.parse(r.updatedAt || r.submittedAt), Infinity);

  // A withdrawal is NOT required to be at head. Requiring it would expire the
  // withdrawal on every push, deadlocking the gate exactly when the objecting
  // reviewer is rate-limited -- the case this PR exists for. The accepted
  // residual: X objects to commit A and withdraws about A, the author pushes B,
  // Y attests B, and X is never re-asked about B. That is inherent to any
  // 2-of-N reviewer model rather than a hole in this one.
  //
  // A withdrawal must be at least as strong as the objection it clears.
  //
  // The third review of #3314 caught this: the positives set was unvalidated on
  // both branches, and `cleanComments` is built by review-gate.yml with `.map`,
  // not `.filter` -- it is EVERY comment on the PR, returned unchanged for
  // non-reviewers. So any artifact from an attesting identity read as a
  // withdrawal, including Codex's own "You have reached your Codex usage limits
  // for code review" notice, which this repo emits in normal operation. The
  // reviewer announcing it could not review cleared its own live objection.
  //
  // Reviews must therefore pass the same marker filter as attesting evidence,
  // and comments must pass the full clean-comment check -- an artifact rejected
  // as evidence must not be accepted as a withdrawal.
  const newestPositive = newestByReviewer(
    [
      ...attestingReviews.filter(r => ATTESTING_REVIEW_STATES.has(r.state)),
      ...cleanComments.filter(comment => cleanCommentMatchesHead(comment, head)),
    ],
    entry => Date.parse(entry.submittedAt ?? entry.createdAt));

  // NaN-safe by construction: an unparseable timestamp makes every `>` false, so
  // the objection stays open and the gate fails closed.
  const hasOpenObjection = [...newestNegative].some(
    ([id, negatedAt]) => !((newestPositive.get(id) ?? 0) > negatedAt));

  const exactReview = unresolvedCount === 0 && !hasOpenObjection &&
    latest?.commit?.oid === head &&
    ATTESTING_REVIEW_STATES.has(latest.state) && Date.parse(latest.submittedAt) > negativeAt;
  const exactCleanComment = unresolvedCount === 0 && !hasOpenObjection &&
    cleanComments.some(comment =>
      cleanCommentMatchesHead(comment, head) && Date.parse(comment.createdAt) > negativeAt);
  // Generic reactions remain insufficient because they carry no reviewed SHA.
  // A clean reviewer comment is accepted only when it is unedited, names one
  // commit whose uniquely resolved full OID equals the head, and no reviewer
  // finding is open.
  // Unconditionally false: a reaction carries no reviewed SHA, so it can never
  // attest. Tests asserting `freshCleanReaction === false` therefore pass against
  // any implementation -- do not count them as coverage.
  const freshCleanReaction = false;
  return { exactReview, exactCleanComment, freshCleanReaction };
}

// Every attesting login, flattened. This is THE source of truth for the reviewer
// identity set, exported so no other component has to restate it. The merge
// train's select.sh reads it via `--print-logins` rather than hardcoding logins
// in its own jq -- it recomputes unresolvedCount and then WRITES the required
// `Review Gate` status, so if its login set drifted from this one it could stamp
// the gate green while a reviewer's threads sat unresolved.
// Includes the suffix-less GraphQL spellings: select.sh counts unresolved
// reviewer threads with jq over the GraphQL snapshot, so omitting `claude` there
// would hide every unresolved Claude thread and let the train stamp the gate
// green over open findings. jq cannot see `__typename`, so this set is the
// permissive one; the direction is fail-safe (a stray bare-`claude` User thread
// only makes the train MORE conservative), and positive evidence is still graded
// by `reviewerFor`, which does enforce the Bot check.
const ATTESTING_LOGINS = ATTESTING_REVIEWERS.flatMap(
  reviewer => [...reviewer.logins, ...(reviewer.botLogins || [])]);

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
