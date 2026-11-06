#!/usr/bin/env python3
"""Fail when a skill link is a missing file, an HTTP error, or the GitBook home page.

GitBook answers a missing page with HTTP 200 and the space home title, so status
alone does not prove a page exists. Relative links must name a file in this repo.
"""
import html
import os
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = Path(HERE).resolve().parents[1]
HOME_URL = "https://honua.gitbook.io/honuaio"
LINK_RE = re.compile(r"\]\(([^)]+)\)")
FENCE_RE = re.compile(r"^\s*(```+|~~~+)")
TITLE_RE = re.compile(r"<title>(.*?)</title>", re.IGNORECASE | re.DOTALL)
USER_AGENT = "honua-skill-link-check"


def link_targets(text):
    """Markdown link targets outside fenced code, without a title suffix."""
    fenced = False
    fence = ""
    for line in text.splitlines():
        marker = FENCE_RE.match(line)
        if marker:
            token = marker.group(1)[0]
            if not fenced:
                fenced, fence = True, token
            elif token == fence:
                fenced = False
            continue
        if fenced:
            continue
        for raw in LINK_RE.findall(line):
            target = raw.strip().split()[0].strip("<>")
            if target:
                yield target


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


def resolve_repo_file(markdown_file, target, repo_root):
    """Return the linked file, or None when the target is only a fragment."""
    path = target.split("#", 1)[0].split("?", 1)[0]
    if path == "":
        return None
    if path.startswith("/"):
        candidate = (repo_root / path.lstrip("/")).resolve()
    else:
        candidate = (markdown_file.parent / path).resolve()
    return candidate


def inside_repo(candidate, repo_root):
    root = repo_root.resolve()
    return candidate == root or root in candidate.parents


def check_skill_tree(skill_root, repo_root, fetch, home_url=HOME_URL):
    """Errors for missing relative files and for pages that are absent or the home page."""
    errors = []
    pages = {}
    home_title = None
    root = repo_root.resolve()
    for path in sorted(skill_root.rglob("*.md")):
        for target in link_targets(path.read_text(encoding="utf-8")):
            location = f"{path.relative_to(root)} -> {target}"
            if target.startswith(("http://", "https://")):
                if home_title is None:
                    status, home_title = fetch(home_url)
                    if status != 200 or not home_title:
                        errors.append(f"GitBook home page title was not readable ({status})")
                        home_title = home_title or ""
                if target not in pages:
                    pages[target] = fetch(target)
                status, title = pages[target]
                if status != 200:
                    errors.append(f"{location} returned HTTP {status}")
                elif not title:
                    errors.append(f"{location} returned no page title")
                elif title == home_title:
                    errors.append(f"{location} is the GitBook home page")
                continue
            if target.startswith(("mailto:", "honua://")):
                continue
            candidate = resolve_repo_file(path, target, root)
            if candidate is None:
                continue
            if not inside_repo(candidate, root) or not candidate.is_file():
                errors.append(f"{location} does not resolve to a file in the repo")
    return errors


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
