#!/usr/bin/env python3
"""Proves the skill link checker rejects a home-page title, a 404, and a missing file."""
import importlib.util
import pathlib
import tempfile
import unittest

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
        self.assertTrue(any("does not resolve" in error for error in errors), errors)
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


if __name__ == "__main__":
    unittest.main()
