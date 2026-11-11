#!/usr/bin/env python3
"""Split Server.Tests from the PR producer without dropping compile coverage."""
import argparse
import json
from dataclasses import dataclass
from pathlib import Path
import xml.etree.ElementTree as ET

from solution_projects import solution_projects

SERVER = 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj'
ROOT = Path(__file__).resolve().parents[2]


def checked_projects(projects):
    if not isinstance(projects, list) or any(not isinstance(p, str) for p in projects):
        raise ValueError('Expected a project array')
    return set(projects)


def server_projects(projects):
    return sorted(checked_projects(projects) & {SERVER})


def main_projects(projects):
    return sorted(checked_projects(projects) - {SERVER})


def consumer_producer(projects):
    selected = checked_projects(projects)
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


def project_references(root, project):
    for node in ET.parse(root / project).getroot().iter():
        if node.tag.rsplit('}', 1)[-1] != 'ProjectReference':
            continue
        include = node.attrib.get('Include', '').replace('\\', '/')
        if not include or any(token in include for token in ('$(', '@(', ';', '*', '?')):
            raise ValueError(f'Cannot prove project reference in {project}: {include}')
        yield ((root / project).parent / include).resolve()


def exclude_server(request):
    root, mode, source, output = request.root, request.mode, request.source, request.output
    members = set(solution_projects(root))
    if SERVER not in members:
        raise ValueError('Server test project is missing from solution')
    for project in members - {SERVER}:
        if (root / SERVER).resolve() in project_references(root, project):
            raise ValueError(f'{project} references the parallel server test producer')
    if mode == 'full':
        projects = members
    elif mode in {'affected', 'test-roots'}:
        projects = {p.replace('\\', '/') for p in json.loads(source.read_text())['solution']['projects']}
        if not projects <= members:
            raise ValueError('Unknown project in build filter')
    else:
        raise ValueError(f'Unsupported producer build mode: {mode}')
    if not projects - {SERVER}:
        raise ValueError('Main producer filter cannot be empty')
    output.write_text(json.dumps({'solution': {'path': str(root / 'Honua.sln'),
                                              'projects': sorted(projects - {SERVER})}}, indent=2) + '\n')


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
