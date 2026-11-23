#!/usr/bin/env python3
"""Rank drift: total band, grown shards, estimated shards and the one tracking issue."""
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location('drift', Path(__file__).with_name('shard-rank-drift.py'))
drift = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(drift)
REPO = 'example/repo'


def config(**ranks):
    return dict(shards=[dict(name=name, dispatch_rank=rank) for name, rank in ranks.items()])


def job(name, minutes, conclusion='success'):
    return dict(name=f'Server Tests ({name})', conclusion=conclusion,
                started_at='2026-11-23T00:00:00Z', completed_at=f'2026-11-23T00:{minutes:02d}:00Z')


def samples(**minutes):
    return {name: list(values) for name, values in minutes.items()}


class FakeGitHub:
    def __init__(self, issues=()):
        self.issues, self.writes = list(issues), []

    def __call__(self, endpoint, payload=None, method=None):
        if payload is None:
            return self.issues
        self.writes.append((method or 'POST', endpoint, payload))
        return dict(number=77)


class TotalDrift(unittest.TestCase):
    def test_inflated_ranks_drift(self):
        report = drift.analyze(config(a=20, b=18), dict(ranks=dict(a=20, b=18)), samples(a=[10, 11], b=[9, 10]))
        self.assertEqual(report['total']['status'], 'drift')
        self.assertEqual(report['total']['ratio'], 1.9)

    def test_deflated_ranks_drift(self):
        report = drift.analyze(config(a=5, b=5), dict(ranks=dict(a=5, b=5)), samples(a=[10, 10], b=[9, 9]))
        self.assertEqual(report['total']['status'], 'drift')

    def test_in_band_is_clean(self):
        report = drift.analyze(config(a=10, b=9), dict(ranks=dict(a=10, b=9)), samples(a=[10, 11], b=[9, 8]))
        self.assertEqual(report['total']['status'], 'ok')
        self.assertFalse(drift.has_findings(report))

    def test_too_few_measured_shards_is_insufficient(self):
        report = drift.analyze(config(a=20, b=9, c=9), dict(ranks={}), samples(a=[10, 10], b=[9]))
        self.assertEqual(report['total']['status'], 'insufficient')


class ShardFindings(unittest.TestCase):
    def test_grown_shard_is_listed(self):
        report = drift.analyze(config(a=6, b=10), dict(ranks=dict(a=6, b=10)), samples(a=[12, 13], b=[10, 10]))
        self.assertEqual([row['name'] for row in report['grown']], ['a'])
        self.assertEqual(report['grown'][0]['median'], 12.5)

    def test_small_shard_spike_under_floor_is_not_listed(self):
        report = drift.analyze(config(a=1, b=10), dict(ranks=dict(a=1, b=10)), samples(a=[3, 3], b=[10, 10]))
        self.assertEqual(report['grown'], [])

    def test_single_sample_is_not_listed(self):
        report = drift.analyze(config(a=6, b=10), dict(ranks=dict(a=6, b=10)), samples(a=[30], b=[10, 10]))
        self.assertEqual(report['grown'], [])

    def test_estimated_shard_with_samples_is_listed(self):
        report = drift.analyze(config(a=5, b=10, c=4), dict(ranks=dict(b=10)), samples(a=[5, 6], b=[10, 10], c=[4]))
        self.assertEqual([row['name'] for row in report['estimated']], ['a'])


class Sampling(unittest.TestCase):
    def test_only_passing_server_shard_jobs_count(self):
        jobs = [job('a', 12), job('b', 30, 'failure'), dict(name='Build & Format Check', conclusion='success')]
        self.assertEqual(drift.shard_samples(jobs), dict(a=[12.0]))

    def test_window_keeps_trunk_full_runs_only(self):
        def run(identity, event, title, repo=REPO):
            return dict(id=identity, event=event, display_title=title, head_repository=dict(full_name=repo))
        page = dict(workflow_runs=[run(1, 'workflow_dispatch', 'CI affected x'), run(2, 'schedule', 'CI full '),
                                   run(3, 'push', 'CI full '), run(4, 'workflow_dispatch', 'CI full ', 'fork/repo'),
                                   run(5, 'workflow_dispatch', 'CI full '), run(6, 'schedule', 'CI full '),
                                   run(7, 'schedule', 'CI full ')])
        self.assertEqual([r['id'] for r in drift.full_runs(lambda endpoint: page, REPO)], [2, 5, 6])


class TrackingIssue(unittest.TestCase):
    DRIFT = dict(total=dict(status='drift'), grown=[], estimated=[])
    CLEAN = dict(total=dict(status='ok'), grown=[], estimated=[])

    def test_finding_opens_one_issue(self):
        github = FakeGitHub()
        self.assertEqual(drift.reconcile(github, REPO, self.DRIFT, 'body'), 'opened #77')
        self.assertEqual(github.writes[0][1], f'repos/{REPO}/issues')

    def test_changed_finding_updates_the_open_issue(self):
        github = FakeGitHub([dict(number=5, body=f'{drift.MARKER} old')])
        self.assertEqual(drift.reconcile(github, REPO, self.DRIFT, f'{drift.MARKER} new'), 'updated #5')
        self.assertEqual(github.writes, [('PATCH', f'repos/{REPO}/issues/5', dict(body=f'{drift.MARKER} new'))])

    def test_back_in_band_closes_it(self):
        github = FakeGitHub([dict(number=5, body=drift.MARKER), dict(number=6, body=drift.MARKER, pull_request={})])
        self.assertEqual(drift.reconcile(github, REPO, self.CLEAN, 'body'), 'closed #5')
        self.assertEqual(github.writes[-1][2]['state'], 'closed')

    def test_insufficient_window_never_touches_issues(self):
        github = FakeGitHub([dict(number=5, body=drift.MARKER)])
        report = dict(total=dict(status='insufficient'), grown=[], estimated=[])
        self.assertEqual(drift.reconcile(github, REPO, report, 'body'), 'skipped')
        self.assertEqual(github.writes, [])


if __name__ == '__main__':
    unittest.main()
