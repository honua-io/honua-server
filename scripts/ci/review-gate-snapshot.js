'use strict';

const { execFileSync } = require('node:child_process');

const QUERY = `
query(
  $owner: String!,
  $repo: String!,
  $number: Int!,
  $labelsCursor: String,
  $reviewsCursor: String,
  $commentsCursor: String,
  $threadsCursor: String,
  $checksCursor: String,
  $includeChecks: Boolean!
) {
  repository(owner: $owner, name: $repo) {
    pullRequest(number: $number) {
      number state isDraft headRefOid
      labels(first: 100, after: $labelsCursor) {
        nodes { name }
        pageInfo { hasNextPage endCursor }
      }
      reviews(first: 100, after: $reviewsCursor) {
        nodes { author { login } body submittedAt updatedAt state commit { oid } }
        pageInfo { hasNextPage endCursor }
      }
      comments(first: 100, after: $commentsCursor) {
        nodes { author { login } body createdAt updatedAt includesCreatedEdit }
        pageInfo { hasNextPage endCursor }
      }
      reviewThreads(first: 100, after: $threadsCursor) {
        nodes {
          isResolved
          comments(first: 100) {
            nodes { author { login } commit { oid } }
            pageInfo { hasNextPage endCursor }
          }
        }
        pageInfo { hasNextPage endCursor }
      }
      commits(last: 1) @include(if: $includeChecks) {
        nodes {
          commit {
            statusCheckRollup {
              contexts(first: 100, after: $checksCursor) {
                nodes {
                  __typename
                  ... on CheckRun { name status conclusion }
                  ... on StatusContext { context state }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
      }
    }
  }
}`;

function connectionPage(pr, name) {
  if (name === 'checks') {
    return pr.commits?.nodes?.[0]?.commit?.statusCheckRollup?.contexts ?? {
      nodes: [],
      pageInfo: { hasNextPage: false, endCursor: null },
    };
  }
  return pr[name];
}

async function collectPullRequestSnapshot(fetchPage) {
  const cursors = {
    labelsCursor: null,
    reviewsCursor: null,
    commentsCursor: null,
    threadsCursor: null,
    checksCursor: null,
  };
  const collected = {
    labels: [],
    reviews: [],
    comments: [],
    reviewThreads: [],
    checks: [],
  };
  let metadata = null;

  for (let pageNumber = 0; pageNumber < 1_000; pageNumber += 1) {
    const result = await fetchPage({ ...cursors });
    const pr = result?.repository?.pullRequest ?? result?.data?.repository?.pullRequest;
    if (!pr) return null;

    const currentMetadata = {
      number: pr.number,
      state: pr.state,
      isDraft: pr.isDraft,
      headRefOid: pr.headRefOid,
    };
    if (metadata === null) {
      metadata = currentMetadata;
    } else if (Object.keys(currentMetadata).some(
      key => currentMetadata[key] !== metadata[key])) {
      throw new Error('Review Gate pull request changed during pagination.');
    }

    const pages = {
      labels: connectionPage(pr, 'labels'),
      reviews: connectionPage(pr, 'reviews'),
      comments: connectionPage(pr, 'comments'),
      reviewThreads: connectionPage(pr, 'reviewThreads'),
      checks: connectionPage(pr, 'checks'),
    };
    for (const [name, page] of Object.entries(pages)) {
      collected[name].push(...(page?.nodes ?? []));
    }

    const cursorMappings = [
      ['labels', 'labelsCursor'],
      ['reviews', 'reviewsCursor'],
      ['comments', 'commentsCursor'],
      ['reviewThreads', 'threadsCursor'],
      ['checks', 'checksCursor'],
    ];
    let hasNextPage = false;
    for (const [name, cursorName] of cursorMappings) {
      const pageInfo = pages[name]?.pageInfo ?? {};
      if (pageInfo.hasNextPage) {
        if (!pageInfo.endCursor || pageInfo.endCursor === cursors[cursorName]) {
          throw new Error(`Review Gate pagination cursor stalled for ${name}.`);
        }
        cursors[cursorName] = pageInfo.endCursor;
        hasNextPage = true;
      } else if (pageInfo.endCursor) {
        // Keeping the terminal cursor prevents a completed connection from replaying its
        // first page while another connection continues paginating.
        cursors[cursorName] = pageInfo.endCursor;
      }
    }

    if (!hasNextPage) {
      const pageInfo = { hasNextPage: false, endCursor: null };
      return {
        ...metadata,
        labels: { nodes: collected.labels, pageInfo },
        reviews: { nodes: collected.reviews, pageInfo },
        comments: { nodes: collected.comments, pageInfo },
        reviewThreads: { nodes: collected.reviewThreads, pageInfo },
        commits: {
          nodes: [{
            commit: {
              statusCheckRollup: {
                contexts: { nodes: collected.checks, pageInfo },
              },
            },
          }],
        },
      };
    }
  }

  throw new Error('Review Gate pagination exceeded 1,000 pages.');
}

