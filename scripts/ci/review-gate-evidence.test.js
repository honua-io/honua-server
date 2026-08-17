'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { evaluateCodexEvidence } = require('./review-gate-evidence');
const head = 'abc123';
const review = (state, submittedAt = '2026-01-02T00:00:00Z', updatedAt = submittedAt) => ({ author: { login: 'chatgpt-codex-connector' }, body: '### Codex Review\nReviewed commit', submittedAt, updatedAt, commit: { oid: head }, state });
const cleanComment = (commit = head, overrides = {}) => ({
  author: { login: 'chatgpt-codex-connector' },
  body: `Codex Review: Didn't find any major issues. Hooray!\n\n**Reviewed commit:** \`${commit}\``,
  createdAt: '2026-01-02T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  includesCreatedEdit: false,
  ...overrides,
});
test('active exact-head Codex review attests', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('COMMENTED')], unresolvedCount: 0, head }).exactReview, true);
});
test('unedited Codex clean comment for exact head attests', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      resolvedCommitOid: fullHead,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, true);
});
test('unedited clean comment with a plain commit anchor attests', () => {
  const fullHead = 'b'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      body: `Codex Review: Didn't find any major issues. Clear!\n\nReviewed commit: \`${fullHead.slice(0, 10)}\``,
      resolvedCommitOid: fullHead,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, true);
});
test('unresolved short SHA cannot attest', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10))],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, false);
});
test('short SHA resolving to another full commit cannot attest', () => {
  const fullHead = 'a'.repeat(40);
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment(fullHead.slice(0, 10), {
      resolvedCommitOid: `${'a'.repeat(10)}${'f'.repeat(30)}`,
    })],
    unresolvedCount: 0, head: fullHead,
  }).exactCleanComment, false);
});
test('clean comment for another head cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('def4567890')],
    unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('edited clean comment cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      updatedAt: '2026-01-03T00:00:00Z',
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('clean comment created with an edit cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      includesCreatedEdit: true,
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('non-Codex clean comment cannot attest', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567', {
      author: { login: 'contributor' },
    })], unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('clean comment with multiple reviewed SHAs cannot attest', () => {
  const comment = cleanComment('abc1234567');
  comment.body += '\n**Reviewed commit:** `abc1234568`';
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [comment],
    unresolvedCount: 0, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('unresolved finding overrides an exact-head clean comment', () => {
  assert.equal(evaluateCodexEvidence({
    reviews: [], cleanComments: [cleanComment('abc1234567')],
    unresolvedCount: 1, head: 'abc1234567890abcdef1234567890abcdef1234',
  }).exactCleanComment, false);
});
test('dismissed exact-head Codex review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('DISMISSED')], unresolvedCount: 0, head }).exactReview, false);
});
test('changes-requested exact-head review cannot attest', () => {
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED')], unresolvedCount: 0, head }).exactReview, false);
});
test('clean reaction before a negative review cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('clean reaction after a negative review cannot attest without a commit-bound review', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [review('CHANGES_REQUESTED', '2026-01-03T00:00:00Z')], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('later nonnegative exact-head review supersedes negative review', () => {
  const reviews = [review('CHANGES_REQUESTED', '2026-01-02T00:00:00Z'), review('COMMENTED', '2026-01-03T00:00:00Z')];
  assert.equal(evaluateCodexEvidence({ reviews, unresolvedCount: 0, head }).exactReview, true);
});
test('unresolved finding overrides clean reaction on same head', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 1, head }).freshCleanReaction, false);
});
test('generic PR reaction cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactions, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('late old-head reaction after a new suite cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-05T00:00:00Z' }];
  const reactionArtifacts = [{ body: '@codex review old123', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('editing an old reacted artifact to the new head cannot rebind it', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-02T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-03T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('multi-SHA reaction artifact cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:01Z' }];
  const reactionArtifacts = [{ body: `@codex review old123 ${head}`, createdAt: '2026-01-04T00:00:00Z', updatedAt: '2026-01-04T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('pre-staged current-SHA reaction artifact cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-05T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});
test('same-second edit and reaction cannot attest', () => {
  const reactions = [{ user: { login: 'chatgpt-codex-connector[bot]' }, content: '+1', created_at: '2026-01-04T00:00:00Z' }];
  const reactionArtifacts = [{ body: `@codex review ${head}`, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-04T00:00:00Z', reactions }];
  assert.equal(evaluateCodexEvidence({ reviews: [], reactionArtifacts, unresolvedCount: 0, head }).freshCleanReaction, false);
});

// --- Second attesting identity: claude[bot]. Every safety property is
// re-asserted here, so widening the reviewer set cannot quietly widen what
// counts as evidence.
//
// NOTE: the clean-comment path requires a 10-40 hex reviewed SHA, so these use a
// full 40-char head rather than the short module-level `head`. With the short one
// the assertions would pass for the wrong reason (regex miss, not policy).
const claudeHead = 'c'.repeat(40);
const claudeReview = (state, submittedAt = '2026-01-02T00:00:00Z', oid = claudeHead) => ({
  author: { login: 'claude[bot]' },
  body: '### Claude Review\nReviewed commit',
  submittedAt, updatedAt: submittedAt, commit: { oid }, state,
});
const claudeCleanComment = (overrides = {}) => ({
  author: { login: 'claude[bot]' },
  body: `Claude Review: No major issues found.\n\n**Reviewed commit:** \`${claudeHead}\``,
  createdAt: '2026-01-02T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  includesCreatedEdit: false,
  ...overrides,
});
const ev = (input) => evaluateCodexEvidence({ reviews: [], unresolvedCount: 0, head: claudeHead, ...input });

test('active exact-head Claude review attests', () => {
  assert.equal(ev({ reviews: [claudeReview('COMMENTED')] }).exactReview, true);
});
test('unedited Claude clean comment for exact head attests', () => {
  assert.equal(ev({ cleanComments: [claudeCleanComment()] }).exactCleanComment, true);
});
test('Claude review for another head cannot attest', () => {
  assert.equal(ev({ reviews: [claudeReview('COMMENTED', '2026-01-02T00:00:00Z', 'd'.repeat(40))] }).exactReview, false);
});
test('edited Claude clean comment cannot attest', () => {
  assert.equal(ev({ cleanComments: [claudeCleanComment({ updatedAt: '2026-01-03T00:00:00Z' })] }).exactCleanComment, false);
});
test('Claude clean comment created with an edit cannot attest', () => {
  assert.equal(ev({ cleanComments: [claudeCleanComment({ includesCreatedEdit: true })] }).exactCleanComment, false);
});
test('unresolved finding overrides an exact-head Claude clean comment', () => {
  assert.equal(ev({ cleanComments: [claudeCleanComment()], unresolvedCount: 1 }).exactCleanComment, false);
});
test('the bare `claude` User account is not an attesting identity', () => {
  // Only claude[bot]. A User can hold a PAT, which would break the
  // bot-distinct-from-author property the reviewer list depends on.
  assert.equal(ev({ reviews: [claudeReview('COMMENTED')].map(r => ({ ...r, author: { login: 'claude' } })) }).exactReview, false);
});
test('Codex clean phrasing from the Claude identity cannot attest', () => {
  // Markers are per-reviewer: replaying one reviewer's text under another
  // identity must not attest.
  assert.equal(ev({
    cleanComments: [claudeCleanComment({ body: `Codex Review: Didn't find any major issues.\n\n**Reviewed commit:** \`${claudeHead}\`` })],
  }).exactCleanComment, false);
});

// --- negativeAt is identity-scoped, not marker-scoped (#3314 review finding 3).
const codexReview = (state, at, body = '### Codex Review\nReviewed commit') => ({
  author: { login: 'chatgpt-codex-connector[bot]' }, body,
  submittedAt: at, updatedAt: at, commit: { oid: claudeHead }, state,
});

test('a CHANGES_REQUESTED whose body lacks its reviewer marker still suppresses', () => {
  // Regression for the exploit found reviewing #3314: negativeAt was reduced
  // over the marker-FILTERED list, so a plain-prose objection was dropped
  // before the negative scan and an older positive review attested.
  const proseObjection = {
    author: { login: 'claude[bot]' },
    body: 'I found a hardcoded AWS key in config.ts. Blocking.',
    submittedAt: '2026-01-09T00:00:00Z', updatedAt: '2026-01-09T00:00:00Z',
    commit: { oid: claudeHead }, state: 'CHANGES_REQUESTED',
  };
  assert.equal(ev({ reviews: [proseObjection, claudeReview('COMMENTED', '2026-01-02T00:00:00Z')] }).exactReview, false);
  assert.equal(ev({ reviews: [proseObjection], cleanComments: [claudeCleanComment()] }).exactCleanComment, false);
});
test('a markerless Codex CHANGES_REQUESTED also suppresses', () => {
  assert.equal(ev({
    reviews: [codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z', 'blocking, see inline')],
    cleanComments: [claudeCleanComment()],
  }).exactCleanComment, false);
});
test('one reviewer objection suppresses the other reviewer clean comment', () => {
  assert.equal(ev({
    reviews: [codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [claudeCleanComment()],
  }).exactCleanComment, false);
});
test('a genuinely LATER clean comment from the OBJECTING reviewer is accepted', () => {
  // The recovery branch: without it an objection would be permanent and the gate
  // could never go green again. The suppression tests above use an EARLIER
  // comment, the trivial direction, which does not prove this.
  //
  // Recovery must come from the SAME identity that objected. These two tests
  // originally used a Codex objection cleared by a Claude approval -- which is
  // the cross-reviewer hole the second #3314 review found, not a recovery case.
  const later = claudeCleanComment({ createdAt: '2026-01-11T00:00:00Z', updatedAt: '2026-01-11T00:00:00Z' });
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [later],
  }).exactCleanComment, true);
});
test('a genuinely LATER review from the OBJECTING reviewer is accepted', () => {
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'), claudeReview('COMMENTED', '2026-01-11T00:00:00Z')],
  }).exactReview, true);
});
test('the exported login set matches the registry', () => {
  const m = require('./review-gate-evidence');
  assert.deepEqual(m.ATTESTING_LOGINS,
    ['chatgpt-codex-connector', 'chatgpt-codex-connector[bot]', 'claude[bot]', 'claude']);
  assert.equal(m.isAttestingReviewer('github-code-quality[bot]'), false);
});

// --- An objection is cleared only by the identity that raised it (#3314 review 2).
// A single global cutoff let mere recency clear an objection regardless of who
// raised it, so Codex could object and a Claude review two minutes later would
// attest. Before the second identity existed this held for free.
test('another reviewer approval does NOT clear an open objection', () => {
  assert.equal(ev({
    reviews: [
      codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      claudeReview('COMMENTED', '2026-01-09T00:02:00Z'),
    ],
  }).exactReview, false);
});
test('another reviewer clean comment does NOT clear an open objection', () => {
  assert.equal(ev({
    reviews: [codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [claudeCleanComment({ createdAt: '2026-01-09T00:02:00Z', updatedAt: '2026-01-09T00:02:00Z' })],
  }).exactCleanComment, false);
});
test('the objecting reviewer CAN withdraw its own objection', () => {
  // The legitimate case the per-identity rule must keep working.
  assert.equal(ev({
    reviews: [
      codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      codexReview('COMMENTED', '2026-01-11T00:00:00Z'),
    ],
  }).exactReview, true);
});
test('a withdrawn objection from one reviewer still blocks on another open one', () => {
  assert.equal(ev({
    reviews: [
      codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      codexReview('COMMENTED', '2026-01-11T00:00:00Z'),
      claudeReview('CHANGES_REQUESTED', '2026-01-12T00:00:00Z'),
    ],
  }).exactReview, false);
});
test('an objection with an unparseable timestamp stays open (fails closed)', () => {
  const broken = { ...codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'), submittedAt: undefined, updatedAt: undefined };
  assert.equal(ev({ reviews: [broken, claudeReview('COMMENTED', '2026-01-11T00:00:00Z')] }).exactReview, false);
});

// --- A withdrawal must be at least as strong as the objection (#3314 review 3).
// `cleanComments` is built with `.map`, not `.filter`, so it is EVERY comment on
// the PR. Treating any artifact from an attesting identity as a withdrawal let a
// Codex rate-limit notice -- "You have reached your Codex usage limits for code
// review", which this repo emits in normal operation -- clear a live objection.
const codexProseComment = (at) => ({
  author: { login: 'chatgpt-codex-connector' },
  body: 'You have reached your Codex usage limits for code review.',
  createdAt: at, updatedAt: at, includesCreatedEdit: false,
});

test('a rate-limit notice does NOT withdraw the objection that reviewer raised', () => {
  const input = {
    reviews: [codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [codexProseComment('2026-01-09T00:30:00Z'),
      claudeCleanComment({ createdAt: '2026-01-10T00:00:00Z', updatedAt: '2026-01-10T00:00:00Z' })],
  };
  assert.equal(ev(input).exactCleanComment, false);
  assert.equal(ev({ ...input, reviews: [...input.reviews, claudeReview('COMMENTED', '2026-01-10T00:00:00Z')] }).exactReview, false);
});
test('an arbitrary prose comment does NOT withdraw an objection', () => {
  assert.equal(ev({
    reviews: [codexReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      claudeReview('COMMENTED', '2026-01-11T00:00:00Z')],
    cleanComments: [{ ...codexProseComment('2026-01-10T00:00:00Z'), body: 'working on it' }],
  }).exactReview, false);
});
test('an EDITED clean comment does NOT withdraw an objection', () => {
  // Rejected as evidence by cleanCommentMatchesHead; must not be accepted as a
  // withdrawal either.
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [claudeCleanComment({ createdAt: '2026-01-10T00:00:00Z', updatedAt: '2026-01-11T00:00:00Z' })],
  }).exactCleanComment, false);
});
test('a markerless review does NOT withdraw an objection', () => {
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      { ...claudeReview('COMMENTED', '2026-01-11T00:00:00Z'), body: 'here are more findings' }],
  }).exactReview, false);
});
test('a timestamp-less entry does not erase a valid withdrawal', () => {
  // NaN poisoning: Math.max(prev, NaN) is NaN, which would permanently erase
  // every valid withdrawal from that reviewer.
  const withdrawal = claudeReview('COMMENTED', '2026-01-11T00:00:00Z');
  const broken = { ...claudeCleanComment(), createdAt: undefined, updatedAt: undefined };
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'), withdrawal],
    cleanComments: [broken],
  }).exactReview, true);
});
test('per-identity rule blocks even when the global recency cutoff would allow it', () => {
  // Isolates hasOpenObjection from the global negativeAt check: the newest
  // artifact of all is claude's approval, so recency alone would pass.
  assert.equal(ev({
    reviews: [
      claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      claudeReview('COMMENTED', '2026-01-10T00:00:00Z'),
      codexReview('CHANGES_REQUESTED', '2026-01-11T00:00:00Z'),
      claudeReview('COMMENTED', '2026-01-12T00:00:00Z'),
    ],
  }).exactReview, false);
});

