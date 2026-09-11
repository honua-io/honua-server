#!/usr/bin/env python3
"""Exercise the collector against a seven-day, 1,400-run API replay."""
import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location('collector', Path(__file__).with_name('collect-impact-routing-runs.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
start = module.timestamp('2026-09-01T00:00:00Z')
end = start + 7 * 86400 - 1
rows = [{'id': i + 1, 'created_at': module.iso(start + i * 400)} for i in range(1400)]
calls = []


def fetch(endpoint, parameters):
    assert 'status' not in parameters
    calls.append(parameters)
    lower, upper = map(module.timestamp, parameters['created'].split('..'))
    selected = list(reversed([row for row in rows if lower <= module.timestamp(row['created_at']) <= upper]))
    offset = (parameters['page'] - 1) * 100
    # Reproduce GitHub's filtered-list ceiling. A naive 14-page reader loses 400.
    return {'total_count': len(selected), 'workflow_runs': selected[:1000][offset:offset + 100]}


queries = module.collect('runs', start, end, 1000, fetch=fetch)
assert len(queries) == 2
assert len(calls) == 16  # root count plus 8 + 7 pages for uneven time slices
assert {row['id'] for query in queries for page in query for row in page['workflow_runs']} == set(range(1, 1401))
assert queries == module.collect('runs', start, end, 1000, fetch=fetch)

# A same-total shift that duplicates a page must be retried too.
raced = False
pauses = []

def race(endpoint, parameters):
    global raced
    page = fetch(endpoint, parameters)
    if parameters['page'] == 2 and not raced:
        raced = True
        return fetch(endpoint, {**parameters, 'page': 1})
    return page

assert module.collect('runs', start, end, 1000, fetch=race, pause=pauses.append) == queries
assert pauses == [5]

# A moved total discards the entire slice, including the first page.
raced = False

def moved(endpoint, parameters):
    global raced
    page = fetch(endpoint, parameters)
    if parameters['page'] == 2 and not raced:
        raced = True
        page['total_count'] += 1
    return page

assert module.collect('runs', start, end, 1000, fetch=moved, pause=lambda _: None) == queries

# No tolerance for persistent missing pages, overlapping IDs, or dense seconds.
for broken in (
    lambda endpoint, p: {**fetch(endpoint, p), 'workflow_runs': []},
    lambda endpoint, p: {**fetch(endpoint, p), 'workflow_runs': [{'id': 1, 'created_at': module.iso(start)}] * 100},
):
    try:
        module.collect('runs', start, end, 1000, fetch=broken, pause=lambda _: None)
    except ValueError:
        pass
    else:
        raise AssertionError('corrupt catalog accepted')
try:
    module.collect('runs', start, start, 1000, fetch=lambda *_: {'total_count': 1001, 'workflow_runs': []})
except ValueError as error:
    assert 'one second' in str(error)
else:
    raise AssertionError('unsplittable query accepted')
print('seven-day-catalog-replay=ok runs=1400 before=800-page-budget-failure after=1400/1400')

# Real command retry behavior: timeouts and 503 retry, 403 sleeps without auth,
# a permanent 404 fails, and exhausted retries never return a partial catalog.
from unittest.mock import patch
import subprocess

success = subprocess.CompletedProcess([], 0, '{"total_count": 0, "workflow_runs": []}', '')
for failure in (
    subprocess.TimeoutExpired(['gh'], 90),
    subprocess.CompletedProcess([], 1, '', 'gh: HTTP 503'),
    subprocess.CompletedProcess([], 1, '', 'gh: HTTP 403'),
):
    with patch.object(module.subprocess, 'run', side_effect=[failure, success]) as request, \
         patch.object(module.time, 'sleep') as sleep:
        assert module.github_page('runs', {})['total_count'] == 0
        sleep.assert_called_once_with(10)
        assert request.call_args_list[0] == request.call_args_list[1] or (
            request.call_args_list[0].args == request.call_args_list[1].args)
for error, expected_calls in (('gh: HTTP 404', 1), ('gh: HTTP 503', 5)):
    with patch.object(module.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, '', error)) as request, \
         patch.object(module.time, 'sleep'):
        try:
            module.github_page('runs', {})
        except RuntimeError:
            pass
        else:
            raise AssertionError('failed request accepted')
        assert request.call_count == expected_calls
print('transport-retries=ok')

# ---------------------------------------------------------------------------
# GITHUB_TOKEN request budget at the measured seven-day volume.
#
# Measured on honua-server for 2026-09-03..09: 1,307 runs per observer, 876 and
# 866 successful, 801 and 775 in-window receipts, 22 serving and 110 worker PR
# image runs. The replay runs hotter: 1,344 runs and 896 successful per
# observer, about 1,600 receipts. It uses the real collector, discovery and
# downloader against a fake GitHub that counts every request.
import hashlib
import io
import itertools
import json
import re
import tempfile
import zipfile
from collections import Counter
from datetime import datetime, timezone

audit_spec = importlib.util.spec_from_file_location(
    'audit', Path(__file__).with_name('audit-impact-routing-evidence.py'))
AUDIT = importlib.util.module_from_spec(audit_spec)
audit_spec.loader.exec_module(AUDIT)
POLICY = json.loads((Path(__file__).parents[2] / '.github/impact-routing-promotion.json').read_text())
REPO = 'honua-io/honua-server'
DAY = 86400
EPOCH = module.timestamp('2026-09-02T00:00:00Z')
WINDOW_A = EPOCH + DAY
WINDOW_B = WINDOW_A + DAY
PR_NAMES = ['pr-gate-impact-docs-only-v3', 'pr-gate-impact-full-v3']
NATIVE_NAMES = ['native-image-impact-observation-v3']
for name in PR_NAMES:
    assert AUDIT.PR_GATE_ARTIFACT.fullmatch(f'{name}-attempt-1')
assert AUDIT.NATIVE_ARTIFACT.fullmatch(f'{NATIVE_NAMES[0]}-attempt-1')
ZIPS = {}
IDS = itertools.count(9_000_000_001)


def artifact_row(run, name, created):
    buffer = io.BytesIO()
    identifier = next(IDS)
    with zipfile.ZipFile(buffer, 'w') as archive:
        archive.writestr('receipt.json', str(identifier))
    ZIPS[identifier] = buffer.getvalue()
    return {'id': identifier, 'name': name, 'expired': False, 'size_in_bytes': len(ZIPS[identifier]),
            'digest': 'sha256:' + hashlib.sha256(ZIPS[identifier]).hexdigest(),
            'created_at': module.iso(created),
            'workflow_run': {'id': run['id'], 'head_sha': run['head_sha']}}


def observer(first_id, workflow, receipt, skip):
    """Ten days of one observer: a third fail, ~9% skip, ~2% lose the receipt."""
    runs, artifacts, successful = [], [], 0
    for i in range(10 * DAY // 450):
        created = EPOCH + i * 450 + first_id % 97
        run = {'id': first_id + i, 'run_attempt': 1, 'event': 'workflow_run',
               'status': 'completed', 'conclusion': 'success' if i % 3 else 'failure',
               'path': workflow, 'head_branch': 'trunk', 'head_sha': f'{first_id + i:040x}',
               'created_at': module.iso(created), 'updated_at': module.iso(created + 120)}
        runs.append(run)
        if run['conclusion'] != 'success':
            continue
        successful += 1
        if successful % 60 == 1:
            continue
        name = skip if successful % 11 == 0 else receipt
        artifacts.append(artifact_row(run, f'{name}-attempt-1', created + 60))
    return runs, artifacts


pr_runs, pr_artifacts = observer(40_000_000_000, AUDIT.PR_GATE_WORKFLOW, PR_NAMES[1],
                                 'pr-gate-impact-skipped-pull-request-moved')
native_runs, native_artifacts = observer(
    50_000_000_000, AUDIT.NATIVE_WORKFLOW, NATIVE_NAMES[0],
    'native-image-impact-skipped-pull-request-identity-moved-during-observation')
# Edges the synthesized catalogs must report exactly as per-run catalogs did: a
# rerun whose attempt-1 receipt is retained, a run with two receipt modes, and
# an expired receipt.
receipted = [run for run in pr_runs if module.timestamp(run['created_at']) > WINDOW_A + DAY
             and any(a['workflow_run']['id'] == run['id'] for a in pr_artifacts)
             and run['conclusion'] == 'success']
receipted[0]['run_attempt'] = 2
pr_artifacts.append(artifact_row(receipted[0], f'{PR_NAMES[1]}-attempt-2',
                                 module.timestamp(receipted[0]['created_at']) + 90))
pr_artifacts.append(artifact_row(receipted[1], f'{PR_NAMES[0]}-attempt-1',
                                 module.timestamp(receipted[1]['created_at']) + 90))
native_artifacts[len(native_artifacts) // 2]['expired'] = True


def image_runs(first_id, count):
    return [{'id': first_id + i, 'event': 'pull_request',
             'created_at': module.iso(EPOCH + i * (10 * DAY // count))} for i in range(count)]


class FakeGitHub:
    def __init__(self):
        self.workflows = {
            'pr-gate-impact-observe.yml': pr_runs,
            'native-image-impact-observe.yml': native_runs,
            'serving-image-boundary.yml': image_runs(60_000_000_000, 28),
            'worker-gdal-image.yml': image_runs(70_000_000_000, 140),
        }
        rows = pr_artifacts + native_artifacts
        self.artifacts = sorted(rows, key=lambda a: (a['created_at'], a['id']), reverse=True)
        self.by_id = {row['id']: row for row in rows}
        self.requests = Counter()

    def page(self, endpoint, parameters):
        route = re.fullmatch(rf'repos/{REPO}/actions/(?:workflows/([^/]+)/runs|artifacts'
                             r'|runs/(\d+)/artifacts|artifacts/(\d+))', endpoint)
        assert route, endpoint
        workflow, run_id, artifact_id = route.groups()
        if workflow:
            self.requests['runs'] += 1
            lower, upper = map(module.timestamp, parameters['created'].split('..'))
            rows = [row for row in reversed(self.workflows[workflow])
                    if lower <= module.timestamp(row['created_at']) <= upper
                    and parameters.get('event', row['event']) == row['event']]
            offset = (parameters['page'] - 1) * 100
            return {'total_count': len(rows), 'workflow_runs': rows[:1000][offset:offset + 100]}
        if run_id:
            self.requests['per-run'] += 1
            return self.run_catalog(int(run_id))
        if artifact_id:
            self.requests['artifact'] += 1
            return {'expired': self.by_id[int(artifact_id)]['expired']}
        self.requests['listing'] += 1
        rows = [row for row in self.artifacts if row['name'] == parameters['name']]
        offset = (parameters['page'] - 1) * parameters['per_page']
        return {'total_count': len(rows), 'artifacts': rows[offset:offset + parameters['per_page']]}

    def run_catalog(self, run_id):
        rows = [row for row in self.artifacts if row['workflow_run']['id'] == run_id]
        return {'total_count': len(rows), 'artifacts': rows}

    def zip(self, endpoint):
        self.requests['download'] += 1
        artifact_id = int(re.fullmatch(rf'repos/{REPO}/actions/artifacts/(\d+)/zip', endpoint).group(1))
        if self.by_id[artifact_id]['expired']:
            raise RuntimeError('gh: HTTP 410')
        return ZIPS[artifact_id]


def write_json_files(root, files):
    root.mkdir(parents=True, exist_ok=True)
    for name, value in files.items():
        (root / name).write_text(json.dumps(value))


def at(value):
    return datetime.fromtimestamp(value, timezone.utc)


def audit_job(fake, window, root, archives):
    """One scheduled audit, every request charged to the real RequestBudget."""
    root.mkdir(parents=True)
    budget = module.RequestBudget.create(
        root / 'request-budget.json', POLICY['github_token_request_limit'],
        POLICY['github_token_request_reserve'], remaining=1000)

    def charged(label, call):
        def request(*args):
            budget.spend(label)
            return call(*args)
        return request

    upper = window + 7 * DAY
    for workflow, stream, event, lower in (
        ('pr-gate-impact-observe.yml', 'pr-gate', None, window - 3600),
        ('native-image-impact-observe.yml', 'native', None, window - 3600),
        ('serving-image-boundary.yml', 'serving', 'pull_request', window - DAY),
        ('worker-gdal-image.yml', 'worker', 'pull_request', window - DAY),
    ):
        queries = module.collect(f'repos/{REPO}/actions/workflows/{workflow}/runs', lower, upper,
                                 POLICY['maximum_runs_per_query'], event=event,
                                 fetch=charged('run-catalogs', fake.page))
        write_json_files(root / 'runs' / stream, {
            f'q{query:03d}-{number:03d}.json': page
            for query, pages in enumerate(queries, 1) for number, page in enumerate(pages, 1)})
    per_run = 0
    for stream, names in (('pr-gate', PR_NAMES), ('native', NATIVE_NAMES)):
        catalogs, stats = module.collect_artifacts(
            REPO, module.read_runs(root / 'runs' / stream), names,
            fetch=charged('artifact-catalogs', fake.page))
        per_run += stats['per_run_catalogs']
        write_json_files(root / 'artifacts' / stream,
                         {f'{run_id}.json': value for run_id, value in catalogs.items()})
    index = AUDIT.discover(root / 'runs/pr-gate', root / 'runs/native',
                           root / 'artifacts/pr-gate', root / 'artifacts/native',
                           POLICY, now=at(upper), cutoff=at(window))
    (root / 'index.json').write_text(json.dumps(index))
    try:
        stats = module.download_receipts(
            REPO, root / 'index.json', archives,
            module.read_digests([root / 'artifacts/pr-gate', root / 'artifacts/native']),
            POLICY['maximum_receipt_downloads'],
            fetch_bytes=charged('downloads', fake.zip), fetch=charged('downloads', fake.page),
            expire=lambda _: None, pause=lambda _: None)
    except module.BudgetExhausted:
        stats = None
    state = budget.state()
    # Every request the job sent was charged first, and none exceeded the allowance.
    assert state['used'] == sum(fake.requests.values()) <= state['allowance'] == 800
    return state, index, stats, per_run


TREND = 1 + 2 * 20
with tempfile.TemporaryDirectory() as temporary:
    temporary = Path(temporary)
    fake = FakeGitHub()
    cache = temporary / 'cache'
    runs = []
    for attempt in range(1, 5):
        fake.requests.clear()
        state, index, stats, per_run = audit_job(fake, WINDOW_A, temporary / f'cold-{attempt}', cache)
        runs.append(state['used'])
        if stats is not None:
            break
    indexed = len(index['artifacts'])
    successful = sum(1 for run in pr_runs + native_runs if run['conclusion'] == 'success'
                     and WINDOW_A - 3600 <= module.timestamp(run['created_at']) <= WINDOW_A + 7 * DAY)
    assert successful >= 876 + 866 and indexed >= 801 + 775, (successful, indexed)
    # A cold cache resumes: each hourly/daily audit keeps its verified archives.
    assert attempt == 3 and stats['downloaded'] + stats['cached'] == indexed, (attempt, stats)
    old_design = successful + indexed + TREND
    assert old_design > 3000, old_design

    # Per-run catalogs (the old reader) and synthesized catalogs index identically.
    for stream, source in (('pr-gate', pr_runs), ('native', native_runs)):
        write_json_files(temporary / 'per-run' / stream, {
            f"{run['id']}.json": fake.run_catalog(run['id'])
            for run in module.read_runs(temporary / 'cold-3' / 'runs' / stream)
            if run['conclusion'] == 'success'})
    root = temporary / 'cold-3'
    assert AUDIT.discover(root / 'runs/pr-gate', root / 'runs/native',
                          temporary / 'per-run/pr-gate', temporary / 'per-run/native',
                          POLICY, now=at(WINDOW_A + 7 * DAY), cutoff=at(WINDOW_A)) == index
    reasons = Counter(item['reason'] for item in index['exclusions'])
    assert reasons['observation-receipt-missing'] > 0 and any(
        reason.startswith('observation-skipped:') for reason in reasons)
    assert [item['reason'] for item in index['integrity_failures']] == [
        'observation-artifact-ambiguous']

    # Steady state: the next day's audit transfers only the new day's receipts.
    fake.requests.clear()
    state, index, stats, per_run = audit_job(fake, WINDOW_B, temporary / 'warm', cache)
    assert stats is not None and stats['downloaded'] < 300 < stats['cached'], stats
    assert state['used'] + TREND <= state['allowance'], state
    assert sorted(path.name for path in cache.iterdir()) == sorted(
        f"{item['artifact_id']}.zip" for item in index['artifacts'])
    spent = state['spent']
    print(f"token-budget-replay=ok successful={successful} receipts={indexed} "
          f"before={old_design}/800 cold_runs={runs} "
          f"warm={state['used'] + TREND}/800 (runs {spent['run-catalogs']} + "
          f"artifact catalogs {spent['artifact-catalogs']} [per-run {per_run}] + "
          f"downloads {spent['downloads']} + trend {TREND})")

# The allowance is the policy limit less reserve, clamped to what is left.
with tempfile.TemporaryDirectory() as temporary:
    path = Path(temporary) / 'budget.json'
    assert module.RequestBudget.create(path, 1000, 200, 5000).state()['allowance'] == 800
    assert module.RequestBudget.create(path, 1000, 200, 300).state()['allowance'] == 100
    try:
        module.RequestBudget.create(path, 1000, 200, 150)
    except module.BudgetExhausted:
        pass
    else:
        raise AssertionError('reserve-breaching budget accepted')
    budget = module.RequestBudget.create(path, 1000, 998, 1000)
    budget.spend('a')
    budget.spend('a')
    for count in (1, 5):
        try:
            budget.spend('b', count)
        except module.BudgetExhausted as error:
            assert '2/2 requests used' in str(error)
        else:
            raise AssertionError('overspend accepted')
    assert budget.state() == {'allowance': 2, 'used': 2, 'spent': {'a': 2}}
    # A retried request is charged per attempt, before it is sent.
    charges = []
    with patch.object(module.subprocess, 'run', side_effect=[
            subprocess.CompletedProcess([], 1, '', 'gh: HTTP 503'), success]), \
            patch.object(module.time, 'sleep'):
        module.github_page('runs', {}, spend=lambda: charges.append(1))
    assert len(charges) == 2
    budget = module.RequestBudget.create(path, 1000, 999, 1000)
    with patch.object(module.subprocess, 'run', return_value=success) as request:
        module.github_page('runs', {}, spend=budget.charge('runs'))
        try:
            module.github_page('runs', {}, spend=budget.charge('runs'))
        except module.BudgetExhausted:
            pass
        else:
            raise AssertionError('request sent past the allowance')
    assert request.call_count == 1

# Downloader edges: corrupt cache entries are replaced, digest mismatches fail
# without an expiry lookup, expired artifacts are reclassified, and running out
# of budget keeps every archive already verified.
with tempfile.TemporaryDirectory() as temporary:
    temporary = Path(temporary)
    good = fake.by_id[pr_artifacts[5]['id']]
    other = fake.by_id[pr_artifacts[6]['id']]
    digests = {row['id']: row['digest'].removeprefix('sha256:') for row in (good, other)}
    index_path = temporary / 'index.json'
    index_path.write_text(json.dumps({'artifacts': [
        {'artifact_id': good['id']}, {'artifact_id': other['id']}]}))
    archives = temporary / 'archives'
    archives.mkdir()
    (archives / f"{good['id']}.zip").write_bytes(b'corrupt')
    (archives / '1.zip').write_bytes(ZIPS[good['id']])
    stats = module.download_receipts(REPO, index_path, archives, digests, 10,
                                     fetch_bytes=fake.zip, fetch=fake.page,
                                     expire=None, pause=lambda _: None)
    assert stats == {'indexed': 2, 'cached': 0, 'downloaded': 2, 'expired': 0}
    assert sorted(path.name for path in archives.iterdir()) == sorted(
        f"{row['id']}.zip" for row in (good, other))
    assert module.download_receipts(REPO, index_path, archives, digests, 10, fetch_bytes=None,
                                    fetch=None, expire=None)['cached'] == 2
    requests = []
    try:
        module.download_receipts(REPO, index_path, temporary / 'mismatch',
                                 {**digests, other['id']: '0' * 64}, 10,
                                 fetch_bytes=fake.zip, fetch=lambda *a: requests.append(a),
                                 expire=None, pause=lambda _: None)
    except RuntimeError as error:
        assert 'unavailable or invalid after 4 attempts' in str(error)
    else:
        raise AssertionError('digest mismatch accepted')
    assert requests == [] and (temporary / 'mismatch' / f"{good['id']}.zip").is_file()
    expired = []
    other['expired'] = True
    stats = module.download_receipts(REPO, index_path, temporary / 'expired', digests, 10,
                                     fetch_bytes=fake.zip, fetch=fake.page,
                                     expire=expired.append, pause=lambda _: None)
    other['expired'] = False
    assert expired == [other['id']] and stats['expired'] == 1
    # A failed expiry lookup proves nothing, so the receipt stays a hard failure.
    def unavailable(endpoint):
        raise RuntimeError('gh: HTTP 500')

    def lookup_fails(endpoint, parameters):
        raise RuntimeError('gh: HTTP 500')
    try:
        module.download_receipts(REPO, index_path, temporary / 'lookup', digests, 10,
                                 fetch_bytes=unavailable, fetch=lookup_fails,
                                 expire=expired.append, pause=lambda _: None)
    except RuntimeError as error:
        assert 'unavailable or invalid after 4 attempts' in str(error)
    else:
        raise AssertionError('unproven expiry accepted')
    assert expired == [other['id']]
    budget = module.RequestBudget.create(temporary / 'budget.json', 1000, 999, 1000)
    try:
        module.download_receipts(REPO, index_path, temporary / 'partial', digests, 10,
                                 fetch_bytes=lambda endpoint: (budget.spend('downloads'),
                                                               fake.zip(endpoint))[1],
                                 fetch=fake.page, expire=None, pause=lambda _: None)
    except module.BudgetExhausted:
        pass
    else:
        raise AssertionError('download past the allowance accepted')
    assert [path.name for path in (temporary / 'partial').iterdir()] == [f"{good['id']}.zip"]
    try:
        module.download_receipts(REPO, index_path, temporary / 'bound', digests, 1,
                                 fetch_bytes=None, fetch=None, expire=None)
    except ValueError as error:
        assert 'maximum_receipt_downloads' in str(error)
    else:
        raise AssertionError('download bound ignored')

# A receipt that a paging race displaces from the name listing is not lost: its
# run lists no current-attempt receipt, so it gets the per-run catalog.
displaced = fake.artifacts[3]
run_id = displaced['workflow_run']['id']
racing = lambda endpoint, parameters: (
    {**fake.page(endpoint, parameters), 'artifacts': [
        row for row in fake.page(endpoint, parameters)['artifacts'] if row is not displaced]}
    if endpoint.endswith('/actions/artifacts') else fake.page(endpoint, parameters))
run = next(row for row in pr_runs + native_runs if row['id'] == run_id)
catalogs, stats = module.collect_artifacts(
    REPO, [run], PR_NAMES if run in pr_runs else NATIVE_NAMES, fetch=racing)
assert catalogs == {run_id: fake.run_catalog(run_id)} and stats['per_run_catalogs'] == 1
print('request-budget=ok download-cache=ok displaced-receipt=per-run-catalog')
