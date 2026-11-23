const test = require('node:test');
const assert = require('node:assert/strict');
const { sweep, keyOf } = require('./scheduled-failure-issues.js');
const realConfig = require('./scheduled-failure-owners.json');

const NOW = new Date('2026-11-23T08:00:00Z');
const config = {
  lookbackHours: 72, maxIssuesPerRun: 2, downstreamJobPattern: '^(CI Gate|.*Summary)$',
  defaultOwner: { lane: 's-ci-doctors', area: 'area/ci' },
  ignore: { app: ['watchdog.yml'] },
  dispatchWorkflows: { app: { 'ci.yml': { ref: 'default', title: '^CI full' }, 'build.yml': { ref: '^candidate-' } } },
  successJobs: { app: { 'build.yml': '^Publish' } },
  repos: { app: { default: { lane: 's-app' }, workflows: { 'nightly.yml': { lane: 's-night', area: 'area/x' } } } },
};

function run(id, file, conclusion) {
  return {
    id, workflow_id: file, name: `${file} run ${id}`, path: `.github/workflows/${file}`, status: 'completed', conclusion,
    event: 'schedule', head_branch: 'trunk', head_sha: `${id}`.padEnd(40, 'a'),
    created_at: new Date(Date.parse('2026-11-23T00:00:00Z') + id * 60000).toISOString(),
    html_url: `https://github.com/honua-io/app/actions/runs/${id}`,
  };
}
const withRun = (base, extra) => ({ ...base, ...extra });
const job = (name, conclusion, minute) => ({
  name, conclusion, started_at: `2026-11-23T01:${String(minute).padStart(2, '0')}:00Z`, html_url: `https://job/${name}`,
});

const BOT = { login: 'honua-io-bot[bot]', type: 'Bot' };
const REPOS = [{ name: 'app', owner: { login: 'honua-io' }, default_branch: 'trunk' },
  { name: 'old', owner: { login: 'honua-io' }, archived: true, default_branch: 'main' }];

function fakeReads(state) {
  return async (route, params) => {
    state.calls.push(['paginate', route, params]);
    if (route === 'GET /installation/repositories') return state.repos;
    if (route === 'GET /repos/{owner}/{repo}/actions/runs') return params.event === 'schedule' ? [...state.runs] : [];
    if (route.endsWith('/workflows/{workflow_id}/runs')) return [...state.dispatchRuns];
    if (route.endsWith('/jobs')) return state.jobs[params.run_id] || [];
    if (route === 'GET /repos/{owner}/{repo}/issues') {
      const fresh = issue => !params.since || Date.parse(issue.updated_at || NOW) >= Date.parse(params.since);
      return state.issues.filter(issue => issue.state === params.state && fresh(issue))
        .map(issue => ({ user: BOT, ...issue, comments: (state.comments[issue.number] || []).length }));
    }
    if (route.endsWith('/comments')) return (state.comments[params.issue_number] || []).map(item => ({ user: BOT, ...item }));
    throw new Error(`unexpected ${route}`);
  };
}

function fakeLookups(state, route, params) {
  if (route === 'GET /repos/{owner}/{repo}/actions/workflows/{workflow_id}') {
    return { data: { name: params.workflow_id.replace('.yml', '') } };
  }
  return null;
}

function fakeWrites(state) {
  return async (route, params) => {
    state.calls.push(['request', route, params]);
    const lookup = fakeLookups(state, route, params);
    if (lookup) return lookup;
    if (route === 'POST /repos/{owner}/{repo}/labels') {
      if (state.labels.includes(params.name)) throw Object.assign(new Error('already_exists'), { status: 422 });
      state.labels.push(params.name);
      return { data: {} };
    }
    if (route === 'POST /repos/{owner}/{repo}/issues') {
      const issue = { number: state.nextIssue++, state: 'open', user: BOT, title: params.title, body: params.body, labels: params.labels };
      state.issues.push(issue);
      return { data: issue };
    }
    if (route.endsWith('/comments')) {
      (state.comments[params.issue_number] ||= []).push({ body: params.body });
      return { data: {} };
    }
    if (route === 'PATCH /repos/{owner}/{repo}/issues/{issue_number}') {
      Object.assign(state.issues.find(issue => issue.number === params.issue_number), { state: params.state });
      return { data: {} };
    }
    throw new Error(`unexpected ${route}`);
  };
}

