#!/usr/bin/env python3
"""Conservative source catalog and feedback-only unit selection; never a PR gate."""
import argparse
import glob
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
CATALOG = '.github/test-projects.json'
ROLES = {'unit', 'mixed', 'component', 'integration', 'architecture', 'support', 'host'}


def documents(root, path, seen=None):
    seen = set() if seen is None else seen
    path = path.resolve()
    if path in seen:
        return []
    if not path.is_file() or not path.is_relative_to(root):
        raise ValueError(f'Missing/external XML build input: {path}')
    seen.add(path)
    tree = ET.parse(path)
    result = [(tree, path.parent)]
    for imported in tree.findall('.//Import'):
        value = imported.get('Project', '').replace('$(MSBuildThisFileDirectory)', str(path.parent) + '/')
        if '$(' in value or '@(' in value:
            raise ValueError(f'{path}: unresolved import {value}')
        result.extend(documents(root, path.parent / value.replace('\\', '/'), seen))
    return result


def build_documents(root, project):
    path = root / project
    seen = set()
    parsed = documents(root, path, seen)
    for parent in [path.parent, *path.parent.parents]:
        if not parent.is_relative_to(root):
            continue
        for name in ('Directory.Build.props', 'Directory.Build.targets'):
            if (parent / name).is_file():
                parsed.extend(documents(root, parent / name, seen))
    return parsed


def items(root, project, kind):
    result = []
    elements = [(item, base) for tree, base in build_documents(root, project) for item in tree.findall('.//' + kind)]
    for item, base in elements:
        value = item.get('Include')
        if not value:
            continue
        value = value.replace('$(MSBuildThisFileDirectory)', str(base) + '/')
        if '$(' in value or '@(' in value or ';' in value:
            raise ValueError(f'{project}: unresolved {kind} input {value}')
        path = base / value.replace('\\', '/')
        if kind == 'ProjectReference':
            path = path.resolve()
            if not path.is_file() or not path.is_relative_to(root):
                raise ValueError(f'{project}: missing/external reference {value}')
            result.append(path.relative_to(root).as_posix())
        else:
            if not glob.has_magic(str(path)) and not path.is_file():
                raise ValueError(f'{project}: missing literal {kind} input {value}')
            result.extend(Path(p).resolve().relative_to(root).as_posix() for p in glob.glob(str(path), recursive=True) if Path(p).is_file())
    return result


def closure(root, project):
    seen = set()
    pending = [project]
    while pending:
        current = pending.pop()
        if current not in seen:
            seen.add(current)
            pending.extend(items(root, current, 'ProjectReference'))
    return seen


def discover(root):
    return sorted(p.relative_to(root).as_posix() for p in (root / 'tests/dotnet').rglob('*.csproj') if not {'bin', 'obj'} & set(p.parts))


def sources(root, project):
    directory = (root / project).parent
    return set(p.relative_to(root).as_posix() for p in directory.rglob('*.cs') if not {'bin', 'obj'} & set(p.parts)) | set(items(root, project, 'Compile'))


def proposed(root, project):
    name = Path(project).stem
    tree = ET.parse(root / project)
    test = any(e.text == 'true' for e in tree.findall('.//IsTestProject')) or any(e.get('Include') == 'Microsoft.NET.Test.Sdk' for e in tree.findall('.//PackageReference'))
    role = 'unit' if name.endswith('.UnitTests') else 'component' if name.endswith('.ComponentTests') else 'architecture' if name == 'Honua.Architecture.Tests' else 'mixed' if test else 'support' if 'TestKit' in name else 'host'
    refs = closure(root, project)
    owners = sorted(p for p in refs if p.startswith('src/') and 'Analyzers' not in p)
    text = '\n'.join((root / p).read_text(errors='replace') for p in sources(root, project))
    resources = []
    if 'Testcontainers' in text or 'PostgresFixture' in text:
        resources.append('docker')
    for token, resource in [('PostgresFixture', 'postgres'), ('RedisFixture', 'redis'), ('GdalCli', 'native-gdal')]:
        if token in text:
            resources.append(resource)
    if role == 'unit' and (resources or any('/Honua.Server/' in p or '/Honua.TestKit/' in p for p in refs) or any(token in text for token in ('WebApplicationFactory<', '[IntegrationTest', '[IntegrationTheory'))):
        role = 'mixed'
    return dict(role=role, shardRouted=name.endswith('.UnitTests') or name in {'Honua.Server.Tests', 'Honua.Ai.Tests', 'Honua.Geoprocessing.Cli.Tests'} or (name.startswith('Honua.Protocols.') and name.endswith('.Tests')), runtimeOwners=owners, resources=resources, isolated=role == 'unit',
                coverageRequired=test and role != 'architecture' and (role in {'unit', 'component'} or name.startswith(('Honua.Protocols.', 'Honua.Ai.')) or name in {'Honua.Server.Tests', 'Honua.ProviderSmoke.Tests', 'Honua.Worker.Gdal.Tests'}))


