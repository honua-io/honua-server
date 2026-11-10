#!/usr/bin/env python3
"""Exercise trunk overlap against divergent histories and dependency changes."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]


class ProjectOverlapTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        scripts = self.root / 'scripts/ci'
        scripts.mkdir(parents=True)
        for name in ['compute-affected-projects.sh', 'compute-project-overlap.py', 'lib/python-resolve.sh']:
            source = ROOT / 'scripts/ci' / name
            if source.exists():
                target = scripts / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy(source, target)
        self.git('init', '-q', '-b', 'trunk')
        self.write('src/Library/Library.csproj', '<Project />')
        self.write('src/App/App.csproj', '<Project><ProjectReference Include="../Library/Library.csproj" /></Project>')
        self.write('src/Other/Other.csproj', '<Project />')
        for project in ['Library', 'App', 'Other']:
            self.write(f'src/{project}/Code.cs', 'baseline')
        self.commit()
        self.git('branch', 'lane')

    def git(self, *arguments):
        return subprocess.check_output(
            ['git', '-C', str(self.root), '-c', 'user.name=ci', '-c', 'user.email=ci@example.invalid',
             *arguments], text=True).strip()

    def write(self, path, contents):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(contents)

    def commit(self):
        self.git('add', '.')
        self.git('commit', '-qm', 'fixture')

    def change(self, branch, path):
        self.git('switch', '-q', branch)
        self.write(path, 'changed')
        self.commit()

    def overlap(self, *refs, env=None):
        result = subprocess.run(
            ['bash', 'scripts/ci/compute-affected-projects.sh', '--trunk-overlap', *refs],
            cwd=self.root, capture_output=True, text=True, check=True, env=env)
        return json.loads(result.stdout)

    def test_unrelated_project_does_not_require_refresh(self):
        self.change('lane', 'src/Other/Code.cs')
        self.change('trunk', 'src/Library/Code.cs')
        self.assertFalse(self.overlap('lane', 'trunk')['refresh_required'])

    def test_reverse_dependencies_require_refresh_without_same_file(self):
        self.change('lane', 'src/App/Code.cs')
        self.change('trunk', 'src/Library/Code.cs')
        result = self.overlap('lane', 'trunk')
        self.assertTrue(result['refresh_required'])
        self.assertIn('src/App/App.csproj', result['overlap'])

    def test_already_merged_trunk_does_not_require_refresh(self):
        self.change('trunk', 'src/Library/Code.cs')
        self.git('switch', '-q', 'lane')
        self.git('merge', '-q', 'trunk')
        self.assertFalse(self.overlap('lane', 'trunk')['refresh_required'])

    def test_infrastructure_and_changed_graph_fail_closed(self):
        self.change('lane', 'src/App/Code.cs')
        for path in ['Directory.Build.props', 'src/Other/Other.csproj']:
            with self.subTest(path=path):
                self.change('trunk', path)
                self.assertTrue(self.overlap('lane', 'trunk')['refresh_required'])

    def test_nonmanaged_folders_and_root_files(self):
        self.change('lane', 'docs/guide.md')
        self.change('trunk', 'docs/other.md')
        self.assertTrue(self.overlap('lane', 'trunk')['refresh_required'])
        self.change('trunk', 'README.md')
        self.assertTrue(self.overlap('lane', 'trunk')['refresh_required'])

    def test_unknown_refs_fail_closed(self):
        result = self.overlap('missing', 'trunk')
        self.assertTrue(result['refresh_required'])
        self.assertEqual('unavailable-history-or-selector', result['reason'])

    def test_dirty_dependency_graph_cannot_hide_overlap(self):
        self.change('lane', 'src/App/Code.cs')
        self.change('trunk', 'src/Library/Code.cs')
        self.write('src/App/App.csproj', '<Project />')
        self.assertTrue(self.overlap('lane', 'trunk')['refresh_required'])

    def test_untracked_project_cannot_shadow_dependency_mapping(self):
        self.change('lane', 'src/App/Code.cs')
        self.change('trunk', 'src/Library/Code.cs')
        self.write('src/App/Nested.csproj', '<Project />')
        self.assertTrue(self.overlap('lane', 'trunk')['refresh_required'])

    def test_broken_python3_alias_uses_working_python(self):
        binaries = self.root / 'bin'
        binaries.mkdir()
        stub = binaries / 'python3'
        stub.write_text('#!/bin/sh\nexit 127\n')
        stub.chmod(0o755)
        (binaries / 'python').symlink_to(sys.executable)
        environment = dict(os.environ, PATH=f"{binaries}{os.pathsep}{os.environ['PATH']}")
        result = self.overlap('lane', 'trunk', env=environment)
        self.assertFalse(result['refresh_required'])

    def test_empty_lane_does_not_require_refresh(self):
        self.change('trunk', 'src/Library/Code.cs')
        self.assertFalse(self.overlap('lane', 'trunk')['refresh_required'])


if __name__ == '__main__':
    unittest.main()
