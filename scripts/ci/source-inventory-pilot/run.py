#!/usr/bin/env python3
"""Observe source/reflection metadata parity; never build inspected test projects."""
import argparse
import datetime
import json
from pathlib import Path
import subprocess
import uuid

DEFAULT_PROJECTS = [
    'tests/dotnet/Honua.Protocols.OData.UnitTests/Honua.Protocols.OData.UnitTests.csproj',
    'tests/dotnet/Honua.Protocols.Scene.ComponentTests/Honua.Protocols.Scene.ComponentTests.csproj',
    'tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj',
]
PROPERTIES = ['Configuration', 'TargetFramework', 'AssemblyName', 'TargetPath', 'DefineConstants',
              'LangVersion', 'Nullable', 'AllowUnsafeBlocks', 'ImplicitUsings', 'PlatformTarget',
              'NETCoreSdkVersion', 'MSBuildVersion']


def evaluation_command(project):
    # ResolveReferences performs design-time reference evaluation. Explicitly
    # prevent project-reference builds and compiler execution. GenerateGlobalUsings
    # supplies actual implicit-usings Compile items; it does not compile or emit.
    return ['dotnet', 'msbuild', project, '/nologo', '/verbosity:quiet',
            '-target:ResolveReferences;GenerateGlobalUsings', '-getItem:Compile,ReferencePath',
            '-getProperty:' + ','.join(PROPERTIES), '-p:Configuration=Release',
            '-p:DesignTimeBuild=true', '-p:BuildProjectReferences=false',
            '-p:SkipCompilerExecution=true', '-p:ProvideCommandLineArgs=true',
            '-p:UseSharedCompilation=false']


def run(root, tool, projects, output):
    output.mkdir(parents=True, exist_ok=False)
    rows = []
    def execute(command, name, *, stdout_file=None):
        with (output / (name + '.stderr.log')).open('w') as error:
            with (stdout_file or output / (name + '.stdout.log')).open('w') as stream:
                return subprocess.run(command, cwd=root, stdout=stream, stderr=error, check=False).returncode
    for index, project in enumerate(projects):
        row = {'project': project, 'status': 'notQualified', 'phase': 'evaluation'}
        rows.append(row)
        prefix = str(index)
        evaluated = output / (prefix + '.evaluated.json')
        command = evaluation_command(project)
        (output / (prefix + '.command.json')).write_text(json.dumps(command, indent=2) + '\n')
        code = execute(command, prefix + '.evaluation', stdout_file=evaluated)
        if code:
            row['diagnostic'] = 'design-time-evaluation-failed:' + str(code)
            continue
        try:
            document = json.loads(evaluated.read_text())
            properties = document['Properties']
            target = Path(properties['TargetPath'])
            if not target.is_absolute():
                target = root / Path(project).parent / target
            if not target.is_file():
                row['diagnostic'] = 'already-built-reflection-output-required'
                continue
        except (ValueError, KeyError, TypeError):
            row['diagnostic'] = 'invalid-design-time-evaluation'
            continue
        source, reflection, comparison = (output / (prefix + suffix) for suffix in ('.source.json', '.reflection.json', '.parity.json'))
        for phase, command in [
            ('source', ['dotnet', str(tool), 'source', str(evaluated), str(root), project, str(source)]),
            ('reflection', ['dotnet', str(tool), 'reflect', str(source), str(target), '-', str(reflection)]),
            ('parity', ['dotnet', str(tool), 'compare', str(source), str(reflection), '-', str(comparison)]),
        ]:
            row['phase'] = phase
            code = execute(command, prefix + '.' + phase)
            if code and not (phase == 'parity' and code == 3):
                row['diagnostic'] = phase + '-failed:' + str(code)
                break
        else:
            parity = json.loads(comparison.read_text())
            row.update(status=parity['Status'], metadataMatch=parity['MetadataMatch'],
                       runtimeTraitSemanticsVerified=parity['RuntimeTraitSemanticsVerified'],
                       diagnostics=parity['Diagnostics'], comparison=str(comparison))
    result = {'schema': 'honua.ci.semantic-test-pilot/v1', 'feedbackOnly': True,
              'validatesPullRequest': False, 'collectorRemainsAuthoritative': True,
              'referenceMetadataPrerequisite': 'existing normal-validation outputs; build floor not removed',
              'status': 'exactParity' if rows and all(row['status'] == 'exactParity' for row in rows) else 'notQualified',
              'projects': rows}
    (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--tool', type=Path, required=True, help='already-built CI inventory tool DLL')
    parser.add_argument('--project', action='append')
    parser.add_argument('--output', type=Path, help='new uniquely owned evidence directory')
    args = parser.parse_args()
    root, tool = args.root.resolve(), args.tool.resolve()
    output = (args.output or root / 'tests/TestResults/source-inventory-pilot' /
              (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex)).resolve()
    if not tool.is_file():
        parser.error('--tool must name an already-built tool')
    result = run(root, tool, args.project or DEFAULT_PROJECTS, output)
    print(json.dumps(result, indent=2))
    return 0 if result['status'] == 'exactParity' else 3


if __name__ == '__main__':
    raise SystemExit(main())
