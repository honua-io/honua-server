#!/usr/bin/env python3
"""Offline managed build selection/receipt regression fixtures; never execute dotnet."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'managed-test-build.py'
spec = importlib.util.spec_from_file_location('managed_build', SCRIPT)
managed = importlib.util.module_from_spec(spec)
spec.loader.exec_module(managed)


class ManagedBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.a = 'tests/dotnet/A.Tests/A.Tests.csproj'
        self.b = 'tests/dotnet/B.Tests/B.Tests.csproj'
        self.literal = {p: p.replace('/', '\\') for p in (self.a, self.b)}
        for p in self.literal:
            target = self.root / p
            target.parent.mkdir(parents=True)
            target.write_text('<Project />')
            (target.parent / 'obj').mkdir()
            (target.parent / 'obj/project.assets.json').write_text(json.dumps({'libraries': {}, 'packageFolders': {}}))
        (self.root / '.github').mkdir()
        (self.root / '.github/test-projects.json').write_text(json.dumps({'projects': {p: {} for p in self.literal}}))
        (self.root / 'Honua.sln').write_text('\n'.join(
            f'Project("{{TYPE}}") = "Test", "{literal}", "{{ID}}"\nEndProject'
            for literal in self.literal.values()))

    def invoke(self, *args):
        with patch.object(sys, 'argv', [str(SCRIPT), *args, '--root', str(self.root)]), contextlib.redirect_stdout(io.StringIO()):
            managed.main()

    def test_filter_unions_requested_and_base_projects_once(self):
        requested = self.root / 'requested.json'
        requested.write_text(json.dumps([self.a, self.a]))
        base = self.root / 'base.slnf'
        base.write_text(json.dumps({'solution': {'projects': [self.literal[self.b], self.literal[self.a]]}}))
        target = self.root / 'union.slnf'
        self.invoke('filter', '--projects-file', str(requested), '--base-filter', str(base), '--output', str(target))
        result = json.loads(target.read_text())['solution']
        self.assertEqual(str(self.root / 'Honua.sln'), result['path'])
        self.assertEqual(sorted(self.literal.values()), result['projects'])

    def test_filter_preserves_standalone_tooling_outside_solution(self):
        tooling = ['scripts/cloud/lambda-deploy-driver/LambdaDeployDriver.csproj',
                   'scripts/conformance/mcp/manifest-gen/Honua.Mcp.ManifestGen.csproj']
        for project in tooling:
            target = self.root / project
            target.parent.mkdir(parents=True)
            target.write_text('<Project />')
        requested = self.root / 'requested.json'
        requested.write_text(json.dumps([self.a]))
        base = self.root / 'base.slnf'
        base.write_text(json.dumps({'solution': {'projects': [self.b, *tooling, tooling[0]]}}))
        target, standalone = self.root / 'build.slnf', self.root / 'standalone.json'
        self.invoke('filter', '--projects-file', str(requested), '--base-filter', str(base),
                    '--output', str(target), '--standalone-output', str(standalone))
        self.assertEqual(sorted(self.literal.values()), json.loads(target.read_text())['solution']['projects'])
        self.assertEqual(sorted(tooling), json.loads(standalone.read_text()))
        with patch.object(managed.subprocess, 'run') as execute:
            self.invoke('build-standalone', '--projects-file', str(standalone))
        self.assertEqual(4, execute.call_count)
        for project, restore, build in zip(sorted(tooling), execute.call_args_list[::2], execute.call_args_list[1::2]):
            self.assertIn(project, restore.args[0])
            self.assertIn(project, build.args[0])
            self.assertTrue(restore.kwargs['check'])
            self.assertTrue(build.kwargs['check'])
            self.assertIn('-p:Configuration=Release', restore.args[0])
            self.assertEqual(['dotnet', 'build'], build.args[0][:2])
            self.assertIn('--no-restore', build.args[0])
            for command in [restore.args[0], build.args[0]]:
                self.assertLessEqual(set(managed.PROPERTIES), set(command))
        with patch.object(managed.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, ['restore'])):
            with self.assertRaises(subprocess.CalledProcessError):
                managed.build_standalone(self.root, tooling)

    def test_standalone_selection_fails_closed_for_invalid_paths(self):
        for invalid in ['missing.csproj', '../escape.csproj', '/absolute.csproj',
                        'C:/escape.csproj', 'scripts/../escape.csproj', 'scripts//a.csproj',
                        'scripts/not-a-project.txt']:
            for standalone in [None, self.root / 'standalone.json']:
                with self.subTest(invalid=invalid, standalone=standalone), self.assertRaises(ValueError):
                    managed.write_filter(self.root, [self.a, invalid],
                                         managed.FilterOutputs(self.root / 'build.slnf', standalone))
        with self.assertRaises(ValueError):
            managed.validate_standalone_projects(self.root, [12])
        (self.root / 'escape.csproj').symlink_to(SCRIPT)
        with self.assertRaises(ValueError):
            managed.validate_standalone_projects(self.root, ['escape.csproj'])
        with patch.object(managed.subprocess, 'run') as execute:
            managed.build_standalone(self.root, [])
        execute.assert_not_called()

    def test_fallback_restore_and_build_use_same_missing_filter(self):
        calls = []

        def execute(command, **kwargs):
            self.assertTrue(kwargs['check'])
            self.assertEqual(self.root, kwargs['cwd'])
            selection = next(Path(item) for item in command if str(item).endswith('.slnf'))
            content = json.loads(selection.read_text())
            calls.append((command, selection, content))

        with patch.object(managed.subprocess, 'run', side_effect=execute), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([self.b], managed.fallback(self.root, [self.a, self.b], [self.a]))
        self.assertEqual(2, len(calls))
        self.assertEqual(calls[0][1:], calls[1][1:])
        self.assertEqual([self.literal[self.b]], calls[0][2]['solution']['projects'])
        self.assertIn('dotnet-restore-retry.sh', calls[0][0][1])
        self.assertEqual(['dotnet', 'build'], calls[1][0][:2])
        self.assertIn('--no-restore', calls[1][0])
        self.assertIn('-graphBuild', calls[1][0])
        for command, _, _ in calls:
            self.assertLessEqual(set(managed.PROPERTIES), set(command))
        self.assertFalse(calls[0][1].exists(), 'temporary filter must be cleaned after both commands finish')

    def test_verified_complete_receipt_needs_no_restore_or_build(self):
        with patch.object(managed.subprocess, 'run') as execute, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([], managed.fallback(self.root, [self.a, self.b], [self.b, self.a]))
        execute.assert_not_called()

    def package_assets(self, project, *, exists):
        cache = self.root / 'nuget'
        relative = 'adapter/1.0/build/adapter.props'
        if exists:
            target = cache / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text('<Project />')
        assets = {'libraries': {'adapter/1.0': {'type': 'package', 'path': 'adapter/1.0',
                    'files': ['build/adapter.props']}}, 'packageFolders': {str(cache): {}}}
        path = self.root / Path(project).parent / 'obj/project.assets.json'
        path.write_text(json.dumps(assets))
        return path

    def test_verified_binaries_with_missing_packages_restore_without_build(self):
        self.package_assets(self.a, exists=False)
        calls = []
        def execute(command, **kwargs):
            selection = next(Path(item) for item in command if str(item).endswith('.slnf'))
            calls.append((command, json.loads(selection.read_text())))
            self.package_assets(self.a, exists=True)
        with patch.object(managed.subprocess, 'run', side_effect=execute), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([], managed.fallback(self.root, [self.a, self.b], [self.a, self.b]))
        self.assertEqual(1, len(calls))
        self.assertEqual('bash', calls[0][0][0])
        self.assertIn('-p:Configuration=Release', calls[0][0])
        self.assertEqual([self.literal[self.a]], calls[0][1]['solution']['projects'])

    def test_cold_consumer_restores_prerequisites_without_compiling_outputs(self):
        for project in [self.a, self.b]:
            (self.root / Path(project).parent / 'obj/project.assets.json').unlink()
        calls = []

        def execute(command, **kwargs):
            self.assertTrue(kwargs['check'])
            self.assertEqual(self.root, kwargs['cwd'])
            selection = next(Path(item) for item in command if str(item).endswith('.slnf'))
            calls.append((command, json.loads(selection.read_text())['solution']['projects']))
            for project in [self.a, self.b]:
                self.package_assets(project, exists=True)

        with patch.object(managed.subprocess, 'run', side_effect=execute), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([], managed.fallback(self.root, [self.a, self.b], [self.a, self.b]))
        self.assertEqual(1, len(calls))
        self.assertEqual('bash', calls[0][0][0])
        self.assertIn('dotnet-restore-retry.sh', calls[0][0][1])
        self.assertEqual(sorted(self.literal.values()), calls[0][1])
        for project in [self.a, self.b]:
            self.assertTrue(managed.package_dependencies_ready(self.root, project))
            self.assertFalse((self.root / Path(project).parent / 'bin').exists())

    def test_restore_union_includes_cache_miss_but_compile_excludes_verified_binary(self):
        self.package_assets(self.a, exists=False)
        calls = []
        def execute(command, **kwargs):
            selection = next(Path(item) for item in command if str(item).endswith('.slnf'))
            calls.append((command, json.loads(selection.read_text())['solution']['projects']))
            if command[0] == 'bash':
                self.package_assets(self.a, exists=True)
        with patch.object(managed.subprocess, 'run', side_effect=execute), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([self.b], managed.fallback(self.root, [self.a, self.b], [self.a]))
        self.assertEqual(2, len(calls))
        self.assertEqual(sorted(self.literal.values()), calls[0][1])
        self.assertEqual([self.literal[self.b]], calls[1][1])
        self.assertIn('--no-restore', calls[1][0])

    def test_complete_package_files_need_no_sdk_invocation(self):
        self.package_assets(self.a, exists=True)
        with patch.object(managed.subprocess, 'run') as execute, contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual([], managed.fallback(self.root, [self.a], [self.a]))
        execute.assert_not_called()

    def test_missing_malformed_and_traversing_assets_conservatively_restore(self):
        assets = self.root / Path(self.a).parent / 'obj/project.assets.json'
        invalid = [None, 'not-json', '{}', json.dumps({'libraries': [], 'packageFolders': {}})]
        for value in invalid:
            if value is None:
                assets.unlink(missing_ok=True)
            else:
                assets.write_text(value)
            self.assertFalse(managed.package_dependencies_ready(self.root, self.a))
        for value in ('../escape', '/absolute', 'C:\\escape', 'build/../escape'):
            path = self.package_assets(self.a, exists=True)
            data = json.loads(path.read_text())
            data['libraries']['adapter/1.0']['files'] = [value]
            path.write_text(json.dumps(data))
            self.assertFalse(managed.package_dependencies_ready(self.root, self.a))
        with patch.object(managed.subprocess, 'run', side_effect=lambda *args, **kwargs: self.package_assets(self.a, exists=True)) as execute, contextlib.redirect_stdout(io.StringIO()):
            managed.fallback(self.root, [self.a], [self.a])
        self.assertEqual(1, execute.call_count)
        self.assertEqual('bash', execute.call_args.args[0][0])

    def test_successful_restore_with_incomplete_cache_fails_before_compilation(self):
        self.package_assets(self.a, exists=False)
        with patch.object(managed.subprocess, 'run') as execute, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(ValueError, 'restore completed but package dependencies remain missing'):
                managed.fallback(self.root, [self.a, self.b], [self.a])
        self.assertEqual(1, execute.call_count)
        self.assertEqual('bash', execute.call_args.args[0][0])

    def test_restore_failure_never_starts_compilation(self):
        self.package_assets(self.a, exists=False)
        filters = []
        def fail_restore(command, **kwargs):
            filters.extend(Path(item) for item in command if str(item).endswith('.slnf'))
            raise subprocess.CalledProcessError(1, command)
        with patch.object(managed.subprocess, 'run', side_effect=fail_restore) as execute, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(subprocess.CalledProcessError):
                managed.fallback(self.root, [self.a, self.b], [self.a])
        self.assertEqual(1, execute.call_count)
        self.assertTrue(filters)
        self.assertTrue(all(not path.exists() for path in filters))

    def test_invalid_restore_receipts_fail_before_execution(self):
        for receipt in ([self.b], ['tests/dotnet/Missing/Missing.csproj'], {}, [False]):
            with self.subTest(receipt=receipt), patch.object(managed.subprocess, 'run') as execute:
                with self.assertRaises(ValueError):
                    managed.fallback(self.root, [self.a], receipt)
                execute.assert_not_called()

    def test_matrix_selection_adds_nonadvisory_projects_once(self):
        matrix = self.root / 'matrix.json'
        matrix.write_text(json.dumps({'matrix_include': json.dumps([
            {'shard_name': 'A', 'csproj': self.a}, {'shard_name': 'B', 'csproj': self.b},
            {'shard_name': 'Advisory', 'csproj': 'not-a-project', 'advisory': True}])}))
        output = self.root / 'selection.json'
        with patch.object(managed, 'gate_projects', return_value=[self.a]):
            self.invoke('projects', '--matrix-file', str(matrix), '--output', str(output))
        self.assertEqual([self.a, self.b], json.loads(output.read_text()))

    def test_pr_shards_excludes_independent_fast_projects(self):
        matrix = self.root / 'matrix.json'
        matrix.write_text(json.dumps({'include': [
            {'shard_name': 'B', 'csproj': self.b},
            {'shard_name': 'Advisory', 'csproj': self.a, 'advisory': True}]}))
        output = self.root / 'selection.json'
        with patch.object(managed, 'gate_projects', side_effect=AssertionError('Fast owns these projects')):
            self.invoke('projects', '--lane', 'pr-shards', '--matrix-file', str(matrix), '--output', str(output))
        self.assertEqual([self.b], json.loads(output.read_text()))

    def test_malformed_matrix_fails_closed(self):
        matrix = self.root / 'matrix.json'
        for value in ({}, {'unrelated': []}, [{'advisory': 'false', 'csproj': self.b}], [None], {'include': 'not-json'}):
            matrix.write_text(json.dumps(value))
            with self.subTest(value=value), patch.object(managed, 'gate_projects', return_value=[self.a]):
                with self.assertRaises((ValueError, TypeError)):
                    self.invoke('projects', '--matrix-file', str(matrix))


if __name__ == '__main__':
    unittest.main()
