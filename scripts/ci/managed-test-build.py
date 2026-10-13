#!/usr/bin/env python3
"""Shared managed-test producer/consumer selection. No dotnet in planning modes."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SERVER = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
ARCHITECTURE = 'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj'
SOURCE = 'tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj'
PROPERTIES = ['-p:TreatWarningsAsErrors=true', '-p:RunAnalyzers=true', '-p:UseSharedCompilation=false']


def family_projects(root, family, *, advisory=False):
    operation = 'projects' if advisory else 'build-projects'
    return subprocess.check_output(['bash', str(root / 'scripts/ci/run-foundation-family.sh'),
                                    operation, family], cwd=root, text=True).splitlines()


def gate_projects(root):
    spec = importlib.util.spec_from_file_location('managed_test_catalog', root / 'scripts/ci/test-projects.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    catalog = json.loads((root / '.github/test-projects.json').read_text())
    units = module.gate_fast_projects(catalog)
    return sorted(set(units + [SERVER, ARCHITECTURE, SOURCE,
        'tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj',
        'tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj']))


def inventory(root):
    shards = json.loads((root / '.github/ci-shards.json').read_text())['shards']
    projects = {s.get('csproj') or SERVER for s in shards}
    for family in ('core', 'server', 'protocols', 'providers'):
        projects.update(family_projects(root, family, advisory=True))
    projects.update(gate_projects(root))
    return sorted(projects)


def validate_projects(root, projects):
    if not isinstance(projects, list) or any(not isinstance(p, str) for p in projects):
        raise ValueError('Project selection must be a JSON array of paths')
    known = json.loads((root / '.github/test-projects.json').read_text())['projects']
    for project in projects:
        if project not in known or not (root / project).is_file():
            raise ValueError(f'Unknown test project: {project}')
    return sorted(set(projects))


def write_filter(root, projects, target):
    # Forward P2P references are traversed by restore and graphBuild.
    target.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'),
                                             'projects': sorted(set(projects))}}, indent=2) + '\n')


def fallback(root, projects, restored, *, execute=True):
    requested = validate_projects(root, projects)
    restored = validate_projects(root, restored)
    if not set(restored) <= set(requested):
        raise ValueError('Restore receipt contains unrequested projects')
    missing = sorted(set(requested) - set(restored))
    if not missing:
        print('All managed-test outputs verified; no restore or compilation needed.')
        return missing
    print('Scoped artifact fallback: ' + ', '.join(missing), flush=True)
    if execute:
        with tempfile.TemporaryDirectory(prefix='honua-managed-fallback-') as directory:
            selection = Path(directory) / 'selected.slnf'
            write_filter(root, missing, selection)
            subprocess.run(['bash', str(root / 'scripts/ci/dotnet-restore-retry.sh'), str(selection),
                            *PROPERTIES], cwd=root, check=True)
            subprocess.run(['dotnet', 'build', str(selection), '--no-restore', '-c', 'Release',
                            '-graphBuild', *PROPERTIES], cwd=root, check=True)
    return missing


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['inventory', 'projects', 'filter', 'fallback'])
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--lane', choices=['pr', 'ci', 'foundation', 'shard'], default='pr')
    parser.add_argument('--family', choices=['core', 'server', 'protocols', 'providers'])
    parser.add_argument('--project')
    parser.add_argument('--matrix-file', type=Path)
    parser.add_argument('--projects-file', type=Path)
    parser.add_argument('--restored-file', type=Path)
    parser.add_argument('--base-filter', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    if args.operation == 'inventory':
        result = inventory(root)
    elif args.operation == 'projects':
        if args.lane == 'foundation':
            if not args.family:
                parser.error('--family required')
            result = family_projects(root, args.family)
        elif args.lane == 'shard':
            if not args.project:
                parser.error('--project required')
            result = [args.project]
        else:
            result = gate_projects(root) if args.lane == 'pr' else []
            if args.lane == 'ci':
                for family in ('core', 'server', 'protocols', 'providers'):
                    result.extend(family_projects(root, family))
                result.append(SOURCE)
            if args.matrix_file:
                matrix = json.loads(args.matrix_file.read_text())
                if isinstance(matrix, dict):
                    matrix = matrix.get('matrix_include', matrix.get('include', []))
                if isinstance(matrix, str):
                    matrix = json.loads(matrix)
                if not isinstance(matrix, list):
                    raise ValueError('Invalid shard matrix')
                # Advisory-only compilation stays in its original consumer.
                result.extend(s.get('csproj') or SERVER for s in matrix if not s.get('advisory', False))
            result = validate_projects(root, result)
    else:
        if not args.projects_file:
            parser.error('--projects-file required')
        projects = validate_projects(root, json.loads(args.projects_file.read_text()))
        if args.operation == 'fallback':
            if not args.restored_file:
                parser.error('--restored-file required')
            result = fallback(root, projects, json.loads(args.restored_file.read_text()), execute=not args.dry_run)
        else:
            if not args.output:
                parser.error('--output required')
            if args.base_filter:
                base = json.loads(args.base_filter.read_text())['solution']['projects']
                projects.extend(p.replace('\\', '/') for p in base)
            write_filter(root, projects, args.output)
            return
    text = json.dumps(result, indent=2) + '\n'
    if args.output:
        args.output.write_text(text)
    else:
        print(text, end='')


if __name__ == '__main__':
    main()
