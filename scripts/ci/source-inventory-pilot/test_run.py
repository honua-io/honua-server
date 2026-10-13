#!/usr/bin/env python3
"""Offline pilot orchestration fixtures; no SDK or inspected compilation."""
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import xml.etree.ElementTree as ET
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('pilot', Path(__file__).with_name('run.py'))
pilot = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pilot)


class DriverTests(unittest.TestCase):
    def test_evaluation_explicitly_disables_inspected_build_and_compiler(self):
        command = pilot.evaluation_command('example.csproj')
        self.assertIn('-p:BuildProjectReferences=false', command)
        self.assertIn('-p:SkipCompilerExecution=true', command)
        self.assertIn('-p:DesignTimeBuild=true', command)
        self.assertIn('-getItem:Compile,ReferencePath', command)
        self.assertIn('-target:ResolveReferences;GenerateGlobalUsings', command)
        self.assertNotIn('build', command)

    def test_missing_existing_output_is_not_qualified_and_never_builds(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            commands = []
            def execute(command, **kwargs):
                commands.append(command)
                kwargs['stdout'].write(json.dumps({'Properties': {'TargetPath': str(root / 'absent.dll')}}))
                return type('Result', (), {'returncode': 0})()
            with patch.object(pilot.subprocess, 'run', side_effect=execute):
                result = pilot.run(root, root / 'tool.dll', ['example.csproj'], root / 'evidence')
            self.assertEqual('notQualified', result['status'])
            self.assertEqual('already-built-reflection-output-required', result['projects'][0]['diagnostic'])
            self.assertEqual(1, len(commands))
            self.assertFalse(result['validatesPullRequest'])

    def test_unsupported_metadata_agreement_does_not_turn_green(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / 'existing.dll'
            binary.write_text('existing-output')
            def execute(command, **kwargs):
                if command[1] == 'msbuild':
                    kwargs['stdout'].write(json.dumps({'Properties': {'TargetPath': str(binary)}}))
                if 'compare' in command:
                    Path(command[-1]).write_text(json.dumps({'Status': 'notQualified', 'MetadataMatch': True,
                        'RuntimeTraitSemanticsVerified': False, 'Diagnostics': ['unsupported-executable-trait-discoverer']}))
                return type('Result', (), {'returncode': 3 if 'compare' in command else 0})()
            with patch.object(pilot.subprocess, 'run', side_effect=execute):
                result = pilot.run(root, root / 'tool.dll', ['example.csproj'], root / 'evidence')
            self.assertEqual('notQualified', result['status'])
            self.assertTrue(result['projects'][0]['metadataMatch'])
            self.assertFalse(result['projects'][0]['runtimeTraitSemanticsVerified'])
            self.assertTrue((root / 'evidence/result.json').is_file())

    def test_metadata_agreement_still_has_no_public_qualification(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            binary = root / 'existing.dll'
            binary.write_text('existing-output')
            def execute(command, **kwargs):
                if command[1] == 'msbuild':
                    kwargs['stdout'].write(json.dumps({'Properties': {'TargetPath': str(binary)}}))
                if 'compare' in command:
                    Path(command[-1]).write_text(json.dumps({'Status': 'metadataParity', 'MetadataMatch': True,
                        'RuntimeTraitSemanticsVerified': True, 'Diagnostics': []}))
                return type('Result', (), {'returncode': 3 if 'compare' in command else 0})()
            with patch.object(pilot.subprocess, 'run', side_effect=execute):
                result = pilot.run(root, root / 'tool.dll', ['example.csproj'], root / 'evidence')
            self.assertEqual('metadataParity', result['status'])
            self.assertFalse(result['qualified'])
            self.assertFalse(result['binarySourceBindingVerified'])
            self.assertFalse(result['projects'][0]['qualified'])
            self.assertFalse(result['projects'][0]['binarySourceBindingVerified'])

    def test_hosted_pilot_runs_with_normal_producer_outputs_and_retains_reports(self):
        root = Path(__file__).resolve().parents[3]
        workflow = (root / '.github/workflows/ci.yml').read_text()
        producer = workflow.split('\n  build:\n', 1)[1].split('\n  format:\n', 1)[0]
        consumer = workflow.split('\n  dotnet-foundation-family-tests:\n', 1)[1].split('\n  dotnet-foundation-tests:\n', 1)[0]
        ordered = ['- name: Build (warnings as errors)', '- name: Publish managed test outputs',
                   '- name: Build source inventory observation tool', '- name: Verify source inventory fixture semantics',
                   '- name: Observe source inventory metadata parity', '- name: Upload producer source inventory observations']
        positions = [producer.index(step) for step in ordered]
        self.assertEqual(sorted(positions), positions)
        self.assertNotIn('source inventory', consumer)
        observation = producer.split('- name: Observe source inventory metadata parity', 1)[1].split('- name:', 1)[0]
        self.assertIn('continue-on-error: true', observation)
        fixtures = producer.split('- name: Verify source inventory fixture semantics', 1)[1].split('- name:', 1)[0]
        self.assertNotIn('continue-on-error', fixtures)
        self.assertIn('if: always()', producer.split('- name: Upload producer source inventory observations', 1)[1])
        self.assertIn('path: tests/TestResults/source-inventory-pilot/', producer)
        self.assertIn('scripts/ci/run-foundation-family.sh run', consumer)
        self.assertIn('--lane ci', producer)
        family = subprocess.check_output(['bash', 'scripts/ci/run-foundation-family.sh', 'build-projects', 'server'], cwd=root, text=True).splitlines()
        self.assertIn('tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj', family)
        collector = ET.parse(root / 'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj')
        self.assertTrue(any('test-coverage-projects.props' in item.get('Project', '') for item in collector.findall('.//Import')))
        coverage = ET.parse(root / '.github/test-coverage-projects.props')
        references = {item.get('Include', '').replace('$(MSBuildThisFileDirectory)../', '') for item in coverage.findall('.//ProjectReference')}
        self.assertTrue(set(pilot.DEFAULT_PROJECTS) <= references)

    def test_invalid_evaluation_fails_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def execute(command, **kwargs):
                kwargs['stdout'].write('{}')
                return type('Result', (), {'returncode': 0})()
            with patch.object(pilot.subprocess, 'run', side_effect=execute):
                result = pilot.run(root, root / 'tool.dll', ['example.csproj'], root / 'evidence')
            self.assertEqual('notQualified', result['status'])
            self.assertEqual('invalid-design-time-evaluation', result['projects'][0]['diagnostic'])


if __name__ == '__main__':
    unittest.main()
