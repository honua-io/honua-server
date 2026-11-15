#!/usr/bin/env python3
"""Coalesced, failed and rerun completions must retain their publication evidence."""
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
SPEC = importlib.util.spec_from_file_location('sweep', Path(__file__).with_name('quarantine-sweep.py'))
sweep = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sweep)
REPOSITORY = 'example/repo'
SINCE = '2026-11-14T00:00:00Z'


def artifact(identity, run=10, **options):
    shard, attempt = options.get('shard', 'integration'), options.get('attempt', 1)
    return dict(id=identity, name=f'pr-gate-affected-shard-{shard}-attempt-{attempt}',
                created_at=SINCE, expired=False, size_in_bytes=100,
                workflow_run=dict(id=run, repository_id=1, head_repository_id=1))


def source(identity=10, **changes):
    return dict(id=identity, run_attempt=2, path='.github/workflows/pr-gate.yml',
                head_repository=dict(full_name=REPOSITORY), head_branch='trunk', status='completed', conclusion='success',
                updated_at='2026-11-15T00:00:00Z', html_url=f'https://github.com/{REPOSITORY}/actions/runs/{identity}',
                **changes)


def cursor(ids=()):
    return dict(schema=sweep.SCHEMA, since=SINCE, artifact_ids=list(ids))


class SweepTests(unittest.TestCase):
    def setUp(self):
        self.environment = patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'})
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def execute(self, artifacts, runs=None, previous=None):
        with tempfile.TemporaryDirectory() as directory:
            checkpoint = Path(directory) / 'cursor.json'
            with patch.object(sweep, 'load_cursor', return_value=previous or cursor()), \
                    patch.object(sweep, 'inventory', return_value=artifacts), \
                    patch.object(sweep, 'api', side_effect=lambda path: (runs or {})[int(path.rsplit('/', 1)[1])]), \
                    patch.object(sweep, 'publish_run') as publish:
                sweep.sweep(checkpoint)
            return json.loads(checkpoint.read_text()), publish

    def test_replaced_pending_events_are_all_published_by_next_sweep(self):
        evidence = [artifact(1, 10), artifact(2, 11), artifact(3, 12)]
        state, publish = self.execute(evidence, {i: source(i) for i in (10, 11, 12)})
        self.assertEqual([1, 2, 3], state['artifact_ids'])
        self.assertEqual([10, 11, 12], [call.args[0]['id'] for call in publish.call_args_list])
        _, replay = self.execute(evidence, previous=state)
        replay.assert_not_called()

    def test_failure_leaves_checkpoint_uncommitted_and_retry_replays(self):
        evidence = [artifact(1), artifact(2, 11)]
        runs = {10: source(), 11: source(11)}
        with tempfile.TemporaryDirectory() as directory:
            checkpoint = Path(directory) / 'cursor.json'
            with patch.object(sweep, 'load_cursor', return_value=cursor()), \
                    patch.object(sweep, 'inventory', return_value=evidence), \
                    patch.object(sweep, 'api', return_value=source()), \
                    patch.object(sweep, 'publish_run', side_effect=RuntimeError('API unavailable')), \
                    self.assertRaises(RuntimeError):
                sweep.sweep(checkpoint)
            self.assertFalse(checkpoint.exists())
        state, publish = self.execute(evidence, runs)
        self.assertEqual([1, 2], state['artifact_ids'])
        self.assertEqual(2, publish.call_count)

    def test_incomplete_source_is_revisited_and_old_run_rerun_is_discovered(self):
        # Source creation time is deliberately old; only completion matters.
        evidence = [artifact(1)]
        pending = source(); pending['status'] = 'in_progress'
        state, publish = self.execute(evidence, {10: pending})
        self.assertEqual([], state['artifact_ids'])
        publish.assert_not_called()
        rerun = source(created_at='2020-01-01T00:00:00Z')
        state, publish = self.execute(evidence, {10: rerun}, previous=state)
        self.assertEqual([1], state['artifact_ids'])
        self.assertEqual(1, publish.call_count)

    def test_untrusted_cancelled_and_pre_marker_sources_never_publish(self):
        runs = {10: source(), 11: source(11), 12: source(12), 13: source(13)}
        runs[10]['head_repository'] = dict(full_name='fork/repo')
        runs[11]['path'] = '.github/workflows/untrusted.yml'
        runs[12]['conclusion'] = 'cancelled'
        runs[13]['updated_at'] = '2026-11-13T23:59:59Z'
        state, publish = self.execute([artifact(i, i) for i in runs], runs)
        publish.assert_not_called()
        self.assertEqual([10, 11, 12, 13], state['artifact_ids'])

    def test_batch_cap_preserves_backlog_and_missing_artifacts_are_pruned(self):
        with patch.object(sweep, 'MAX_BATCH_RUNS', 1):
            state, publish = self.execute([artifact(1), artifact(2, 11)], {10: source()}, cursor([999]))
        self.assertEqual([1], state['artifact_ids'])
        self.assertEqual(1, publish.call_count)

    def test_newest_attempt_wins_even_when_artifact_ids_rank_differently(self):
        items = [artifact(100, attempt=1), artifact(10, attempt=2), artifact(11, shard='unit')]
        self.assertEqual({10, 11}, {item['id'] for item in sweep.newest_shards(items)})
        self.assertIn(10, sweep.candidates(items, {10, 11}))  # unseen older artifact is consumed, not replayed
        with patch.object(sweep, 'download') as download, patch.object(sweep.PUBLISHER, 'publish') as publish:
            sweep.publish_run(source(), items, {10, 11})
        download.assert_not_called()
        publish.assert_not_called()

    def test_publication_preserves_source_link_and_uses_artifact_ids_as_directories(self):
        with patch.object(sweep, 'download') as download, patch.object(sweep.PUBLISHER, 'publish') as publish:
            sweep.publish_run(source(), [artifact(42)], set())
        self.assertEqual('42', download.call_args.args[1].name)
        self.assertEqual(1, publish.call_count)
        self.assertTrue(os.environ['QUARANTINE_EVIDENCE_URL'].endswith('/runs/10/attempts/1'))


