#!/usr/bin/env python3
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('catalog', Path(__file__).with_name('test-projects.py'))
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class CatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.owner = self.project('src/Honua.Protocols.Example')
        self.unrelated = self.project('src/Honua.Protocols.Other')
        self.unit = self.project('tests/dotnet/Honua.Protocols.Example.UnitTests', [self.owner])
        self.integration = self.project('tests/dotnet/Honua.Protocols.Example.Tests', [self.owner])
        (self.root / '.github').mkdir()
        self.entries = {'schema': 'honua.test-projects/v1', 'projects': {
            self.unit: dict(role='unit', runtimeOwners=[self.owner], resources=[], isolated=True, coverageRequired=True),
            self.integration: dict(role='mixed', runtimeOwners=[self.owner], resources=['docker'], isolated=False, coverageRequired=True)}}
        self.generate()

    def tearDown(self):
        self.temp.cleanup()

    def project(self, directory, references=(), extra=''):
        path = self.root / directory / (Path(directory).name + '.csproj')
        path.parent.mkdir(parents=True, exist_ok=True)
        import os
        refs = ''.join(f'<ProjectReference Include="{os.path.relpath(self.root / ref, path.parent)}" />' for ref in references)
        path.write_text(f'<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup>{refs}{extra}</ItemGroup></Project>')
        (path.parent / 'Example.cs').write_text('class Example {}')
        return path.relative_to(self.root).as_posix()

    def generate(self):
        for path, content in catalog.generated(self.entries).items():
            (self.root / path).write_text(content)

    def test_unit_plan_uses_forward_closure_not_integration_consumer(self):
        catalog.validate(self.root, self.entries)
        result = catalog.plan(self.root, self.entries, ['src/Honua.Protocols.Example/Example.cs'])
        self.assertEqual([self.unit], result['selectedProjects'])
        self.assertNotIn(self.integration, result['forwardBuildClosure'])
        self.assertFalse(result['validatesPullRequest'])
        self.assertEqual([self.unit], catalog.plan(self.root, self.entries, ['src/Honua.Protocols.Other/Example.cs'])['selectedProjects'])

    def test_linked_input_selects_consumer_without_source_owner_assembly(self):
        shared = self.root / 'tests/shared/Pure.cs'
        shared.parent.mkdir()
        shared.write_text('class Pure {}')
        unit = self.root / self.unit
        unit.write_text(unit.read_text().replace('</ItemGroup>', '<Compile Include="../../shared/Pure.cs" Link="Pure.cs" /></ItemGroup>'))
        result = catalog.plan(self.root, self.entries, ['tests/shared/Pure.cs'])
        self.assertEqual([self.unit], result['selectedProjects'])
        self.assertNotIn(self.integration, result['forwardBuildClosure'])
        shared.write_text('class Pure { PostgresFixture fixture; }')
        with self.assertRaisesRegex(ValueError, 'including links'):
            catalog.validate(self.root, self.entries)

    def test_transitive_server_dependency_is_rejected(self):
        server = self.project('src/Honua.Server')
        owner = self.root / self.owner
        owner.write_text(owner.read_text().replace('</ItemGroup>', f'<ProjectReference Include="../Honua.Server/Honua.Server.csproj" /></ItemGroup>'))
        with self.assertRaisesRegex(ValueError, 'forbidden runtime'):
            catalog.validate(self.root, self.entries)

    def test_unrelated_protocol_and_integration_package_are_rejected(self):
        unit = self.root / self.unit
        original = unit.read_text()
        unit.write_text(original.replace('</ItemGroup>', '<ProjectReference Include="../../../src/Honua.Protocols.Other/Honua.Protocols.Other.csproj" /></ItemGroup>'))
        with self.assertRaisesRegex(ValueError, 'forbidden runtime'):
            catalog.validate(self.root, self.entries)
        unit.write_text(original.replace('</ItemGroup>', '<PackageReference Include="Testcontainers.PostgreSql" /></ItemGroup>'))
        with self.assertRaisesRegex(ValueError, 'integration package'):
            catalog.validate(self.root, self.entries)

    def test_missing_catalog_and_stale_discovery_fail_closed(self):
        self.project('tests/dotnet/Honua.New.UnitTests')
        with self.assertRaisesRegex(ValueError, 'inventory differs'):
            catalog.validate(self.root, self.entries)
        new = 'tests/dotnet/Honua.New.UnitTests/Honua.New.UnitTests.csproj'
        self.entries['projects'][new] = dict(role='unit', runtimeOwners=[], resources=[], isolated=True, coverageRequired=True)
        with self.assertRaisesRegex(ValueError, 'stale generated'):
            catalog.validate(self.root, self.entries)

    def test_core_kit_purity_rejects_hosting_transitively(self):
        hosting = self.project('src/Honua.Hosting')
        kit = self.project('tests/dotnet/Honua.TestKit.Core', [hosting])
        self.entries['projects'][kit] = dict(role='support', runtimeOwners=[hosting], resources=[], isolated=False, coverageRequired=False)
        unit = self.root / self.unit
        unit.write_text(unit.read_text().replace('</ItemGroup>', '<ProjectReference Include="../Honua.TestKit.Core/Honua.TestKit.Core.csproj" /></ItemGroup>'))
        self.generate()
        with self.assertRaisesRegex(ValueError, 'impure transitive'):
            catalog.validate(self.root, self.entries)

    def test_misnamed_full_host_unit_project_is_not_auto_classified_unit(self):
        server = self.project('src/Honua.Server')
        wrong = self.project('tests/dotnet/Honua.Misleading.UnitTests', [server])
        self.assertEqual('mixed', catalog.proposed(self.root, wrong)['role'])
        self.assertFalse(catalog.proposed(self.root, wrong)['isolated'])

    def test_one_combined_build_then_tests_without_build_or_restore(self):
        second = self.project('tests/dotnet/Honua.Other.UnitTests', [self.owner])
        self.entries['projects'][second] = dict(role='unit', runtimeOwners=[self.owner], resources=[], isolated=True, coverageRequired=True)
        (self.root / 'Honua.sln').write_text('fixture solution')
        result = catalog.plan(self.root, self.entries, ['Directory.Build.props'])
        selections = []
        def run(command, **kwargs):
            if command[1] == 'build':
                selections.append(json.loads(Path(command[2]).read_text()))
            else:
                self.assertIn('--no-build', command)
                self.assertIn('--no-restore', command)
        with patch.object(catalog.subprocess, 'run', side_effect=run) as execution:
            catalog.execute(self.root, result)
        self.assertEqual(3, execution.call_count)
        self.assertEqual(sorted([self.unit, second]), selections[0]['solution']['projects'])
        self.assertNotIn(self.integration, selections[0]['solution']['projects'])

    def test_mocked_redis_name_does_not_require_resource(self):
        source = (self.root / self.unit).parent / 'Example.cs'
        source.write_text('class RedisCacheMockTests { Mock<IRedisService> mocked; }')
        self.assertEqual([], catalog.proposed(self.root, self.unit)['resources'])
        self.assertEqual('unit', catalog.proposed(self.root, self.unit)['role'])
        source.write_text('class RedisContainerTests { RedisFixture fixture; }')
        self.assertIn('redis', catalog.proposed(self.root, self.unit)['resources'])
        self.assertEqual('mixed', catalog.proposed(self.root, self.unit)['role'])

    def test_unknown_source_build_or_test_changes_select_all_units(self):
        for changed in ('src/New/Unknown.cs', 'tests/new-fixtures/Unknown.cs', 'eng/build.py', 'custom.props'):
            result = catalog.plan(self.root, self.entries, [changed])
            self.assertEqual([self.unit], result['selectedProjects'])
            self.assertIn('Unaccounted', result['selectionReasons'][0])

    def test_missing_literal_linked_source_fails_closed(self):
        unit = self.root / self.unit
        unit.write_text(unit.read_text().replace('</ItemGroup>', '<Compile Include="Missing.cs" /></ItemGroup>'))
        with self.assertRaisesRegex(ValueError, 'missing literal Compile'):
            catalog.validate(self.root, self.entries)

    def test_linked_source_change_selects_all_unit_consumers(self):
        shared = self.root / 'tests/shared/Pure.cs'
        shared.parent.mkdir()
        shared.write_text('class Pure {}')
        second = self.project('tests/dotnet/Honua.Second.UnitTests', [self.owner])
        self.entries['projects'][second] = dict(role='unit', runtimeOwners=[self.owner], resources=[], isolated=True, coverageRequired=True)
        for project in (self.unit, second):
            path = self.root / project
            path.write_text(path.read_text().replace('</ItemGroup>', '<Compile Include="../../shared/Pure.cs" /></ItemGroup>'))
        result = catalog.plan(self.root, self.entries, ['tests/shared/Pure.cs'])
        self.assertEqual(sorted([self.unit, second]), result['selectedProjects'])

    def test_inherited_build_reference_and_unknown_import_fail_closed(self):
        server = self.project('src/Honua.Server')
        (self.root / 'Directory.Build.props').write_text('<Project><ItemGroup><ProjectReference Include="$(MSBuildThisFileDirectory)src/Honua.Server/Honua.Server.csproj" /></ItemGroup></Project>')
        with self.assertRaisesRegex(ValueError, 'forbidden runtime'):
            catalog.validate(self.root, self.entries)
        (self.root / 'Directory.Build.props').write_text('<Project><Import Project="$(Unknown)/Custom.props" /></Project>')
        with self.assertRaisesRegex(ValueError, 'unresolved import'):
            catalog.validate(self.root, self.entries)

    def test_updater_preserves_reviewed_owners_and_exact_shard_artifacts(self):
        reviewed = json.loads(json.dumps(self.entries))
        (self.root / catalog.CATALOG).write_text(json.dumps(self.entries))
        (self.root / '.github/ci-shards.json').write_text(json.dumps({'shards': [{'csproj': self.unit}]}))
        registry = self.root / '.github/server-test-artifact-projects.json'
        registry.write_text(json.dumps({'contract_version': 1, 'projects': [
            {'csproj': self.unit, 'artifact_suffix': 'reviewed', 'proof_filter': 'FullyQualifiedName~SpecificCase'},
            {'csproj': self.integration, 'artifact_suffix': 'not-routed', 'proof_filter': 'FullyQualifiedName~Honua'}]}))
        with patch.object(catalog.sys, 'argv', ['test-projects.py', 'update', '--root', str(self.root)]):
            catalog.main()
        updated = json.loads((self.root / catalog.CATALOG).read_text())
        self.assertEqual(reviewed, updated)
        self.assertEqual([{'csproj': self.unit, 'artifact_suffix': 'reviewed', 'proof_filter': 'FullyQualifiedName~SpecificCase'}], json.loads(registry.read_text())['projects'])
        catalog.validate(self.root, updated)
        registry.write_text(json.dumps({'projects': []}))
        with self.assertRaisesRegex(ValueError, 'Stale shard artifact inventory'):
            catalog.validate(self.root, updated)

    def test_external_linked_content_selects_unit_consumer(self):
        schema = self.root / 'tests/schemas/contract.json'
        schema.parent.mkdir()
        schema.write_text('{}')
        unit = self.root / self.unit
        unit.write_text(unit.read_text().replace('</ItemGroup>', '<Content Include="../../schemas/*.json" /></ItemGroup>'))
        result = catalog.plan(self.root, self.entries, ['tests/schemas/contract.json'])
        self.assertEqual([self.unit], result['selectedProjects'])
        self.assertEqual([], result['selectionReasons'])

    def test_component_support_cannot_pull_executable_test_transitively(self):
        support = self.project('tests/dotnet/Honua.TestKit.Component', [self.unit])
        component = self.project('tests/dotnet/Honua.Example.ComponentTests', [support])
        for project, role in ((support, 'support'), (component, 'component')):
            self.entries['projects'][project] = dict(role=role, runtimeOwners=[self.owner], resources=[], isolated=False, coverageRequired=False)
        self.generate()
        with self.assertRaisesRegex(ValueError, 'executable test dependency'):
            catalog.validate(self.root, self.entries)

    def test_only_canonical_architecture_collector_can_reference_tests(self):
        architecture = self.project('tests/dotnet/Honua.Architecture.Tests', [self.unit, self.integration])
        self.entries['projects'][architecture] = dict(role='architecture', runtimeOwners=[self.owner], resources=[], isolated=False, coverageRequired=False)
        self.generate()
        catalog.validate(self.root, self.entries)
        self.entries['projects'][architecture]['role'] = 'component'
        with self.assertRaisesRegex(ValueError, 'executable test dependency'):
            catalog.validate(self.root, self.entries)

    def test_shared_change_selects_units_only(self):
        result = catalog.plan(self.root, self.entries, ['Directory.Build.props'])
        self.assertEqual([self.unit], result['selectedProjects'])
        self.assertEqual('.github/ci-shards.json', result['shardRoutingAuthority'])


if __name__ == '__main__':
    unittest.main()
