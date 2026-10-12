"""Exercise the real dry-run planner with cheap selector fixtures, never dotnet/Docker."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]


class LocalPlanTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.scripts = self.root / "scripts/ci"
        (self.scripts / "lib").mkdir(parents=True)
        for name in ("pre-pr-check.sh", "classify-pre-pr-changes.sh", "lib/jq-cr-safe.sh"):
            shutil.copy2(ROOT / "scripts/ci" / name, self.scripts / name)
        # Reuse the actual reviewed Fast policy, without evaluating/building fake projects.
        self.fast_projects = sorted(p for p, entry in json.loads(
            (ROOT / ".github/test-projects.json").read_text())["projects"].items()
            if entry["role"] == "unit" and entry["isolated"])
        (self.scripts / "test-projects.py").write_text(
            "import importlib.util, json, sys\n"
            + "spec=importlib.util.spec_from_file_location('policy', "
            + repr(str(ROOT / "scripts/ci/test-projects.py")) + ")\n"
            + "policy=importlib.util.module_from_spec(spec); spec.loader.exec_module(policy)\n"
            + "projects=" + repr(self.fast_projects) + "\n"
            + "completed=sys.argv[3::2]; projects=[p for p in projects if p not in completed]\n"
            + "print('\\n'.join(projects) if sys.argv[1]=='gate-fast-projects' else json.dumps(policy.gate_fast_actions(projects)))\n")
        self.stub("check-instructions-sync.sh", "exit 0")
        self.stub("compute-affected-projects.sh", "echo src/A/A.csproj")
        self.stub("honua-server-targeted-tests.sh", "echo '{\"run_all\":false,\"shards\":[\"example\"],\"reason\":\"fixture\"}'")
        (self.scripts / "capability-impact.py").write_text('''import json, sys
if sys.argv[1] != "validate":
 print(json.dumps({"runAll":False,"unmatchedSourceFiles":[],"provingTestCount":1,
 "shards":["example"],"testsByShard":{"example":["Example.ProvesIt"]},"capabilities":[]}))
''')
        (self.root / ".github").mkdir()
        (self.root / ".github/ci-shards.json").write_text(json.dumps({"shards":[{
            "shard_name":"example", "filter":"Tier=Fast", "log_name":"example",
            "timeout_minutes":10, "test_timeout_minutes":5, "max_cpu_count":1}]}))
        projects = ["src/A/A.csproj", "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"]
        (self.root / "Honua.sln").write_text("\n".join(f'Project = "p", "{p}", "id"' for p in projects))
        (self.root / "src/A").mkdir(parents=True)
        (self.root / "src/A/A.csproj").write_text("<Project/>")
        (self.root / "src/A/File.cs").write_text("// baseline")
        self.git("init", "-b", "trunk")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.invalid")
        self.git("add", ".")
        self.git("commit", "-m", "baseline")
        self.git("branch", "baseline")
        (self.root / "src/A/File.cs").write_text("// dirty")

    def stub(self, name, body):
        path = self.scripts / name
        path.write_text("#!/usr/bin/env bash\n" + body + "\n")
        path.chmod(0o755)

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, stderr=subprocess.DEVNULL, text=True)

    def plan(self, *args):
        # Outside the worktree so the requested output does not enter the manifest.
        destination = self.root.parent / (self.root.name + "-plan.json")
        self.addCleanup(lambda: destination.unlink(missing_ok=True))
        env = {k: v for k, v in os.environ.items() if not k.startswith("HONUA_PRE_PR_")}
        result = subprocess.run(["bash", "scripts/ci/pre-pr-check.sh", "--base", "baseline",
                                 "--plan-json", str(destination), *args],
                                cwd=self.root, env=env, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return json.loads(destination.read_text())

    def test_full_keeps_manifest_and_resolves_actions(self):
        plan = self.plan("--full", "--fast")
        self.assertEqual(["src/A/File.cs"], plan["changedFiles"])
        self.assertEqual("complete", plan["changedFilesStatus"])
        self.assertFalse(plan["fast"])
        self.assertEqual(["example"], plan["serverShards"]["shards"])
        self.assertEqual("Tier=Fast", plan["serverTestActions"][0]["filter"])
        self.assertEqual(len(self.fast_projects) + 4, len(plan["unitTestActions"]))

    def test_fast_defers_shards_but_preserves_routing_diagnostic(self):
        plan = self.plan("--fast")
        self.assertEqual([], plan["serverTestActions"])
        self.assertEqual([], plan["serverShards"]["shards"])
        self.assertFalse(plan["serverShards"]["run_all"])
        self.assertEqual(["example"], plan["selectionRouting"]["shards"])
        self.assertEqual(len(self.fast_projects) + 2, len(plan["unitTestActions"]))
        actions = plan["unitTestActions"]
        self.assertEqual(set(self.fast_projects), {a["project"] for a in actions if a["project"] in self.fast_projects})
        governance = actions[-1]
        self.assertIn("McpTaxonomyAlignmentTests", governance["filter"])
        self.assertEqual("tests/dotnet/Honua.Ai.UnitTests/Honua.Ai.UnitTests.csproj", governance["project"])
        self.assertTrue(any(a["project"].endswith("Honua.Server.Tests.csproj") and a["filter"] == "Tier=Fast" for a in actions))

    def test_smart_records_final_narrowed_filter_and_fallback_project(self):
        plan = self.plan()
        action = plan["serverTestActions"][0]
        self.assertEqual("(Tier=Fast)&(FullyQualifiedName=Example.ProvesIt)", action["filter"])
        self.assertEqual("tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj", action["csproj"])
        self.assertEqual(5, action["test_timeout_minutes"])

    def test_unknown_manifest_is_explicit_not_empty(self):
        plan = self.plan("--base", "missing")
        self.assertEqual("full", plan["mode"])
        self.assertEqual("unknown", plan["changedFilesStatus"])
        self.assertIsNone(plan["changedFiles"])

    def test_explicit_full_does_not_short_circuit_ci_only_changes(self):
        self.git("checkout", "--", "src/A/File.cs")
        (self.root / "README.md").write_text("documentation change")
        plan = self.plan("--full")
        self.assertEqual("full", plan["mode"])
        self.assertEqual(["README.md"], plan["changedFiles"])
        self.assertEqual(["example"], plan["serverShards"]["shards"])

    def test_plan_rejects_feedback_modes_before_dispatch(self):
        for mode in ("--unit-only", "--affected-tests"):
            result = subprocess.run(["bash", "scripts/ci/pre-pr-check.sh", mode,
                                     "--plan-json", str(self.root / "invalid.json")],
                                    cwd=self.root, capture_output=True, text=True)
            self.assertEqual(2, result.returncode)
            self.assertIn("cannot be combined with feedback", result.stderr)
            self.assertFalse((self.root / "invalid.json").exists())