function fakeGithub({ runs = [], dispatchRuns = [], jobs = {}, issues = [], comments = {}, labels = ['bug'], repos = REPOS }) {
  const state = { calls: [], nextIssue: 100, runs, dispatchRuns, jobs, issues, comments, labels, repos };
  return { calls: state.calls, issues, comments, paginate: fakeReads(state), request: fakeWrites(state) };
}
const core = { info() {}, warning() {} };
const go = github => sweep({ github, core, config, owner: 'honua-io', now: NOW });

test('a failed scheduled run opens one issue per failing job with owner, labels, due time and claim', async () => {
  const github = fakeGithub({
    runs: [run(1, 'nightly.yml', 'failure')],
    jobs: { 1: [job('Gate', 'failure', 9), job('Build', 'failure', 1), job('Lint', 'success', 0), job('Test', 'timed_out', 5),
      job('CI Gate', 'failure', 20), job('Test Suite Summary', 'failure', 19)] },
  });
  const report = await go(github);
  assert.deepEqual(report.opened, ['app#100', 'app#101']);
  const [first, second] = github.issues;
  assert.equal(first.title, 'Scheduled run failing: nightly / Build');
  assert.equal(second.title, 'Scheduled run failing: nightly / Test');
  assert.deepEqual(first.labels, ['bug', 'area/x', 'scheduled-failure']);
  assert.equal(keyOf(first.body), '.github/workflows/nightly.yml::Build');
  assert.match(first.body, /Owner lane: `s-night`/);
  assert.match(first.body, /Due: 2026-11-24T08:00:00Z/);
  assert.match(first.body, /Other failed jobs in this run: `Test`, `Gate`/);
  assert.match(github.comments[100][0].body, /^claimed: s-night 2026-11-23T08:00:00Z - auto-assigned owner of nightly.yml/);
  const posted = github.calls.filter(([, route]) => route === 'POST /repos/{owner}/{repo}/labels').map(([, , p]) => p.name);
  assert.deepEqual(posted, ['bug', 'area/x', 'scheduled-failure']);
  assert.ok(!github.calls.some(([, route]) => route.startsWith('GET /repos/{owner}/{repo}/labels')));
});

test('a repeat failure comments once per run on the existing issue instead of opening another', async () => {
  const key = '.github/workflows/nightly.yml::Build';
  const issues = [{ number: 7, state: 'open', body: `<!-- scheduled-failure key=${key} -->\n<!-- scheduled-failure run=1 -->` }];
  const github = fakeGithub({
    runs: [run(1, 'nightly.yml', 'failure'), run(2, 'nightly.yml', 'failure')],
    jobs: { 1: [job('Build', 'failure', 1)], 2: [job('Build', 'failure', 1)] }, issues,
  });
  assert.deepEqual((await go(github)).updated, ['app#7']);
  assert.match(github.comments[7][0].body, /^claimed: s-night /);
  assert.match(github.comments[7][1].body, /Failed again: .*runs\/2/);
  const again = await go(github);
  assert.deepEqual([again.opened, again.updated], [[], []]);
  assert.equal(github.issues.length, 1);
});

