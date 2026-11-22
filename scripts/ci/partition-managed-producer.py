#!/usr/bin/env python3
"""Split Server.Tests from the PR producer without dropping compile coverage."""
import argparse
import json
from importlib import import_module
from dataclasses import dataclass
from pathlib import Path

from shard_projects import shard_projects
from solution_projects import solution_projects

affected_shards = import_module('compute-affected-shards')
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


@dataclass(frozen=True)
class LocalOnlyRequest:
    projects: list[str]
    matrix_result: dict
    config: dict
    heavy_ready: bool
    heavy_runner_configured: bool
    same_repo_pr: bool


def local_only_projects(request: LocalOnlyRequest):
    """Return main-producer projects whose affected-shard-held consumers all run on the build host."""
    selected = checked_projects(request.projects) - INDEPENDENT_ROOTS
    if not (request.heavy_ready and request.heavy_runner_configured and request.same_repo_pr):
        return []
    if not isinstance(request.matrix_result, dict) or request.matrix_result.get('skip'):
        return []
    include = affected_shards.matrix_include(request.matrix_result, request.config)
    placed = affected_shards.place_heavy_shards(
        include, affected_shards.time_budget.load_heavy_slowdown(request.config))
    _, held = affected_shards.partition_producer_matrices(placed)
    consumers_by_project: dict[str, list[bool]] = {}
    for entry in held:
        for project in shard_projects(entry):
            consumers_by_project.setdefault(project, []).append(entry['heavy'])
    return sorted(
        project
        for project in selected
        if consumers_by_project.get(project)
        and all(consumers_by_project[project])
    )


def _parse_bool(value: str | None) -> bool:
    return (value or '').strip().lower() == 'true'


def _write_local_only(args, parser):
    if args.projects_file is None or args.matrix_file is None:
        parser.error('local-only-projects requires --projects-file and --matrix-file')
    result = local_only_projects(
        LocalOnlyRequest(
            projects=json.loads(args.projects_file.read_text()),
            matrix_result=json.loads(args.matrix_file.read_text()),
            config=json.loads(args.shards_config.read_text()),
            heavy_ready=_parse_bool(args.heavy_ready),
            heavy_runner_configured=_parse_bool(args.heavy_runner_configured),
            same_repo_pr=_parse_bool(args.same_repo_pr),
        )
    )
    args.output.write_text(json.dumps(result) + '\n')


def _build_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        'operation',
        choices=['server-projects', 'main-projects', 'consumer', 'filter', 'local-only-projects'],
    )
    parser.add_argument('--projects-file', type=Path)
    parser.add_argument('--matrix-file', type=Path)
    parser.add_argument('--shards-config', type=Path, default=ROOT / '.github/ci-shards.json')
    parser.add_argument('--heavy-ready', default='false')
    parser.add_argument('--heavy-runner-configured', default='false')
    parser.add_argument('--same-repo-pr', default='false')
    parser.add_argument('--mode')
    parser.add_argument('--filter', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    return parser


def main():
    parser = _build_parser()
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
    elif args.operation == 'local-only-projects':
        _write_local_only(args, parser)
    else:
        if args.mode is None or (args.mode != 'full' and args.filter is None):
            parser.error('filter requires --mode and a --filter for scoped modes')
        exclude_server(BuildFilterRequest(ROOT, args.mode, args.filter, args.output))


if __name__ == '__main__':
    main()
