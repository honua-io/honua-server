#!/usr/bin/env python3
"""Prove producer partition coverage and reject overlapping project graphs."""
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'partition-managed-producer.py'
sys.path.insert(0, str(MODULE.parent))
spec = importlib.util.spec_from_file_location('partition', MODULE)
partition = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = partition
spec.loader.exec_module(partition)
SERVER = partition.SERVER
OTHER = 'src/Other/Other.csproj'


class ProducerTests(unittest.TestCase):
    def test_partition_is_disjoint_and_complete(self):
        selected = [SERVER, OTHER, SERVER]
        server = set(partition.server_projects(selected))
        main = set(partition.main_projects(selected))
        self.assertFalse(server & main)
        self.assertEqual(set(selected), server | main)
        self.assertEqual([], partition.server_projects([OTHER]))
        for invalid in [None, {}, [1]]:
            with self.assertRaises(ValueError):
                partition.main_projects(invalid)

    def test_consumer_selects_own_identity_and_rebuilds_mixed_projects(self):
        self.assertEqual('server', partition.consumer_producer([SERVER]))
        self.assertEqual('main', partition.consumer_producer([OTHER]))
        self.assertEqual('none', partition.consumer_producer([SERVER, OTHER]))
        with self.assertRaises(ValueError):
            partition.consumer_producer([None])

    def make_root(self, root, reference=''):
        for project in [SERVER, OTHER]:
            path = root / project
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('<Project>' + (reference if project == OTHER else '') + '</Project>')
        (root / 'Honua.sln').write_text('\n'.join(
            f'Project("x") = "project", "{project}", "id"' for project in [SERVER, OTHER]))

    def test_full_and_scoped_builds_retain_every_other_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_root(root)
            source, output = root / 'source.slnf', root / 'output.slnf'
            source.write_text(json.dumps({'solution': {'projects': [SERVER, OTHER]}}))
            for mode in ['full', 'affected', 'test-roots']:
                partition.exclude_server(partition.BuildFilterRequest(root, mode, source, output))
                self.assertEqual([OTHER], json.loads(output.read_text())['solution']['projects'])
            for projects in [[SERVER], ['unknown.csproj']]:
                source.write_text(json.dumps({'solution': {'projects': projects}}))
                with self.assertRaises(ValueError):
                    partition.exclude_server(partition.BuildFilterRequest(root, 'affected', source, output))

    def test_imported_collector_stays_on_independent_architecture_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_root(root)
            collector = root / partition.COLLECTOR
            collector.parent.mkdir(parents=True)
            collector.write_text('<Project><Import Project="../../../coverage.props" /></Project>')
            (root / 'coverage.props').write_text(
                f'<Project><ItemGroup><ProjectReference Include="$(MSBuildThisFileDirectory){SERVER}" /></ItemGroup></Project>')
            with (root / 'Honua.sln').open('a') as solution:
                solution.write(f'\nProject("x") = "collector", "{partition.COLLECTOR}", "id"')
            output = root / 'output.slnf'
            partition.exclude_server(partition.BuildFilterRequest(root, 'full', None, output))
            self.assertEqual([OTHER], json.loads(output.read_text())['solution']['projects'])
            self.assertEqual([], partition.main_projects([partition.COLLECTOR]))
            self.assertEqual('none', partition.consumer_producer([partition.COLLECTOR]))
            (root / OTHER).write_text('<Project><Import Project="../../coverage.props" /></Project>')
            with self.assertRaises(ValueError):
                partition.exclude_server(partition.BuildFilterRequest(root, 'full', None, output))

    def test_real_solution_has_no_remaining_server_reverse_dependency(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'output.slnf'
            partition.exclude_server(partition.BuildFilterRequest(partition.ROOT, 'full', None, output))
            projects = json.loads(output.read_text())['solution']['projects']
            self.assertNotIn(SERVER, projects)
            self.assertNotIn(partition.COLLECTOR, projects)

    def test_reverse_dependency_rejects_duplicate_compile(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.make_root(root, f'<ItemGroup><ProjectReference Include="../../{SERVER}" /></ItemGroup>')
            with self.assertRaises(ValueError):
                partition.exclude_server(partition.BuildFilterRequest(root, 'full', None, root / 'output.slnf'))

    def test_local_only_projects_respects_duration_based_held_shard_routing(self):
        proj_a = 'tests/dotnet/Honua.A.Tests/Honua.A.Tests.csproj'
        proj_b = 'tests/dotnet/Honua.B.Tests/Honua.B.Tests.csproj'
        proj_c = 'tests/dotnet/Honua.C.Tests/Honua.C.Tests.csproj'
        proj_shared = 'tests/dotnet/Honua.Shared.Tests/Honua.Shared.Tests.csproj'
        config = {'pr_heavy_placement': {'heavy_slowdown': 2.0}, 'shards': [
            {'name': 'Server Shard', 'csproj': SERVER, 'dispatch_rank': 9.0},
            {'name': 'Held 0', 'csprojs': [proj_a, proj_shared], 'dispatch_rank': 5.0},
            {'name': 'Held 1', 'csprojs': [proj_b, proj_shared], 'dispatch_rank': 4.0},
            {'name': 'Held 2', 'csproj': proj_c, 'dispatch_rank': 2.0},
        ]}
        matrix_result = {'skip': False, 'shards': ['Server Shard', 'Held 0', 'Held 1', 'Held 2']}
        projects = [proj_a, proj_b, proj_c, proj_shared]
        req = partition.LocalOnlyRequest
        # Only Held 2 fits the longest hosted shard after heavy-pool slowdown.
        self.assertEqual([proj_c], partition.local_only_projects(req(
            projects=projects, matrix_result=matrix_result, config=config,
            heavy_ready=True, heavy_runner_configured=True, same_repo_pr=True)))
        for heavy_ready, heavy_runner_configured, same_repo_pr in (
                (False, True, True), (True, False, True), (True, True, False)):
            self.assertEqual([], partition.local_only_projects(req(
                projects=projects, matrix_result=matrix_result, config=config,
                heavy_ready=heavy_ready, heavy_runner_configured=heavy_runner_configured, same_repo_pr=same_repo_pr)))
        self.assertEqual([], partition.local_only_projects(req(
            projects=projects, matrix_result={'skip': True, 'shards': ['Held 0']}, config=config,
            heavy_ready=True, heavy_runner_configured=True, same_repo_pr=True)))


    def test_singleton_and_real_selection_match_emitted_heavy_placement(self):
        config = json.loads((partition.ROOT / '.github/ci-shards.json').read_text())
        names = ['OGC API Features 2']
        for selected_names in (names, [shard['name'] for shard in config['shards']]):
            result = {'skip': False, 'shards': selected_names}
            affected = partition.affected_shards
            entries = affected.place_heavy_shards(
                affected.matrix_include(result, config), affected.time_budget.load_heavy_slowdown(config))
            _, held = affected.partition_producer_matrices(entries)
            consumers = {}
            for entry in held:
                for project in partition.shard_projects(entry):
                    consumers.setdefault(project, []).append(entry['heavy'])
            eligible = sorted(project for project, placements in consumers.items()
                              if all(placements) and project not in partition.INDEPENDENT_ROOTS)
            actual = partition.local_only_projects(partition.LocalOnlyRequest(
                list(consumers), result, config, True, True, True))
            self.assertEqual(eligible, actual)
            if selected_names == names:
                self.assertEqual([], actual)


if __name__ == '__main__':
    unittest.main()
