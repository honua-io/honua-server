#!/usr/bin/env python3
"""Fail when a skill link is a missing file, an HTTP error, or the GitBook home page.

GitBook answers a missing page with HTTP 200 and the space home title, so status
alone does not prove a page exists. Relative links use the documentation checker.
"""
import html
import importlib.util
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = Path(HERE).resolve().parents[1]
HOME_URL = "https://honua.gitbook.io/honuaio"
HTTP_OK = 200
TITLE_RE = re.compile(r"<title>(.*?)</title>", re.IGNORECASE | re.DOTALL)
USER_AGENT = "honua-skill-link-check"

_DOC_SPEC = importlib.util.spec_from_file_location(
    "check_doc_links", os.path.join(HERE, "check-doc-links.py"))
DOC_LINKS = importlib.util.module_from_spec(_DOC_SPEC)
_DOC_SPEC.loader.exec_module(DOC_LINKS)


def page_title(body):
    match = TITLE_RE.search(body)
    if match is None:
        return ""
    return re.sub(r"\s+", " ", html.unescape(match.group(1))).strip()


def fetch_page(url):
    """Return (status, title). HTTP errors return their status and an empty title."""
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=25) as response:
            body = response.read(120000).decode("utf-8", "replace")
            return response.status, page_title(body)
    except urllib.error.HTTPError as error:
        return error.code, ""


def published_page_errors(skill_root, repo_root, fetch):
    """Errors when an absolute page is missing, untitled, or the GitBook home page."""
    errors = []
    pages = {}
    home_title = None
    for path in sorted(Path(skill_root).rglob("*.md")):
        text = path.read_text(encoding="utf-8")
        for target in DOC_LINKS.link_targets(text):
            if not target.startswith(("http://", "https://")):
                continue
            location = f"{DOC_LINKS.rel(path, repo_root)} -> {target}"
            if home_title is None:
                status, home_title = fetch(HOME_URL)
                if status != HTTP_OK or not home_title:
                    errors.append(f"GitBook home page title was not readable ({status})")
                    home_title = home_title or ""
            if target not in pages:
                pages[target] = fetch(target)
            status, title = pages[target]
            if status != HTTP_OK:
                errors.append(f"{location} returned HTTP {status}")
            elif not title:
                errors.append(f"{location} returned no page title")
            elif title == home_title:
                errors.append(f"{location} is the GitBook home page")
    return errors


def check_skill_tree(skill_root, repo_root, fetch):
    """Errors for relative links and for pages that are absent or the home page."""
    _, relative_errors, _ = DOC_LINKS.check_relative_links(str(skill_root), str(repo_root), set())
    return relative_errors + published_page_errors(skill_root, repo_root, fetch)


def main():
    skill_root = REPO / "skills"
    errors = check_skill_tree(skill_root, REPO, fetch_page)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"Skill links passed ({skill_root}).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
