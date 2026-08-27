'use strict';

const {
  ATTESTING_LOGINS,
  evaluateCodexEvidence,
} = require('./review-gate-evidence');
const {
  fetchPullRequestSnapshot,
  trainSnapshot,
} = require('./review-gate-snapshot');

const DEFAULT_LIMIT = 3;
const DEFAULT_MIN_AGE_MINUTES = 30;
const DISPATCH_TITLE_PREFIX = 'Claude catch-up';

function latestBy(items, keyOf, startedAtOf) {
  const latest = new Map();
  for (const item of items) {
    const key = keyOf(item);
    const current = latest.get(key);
    if (!current || Date.parse(startedAtOf(item) || 0) > Date.parse(startedAtOf(current) || 0)) {
      latest.set(key, item);
    }
  }
  return latest;
}

// GitHub App that publishes this repository's canonical `PR Gate`. Branch
// protection binds the required check to the same app id, so anything published
// under that context by another producer is not the gate the train honours.
const CANONICAL_GATE_APP_ID = 15368;

function isCanonicalProducer(item) {
  const appId = item?.app?.id;
  // Absent app metadata means the producer cannot be established. Fail closed:
  // an unattributable success must not be read as a green canonical gate.
  return appId === CANONICAL_GATE_APP_ID;
}

function currentPrGate(statusResponse, checkRuns) {
  // The combined status endpoint has already reduced commit statuses to the
  // latest state per context. Do not replace it with /statuses (raw history).
  // Bind to the canonical producer as well as the name: `PR Gate` is a context
  // string anyone can publish, and matching the name alone lets a success from
  // some other producer mask a failing canonical gate -- the catch-up would then
  // spend a paid review on a head that is not actually green.
  const status = (statusResponse.statuses || []).find(
    item => item.context === 'PR Gate' && isCanonicalProducer(item),
  );
  // The check-runs endpoint returns retry history. Reduce it independently by
  // name and started_at so an old failed attempt cannot make a green head look
  // current (or an old success hide a current failure).
  const check = latestBy(
    (checkRuns || []).filter(isCanonicalProducer),
    item => item.name,
    item => item.started_at,
  ).get('PR Gate');
  const successful = [];
  if (status?.state?.toLowerCase() === 'success') {
    successful.push({ kind: 'status', at: status.updated_at || status.created_at });
  }
  if (check?.status?.toLowerCase() === 'completed' && check?.conclusion?.toLowerCase() === 'success') {
    successful.push({ kind: 'check', at: check.completed_at || check.started_at });
  }
  return successful.sort((a, b) => Date.parse(b.at || 0) - Date.parse(a.at || 0))[0] || null;
}

function dispatchTitle(number, head) {
  return `${DISPATCH_TITLE_PREFIX} #${number} @ ${head}`;
}

async function resolveCleanCommentCommits(github, repo, comments) {
  return Promise.all(comments.map(async comment => {
    if (comment.resolvedCommitOid || !ATTESTING_LOGINS.includes(comment.author?.login)) return comment;
    const matches = [...String(comment.body || '').matchAll(
      /(?:\*\*Reviewed commit:\*\*|Reviewed commit:)\s*`([0-9a-f]{10,40})`/gi,
    )];
    if (matches.length !== 1) return comment;
    const reference = matches[0][1];
    if (reference.length === 40) return { ...comment, resolvedCommitOid: reference };
    try {
      const { data } = await github.rest.repos.getCommit({ ...repo, ref: reference });
      return { ...comment, resolvedCommitOid: data.sha };
    } catch {
      return comment;
    }
  }));
}

async function hasCleanExactHeadAttestation(github, repo, number, expectedHead) {
  const snapshot = await fetchPullRequestSnapshot(github, repo.owner, repo.repo, number);
  if (!snapshot) return { eligible: false, clean: false, reason: 'snapshot unavailable' };
  const state = trainSnapshot(snapshot);
  if (state.state !== 'OPEN' || state.headRefOid !== expectedHead) {
    return { eligible: false, clean: false, reason: 'attested-and-superseded' };
  }
  if (state.labelsTruncated || state.reviewsTruncated || state.commentsTruncated ||
      state.reviewThreadsTruncated) {
    return { eligible: false, clean: false, reason: 'evidence snapshot truncated' };
  }
  const unresolvedCount = state.reviewThreads.filter(thread =>
    !thread.isResolved && thread.comments.nodes.some(comment =>
      ATTESTING_LOGINS.includes(comment.author?.login) && comment.commit?.oid === expectedHead,
    )).length;
  const cleanComments = await resolveCleanCommentCommits(
    github, repo, state.cleanComments || [],
  );
  const evidence = evaluateCodexEvidence({
    reviews: state.reviews || [],
    cleanComments,
    unresolvedCount,
    head: expectedHead,
  });
  return {
    eligible: true,
    clean: evidence.exactReview || evidence.exactCleanComment,
    reason: evidence.exactReview || evidence.exactCleanComment
      ? 'clean exact-head attestation'
      : 'no clean exact-head attestation',
  };
}

