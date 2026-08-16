'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  collectPullRequestSnapshot,
  stabilizePullRequestSnapshot,
  trainSnapshot,
} = require('./review-gate-snapshot');

function connection(nodes, hasNextPage, endCursor) {
  return { nodes, pageInfo: { hasNextPage, endCursor } };
}

function page({ threadPage, reviewsPage = 0, commentsPage = 0 }) {
  const first = threadPage === 0;
  const thread = id => ({
    id,
    isResolved: true,
    comments: connection([], false, null),
  });
  return {
    repository: {
      pullRequest: {
        number: 3163,
        state: 'OPEN',
        isDraft: false,
        headRefOid: 'abc123',
        labels: connection(first ? [{ name: 'ready' }] : [], false, first ? 'label-end' : null),
        reviews: connection(
          [reviewsPage === 0 ? { id: 'review-1' } : { id: 'review-2' }],
          reviewsPage === 0,
          `review-${reviewsPage}`,
        ),
        comments: connection(
          [commentsPage === 0 ? { id: 'comment-1' } : { id: 'comment-2' }],
          commentsPage === 0,
          `comment-${commentsPage}`,
        ),
        reviewThreads: connection(
          threadPage === 0
            ? Array.from({ length: 100 }, (_, index) => thread(`thread-${index}`))
            : Array.from({ length: 9 }, (_, index) => thread(`thread-${100 + index}`)),
          threadPage === 0,
          `thread-${threadPage}`,
        ),
        commits: {
          nodes: [{
            commit: {
              statusCheckRollup: {
                contexts: connection(first ? [{ name: 'PR Gate' }] : [], false, first ? 'check-end' : null),
              },
            },
          }],
        },
      },
    },
  };
}

test('collects review evidence beyond the first 100 threads', async () => {
  const seen = [];
  const result = await collectPullRequestSnapshot(async cursors => {
    seen.push(cursors);
    return page({
      threadPage: cursors.threadsCursor ? 1 : 0,
      reviewsPage: cursors.reviewsCursor ? 1 : 0,
      commentsPage: cursors.commentsCursor ? 1 : 0,
    });
  });

  assert.equal(result.reviewThreads.nodes.length, 109);
  assert.equal(result.reviews.nodes.length, 2);
  assert.equal(result.comments.nodes.length, 2);
  assert.equal(result.labels.nodes.length, 1);
  assert.equal(result.commits.nodes[0].commit.statusCheckRollup.contexts.nodes.length, 1);
  assert.equal(seen.length, 2);
  assert.equal(seen[1].threadsCursor, 'thread-0');

  const normalized = trainSnapshot(result);
  assert.equal(normalized.reviewThreadsTruncated, false);
  assert.equal(normalized.reviewsTruncated, false);
  assert.equal(normalized.commentsTruncated, false);
});

test('fails closed when one thread has more than 100 inline comments', async () => {
  const result = await collectPullRequestSnapshot(async () => {
    const value = page({ threadPage: 1, reviewsPage: 1, commentsPage: 1 });
    value.repository.pullRequest.reviewThreads.nodes[0].comments.pageInfo.hasNextPage = true;
    return value;
  });

  assert.equal(trainSnapshot(result).reviewThreadsTruncated, true);
});

test('rejects a stalled cursor instead of looping', async () => {
  await assert.rejects(
    collectPullRequestSnapshot(async () => page({ threadPage: 0 })),
    /pagination cursor stalled/,
  );
});

test('rejects a head change during pagination', async () => {
  let request = 0;
  await assert.rejects(
    collectPullRequestSnapshot(async () => {
      const value = page({
        threadPage: request === 0 ? 0 : 1,
        reviewsPage: request === 0 ? 0 : 1,
        commentsPage: request === 0 ? 0 : 1,
      });
      request += 1;
      if (request === 2) value.repository.pullRequest.headRefOid = 'def456';
      return value;
    }),
    /pull request changed during pagination/,
  );
});

test('requires two identical complete snapshots', async () => {
  const first = await collectPullRequestSnapshot(async () =>
    page({ threadPage: 1, reviewsPage: 1, commentsPage: 1 }));
  const second = structuredClone(first);
  second.reviewThreads.nodes[0].isResolved = false;
  const snapshots = [first, second];

  await assert.rejects(
    stabilizePullRequestSnapshot(async () => snapshots.shift()),
    /changed while taking its snapshot/,
  );
});
