#!/usr/bin/env python3
"""Compare committed lane and trunk dependency closures from their merge base."""

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parents[2]
SELECTOR = ROOT / 'scripts/ci/compute-affected-projects.sh'
ALL = '*'


def git(*arguments):
    return subprocess.check_output(['git', *arguments], cwd=ROOT, stderr=subprocess.PIPE,
                                   text=True).rstrip('\n')


def changed_files(base, head):
    return git('diff', '--no-renames', '--name-only', '-z', base, head).split('\0')[:-1]


def project_scope(files, base):
    if any('\n' in path or '\r' in path or path.endswith('.csproj') for path in files):
        return {ALL}
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8') as manifest:
        manifest.write('\n'.join(files) + '\n')
        manifest.flush()
        env = {key: value for key, value in os.environ.items() if key != 'FORCE_FULL_BUILD_PATHS'}
        env.update(BASE_REF=base, HEAD_REF=base, HONUA_CHANGED_FILES_FILE=manifest.name)
        result = subprocess.check_output(['bash', str(SELECTOR)], cwd=ROOT, env=env,
                                         text=True, stderr=subprocess.PIPE)
    projects = set(result.splitlines())
    if 'ALL' in projects:
        return {ALL}
    directories = [str(PurePosixPath(project).parent) + '/' for project in projects]
    for path in files:
        if not any(path.startswith(directory) for directory in directories):
            projects.add('tree:' + path.split('/')[0] if '/' in path else ALL)
    return projects


def intersect_scopes(lane, trunk):
    if ALL in lane or ALL in trunk:
        return [ALL]
    overlap = lane & trunk
    for scope in lane | trunk:
        if scope.startswith('tree:'):
            prefix = scope.removeprefix('tree:') + '/'
            others = trunk if scope in lane else lane
            overlap.update(project for project in others if project.startswith(prefix))
    return sorted(overlap)


def compare(lane_ref, trunk_ref):
    lane = git('rev-parse', '--verify', '--end-of-options', lane_ref + '^{commit}')
    trunk = git('rev-parse', '--verify', '--end-of-options', trunk_ref + '^{commit}')
    base = git('merge-base', lane, trunk)
    lane_files = changed_files(base, lane)
    trunk_files = changed_files(base, trunk)
    overlap = []
    if lane_files and trunk_files:
        graph_changes = git('diff', '--name-only', base, '--', '*.csproj')
        untracked_projects = git('ls-files', '--others', '--exclude-standard', '--', '*.csproj')
        if graph_changes or untracked_projects:
            overlap = [ALL]
        else:
            overlap = intersect_scopes(project_scope(lane_files, base), project_scope(trunk_files, base))
    return {'refresh_required': bool(overlap), 'reason': 'overlapping-inputs' if overlap else 'disjoint-inputs',
            'base': base, 'lane_head': lane, 'trunk_head': trunk, 'overlap': overlap}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('lane_ref', nargs='?', default='HEAD')
    parser.add_argument('trunk_ref', nargs='?', default='origin/trunk')
    arguments = parser.parse_args()
    try:
        result = compare(arguments.lane_ref, arguments.trunk_ref)
    except (subprocess.CalledProcessError, OSError, UnicodeError):
        result = {'refresh_required': True, 'reason': 'unavailable-history-or-selector', 'overlap': [ALL]}
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
