import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch


CHECKER = Path(__file__).resolve().parents[1] / "check-retired-terms.py"
spec = importlib.util.spec_from_file_location("retired_terms", CHECKER)
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class RetiredTermsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.policy = self.root / "policy.json"
        self.policy.write_text(json.dumps({"version": 1, "rules": [
            {"id": "legacy", "literal": "retired phrase", "replacement": "canonical phrase"}
        ]}))
        self.allowlist = self.root / "allowlist.json"
        self.run_git("init", "-q")
        self.write("page.md", "retired phrase already exists\nunchanged\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "baseline")

    def run_git(self, *arguments):
        return checker.git(self.root, *arguments)

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def scan(self, **arguments):
        return checker.scan(self.root, "HEAD", self.policy, self.allowlist, **arguments)

    def test_existing_debt_is_ignored_but_new_case_insensitive_line_is_reported(self):
        self.write("page.md", "retired phrase already exists\nunchanged\nNew RETIRED PHRASE\n")
        self.assertEqual(self.scan(), ["page.md:3: legacy: use canonical phrase"])

    def test_deleted_term_is_not_a_finding(self):
        self.write("page.md", "unchanged\n")
        self.assertEqual(self.scan(), [])

    def test_all_mode_reports_existing_debt(self):
        self.assertEqual(self.scan(scan_all=True), ["page.md:1: legacy: use canonical phrase"])

    def test_exception_requires_exact_path_rule_line_and_nonempty_reason(self):
        self.write("page.md", "allowed retired phrase\n")
        entry = {"path": "page.md", "rule": "legacy", "line": "allowed retired phrase",
                 "reason": "Protocol example retained verbatim"}
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [entry]}))
        self.assertEqual(self.scan(), [])
        self.write("page.md", "allowed retired phrase changed\n")
        self.assertEqual(len(self.scan()), 1)
        entry["path"] = "*.md"
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [entry]}))
        with self.assertRaises(ValueError):
            self.scan()
        entry["path"] = "page.md"
        entry["reason"] = ""
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [entry]}))
        with self.assertRaises(ValueError):
            self.scan()

    def test_protected_inputs_are_never_read_or_diffed(self):
        names = ["certification/agent-e2e/fixture.json", "certification/ai-arc/heldout-fixture.sealed"]
        for name in names:
            self.write(name, "retired phrase\n")
        self.run_git("add", ".")
        actual_git = checker.git

        def guarded_git(root, *arguments):
            if any(name in arguments for name in names):
                self.fail("Protected input passed to git diff")
            return actual_git(root, *arguments)

        actual_read = Path.read_bytes

        def guarded_read(path):
            if path.relative_to(self.root).as_posix() in names:
                self.fail("Protected input read")
            return actual_read(path)

        with patch.object(checker, "git", side_effect=guarded_git), patch.object(Path, "read_bytes", guarded_read):
            self.assertEqual(self.scan(), [])
            self.assertEqual(len(self.scan(scan_all=True)), 1)

    def test_literal_pathspec_name_does_not_select_other_files(self):
        self.write(":(glob)*.md", "retired phrase\n")
        self.run_git("add", ".")
        self.assertEqual(self.scan(), [":(glob)*.md:1: legacy: use canonical phrase"])

    def test_bad_base_fails_instead_of_reporting_a_pass(self):
        with self.assertRaises(subprocess.CalledProcessError):
            checker.scan(self.root, "missing-base", self.policy, self.allowlist)

    def test_invalid_text_and_invalid_policy_fail(self):
        (self.root / "page.md").write_bytes(b"invalid\xff\n")
        with self.assertRaises(UnicodeDecodeError):
            self.scan()
        self.policy.write_text('{"version": 1, "rules": []}')
        with self.assertRaises(ValueError):
            self.scan()

    def test_committed_and_staged_additions_are_checked(self):
        self.write("new.md", "retired phrase\n")
        self.run_git("add", ".")
        before_commit = self.scan()
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "new phrase")
        after_commit = checker.scan(self.root, "HEAD^", self.policy, self.allowlist)
        self.assertEqual(before_commit, ["new.md:1: legacy: use canonical phrase"])
        self.assertEqual(after_commit, before_commit)


if __name__ == "__main__":
    unittest.main()
