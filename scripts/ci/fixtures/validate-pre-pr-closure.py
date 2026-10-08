#!/usr/bin/env python3
"""Offline regression fixtures for FAST selection and real closure reporting."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]


class PrePrClosureTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.run_command("git", "init", "-q")
        self.run_command("git", "config", "user.name", "Mike McDougall")
        self.run_command("git", "config", "user.email", "mike@honua.io")
        for name in ("pre-pr-check.sh", "pre-pr-contract-guard.py", "pre-pr-dependency-plan.py",
                     "classify-pre-pr-changes.sh", "compute-affected-projects.sh", "lib/jq-cr-safe.sh"):
            target = self.root / "scripts/ci" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(SCRIPTS / name, target)
        self.write("scripts/ci/check-instructions-sync.sh", "#!/bin/bash\ntrue\n", executable=True)
        self.write("scripts/ci/honua-server-targeted-tests.sh", '#!/bin/bash\necho \'{"run_all":false,"shards":[],"reason":"fixture"}\'\n', executable=True)
        self.write("scripts/ci/capability-impact.py", '''import json, sys
if sys.argv[1] != 'validate':
    print(json.dumps({'runAll': True, 'reason': 'fixture', 'capabilities': [],
                      'unmatchedSourceFiles': [], 'provingTestCount': 0}))
''')
        self.projects = [
            "src/Honua.Geometry/Honua.Geometry.csproj",
            "src/Honua.Analyzers/Honua.Analyzers.csproj",
            "src/Honua.Server/Honua.Server.csproj",
            "tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj",
            "tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj",
            "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj",
            "tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj",
            "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj",
        ]
        for project in self.projects:
            self.write(project, '<Project Sdk="Microsoft.NET.Sdk" />')
        arch = self.projects[-1]
        refs = ''.join(f'<ProjectReference Include="{os.path.relpath(self.root / ref, (self.root / arch).parent)}" />'
                       for ref in self.projects[5:7])
        self.write(arch, '<Project>' + refs + '</Project>')
        self.topology = "tests/dotnet/Honua.Architecture.Tests/Topology/Honua.Architecture.Topology.Tests.csproj"
        self.write(self.topology, '<Project />')
        self.write("src/Honua.Geometry/Repair.cs", 'internal class Repair\n{\n    internal int Value()\n    {\n        return 1;\n    }\n}\n')
        self.test_path = "tests/dotnet/Honua.Core.Tests/RepairTests.cs"
        self.test_source = '''public class RepairTests
{
    [Endpoint(
        "GET /old")]
    public void Repair()
    {
        Assert.Equal(1, 1);
    }
}
'''
        self.write(self.test_path, self.test_source)
        self.protocol_helper = "src/Honua.Protocols.GeoServices/InputRepair.cs"
        self.write(self.protocol_helper, "internal class InputRepair\n{\n    public int Value()\n    {\n        return 1;\n    }\n}\n")
        self.write(".github/ci-shards.json", '{"shards":[]}')
        self.write("Honua.sln", '\n'.join(f'Project("guid") = "p", "{p.replace(chr(47), chr(92))}", "guid"\nEndProject' for p in self.projects))
        # MSBuild evaluation is the only allowed managed command in --dry-run.
        # Its fixture graph deliberately contains transitive test references.
        self.write("bin/dotnet", '''#!/usr/bin/env python3
import json, pathlib, sys, xml.etree.ElementTree as ET
assert sys.argv[1] == 'msbuild', sys.argv
root = pathlib.Path.cwd()
target = pathlib.Path(sys.argv[2])
out = next(arg.split('=', 1)[1] for arg in sys.argv if arg.startswith('-p:RestoreGraphOutputPath='))
if target.suffix == '.slnf':
    roots = [root / p.replace('\\\\', '/') for p in json.loads(target.read_text())['solution']['projects']]
elif target.suffix == '.sln':
    roots = list(root.rglob('*.csproj'))
else:
    roots = [target.resolve()]
projects = {}
def visit(p):
    p = p.resolve()
    if str(p) in projects:
        return
    projects[str(p)] = {}
    for ref in ET.parse(p).iter('ProjectReference'):
        visit(p.parent / ref.attrib['Include'].replace('\\\\', '/'))
for p in roots:
    visit(p)
visit(root / 'src/Honua.Analyzers/Honua.Analyzers.csproj')
pathlib.Path(out).write_text(json.dumps({'projects': projects}))
''', executable=True)
        self.commit()
        self.base = self.run_command("git", "rev-parse", "HEAD").strip()

    def write(self, path, text, executable=False):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        if executable:
            target.chmod(0o755)

    def run_command(self, *args):
        return subprocess.check_output(args, cwd=self.root, text=True, stderr=subprocess.STDOUT)

    def commit(self):
        self.run_command("git", "add", ".")
        self.run_command("git", "commit", "-qm", "test: closure fixture")

    def plan(self, *args):
        env = dict(os.environ, PATH=str(self.root / "bin") + os.pathsep + os.environ["PATH"],
                   HONUA_PRE_PR_FULL="0", HONUA_PRE_PR_FAST="0")
        result = subprocess.run(["bash", "scripts/ci/pre-pr-check.sh", "--base", self.base,
                                 "--dry-run", *args], cwd=self.root, env=env, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        self.assertEqual(result.returncode, 0, result.stdout)
        return result.stdout

    def guard(self, paths):
        self.write(".git/paths.txt", '\n'.join(paths))
        return self.run_command("python3", "scripts/ci/pre-pr-contract-guard.py",
                                "--base", self.base, "--changed-files", ".git/paths.txt").strip()

    def source_repair(self):
        self.write("src/Honua.Geometry/Repair.cs", 'internal class Repair\n{\n    internal int Value()\n    {\n        return 2;\n    }\n}\n')
        self.commit()

    def test_fast_source_closure_and_format_owner(self):
        self.source_repair()
        output = self.plan("--fast")
        self.assertIn("Architecture: source topology", output)
        self.assertIn("Deferred to default/FULL local and hosted CI", output)
        self.assertIn("Honua.Architecture.Topology.Tests.csproj", output)
        self.assertNotIn("   tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj", output)
        self.assertNotIn("   tests/dotnet/Honua.Protocols.GeoServices.Tests/", output)
        self.assertIn("Format dependency closure: 2 projects", output)
        self.assertIn("Format workspace: 1 owning project", output)

    def test_default_preserves_transitive_test_closure(self):
        self.source_repair()
        output = self.plan()
        self.assertIn("Architecture: full catalogue/proof enforcement", output)
        self.assertIn("   tests/dotnet/Honua.Protocols.GeoServices.Tests/", output)
        self.assertNotIn("Deferred to default/FULL local", output)

    def test_full_overrides_fast(self):
        self.source_repair()
        output = self.plan("--fast", "--full")
        self.assertIn("Mode: FULL", output)
        self.assertIn("Architecture: full catalogue/proof enforcement", output)

    def test_test_body_only_can_defer_but_multiline_route_cannot(self):
        self.write(self.test_path, self.test_source.replace('Assert.Equal(1, 1)', 'Assert.Equal(2, 2)'))
        self.assertEqual(self.guard([self.test_path]), '')
        self.commit()
        self.assertIn("Architecture: source topology", self.plan("--fast"))
        self.write(self.test_path, self.test_source.replace('GET /old', 'GET /new'))
        self.assertIn("metadata", self.guard([self.test_path]))
        self.assertIn("Architecture: full catalogue/proof enforcement", self.plan("--fast"))

    def test_new_removed_renamed_and_unclassified_inputs_fail_closed(self):
        path = "src/Honua.Geometry/New.cs"
        self.write(path, 'internal class New {}')
        self.assertIn("new or untracked", self.guard([path]))
        (self.root / "src/Honua.Geometry/Repair.cs").unlink()
        self.assertIn("removed", self.guard(["src/Honua.Geometry/Repair.cs"]))
        self.write(self.test_path, self.test_source.replace('void Repair()', 'void Renamed()'))
        self.assertIn("metadata", self.guard([self.test_path]))
        self.assertIn("unclassified", self.guard(["Directory.Build.props"]))

    def test_staged_discovery_edit_cannot_cancel_into_deferral(self):
        self.write(self.test_path, self.test_source.replace('GET /old', 'GET /new'))
        self.run_command('git', 'add', self.test_path)
        self.write(self.test_path, self.test_source)
        self.assertIn("metadata", self.guard([self.test_path]))

    def test_route_source_and_parity_data_force_catalogue(self):
        self.write("src/Honua.Geometry/Repair.cs", 'internal class Repair { void Route() { app.MapGet("/new", Handler); } }')
        self.assertIn("route/capability", self.guard(["src/Honua.Geometry/Repair.cs"]))
        self.assertIn("Architecture: full catalogue/proof enforcement", self.plan("--fast"))
        self.write("docs/gis/data/geoservices-rest-parity.json", '{}')
        output = self.plan("--fast")
        self.assertNotIn("Mode: CI-SHELL-ONLY", output)
        self.assertIn("Architecture: full catalogue/proof enforcement", output)

    def test_protocol_helper_body_can_defer_but_registration_cannot(self):
        source = (self.root / self.protocol_helper).read_text()
        self.write(self.protocol_helper, source.replace("return 1;", "return 2;"))
        self.assertEqual(self.guard([self.protocol_helper]), "")
        self.write(self.protocol_helper, source.replace("return 1;", 'app.MapGet("/rest/new", Handler); return 1;'))
        self.assertIn("route/capability", self.guard([self.protocol_helper]))

    def test_ordinary_docs_keep_shell_only_path(self):
        self.write("README.md", "A documentation-only repair.\n")
        output = self.plan("--fast")
        self.assertIn("Mode: CI-SHELL-ONLY", output)
        self.assertNotIn("dependency closure:", output)

    def test_public_declarations_and_preprocessor_edits_force_catalogue(self):
        path = "src/Honua.Geometry/Repair.cs"
        self.write(path, "public class Repair {}\n")
        self.assertIn("route/capability", self.guard([path]))
        self.write(self.test_path, "#if OMIT_PROOFS\n" + self.test_source + "#endif\n")
        self.assertIn("metadata", self.guard([self.test_path]))

    def test_selector_edits_escalate_full(self):
        self.source_repair()
        with (self.root / 'scripts/ci/pre-pr-contract-guard.py').open('a') as f:
            f.write('\n# selector repair\n')
        output = self.plan("--fast")
        self.assertIn("forcing FULL mode", output)


if __name__ == "__main__":
    unittest.main()
