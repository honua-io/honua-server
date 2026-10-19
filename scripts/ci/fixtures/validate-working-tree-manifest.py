#!/usr/bin/env python3
"""Real pre-PR plan keeps committed, staged, unstaged, deleted, and untracked paths."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class WorkingTreeManifestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        scripts = self.root / "scripts/ci"
        (scripts / "lib").mkdir(parents=True)
        for name in ("pre-pr-check.sh", "classify-pre-pr-changes.sh",
                     "compute-affected-projects.sh", "lib/jq-cr-safe.sh"):
            shutil.copy2(ROOT / "scripts/ci" / name, scripts / name)
        fast_projects = sorted(project for project, entry in json.loads(
            (ROOT / ".github/test-projects.json").read_text())["projects"].items()
            if entry["role"] == "unit" and entry["isolated"])
        (scripts / "test-projects.py").write_text(
            "import importlib.util, json, sys\n"
            "spec=importlib.util.spec_from_file_location('policy', "
            + repr(str(ROOT / "scripts/ci/test-projects.py")) + ")\n"
            "policy=importlib.util.module_from_spec(spec); spec.loader.exec_module(policy)\n"
            "projects=" + repr(fast_projects) + "\n"
            "completed=sys.argv[3::2]; projects=[p for p in projects if p not in completed]\n"
            "print('\\n'.join(projects) if sys.argv[1]=='gate-fast-projects' else json.dumps(policy.gate_fast_actions(projects)))\n")
        self.stub("check-instructions-sync.sh", "exit 0")
        self.stub("honua-server-targeted-tests.sh",
                  "echo '{\"run_all\":false,\"shards\":[\"example\"],\"reason\":\"fixture\"}'")
        (scripts / "capability-impact.py").write_text(
            "import json, sys\n"
            "if sys.argv[1] != 'validate':\n"
            "    print(json.dumps({'runAll': False, 'unmatchedSourceFiles': [], 'provingTestCount': 1,\n"
            "                      'shards': ['example'], 'testsByShard': {'example': ['Example.ProvesIt']},\n"
            "                      'capabilities': []}))\n")
        (self.root / ".github").mkdir()
        (self.root / ".github/ci-shards.json").write_text(json.dumps({"shards": [{
            "shard_name": "example", "filter": "Tier=Fast", "log_name": "example",
            "timeout_minutes": 10, "test_timeout_minutes": 5, "max_cpu_count": 1}]}))
        projects = [
            "src/A/A.csproj",
            "tests/dotnet/KeepMe.Tests/KeepMe.Tests.csproj",
            "tests/dotnet/DropMe.Tests/DropMe.Tests.csproj",
        ]
        (self.root / "Honua.sln").write_text("\n".join(
            f'Project("{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}") = "{Path(p).stem}", "{p}", "{{00000000-0000-0000-0000-000000000000}}"'
            for p in projects) + "\n")
        for project in projects:
            path = self.root / project
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("<Project/>\n")
        (self.root / "src/A/File.cs").write_text("baseline\n")
        (self.root / "src/A/Committed.cs").write_text("baseline\n")
        (self.root / "tests/dotnet/KeepMe.Tests/Staged.cs").write_text("baseline\n")
        (self.root / "tests/dotnet/DropMe.Tests/Drop.cs").write_text("baseline\n")
        self.git("init", "-q", "-b", "trunk")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("add", ".")
        self.git("commit", "-qm", "baseline")
        self.git("branch", "baseline")

    def stub(self, name, body):
        path = self.root / "scripts/ci" / name
        path.write_text("#!/usr/bin/env bash\n" + body + "\n")
        path.chmod(0o755)

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True).strip()

    def test_plan_retains_every_working_tree_path_and_edited_test_project(self):
        (self.root / "src/A/Committed.cs").write_text("committed\n")
        self.git("add", "src/A/Committed.cs")
        self.git("commit", "-qm", "committed")
        (self.root / "src/A/File.cs").write_text("unstaged\n")
        (self.root / "tests/dotnet/KeepMe.Tests/Staged.cs").write_text("staged\n")
        self.git("add", "tests/dotnet/KeepMe.Tests/Staged.cs")
        (self.root / "tests/dotnet/DropMe.Tests/Drop.cs").unlink()
        (self.root / "src/A/New.cs").write_text("untracked\n")
        destination = self.root.parent / (self.root.name + "-plan.json")
        self.addCleanup(lambda: destination.unlink(missing_ok=True))
        env = {key: value for key, value in os.environ.items() if not key.startswith("HONUA_")}
        result = subprocess.run(
            ["bash", "scripts/ci/pre-pr-check.sh", "--base", "baseline", "--plan-json", str(destination)],
            cwd=self.root, env=env, capture_output=True, text=True, timeout=60, check=False)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        plan = json.loads(destination.read_text())
        expected = [
            "src/A/Committed.cs",
            "src/A/File.cs",
            "src/A/New.cs",
            "tests/dotnet/DropMe.Tests/Drop.cs",
            "tests/dotnet/KeepMe.Tests/Staged.cs",
        ]
        self.assertEqual("smart", plan["mode"])
        self.assertEqual("complete", plan["changedFilesStatus"])
        self.assertEqual(expected, plan["changedFiles"])
        self.assertEqual(expected, plan["formatFiles"])
        for project in ("src/A/A.csproj",
                        "tests/dotnet/KeepMe.Tests/KeepMe.Tests.csproj",
                        "tests/dotnet/DropMe.Tests/DropMe.Tests.csproj"):
            self.assertIn(project, plan["buildProjects"])


if __name__ == "__main__":
    unittest.main()