def generated(catalog):
    required = sorted(p for p, entry in catalog['projects'].items() if entry['coverageRequired'])
    props = '<Project>\n  <ItemGroup>\n' + ''.join(f'    <ProjectReference Include="$(MSBuildThisFileDirectory)../{p}" />\n' for p in required) + '  </ItemGroup>\n</Project>\n'
    assemblies = ''.join(Path(p).stem + '.dll\n' for p in required)
    return {'.github/test-coverage-projects.props': props, '.github/test-coverage-assemblies.txt': assemblies}


def validate(root, catalog):
    entries = catalog['projects']
    if set(entries) != set(discover(root)):
        raise ValueError('Catalog project inventory differs; run test-projects.py update and review new entries')
    for project, entry in entries.items():
        if entry['role'] not in ROLES or not isinstance(entry['isolated'], bool) or not isinstance(entry['coverageRequired'], bool):
            raise ValueError(f'{project}: invalid role or flags')
        refs = closure(root, project)
        # Only the canonical full-coverage collector may load executable test assemblies.
        collector = project == 'tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj' and entry['role'] == 'architecture'
        if not collector:
            for reference in sorted(refs - {project}):
                if reference in entries and entries[reference]['role'] not in {'support', 'host'}:
                    raise ValueError(f'{project}: executable test dependency {reference} (including transitive support dependencies)')
        if not set(entry['runtimeOwners']) <= refs:
            raise ValueError(f'{project}: runtime owner outside dependency closure')
        if entry['role'] == 'component':
            for reference in refs - {project}:
                if '/Honua.Server/' in reference or '/Honua.TestKit/' in reference:
                    raise ValueError(f'{project}: component references full host infrastructure {reference}')
                if reference.startswith('src/') and 'Honua.Protocols.' in reference and reference not in entry['runtimeOwners']:
                    raise ValueError(f'{project}: component references unreviewed protocol {reference}')
        if not entry['isolated']:
            continue
        if entry['role'] != 'unit' or entry['resources']:
            raise ValueError(f'{project}: isolated unit cannot require external resources')
        allowed = set(entry['runtimeOwners'])
        for reference in refs - {project}:
            if reference.startswith('tests/') and not any('/' + name + '/' in reference for name in ('Honua.TestKit.Abstractions', 'Honua.TestKit.Core')):
                raise ValueError(f'{project}: isolated unit references test/integration project {reference}')
            if reference.startswith('src/') and (('/Honua.Server/' in reference) or ('Honua.Protocols.' in reference and reference not in allowed)):
                raise ValueError(f'{project}: forbidden runtime dependency {reference}')
        for reference in refs:
            packages = [e.get('Include', '') for tree, _base in build_documents(root, reference) for e in tree.findall('.//PackageReference')]
            if any('Testcontainers' in package or package == 'Microsoft.AspNetCore.Mvc.Testing' for package in packages):
                raise ValueError(f'{project}: integration package in isolated unit closure {reference}')
        core_kit = next((p for p in refs if '/Honua.TestKit.Core/' in p), None)
        if core_kit:
            pure_names = {'Honua.TestKit.Core', 'Honua.TestKit.Abstractions', 'Honua.Core', 'Honua.Core.Abstractions', 'Honua.Geometry', 'Honua.Analyzers'}
            if any(Path(p).stem not in pure_names for p in closure(root, core_kit)):
                raise ValueError(f'{project}: TestKit.Core has impure transitive dependencies')
        text = '\n'.join((root / p).read_text(errors='replace') for dependency in refs if dependency == project or '/Honua.TestKit.Core/' in dependency for p in sources(root, dependency))
        if any(token in text for token in ('Testcontainers', 'PostgresFixture', 'WebApplicationFactory<', '[IntegrationTest', '[IntegrationTheory')):
            raise ValueError(f'{project}: integration infrastructure in isolated unit sources (including links)')
    shard_path = root / '.github/ci-shards.json'
    if shard_path.is_file():
        registry_path = root / '.github/server-test-artifact-projects.json'
        if not registry_path.is_file():
            raise ValueError('Missing shard artifact inventory; run update')
        routed = {entry.get('csproj') or 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj' for entry in json.loads(shard_path.read_text())['shards']}
        registered = {entry['csproj'] for entry in json.loads(registry_path.read_text())['projects']}
        if registered != routed:
            raise ValueError('Stale shard artifact inventory; run update')
    for path, content in generated(catalog).items():
        if not (root / path).is_file() or (root / path).read_text() != content:
            raise ValueError(f'{path}: stale generated coverage inventory; run update')


def plan(root, catalog, changed):
    selected = []
    broad = any(p.startswith(('.github/', 'scripts/ci/')) or Path(p).name in {'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config', 'Honua.sln'} for p in changed)
    reasons = []
    eligible_inputs = set()
    eligible_directories = []
    candidates = []
    for project, entry in catalog['projects'].items():
        if entry['role'] != 'unit' or not entry['isolated']:
            continue
        refs = closure(root, project)
        inputs = set().union(*(sources(root, p) | set(items(root, p, 'Content')) | set(items(root, p, 'EmbeddedResource')) for p in refs)) | refs
        directories = [(root / p).parent.relative_to(root).as_posix() + '/' for p in refs]
        eligible_inputs |= inputs
        eligible_directories.extend(directories)
        candidates.append((project, inputs, directories))
    unknown = [p for p in changed if (p.startswith(('src/', 'tests/', 'eng/')) or Path(p).suffix in {'.props', '.targets'}) and p not in eligible_inputs and not any(p.startswith(d) for d in eligible_directories)]
    if unknown:
        broad = True
        reasons.append('Unaccounted source/test/build inputs select all eligible units: ' + ', '.join(unknown))
    for project, inputs, directories in candidates:
        if broad or any(p in inputs or any(p.startswith(d) for d in directories) for p in changed):
            selected.append(project)
    dependencies = sorted(set().union(*(closure(root, p) for p in selected)) if selected else set())
    return {'schema': 'honua.unit-feedback/v1', 'feedbackOnly': True, 'validatesPullRequest': False,
            'selectedProjects': sorted(selected), 'forwardBuildClosure': dependencies,
            'selectionReasons': reasons,
            'commands': ([['dotnet', 'build', '<temporary-unit-selection.slnf>', '--configuration', 'Release', '-p:TreatWarningsAsErrors=true', '-p:UseSharedCompilation=false']] if selected else []) + [['dotnet', 'test', p, '--no-build', '--no-restore', '--configuration', 'Release'] for p in sorted(selected)],
            'fullGatesRequired': True, 'shardRoutingAuthority': '.github/ci-shards.json'}


def execute(root, result):
    if not result['selectedProjects']:
        return
    solution = root / 'Honua.sln'
    if not solution.is_file():
        raise ValueError('Combined feedback build requires Honua.sln')
    with tempfile.TemporaryDirectory(prefix='.honua-unit-feedback-', dir=root) as temporary:
        selection = Path(temporary) / 'units.slnf'
        selection.write_text(json.dumps({'solution': {'path': str(solution), 'projects': result['selectedProjects']}}))
        for planned in result['commands']:
            command = [str(selection) if arg == '<temporary-unit-selection.slnf>' else arg for arg in planned]
            subprocess.run(command, cwd=root, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['update', 'validate', 'plan', 'run', 'role'])
    parser.add_argument('--project')
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--base', default='origin/trunk')
    parser.add_argument('--changed', action='append')
    args = parser.parse_args()
    root = args.root.resolve()
    path = root / CATALOG
    catalog = json.loads(path.read_text()) if path.exists() else {'schema': 'honua.test-projects/v1', 'projects': {}}
    if args.operation == 'role':
        project = Path(args.project)
        if project.is_absolute():
            project = project.resolve().relative_to(root)
        print(catalog['projects'][project.as_posix()]['role'])
        return
    if args.operation == 'update':
        catalog['projects'] = {p: catalog['projects'].get(p) or proposed(root, p) for p in discover(root)}
        path.write_text(json.dumps(catalog, indent=2) + '\n')
        for relative, content in generated(catalog).items():
            (root / relative).write_text(content)
        registry_path = root / '.github/server-test-artifact-projects.json'
        if registry_path.is_file():
            registry = json.loads(registry_path.read_text())
            shard_file = root / '.github/ci-shards.json'
            routed = {entry.get('csproj') or 'tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj' for entry in json.loads(shard_file.read_text())['shards']}
            registry['projects'] = [entry for entry in registry['projects'] if entry['csproj'] in routed]
            known = {entry['csproj'] for entry in registry['projects']}
            for project in sorted(routed - known):
                registry['projects'].append({'csproj': project, 'artifact_suffix': Path(project).stem.lower().replace('.', '-'), 'proof_filter': 'FullyQualifiedName~Honua'})
            registry_path.write_text(json.dumps(registry, indent=2) + '\n')
        print('Updated inventory; review roles, runtimeOwners, resources and isolation before commit.')
        return
    validate(root, catalog)
    if args.operation == 'validate':
        return
    changed = args.changed
    if changed is None:
        changed = subprocess.check_output(['git', 'diff', '--name-only', args.base], cwd=root, text=True).splitlines()
        changed += subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=root, text=True).splitlines()
    result = plan(root, catalog, changed)
    print(json.dumps(result, indent=2), flush=True)
    if args.operation == 'run':
        execute(root, result)
        print('Unit feedback complete. Full PR gates remain required.')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, ET.ParseError, subprocess.CalledProcessError, KeyError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