test('the latest scheduled success closes every open issue of that workflow only', async () => {
  const issues = [
    { number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::Build -->' },
    { number: 8, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::Test -->' },
    { number: 9, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/other.yml::Build -->' },
  ];
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure'), run(3, 'nightly.yml', 'success'), run(2, 'nightly.yml', 'cancelled')], issues });
  const report = await go(github);
  assert.deepEqual(report.closed, ['app#7', 'app#8']);
  assert.deepEqual(issues.map(issue => issue.state), ['closed', 'closed', 'open']);
  assert.match(github.comments[7][0].body, /runs\/3 succeeded/);
});

test('cancelled, ignored, push-dispatched and human-dispatched runs are not swept; bot dispatches of listed workflows are', async () => {
  const bot = { event: 'workflow_dispatch', actor: { login: 'github-actions[bot]' }, display_title: 'CI full ' };
  const github = fakeGithub({
    runs: [run(1, 'later.yml', 'cancelled'), run(2, 'watchdog.yml', 'failure')],
    dispatchRuns: [withRun(run(3, 'ci.yml', 'failure'), bot),
      withRun(run(4, 'ci.yml', 'failure'), { ...bot, actor: { login: 'founder' }, triggering_actor: { login: 'github-actions[bot]' } }),
      withRun(run(5, 'ci.yml', 'failure'), { ...bot, head_branch: 'feature' })],
    jobs: { 3: [] },
  });
  const report = await go(github);
  assert.deepEqual(report.opened, ['app#100']);
  assert.equal(github.issues[0].title, 'Scheduled run failing: ci / (no job ran)');
  assert.match(github.issues[0].body, /Owner lane: `s-app`/);
  assert.deepEqual(github.issues[0].labels, ['bug', 'area/ci', 'scheduled-failure']);
  const repos = github.calls.filter(([, route]) => route === 'GET /repos/{owner}/{repo}/actions/runs').map(([, , p]) => p.repo);
  assert.deepEqual(repos, ['app']);
});

test('a later sweep reads the comment list of an issue it opened earlier and records each run once', async () => {
  const jobs = { 1: [job('Build', 'failure', 1)], 12: [job('Build', 'failure', 1)] };
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')], jobs });
  await go(github);
  const later = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure'), run(12, 'nightly.yml', 'failure')],
    jobs, issues: github.issues, comments: github.comments });
  const report = await go(later);
  assert.deepEqual([report.opened, report.updated, report.errors], [[], ['app#100'], []]);
  assert.equal(github.comments[100].filter(item => item.body.startsWith('claimed: ')).length, 1);
  assert.deepEqual((await go(later)).updated, []);
});

test('a successful partial dispatch neither closes issues nor counts as a run', async () => {
  const issues = [{ number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/ci.yml::Build -->' }];
  const github = fakeGithub({
    runs: [run(1, 'ci.yml', 'failure')], jobs: { 1: [job('Build', 'failure', 1)] }, issues,
    dispatchRuns: [withRun(run(2, 'ci.yml', 'success'), {
      event: 'workflow_dispatch', actor: { login: 'github-actions[bot]' }, display_title: 'CI affected a..b' })],
  });
  const report = await go(github);
  assert.deepEqual([report.closed, report.updated], [[], ['app#7']]);
  assert.equal(issues[0].state, 'open');
});

test('every failed run since the last success is filed, oldest first', async () => {
  const github = fakeGithub({
    runs: [run(3, 'nightly.yml', 'failure'), run(1, 'nightly.yml', 'success'), run(2, 'nightly.yml', 'failure')],
    jobs: { 2: [job('Build', 'failure', 1)], 3: [job('Test', 'failure', 2), job('Build', 'failure', 1)] },
  });
  const report = await go(github);
  assert.deepEqual(github.issues.map(issue => issue.title),
    ['Scheduled run failing: nightly / Build', 'Scheduled run failing: nightly / Test']);
  assert.deepEqual(report.updated, ['app#100']);
  assert.match(github.comments[100][1].body, /Failed again: .*runs\/3/);
});

test('the per-run cap counts only new issues, so later failing jobs are filed on the next sweep', async () => {
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')], jobs: {
    1: [job('Build', 'failure', 1), job('Test', 'failure', 2), job('Lint', 'failure', 3)] } });
  await go(github);
  assert.equal(github.issues.length, 2);
  await go(github);
  assert.deepEqual(github.issues.map(issue => issue.title.split(' / ')[1]), ['Build', 'Test', 'Lint']);
});

test('a run already recorded on a closed issue is not filed again; a new failing run is', async () => {
  const key = '.github/workflows/nightly.yml::Build';
  const issues = [{ number: 7, state: 'closed', body: `${'<'}!-- scheduled-failure key=${key} -->\n<!-- scheduled-failure run=1 -->` }];
  const jobs = { 1: [job('Build', 'failure', 1)], 2: [job('Build', 'failure', 1)] };
  const quiet = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')], jobs, issues });
  assert.deepEqual((await go(quiet)).opened, []);
  const again = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure'), run(2, 'nightly.yml', 'failure')], jobs, issues });
  assert.deepEqual((await go(again)).opened, ['app#100']);
  assert.match(issues[1].body, /scheduled-failure run=2 -->/);
});

