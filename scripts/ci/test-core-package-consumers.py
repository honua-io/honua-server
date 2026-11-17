#!/usr/bin/env python3
"""Exercise package consumers without relying on installed SDK templates."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def consumer_script():
    workflow = (ROOT / '.github/workflows/ci.yml').read_text()
    step = workflow.split('      - name: Test package integration\n', 1)[1]
    body = step.split('        run: |\n', 1)[1]
    lines = []
    for line in body.splitlines():
        if line.strip() and not line.startswith('          '):
            break
        lines.append(line[10:])
    script = '\n'.join(lines)
    for expression in ('github.repository_owner', 'github.actor', 'secrets.GITHUB_TOKEN'):
        script = script.replace('${{ ' + expression + ' }}', 'test-value')
    return script


class PackageConsumers(unittest.TestCase):
    def test_package_consumer_builds_against_the_pinned_framework(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'packages').mkdir()
            helper = root / 'scripts' / 'ci' / 'honua-target-framework.sh'
            helper.parent.mkdir(parents=True)
            helper.write_text("#!/bin/sh\nprintf '%s\\n' net11.0\n")
            helper.chmod(0o755)
            shim = root / 'dotnet'
            shim.write_text('''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
args = sys.argv[1:]
assert args[0] != 'new', 'SDK 11 templates reject net10.0'
project, = Path.cwd().glob('*.csproj')
framework = ET.parse(project).findtext('./PropertyGroup/TargetFramework')
assert framework == 'net11.0', framework
assert ET.parse(project).findtext('./PropertyGroup/OutputType') == 'Exe'
if args[0] == 'add':
    assert args == ['add', 'package', 'Honua.Core', '--version', '0.0.0-ci.42.1']
    Path('.package-added').touch()
else:
    assert args == ['build', '--configuration', 'Release']
    assert Path('.package-added').exists()
    assert 'new SpatialReference' in Path('Program.cs').read_text()
    assert 'new ValidationException' in Path('Program.cs').read_text()
with open(os.environ['CONSUMER_CALLS'], 'a') as output:
    output.write(json.dumps([args[0], framework]) + '\\n')
''')
            shim.chmod(0o755)
            calls = root / 'calls.jsonl'
            environment = {**os.environ, 'PATH': str(root) + ':' + os.environ['PATH'],
                           'GITHUB_RUN_NUMBER': '42', 'GITHUB_RUN_ATTEMPT': '1',
                           'CONSUMER_CALLS': str(calls)}
            subprocess.run(['bash', '-euo', 'pipefail', '-c', consumer_script()],
                           cwd=root, env=environment, check=True, capture_output=True, text=True)
            self.assertEqual([json.loads(line) for line in calls.read_text().splitlines()],
                             [['add', 'net11.0'], ['build', 'net11.0']])


if __name__ == '__main__':
    unittest.main()
