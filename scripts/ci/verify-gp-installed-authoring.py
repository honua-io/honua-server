"""Exercise a packed GP devkit with no source checkout on the consuming runner."""
import argparse
import json
import os
import re
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tool')
    parser.add_argument('--package-version', type=Path)
    args = parser.parse_args()
    if args.package_version:
        packages = list(args.package_version.glob('Honua.Geoprocessing.Cli.*.nupkg'))
        if len(packages) != 1:
            raise RuntimeError('Expected exactly one packed devkit candidate')
        with zipfile.ZipFile(packages[0]) as package:
            specs = [name for name in package.namelist() if name.endswith('.nuspec')]
            if len(specs) != 1:
                raise RuntimeError('Expected exactly one package manifest')
            metadata = ET.fromstring(package.read(specs[0]))
            identity = metadata.find('.//{*}id')
            version = metadata.find('.//{*}version')
            if identity is None or identity.text != 'Honua.Geoprocessing.Cli' or version is None or not version.text:
                raise RuntimeError('Candidate package identity/version is missing or incorrect')
            print(version.text)
        return
    if not args.tool:
        parser.error('--tool is required for the installed journey')
    tool = str(Path(args.tool).resolve())
    env = dict(os.environ)
    for key in ('HONUA_SERVER_CHECKOUT', 'HONUA_GP_HARNESS_ROOT', 'HONUA_CUSTOMCODE_SDK_ASSEMBLY', 'PYTHONPATH'):
        env.pop(key, None)
    with tempfile.TemporaryDirectory(prefix='gp installed ') as temp:
        root = Path(temp)
        def run(*command, cwd=root, expected=0, extra_env=None):
            result = subprocess.run(command, cwd=cwd, env={**env, **(extra_env or {})}, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=300)
            print(result.stdout)
            if result.returncode != expected:
                # Preserve concrete dependency-resolution evidence on consumer-only runners.
                for artifact in sorted(root.rglob('*')):
                    if artifact.is_file() and (artifact.name.endswith(('.deps.json', '.runtimeconfig.json')) or artifact.name == 'Honua.CustomCode.Sdk.dll'):
                        print(f'DEPENDENCY {artifact.relative_to(root)} ({artifact.stat().st_size} bytes)')
                        if artifact.suffix == '.json':
                            print(artifact.read_text())
                raise RuntimeError(f'{command[0]} exited {result.returncode}, expected {expected}')
            return result.stdout
        for language in ('python', 'dotnet'):
            run(tool, 'init', '--language', language, '--name', f'summary-{language}', '--output', str(root))
            project = root / f'summary-{language}'
            assert (project / '.honua/toolchain').is_dir()
            assert not (project / 'Directory.Build.rsp').exists(), 'installed flow must not capture a checkout path'
            output = run(tool, 'test', '--project', str(project))
            assert 'PASS  summary.json' in output and 'summary : passed' in output
            expected = project / 'expected/summary.json'
            original = expected.read_bytes()
            broken = json.loads(original)
            broken['mean'] = 999
            expected.write_text(json.dumps(broken))
            assert 'FAIL  summary.json' in run(tool, 'test', '--project', str(project), expected=1)
            expected.write_bytes(original)
            run(tool, 'test', '--project', str(project))
            # Recreate the ignored payload exactly as a fresh source clone must do.
            shutil.rmtree(project / '.honua/toolchain')
            run(tool, 'test', '--project', str(project))
            if language == 'python':
                harness = project / '.honua/toolchain/docker/worker-customcode-python/harness'
                run(sys.executable, '-m', 'pip', 'install', '--no-deps', str(harness), cwd=project)
                assert '4 passed' in run(sys.executable, '-m', 'pytest', '-q', 'tests', cwd=project)
            else:
                direct = run('dotnet', 'test', 'tests/SummaryTool.Tests/SummaryTool.Tests.csproj', '--no-restore', cwd=project)
                assert re.search(r'Passed:\s+4', direct) and re.search(r'Failed:\s+0', direct) and re.search(r'Skipped:\s+0', direct)
            # The same untouched v1 descriptor reaches release validation. Local tests
            # deliberately cannot synthesize a passing worker-image validation receipt.
            run('git', 'init', cwd=project)
            run('git', 'add', '.', cwd=project)
            run('git', '-c', 'user.name=GP CI', '-c', 'user.email=ci@honua.io', 'commit', '-m', 'Fixture source', cwd=project)
            refusal = run(tool, 'release', '--output', str(root / f'release-{language}'),
                          '--source-repository', 'honua-io/installed-fixture',
                          '--runtime-image', 'ghcr.io/honua-io/fixture@sha256:' + 'a' * 64,
                          cwd=project, expected=1)
            assert 'receipt' in refusal.lower() and 'not valid honua.gp-process' not in refusal
        print('Installed Python and .NET init/test/negative golden/direct test/release refusal passed.')


if __name__ == '__main__':
    main()