test('a hand-off-only success is ignored; candidate-ref bot builds count and their publish success closes', async () => {
  const bot = { event: 'workflow_dispatch', actor: { login: 'honua-io-bot[bot]' }, triggering_actor: { login: 'founder' }, head_branch: 'candidate-abc' };
  const issues = [{ number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/build.yml::Build_JIT -->' }];
  const failing = fakeGithub({
    runs: [run(1, 'build.yml', 'failure'), run(3, 'build.yml', 'success')],
    dispatchRuns: [withRun(run(2, 'build.yml', 'failure'), bot)],
    jobs: { 1: [job('Build JIT', 'failure', 1)], 2: [job('Build JIT', 'failure', 1)], 3: [job('Select', 'success', 1)] }, issues,
  });
  const first = await go(failing);
  assert.deepEqual([first.closed, first.updated], [[], ['app#7', 'app#7']]);
  const green = fakeGithub({
    runs: [run(1, 'build.yml', 'failure')], dispatchRuns: [withRun(run(4, 'build.yml', 'success'), bot)],
    jobs: { 4: [job('Publish JIT Manifests', 'success', 9)] }, issues,
  });
  assert.deepEqual((await go(green)).closed, ['app#7']);
});

test('logs and the report never name a private repository or its issue numbers', async () => {
  const warnings = [];
  const github = fakeGithub({
    repos: [{ name: 'secret-repo', private: true, owner: { login: 'honua-io' }, default_branch: 'trunk' }],
    runs: [run(1, 'nightly.yml', 'failure')], jobs: { 1: [job('Build', 'failure', 1)] },
  });
  github.log = { error: text => warnings.push(text) };
  const report = await sweep({ github, core: { info: text => warnings.push(text), warning: text => warnings.push(text) },
    config, owner: 'honua-io', now: NOW });
  github.log.error('GET /repos/honua-io/secret-repo/labels/bug - 404');
  assert.match(warnings.at(-1), /honua-io\/a private repository\/labels/);
  assert.deepEqual(report.opened, ['a private repository#']);
  assert.doesNotMatch(JSON.stringify([report, warnings]), /secret-repo|#100/);
});

test('an open issue untouched for longer than the run window is still updated and closed', async () => {
  const old = () => [{ number: 7, state: 'open', updated_at: '2026-11-10T00:00:00Z',
    body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::Build -->' }];
  const failing = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')], jobs: { 1: [job('Build', 'failure', 1)] }, issues: old() });
  const report = await go(failing);
  assert.deepEqual([report.opened, report.updated], [[], ['app#7']]);
  const green = fakeGithub({ runs: [run(2, 'nightly.yml', 'success')], issues: old() });
  assert.deepEqual((await go(green)).closed, ['app#7']);
});

test('a success between failures closes the issues it fixed and keeps the still-failing ones', async () => {
  const issues = [
    { number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::A -->\n<!-- scheduled-failure run=1 -->' },
    { number: 8, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::B -->\n<!-- scheduled-failure run=1 -->' },
  ];
  const github = fakeGithub({
    runs: [run(1, 'nightly.yml', 'failure'), run(2, 'nightly.yml', 'success'), run(3, 'nightly.yml', 'failure')],
    jobs: { 3: [job('B', 'failure', 1)] }, issues,
  });
  const report = await go(github);
  assert.deepEqual([report.closed, report.updated], [['app#7'], ['app#8']]);
});

test('runs recorded on a closed issue are not replayed onto its open successor', async () => {
  const key = '.github/workflows/nightly.yml::Build';
  const issues = [
    { number: 7, state: 'closed', body: `<!-- scheduled-failure key=${key} -->\n<!-- scheduled-failure run=1 -->` },
    { number: 20, state: 'open', body: `<!-- scheduled-failure key=${key} -->\n<!-- scheduled-failure run=2 -->` },
  ];
  const jobs = { 1: [job('Build', 'failure', 1)], 2: [job('Build', 'failure', 1)] };
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure'), run(2, 'nightly.yml', 'failure')], jobs, issues,
    comments: { 20: [{ body: 'claimed: s-night x' }] } });
  assert.deepEqual((await go(github)).updated, []);
});

test('a job cancelled by its own timeout names the issue when no job concluded failed', async () => {
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')],
    jobs: { 1: [job('Soak', 'cancelled', 1), job('Prep', 'success', 0)] } });
  await go(github);
  assert.deepEqual(github.issues.map(issue => issue.title), ['Scheduled run failing: nightly / Soak']);
});

test('an older success never closes an issue for a newer run that is being re-run or was cancelled', async () => {
  const issues = [{ number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::Build -->\n<!-- scheduled-failure run=2 -->' }];
  const rerunning = { ...run(2, 'nightly.yml', null), status: 'in_progress' };
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'success'), rerunning], issues });
  assert.deepEqual((await go(github)).closed, []);
  const green = fakeGithub({ runs: [run(1, 'nightly.yml', 'success'), run(3, 'nightly.yml', 'success')], issues });
  assert.deepEqual((await go(green)).closed, ['app#7']);
});

test('run markers from other users and issues not opened by a bot are ignored', async () => {
  const key = '.github/workflows/nightly.yml::Build';
  const issues = [
    { number: 7, state: 'open', body: `<!-- scheduled-failure key=${key} -->\n<!-- scheduled-failure run=1 -->` },
    { number: 8, state: 'open', user: { login: 'someone', type: 'User' }, body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::Test -->' },
  ];
  const comments = { 7: [{ user: { login: 'someone', type: 'User' }, body: '<!-- scheduled-failure run=99999 -->' }] };
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure'), run(2, 'nightly.yml', 'success')], issues, comments });
  assert.deepEqual((await go(github)).closed, ['app#7']);
});

test('issues are not closed as fixed when failures after the success were beyond the replay cap', async () => {
  const issues = [{ number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::A -->' }];
  const failures = [3, 4, 5, 6, 7, 8, 9].map(id => run(id, 'nightly.yml', 'failure'));
  const jobs = Object.fromEntries(failures.map(item => [item.id, [job('B', 'failure', 1)]]));
  const github = fakeGithub({ runs: [run(2, 'nightly.yml', 'success'), ...failures], jobs, issues });
  assert.deepEqual((await go(github)).closed, []);
});

test('an issue closes when its job succeeds in a newer run while another job keeps the workflow red', async () => {
  const key = name => `<!-- scheduled-failure key=.github/workflows/nightly.yml::${name} -->\n<!-- scheduled-failure run=1 -->`;
  const issues = [{ number: 7, state: 'open', body: key('Postgres_16') }, { number: 8, state: 'open', body: key('Build') }];
  const jobs = { 1: [job('Postgres 16', 'failure', 1), job('Build', 'failure', 2)],
    2: [job('Postgres 16', 'success', 1), job('Build', 'failure', 2)], 3: [job('Postgres 16', 'skipped', 1), job('Build', 'failure', 2)] };
  const runs = [1, 2, 3].map(id => run(id, 'nightly.yml', 'failure'));
  const github = fakeGithub({ runs, jobs, issues, comments: { 7: [{ body: 'claimed: s-night x' }], 8: [{ body: 'claimed: s-night x' }] } });
  const report = await go(github);
  assert.deepEqual([report.closed, report.updated], [['app#7'], ['app#8', 'app#8']]);
  assert.match(github.comments[7][1].body, /^Job `Postgres 16` in scheduled run .*runs\/2 succeeded/);
  assert.deepEqual(issues.map(issue => issue.state), ['closed', 'open']);
});

test('a job success older than its newest recorded failure, or followed by a failure, never closes', async () => {
  const issues = [{ number: 7, state: 'open', body: '<!-- scheduled-failure key=.github/workflows/nightly.yml::A -->\n<!-- scheduled-failure run=3 -->' }];
  const runs = [1, 2, 3, 4, 5].map(id => run(id, 'nightly.yml', 'failure'));
  const jobs = { 2: [job('A', 'success', 1)], 3: [job('A', 'failure', 1)], 4: [job('A', 'success', 1)], 5: [job('A', 'timed_out', 1)] };
  const github = fakeGithub({ runs, jobs, issues, comments: { 7: [{ body: 'claimed: s-night x' }] } });
  assert.deepEqual((await go(github)).closed, []);
  assert.equal(issues[0].state, 'open');
});

test('a failing repository sweep is reported without stopping the others; all failing throws', async () => {
  const github = fakeGithub({});
  github.paginate = async route => {
    if (route === 'GET /installation/repositories') return [{ name: 'app', owner: { login: 'honua-io' } }];
    throw Object.assign(new Error('Resource not accessible'), { status: 403 });
  };
  await assert.rejects(go(github), /every repository sweep failed/);
});

test('summary and gate jobs get an issue only when they are the only failure', async () => {
  const github = fakeGithub({ runs: [run(1, 'nightly.yml', 'failure')], jobs: { 1: [job('CI Gate', 'failure', 9)] } });
  await go(github);
  assert.deepEqual(github.issues.map(issue => issue.title), ['Scheduled run failing: nightly / CI Gate']);
});

test('the owner map names real workflow files for honua-server and lanes for every entry', () => {
  const fs = require('node:fs');
  for (const file of Object.keys(realConfig.repos['honua-server'].workflows)) {
    assert.ok(fs.existsSync(`.github/workflows/${file}`), `${file} is not a honua-server workflow`);
  }
  for (const [repo, entry] of Object.entries(realConfig.repos)) {
    for (const owner of [entry.default, ...Object.values(entry.workflows || {})]) {
      assert.match(owner.lane, /^s-[a-z0-9-]+$/, `${repo} owner lane`);
    }
  }
});
