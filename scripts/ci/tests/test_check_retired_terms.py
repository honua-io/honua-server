import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


CHECKER = Path(__file__).resolve().parents[1] / "check-retired-terms.py"
spec = importlib.util.spec_from_file_location("retired_terms", CHECKER)
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class RetiredTermsTests(unittest.TestCase):
    def setUp(self):
        self.unpatched_git = checker.git
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.policy = self.root / "policy.json"
        self.policy.write_text(json.dumps({"version": 1, "rules": [
            {"id": "legacy", "literal": "retired phrase", "replacement": "canonical phrase"}
        ], "reviewRules": []}))
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

    def check(self):
        return checker.VocabularyCheck(self.root, self.policy, self.allowlist)

    def scan(self):
        return self.check().check_changes("HEAD")

    def test_existing_debt_is_ignored_but_new_case_insensitive_line_is_reported(self):
        self.write("page.md", "retired phrase already exists\nunchanged\nNew RETIRED PHRASE\n")
        self.assertEqual(self.scan(), ["page.md:3: legacy: use canonical phrase"])

    def test_deleted_term_is_not_a_finding(self):
        self.write("page.md", "unchanged\n")
        self.assertEqual(self.scan(), [])

    def test_all_mode_reports_existing_debt(self):
        self.assertEqual(self.check().audit(), ["page.md:1: legacy: use canonical phrase"])

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

        def guarded_git(root, *arguments, **options):
            if any(name in arguments for name in names):
                self.fail("Protected input passed to git diff")
            return actual_git(root, *arguments, **options)

        actual_read = Path.read_bytes

        def guarded_read(path):
            if path.relative_to(self.root).as_posix() in names:
                self.fail("Protected input read")
            return actual_read(path)

        with patch.object(checker, "git", side_effect=guarded_git), patch.object(Path, "read_bytes", guarded_read):
            self.assertEqual(self.scan(), [])
            self.assertEqual(len(self.check().audit()), 1)

    def test_literal_pathspec_name_does_not_select_other_files(self):
        self.write(":(glob)*.md", "retired phrase\n")
        self.write("page.md", "retired phrase\n")
        self.run_git("add", ".")
        self.assertEqual(self.scan(), [":(glob)*.md:1: legacy: use canonical phrase",
                                       "page.md:1: legacy: use canonical phrase"])

    def test_bad_base_fails_instead_of_reporting_a_pass(self):
        with self.assertRaises(subprocess.CalledProcessError):
            self.check().check_changes("missing-base")

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
        after_commit = self.check().check_changes("HEAD^")
        self.assertEqual(before_commit, ["new.md:1: legacy: use canonical phrase"])
        self.assertEqual(after_commit, before_commit)

    def test_subdirectory_root_and_relative_diff_configuration_find_changes(self):
        self.write("docs/page.md", "retired phrase\n")
        self.run_git("add", ".")
        self.run_git("config", "diff.relative", "true")
        check = checker.VocabularyCheck(self.root / "docs", self.policy, self.allowlist)
        self.assertEqual(check.check_changes("HEAD"), ["docs/page.md:1: legacy: use canonical phrase"])

    def test_forced_color_does_not_hide_added_lines(self):
        self.write("page.md", "new retired phrase\n")
        self.run_git("config", "color.ui", "always")
        self.run_git("config", "color.diff", "always")
        self.assertEqual(self.scan(), ["page.md:1: legacy: use canonical phrase"])

    def test_pathspec_environment_does_not_hide_cli_findings(self):
        self.write("page.md", "new retired phrase\n")
        command = [sys.executable, str(CHECKER), "--root", str(self.root),
                   "--policy", str(self.policy), "--allowlist", str(self.allowlist), "--base", "HEAD"]
        for key in ("GIT_LITERAL_PATHSPECS", "GIT_GLOB_PATHSPECS",
                    "GIT_NOGLOB_PATHSPECS", "GIT_ICASE_PATHSPECS"):
            with self.subTest(key=key):
                result = subprocess.run(command, capture_output=True, env=dict(os.environ, **{key: "1"}))
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertIn(b"page.md:1: legacy: use canonical phrase", result.stdout)

    def test_crlf_checkout_ignores_debt_and_finds_only_violating_additions(self):
        self.run_git("config", "core.autocrlf", "true")
        page = self.root / "page.md"
        baseline = b"retired phrase already exists\r\nunchanged\r\n"
        page.write_bytes(baseline + b"clean addition\r\n")
        self.assertEqual(self.scan(), [])
        page.write_bytes(baseline + b"clean addition\r\nnew retired phrase\r\n")
        self.assertEqual(self.scan(), ["page.md:4: legacy: use canonical phrase"])

    def test_unicode_separators_preserve_complete_line_and_exception(self):
        line = "prefix\u2028retired phrase\f suffix"
        self.write("page.md", line + "\n")
        self.assertEqual(self.scan(), ["page.md:1: legacy: use canonical phrase"])
        self.assertEqual(self.check().audit(), self.scan())
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [
            {"path": "page.md", "rule": "legacy", "line": line, "reason": "Old protocol example"}
        ]}))
        self.assertEqual(self.scan(), [])

    def test_advanced_base_does_not_reintroduce_unchanged_debt(self):
        self.run_git("branch", "baseline")
        self.write("page.md", "unchanged\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "remove debt")
        self.run_git("branch", "advanced")
        self.run_git("checkout", "-q", "baseline")
        self.assertEqual(self.check().check_changes("advanced"), [])

    def test_exact_exceptions_do_not_suppress_another_path_or_rule(self):
        policy = json.loads(self.policy.read_text())
        policy["rules"].append({"id": "second", "literal": "phrase", "replacement": "word"})
        self.policy.write_text(json.dumps(policy))
        self.write("page.md", "retired phrase\n")
        self.write("other.md", "retired phrase\n")
        self.run_git("add", ".")
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [
            {"path": "page.md", "rule": "legacy", "line": "retired phrase", "reason": "Protocol name"}
        ]}))
        self.assertEqual(self.scan(), ["other.md:1: legacy: use canonical phrase",
                                       "other.md:1: second: use word", "page.md:1: second: use word"])

    def test_pure_rename_ignores_debt_and_rename_edit_reports_only_addition(self):
        self.run_git("mv", "page.md", "renamed.md")
        self.assertEqual(self.scan(), [])
        self.write("renamed.md", "retired phrase already exists\nunchanged\nnew retired phrase\n")
        self.assertEqual(self.scan(), ["renamed.md:3: legacy: use canonical phrase"])

    def test_binary_to_text_and_binary_attributes_do_not_hide_text(self):
        self.write(".gitattributes", "*.md -diff\n")
        (self.root / "page.md").write_bytes(b"old\0binary\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "binary baseline")
        self.write("page.md", "retired phrase\n")
        self.assertEqual(self.scan(), ["page.md:1: legacy: use canonical phrase"])

    def test_option_and_range_bases_are_rejected(self):
        for base in ("--name-only", "--no-patch", "HEAD..HEAD", "missing-base"):
            with self.subTest(base=base), self.assertRaises(subprocess.CalledProcessError):
                self.check().check_changes(base)

    def test_malformed_policy_and_exception_shapes_fail(self):
        for document in ([], {"version": 1, "entries": {}}, {"version": 1, "entries": ["bad"]}):
            with self.subTest(document=document):
                self.allowlist.write_text(json.dumps(document))
                with self.assertRaises(ValueError):
                    self.check()
        self.allowlist.unlink()
        for rules in ({}, ["bad"], []):
            with self.subTest(rules=rules):
                self.policy.write_text(json.dumps({"version": 1, "rules": rules, "reviewRules": []}))
                with self.assertRaises(ValueError):
                    self.check()

    def test_policy_version_and_contextual_rules_are_validated(self):
        valid = json.loads(self.policy.read_text())
        documents = [dict(valid, version=True), dict(valid, version=1.0),
                     {"version": 1, "rules": valid["rules"]},
                     dict(valid, reviewRules=["bad"]),
                     dict(valid, reviewRules=[{"literal": "word", "replacement": "name", "exceptions": ""}])]
        for document in documents:
            with self.subTest(document=document):
                self.policy.write_text(json.dumps(document))
                with self.assertRaises(ValueError):
                    self.check()

    def test_exception_paths_must_be_canonical_and_relative(self):
        for name in ("/abs.md", "../x.md", "./page.md", "a//b.md"):
            with self.subTest(name=name):
                self.allowlist.write_text(json.dumps({"version": 1, "entries": [
                    {"path": name, "rule": "legacy", "line": "retired phrase", "reason": "Protocol name"}
                ]}))
                with self.assertRaises(ValueError):
                    self.check()

    def test_unchanged_non_utf8_bytes_do_not_hide_valid_added_text(self):
        (self.root / "page.md").write_bytes(b"unchanged\xff\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "legacy encoding")
        (self.root / "page.md").write_bytes(b"unchanged\xff\nnew retired phrase\n")
        self.assertEqual(self.scan(), ["page.md:2: legacy: use canonical phrase"])

    def test_directory_replacement_never_exposes_protected_blob_content(self):
        name = "certification/agent-e2e/synthetic.txt"
        self.write(name, "synthetic-protected-sentinel\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "synthetic protected directory")
        (self.root / name).unlink()
        (self.root / "certification/agent-e2e").rmdir()
        (self.root / "certification").rmdir()
        self.write("certification", "new retired phrase\n")
        self.run_git("add", "-A")
        actual_git = checker.git

        def guarded_git(root, *arguments, **options):
            if "diff" in arguments:
                self.assert_safe_diff_inputs(arguments, options)
            result = actual_git(root, *arguments, **options)
            self.assertNotIn(b"synthetic-protected-sentinel", result)
            return result

        with patch.object(checker, "git", side_effect=guarded_git):
            self.assertEqual(self.scan(), ["certification:1: legacy: use canonical phrase"])

    def test_rename_into_original_directory_does_not_attribute_sibling_changes(self):
        self.run_git("mv", "page.md", "temp.md")
        (self.root / "page.md").mkdir()
        self.run_git("mv", "temp.md", "page.md/renamed.md")
        self.write("page.md/other.md", "new retired phrase\n")
        self.run_git("add", ".")
        self.assertEqual(self.scan(), ["page.md/other.md:1: legacy: use canonical phrase"])

    def test_missing_final_newline_preserves_added_line_detection(self):
        self.write("page.md", "unchanged")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "no final newline")
        self.write("page.md", "unchanged\nnew retired phrase")
        self.assertEqual(self.scan(), ["page.md:2: legacy: use canonical phrase"])

    def test_symlink_ancestors_and_binary_files_are_skipped(self):
        self.write("docs/page.md", "retired phrase\n")
        self.run_git("add", ".")
        (self.root / "docs/page.md").unlink()
        (self.root / "docs").rmdir()
        with tempfile.TemporaryDirectory() as outside:
            Path(outside, "page.md").write_text("retired phrase\n")
            (self.root / "docs").symlink_to(outside, target_is_directory=True)
            (self.root / "link.md").symlink_to(Path(outside, "page.md"))
            (self.root / "page.md").write_bytes(b"retired phrase\0binary\n")
            self.run_git("add", "link.md", "page.md")
            with patch.object(Path, "read_bytes", side_effect=AssertionError("Unexpected content read")):
                self.assertEqual(self.check().tracked_file_findings("docs/page.md"), [])
                self.assertEqual(self.check().tracked_file_findings("link.md"), [])
            self.assertEqual(self.scan(), [])
            self.assertEqual(self.check().audit(), [])

    def test_nonheldout_certification_is_scanned_and_allowlist_is_excluded(self):
        self.write("certification/ai-arc/public.md", "retired phrase\n")
        self.allowlist.write_text(json.dumps({"version": 1, "entries": [
            {"path": "elsewhere.md", "rule": "legacy", "line": "retired phrase", "reason": "Protocol name"}
        ]}))
        self.run_git("add", ".")
        self.assertEqual(self.scan(), ["certification/ai-arc/public.md:1: legacy: use canonical phrase"])

    def assert_safe_diff_inputs(self, arguments, options):
        index_file = options["index_file"]
        selected_base = arguments[arguments.index("--") - 1]
        actual_git = self.unpatched_git
        previous = actual_git(self.root, "ls-tree", "-r", "--name-only", "-z", selected_base)
        current = actual_git(self.root, "ls-files", "-z", index_file=index_file)
        for name in filter(None, (previous + current).decode().split("\0")):
            self.assertFalse(checker.is_protected_path(name), name)
        self.assertFalse(arguments[arguments.index("--") + 1:])

    def test_discovery_excludes_protected_paths_before_content_aware_git(self):
        name = "certification/agent-e2e/synthetic.json"
        self.write(name, "retired phrase\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "synthetic protected baseline")
        self.run_git("mv", name, "moved.md")
        actual_git = checker.git

        def guarded_git(root, *arguments, **options):
            if "diff" in arguments:
                self.assert_safe_diff_inputs(arguments, options)
            return actual_git(root, *arguments, **options)

        with patch.object(checker, "git", side_effect=guarded_git):
            self.assertEqual(self.scan(), ["moved.md:1: legacy: use canonical phrase"])

    @unittest.skipUnless(sys.platform == "linux", "Linux argument-limit regression")
    def test_streamed_selection_preserves_renames_and_excludes_protected_inputs_at_low_limit(self):
        for index in range(1800):
            self.write(f"docs/{index:04d}-{'x' * 80}.md", "clean\n")
            self.write(f"certification/agent-e2e/{index:04d}-{'x' * 80}.json", "protected-sentinel\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "large synthetic inventory")
        self.run_git("mv", "page.md", "renamed.md")
        self.write("renamed.md", "retired phrase already exists\nunchanged\nnew retired phrase\n")
        self.write("certification/agent-e2e/new.json", "protected-sentinel\n")
        self.run_git("add", ".")
        program = ("import resource,runpy,sys; "
                   "resource.setrlimit(resource.RLIMIT_STACK, (1048576, resource.getrlimit(resource.RLIMIT_STACK)[1])); "
                   "sys.argv=sys.argv[1:]; runpy.run_path(sys.argv[0],run_name='__main__')")
        command = [sys.executable, "-c", program, str(CHECKER), "--root", str(self.root),
                   "--policy", str(self.policy), "--allowlist", str(self.allowlist), "--base", "HEAD"]
        result = subprocess.run(command, capture_output=True)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertEqual(result.stdout.decode().splitlines(),
                         ["renamed.md:3: legacy: use canonical phrase", "Retired-term check: 1 finding(s)"])
        actual_git = checker.git

        def guarded_git(root, *arguments, **options):
            if "diff" in arguments:
                self.assertLess(sum(len(str(argument).encode()) + 1 for argument in arguments), 16384)
                self.assert_safe_diff_inputs(arguments, options)
            return actual_git(root, *arguments, **options)

        with patch.object(checker, "git", side_effect=guarded_git):
            self.assertEqual(self.scan(), ["renamed.md:3: legacy: use canonical phrase"])

    def test_scan_preserves_real_index_and_removes_temporary_index(self):
        index_file = Path(self.run_git("rev-parse", "--git-path", "index").decode().strip())
        if not index_file.is_absolute():
            index_file = self.root / index_file
        self.write("page.md", "new retired phrase\n")
        self.run_git("add", ".")
        index_before = index_file.read_bytes()
        temporary_indexes = []
        actual_git = checker.git

        def guarded_git(root, *arguments, **options):
            if "index_file" in options:
                temporary_indexes.append(options["index_file"])
            return actual_git(root, *arguments, **options)

        with patch.object(checker, "git", side_effect=guarded_git):
            self.assertEqual(self.scan(), ["page.md:1: legacy: use canonical phrase"])
        self.assertEqual(index_file.read_bytes(), index_before)
        self.assertTrue(temporary_indexes)
        self.assertTrue(all(not path.exists() for path in temporary_indexes))

    def test_safe_sibling_of_protected_leaf_is_still_scanned(self):
        directory = "certification/ai-arc/heldout-notes"
        self.write(f"{directory}/heldout-fixture.sealed", "synthetic-protected-sentinel\n")
        self.run_git("add", ".")
        self.run_git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                     "commit", "-qm", "synthetic protected leaf")
        self.write(f"{directory}/public.md", "new retired phrase\n")
        self.run_git("add", ".")
        self.assertEqual(self.scan(), [f"{directory}/public.md:1: legacy: use canonical phrase"])

    def test_cli_exit_codes_distinguish_findings_and_incomplete_checks(self):
        command = [sys.executable, str(CHECKER), "--root", str(self.root),
                   "--policy", str(self.policy), "--allowlist", str(self.allowlist)]
        result = subprocess.run([*command, "--base", "HEAD"], capture_output=True)
        self.assertEqual(result.returncode, 0)
        self.write("page.md", "new retired phrase\n")
        self.assertEqual(subprocess.run([*command, "--base", "HEAD"], capture_output=True).returncode, 1)
        self.allowlist.write_text('{"version":1,"entries":["bad"]}')
        result = subprocess.run([*command, "--base", "HEAD"], capture_output=True)
        self.assertEqual(result.returncode, 2)
        self.assertNotIn(b"Traceback", result.stderr)
        self.assertEqual(subprocess.run(command, capture_output=True).returncode, 2)


if __name__ == "__main__":
    unittest.main()
