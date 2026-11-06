#!/usr/bin/env python3
import importlib.util
import pathlib
import subprocess
import sys
import tempfile
import unittest
import urllib.error

SCRIPT = pathlib.Path(__file__).with_name("check-skill-links.py")
SPEC = importlib.util.spec_from_file_location("check_skill_links", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
checker = MODULE


HOME = "https://honua.gitbook.io/honuaio"
HOME_TITLE = "README | honuaio"


def pages(mapping):
    def fetch(url):
        if url not in mapping:
            raise AssertionError(url)
        return mapping[url]
    return fetch


class SkillLinkCheckerTests(unittest.TestCase):
    def test_home_title_missing_file_and_http_error_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            skill = root / "skills"
            skill.mkdir()
            (skill / "SKILL.md").write_text(
                "# Skill\n"
                "[Missing](references/missing.md)\n"
                "[Home](https://example.test/missing)\n"
                "[Broken](https://example.test/nope)\n"
                "[Real](https://example.test/layer)\n",
                encoding="utf-8")
            errors = checker.check_skill_tree(skill, root, pages({
                HOME: (200, HOME_TITLE),
                "https://example.test/missing": (200, HOME_TITLE),
                "https://example.test/nope": (404, ""),
                "https://example.test/layer": (200, "Published layer | honuaio"),
            }))
        self.assertEqual(3, len(errors), errors)
        self.assertTrue(any("file not found" in error for error in errors), errors)
        self.assertTrue(any("GitBook home page" in error for error in errors), errors)
        self.assertTrue(any("HTTP 404" in error for error in errors), errors)

    def test_relative_file_and_distinct_title_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            skill = root / "skills" / "example"
            (skill / "references").mkdir(parents=True)
            (skill / "references" / "steps.md").write_text("# Steps\n", encoding="utf-8")
            (skill / "SKILL.md").write_text(
                "# Skill\n[Steps](references/steps.md)\n"
                "[Layer](https://example.test/layer)\n",
                encoding="utf-8")
            errors = checker.check_skill_tree(skill.parent, root, pages({
                HOME: (200, HOME_TITLE),
                "https://example.test/layer": (200, "Published layer | honuaio"),
            }))
        self.assertEqual([], errors)

    def test_live_skill_tree(self):
        errors = checker.check_skill_tree(checker.REPO / "skills", checker.REPO, checker.fetch_page)
        self.assertEqual([], errors)

    def test_blank_destination_does_not_crash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            skill = root / "skills"
            skill.mkdir()
            (skill / "SKILL.md").write_text("# Skill\n[self]( )\n[self](<>)\n", encoding="utf-8")
            errors = checker.check_skill_tree(skill, root, pages({}))
        self.assertEqual([], errors)

    def test_fenced_link_is_ignored(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            skill = root / "skills"
            (skill / "references").mkdir(parents=True)
            (skill / "references" / "steps.md").write_text("# Steps\n", encoding="utf-8")
            (skill / "SKILL.md").write_text(
                "# Skill\n```\n[Missing](references/missing.md)\n```\n"
                "[Steps](references/steps.md)\n",
                encoding="utf-8")
            errors = checker.check_skill_tree(skill.parent, root, pages({}))
        self.assertEqual([], errors)

    def test_link_outside_the_repo_fails_when_the_file_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            outside = root.parent / f"skill-link-escape-{root.name}.md"
            outside.write_text("# Outside\n", encoding="utf-8")
            try:
                skill = root / "skills"
                skill.mkdir()
                (skill / "SKILL.md").write_text(
                    f"# Skill\n[Out](../../{outside.name})\n", encoding="utf-8")
                errors = checker.check_skill_tree(skill, root, pages({}))
            finally:
                outside.unlink(missing_ok=True)
        self.assertTrue(any("escapes the repository" in error for error in errors), errors)

    def test_unreadable_home_and_untitled_page_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            skill = root / "skills"
            skill.mkdir()
            (skill / "SKILL.md").write_text(
                "# Skill\n[Layer](https://example.test/layer)\n", encoding="utf-8")
            home_down = checker.check_skill_tree(skill, root, pages({
                HOME: (500, ""),
                "https://example.test/layer": (200, "Published layer | honuaio"),
            }))
            untitled = checker.check_skill_tree(skill, root, pages({
                HOME: (200, ""),
                "https://example.test/layer": (200, ""),
            }))
        self.assertTrue(any("was not readable" in error for error in home_down), home_down)
        self.assertTrue(any("no page title" in error for error in untitled), untitled)

    def test_page_title_normalizes_markup(self):
        self.assertEqual("A & B", checker.page_title("<title>  A   &amp;   B </title>"))
        self.assertEqual("", checker.page_title("<html>no title</html>"))

    def test_fetch_page_reads_title_and_http_error(self):
        class Response:
            status = 200

            def read(self, _limit):
                return b"<title>Layer &amp; map</title>"

            def __enter__(self):
                return self

            def __exit__(self, *_args):
                return False

        def urlopen(_request, timeout=0):
            return Response()

        original = checker.urllib.request.urlopen
        checker.urllib.request.urlopen = urlopen
        try:
            self.assertEqual((200, "Layer & map"), checker.fetch_page("https://example.test/layer"))
        finally:
            checker.urllib.request.urlopen = original

        def refuse(_request, timeout=0):
            raise urllib.error.HTTPError("https://example.test/missing", 404, "missing", None, None)

        checker.urllib.request.urlopen = refuse
        try:
            self.assertEqual((404, ""), checker.fetch_page("https://example.test/missing"))
        finally:
            checker.urllib.request.urlopen = original

    def test_cli_returns_failure_when_links_fail(self):
        probe = (
            "import importlib.util, pathlib, sys\n"
            "spec = importlib.util.spec_from_file_location('check_skill_links', sys.argv[1])\n"
            "module = importlib.util.module_from_spec(spec)\n"
            "spec.loader.exec_module(module)\n"
            "module.check_skill_tree = lambda *_args: ['broken']\n"
            "sys.exit(module.main())\n"
        )
        result = subprocess.run(
            [sys.executable, "-c", probe, str(SCRIPT)],
            capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("broken", result.stderr)


if __name__ == "__main__":
    unittest.main()
