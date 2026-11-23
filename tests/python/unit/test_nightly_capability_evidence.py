"""Failures, missing artifacts and untrusted observations must never stay green."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[3]


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), ROOT / f'scripts/ci/{name}.py')
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


COLLECT = module('collect-nightly-capability-evidence')
JOIN = module('nightly-capability-evidence')
IMPACT = module('capability-impact')
SHA = 'a' * 40
NOW = '2026-11-23T12:00:00Z'
DATE = '2026-11-23T10:00:00Z'


def producer(kind, documents, **overrides):
    return dict(kind=kind, workflow=f'{kind}.yml', conclusion='success', sourceSha=SHA,
                observedAt='2026-11-23T11:00:00Z', documents=documents, runId=123,
                runAttempt=1, artifacts=[456], **overrides)


def snapshot():
    envelope = dict(client_lane='desktop-qgis', protocol='wfs', run_date=DATE, server_commit=SHA,
                    summary=dict(total=1, passed=1, failed=0, skipped=0, not_applicable=0),
                    results=[dict(status='pass')])
    cite = dict(generatedAt=DATE, git=dict(sha=SHA, producerSha=SHA), suites=[dict(
        name='WFS 2.0', status='passed', totalTests=2, passed=2, failed=0, skipped=0, cantTell=0)])
    load = dict(generatedAt=DATE, scenarios=[dict(name='ogc_query_load', okCount=100, failCount=0,
                                               p95Ms=12, p99Ms=20, meanMs=8, maxMs=25, okRps=10, durationSeconds=600)])
    return dict(schemaVersion='1.0.0', repository='honua-io/honua-server', collectedAt=NOW,
                producers=[producer('interop', [envelope]) for _ in range(3)] +
                [producer('cite', [cite]), producer('geobench', [load])])


def matrix():
    return dict(schemaVersion='1.2.0', sourceArtifacts=[], capabilities=[dict(
        key='serve.ogc-api-features', interop=[dict(clientLane='desktop-qgis', protocol='wfs')],
        cite=[dict(suite='WFS 2.0', passRate=100.0)], geobench=['attribute-filter'])])


class CollectionTests(unittest.TestCase):
    def test_latest_failure_wins_over_older_success_and_fork_is_ignored(self):
        base = dict(status='completed', head_branch='trunk', head_repository=dict(full_name='honua-io/honua-server'),
                    event='schedule', path='.github/workflows/load-soak-nightly.yml')
        runs = [dict(base, id=1, created_at='2026-11-21', conclusion='success'),
                dict(base, id=2, created_at='2026-11-22', conclusion='failure'),
                dict(base, id=3, created_at='2026-11-23', head_repository=dict(full_name='fork/server'))]
        self.assertEqual(2, COLLECT.latest_run(runs, 'honua-io/honua-server', 'load-soak-nightly.yml')['id'])

    def test_archive_reads_only_expected_json_and_removes_evidence_urls(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, 'w') as archive:
            document = snapshot()['producers'][0]['documents'][0]
            document['results'][0]['evidence_ref'] = 'https://private.example/?token=secret'
            archive.writestr('../../never-extract.cert.json', json.dumps(document))
            archive.writestr('unrelated.json', 'invalid JSON')
        actual = COLLECT.read_documents(data.getvalue(), 'interop')
        self.assertEqual([{'status': 'pass'}], actual[0]['results'])

    def test_expired_and_previous_attempt_artifacts_do_not_resurrect_pass(self):
        run = dict(status='completed', head_branch='trunk', head_repository=dict(full_name='honua-io/honua-server'),
                   event='schedule', path='.github/workflows/load-soak-nightly.yml', id=1, run_attempt=2,
                   created_at=DATE, run_started_at=DATE, updated_at=NOW, head_sha=SHA,
                   conclusion='failure', html_url='https://example/run/1')
        artifacts = [dict(id=5, name='load-soak-reports-1', expired=False, size_in_bytes=10,
                          created_at='2026-11-22T12:00:00Z')]
        with patch.object(COLLECT, 'api', side_effect=[dict(workflow_runs=[run]), dict(artifacts=artifacts)]) as api:
            result = COLLECT.collect_producer('honua-io/honua-server', 'load-soak-nightly.yml', 'geobench')
        self.assertEqual([], result['documents'])
        self.assertEqual(2, api.call_count)


class JoinTests(unittest.TestCase):
    def join(self, data):
        return JOIN.apply(matrix(), data, IMPACT)['capabilities'][0]

    def test_current_results_join_with_dates_metrics_and_provenance(self):
        row = self.join(snapshot())
        self.assertEqual('fresh', row['interop'][0]['freshness']['state'])
        self.assertEqual(100, row['cite'][0]['passRate'])
        measured = next(r for r in row['geobenchResults'] if r['scenario'] == 'ogc_query_load')
        self.assertEqual(12, measured['metrics']['p95Ms'])
        self.assertEqual(SHA, measured['evidence']['sourceSha'])
        self.assertEqual('unknown', row['geobenchResults'][0]['freshness']['state'])

    def test_failed_latest_run_cannot_use_its_green_artifact(self):
        data = snapshot()
        for producer in data['producers']:
            producer['conclusion'] = 'failure'
        row = self.join(data)
        self.assertEqual('stale', row['interop'][0]['freshness']['state'])
        self.assertEqual(0, row['cite'][0]['passRate'])
        self.assertTrue(all(r['freshness']['state'] != 'fresh' for r in row['geobenchResults']))

    def test_missing_reports_do_not_fall_back_to_reviewed_baseline(self):
        data = snapshot()
        for producer in data['producers']:
            producer['documents'] = []
        row = self.join(data)
        self.assertEqual('unknown', row['interop'][0]['freshness']['state'])
        self.assertEqual(0, row['cite'][0]['passRate'])

    def test_missing_overlapping_producer_invalidates_other_older_green(self):
        data = snapshot()
        data['producers'][0]['documents'] = []
        self.assertNotEqual('fresh', self.join(data)['interop'][0]['freshness']['state'])

    def test_source_mismatch_future_and_stale_dates_fail_closed(self):
        for field, value in [('server_commit', 'b' * 40), ('run_date', '2026-11-24T00:00:00Z'),
                             ('run_date', '2026-10-01T00:00:00Z')]:
            with self.subTest(field=field, value=value):
                data = snapshot()
                for producer in data['producers'][:3]:
                    producer['documents'][0][field] = value
                self.assertNotEqual('fresh', self.join(data)['interop'][0]['freshness']['state'])

    def test_cite_wrong_producer_and_skips_cannot_claim_100_percent(self):
        for key, value in [('producerSha', 'b' * 40), ('skipped', 1)]:
            data = snapshot()
            cite = data['producers'][3]['documents'][0]
            (cite['git'] if key == 'producerSha' else cite['suites'][0])[key] = value
            self.assertEqual(0, self.join(data)['cite'][0]['passRate'])

    def test_incomplete_load_metrics_cannot_be_fresh(self):
        data = snapshot()
        del data['producers'][4]['documents'][0]['scenarios'][0]['p99Ms']
        measured = next(r for r in self.join(data)['geobenchResults'] if r['scenario'] == 'ogc_query_load')
        self.assertNotEqual('fresh', measured['freshness']['state'])

    def test_join_is_deterministic_and_does_not_mutate_snapshot(self):
        data = snapshot()
        before = copy.deepcopy(data)
        self.assertEqual(self.join(data), self.join(data))
        self.assertEqual(before, data)


if __name__ == '__main__':
    unittest.main()