// --- The global negativeAt cutoff, isolated (#3314 review 3, finding 15).
// Mutating it away previously failed zero tests, so it could not be relied on as
// a second layer. These two fail if it is removed.
test('a clean comment predating a since-withdrawn objection does not attest', () => {
  assert.equal(ev({
    reviews: [
      claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      claudeReview('COMMENTED', '2026-01-12T00:00:00Z'),
    ],
    cleanComments: [claudeCleanComment({ createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' })],
  }).exactCleanComment, false);
});
test('an undated objection can never be superseded (fails closed forever)', () => {
  // Recorded as Infinity rather than skipped: an objection we cannot date must
  // not be silently droppable by anything that happens to carry a timestamp.
  const undated = { ...claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'), submittedAt: undefined, updatedAt: undefined };
  assert.equal(ev({
    reviews: [undated, claudeReview('COMMENTED', '2026-01-12T00:00:00Z')],
  }).exactReview, false);
});

// --- The review branch's marker filter, actually covered (#3314 review 4,
// finding 19). Test 46 named this property but never reached the withdrawal
// logic -- it failed earlier, on `latest` being the objection itself. Mutation
// testing showed the filter had ZERO coverage. This construction supplies valid
// head evidence from the OTHER identity, so evaluation gets far enough for the
// withdrawal check to be the deciding factor.
test('a markerless review does not withdraw, even when other head evidence exists', () => {
  const input = {
    reviews: [
      claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z'),
      { ...claudeReview('COMMENTED', '2026-01-10T00:00:00Z'), body: 'here are more findings' },
    ],
    cleanComments: [{
      author: { login: 'chatgpt-codex-connector' },
      body: `Codex Review: Didn't find any major issues.\n\n**Reviewed commit:** \`${claudeHead}\``,
      createdAt: '2026-01-11T00:00:00Z', updatedAt: '2026-01-11T00:00:00Z', includesCreatedEdit: false,
    }],
  };
  // Claude's objection is still open: a body without its reviewer's marker is
  // not a withdrawal, so Codex's otherwise-valid head evidence cannot carry it.
  assert.equal(ev(input).exactCleanComment, false);
});
test('an edited clean comment does not withdraw, even when other head evidence exists', () => {
  // Finding 20: the original version of this test passed vacuously (its input
  // held no valid evidence at all). This one bites in both directions.
  assert.equal(ev({
    reviews: [claudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [
      claudeCleanComment({ createdAt: '2026-01-10T00:00:00Z', updatedAt: '2026-01-11T00:00:00Z' }),
      {
        author: { login: 'chatgpt-codex-connector' },
        body: `Codex Review: Didn't find any major issues.\n\n**Reviewed commit:** \`${claudeHead}\``,
        createdAt: '2026-01-12T00:00:00Z', updatedAt: '2026-01-12T00:00:00Z', includesCreatedEdit: false,
      },
    ],
  }).exactCleanComment, false);
});


// --- The GraphQL identity shape the gate ACTUALLY reads (#3213).
//
// review-gate.yml grades evidence from review-gate-snapshot.js, i.e. GraphQL,
// and GraphQL reports bot logins WITHOUT the `[bot]` suffix. Verified live:
// a Claude App review is `{login: "claude", __typename: "Bot"}` on
// anthropics/claude-code-action#1650, while REST reports `claude[bot]`.
// Every fixture below therefore uses the GraphQL shape; the REST-shaped
// fixtures earlier in this file continue to cover the other spelling.
const graphqlClaudeReview = (state, submittedAt = '2026-01-02T00:00:00Z', typename = 'Bot') => ({
  author: { login: 'claude', __typename: typename },
  body: '### Claude Review\nReviewed commit',
  submittedAt, updatedAt: submittedAt, commit: { oid: claudeHead }, state,
});

test('a GraphQL-shaped Claude review attests', () => {
  // The regression that made the whole lane dead evidence: `claude` was not an
  // accepted login, so nothing the lane posted was ever visible to the gate.
  assert.equal(ev({ reviews: [graphqlClaudeReview('COMMENTED')] }).exactReview, true);
});
test('the bare `claude` User still cannot attest', () => {
  // `claude` is a real GitHub User. Accepting it on login alone would let a PAT
  // holder attest, breaking the bot-distinct-from-author property.
  assert.equal(ev({ reviews: [graphqlClaudeReview('COMMENTED', '2026-01-02T00:00:00Z', 'User')] }).exactReview, false);
  const noTypename = { ...graphqlClaudeReview('COMMENTED') };
  delete noTypename.author.__typename;
  assert.equal(ev({ reviews: [{ ...noTypename, author: { login: 'claude' } }] }).exactReview, false);
});
test('a GraphQL-shaped Claude objection still suppresses', () => {
  assert.equal(ev({
    reviews: [graphqlClaudeReview('CHANGES_REQUESTED', '2026-01-09T00:00:00Z')],
    cleanComments: [claudeCleanComment()],
  }).exactCleanComment, false);
});
test('a GraphQL-shaped Codex review attests', () => {
  assert.equal(ev({
    reviews: [{
      author: { login: 'chatgpt-codex-connector', __typename: 'Bot' },
      body: '### Codex Review\nReviewed commit',
      submittedAt: '2026-01-02T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
      commit: { oid: claudeHead }, state: 'COMMENTED',
    }],
  }).exactReview, true);
});

// --- The body the Claude lane posts is generated, not transcribed.
//
// `claude-review-body.js` reads the template AND the grading marker from the
// same registry entry that decides whether the text attests, so these tests
// fail if either half drifts.
const { buildCleanCommentBody } = require('./claude-review-body');

test('the generated clean body attests in the GraphQL shape the gate reads', () => {
  const body = buildCleanCommentBody(claudeHead);
  assert.equal(ev({
    cleanComments: [{
      author: { login: 'claude', __typename: 'Bot' },
      body,
      createdAt: '2026-01-02T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
      includesCreatedEdit: false,
    }],
  }).exactCleanComment, true);
});
test('the generated clean body cannot attest for a different head', () => {
  const body = buildCleanCommentBody('d'.repeat(40));
  assert.equal(ev({
    cleanComments: [{
      author: { login: 'claude', __typename: 'Bot' },
      body,
      createdAt: '2026-01-02T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
      includesCreatedEdit: false,
    }],
  }).exactCleanComment, false);
});
test('the body builder refuses anything that would not attest', () => {
  assert.throws(() => buildCleanCommentBody('abc123'), /40-character/);
  assert.throws(() => buildCleanCommentBody(claudeHead, 'codex'), /no clean-comment template/);
});
