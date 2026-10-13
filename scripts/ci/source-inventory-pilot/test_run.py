#!/usr/bin/env python3
"""Offline pilot orchestration fixtures; no SDK or inspected compilation."""
import importlib.util
import json
from pathlib import Path
import tempfile
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
