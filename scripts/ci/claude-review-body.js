'use strict';

// Builds THE body `.github/workflows/claude-review.yml` posts for a clean head.
//
// The workflow never spells the body itself. It calls this script, which reads
// the template and the grading marker from the same registry entry in
// review-gate-evidence.js that decides whether the posted text attests. A body
// that would not attest is refused here rather than posted and silently
// ignored, so the lane cannot drift from the gate that grades it.

const {
  ATTESTING_REVIEWERS,
  evaluateCodexEvidence,
} = require('./review-gate-evidence');

const SHA = /^[0-9a-f]{40}$/;
const REVIEWER_ID = 'claude';

function reviewerEntry(id = REVIEWER_ID) {
  const reviewer = ATTESTING_REVIEWERS.find(candidate => candidate.id === id);
  if (!reviewer) throw new Error(`unknown attesting reviewer '${id}'`);
  if (typeof reviewer.cleanCommentBody !== 'function') {
    throw new Error(`attesting reviewer '${id}' declares no clean-comment template`);
  }
  return reviewer;
}

// Build the clean body and PROVE it attests by feeding it back through the real
// evaluator in the exact shape the gate sees (GraphQL: suffix-less bot login
// plus `__typename`). Any drift -- marker, template, reviewed-commit parser,
// identity spelling -- fails here instead of producing dead evidence.
function buildCleanCommentBody(head, id = REVIEWER_ID) {
  if (!SHA.test(String(head || ''))) {
    throw new Error('head must be a full 40-character lowercase commit sha');
  }
  const reviewer = reviewerEntry(id);
  const body = reviewer.cleanCommentBody(head);
  if (!reviewer.cleanMarker.test(body)) {
    throw new Error(`generated body does not satisfy the ${id} cleanMarker`);
  }
  const login = (reviewer.botLogins || [])[0] || reviewer.logins[0];
  const { exactCleanComment } = evaluateCodexEvidence({
    reviews: [],
    cleanComments: [{
      author: { login, __typename: 'Bot' },
      body,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      includesCreatedEdit: false,
    }],
    unresolvedCount: 0,
    head,
  });
  if (!exactCleanComment) {
    throw new Error(`generated body does not attest for ${id} at ${head}`);
  }
  return body;
}

if (require.main === module) {
  const [flag, head] = process.argv.slice(2);
  if (flag !== '--clean-body' || !head) {
    process.stderr.write('Usage: claude-review-body.js --clean-body <40-char-head-sha>\n');
    process.exitCode = 2;
  } else {
    process.stdout.write(buildCleanCommentBody(head));
  }
}

module.exports = { buildCleanCommentBody, reviewerEntry, REVIEWER_ID };
