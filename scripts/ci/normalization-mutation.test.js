'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  NORMALIZATION_COMMIT_TRAILER,
  applyNormalizationMutation,
  buildNormalizationCommitMessage,
  isNormalizationReplay,
  parseNormalizationTrailer,
  planNormalizationMutation,
  probeNormalizationCredential,
} = require('./normalization-mutation');

const head = 'a'.repeat(40);
const parent = 'b'.repeat(40);
const tree = 'c'.repeat(40);

const ALLOWED = [
  'docs/gis/data/feature-catalog.json',
  'docs/gis/data/geoservices-rest-parity.json',
  'docs/gis/data/capability-matrix.v1.json',
];

function change(path = ALLOWED[0]) {
  return { path, sha256: 'e'.repeat(64) };
}

function input(overrides = {}) {
  return {
    mode: 'enforce',
    credentialPresent: true,
    sameRepository: true,
    pullRequest: { state: 'open', draft: false, headSha: head, headRef: 'feat/example' },
    envelopeSourceSha: head,
    planTreeSha: tree,
    headCommit: { sha: head, treeSha: tree, message: 'feat: example', parents: [parent] },
    changes: [change()],
    allowedPaths: ALLOWED,
    ...overrides,
  };
}

test('observe mode never mutates', () => {
  assert.equal(planNormalizationMutation(input({ mode: 'observe' })).action, 'observe');
});

test('an unknown mode never mutates', () => {
  assert.equal(planNormalizationMutation(input({ mode: 'enforce-later' })).action, 'observe');
});

test('a three-file update commits', () => {
  const result = planNormalizationMutation(input({ changes: ALLOWED.map((path) => change(path)) }));
  assert.equal(result.action, 'commit');
  assert.equal(result.changes.length, 3);
});

test('a one-file update commits', () => {
  assert.equal(planNormalizationMutation(input()).action, 'commit');
});

