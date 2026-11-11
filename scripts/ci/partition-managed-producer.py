#!/usr/bin/env python3
"""Split Server.Tests from the PR producer without dropping compile coverage."""
import argparse
import json
from importlib import import_module
from dataclasses import dataclass
from pathlib import Path

from solution_projects import solution_projects

project_closure = import_module('test-projects').closure
COLLECTOR = 'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj'
SERVER = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
INDEPENDENT_ROOTS = {COLLECTOR, SERVER}
ROOT = Path(__file__).resolve().parents[2]


def checked_projects(projects):
    if not isinstance(projects, list) or any(not isinstance(p, str) for p in projects):
        raise ValueError('Expected a project array')
    return set(projects)


def server_projects(projects):
    return sorted(checked_projects(projects) & {SERVER})


def main_projects(projects):
    return sorted(checked_projects(projects) - INDEPENDENT_ROOTS)


def consumer_producer(projects):
    selected = checked_projects(projects)
    if COLLECTOR in selected:
        return 'none'
    if selected == {SERVER}:
        return 'server'
    # A mixed shard must rebuild locally rather than combine potentially
    # incompatible SDK outputs from the two independently resolved producers.
    return 'none' if SERVER in selected else 'main'


@dataclass(frozen=True)
class BuildFilterRequest:
    root: Path
    mode: str
    source: Path | None
    output: Path


def exclude_server(request):
    root, mode, source, output = request.root, request.mode, request.source, request.output
    members = set(solution_projects(root))
    if SERVER not in members:
        raise ValueError('Server test project is missing from solution')
    for project in members - INDEPENDENT_ROOTS:
        if SERVER in project_closure(root, project):
            raise ValueError(f'{project} references the parallel server test producer')
    if mode == 'full':
        projects = members
    elif mode in {'affected', 'test-roots'}:
        projects = {p.replace('\\', '/') for p in json.loads(source.read_text())['solution']['projects']}
        if not projects <= members:
            raise ValueError('Unknown project in build filter')
    else:
        raise ValueError(f'Unsupported producer build mode: {mode}')
    if not projects - INDEPENDENT_ROOTS:
        raise ValueError('Main producer filter cannot be empty')
    output.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'),
                                              'projects': sorted(projects - INDEPENDENT_ROOTS)}}, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['server-projects', 'main-projects', 'consumer', 'filter'])
    parser.add_argument('--projects-file', type=Path)
    parser.add_argument('--mode')
    parser.add_argument('--filter', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.operation == 'consumer':
        if args.projects_file is None:
            parser.error('consumer requires --projects-file')
        producer = consumer_producer(json.loads(args.projects_file.read_text()))
        with args.output.open('a') as output:
            output.write(f'producer={producer}\n')
    elif args.operation in {'server-projects', 'main-projects'}:
        if args.projects_file is None:
            parser.error('project partition requires --projects-file')
        select = server_projects if args.operation == 'server-projects' else main_projects
        args.output.write_text(json.dumps(select(json.loads(args.projects_file.read_text()))) + '\n')
    else:
        if args.mode is None or (args.mode != 'full' and args.filter is None):
            parser.error('filter requires --mode and a --filter for scoped modes')
        exclude_server(BuildFilterRequest(ROOT, args.mode, args.filter, args.output))


if __name__ == '__main__':
    main()
