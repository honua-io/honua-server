#!/usr/bin/env python3
"""Select independent PR fast-test groups from the shared affected closure."""
import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[2]
GROUPS = ('architecture', 'unit-fast', 'server-fast', 'server-governance')


def select(closure, roots):
    if closure is None or 'ALL' in closure:
        return list(GROUPS)
    if not closure:
        return []
    # Architecture checks inspect repository files beyond ProjectReference edges.
    return [group for group in GROUPS
            if group == 'architecture' or set(closure).intersection(roots[group])]


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
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(f'::warning::Fast-test selection unavailable; retaining every group: {error}')
        groups = list(GROUPS)
    print(json.dumps({'groups': groups}))
    if args.github_output:
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            for group in GROUPS:
                output.write(f'{group}={str(group in groups).lower()}\n')


if __name__ == '__main__':
    main()