test('an empty delta emits no commit', () => {
  const result = planNormalizationMutation(input({ changes: [] }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'no-delta');
});

test('a fork is read-only even with a delta', () => {
  const result = planNormalizationMutation(input({ sameRepository: false }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'fork-read-only');
});

test('a moved head fails closed', () => {
  const result = planNormalizationMutation(input({
    pullRequest: { state: 'open', draft: false, headSha: 'f'.repeat(40), headRef: 'feat/example' },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'head-moved');
});

test('a head commit that does not match the envelope fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: { sha: 'f'.repeat(40), treeSha: tree, message: 'x', parents: [parent] },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'head-commit-mismatch');
});

test('a tree that does not match the envelope fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: { sha: head, treeSha: 'f'.repeat(40), message: 'x', parents: [parent] },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'tree-mismatch');
});

test('a closed or draft pull request fails closed', () => {
  for (const pullRequest of [
    { state: 'closed', draft: false, headSha: head, headRef: 'feat/example' },
    { state: 'open', draft: true, headSha: head, headRef: 'feat/example' },
  ]) {
    const result = planNormalizationMutation(input({ pullRequest }));
    assert.equal(result.action, 'fail');
    assert.equal(result.reason, 'pull-request-not-open');
  }
});

test('an unsafe head ref fails closed', () => {
  for (const headRef of ['', '-x', 'feat/..hidden', 'feat/a b', 'feat/a^', 'refs/heads/a:b']) {
    const result = planNormalizationMutation(input({
      pullRequest: { state: 'open', draft: false, headSha: head, headRef },
    }));
    assert.equal(result.action, 'fail', headRef);
    assert.equal(result.reason, 'unsafe-head-ref', headRef);
  }
});

test('a path outside the validated plan allowlist fails closed', () => {
  const result = planNormalizationMutation(input({ changes: [change('.github/workflows/ci.yml')] }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'path-outside-allowlist');
});

test('an allowlist that leaves the derived-data tree fails closed', () => {
  for (const allowedPaths of [
    [],
    ['scripts/ci/normalization-envelope.py'],
    ['docs/gis/data/../../.github/workflows/ci.yml'],
    ['docs/gis/data/feature-catalog.json', '.github/workflows/ci.yml'],
  ]) {
    const result = planNormalizationMutation(input({ allowedPaths, changes: [] }));
    assert.equal(result.action, 'fail', JSON.stringify(allowedPaths));
    assert.equal(result.reason, 'invalid-allowlist');
  }
});

test('a duplicate change path fails closed', () => {
  const result = planNormalizationMutation(input({ changes: [change(), change()] }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'duplicate-change-path');
});

test('a change without a validated digest fails closed', () => {
  const result = planNormalizationMutation(input({
    changes: [{ ...change(), sha256: 'not-a-digest' }],
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'change-missing-digest');
});

test('non-array changes fail closed', () => {
  assert.equal(planNormalizationMutation(input({ changes: null })).action, 'fail');
  assert.equal(planNormalizationMutation(undefined).action, 'fail');
});

test('enforce without the scoped credential fails closed', () => {
  const result = planNormalizationMutation(input({ credentialPresent: false }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'missing-normalization-credential');
});

// --- replay detection (#3219 review item 6) -------------------------------

test('a replay of this exact parent fails closed', () => {
  const result = planNormalizationMutation(input({
    headCommit: {
      sha: head,
      treeSha: tree,
      message: `chore(ci): normalize governed derived artifacts\n\n${NORMALIZATION_COMMIT_TRAILER} ${parent}`,
      parents: [parent],
    },
  }));
  assert.equal(result.action, 'fail');
  assert.equal(result.reason, 'non-converging-normalization');
});

test('an inherited trailer from a squash or amend is not a replay', () => {
  const inherited = {
    sha: head,
    treeSha: tree,
    // Squashed history: the trailer names an older SHA, not this commit's parent.
    message: `feat: squashed work\n\n${NORMALIZATION_COMMIT_TRAILER} ${'9'.repeat(40)}`,
    parents: [parent],
  };
  assert.equal(isNormalizationReplay(inherited), false);
  assert.equal(planNormalizationMutation(input({ headCommit: inherited })).action, 'commit');
});

test('a merge commit carrying the trailer is not a replay', () => {
  const merged = {
    sha: head,
    treeSha: tree,
    message: `Merge branch 'trunk'\n\n${NORMALIZATION_COMMIT_TRAILER} ${parent}`,
    parents: [parent, '8'.repeat(40)],
  };
  assert.equal(isNormalizationReplay(merged), false);
  assert.equal(planNormalizationMutation(input({ headCommit: merged })).action, 'commit');
});

test('the trailer parser only accepts a full lowercase SHA on its own line', () => {
  assert.equal(parseNormalizationTrailer(`x\n${NORMALIZATION_COMMIT_TRAILER} ${parent}`), parent);
  assert.equal(parseNormalizationTrailer(`${NORMALIZATION_COMMIT_TRAILER} deadbeef`), null);
  assert.equal(parseNormalizationTrailer('no trailer here'), null);
  assert.equal(parseNormalizationTrailer(undefined), null);
});

test('a converged normalization commit emits no second commit', () => {
  const result = planNormalizationMutation(input({
    changes: [],
    headCommit: {
      sha: head,
      treeSha: tree,
      message: `chore(ci): normalize governed derived artifacts\n\n${NORMALIZATION_COMMIT_TRAILER} ${parent}`,
      parents: [parent],
    },
  }));
  assert.equal(result.action, 'skip');
  assert.equal(result.reason, 'no-delta');
});

test('the commit message binds paths, source SHA, run, and attempt', () => {
  const message = buildNormalizationCommitMessage({
    changes: [change(ALLOWED[2]), change()],
    sourceSha: head,
    runId: 42,
    runAttempt: 2,
  });
  assert.match(message, /^chore\(ci\): normalize governed derived artifacts\n/);
  assert.ok(message.includes(`- ${ALLOWED[2]}`));
  assert.ok(message.includes(`- ${ALLOWED[0]}`));
  assert.ok(message.includes(`${NORMALIZATION_COMMIT_TRAILER} ${head}`));
  assert.ok(message.includes('Normalization-Run-Id: 42'));
  assert.ok(message.includes('Normalization-Run-Attempt: 2'));
});

test('a commit message requires a delta and an exact source SHA', () => {
  assert.throws(() => buildNormalizationCommitMessage({
    changes: [], sourceSha: head, runId: 1, runAttempt: 1,
  }));
  assert.throws(() => buildNormalizationCommitMessage({
    changes: [change()], sourceSha: 'nope', runId: 1, runAttempt: 1,
  }));
});

// --- mutation orchestration (#3219 review items 3 and 8) ------------------

const newCommit = 'd'.repeat(40);
const newTree = '7'.repeat(40);

function fakeOctokit(overrides = {}) {
  const calls = [];
  const graphqlCalls = [];
  const octokit = {
    calls,
    graphqlCalls,
    rest: {
      git: {
        createBlob: async (args) => {
          calls.push(['createBlob', args]);
          return { data: { sha: `blob-${args.content}` } };
        },
        createTree: async (args) => {
          calls.push(['createTree', args]);
          return { data: { sha: newTree } };
        },
        createCommit: async (args) => {
          calls.push(['createCommit', args]);
          return { data: { sha: newCommit } };
        },
        getRef: async (args) => {
          calls.push(['getRef', args]);
          return { data: { object: { sha: overrides.refAfterUpdate || newCommit } } };
        },
      },
      pulls: {
        get: async (args) => {
          calls.push(['pulls.get', args]);
          if (overrides.pullsGetError) throw new Error(overrides.pullsGetError);
          return { data: { number: args.pull_number } };
        },
      },
      issues: {
        createComment: async (args) => {
          calls.push(['createComment', args]);
          if (overrides.commentError) throw new Error(overrides.commentError);
          return { data: { id: 1 } };
        },
      },
    },
    graphql: async (query, variables) => {
      graphqlCalls.push([query, variables]);
      if (query.includes('repository(owner')) {
        return overrides.repositoryId === null ? { repository: null } : { repository: { id: 'R_1' } };
      }
      if (overrides.casError) throw new Error(overrides.casError);
      return { updateRefs: { clientMutationId: null } };
    },
  };
  return octokit;
}

function mutationArgs(octokit, overrides = {}) {
  return {
    octokit,
    repo: { owner: 'honua-io', repo: 'honua-server' },
    decision: { action: 'commit', reason: 'derived-artifact-drift', changes: [change(ALLOWED[1]), change()] },
    contents: new Map([[ALLOWED[0], 'AAA='], [ALLOWED[1], 'BBB=']]),
    headCommit: { sha: head, treeSha: tree, message: 'feat: example', parents: [parent] },
    headRef: 'feat/example',
    sourceSha: head,
    runId: '7',
    runAttempt: '1',
    pullNumber: 3219,
    ...overrides,
  };
}

test('the mutation writes blobs, tree, commit, then a compare-and-swap ref update', async () => {
  const octokit = fakeOctokit();
  const result = await applyNormalizationMutation(mutationArgs(octokit));
  const order = octokit.calls.map(([name]) => name);
  assert.deepEqual(order, ['createBlob', 'createBlob', 'createTree', 'createCommit', 'getRef']);
  assert.equal(result.commitSha, newCommit);
  assert.deepEqual(result.paths, [ALLOWED[0], ALLOWED[1]]);
  const [, treeArgs] = octokit.calls.find(([name]) => name === 'createTree');
  assert.equal(treeArgs.base_tree, tree);
  assert.ok(treeArgs.tree.every((entry) => entry.mode === '100644' && entry.type === 'blob'));
  const [, commitArgs] = octokit.calls.find(([name]) => name === 'createCommit');
  assert.deepEqual(commitArgs.parents, [head]);
  const [, casVariables] = octokit.graphqlCalls[1];
  assert.deepEqual(casVariables.input.refUpdates, [{
    name: 'refs/heads/feat/example', afterOid: newCommit, beforeOid: head, force: false,
  }]);
  assert.equal(result.reviewRequested, false);
});

test('a compare-and-swap rejection surfaces and never retries unforced', async () => {
  const octokit = fakeOctokit({ casError: 'beforeOid does not match' });
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit)),
    /compare-and-swap ref update refused/,
  );
  assert.equal(octokit.calls.filter(([name]) => name === 'getRef').length, 0);
});

test('a ref that does not read back as the new commit fails loudly', async () => {
  const octokit = fakeOctokit({ refAfterUpdate: '5'.repeat(40) });
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit)),
    /normalization ref verification failed/,
  );
});

test('an unresolvable repository id blocks the update', async () => {
  const octokit = fakeOctokit({ repositoryId: null });
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit)),
    /repository node id/,
  );
  assert.equal(octokit.graphqlCalls.length, 1);
});