async function fetchPullRequestSnapshot(github, owner, repo, number) {
  return collectPullRequestSnapshot(cursors => github.graphql(
    QUERY,
    { owner, repo, number, includeChecks: false, ...cursors },
  ));
}

function trainSnapshot(pr) {
  const contexts = pr.commits?.nodes?.[0]?.commit?.statusCheckRollup?.contexts;
  return {
    number: pr.number,
    state: pr.state,
    isDraft: pr.isDraft,
    headRefOid: pr.headRefOid,
    labels: pr.labels.nodes,
    labelsTruncated: pr.labels.pageInfo.hasNextPage,
    reviews: pr.reviews.nodes,
    reviewsTruncated: pr.reviews.pageInfo.hasNextPage,
    cleanComments: pr.comments.nodes,
    commentsTruncated: pr.comments.pageInfo.hasNextPage,
    reviewThreads: pr.reviewThreads.nodes,
    reviewThreadsTruncated: pr.reviewThreads.pageInfo.hasNextPage ||
      pr.reviewThreads.nodes.some(thread => thread.comments.pageInfo.hasNextPage),
    statusCheckRollup: contexts?.nodes ?? [],
    checksTruncated: contexts?.pageInfo?.hasNextPage ?? false,
  };
}

function parseCliArguments(argv) {
  const values = {};
  for (let index = 0; index < argv.length; index += 2) {
    values[argv[index]] = argv[index + 1];
  }
  const repository = values['--repo'] ?? process.env.GITHUB_REPOSITORY;
  const number = Number(values['--pr']);
  if (!repository?.includes('/') || !Number.isInteger(number) || number <= 0) {
    throw new Error('Usage: review-gate-snapshot.js --repo owner/name --pr number');
  }
  const [owner, repo] = repository.split('/', 2);
  return { owner, repo, number };
}

function ghGraphqlPage(owner, repo, number, cursors) {
  const args = [
    'api',
    'graphql',
    '-f',
    `query=${QUERY}`,
    '-F',
    `owner=${owner}`,
    '-F',
    `repo=${repo}`,
    '-F',
    `number=${number}`,
    '-F',
    'includeChecks=true',
  ];
  for (const [name, value] of Object.entries(cursors)) {
    if (value) args.push('-f', `${name}=${value}`);
  }
  return JSON.parse(execFileSync('gh', args, {
    encoding: 'utf8',
    maxBuffer: 50 * 1024 * 1024,
  }));
}

async function main() {
  const { owner, repo, number } = parseCliArguments(process.argv.slice(2));
  const pr = await collectPullRequestSnapshot(cursors =>
    ghGraphqlPage(owner, repo, number, cursors));
  if (!pr) throw new Error(`PR #${number} not found.`);
  process.stdout.write(`${JSON.stringify(trainSnapshot(pr))}\n`);
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`${error.stack ?? error.message}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  QUERY,
  collectPullRequestSnapshot,
  fetchPullRequestSnapshot,
  trainSnapshot,
};