async function listPriorCatchupTitles(github, repo, workflowId) {
  const runs = await github.paginate(
    github.rest.actions.listWorkflowRuns,
    { ...repo, workflow_id: workflowId, event: 'workflow_dispatch', per_page: 100 },
  );
  return new Set(runs.map(run => run.display_title));
}

async function enumerateCatchups({
  github,
  repo,
  workflowId = 'claude-review.yml',
  limit = DEFAULT_LIMIT,
  minAgeMinutes = DEFAULT_MIN_AGE_MINUTES,
  now = new Date(),
}) {
  const priorTitles = await listPriorCatchupTitles(github, repo, workflowId);
  const { data: repository } = await github.rest.repos.get(repo);
  const pullRequests = await github.paginate(github.rest.pulls.list, {
    ...repo,
    state: 'open',
    sort: 'created',
    direction: 'asc',
    per_page: 100,
  });
  const selected = [];
  const decisions = [];
  const cutoff = now.getTime() - minAgeMinutes * 60_000;

  for (const pr of pullRequests) {
    let reason;
    if (pr.draft) reason = 'draft';
    else if (pr.base?.repo?.full_name !== `${repo.owner}/${repo.repo}`) reason = 'unexpected base repository';
    else if (pr.base?.ref !== repository.default_branch) reason = `does not target ${repository.default_branch}`;
    else if (pr.head?.repo?.full_name !== `${repo.owner}/${repo.repo}`) reason = 'fork';
    // Every documented hold mechanism must suppress a paid review, not just the
    // two the train itself keys on.
    else if ((pr.labels || []).some(
      label => ['hold', 'train:hold', 'train:escalated'].includes(label.name),
    )) {
      reason = 'held/escalated';
    }

    const head = pr.head?.sha;
    if (!reason && priorTitles.has(dispatchTitle(pr.number, head))) reason = 'already triggered for this head';

    let gate;
    if (!reason) {
      const [{ data: status }, checks] = await Promise.all([
        github.rest.repos.getCombinedStatusForRef({ ...repo, ref: head }),
        github.paginate(github.rest.checks.listForRef, { ...repo, ref: head, per_page: 100 }),
      ]);
      gate = currentPrGate(status, checks);
      if (!gate) reason = 'current PR Gate is not green';
      else if (!gate.at || Date.parse(gate.at) > cutoff) reason = 'green head is younger than 30 minutes';
    }

    if (!reason) {
      // A push, label, comment or review landing between the snapshot's two
      // stability reads makes it throw. Contain that to this PR: letting it
      // reject the whole enumeration discarded every earlier selection and let
      // one busy PR starve catch-up for all the others, indefinitely.
      try {
        const evidence = await hasCleanExactHeadAttestation(github, repo, pr.number, head);
        reason = !evidence.eligible || evidence.clean ? evidence.reason : null;
      } catch (error) {
        reason = `attestation snapshot unstable: ${error?.message || error}`;
      }
    }

    if (!reason && selected.length >= limit) reason = `bounded after ${limit} selections`;
    const decision = { number: pr.number, head, selected: !reason, reason: reason || 'selected' };
    decisions.push(decision);
    if (!reason) selected.push({ number: pr.number, head });
  }
  return { selected, decisions };
}

module.exports = {
  CANONICAL_GATE_APP_ID,
  DEFAULT_LIMIT,
  DEFAULT_MIN_AGE_MINUTES,
  DISPATCH_TITLE_PREFIX,
  currentPrGate,
  dispatchTitle,
  enumerateCatchups,
  hasCleanExactHeadAttestation,
  latestBy,
};