test('the mutation refuses anything but a commit decision', async () => {
  const octokit = fakeOctokit();
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit, { decision: { action: 'skip', reason: 'no-delta' } })),
    /requires a commit decision/,
  );
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit, { sourceSha: '4'.repeat(40) })),
    /exact envelope source head/,
  );
  assert.equal(octokit.calls.length, 0);
});

test('a change without an envelope payload never reaches the tree', async () => {
  const octokit = fakeOctokit();
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit, { contents: new Map() })),
    /no validated envelope payload/,
  );
  assert.deepEqual(octokit.calls.map(([name]) => name), []);
});

test('a successful mutation re-requests exact-head review', async () => {
  const octokit = fakeOctokit();
  const result = await applyNormalizationMutation(
    mutationArgs(octokit, { reviewRequestBody: '@codex review' }));
  const [, commentArgs] = octokit.calls.find(([name]) => name === 'createComment');
  assert.equal(commentArgs.issue_number, 3219);
  assert.equal(commentArgs.body, '@codex review');
  assert.equal(result.reviewRequested, true);
});

test('a failed review re-request surfaces after the commit', async () => {
  const octokit = fakeOctokit({ commentError: 'Resource not accessible by integration' });
  await assert.rejects(
    applyNormalizationMutation(mutationArgs(octokit, { reviewRequestBody: '@codex review' })),
    /Resource not accessible by integration/,
  );
});

test('the credential probe names the missing App permission', async () => {
  const octokit = fakeOctokit({ pullsGetError: 'Resource not accessible by integration' });
  await assert.rejects(
    probeNormalizationCredential({
      octokit, repo: { owner: 'honua-io', repo: 'honua-server' }, pullNumber: 1, headRef: 'x',
    }),
    /needs Pull requests: read/,
  );
  const ok = fakeOctokit();
  await probeNormalizationCredential({
    octokit: ok, repo: { owner: 'honua-io', repo: 'honua-server' }, pullNumber: 1, headRef: 'x',
  });
  assert.deepEqual(ok.calls.map(([name]) => name), ['pulls.get', 'getRef']);
});
