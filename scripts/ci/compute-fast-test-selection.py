#!/usr/bin/env python3
"""Select independent PR fast-test groups from the shared affected closure."""
import argparse
import fnmatch
from importlib import import_module
import json
import os
import posixpath
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
build_documents = import_module('test-projects').build_documents
GROUPS = ('architecture', 'unit-fast', 'server-fast', 'server-governance')


def select(closure, roots):
    if closure is None or 'ALL' in closure:
        return list(GROUPS)
    if not closure:
        return []
    # Architecture and Server governance inspect repository files beyond P2P edges.
    return [group for group in GROUPS
            if group in {'architecture', 'server-governance'} or set(closure).intersection(roots[group])]


def item_includes(tree):
    for node in tree.getroot().iter():
        if node.tag.rsplit('}', 1)[-1] in {'Compile', 'Content', 'EmbeddedResource', 'None'}:
            yield from node.attrib.get('Include', '').replace('\\', '/').split(';')


def normalize_include(include, imported_base, project_base):
    include = include.replace('$(MSBuildThisFileDirectory)', str(imported_base) + '/')
    if not include:
        return None
    if any(token in include for token in ('$(', '@(')):
        raise ValueError('Cannot resolve linked build input')
    # MSBuild item paths are relative to the project, even inside imported files.
    path = Path(posixpath.normpath(str(project_base / include)))
    if not path.is_relative_to(ROOT):
        raise ValueError('External linked build input')
    return path.relative_to(ROOT).as_posix()


def linked_patterns(project):
    project_base = (ROOT / project).parent
    for tree, imported_base in build_documents(ROOT, project):
        patterns = (normalize_include(include, imported_base, project_base)
                    for include in item_includes(tree))
        yield from (pattern for pattern in patterns if pattern is not None)


def linked_inputs(changed, members):
    """The shared P2P closure cannot represent linked or imported file inputs."""
    patterns = (pattern for project in members for pattern in linked_patterns(project))
    return any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns for path in changed)


def docs_proven(paths):
    verdict = subprocess.run(
        ['bash', 'scripts/ci/docs-only-diff.sh', '--paths'], input='\n'.join(paths) + '\n',
        cwd=ROOT, capture_output=True, text=True, check=False)
    return verdict.returncode == 0 and 'docs_only=true' in verdict.stdout.splitlines()


def safe_selection(closure, members):
    changed = subprocess.check_output(
        ['git', 'diff', '--no-renames', '--name-only', '-z', 'HEAD^1', 'HEAD'],
        cwd=ROOT, text=True).rstrip('\0').split('\0')
    if not closure:
        return [] if docs_proven(changed) else list(GROUPS)
    if 'ALL' in closure or linked_inputs(changed, members):
        return list(GROUPS)
    # Only ordinary managed source is represented by a project-directory/P2P
    # closure. Prove every other input independently, including mixed diffs.
    directories = [posixpath.dirname(project) + '/' for project in members]
    unmapped = [path for path in changed
                if not (path.endswith(('.cs', '.csproj'))
                        and any(path.startswith(directory) for directory in directories))]
    if unmapped and not docs_proven(unmapped):
        return list(GROUPS)
    return None


def plan():
    if os.environ.get('GITHUB_EVENT_NAME') != 'pull_request':
        return list(GROUPS)
    result = subprocess.run(
        ['bash', 'scripts/ci/compute-affected-projects.sh'], cwd=ROOT,
        capture_output=True, text=True, check=True,
        env=dict(os.environ, BASE_REF='HEAD^1', HEAD_REF='HEAD'))
    closure = result.stdout.splitlines()
    members = set(subprocess.check_output(
        ['git', 'ls-files', '*.csproj'], cwd=ROOT, text=True).splitlines())
    if any(project != 'ALL' and project not in members for project in closure):
        raise ValueError('Affected closure contains an unknown project')
    safe = safe_selection(closure, members)
    if safe is not None:
        return safe
    roots = {}
    for group in GROUPS:
        roots[group] = subprocess.check_output(
            ['bash', 'scripts/ci/compute-lean-gate-build-scope.sh', '--test-roots'],
            cwd=ROOT, env=dict(os.environ, GATE_TEST_GROUP=group), text=True).splitlines()
        if not roots[group] or not set(roots[group]) <= members:
            raise ValueError(f'Invalid test roots for {group}')
    return select(closure, roots)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--github-output', action='store_true')
    args = parser.parse_args()
    try:
        groups = plan()
    except (OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f'::warning::Fast-test selection unavailable; retaining every group: {error}')
        groups = list(GROUPS)
    print(json.dumps({'groups': groups}))
    if args.github_output:
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for group in GROUPS:
                output.write(f'{group}={str(group in groups).lower()}\n')


if __name__ == '__main__':
    main()
