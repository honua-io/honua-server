#!/usr/bin/env python3
"""Shared managed-test producer/consumer selection. No dotnet in planning modes."""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
from pathlib import Path, PurePosixPath
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SERVER = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
ARCHITECTURE = 'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj'
SOURCE = 'tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj'
PROPERTIES = ['-p:TreatWarningsAsErrors=true', '-p:RunAnalyzers=true', '-p:UseSharedCompilation=false']


def family_projects(root, family, *, advisory=False):
    operation = 'projects' if advisory else 'build-projects'
    projects = subprocess.check_output(['bash', str(root / 'scripts/ci/run-foundation-family.sh'),
                                        operation, family], cwd=root, text=True).splitlines()
    if family == 'server':
        # Fresh projections need all three emitters before either architecture suite.
        projects.extend([SOURCE, 'tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj'])
    return sorted(set(projects))


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
    members = {path.replace('\\', '/'): path for path in re.findall(
        r'^Project\([^\n]+? = "[^"]+", "([^"]+\.csproj)"',
        (root / 'Honua.sln').read_text(), re.MULTILINE)}
    normalized = {path.replace('\\', '/') for path in projects}
    unknown = normalized - members.keys()
    if unknown:
        raise ValueError(f'Projects missing from Honua.sln: {sorted(unknown)}')
    target.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'),
                                             'projects': [members[p] for p in sorted(normalized)]}}, indent=2) + '\n')


def package_dependencies_ready(root, project):
    """A binary receipt does not prove that MSBuild's NuGet imports are available."""
    assets = root / Path(project).parent / 'obj/project.assets.json'
    try:
        data = json.loads(assets.read_text())
        libraries, folders = data['libraries'], data['packageFolders']
        if not isinstance(libraries, dict) or not isinstance(folders, dict):
            return False
        package_roots = [Path(folder) for folder in folders]
        if any(not folder.is_absolute() for folder in package_roots):
            return False
        for library in libraries.values():
            if not isinstance(library, dict) or library.get('type') not in {'package', 'project'}:
                return False
            if library['type'] != 'package':
                continue
            files = library.get('files')
            package_path = library.get('path')
            if not isinstance(package_path, str) or not isinstance(files, list) or not files:
                return False
            relative_paths = [package_path, *files]
            for value in relative_paths:
                if not isinstance(value, str):
                    return False
                normalized = value.replace('\\', '/')
                path = PurePosixPath(normalized)
                if not normalized or path.is_absolute() or ':' in normalized or any(part in {'.', '..', ''} for part in normalized.split('/')):
                    return False
            # A package must be complete in one declared package folder. Mixing
            # partial copies across folders cannot satisfy generated import paths.
            if not any(all((folder / package_path / file.replace('\\', '/')).is_file()
                           for file in files) for folder in package_roots):
                return False
        return True
    except (OSError, ValueError, KeyError, TypeError):
        return False


def fallback(root, projects, restored, *, execute=True):
    requested = validate_projects(root, projects)
    restored = validate_projects(root, restored)
    if not set(restored) <= set(requested):
        raise ValueError('Restore receipt contains unrequested projects')
    missing = sorted(set(requested) - set(restored))
    missing_dependencies = [project for project in restored if not package_dependencies_ready(root, project)]
    restore_projects = sorted(set(missing + missing_dependencies))
    if not restore_projects:
        print('All managed-test outputs and package dependencies verified; no restore or compilation needed.')
        return missing
    print('Scoped dependency restore: ' + ', '.join(restore_projects), flush=True)
    if missing:
        print('Scoped artifact compilation fallback: ' + ', '.join(missing), flush=True)
    if execute:
        with tempfile.TemporaryDirectory(prefix='honua-managed-fallback-') as directory:
            selection = Path(directory) / 'restore.slnf'
            write_filter(root, restore_projects, selection)
            subprocess.run(['bash', str(root / 'scripts/ci/dotnet-restore-retry.sh'), str(selection), '-p:Configuration=Release',
                            *PROPERTIES], cwd=root, check=True)
            if missing:
                build_selection = selection
                if missing != restore_projects:
                    build_selection = Path(directory) / 'build.slnf'
                    write_filter(root, missing, build_selection)
                subprocess.run(['dotnet', 'build', str(build_selection), '--no-restore', '-c', 'Release',
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
            result = validate_projects(root, [args.project])
        else:
            result = gate_projects(root) if args.lane == 'pr' else []
            if args.lane == 'ci':
                for family in ('core', 'server', 'protocols', 'providers'):
                    result.extend(family_projects(root, family))
                result.append(SOURCE)
            if args.matrix_file:
                matrix = json.loads(args.matrix_file.read_text())
                if isinstance(matrix, dict):
                    matrix = matrix.get('matrix_include', matrix.get('include', matrix.get('executionMatrix')))
                if isinstance(matrix, str):
                    matrix = json.loads(matrix)
                if not isinstance(matrix, list) or any(not isinstance(row, dict) for row in matrix):
                    raise ValueError('Invalid shard matrix')
                if any(not isinstance(row.get('advisory', False), bool) for row in matrix):
                    raise ValueError('Shard advisory must be a boolean')
                if any(not isinstance(row.get('shard_name'), str) or not row['shard_name'] for row in matrix):
                    raise ValueError('Shard name is required')
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