class CheckpointTests(unittest.TestCase):
    def test_legacy_success_reconciles_all_retained_backlog(self):
        run = source(event='workflow_run', created_at=SINCE)
        run['path'] = sweep.WORKFLOW
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                patch.object(sweep, 'api', return_value={'workflow_runs': [run]}), \
                patch.object(sweep, 'inventory', side_effect=lambda path, key: [] if key == 'artifacts' else [{'conclusion': 'success'}]):
            self.assertEqual(dict(cursor(), since='1970-01-01T00:00:00Z'), sweep.load_cursor())

    def test_missing_new_checkpoint_fails_closed_instead_of_skipping_backlog(self):
        run = source(event='schedule', created_at=SINCE, display_title='Quarantine evidence sweep')
        run['path'] = sweep.WORKFLOW
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                patch.object(sweep, 'api', return_value={'workflow_runs': [run]}), \
                patch.object(sweep, 'inventory', side_effect=lambda path, key: [] if key == 'artifacts' else [{'conclusion': 'success'}]), self.assertRaisesRegex(ValueError, 'checkpoint missing'):
            sweep.load_cursor()

    def test_successful_skipped_completion_does_not_hide_previous_checkpoint(self):
        skipped = source(event='workflow_run', display_title='Quarantine evidence sweep')
        skipped['path'] = sweep.WORKFLOW
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                patch.object(sweep, 'api', return_value={'workflow_runs': [skipped, dict(skipped, id=11)]}), \
                patch.object(sweep, 'inventory', side_effect=[[], [{'conclusion': 'skipped'}],
                    [dict(artifact(2), name=sweep.CURSOR_PREFIX + '2')]]), \
                patch.object(sweep, 'download', side_effect=lambda item, root:
                    (root / 'quarantine-cursor.json').write_text(json.dumps(cursor([1])))):
            self.assertEqual(cursor([1]), sweep.load_cursor())

    def test_nondefault_dispatch_cannot_supply_checkpoint(self):
        run = source(event='workflow_dispatch'); run['path'] = sweep.WORKFLOW
        run['head_branch'] = 'feature/untrusted'
        trusted = dict(run, id=11, head_branch='trunk')
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                patch.object(sweep, 'api', return_value={'workflow_runs': [run, trusted]}), \
                patch.object(sweep, 'cursor_from_run', return_value=cursor([1])) as read:
            self.assertEqual(cursor([1]), sweep.load_cursor())
            read.assert_called_once_with(trusted)
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                self.assertRaisesRegex(ValueError, 'Untrusted'):
            sweep.cursor_from_run(run)

    def test_old_evidence_cannot_renew_legacy_quarantine_during_backfill(self):
        publisher = sweep.PUBLISHER
        entry = dict(project='tests/Honua.Core.Tests/Honua.Core.Tests.csproj', test='Example.Flaky')
        issue = dict(body=publisher.issue_body(entry, 'https://example.com/legacy-run'))
        with patch.dict(os.environ, {'QUARANTINE_EVIDENCE_CREATED_AT': '2020-01-01T00:00:00Z'}):
            self.assertTrue(publisher.repeats_evidence(issue, 'https://example.com/legacy-run/attempts/1'))
        with patch.dict(os.environ, {'QUARANTINE_EVIDENCE_CREATED_AT': '2099-01-01T00:00:00Z'}):
            self.assertFalse(publisher.repeats_evidence(issue, 'https://example.com/legacy-run/attempts/2'))

    def test_checkpoint_is_loaded_only_from_trusted_successful_publisher(self):
        run = source(event='workflow_run'); run['path'] = sweep.WORKFLOW
        def download(item, root):
            (root / 'quarantine-cursor.json').write_text(json.dumps(cursor([1])))
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'QUARANTINE_DEFAULT_BRANCH': 'trunk'}), \
                patch.object(sweep, 'api', return_value={'workflow_runs': [run]}), \
                patch.object(sweep, 'inventory', return_value=[dict(artifact(2), name=sweep.CURSOR_PREFIX + '2')]), \
                patch.object(sweep, 'download', side_effect=download):
            self.assertEqual(cursor([1]), sweep.load_cursor())
            run['event'] = 'pull_request'
            with self.assertRaisesRegex(ValueError, 'Untrusted'):
                sweep.load_cursor()

    def test_inventory_is_paginated_and_capacity_overflow_fails_closed(self):
        with patch.object(sweep, 'api', side_effect=[
            dict(total_count=101, artifacts=[artifact(i) for i in range(1, 101)]),
            dict(total_count=101, artifacts=[artifact(101)]),
        ]):
            self.assertEqual(101, len(sweep.inventory('actions/artifacts', 'artifacts')))
        with patch.object(sweep, 'api', return_value=dict(total_count=sweep.MAX_PAGES * 100 + 1)), \
                self.assertRaisesRegex(ValueError, 'capacity'):
            sweep.inventory('actions/artifacts', 'artifacts')

    def test_inventory_fetches_bounded_parallel_pages_without_losing_order(self):
        barrier = threading.Barrier(8, timeout=2)
        active = 0
        peak = 0
        lock = threading.Lock()

        def page(path):
            nonlocal active, peak
            number = int(path.rsplit('=', 1)[1])
            if number > 1:
                with lock:
                    active += 1
                    peak = max(peak, active)
                barrier.wait()
                with lock:
                    active -= 1
            start = (number - 1) * 100
            return dict(total_count=850, artifacts=[dict(id=i) for i in range(start, min(start + 100, 850))])

        with patch.object(sweep, 'api', side_effect=page):
            items = sweep.inventory('actions/artifacts', 'artifacts')
        self.assertEqual(8, peak)
        self.assertEqual(list(range(850)), [item['id'] for item in items])

    def test_inventory_page_failure_leaves_checkpoint_unwritten(self):
        def page(path):
            if path.endswith('page=2'):
                raise RuntimeError('page unavailable')
            return dict(total_count=201, artifacts=[artifact(i) for i in range(1, 101)])

        with tempfile.TemporaryDirectory() as directory:
            checkpoint = Path(directory) / 'cursor.json'
            with patch.object(sweep, 'load_cursor', return_value=cursor()), \
                    patch.object(sweep, 'api', side_effect=page), \
                    patch.object(sweep, 'publish_run') as publish, \
                    self.assertRaisesRegex(RuntimeError, 'page unavailable'):
                sweep.sweep(checkpoint)
            self.assertFalse(checkpoint.exists())
            publish.assert_not_called()

    def test_inventory_upload_during_out_of_order_reads_leaves_checkpoint_unwritten(self):
        page_three_read = threading.Event()
        old = list(range(301, 0, -1))
        new = [302] + old

        def page(path):
            number = int(path.rsplit('=', 1)[1])
            if number == 1:
                snapshot = old
            elif number == 3:
                snapshot = old
                page_three_read.set()
            else:
                self.assertTrue(page_three_read.wait(2))
                snapshot = new
            start = (number - 1) * 100
            return dict(total_count=len(snapshot), artifacts=[dict(id=i) for i in snapshot[start:start + 100]])

        with tempfile.TemporaryDirectory() as directory:
            checkpoint = Path(directory) / 'cursor.json'
            with patch.object(sweep, 'load_cursor', return_value=cursor()), \
                    patch.object(sweep, 'api', side_effect=page), \
                    patch.object(sweep, 'publish_run') as publish, \
                    self.assertRaisesRegex(ValueError, 'changed during discovery'):
                sweep.sweep(checkpoint)
            self.assertFalse(checkpoint.exists())
            publish.assert_not_called()

    def test_inventory_duplicate_across_pages_cannot_hide_a_missing_entry(self):
        with patch.object(sweep, 'api', side_effect=[
            dict(total_count=101, artifacts=[artifact(i) for i in range(100)]),
            dict(total_count=101, artifacts=[artifact(99)]),
        ]), self.assertRaisesRegex(ValueError, 'incomplete'):
            sweep.inventory('actions/artifacts', 'artifacts')

    def test_inventory_full_last_page_requires_empty_terminal_page(self):
        def page(path):
            number = int(path.rsplit('=', 1)[1])
            return dict(total_count=200, artifacts=[dict(id=i) for i in range((number - 1) * 100, min(number * 100, 200))])

        with patch.object(sweep, 'api', side_effect=page) as read:
            self.assertEqual(list(range(200)), [item['id'] for item in sweep.inventory('actions/artifacts', 'artifacts')])
        self.assertEqual(3, read.call_count)

    def test_inventory_growth_beyond_capacity_in_later_page_fails_closed(self):
        def page(path):
            if path.endswith('page=1'):
                return dict(total_count=101, artifacts=[artifact(i) for i in range(100)])
            return dict(total_count=sweep.MAX_PAGES * 100 + 1, artifacts=[])

        with patch.object(sweep, 'api', side_effect=page), self.assertRaisesRegex(ValueError, 'capacity'):
            sweep.inventory('actions/artifacts', 'artifacts')

    def test_inventory_accepts_retained_repository_volume(self):
        count = 75855
        def page(path):
            number = int(path.rsplit('=', 1)[1])
            start = (number - 1) * 100
            return dict(total_count=count, artifacts=[dict(id=i) for i in range(start, min(start + 100, count))])
        with patch.object(sweep, 'api', side_effect=page) as read:
            items = sweep.inventory('actions/artifacts', 'artifacts')
        self.assertEqual(count, len(items))
        self.assertEqual(759, read.call_count)
        self.assertEqual(set(range(count)), {item['id'] for item in items})

    def test_archive_rejects_traversal_symlinks_and_expansion_overflow(self):
        for name, mode in (('../evil.flake-candidates.json', 0), ('/evil.retry-attempt.xml', 0),
                           ('link.first-attempt.xml', stat.S_IFLNK << 16)):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory, \
                    zipfile.ZipFile(io.BytesIO(), 'w') as archive:
                item = zipfile.ZipInfo(name); item.external_attr = mode
                archive.writestr(item, 'bad')
                with self.assertRaisesRegex(ValueError, 'Unsafe'):
                    sweep.extract_proof(archive, Path(directory))
        with tempfile.TemporaryDirectory() as directory, zipfile.ZipFile(io.BytesIO(), 'w') as archive:
            archive.writestr('run.first-attempt.xml', '1234')
            with patch.object(sweep, 'MAX_EXPANDED_BYTES', 1), self.assertRaisesRegex(ValueError, 'extraction limit'):
                sweep.extract_proof(archive, Path(directory))


if __name__ == '__main__':
    unittest.main()
